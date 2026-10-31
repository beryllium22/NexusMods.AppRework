using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Reactive;

namespace NexusMods.DataModel.Tests;

public class QueryLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDrainsSelectedNativeQueryBeforeDisposingConnection(bool disposeProvider)
    {
        await using var fixture = await QueryLifetimeTestHost.Create();
        using var subscription = fixture.Query.ObserveOwned(value => value).Subscribe(_ => { });
        await fixture.Connection.FlushQueries().WaitAsync(Timeout);

        var gate = fixture.Table.BlockNextUpdate();
        Task? shutdown = null;
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);
            shutdown = disposeProvider ? fixture.DisposeProviderAsync() : fixture.StopAsync();
            shutdown.IsCompleted.Should().BeFalse("the native update has not been allowed to finish");

            // The live query is inside native execution. A second real SQL query must
            // still bind the registered MnemonicDB connection and open a DuckDB handle.
            CountTransactions(fixture.Connection).Should().BePositive();

            var repeatedDrain = fixture.Owner.DisposeAsync().AsTask();
            repeatedDrain.IsCompleted.Should().BeFalse("repeated disposal must await the same worker");

            gate.Release.TrySetResult();
            await shutdown.WaitAsync(Timeout);
            await repeatedDrain.WaitAsync(Timeout);
            await fixture.Owner.DisposeAsync();

            if (!disposeProvider)
            {
                CountTransactions(fixture.Connection).Should().BePositive(
                    "host stopping drains the updater but leaves connections owned by the provider");
            }
            else
            {
                var queryDisposedEngine = () => fixture.Connection.Query<int>("SELECT 1").ToArray();
                queryDisposedEngine.Should().Throw<ObjectDisposedException>();
            }
        }
        finally
        {
            gate.Release.TrySetResult();
            if (shutdown is not null)
                await shutdown.WaitAsync(Timeout);
        }
    }

    private static long CountTransactions(IConnection connection)
        => connection.Query<long>($"SELECT COUNT(*) FROM mdb_Transaction(Db => {connection})").Single();
}

/// <summary>
/// A real native query engine with no game, network, API key, or filesystem fixture.
/// </summary>
internal sealed class QueryLifetimeTestHost : IAsyncDisposable
{
    private readonly Lazy<Task> _stop;
    private readonly Lazy<Task> _dispose;

    private QueryLifetimeTestHost(IHost host, GatedQueryTable table)
    {
        Host = host;
        Table = table;
        Connection = host.Services.GetRequiredService<IConnection>();
        Owner = host.Services.GetServices<IHostedService>().OfType<QueryLifetimeHostedService>().Single();
        _stop = new Lazy<Task>(() => host.StopAsync());
        _dispose = new Lazy<Task>(() => ((IAsyncDisposable)host).DisposeAsync().AsTask());
    }

    public IHost Host { get; }
    public IConnection Connection { get; }
    public QueryLifetimeHostedService Owner { get; }
    public GatedQueryTable Table { get; }
    public Query<int> Query => Connection.Query<int>("SELECT * FROM query_lifetime_test()");

    public static async Task<QueryLifetimeTestHost> Create()
    {
        var table = new GatedQueryTable();
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging()
                    .AddMnemonicDB()
                    .AddRocksDbBackend()
                    .AddDatomStoreSettings(DatomStoreSettings.InMemory)
                    .AddSingleton<ATableFunction>(table)
                    .AddQueryLifetime();
                services.Configure<HostOptions>(options => options.ServicesStopConcurrently = true);
            })
            .Build();

        try
        {
            await host.StartAsync();
            return new QueryLifetimeTestHost(host, table);
        }
        catch
        {
            await ((IAsyncDisposable)host).DisposeAsync();
            throw;
        }
    }

    public Task StopAsync() => _stop.Value;
    public Task DisposeProviderAsync() => _dispose.Value;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_dispose.IsValueCreated)
                await StopAsync();
        }
        finally
        {
            await DisposeProviderAsync();
        }
    }
}

internal sealed class GatedQueryTable : ATableFunction
{
    private int _value = 1;
    private int _executions;
    private QueryGate? _nextGate;

    public int Executions => Volatile.Read(ref _executions);

    public QueryGate BlockNextUpdate()
    {
        var gate = new QueryGate();
        if (Interlocked.CompareExchange(ref _nextGate, gate, null) is not null)
            throw new InvalidOperationException("A query gate is already armed.");
        Change();
        return gate;
    }

    public void Change()
    {
        Interlocked.Increment(ref _value);
        Revise();
    }

    protected override void Setup(RegistrationInfo info) => info.SetName("query_lifetime_test");
    protected override void Bind(BindInfo info) => info.AddColumn<int>("Value");
    protected override object Init(InitInfo initInfo, InitData initData) => new ScanState();

    protected override void Execute(FunctionInfo functionInfo)
    {
        var state = functionInfo.GetInitInfo<ScanState>();
        var chunk = functionInfo.Chunk;
        if (state.Finished)
        {
            chunk.Size = 0;
            return;
        }

        state.Finished = true;
        Interlocked.Increment(ref _executions);
        var gate = Interlocked.Exchange(ref _nextGate, null);
        if (gate is not null)
        {
            gate.Entered.TrySetResult();
            gate.Release.Task.GetAwaiter().GetResult();
        }

        chunk[0].GetData<int>()[0] = Volatile.Read(ref _value);
        chunk.Size = 1;
    }

    private sealed class ScanState
    {
        public bool Finished { get; set; }
    }
}

internal sealed class QueryGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
