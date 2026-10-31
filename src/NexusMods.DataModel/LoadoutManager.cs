using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.DataModel.Sorting;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.DatomIterators;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.MnemonicDB.Abstractions.Internals;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.MnemonicDB.Abstractions.ValueSerializers;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using OneOf;

namespace NexusMods.DataModel;

internal partial class LoadoutManager : ILoadoutManager
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IJobMonitor _jobMonitor;
    private readonly IFileHashesService _fileHashesService;
    private readonly IConnection _connection;
    private readonly ISynchronizerService _synchronizerService;
    private readonly IGarbageCollectorRunner _garbageCollectorRunner;
    private readonly IGameRegistry _gameRegistry;
    // Metadata creation and first ingestion must finish before another profile can
    // decide whether this installation has an active profile or initial disk state.
    private readonly SemaphoreSlim _installationCreationGate = new(1, 1);

    public LoadoutManager(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<LoadoutManager>>();
        _serviceProvider = serviceProvider;
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _fileHashesService = serviceProvider.GetRequiredService<IFileHashesService>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _synchronizerService = serviceProvider.GetRequiredService<ISynchronizerService>();
        _garbageCollectorRunner = serviceProvider.GetRequiredService<IGarbageCollectorRunner>();
        _gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();
    }

    public async ValueTask<GameInstallMetadata.ReadOnly> ManageInstallation(GameInstallation installation)
    {
        await _installationCreationGate.WaitAsync();
        try
        {
            return await ManageInstallationCore(installation);
        }
        finally
        {
            _installationCreationGate.Release();
        }
    }

    private async ValueTask<GameInstallMetadata.ReadOnly> ManageInstallationCore(GameInstallation installation)
    {
        if (!_gameRegistry.TryGetMetadata(installation, out var metadata))
        {
            using var metadataTx = _connection.BeginTransaction();
            var newMetadata = new GameInstallMetadata.New(metadataTx)
            {
                Store = installation.LocatorResult.Store,
                Path = installation.Locations[LocationId.Game].Path.ToString(),
                GameId = installation.Game.NexusModsGameId.Value,
                Name = installation.Game.DisplayName,
            };

            var metadataResult = await metadataTx.Commit();
            metadata = metadataResult.Remap(newMetadata);
        }

        return metadata;
    }

    public IJobTask<CreateLoadoutJob, Loadout.ReadOnly> CreateLoadout(GameInstallation installation, string? suggestedName = null)
    {
        var name = suggestedName is null ? null : LoadoutName.Normalize(suggestedName);
        return _jobMonitor.Begin(new CreateLoadoutJob(installation), async ctx =>
        {
            await _installationCreationGate.WaitAsync(ctx.CancellationToken);
            try
            {
                // Prime the hash database to make sure it's loaded
                await _fileHashesService.GetFileHashesDb();

                var metadata = await ManageInstallationCore(installation);
                using var tx = _connection.BeginTransaction();

                List<LocatorId> locatorIds = [];
                {
                    var metadataLocatorIds = installation.LocatorResult.LocatorIds;
                    var distinctLocatorIds = metadataLocatorIds.Distinct().ToArray();

                    if (distinctLocatorIds.Length != metadataLocatorIds.Length)
                    {
                        _logger.LogWarning("Duplicate locator ids `{LocatorIds}` found in LocatorResultMetadata for {Game} when creating new loadout", metadataLocatorIds, installation.Game.DisplayName);
                    }

                    locatorIds.AddRange(distinctLocatorIds);
                }

                if (!_fileHashesService.TryGetVanityVersion((installation.LocatorResult.Store, locatorIds.ToArray()), out var version))
                    _logger.LogWarning("Unable to find game version for {Game}", installation.Game.DisplayName);

                var loadoutId = tx.TempId();
                tx.Add(loadoutId, Loadout.Installation, metadata.Id);
                tx.Add(loadoutId, Loadout.Revision, 0UL);
                tx.Add(loadoutId, Loadout.LoadoutKind, LoadoutKind.Default);
                foreach (var locatorId in locatorIds) tx.Add(loadoutId, Loadout.LocatorIds, locatorId);
                tx.Add(loadoutId, Loadout.GameVersion, version);
                // Allocate the short name against the commit's current basis, so concurrent
                // create/copy requests cannot both claim the same label.
                tx.Add((loadoutId, metadata.Id, name), static (transaction, basis, args) =>
                {
                    var shortName = GetNewShortName(basis, GameInstallMetadataId.From(args.Id));
                    transaction.Add(args.loadoutId, Loadout.ShortName, shortName);
                    transaction.Add(args.loadoutId, Loadout.Name, args.name ?? "Loadout " + shortName);
                });

                // Create the user's default collection
                _ = new CollectionGroup.New(tx, out var userCollectionId)
                {
                    IsReadOnly = false,
                    LoadoutItemGroup = new LoadoutItemGroup.New(tx, userCollectionId)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(tx, userCollectionId)
                        {
                            Name = "My Mods",
                            LoadoutId = LoadoutId.From(loadoutId),
                        },
                    },
                };

                var result = await tx.Commit();
                var remappedLoadout = Loadout.Load(result.Db, result[loadoutId]);

                // If there is no currently synced loadout, then we can ingest the game folder
                if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(remappedLoadout.Installation, out var lastSyncedLoadoutId))
                {
                    await _synchronizerService.Synchronize(remappedLoadout.LoadoutId);
                    remappedLoadout = remappedLoadout.Rebase();
                }
                else
                {
                    // check if the last synced loadout is valid (can apparently happen if the user unmanaged the game and manages it again)
                    var lastSyncedLoadout = Loadout.Load(remappedLoadout.Db, lastSyncedLoadoutId);
                    if (!lastSyncedLoadout.IsValid())
                    {
                        await _synchronizerService.Synchronize(remappedLoadout.LoadoutId);
                        remappedLoadout = remappedLoadout.Rebase();
                    }
                }

                return remappedLoadout;
            }
            finally
            {
                _installationCreationGate.Release();
            }
        });
    }

    private static string GetNewShortName(IDb db, GameInstallMetadataId installationId)
    {
        var existingShortNames = Loadout.All(db)
            .Where(l => l.IsVisible() && l.InstallationId == installationId)
            .Select(l => l.ShortName)
            .ToArray();

        var result = LoadoutNameProvider.GetNewShortName(existingShortNames);
        return result;
    }

    public async ValueTask<Loadout.ReadOnly> CopyLoadout(LoadoutId loadoutId, CancellationToken cancellationToken = default, string? suggestedName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = suggestedName is null ? null : LoadoutName.Normalize(suggestedName);
        var baseDb = _connection.Db;
        var loadout = Loadout.Load(baseDb, loadoutId);
        if (!loadout.IsValid() || !loadout.IsVisible())
            throw new InvalidOperationException("This profile is no longer available to copy.");

        var items = LoadoutItem.FindByLoadout(baseDb, loadout).ToArray();
        var itemIds = items.Select(item => item.Id).ToHashSet();
        var priorities = LoadoutItemGroupPriority.FindByLoadout(baseDb, loadout).ToArray();
        var parentLinks = new Dictionary<EntityId, EntityId>();
        foreach (var item in items)
        {
            if (!LoadoutItem.Parent.TryGetValue(item, out var parentId)) continue;
            if (!itemIds.Contains(parentId) || !LoadoutItemGroup.Load(baseDb, parentId).IsValid())
                throw new InvalidOperationException("A mod refers to a missing group or a group outside this profile. Repair its group before copying.");
            parentLinks.Add(item.Id, parentId);
        }
        var verifiedParents = new HashSet<EntityId>();
        foreach (var start in parentLinks.Keys)
        {
            var chain = new HashSet<EntityId>();
            var current = start;
            while (!verifiedParents.Contains(current) && parentLinks.TryGetValue(current, out var parent))
            {
                if (!chain.Add(current))
                    throw new InvalidOperationException("The profile contains a cycle of parent groups. Repair those groups before copying.");
                current = parent;
            }
            verifiedParents.UnionWith(chain);
        }
        foreach (var priority in priorities)
        {
            if (!itemIds.Contains(priority.TargetId.Value) || !priority.Target.IsValid())
                throw new InvalidOperationException("A conflict priority refers to a missing mod or another profile. Repair the priority before copying.");
        }
        var sourceIds = new HashSet<EntityId>(itemIds) { loadout.Id };
        sourceIds.UnionWith(priorities.Select(priority => priority.Id));
        var sortOrders = SortOrder.FindByLoadout(baseDb, loadout).ToArray();
        foreach (var order in sortOrders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validParent = order.ParentEntity.Match(
                id => id == loadout.LoadoutId,
                id => CollectionGroup.Load(baseDb, id).IsValid() && itemIds.Contains(id.Value));
            if (!validParent)
                throw new InvalidOperationException("A load order refers to an invalid collection or another profile. Repair the load order before copying.");
            sourceIds.Add(order.Id);
            sourceIds.UnionWith(SortOrderItem.FindByParentSortOrder(baseDb, order).Select(item => item.Id));
        }

        // Packed SortOrder.ParentEntity IDs are scalars, not database references.
        // Reserve permanent IDs first, without exposing any partially copied profiles,
        // items, or orders to UI and sort-order reconciliation observers.
        var entityIds = new Dictionary<EntityId, EntityId>();
        using (var reserve = _connection.BeginTransaction())
        {
            foreach (var sourceId in sourceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reservedId = reserve.TempId();
                reserve.Add(reservedId, ProfileCopyReservation.Reserved, Null.Instance);
                entityIds.Add(sourceId, reservedId);
            }
            var reserved = await reserve.Commit();
            entityIds = entityIds.ToDictionary(pair => pair.Key, pair => reserved[pair.Value]);
        }

        try
        {
            using var tx = _connection.BeginTransaction();
            Memory<byte> buffer = GC.AllocateUninitializedArray<byte>(32);
            var parentAttribute = baseDb.AttributeCache.GetAttributeId(SortOrder.ParentEntity.Id);
            EntityId Remap(EntityId id) => entityIds.GetValueOrDefault(id, id);

            foreach (var (oldId, newId) in entityIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tx.Retract(newId, ProfileCopyReservation.Reserved, Null.Instance);
                if (oldId == loadout.Id) continue;

                foreach (var datom in baseDb.Get(oldId))
                {
                    if (datom.A == parentAttribute) continue;
                    if (buffer.Length < datom.ValueSpan.Length)
                        buffer = GC.AllocateUninitializedArray<byte>(datom.ValueSpan.Length);
                    var value = buffer[..datom.ValueSpan.Length];
                    datom.ValueSpan.CopyTo(value.Span);
                    datom.Prefix.ValueTag.Remap(value.Span, Remap);
                    tx.Add(new Datom(new KeyPrefix(newId, datom.A, TxId.Tmp, false, datom.Prefix.ValueTag), value));
                }
            }

            foreach (var order in sortOrders)
            {
                var parent = order.ParentEntity.Match(
                    id => OneOf<LoadoutId, CollectionGroupId>.FromT0(LoadoutId.From(Remap(id.Value))),
                    id => OneOf<LoadoutId, CollectionGroupId>.FromT1(CollectionGroupId.From(Remap(id.Value))));
                tx.Add(Remap(order.Id), SortOrder.ParentEntity, parent);
            }

            // Copy profile configuration explicitly. Application timestamps and game
            // deployment receipts describe the source's disk state, never the new profile.
            var newLoadoutId = Remap(loadout.Id);
            tx.Add(newLoadoutId, Loadout.Installation, loadout.InstallationId.Value);
            tx.Add(newLoadoutId, Loadout.GameVersion, loadout.GameVersion);
            foreach (var locatorId in loadout.LocatorIds) tx.Add(newLoadoutId, Loadout.LocatorIds, locatorId);
            tx.Add(newLoadoutId, Loadout.Revision, 0UL);
            tx.Add(newLoadoutId, Loadout.LoadoutKind, LoadoutKind.Default);
            tx.Add(newLoadoutId, (loadoutId, name, cancellationToken), static (transaction, basis, targetId, args) =>
            {
                args.cancellationToken.ThrowIfCancellationRequested();
                var source = Loadout.Load(basis, args.loadoutId);
                if (!source.IsValid() || !source.IsVisible())
                    throw new InvalidOperationException("The original profile was removed before its copy could be completed.");
                var shortName = GetNewShortName(basis, source.InstallationId);
                transaction.Add(targetId, Loadout.ShortName, shortName);
                transaction.Add(targetId, Loadout.Name, args.name ?? "Loadout " + shortName);
            });

            cancellationToken.ThrowIfCancellationRequested();
            var result = await tx.Commit();
            return Loadout.Load(result.Db, newLoadoutId);
        }
        catch
        {
            // Do not use the caller's cancelled token to abandon cleanup. A crash between
            // reservation and publication is recovered on the next application startup.
            using var cleanup = _connection.BeginTransaction();
            foreach (var id in entityIds.Values)
            {
                if (ProfileCopyReservation.Load(_connection.Db, id).IsValid())
                    cleanup.Delete(id, recursive: false);
            }
            await cleanup.Commit();
            throw;
        }
    }

    public async ValueTask RenameLoadout(LoadoutId loadoutId, string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = LoadoutName.Normalize(name);
        cancellationToken.ThrowIfCancellationRequested();
        using var tx = _connection.BeginTransaction();
        tx.Add((loadoutId, normalizedName, cancellationToken), static (transaction, basis, args) =>
        {
            args.cancellationToken.ThrowIfCancellationRequested();
            var loadout = Loadout.Load(basis, args.loadoutId);
            if (!loadout.IsValid() || !loadout.IsVisible())
                throw new InvalidOperationException("This profile is no longer available to rename.");
            transaction.Add(args.loadoutId, Loadout.Name, args.normalizedName);
        });
        await tx.Commit();
    }

    public async ValueTask DeleteLoadout(LoadoutId loadoutId, GarbageCollectorRunMode gcRunMode = GarbageCollectorRunMode.DoNotRun, bool deactivateIfActive = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        if (!loadout.IsValid()) return;

        var metadata = GameInstallMetadata.Load(_connection.Db, loadout.InstallationId);
        if (deactivateIfActive && GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastAppliedLoadout) && lastAppliedLoadout == loadoutId.Value)
        {
            // Keep the profile available if deactivation fails or is cancelled. No
            // deletion marker is needed when the final graph removal is atomic.
            await DeactivateCurrentLoadout(loadout.InstallationInstance, cancellationToken: cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var tx = _connection.BeginTransaction();
        tx.Add((loadoutId, deactivateIfActive, cancellationToken), static (transaction, basis, args) =>
        {
            args.cancellationToken.ThrowIfCancellationRequested();
            var current = Loadout.Load(basis, args.loadoutId);
            if (!current.IsValid()) return;

            if (args.deactivateIfActive
                && GameInstallMetadata.LastSyncedLoadout.TryGetValue(current.Installation, out var applied)
                && applied == current.Id)
                throw new InvalidOperationException("This profile was applied while it was being deleted. Apply another profile before retrying.");

            // Deactivation can ingest external files, and queued operations may have
            // added orders or items. Remove the complete graph from the commit basis.
            transaction.Delete(current.Id, recursive: false);
            foreach (var item in LoadoutItem.FindByLoadout(basis, current))
                transaction.Delete(item.Id, recursive: false);
            foreach (var priority in LoadoutItemGroupPriority.FindByLoadout(basis, current))
                transaction.Delete(priority.Id, recursive: false);
            foreach (var order in SortOrder.FindByLoadout(basis, current))
            {
                foreach (var item in SortOrderItem.FindByParentSortOrder(basis, order))
                    transaction.Delete(item.Id, recursive: false);
                transaction.Delete(order.Id, recursive: false);
            }
        });
        await tx.Commit();
        
        // Execute the garbage collector
        await _garbageCollectorRunner.RunWithMode(gcRunMode);
    }

    public async ValueTask ActivateLoadout(LoadoutId loadoutId, CancellationToken cancellationToken = default)
    {
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;

        var state = await synchronizer.ReindexState(loadout.InstallationInstance);
        await synchronizer.BuildProcessRun(loadout, state, cancellationToken);
    }

    public async ValueTask DeactivateCurrentLoadout(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = _gameRegistry.ForceGetMetadata(installation);
        var synchronizer = installation.GetGame().Synchronizer;

        if (!metadata.Contains(GameInstallMetadata.LastSyncedLoadout)) return;

        // Preserve external file changes without retrying a failed deployment of the loadout
        // being left. Deployment completion is tracked independently from this disk baseline.
        var previousLoadout = Loadout.Load(_connection.Db, metadata.LastSyncedLoadout);
        await synchronizer.SynchronizeFiles(previousLoadout, new SynchronizeLoadoutJob(previousLoadout.LoadoutId)
        {
            CancellationToken = cancellationToken,
        });
        cancellationToken.ThrowIfCancellationRequested();

        var metadataLocatorIds = installation.LocatorResult.LocatorIds;
        var locatorIds = metadataLocatorIds.Distinct().ToArray();
        
        if (locatorIds.Length != metadataLocatorIds.Length)
        {
            _logger.LogWarning("Duplicate locator ids `{LocatorIds}` found in LocatorResultMetadata for {Game} when deactivating loadout", metadataLocatorIds, installation.Game.DisplayName);
        }

        await synchronizer.ResetToOriginalGameState(installation, locatorIds);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Optional<LoadoutId> GetCurrentlyActiveLoadout(GameInstallation installation)
    {
        var metadata = _gameRegistry.ForceGetMetadata(installation);
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastAppliedLoadout))
            return Optional<LoadoutId>.None;
        return LoadoutId.From(lastAppliedLoadout);
    }

    public IJobTask<UnmanageGameJob, GameInstallation> UnManage(GameInstallation installation, bool runGc = true, bool cleanGameFolder = true, CancellationToken cancellationToken = default)
    {
        return _jobMonitor.Begin(new UnmanageGameJob(installation), async ctx =>
        {
            var metadata = _gameRegistry.ForceGetMetadata(installation);

            if (GetCurrentlyActiveLoadout(installation).HasValue && cleanGameFolder) await DeactivateCurrentLoadout(installation, cancellationToken: cancellationToken);
            await ctx.YieldAsync();

            {
                using var tx1 = _connection.BeginTransaction();
                foreach (var loadout in Loadout.FindByInstallation(metadata.Db, metadata))
                {
                    tx1.Add(loadout.Id, Loadout.LoadoutKind, LoadoutKind.Deleted);
                }

                var result = await tx1.Commit();

                metadata = GameInstallMetadata.Load(result.Db, metadata.Id);
                Debug.Assert(Loadout.FindByInstallation(metadata.Db, metadata).All(x => !x.IsVisible()), "all loadouts shouldn't be visible anymore");
            }

            foreach (var loadout in Loadout.FindByInstallation(metadata.Db, metadata))
            {
                _logger.LogInformation("Deleting loadout {Loadout} - {ShortName}", loadout.Name, loadout.ShortName);
                await ctx.YieldAsync();
                await DeleteLoadout(loadout, gcRunMode: GarbageCollectorRunMode.DoNotRun, deactivateIfActive: cleanGameFolder, cancellationToken: cancellationToken);
            }

            // Retract all `GameBakedUpFile` entries to allow for game file backups to be cleaned up from the FileStore
            using var tx = _connection.BeginTransaction();
            foreach (var file in GameBackedUpFile.All(_connection.Db))
            {
                if (file.GameInstallId.Value == metadata)
                    tx.Delete(file, recursive: false);
            }

            // Delete the last applied/scanned disk state data
            metadata = metadata.Rebase();

            foreach (var entry in DiskStateEntry.FindByGame(metadata.Db, metadata))
            {
                tx.Delete(entry, recursive: false);
            }

            if (metadata.Contains(GameInstallMetadata.LastSyncedLoadoutId))
                tx.Retract(metadata, GameInstallMetadata.LastSyncedLoadoutId, metadata.LastSyncedLoadoutId.Value);
            if (metadata.Contains(GameInstallMetadata.LastSyncedLoadoutTransactionId))
                tx.Retract(metadata, GameInstallMetadata.LastSyncedLoadoutTransactionId, metadata.LastSyncedLoadoutTransactionId.Value);
            if (metadata.Contains(GameInstallMetadata.InitialDiskStateTransactionId))
                tx.Retract(metadata, GameInstallMetadata.InitialDiskStateTransactionId, metadata.InitialDiskStateTransactionId.Value);
            if (metadata.Contains(GameInstallMetadata.LastScannedDiskStateTransactionId))
                tx.Retract(metadata, GameInstallMetadata.LastScannedDiskStateTransactionId, metadata.LastScannedDiskStateTransactionId.Value);

            await tx.Commit();

            if (runGc) _garbageCollectorRunner.Run();
            return installation;
        });
    }

    public async Task<LoadoutItemGroup.ReadOnly> InstallItemWrapper(LoadoutId targetLoadout, Func<ITransaction, Task<LoadoutItemGroupId>> func)
    {
        using var tx = _connection.BeginTransaction();

        var groupId = await func(tx);
        tx.Add(new AddPriorityTxFunc(targetLoadout, groupId));

        var result = await tx.Commit();
        return LoadoutItemGroup.Load(result.Db, result[groupId]);
    }

    public IJobTask<IInstallLoadoutItemJob, InstallLoadoutItemJobResult> InstallItem(
        LibraryItem.ReadOnly libraryItem,
        LoadoutId targetLoadout,
        Optional<LoadoutItemGroupId> parent = default,
        ILibraryItemInstaller? installer = null,
        ILibraryItemInstaller? fallbackInstaller = null)
    {
        return InstallItem(libraryItem, targetLoadout, inputTx: null, parent: parent, installer: installer, fallbackInstaller: fallbackInstaller);
    }

    private IJobTask<IInstallLoadoutItemJob, InstallLoadoutItemJobResult> InstallItem(
        LibraryItem.ReadOnly libraryItem,
        LoadoutId targetLoadout,
        ITransaction? inputTx,
        Optional<LoadoutItemGroupId> parent = default,
        ILibraryItemInstaller? installer = null,
        ILibraryItemInstaller? fallbackInstaller = null,
        bool useDefaultCollection = true,
        bool addPriority = true)
    {
        var mainTransaction = inputTx is null ? _connection.BeginTransaction() : null;
        var tx = inputTx ?? mainTransaction!;

        var job = InstallLoadoutItemJob.Create(_serviceProvider, libraryItem, targetLoadout, tx,
            groupId: parent, installer: installer, fallbackInstaller: fallbackInstaller, useDefaultCollection: useDefaultCollection);
        return _jobMonitor.Begin(job, async context =>
        {
            var result = await job.StartAsync(context);
            var targetId = result.GroupTxId;
            if (addPriority)
                tx.Add(new AddPriorityTxFunc(targetLoadout, targetId));

            if (mainTransaction is null) return new InstallLoadoutItemJobResult(null, targetId);

            var commitResult = await mainTransaction.Commit();
            var remapped = commitResult[targetId];
            mainTransaction.Dispose();

            return new InstallLoadoutItemJobResult(LoadoutItemGroup.Load(commitResult.Db, remapped), LoadoutItemGroupId.From(0));
        });
    }

    public async ValueTask RemoveItems(LoadoutItemGroupId[] groupIds)
    {
        using var tx = _connection.BeginTransaction();
        RemoveItems(tx, groupIds);
        await tx.Commit();
    }

    private void RemoveItems(ITransaction tx, LoadoutItemGroupId[] groupIds)
    {
        var db = _connection.Db;
        var loadouts = new Dictionary<LoadoutId, List<LoadoutItemGroupId>>();

        foreach (var groupId in groupIds)
        {
            var group = LoadoutItemGroup.Load(db, groupId);
            if (!group.IsValid()) continue;

            tx.Delete(groupId, recursive: true);

            var priorities = LoadoutItemGroupPriority.FindByTarget(db, groupId);
            foreach (var priority in priorities)
            {
                tx.Delete(priority.Id, recursive: false);
            }

            var loadoutId = group.AsLoadoutItem().LoadoutId;
            if (!loadouts.TryGetValue(loadoutId, out var list))
            {
                list = new List<LoadoutItemGroupId>(capacity: groupIds.Length);
                loadouts[loadoutId] = list;
            }

            list.Add(groupId);
        }

        foreach (var kv in loadouts)
        {
            tx.Add(new RebalancePrioritiesTxFunc(kv.Key, kv.Value.Select(x => x.Value).ToArray()));
        }
    }

    public async ValueTask RemoveCollection(CollectionGroupId collection)
    {
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();

        tx.Delete(collection, recursive: true);
        var groupIds = LoadoutItem.FindByParent(db, collection).OfTypeLoadoutItemGroup().Select(x => x.LoadoutItemGroupId).ToArray();
        RemoveItems(tx, groupIds);

        await tx.Commit();
    }

    public async ValueTask<CollectionGroup.ReadOnly> CloneCollection(CollectionGroupId collectionId)
    {
        Span<byte> refScratch = stackalloc byte[8];
        using var writer = new PooledMemoryBufferWriter();

        var basisDb = _connection.Db;

        Dictionary<EntityId, EntityId> remappedIds = new();
        var query = _connection.Query<EntityId>($"""
                                          WITH RECURSIVE ChildLoadoutItems (Id) AS 
                                          (SELECT {collectionId.Value} 
                                          UNION
                                          SELECT Id FROM (SELECT Id, Parent FROM mdb_LoadoutItem(Db=>{basisDb})
                                          UNION ALL
                                          SELECT Id, ParentEntity FROM mdb_SortOrder(Db=>{basisDb})
                                          UNION ALL
                                          SELECT Id, ParentSortOrder FROM mdb_SortOrderItem(Db=>{basisDb}))
                                          WHERE Parent in (SELECT Id FROM ChildLoadoutItems))
                                          SELECT DISTINCT Id FROM ChildLoadoutItems
                                          """);
        using var tx = _connection.BeginTransaction();
        foreach (var itemId in query)
        {
            remappedIds.TryAdd(itemId, tx.TempId());
        }

        foreach (var (oldId, newId) in remappedIds)
        {
            var entity = basisDb.Get(oldId);
            foreach (var datom in entity)
            {
                // Remap the value part of references
                if (datom.Prefix.ValueTag == ValueTag.Reference)
                {
                    var oldRef = EntityId.From(UInt64Serializer.Read(datom.ValueSpan));
                    if (!remappedIds.TryGetValue(oldRef, out var newRef))
                    {
                        tx.Add(newId, datom.A, datom.Prefix.ValueTag, datom.ValueSpan);
                        continue;
                    }
                    MemoryMarshal.Write(refScratch, newRef);
                    tx.Add(newId, datom.A, datom.Prefix.ValueTag, refScratch);
                }
                // It's rare, but the Ref,UShort/String tuple type may include a ref that needs to be remapped
                else if (datom.Prefix.ValueTag == ValueTag.Tuple3_Ref_UShort_Utf8I)
                {
                    var (r, s, str) = Tuple3_Ref_UShort_Utf8I_Serializer.Read(datom.ValueSpan);
                    if (!remappedIds.TryGetValue(r, out var newR))
                    {
                        tx.Add(newId, datom.A, datom.Prefix.ValueTag, datom.ValueSpan);
                        continue;
                    }
                    writer.Reset();
                    var newTuple = (newR, s, str);
                    Tuple3_Ref_UShort_Utf8I_Serializer.Write(newTuple, writer);
                    tx.Add(newId, datom.A, datom.Prefix.ValueTag, writer.GetWrittenSpan());
                }
                // Otherwise just remap the E value
                else
                {
                    tx.Add(newId, datom.A, datom.Prefix.ValueTag, datom.ValueSpan);
                }
            }
        }

        var loadoutId = CollectionGroup.Load(basisDb, collectionId).AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        var nextPriority = GetNextPriority(loadoutId, _connection.Db);
        var oldPriorities = LoadoutItemGroupPriority.FindByLoadout(basisDb, loadoutId)
            .Where(priority =>
            {
                var item = priority.Target.AsLoadoutItem();
                return item.HasParent() && item.ParentId.Value == collectionId.Value;
            })
            .OrderBy(x => x.Priority)
            .ToArray();

        for (uint i = 0; i < oldPriorities.Length; i++)
        {
            var oldPriority = oldPriorities[i];
            _ = new LoadoutItemGroupPriority.New(tx)
            {
                LoadoutId = loadoutId,
                Priority = ConflictPriority.From(nextPriority.Value + i),
                TargetId = remappedIds[oldPriority.TargetId],
            };
        }

        tx.Add(new RebalancePrioritiesTxFunc(loadoutId, toSkip: []));

        var result = await tx.Commit();
        return CollectionGroup.Load(result.Db, result[remappedIds[collectionId]]);
    }

    public async ValueTask ReplaceItems(LoadoutId loadoutId, LoadoutItemGroupId[] groupsToRemove, LibraryItem.ReadOnly libraryItemToInstall)
    {
        var db = _connection.Db;
        var groups = groupsToRemove.Distinct()
            .Select(id => LoadoutItemGroup.Load(db, id))
            .Where(group => group.IsValid())
            .ToArray();
        if (groups.Length == 0) return;

        var nextPriority = GetNextPriority(loadoutId, db).Value;
        using var tx = _connection.BeginTransaction();
        foreach (var group in groups)
        {
            var item = group.AsLoadoutItem();
            var priorities = LoadoutItemGroupPriority.FindByTarget(db, group.LoadoutItemGroupId).ToArray();
            tx.Delete(group.Id, recursive: true);
            foreach (var priority in priorities)
                tx.Delete(priority.Id, recursive: false);

            var parent = item.HasParent()
                ? Optional<LoadoutItemGroupId>.Create(item.Parent.LoadoutItemGroupId)
                : Optional<LoadoutItemGroupId>.None;
            var replacement = await InstallItem(libraryItemToInstall, loadoutId, inputTx: tx,
                parent: parent, useDefaultCollection: false, addPriority: false);

            // Preserve the item's own setting; a disabled collection must continue
            // to control its children through inheritance rather than disabling them individually.
            if (item.IsDisabled)
                tx.Add(replacement.GroupTxId, LoadoutItem.Disabled, Null.Instance);

            // Keep conflict order and create a distinct record for every replacement.
            // AddPriorityTxFunc is intended for a single install in a transaction.
            _ = new LoadoutItemGroupPriority.New(tx)
            {
                LoadoutId = loadoutId,
                TargetId = replacement.GroupTxId,
                Priority = priorities.Length > 0 ? priorities[0].Priority : ConflictPriority.From(nextPriority++),
            };
        }

        await tx.Commit();
    }

    public async ValueTask ResolveFileConflicts(LoadoutItemGroupPriorityId[] winnerIds, LoadoutItemGroupPriorityId loserId)
    {
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();

        tx.Add(new ResolveFileConflictsTxFunc(
            loadoutId: LoadoutItemGroupPriority.Load(db,winnerIds[0]).LoadoutId,
            winnerIds: winnerIds,
            loserId: loserId
        ));

        await tx.Commit();
    }

    public async ValueTask ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId collectionId)
    {
        var items = _connection.Query<EntityId>($"SELECT Id FROM MDB_NexusCollectionItemLoadoutGroup(Db => {_connection}) WHERE Parent = {collectionId.Value}").ToList();

        var rulesQuery = _connection.Query<(EntityId SourceId, EntityId OtherId, int RuleType)>(
            $"""
              SELECT Source, Other, RuleType FROM loadouts.CollectionRulesOnItems({_connection})
              WHERE Parent = {collectionId.Value};
              """
        );

        var rules = rulesQuery
            .GroupBy(tuple => tuple.SourceId, tuple => (tuple.OtherId, tuple.RuleType))
            .ToFrozenDictionary(group => group.Key, group => group.ToArray());

        var sortedItems = new Sorter().Sort(
            items: items,
            idSelector: static x => x,
            ruleFn: id =>
            {
                if (!rules.TryGetValue(id, out var itemRules)) return [];
                return itemRules.Select(ISortRule<EntityId, EntityId> (rule) => (CollectionDownloadRuleType)rule.RuleType switch
                {
                    CollectionDownloadRuleType.Before => new Before<EntityId, EntityId>() { Other = rule.OtherId },
                    CollectionDownloadRuleType.After => new After<EntityId, EntityId>() { Other = rule.OtherId },
                    _ => throw new UnreachableException(),
                }).ToArray();
            }
        ).ToImmutableArray();

        using var tx = _connection.BeginTransaction();

        var loadoutId = LoadoutItem.Load(_connection.Db, collectionId).LoadoutId;
        tx.Add(new ApplyCollectionRulesTxFunc(loadoutId, sortedItems));
        await tx.Commit();
    }

    public async ValueTask LoseAllFileConflicts(LoadoutItemGroupPriorityId[] loserIds)
    {
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();

        tx.Add(new ResolveFileConflictsTxFunc(
            loadoutId: LoadoutItemGroupPriority.Load(db, loserIds[0]).LoadoutId,
            winnerIds: loserIds,
            loserId: Optional<LoadoutItemGroupPriorityId>.None
        ));

        await tx.Commit();
    }

    public ValueTask WinAllFileConflicts(LoadoutItemGroupPriorityId[] winnerIds)
    {
        var db = _connection.Db;
        var loser = LoadoutItemGroupPriority
            .FindByLoadout(db, LoadoutItemGroupPriority.Load(db, winnerIds[0]).LoadoutId)
            .OrderByDescending(static model => model.Priority)
            .First();

        return ResolveFileConflicts(winnerIds: winnerIds, loserId: loser.LoadoutItemGroupPriorityId);
    }
}
