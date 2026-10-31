using System.Collections.Concurrent;
using System.ComponentModel;
using CliWrap;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.RedEngine.Witcher3;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using OneOf;
using Xunit.Abstractions;

namespace NexusMods.DataModel.Tests;

public class SortOrderManagerLifetimeTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<SortOrderManagerLifetimeTests>(helper)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly SortOrderId TestSortOrderId = SortOrderId.From(EntityId.From(123));
    private static readonly SortOrderVarietyId TestVarietyId = SortOrderVarietyId.From(Guid.Parse("b2d49c95-3c74-4db9-92dc-b12c82d0ef30"));

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IProcessRunner, NoExternalProcesses>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDisposalWaitsForSortWorkBeforeDisposingItsDependencies(bool reactiveUpdate)
    {
        // A real loadout primes the database subscriptions. The direct-call case leaves them
        // empty so that only the explicit UpdateLoadOrders call can hold disposal open.
        var loadoutId = reactiveUpdate
            ? (await CreateLoadout()).LoadoutId
            : LoadoutId.From(EntityId.From(456));
        var pendingQuery = new PendingQuery(Connection);
        var variety = CreateVariety(pendingQuery);
        var provider = CreateProvider(variety);
        var manager = provider.GetRequiredService<SortOrderManager>();
        manager.RegisterSortOrderVarieties([variety], Game);

        Task? update = reactiveUpdate ? null : manager.UpdateLoadOrders(loadoutId).AsTask();
        Task? disposal = null;
        try
        {
            await pendingQuery.Entered.Task.WaitAsync(Timeout);
            disposal = provider.DisposeAsync().AsTask();
            await pendingQuery.CancellationObserved.Task.WaitAsync(Timeout);

            disposal.IsCompleted.Should().BeFalse("a cancelled callback can still be completing a database operation");
            pendingQuery.DependencyDisposed.Task.IsCompleted.Should().BeFalse("DI must keep the callback's dependencies alive until it exits");

            var lateUpdate = () => manager.UpdateLoadOrders(loadoutId).AsTask();
            await lateUpdate.Should().ThrowAsync<ObjectDisposedException>();
            var lateRegistration = () => manager.RegisterSortOrderVarieties([variety], Game);
            lateRegistration.Should().Throw<ObjectDisposedException>();

            pendingQuery.Release.TrySetResult();
            await disposal.WaitAsync(Timeout);
            if (update is not null)
            {
                var awaitUpdate = () => update;
                await awaitUpdate.Should().ThrowAsync<OperationCanceledException>();
            }

            pendingQuery.DatabaseReads.Should().NotBeEmpty();
            pendingQuery.DatabaseReads.Should().OnlyContain(count => count >= 0,
                "the final database access must happen before its dependency is disposed");
            pendingQuery.DependencyDisposed.Task.IsCompleted.Should().BeTrue();
            variety.ReconcileCalls.Should().Be(0, "cancellation must prevent the next database operation from starting");

            // DI or an owner can encounter an already disposed singleton again.
            await manager.DisposeAsync();
            manager.Dispose();
        }
        finally
        {
            pendingQuery.Release.TrySetResult();
            if (update is not null)
            {
                try { await update.WaitAsync(Timeout); }
                catch (OperationCanceledException) { }
            }
            await (disposal ?? provider.DisposeAsync().AsTask()).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task OpeningAnotherGamePreservesExistingGameSubscriptionsAndVarieties()
    {
        var pendingQuery = new PendingQuery(Connection);
        var variety = CreateVariety(pendingQuery);
        var provider = CreateProvider(variety);
        var cyberpunkManager = provider.GetRequiredService<SortOrderManager>();
        cyberpunkManager.RegisterSortOrderVarieties([variety], Game);
        var witcherManager = provider.GetRequiredService<SortOrderManager>();
        var witcherGame = ServiceProvider.GetRequiredService<Witcher3Game>();
        witcherManager.RegisterSortOrderVarieties([], witcherGame);

        try
        {
            witcherManager.Should().NotBeSameAs(cyberpunkManager);
            witcherManager.GetSortOrderVarieties().Should().BeEmpty();
            cyberpunkManager.GetSortOrderVarieties().Should().ContainSingle().Which.Should().BeSameAs(variety);

            // Adding a Cyberpunk profile must still reach its original reactive subscription
            // after another game has initialized a manager with no sort-order varieties.
            var creation = CreateLoadout();
            await pendingQuery.Entered.Task.WaitAsync(Timeout);
            pendingQuery.Release.TrySetResult();
            await creation.WaitAsync(Timeout);
        }
        finally
        {
            pendingQuery.Release.TrySetResult();
            await provider.DisposeAsync().AsTask().WaitAsync(Timeout);
        }

        pendingQuery.DatabaseReads.Should().NotBeEmpty();
        pendingQuery.DatabaseReads.Should().OnlyContain(count => count >= 0);
        pendingQuery.DependencyDisposed.Task.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task CallerCancellationStopsBeforeStartingAnotherDatabaseOperation()
    {
        var pendingQuery = new PendingQuery(Connection);
        var variety = CreateVariety(pendingQuery);
        var provider = CreateProvider(variety);
        var manager = provider.GetRequiredService<SortOrderManager>();
        manager.RegisterSortOrderVarieties([variety], Game);
        using var cancellation = new CancellationTokenSource();

        var update = manager.UpdateLoadOrders(LoadoutId.From(EntityId.From(456)), token: cancellation.Token).AsTask();
        try
        {
            await pendingQuery.Entered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            await pendingQuery.CancellationObserved.Task.WaitAsync(Timeout);
            pendingQuery.Release.TrySetResult();

            var awaitUpdate = () => update;
            await awaitUpdate.Should().ThrowAsync<OperationCanceledException>();
            pendingQuery.DatabaseReads.Should().ContainSingle().Which.Should().Be(0);
            variety.ReconcileCalls.Should().Be(0, "cancellation must prevent the next database operation from starting");
        }
        finally
        {
            pendingQuery.Release.TrySetResult();
            try { await update.WaitAsync(Timeout); }
            catch (OperationCanceledException) { }
            await provider.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    private ServiceProvider CreateProvider(ISortOrderVariety variety)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            // This database belongs to the outer test fixture; it is not disposed by this provider.
            .AddSingleton(Connection)
            .AddSingleton<ISortOrderVariety>(_ => variety)
            .AddGames()
            .BuildServiceProvider();

        // Model a dependency resolved before the manager, as its native connection is in the app.
        _ = provider.GetRequiredService<ISortOrderVariety>();
        return provider;
    }

    private static TestSortOrderVariety CreateVariety(PendingQuery pendingQuery) => new(pendingQuery);

    private sealed class TestSortOrderVariety(PendingQuery pendingQuery) : ISortOrderVariety, IDisposable
    {
        private int _reconcileCalls;

        public int ReconcileCalls => Volatile.Read(ref _reconcileCalls);
        public SortOrderVarietyId SortOrderVarietyId => TestVarietyId;
        public ListSortDirection SortDirectionDefault => ListSortDirection.Ascending;
        public IndexOverrideBehavior IndexOverrideBehavior => throw new NotSupportedException();
        public SortOrderUiMetadata SortOrderUiMetadata => throw new NotSupportedException();

        public Optional<SortOrderId> GetSortOrderIdFor(OneOf<LoadoutId, CollectionGroupId> parentEntity, IDb? db = null)
            => Optional<SortOrderId>.None;

        public ValueTask<SortOrderId> GetOrCreateSortOrderFor(LoadoutId loadoutId,
            OneOf<LoadoutId, CollectionGroupId> parentEntity, CancellationToken token = default)
            => pendingQuery.Run(token);

        public ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? db = null, CancellationToken token = default)
        {
            Interlocked.Increment(ref _reconcileCalls);
            return ValueTask.CompletedTask;
        }

        public IObservable<IChangeSet<IReactiveSortItem, ISortItemKey>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId)
            => throw new NotSupportedException();

        public IReadOnlyList<IReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db = null)
            => throw new NotSupportedException();

        public Task MoveItems(SortOrderId sortOrderId, ISortItemKey[] itemsToMove, ISortItemKey dropTargetItem,
            TargetRelativePosition relativePosition, IDb? db = null, CancellationToken token = default)
            => throw new NotSupportedException();

        public Task MoveItemDelta(SortOrderId sortOrderId, ISortItemKey sourceItem, int delta,
            IDb? db = null, CancellationToken token = default)
            => throw new NotSupportedException();

        public ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default)
            => throw new NotSupportedException();

        public void Dispose() => pendingQuery.DependencyDisposed.TrySetResult();
    }

    private sealed class PendingQuery(IConnection connection)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DependencyDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<int> DatabaseReads { get; } = new();

        public async ValueTask<SortOrderId> Run(CancellationToken token)
        {
            using var registration = token.Register(() => CancellationObserved.TrySetResult());
            Entered.TrySetResult();

            // A submitted transaction must finish even after cancellation. Hold its continuation
            // here, then exercise the same final model read before returning to the manager.
            await Release.Task.ConfigureAwait(false);
            DatabaseReads.Enqueue(DependencyDisposed.Task.IsCompleted ? -1 : Loadout.All(connection.Db).Count());
            return TestSortOrderId;
        }
    }

    private sealed class NoExternalProcesses : IProcessRunner
    {
        public void Run(Command command, bool logOutput = false)
            => throw new NotSupportedException("This test must not start external processes.");

        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException($"{command.TargetFilePath} is unavailable in this test.");

        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test must not start external processes.");
    }
}
