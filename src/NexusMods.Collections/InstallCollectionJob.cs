using System.Diagnostics;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Collections.Json;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Tracking;

namespace NexusMods.Collections;

using ModAndDownload = (Mod Mod, CollectionDownload.ReadOnly Download);

/// <summary>
/// Job for installing a collection.
/// </summary>
public class InstallCollectionJob : IJobDefinitionWithStart<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly>
{ 
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public required NexusModsCollectionLibraryFile.ReadOnly SourceCollection { get; init; }
    public required CollectionRevisionMetadata.ReadOnly RevisionMetadata { get; init; }
    public required CollectionDownload.ReadOnly[] Items { get; init; }
    public required Optional<NexusCollectionLoadoutGroup.ReadOnly> Group { get; init; }

    public required IServiceProvider ServiceProvider { get; init; }
    public required IFileStore FileStore { get; init; }
    public required ILibraryService LibraryService { get; init; }
    public required ILoadoutManager LoadoutManager { get; init; }
    public required IConnection Connection { get; init; }
    public required LoadoutId TargetLoadout { get; init; }
    public required NexusModsLibrary NexusModsLibrary { get; init; }
    public required ILogger Logger { get; init; }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

    /// <summary>
    /// Factory.
    /// </summary>
    public static IJobTask<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly> Create(
        IServiceProvider provider,
        LoadoutId target,
        NexusModsCollectionLibraryFile.ReadOnly source,
        CollectionRevisionMetadata.ReadOnly revisionMetadata,
        CollectionDownload.ReadOnly[] items)
    {
        var connection = provider.GetRequiredService<IConnection>();
        var group = CollectionDownloader.GetCollectionGroup(revisionMetadata, target, connection.Db);

        var monitor = provider.GetRequiredService<IJobMonitor>();

        var job = new InstallCollectionJob
        {
            Group = group,
            Items = items,
            TargetLoadout = target,
            SourceCollection = source,
            RevisionMetadata = revisionMetadata,
            ServiceProvider = provider,
            Connection = connection,
            FileStore = provider.GetRequiredService<IFileStore>(),
            LibraryService = provider.GetRequiredService<ILibraryService>(),
            LoadoutManager = provider.GetRequiredService<ILoadoutManager>(),
            NexusModsLibrary = provider.GetRequiredService<NexusModsLibrary>(),
            Logger = provider.GetRequiredService<ILogger<InstallCollectionJob>>(),
        };

        return monitor.Begin<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly>(job);
    }

    /// <summary>
    /// Installs the collection.
    /// </summary>
    public async ValueTask<NexusCollectionLoadoutGroup.ReadOnly> StartAsync(IJobContext<InstallCollectionJob> context)
    {
        Logger.LogInformation("Starting installation of `{CollectionName}/{RevisionNumber}`", RevisionMetadata.Collection.Name, RevisionMetadata.RevisionNumber);

        context.CancellationToken.ThrowIfCancellationRequested();
        // A retry may reuse this job definition; find the latest committed group instead of
        // relying on the snapshot supplied when the original attempt was created.
        var currentGroup = CollectionDownloader.GetCollectionGroup(RevisionMetadata, TargetLoadout, Connection.Db);
        var g = currentGroup.Convert(static x => x.AsCollectionGroup());
        var items = Items
            .Where(item => !CollectionDownloader.GetStatus(item, g, Connection.Db).IsInstalled(out _))
            .ToArray();

        var skipCount = Items.Length - items.Length;
        if (skipCount > 0) Logger.LogInformation("Skipping `{Count}` already installed items for `{CollectionName}/{RevisionNumber}`", skipCount, RevisionMetadata.Collection.Name, RevisionMetadata.RevisionNumber);

        var isFullyDownloaded = CollectionDownloader.IsFullyDownloaded(items, db: Connection.Db);
        if (!isFullyDownloaded) throw new InvalidOperationException("The collection hasn't fully been downloaded!");

        var sw = Stopwatch.StartNew();
        Events.CollectionsInstallationStarted(
            collectionId: RevisionMetadata.Collection.CollectionId.Value,
            revisionId: RevisionMetadata.RevisionId.Value,
            gameId: RevisionMetadata.Collection.GameId.Value,
            modCount: items.Length
        );

        var root = await NexusModsLibrary.ParseCollectionJsonFile(SourceCollection, context.CancellationToken);
        var modsAndDownloads = GatherDownloads(items, root);

        NexusCollectionLoadoutGroup.ReadOnly collectionGroup;
        if (currentGroup.HasValue)
        {
            collectionGroup = currentGroup.Value;
        }
        else
        {
            using var tx = Connection.BeginTransaction() ;
            var group = new NexusCollectionLoadoutGroup.New(tx, out var id)
            {
                CollectionId = RevisionMetadata.Collection,
                RevisionId = RevisionMetadata,
                LibraryFileId = SourceCollection,
                CollectionGroup = new CollectionGroup.New(tx, id)
                {
                    IsReadOnly = true,
                    LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(tx, id)
                        {
                            Name = RevisionMetadata.Collection.Name,
                            LoadoutId = TargetLoadout,
                            IsDisabled = true,
                        },
                    },
                },
            };

            var groupResult = await tx.Commit();
            collectionGroup = groupResult.Remap(group);
        }

        var loadout = Loadout.Load(Connection.Db, TargetLoadout);
        var game = (loadout.InstallationInstance.Game as IGame)!;
        var fallbackInstaller = FallbackCollectionDownloadInstaller.Create(ServiceProvider, loadout, game);

        var failures = new List<(CollectionDownload.ReadOnly Download, Exception Error)>();
        // Install in manifest order: later FOMODs may depend on files committed by earlier
        // entries, even before the collection is enabled or deployed.
        foreach (var modAndDownload in modsAndDownloads)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                Logger.LogDebug("Installing `{DownloadName}` (index={Index}) into `{CollectionName}/{RevisionNumber}`", modAndDownload.Mod.Name, modAndDownload.Download.ArrayIndex, RevisionMetadata.Collection.Name, RevisionMetadata.RevisionNumber);
                var child = InstallMod(modAndDownload, collectionGroup, fallbackInstaller, game.GetFallbackCollectionInstallDirectory(loadout.InstallationInstance));
                using var registration = context.CancellationToken.Register(() => child.Job.AsContext().Cancel());
                await child;
            }
            catch (OperationCanceledException)
            {
                // Cancellation must settle the child before the parent exits. Completed
                // children remain attached to the collection and are skipped on retry.
                throw;
            }
            catch (Exception exception)
            {
                failures.Add((modAndDownload.Download, exception));
                Logger.LogError(exception, "Failed to install {Requirement} collection item `{DownloadName}` (index={Index})", modAndDownload.Download.IsRequired ? "required" : "optional", modAndDownload.Mod.Name, modAndDownload.Download.ArrayIndex);
            }
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        var allRequiredItems = CollectionDownloader.GetItems(CollectionRevisionMetadata.Load(Connection.Db, RevisionMetadata.Id), CollectionDownloader.ItemType.Required);
        var missingRequiredItems = allRequiredItems.Where(item => !CollectionDownloader
            .GetStatus(item, collectionGroup.AsCollectionGroup(), db: Connection.Db)
            .IsInstalled(out _)).ToArray();
        var allRequiredItemsInstalled = missingRequiredItems.Length == 0;
        {
            await LoadoutManager.ApplyCollectionDownloadRules(collectionGroup);
            context.CancellationToken.ThrowIfCancellationRequested();

            using var tx = Connection.BeginTransaction();

            if (allRequiredItemsInstalled)
            {
                tx.Retract(collectionGroup.Id, LoadoutItem.Disabled, Null.Instance);
            }
            else
            {
                tx.Add(collectionGroup.Id, LoadoutItem.Disabled, Null.Instance);
            }

            var result = await tx.Commit();
            collectionGroup = NexusCollectionLoadoutGroup.Load(result.Db, collectionGroup.Id);
        }

        if (!allRequiredItemsInstalled || failures.Count > 0)
        {
            var requiredMessage = allRequiredItemsInstalled
                ? "All required mods were installed."
                : $"Required mods are still missing: {string.Join(", ", missingRequiredItems.Select(item => item.Name))}. The collection remains disabled.";
            var optionalFailures = failures.Where(failure => !failure.Download.IsRequired).Select(failure => failure.Download.Name).ToArray();
            var optionalMessage = optionalFailures.Length == 0 ? string.Empty
                : $" Optional mods failed: {string.Join(", ", optionalFailures)}.";
            throw new AggregateException($"Collection installation is incomplete. {requiredMessage}{optionalMessage} Retry to install the remaining mods; successful installs have been kept.",
                failures.Select(failure => failure.Error));
        }

        Events.CollectionsInstallationCompleted(
            collectionId: RevisionMetadata.Collection.CollectionId.Value,
            revisionId: RevisionMetadata.RevisionId.Value,
            gameId: RevisionMetadata.Collection.GameId.Value,
            modCount: items.Length,
            duration: sw);
        return collectionGroup;
    }

    private IJobTask<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly> InstallMod(
        ModAndDownload modAndDownload,
        NexusCollectionLoadoutGroup.ReadOnly collectionGroup,
        ILibraryItemInstaller? fallbackInstaller,
        Optional<GamePath> fallbackCollectionInstallDirectory)
    {
        var monitor = ServiceProvider.GetRequiredService<IJobMonitor>();

        var job = new InstallCollectionDownloadJob
        {
            Logger = ServiceProvider.GetRequiredService<ILogger<InstallCollectionJob>>(),
            Item = modAndDownload.Download,
            CollectionMod = modAndDownload.Mod,
            Group = collectionGroup.AsCollectionGroup(),
            TargetLoadout = TargetLoadout,
            SourceCollection = SourceCollection,

            ServiceProvider = ServiceProvider,
            Connection = Connection,
            FileStore = FileStore,
            LibraryService = LibraryService,
            LoadoutManager = LoadoutManager,

            FallbackInstaller = fallbackInstaller,
            FallbackCollectionInstallDirectory = fallbackCollectionInstallDirectory,
        };

        return monitor.Begin<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly>(job);
    }

    private static List<ModAndDownload> GatherDownloads(CollectionDownload.ReadOnly[] items, CollectionRoot root)
    {
        var map = items.ToDictionary(static download => download.ArrayIndex, static download => download);
        var list = new List<ModAndDownload>();

        foreach (var kv in map.OrderBy(pair => pair.Key))
        {
            var (index, download) = kv;
            if (index < 0 || index >= root.Mods.Length)
                throw new InvalidDataException($"Collection item '{download.Name}' refers to missing manifest entry {index}.");
            var mod = root.Mods[index];

            list.Add((mod, download));
        }

        return list;
    }
}
