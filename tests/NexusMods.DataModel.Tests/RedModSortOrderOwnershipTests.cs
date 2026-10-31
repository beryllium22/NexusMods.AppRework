using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.RedEngine.Cyberpunk2077.Models;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using OneOf;
using Xunit.Abstractions;

namespace NexusMods.DataModel.Tests;

public class RedModSortOrderOwnershipTests(ITestOutputHelper output)
    : ACyberpunkIsolatedGameTest<RedModSortOrderOwnershipTests>(output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IProcessRunner, NoExternalProcesses>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCreationUsesOneOrderForEachLiveParent(bool collectionOrder)
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout, "Collection order");
        var parent = collectionOrder
            ? OneOf<LoadoutId, CollectionGroupId>.FromT1(collection.CollectionGroupId)
            : OneOf<LoadoutId, CollectionGroupId>.FromT0(loadout.LoadoutId);
        var variety = new RedModSortOrderVariety(ServiceProvider);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => variety.GetOrCreateSortOrderFor(loadout, parent).AsTask()));

        results.Distinct().Should().ContainSingle();
        results[0].Should().NotBe(default(SortOrderId));
        var order = SortOrder.FindByParentEntity(Connection.Db, parent).Should().ContainSingle().Which;
        order.SortOrderId.Should().Be(results[0]);
        order.LoadoutId.Should().Be(loadout.LoadoutId);
        order.Contains(RedModSortOrder.Marker).Should().BeTrue();
    }

    [Fact]
    public async Task QueuedReactiveWorkCannotRecreateDeletedProfileOrders()
    {
        var loadout = await CreateLoadout();
        var variety = new GatedVariety(ServiceProvider);
        var manager = new SortOrderManager(ServiceProvider);
        manager.RegisterSortOrderVarieties([variety], Game);
        var update = manager.UpdateLoadOrders(loadout).AsTask();
        try
        {
            await variety.Entered.Task.WaitAsync(Timeout);
            await LoadoutManager.DeleteLoadout(loadout);
            variety.Release.TrySetResult();
            await update.WaitAsync(Timeout);
            await manager.DisposeAsync().AsTask().WaitAsync(Timeout);

            SortOrder.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
            SortOrderItem.All(Connection.Db).Should().BeEmpty();
            variety.ReconcileCalls.Should().Be(0, "the manager must skip the default ID returned for a deleted owner");
        }
        finally
        {
            variety.Release.TrySetResult();
            await manager.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleReconciliationCannotWriteItemsAfterItsOwnerWasDeleted(bool collectionOrder)
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout, "Removed collection");
        var parent = collectionOrder
            ? OneOf<LoadoutId, CollectionGroupId>.FromT1(collection.CollectionGroupId)
            : OneOf<LoadoutId, CollectionGroupId>.FromT0(loadout.LoadoutId);
        var variety = new ExposedVariety(ServiceProvider);
        var order = await variety.GetOrCreateSortOrderFor(loadout, parent);
        var snapshot = Connection.Db;

        if (collectionOrder)
        {
            // Packed collection parents are not ordinary reverse references. Simulate
            // the window before the reactive order-removal callback has caught up.
            using var tx = Connection.BeginTransaction();
            tx.Delete(collection.Id, recursive: false);
            await tx.Commit();
        }
        else
        {
            await LoadoutManager.DeleteLoadout(loadout);
        }

        (await variety.PersistLateResult(order, snapshot)).Should().BeFalse();
        SortOrderItem.FindByParentSortOrder(Connection.Db, order).Should().BeEmpty();
        (await variety.GetOrCreateSortOrderFor(loadout, parent)).Should().Be(default(SortOrderId));
    }

    [Fact]
    public async Task AParentFromAnotherProfileCannotCreateAnOrder()
    {
        var source = await CreateLoadout();
        var other = await CreateLoadout();
        var collection = await CreateCollection(other, "Foreign collection");
        var variety = new RedModSortOrderVariety(ServiceProvider);

        (await variety.GetOrCreateSortOrderFor(source, OneOf<LoadoutId, CollectionGroupId>.FromT0(other.LoadoutId)))
            .Should().Be(default(SortOrderId));
        (await variety.GetOrCreateSortOrderFor(source, OneOf<LoadoutId, CollectionGroupId>.FromT1(collection.CollectionGroupId)))
            .Should().Be(default(SortOrderId));
        SortOrder.All(Connection.Db).Should().BeEmpty();
    }

    private sealed class ExposedVariety(IServiceProvider services) : RedModSortOrderVariety(services)
    {
        public ValueTask<bool> PersistLateResult(SortOrderId order, IDb snapshot)
            => TryPersistSortOrder(order, [new SortItemData<SortItemKey<string>>(new SortItemKey<string>("LateModule"), 0)], snapshot);
    }

    private sealed class GatedVariety(IServiceProvider services) : RedModSortOrderVariety(services)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReconcileCalls;

        public override async ValueTask<SortOrderId> GetOrCreateSortOrderFor(LoadoutId loadoutId,
            OneOf<LoadoutId, CollectionGroupId> parentEntity, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return await base.GetOrCreateSortOrderFor(loadoutId, parentEntity, token);
        }

        public override ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? referenceDb = null, CancellationToken token = default)
        {
            Interlocked.Increment(ref ReconcileCalls);
            return base.ReconcileSortOrder(sortOrderId, referenceDb, token);
        }
    }

    private sealed class NoExternalProcesses : IProcessRunner
    {
        public void Run(Command command, bool logOutput = false) => throw new NotSupportedException("Sort-order tests must not launch processes.");
        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Sort-order tests must not launch processes.");
        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Sort-order tests must not launch processes.");
    }
}
