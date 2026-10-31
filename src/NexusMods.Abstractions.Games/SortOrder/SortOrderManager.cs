using System.Collections.Frozen;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using R3;
using OneOf;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Implementation for ISortOrderManager
/// Responsible for subscribing and updating sortOrders for all registered varieties when loadouts or collections change.
/// </summary>
public class SortOrderManager : ISortOrderManager, IDisposable, IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider; 
    private readonly IConnection _connection;
    private IDisposable? _subscription;
    private readonly ILogger<SortOrderManager> _logger;

    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private bool _isStopping;
    private Task? _disposeTask;
    
    private FrozenDictionary<SortOrderVarietyId, ISortOrderVariety> _sortOrderVarieties;

    public SortOrderManager(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _connection = _serviceProvider.GetRequiredService<IConnection>();
        _sortOrderVarieties = FrozenDictionary<SortOrderVarietyId, ISortOrderVariety>.Empty;
        _logger = _serviceProvider.GetRequiredService<ILogger<SortOrderManager>>();
    }

    /// <inheritdoc />
    public async ValueTask UpdateLoadOrders(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId = default, CancellationToken token = default)
    {
        using var operation = BeginOperation(token, throwIfStopping: true)!;
        await UpdateLoadOrdersCore(loadoutId, collectionGroupId, operation.Token).ConfigureAwait(false);
    }

    private async ValueTask UpdateLoadOrdersCore(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parentEntity = collectionGroupId.HasValue
            ? OneOf<LoadoutId, CollectionGroupId>.FromT1(collectionGroupId.Value)
            : OneOf<LoadoutId, CollectionGroupId>.FromT0(loadoutId);
        
        foreach (var sortOrderVariety in _sortOrderVarieties.Values)
        {
            token.ThrowIfCancellationRequested();
            // Reconcile the sort order for each variety
            var sortOrderId = await sortOrderVariety.GetOrCreateSortOrderFor(loadoutId, parentEntity, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await sortOrderVariety.ReconcileSortOrder(sortOrderId, token: token).ConfigureAwait(false);
        }
    }
    
    protected async ValueTask CreateSortOrders(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId = default, CancellationToken token = default)
    {
        var parentEntity = collectionGroupId.HasValue
            ? OneOf<LoadoutId, CollectionGroupId>.FromT1(collectionGroupId.Value)
            : OneOf<LoadoutId, CollectionGroupId>.FromT0(loadoutId);
        
        foreach (var sortOrderVariety in _sortOrderVarieties.Values)
        {
            // Create the sort order for each variety
            token.ThrowIfCancellationRequested();
            _ = await sortOrderVariety.GetOrCreateSortOrderFor(loadoutId, parentEntity, token).ConfigureAwait(false);
        }
    }

    protected async ValueTask DeleteSortOrders(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId = default, CancellationToken token = default)
    {
        var parentEntity = collectionGroupId.HasValue
            ? OneOf<LoadoutId, CollectionGroupId>.FromT1(collectionGroupId.Value)
            : OneOf<LoadoutId, CollectionGroupId>.FromT0(loadoutId);
        
        foreach (var sortOrderVariety in _sortOrderVarieties.Values)
        {
            // Delete the sort order for each variety
            token.ThrowIfCancellationRequested();
            var sortOrderId = sortOrderVariety.GetSortOrderIdFor(parentEntity);
            if (!sortOrderId.HasValue) continue;
            
            token.ThrowIfCancellationRequested();
            await sortOrderVariety.DeleteSortOrder(sortOrderId.Value, token: token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ISortOrderVariety> GetSortOrderVarieties()
    {
        return _sortOrderVarieties.Values;
    }

    /// <inheritdoc />
    public void RegisterSortOrderVarieties(ISortOrderVariety[] sortOrderVarieties, IGame game)
    {
        var varieties = sortOrderVarieties.ToDictionary(variety => variety.SortOrderVarietyId)
            .ToFrozenDictionary();

        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_isStopping, this);
            _subscription?.Dispose();
            _subscription = null;
            _sortOrderVarieties = varieties;

            if (_sortOrderVarieties.Count > 0)
                SubscribeToChanges(game.NexusModsGameId.Value);
        }
    }

    protected void SubscribeToChanges(NexusModsGameId nexusModsGameId)
    {
        var compositeDisposable = new CompositeDisposable();
        _subscription = compositeDisposable;
 
        
        // Listen to Loadouts additions/removals
        Subscribe(Loadout.ObserveAll(_connection)
            .StartWithEmpty()
            .FilterImmutable(l => l.Installation.GameId == nexusModsGameId)
            .ToObservable(), static async (changes, state, token) =>
                {
                    foreach (var change in changes)
                    {
                        token.ThrowIfCancellationRequested();
                        var loadoutId = change.Current.LoadoutId;
                        var parentEntity = loadoutId;

                        switch (change.Reason)
                        {
                            case ChangeReason.Add:
                                // Create the sort order for this loadout
                                // Might not be needed if the other subscription to loadout items handles it
                                await state.CreateSortOrders(loadoutId, token: token).ConfigureAwait(false);
                                break;
                            case ChangeReason.Update:
                                // If loadout changes, we handle that in a separate subscription
                                break;
                            case ChangeReason.Remove:
                                // Remove the orphaned sort orders
                                await state.DeleteSortOrders(loadoutId, token: token).ConfigureAwait(false);
                                break;
                        }
                    }
                }
            )
            .AddTo(compositeDisposable);
        
        
        // Listen to collection groups additions/removals
        Subscribe(CollectionGroup.ObserveAll(_connection)
            .StartWithEmpty()
            .FilterImmutable(cg => cg.AsLoadoutItemGroup().AsLoadoutItem().Loadout.Installation.GameId == nexusModsGameId)
            .ToObservable(), static async (changes, state, token) =>
                {
                    foreach (var change in changes)
                    {
                        token.ThrowIfCancellationRequested();
                        var collectionGroupId = change.Current.CollectionGroupId;
                        var loadoutId = change.Current.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
                        var parentEntity = Optional.Some(collectionGroupId);

                        switch (change.Reason)
                        {
                            case ChangeReason.Add:
                                // Create the sort order for this collection group
                                // Might not be needed if the other subscription to loadout items handles it
                                await state.CreateSortOrders(loadoutId, parentEntity, token: token).ConfigureAwait(false);
                                break;
                            case ChangeReason.Update:
                                // If collection group changes, we handle that in a separate subscription
                                break;
                            case ChangeReason.Remove:
                                // Remove the orphaned sort orders
                                await state.DeleteSortOrders(loadoutId, parentEntity, token: token).ConfigureAwait(false);
                                break;
                        }
                    }
                }
            )
            .AddTo(compositeDisposable);
        

        // Listen to item additions/removals
        // TODO: consider making this Variant specific, to only listen to changes for the relevant items
        // TODO: listen to changes to Game files too
        Subscribe(SortOrderQueries.TrackLoadoutItemChanges(_connection, nexusModsGameId)
            .ToObservable(), static async (changes, state, token) =>
            {
                // Filter out updates where nothing changed
                var filteredChanges = changes
                    .Where(change => change.Reason != ChangeReason.Update)
                    .ToList();
                if (filteredChanges.Count == 0) return;

                var loadouts = new HashSet<LoadoutId>();
                var collections = new Dictionary<CollectionGroupId, LoadoutId>();
                foreach (var change in filteredChanges)
                {
                    token.ThrowIfCancellationRequested();
                    if (change.Reason == ChangeReason.Update)
                        continue;
                    
                    var loadoutId = change.Current.LoadoutId;
                    loadouts.Add(loadoutId);
                    
                    var collectionId = change.Current.CollectionId;
                    if (collectionId != 0)
                        collections[collectionId] = loadoutId;
                }
                
                // Update loadout sort orders
                foreach (var loadoutId in loadouts)
                {
                    await state.UpdateLoadOrdersCore(loadoutId, default, token).ConfigureAwait(false);
                }
                
                // Update collection sort orders
                foreach (var pair in collections)
                {
                    await state.UpdateLoadOrdersCore(pair.Value, Optional.Some(pair.Key), token).ConfigureAwait(false);
                }
            })
            .AddTo(compositeDisposable);
        
    }

    private IDisposable Subscribe<T>(Observable<T> source, Func<T, SortOrderManager, CancellationToken, ValueTask> callback)
    {
        return source.SubscribeAwait((Manager: this, Callback: callback), static async (changes, state, token) =>
        {
            using var operation = state.Manager.BeginOperation(token, throwIfStopping: false);
            if (operation is null) return;

            try
            {
                operation.Token.ThrowIfCancellationRequested();
                await state.Callback(changes, state.Manager, operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                // Cancellation ends this update during shutdown or subscription replacement.
            }
        }, configureAwait: false);
    }

    private Operation? BeginOperation(CancellationToken token, bool throwIfStopping)
    {
        lock (_lifetimeGate)
        {
            if (_isStopping)
            {
                ObjectDisposedException.ThrowIf(throwIfStopping, this);
                return null;
            }

            var operation = new Operation(this, CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token));
            _activeOperations++;
            return operation;
        }
    }

    private void EndOperation()
    {
        lock (_lifetimeGate)
        {
            _activeOperations--;
            if (_isStopping && _activeOperations == 0)
                _operationsDrained.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);

            _isStopping = true;
            if (_activeOperations == 0)
                _operationsDrained.TrySetResult();

            // Leave the gate before cancellation callbacks run. DI must await all owned work
            // before it disposes the connection and its native query/storage dependencies.
            _disposeTask = Task.Run(async () =>
            {
                try
                {
                    _subscription?.Dispose();
                }
                finally
                {
                    try
                    {
                        await _shutdown.CancelAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await _operationsDrained.Task.ConfigureAwait(false);
                        _shutdown.Dispose();
                    }
                }
            });
            return new ValueTask(_disposeTask);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Operation(SortOrderManager owner, CancellationTokenSource cancellation) : IDisposable
    {
        public CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            cancellation.Dispose();
            owner.EndOperation();
        }
    }
}


