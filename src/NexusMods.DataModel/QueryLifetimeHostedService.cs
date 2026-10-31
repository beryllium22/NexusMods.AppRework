using Microsoft.Extensions.Hosting;
using NexusMods.HyperDuck.Internals;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.DataModel;

/// <summary>
/// Drains native live queries while the connections they read are still alive.
/// </summary>
internal sealed class QueryLifetimeHostedService : IHostedLifecycleService, IAsyncDisposable
{
    private readonly Lazy<Task> _shutdown;

    public QueryLifetimeHostedService(IConnection connection)
    {
        // The constructor dependency also makes DI dispose this owner before the
        // connection and its query engine. Keep those resources owned by DI.
        //
        // HyperDuck 0.28.2 exposes the shared updater only through its public live
        // query handle. A temporary, connection-independent query obtains it without
        // private reflection or changing the pinned database/native packages.
        // Remove this bridge once upstream unregisters disposed observations and
        // drains the updater before either connections or DuckDB become unusable.
        // https://github.com/Nexus-Mods/NexusMods.MnemonicDB/tree/18a1e81f7cc51826f14e3c732563f321fed2745d/src/NexusMods.HyperDuck
        using var handle = connection.Query<int>("SELECT 1").ObserveInto(new List<int>());
        if (handle is not LiveQuery<List<int>> liveQuery)
            throw new NotSupportedException("The HyperDuck live-query lifetime API has changed.");

        var updater = liveQuery.Updater;
        // The pinned updater awaits its worker with ConfigureAwait(false), including
        // when Host.Dispose synchronously waits for asynchronous service disposal.
        _shutdown = new Lazy<Task>(() => updater.DisposeAsync().AsTask());
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The app stops hosted services concurrently. StoppedAsync runs after all their
    // StopAsync calls, so consumers finish before the shared polling worker stops.
    // A cancelled shutdown token must not release native resources ahead of a query
    // that is still executing; await the actual drain, including any failure.
    public Task StoppedAsync(CancellationToken cancellationToken) => _shutdown.Value;

    // Also covers disposal of a started host when StopAsync was skipped. The same
    // task makes repeat/concurrent disposal wait for the original drain.
    public ValueTask DisposeAsync() => new(_shutdown.Value);
}
