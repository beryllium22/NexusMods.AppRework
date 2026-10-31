using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CliWrap;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Collections.Json;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NSubstitute;
using Xunit.Abstractions;

namespace NexusMods.Collections.Tests;

public class CollectionRecoveryTests(ITestOutputHelper output)
    : ACyberpunkIsolatedGameTest<CollectionRecoveryTests>(output)
{
    private static readonly byte[] Payload = "offline mod payload"u8.ToArray();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IProcessRunner, NoExternalProcesses>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedEntriesAreReportedAndRetryKeepsPreviouslyInstalledMods(bool optional)
    {
        var loadout = await CreateLoadout();
        var (source, revision) = await CreateSource(optional, rawSecond: false);
        var attempt = () => Run(source, revision, loadout, FileStore);
        await attempt.Should().ThrowAsync<AggregateException>()
            .WithMessage(optional ? "*Optional mods failed*" : "*Required mods are still missing*");

        var group = CollectionDownloader.GetCollectionGroup(revision, loadout, Connection.Db).Value;
        group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().Be(!optional,
            "optional failures must not disable an otherwise complete required collection");
        var originalMod = group.AsCollectionGroup().AsLoadoutItemGroup().Children.Single();
        originalMod.Name.Should().Be("First mod");

        // Repair the fixture's missing bundle and retry the same revision.
        var archive = source.AsLibraryFile().ToLibraryArchive();
        var payload = archive.Children.Single(file => file.Path.ToString().StartsWith("bundled/first/", StringComparison.Ordinal)).AsLibraryFile();
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LibraryArchiveFileEntry.New(tx, out var id)
            {
                ParentId = archive, Path = "bundled/second/mods/Second/info.json",
                LibraryFile = new LibraryFile.New(tx, id)
                {
                    Hash = payload.Hash, Size = payload.Size, FileName = "info.json",
                    LibraryItem = new LibraryItem.New(tx, id) { Name = "Repaired bundle" },
                },
            };
            await tx.Commit();
        }

        var completed = await Run(NexusModsCollectionLibraryFile.Load(Connection.Db, source.Id), revision, loadout, FileStore);
        completed.Id.Should().Be(group.Id);
        completed.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeFalse();
        var mods = completed.AsCollectionGroup().AsLoadoutItemGroup().Children.ToArray();
        mods.Should().HaveCount(2);
        mods.Select(mod => mod.Id).Should().Contain(originalMod.Id, "retry must reuse successful children instead of reinstalling them");
        CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Required | CollectionDownloader.ItemType.Optional)
            .All(item => CollectionDownloader.GetStatus(item, completed.AsCollectionGroup(), Connection.Db).IsInstalled(out _)).Should().BeTrue();
    }

    [Fact]
    public async Task CancellingTheParentCancelsItsActiveChildAndRetryResumesTheCollection()
    {
        var loadout = await CreateLoadout();
        var (source, revision) = await CreateSource(optional: false, rawSecond: true);
        var store = Substitute.For<IFileStore>();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockReading = true;
        store.GetFileStream(Payload.AsSpan().xxHash3(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (!blockReading) return new MemoryStream(Payload, writable: false);
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            return Stream.Null;
        });
        using var cancellation = new CancellationTokenSource();
        var install = Run(source, revision, loadout, store, cancellation.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var action = () => install.WaitAsync(TimeSpan.FromSeconds(10));
        await action.Should().ThrowAsync<OperationCanceledException>();

        var partial = CollectionDownloader.GetCollectionGroup(revision, loadout, Connection.Db).Value;
        partial.AsCollectionGroup().AsLoadoutItemGroup().Children.Should().ContainSingle();
        partial.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeTrue();
        var firstId = partial.AsCollectionGroup().AsLoadoutItemGroup().Children.Single().Id;
        blockReading = false;

        var completed = await Run(source, revision, loadout, store);
        completed.AsCollectionGroup().AsLoadoutItemGroup().Children.Should().HaveCount(2);
        completed.AsCollectionGroup().AsLoadoutItemGroup().Children.Select(child => child.Id).Should().Contain(firstId);
        completed.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeFalse();
    }

    private Task<NexusCollectionLoadoutGroup.ReadOnly> Run(NexusModsCollectionLibraryFile.ReadOnly source,
        CollectionRevisionMetadata.ReadOnly revision, LoadoutId loadout, IFileStore store, CancellationToken cancellationToken = default)
    {
        revision = CollectionRevisionMetadata.Load(Connection.Db, revision.Id);
        var job = new InstallCollectionJob
        {
            Logger = Logger, SourceCollection = source, RevisionMetadata = revision, Items = revision.Downloads.ToArray(),
            Group = Optional<NexusCollectionLoadoutGroup.ReadOnly>.None, TargetLoadout = loadout,
            Connection = Connection, ServiceProvider = ServiceProvider, FileStore = store,
            LibraryService = LibraryService, LoadoutManager = LoadoutManager, NexusModsLibrary = NexusModsLibrary,
        };
        var context = Substitute.For<IJobContext<InstallCollectionJob>>();
        context.CancellationToken.Returns(cancellationToken);
        return job.StartAsync(context).AsTask();
    }

    private async Task<(NexusModsCollectionLibraryFile.ReadOnly, CollectionRevisionMetadata.ReadOnly)> CreateSource(bool optional, bool rawSecond)
    {
        using var payloadStream = new MemoryStream(Payload, writable: false);
        var md5 = await Md5Hasher.HashAsync(payloadStream);
        var domain = GameDomain.From("cyberpunk2077");
        var root = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Recovery collection", DomainName = domain },
            Mods =
            [
                new Mod { Name = "First mod", DomainName = domain, Source = new ModSource { Type = ModSourceType.Bundle, FileExpression = "first" } },
                new Mod
                {
                    Name = "Second mod", DomainName = domain, Optional = optional,
                    Source = new ModSource { Type = rawSecond ? ModSourceType.Direct : ModSourceType.Bundle, FileExpression = "second" },
                    Hashes = rawSecond ? [new PathAndHash { Path = "mods/Second/info.json", MD5 = md5 }] : [],
                },
            ],
        };
        var json = JsonSerializer.Serialize(root, ServiceProvider.GetRequiredService<JsonSerializerOptions>());
        await using var local = TemporaryFileManager.CreateFile();
        await using (var stream = local.Path.Create())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await using (var entry = zip.CreateEntry("collection.json").Open())
                await entry.WriteAsync(Encoding.UTF8.GetBytes(json));
            await using (var entry = zip.CreateEntry("bundled/first/mods/First/info.json").Open())
                await entry.WriteAsync(Payload);
        }
        var archive = await RegisterLocalArchive(local.Path);
        using var tx = Connection.BeginTransaction();
        var slug = CollectionSlug.From("offline-recovery");
        var author = new User.New(tx) { Name = "Offline author", NexusId = 1 };
        var metadata = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(42), Slug = slug, Name = "Recovery collection",
            GameId = Cyberpunk2077Game.NexusModsGameId.Value, AuthorId = author.Id,
        };
        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(42), RevisionNumber = RevisionNumber.From(1), CollectionId = metadata.Id,
        };
        tx.Add(archive.Id, NexusModsCollectionLibraryFile.CollectionSlug, slug);
        tx.Add(archive.Id, NexusModsCollectionLibraryFile.CollectionRevisionNumber, RevisionNumber.From(1));
        for (var index = 0; index < 2; index++)
        {
            var download = new CollectionDownload.New(tx)
            {
                Name = root.Mods[index].Name, CollectionRevisionId = revision.Id, ArrayIndex = index, IsOptional = index == 1 && optional,
            };
            if (index == 1 && rawSecond)
            {
                _ = new CollectionDownloadExternal.New(tx, download.Id)
                {
                    CollectionDownload = download, Md5 = md5, Size = Size.FromLong(Payload.Length), Uri = new Uri("https://example.invalid/offline-file"),
                };
                _ = new LibraryFile.New(tx, out var fileId)
                {
                    Hash = Payload.AsSpan().xxHash3(), Md5 = md5, Size = Size.FromLong(Payload.Length), FileName = "info.json",
                    LibraryItem = new LibraryItem.New(tx, fileId) { Name = "Raw second mod" },
                };
            }
            else
            {
                _ = new CollectionDownloadBundled.New(tx, download.Id)
                {
                    CollectionDownload = download, BundledPath = index == 0 ? "first" : "second",
                };
            }
        }
        var result = await tx.Commit();
        return (NexusModsCollectionLibraryFile.Load(result.Db, archive.Id), result.Remap(revision));
    }

    private sealed class NoExternalProcesses : IProcessRunner
    {
        public void Run(Command command, bool logOutput = false) => throw new NotSupportedException();
        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException($"{command.TargetFilePath} is unavailable in this test.");
        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
