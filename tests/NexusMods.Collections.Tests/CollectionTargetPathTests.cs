using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Collections.Json;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NSubstitute;
using Xunit.Abstractions;

namespace NexusMods.Collections.Tests;

/// <summary>
/// Uses local collection metadata and in-memory file contents to exercise the public install
/// and copy operations. No Nexus account, download, or installed game is required.
/// </summary>
public class CollectionTargetPathTests(ITestOutputHelper helper)
    : ACyberpunkIsolatedGameTest<CollectionTargetPathTests>(helper)
{
    private static readonly GamePath TargetPath = new(LocationId.Game, "mods/CollectionTest/info.json");
    private static readonly byte[] Payload = "{\"name\":\"CollectionTest\"}"u8.ToArray();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IProcessRunner, NoExternalProcesses>();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task InstalledAndCopiedCollectionFilesRemainInTheirContainingLoadout(bool bundled, bool rawFile)
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout, "Offline collection");
        var (download, sourceCollection, mod, fileStore) = await CreateSource(bundled, rawFile);

        // A standalone mod legitimately has no ParentId. It must not prevent copying a
        // collection merely because both groups participate in conflict priorities.
        var standalone = await LoadoutManager.InstallItemWrapper(loadout, tx =>
            Task.FromResult(AddEmptyGroup(tx, loadout, "Standalone mod")));
        standalone.IsValid().Should().BeTrue("InstallItemWrapper must return the committed group, not its temporary ID");

        var otherLoadout = await CreateLoadout();
        var unrelated = await LoadoutManager.InstallItemWrapper(otherLoadout, tx =>
            Task.FromResult(AddEmptyGroup(tx, otherLoadout, "Mod in another loadout")));
        var unrelatedPriority = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, otherLoadout)
            .Single(priority => priority.Target.Id == unrelated.Id).Priority;

        // Installing into a loadout and copying it must not deploy over existing user files.
        var diskPath = GameInstallation.Locations.ToAbsolutePath(TargetPath);
        diskPath.Parent.CreateDirectory();
        const string userContent = "Existing user content must survive until an explicit Apply";
        await diskPath.WriteAllTextAsync(userContent);

        var job = new InstallCollectionDownloadJob
        {
            Logger = Logger,
            Item = download,
            Group = collection,
            TargetLoadout = loadout,
            CollectionMod = mod,
            SourceCollection = sourceCollection,
            ServiceProvider = ServiceProvider,
            Connection = Connection,
            FileStore = fileStore,
            LibraryService = LibraryService,
            LoadoutManager = LoadoutManager,
        };

        var context = Substitute.For<IJobContext<InstallCollectionDownloadJob>>();
        context.CancellationToken.Returns(CancellationToken.None);
        var installed = await job.StartAsync(context);
        installed.IsValid().Should().BeTrue();
        installed.AsLoadoutItem().Name.Should().Be(mod.Source.LogicalFilename);

        var installedMetadata = NexusCollectionItemLoadoutGroup.Load(Connection.Db, installed.Id);
        installedMetadata.DownloadId.Should().Be(download.CollectionDownloadId);
        installedMetadata.IsRequired.Should().BeTrue();

        var installedFile = AssertFileBelongsToLoadout(installed, loadout);
        FindRedModGroups(loadout).Should().Contain(installed.Id,
            "the actual REDmod load-order query filters by the loadout stored inside TargetPath");

        var originalPriorities = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout)
            .ToDictionary(priority => priority.Target.Id, priority => priority.Priority);
        originalPriorities.Should().ContainKey(installed.Id);
        originalPriorities.Should().ContainKey(standalone.Id);

        var copy = await LoadoutManager.CloneCollection(collection);
        var copiedMod = copy.AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().Single();
        var copiedFile = AssertFileBelongsToLoadout(copiedMod, loadout);
        copiedFile.Id.Should().NotBe(installedFile.Id);
        FindRedModGroups(loadout).Should().Contain(copiedMod.Id);

        var prioritiesAfterClone = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout)
            .ToDictionary(priority => priority.Target.Id, priority => priority.Priority);
        prioritiesAfterClone.Should().HaveCount(originalPriorities.Count + 1,
            "copying the collection must add one priority for its copied mod");
        prioritiesAfterClone.Should().ContainKey(copiedMod.Id);
        prioritiesAfterClone[copiedMod.Id].Value.Should().BeGreaterThan(originalPriorities.Values.Max(priority => priority.Value),
            "the copied collection must retain its mod in the destination loadout's conflict order");
        foreach (var (targetId, priority) in originalPriorities)
        {
            prioritiesAfterClone[targetId].Should().Be(priority,
                "copying a collection must preserve the source and standalone mods' priorities");
        }
        LoadoutItemGroupPriority.FindByLoadout(Connection.Db, otherLoadout)
            .Single(priority => priority.Target.Id == unrelated.Id).Priority.Should().Be(unrelatedPriority,
                "copying a collection must not change another loadout's conflict order");

        var copiedLoadout = await LoadoutManager.CopyLoadout(Loadout.Load(Connection.Db, loadout.Id));
        var copiedFiles = LoadoutItem.FindByLoadout(copiedLoadout.Db, copiedLoadout)
            .OfTypeLoadoutItemWithTargetPath()
            .OfTypeLoadoutFile()
            .Where(file => file.AsLoadoutItemWithTargetPath().TargetPath.Item3 == TargetPath.Path)
            .ToArray();

        copiedFiles.Should().HaveCount(2, "the original collection and its copy each contain this file");
        foreach (var file in copiedFiles)
        {
            file.AsLoadoutItemWithTargetPath().TargetPath.Item1.Should().Be(copiedLoadout.Id);
            file.Hash.Should().Be(Payload.AsSpan().xxHash3());
            file.Size.Should().Be(Size.FromLong(Payload.Length));
        }

        AssertFileBelongsToLoadout(LoadoutItemGroup.Load(Connection.Db, installed.Id), loadout);
        (await diskPath.ReadAllTextAsync()).Should().Be(userContent);
    }

    private static LoadoutFile.ReadOnly AssertFileBelongsToLoadout(LoadoutItemGroup.ReadOnly group, LoadoutId loadoutId)
    {
        var file = group.Children.OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile().Single();
        var target = file.AsLoadoutItemWithTargetPath();
        target.TargetPath.Item1.Should().Be(loadoutId.Value);
        target.AsLoadoutItem().LoadoutId.Should().Be(loadoutId);
        ((GamePath)target.TargetPath).Should().Be(TargetPath);
        file.Hash.Should().Be(Payload.AsSpan().xxHash3());
        file.Size.Should().Be(Size.FromLong(Payload.Length));
        return file;
    }

    private EntityId[] FindRedModGroups(LoadoutId loadoutId)
        => Connection.Query<EntityId>($"SELECT ModGroupId FROM redmod.LoadoutRedModGroups({Connection.Db}, {loadoutId.Value}, {LocationId.Game.Value})").ToArray();

    private async Task<(CollectionDownload.ReadOnly, NexusModsCollectionLibraryFile.ReadOnly, Mod, IFileStore)> CreateSource(bool bundled, bool rawFile = false)
    {
        var sourceArchive = await CreateLibraryArchive(Connection, "offline-collection.zip");
        var payloadFile = bundled ? sourceArchive.AsLibraryFile()
            : rawFile ? await CreateRawFile()
            : (await CreateLibraryArchive(Connection, "offline-mod.zip")).AsLibraryFile();
        using var payloadStream = new MemoryStream(Payload, writable: false);
        var payloadMd5 = await Md5Hasher.HashAsync(payloadStream);
        var archiveMd5 = Md5Value.FromHex("1234567890abcdef1234567890abcdef");
        var payloadHash = Payload.AsSpan().xxHash3();
        var slug = CollectionSlug.From("offline-collection");
        var revisionNumber = RevisionNumber.From(1);

        using var tx = Connection.BeginTransaction();
        var author = new User.New(tx) { Name = "Offline author", NexusId = 1 };
        var metadata = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(1),
            Slug = slug,
            Name = "Offline collection",
            GameId = Cyberpunk2077Game.NexusModsGameId.Value,
            AuthorId = author.Id,
        };
        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(1),
            RevisionNumber = revisionNumber,
            CollectionId = metadata.Id,
        };
        var download = new CollectionDownload.New(tx)
        {
            Name = "Original download name",
            CollectionRevisionId = revision.Id,
            ArrayIndex = 0,
            IsOptional = false,
        };

        if (bundled)
        {
            _ = new CollectionDownloadBundled.New(tx, download.Id)
            {
                CollectionDownload = download,
                BundledPath = "offline",
            };
        }
        else
        {
            _ = new CollectionDownloadExternal.New(tx, download.Id)
            {
                CollectionDownload = download,
                Md5 = archiveMd5,
                Size = payloadFile.Size,
                Uri = new Uri("https://example.invalid/offline-mod.zip"),
            };
        }

        tx.Add(sourceArchive.Id, NexusModsCollectionLibraryFile.CollectionSlug, slug);
        tx.Add(sourceArchive.Id, NexusModsCollectionLibraryFile.CollectionRevisionNumber, revisionNumber);
        tx.Add(payloadFile.Id, LibraryFile.Md5, archiveMd5);
        var archivePath = bundled ? RelativePath.FromUnsanitizedInput("bundled/offline").Join(TargetPath.Path) : RelativePath.FromUnsanitizedInput("source/info.json");
        if (!rawFile)
        _ = new LibraryArchiveFileEntry.New(tx, out var fileId)
        {
            ParentId = payloadFile.Id,
            Path = archivePath,
            LibraryFile = new LibraryFile.New(tx, fileId)
            {
                FileName = archivePath.FileName,
                Hash = payloadHash,
                Size = Size.FromLong(Payload.Length),
                LibraryItem = new LibraryItem.New(tx, fileId) { Name = archivePath },
            },
        };

        var result = await tx.Commit();
        var fileStore = Substitute.For<IFileStore>();
        fileStore.GetFileStream(payloadHash, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Payload, writable: false)));

        var mod = new Mod
        {
            Name = "Offline mod",
            DomainName = GameDomain.From("cyberpunk2077"),
            Source = new ModSource
            {
                Type = bundled ? ModSourceType.Bundle : ModSourceType.Direct,
                LogicalFilename = "Installed collection mod",
                FileExpression = "offline",
            },
            Hashes = bundled ? [] : [new PathAndHash { Path = TargetPath.Path, MD5 = payloadMd5 }],
        };

        return (result.Remap(download), NexusModsCollectionLibraryFile.Load(result.Db, sourceArchive.Id), mod, fileStore);
    }

    private async Task<LibraryFile.ReadOnly> CreateRawFile()
    {
        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            Hash = Payload.AsSpan().xxHash3(), Size = Size.FromLong(Payload.Length), FileName = "info.json",
            LibraryItem = new LibraryItem.New(tx, id) { Name = "Standalone collection file" },
        };
        return (await tx.Commit()).Remap(file);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplicatedFilesRejectMissingHashesAndConflictingDestinationsWithoutCommitting(bool conflict)
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout, "Offline collection");
        var (download, source, original, store) = await CreateSource(bundled: false, rawFile: true);
        var invalid = new PathAndHash { Path = TargetPath.Path, MD5 = Md5Value.FromHex("ffffffffffffffffffffffffffffffff") };
        var mod = new Mod
        {
            Name = original.Name, DomainName = original.DomainName, Source = original.Source,
            Hashes = conflict ? [original.Hashes[0], invalid] : [invalid],
        };
        var job = new InstallCollectionDownloadJob
        {
            Logger = Logger, Item = download, Group = collection, TargetLoadout = loadout,
            CollectionMod = mod, SourceCollection = source, ServiceProvider = ServiceProvider,
            Connection = Connection, FileStore = store, LibraryService = LibraryService, LoadoutManager = LoadoutManager,
        };
        var context = Substitute.For<IJobContext<InstallCollectionDownloadJob>>();
        context.CancellationToken.Returns(CancellationToken.None);
        var action = async () => await job.StartAsync(context);
        await action.Should().ThrowAsync<InvalidDataException>();
        CollectionGroup.Load(Connection.Db, collection.Id).AsLoadoutItemGroup().Children.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellingReplicationStopsHashingAndDoesNotCommitAnIncompleteMod()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout, "Offline collection");
        var (download, source, mod, store) = await CreateSource(bundled: false, rawFile: true);
        using var cancellation = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GetFileStream(Payload.AsSpan().xxHash3(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                reading.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return Stream.Null;
            });
        var job = new InstallCollectionDownloadJob
        {
            Logger = Logger, Item = download, Group = collection, TargetLoadout = loadout,
            CollectionMod = mod, SourceCollection = source, ServiceProvider = ServiceProvider,
            Connection = Connection, FileStore = store, LibraryService = LibraryService, LoadoutManager = LoadoutManager,
        };
        var context = Substitute.For<IJobContext<InstallCollectionDownloadJob>>();
        context.CancellationToken.Returns(cancellation.Token);
        var install = job.StartAsync(context).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var action = () => install.WaitAsync(TimeSpan.FromSeconds(10));
        await action.Should().ThrowAsync<OperationCanceledException>();
        CollectionGroup.Load(Connection.Db, collection.Id).AsLoadoutItemGroup().Children.Should().BeEmpty();
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
