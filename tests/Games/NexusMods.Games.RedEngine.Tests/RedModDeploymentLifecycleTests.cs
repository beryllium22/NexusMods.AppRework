using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.DataModel;
using NexusMods.DataModel.Synchronizer;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Games.RedEngine.Cyberpunk2077.Models;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Threading;
using OneOf;
using Xunit.Abstractions;
using static NexusMods.Games.RedEngine.Constants;

namespace NexusMods.Games.RedEngine.Tests;

/// <summary>
/// Exercises actual file synchronization and persisted deployment state with an in-process
/// REDmod stand-in. No game, account, external process, or network access is required.
/// The Windows command path runs on both CI operating systems.
/// </summary>
public class RedModDeploymentLifecycleTests(ITestOutputHelper output)
    : ACyberpunkIsolatedGameTest<RedModDeploymentLifecycleTests>(output)
{
    private RecordingProcessRunner Processes => ServiceProvider.GetRequiredService<RecordingProcessRunner>();
    private RedModDeployTool DeployTool => ServiceProvider.GetServices<ITool>().OfType<RedModDeployTool>().Single();
    private RunCyberpunk2077Game LaunchTool => ServiceProvider.GetServices<ITool>().OfType<RunCyberpunk2077Game>().Single();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.AddSingleton<RecordingProcessRunner>();
        services.AddSingleton<IProcessRunner>(provider => provider.GetRequiredService<RecordingProcessRunner>());
        services.AddSingleton<InterceptingFileStore>();
        services.AddSingleton<IFileStore>(provider => provider.GetRequiredService<InterceptingFileStore>());

        services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(RedModDeployTool)));
        services.AddSingleton<ITool>(provider => new RedModDeployTool(
            provider.GetRequiredService<GameToolRunner>(),
            provider.GetRequiredService<TemporaryFileManager>(),
            provider.GetRequiredService<ILogger<RedModDeployTool>>(),
            provider.GetRequiredService<RedModSortOrderVariety>(),
            new OSInformation(OSPlatform.Windows)));
        return services;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDlcDiagnosticUsesEnabledPendingManifests(bool disabledCollection)
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout, "Test collection");
        await AddRedMod(loadout, "PendingMod", collection.AsLoadoutItemGroup().LoadoutItemGroupId);
        var beforeDisable = loadout.Rebase();
        if (disabledCollection) await DisableItem(collection.Id);

        GameInstallation.Locations.ToAbsolutePath(RedModInstallFolder).DirectoryExists().Should().BeFalse();
        var current = loadout.Rebase();
        var emitter = new MissingRedModEmitter();
        var diagnostics = await emitter.Diagnose(current, CancellationToken.None).ToListAsync();
        diagnostics.Should().HaveCount(disabledCollection ? 0 : 1);

        // Historical snapshots must not silently use the latest connection's enabled state.
        DeployTool.GetModOrder(beforeDisable).Should().Equal("PendingMod");
        if (disabledCollection) DeployTool.GetModOrder(current).Should().BeEmpty();
    }

    [Fact]
    public async Task UnrelatedPhysicalFolderDoesNotRequireRedmod()
    {
        var loadout = await CreateLoadout();
        var unrelated = GameInstallation.Locations.ToAbsolutePath(RedModInstallFolder).Combine("UserNotes/readme.txt");
        unrelated.Parent.CreateDirectory();
        await unrelated.WriteAllTextAsync("User notes, not a REDmod manifest");

        var diagnostics = await new MissingRedModEmitter().Diagnose(loadout, CancellationToken.None).ToListAsync();
        diagnostics.Should().BeEmpty();
        Synchronizer.HasPendingDeployment(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task MissingToolFailsBeforeWritingModFilesOrRecordingApply()
    {
        var loadout = await CreateLoadout();
        var lastApplied = loadout.LastAppliedDateTime.Value;
        await AddRedMod(loadout, "PendingMod");
        var existingFile = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "user-file.txt"));
        await existingFile.WriteAllTextAsync("Preserve this existing user file");

        Func<Task> apply = () => SynchronizerService.Synchronize(loadout);
        await apply.Should().ThrowAsync<FileNotFoundException>().WithMessage("*REDmod DLC*");

        GameInstallation.Locations.ToAbsolutePath(RedModInstallFolder).DirectoryExists().Should().BeFalse();
        (await existingFile.ReadAllTextAsync()).Should().Be("Preserve this existing user file");
        loadout.Rebase().LastAppliedDateTime.Value.Should().Be(lastApplied);
        Processes.Deployments.Should().BeEmpty();
        (await GetStatus(loadout)).Should().Be(LoadoutSynchronizerState.NeedsSync);
    }

    [Fact]
    public async Task FailedDeploymentRetainsDiskBaselineAndCanRetryWithANewSynchronizer()
    {
        await InstallTool();
        var loadout = await CreateLoadout();
        var lastApplied = loadout.LastAppliedDateTime.Value;
        await AddRedMod(loadout, "RetryMod");
        Processes.DeployExitCode = 7;

        Func<Task> apply = () => SynchronizerService.Synchronize(loadout);
        await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exit code 7*");

        var failed = Loadout.Load(Connection.Db, loadout.Id);
        failed.Contains(RedModDeploymentState.Pending).Should().BeTrue();
        failed.LastAppliedDateTime.Value.Should().Be(lastApplied);
        failed.Installation.LastSyncedLoadout.Id.Should().Be(loadout.Id,
            "the file phase really did apply, and its baseline must not be rolled back");
        GameInstallation.Locations.ToAbsolutePath(RedModInstallFolder).Combine("RetryMod/info.json").FileExists.Should().BeTrue();
        (await GetStatus(loadout)).Should().Be(LoadoutSynchronizerState.NeedsSync);

        var reloadedSynchronizer = new Cyberpunk2077Synchronizer(ServiceProvider);
        reloadedSynchronizer.HasPendingDeployment(failed).Should().BeTrue("completion is stored in the database, not in a transient service field");
        Func<Task> launch = () => LaunchTool.Execute(failed, CancellationToken.None, []);
        await launch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Apply*this loadout*");
        Processes.LaunchArguments.Should().BeEmpty();

        Processes.DeployExitCode = 0;
        await reloadedSynchronizer.Synchronize(failed, job: null);
        var completed = loadout.Rebase();
        completed.Contains(RedModDeploymentState.Pending).Should().BeFalse();
        reloadedSynchronizer.HasPendingDeployment(completed).Should().BeFalse();
        Processes.Deployments.Should().HaveCount(2);
        (await GetStatus(loadout, LoadoutSynchronizerState.Current)).Should().Be(LoadoutSynchronizerState.Current);
        await LaunchTool.Execute(completed, CancellationToken.None, []);
        Processes.LaunchArguments.Should().ContainSingle().Which.Should().Contain("-modded");
    }

    [Fact]
    public async Task CancellingApplyWaitsForDeploymentToStopAndLeavesARetryableState()
    {
        await InstallTool();
        var loadout = await CreateLoadout();
        var lastApplied = loadout.LastAppliedDateTime.Value;
        await AddRedMod(loadout, "CancelledMod");
        Processes.WaitForCancellation = true;

        var apply = SynchronizerService.Synchronize(loadout);
        await Processes.DeploymentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var monitor = ServiceProvider.GetRequiredService<IJobMonitor>();
        var job = monitor.Jobs.Single(job => job.Status == JobStatus.Running &&
            job.Definition is SynchronizeLoadoutJob sync && sync.LoadoutId == loadout.LoadoutId);
        monitor.Cancel(job.Id);

        Func<Task> waitForApply = () => apply;
        await waitForApply.Should().ThrowAsync<OperationCanceledException>();
        Processes.DeploymentStopped.Task.IsCompleted.Should().BeTrue();
        loadout.Rebase().Contains(RedModDeploymentState.Pending).Should().BeTrue();
        loadout.Rebase().LastAppliedDateTime.Value.Should().Be(lastApplied);
        (await GetStatus(loadout)).Should().Be(LoadoutSynchronizerState.NeedsSync);

        Processes.WaitForCancellation = false;
        await SynchronizerService.Synchronize(loadout);
        Synchronizer.HasPendingDeployment(loadout.Rebase()).Should().BeFalse();
    }

    [Fact]
    public async Task DisablingAllRedmodsNeedsNoToolAndLaunchesWithoutTheModdedFlag()
    {
        await InstallTool();
        var loadout = await CreateLoadout();
        var group = await AddRedMod(loadout, "DisabledMod");
        var generatedPath = new GamePath(LocationId.Game, "r6/cache/modded/test.redscripts");
        var generatedFile = GameInstallation.Locations.ToAbsolutePath(generatedPath);
        Processes.DuringDeployment = async () =>
        {
            generatedFile.Parent.CreateDirectory();
            await generatedFile.WriteAllTextAsync("Generated REDmod cache");
        };
        await SynchronizerService.Synchronize(loadout);
        Processes.Deployments.Should().ContainSingle();
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Should().Contain(file => file.TargetPath == generatedPath, "successful deployment output must be ingested before recording completion");

        // Generated output is ingested as ordinary override files. Preserve later user edits;
        // disabling the sources must not blindly delete files whose origin is no longer known.
        await generatedFile.WriteAllTextAsync("User-edited cached output");

        await DisableItem(group.Id);
        GameInstallation.Locations.ToAbsolutePath(RedModPath).Delete();
        await SynchronizerService.Synchronize(loadout);

        Processes.Deployments.Should().ContainSingle("no enabled REDmod needs rebuilding");
        Synchronizer.HasPendingDeployment(loadout.Rebase()).Should().BeFalse();
        GameInstallation.Locations.ToAbsolutePath(RedModInstallFolder).Combine("DisabledMod/info.json").FileExists.Should().BeFalse();
        (await generatedFile.ReadAllTextAsync()).Should().Be("User-edited cached output");
        await LaunchTool.Execute(loadout.Rebase(), CancellationToken.None, ["-skipStartScreen"]);
        Processes.LaunchArguments.Should().ContainSingle().Which.Should().Contain("-skipStartScreen").And.NotContain("-modded");
    }

    [Fact]
    public async Task FailedDeploymentCanSwitchToCleanLoadoutWithoutLosingExternalChanges()
    {
        await InstallTool();
        var failedLoadout = await CreateLoadout();
        var lastApplied = failedLoadout.LastAppliedDateTime.Value;
        await AddRedMod(failedLoadout, "FailedMod");
        var cleanLoadout = await CreateLoadout();
        Processes.DeployExitCode = 7;
        Func<Task> apply = () => SynchronizerService.Synchronize(failedLoadout);
        await apply.Should().ThrowAsync<InvalidOperationException>();

        var externalPath = new GamePath(LocationId.Game, "user-created.txt");
        var externalFile = GameInstallation.Locations.ToAbsolutePath(externalPath);
        await externalFile.WriteAllTextAsync("Preserve across loadout changes");
        GameInstallation.Locations.ToAbsolutePath(RedModPath).Delete();
        await SynchronizerService.Synchronize(cleanLoadout);

        Processes.Deployments.Should().ContainSingle("switching away reconciles files without retrying the failed REDmod tool");
        failedLoadout.Rebase().Contains(RedModDeploymentState.Pending).Should().BeTrue();
        failedLoadout.Rebase().LastAppliedDateTime.Value.Should().Be(lastApplied);
        LoadoutItem.FindByLoadout(Connection.Db, failedLoadout).OfTypeLoadoutItemWithTargetPath()
            .Should().Contain(file => file.TargetPath == externalPath, "deactivation must ingest user files before resetting the game folder");
        cleanLoadout.Rebase().Installation.LastSyncedLoadout.Id.Should().Be(cleanLoadout.Id);
        Synchronizer.HasPendingDeployment(cleanLoadout.Rebase()).Should().BeFalse();
        (await GetStatus(cleanLoadout, LoadoutSynchronizerState.Current)).Should().Be(LoadoutSynchronizerState.Current);
        await LaunchTool.Execute(cleanLoadout.Rebase(), CancellationToken.None, []);
        Processes.LaunchArguments.Should().ContainSingle().Which.Should().NotContain("-modded");
    }

    [Fact]
    public async Task LoadOrderOnlyChangeRemainsUnappliedUntilSuccessfulDeployment()
    {
        await InstallTool();
        var loadout = await CreateLoadout();
        await AddRedMod(loadout, "FirstMod");
        await AddRedMod(loadout, "SecondMod");
        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();
        var sortOrder = await variety.GetOrCreateSortOrderFor(loadout, OneOf<LoadoutId, CollectionGroupId>.FromT0(loadout.LoadoutId));
        await variety.ReconcileSortOrder(sortOrder);
        await SynchronizerService.Synchronize(loadout);
        var before = DeployTool.GetModOrder(loadout.Rebase()).ToArray();

        await variety.MoveItemDelta(sortOrder, new SortItemKey<string>(before[0]), 1);
        var changed = loadout.Rebase();
        DeployTool.GetModOrder(changed).Should().Equal(before.Reverse());
        var disk = Synchronizer.GetDiskStateForGame(changed.Installation);
        Synchronizer.ShouldSynchronize(changed, disk, disk).Should().BeFalse("only the REDmod ordering changed");
        (await ServiceProvider.GetRequiredService<SynchronizerService>().GetShouldSynchronize(loadout)).Should().BeTrue();
        (await GetStatus(loadout)).Should().Be(LoadoutSynchronizerState.NeedsSync);

        await SynchronizerService.Synchronize(loadout);
        Processes.Deployments.Should().HaveCount(2);
        Processes.Deployments[1].Should().Be(string.Join("\r\n", before.Reverse()));
        (await GetStatus(loadout, LoadoutSynchronizerState.Current)).Should().Be(LoadoutSynchronizerState.Current);
    }

    [Fact]
    public async Task InputsChangedDuringDeploymentCannotBeRecordedAsComplete()
    {
        await InstallTool();
        var loadout = await CreateLoadout();
        await AddRedMod(loadout, "OriginalMod");
        Processes.DuringDeployment = async () => { await AddRedMod(loadout, "AddedDuringDeployment"); };

        Func<Task> apply = () => SynchronizerService.Synchronize(loadout);
        await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed during deployment*");
        loadout.Rebase().Contains(RedModDeploymentState.Pending).Should().BeTrue();
        (await GetStatus(loadout)).Should().Be(LoadoutSynchronizerState.NeedsSync);

        Processes.DuringDeployment = null;
        await SynchronizerService.Synchronize(loadout);
        Synchronizer.HasPendingDeployment(loadout.Rebase()).Should().BeFalse();
    }

    [Fact]
    public async Task FileEditedDuringCopyCannotBeRecordedAsDeployedBeforeRedmodReadsIt()
    {
        await InstallTool();
        var loadout = await CreateLoadout();
        await AddRedMod(loadout, "ChangingMod");
        var archivePath = new GamePath(LocationId.Game, "mods/ChangingMod/archives/content.archive");
        var storedFile = LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .Single(file => file.AsLoadoutItemWithTargetPath().TargetPath == archivePath);
        var copyStore = ServiceProvider.GetRequiredService<InterceptingFileStore>();
        copyStore.AfterExtract = async () =>
        {
            copyStore.AfterExtract = null;
            var changedBytes = Encoding.UTF8.GetBytes("New mod version selected during copying");
            var changedHash = changedBytes.xxHash3();
            var changedSize = Size.FromLong(changedBytes.Length);
            await FileStore.BackupFiles([new ArchivedFileEntry(
                new MemoryStreamFactory(archivePath.FileName, new MemoryStream(changedBytes)), changedHash, changedSize)]);
            using var tx = Connection.BeginTransaction();
            tx.Add(storedFile.Id, LoadoutFile.Hash, changedHash);
            tx.Add(storedFile.Id, LoadoutFile.Size, changedSize);
            await tx.Commit();
        };

        Func<Task> apply = () => SynchronizerService.Synchronize(loadout);
        await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed while Apply was copying*");
        Processes.Deployments.Should().BeEmpty("REDmod must not run against bytes different from its input snapshot");
        loadout.Rebase().Contains(RedModDeploymentState.Pending).Should().BeTrue();

        await SynchronizerService.Synchronize(loadout);
        (await GameInstallation.Locations.ToAbsolutePath(archivePath).ReadAllTextAsync()).Should().Be("New mod version selected during copying");
        Processes.Deployments.Should().ContainSingle();
        Synchronizer.HasPendingDeployment(loadout.Rebase()).Should().BeFalse();
    }

    [Fact]
    public async Task DisablingLastModDuringCopyCannotLeaveItsFilesRecordedAsApplied()
    {
        await InstallTool();
        var loadout = await CreateLoadout();
        var lastApplied = loadout.LastAppliedDateTime.Value;
        var group = await AddRedMod(loadout, "DisabledDuringCopy");
        var copyStore = ServiceProvider.GetRequiredService<InterceptingFileStore>();
        copyStore.AfterExtract = async () =>
        {
            copyStore.AfterExtract = null;
            await DisableItem(group.Id);
        };

        Func<Task> apply = () => SynchronizerService.Synchronize(loadout);
        await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed while Apply was copying*");
        Processes.Deployments.Should().BeEmpty();
        loadout.Rebase().LastAppliedDateTime.Value.Should().Be(lastApplied);
        loadout.Rebase().Contains(RedModDeploymentState.Pending).Should().BeTrue();

        await SynchronizerService.Synchronize(loadout);
        GameInstallation.Locations.ToAbsolutePath(RedModInstallFolder).Combine("DisabledDuringCopy/info.json").FileExists.Should().BeFalse();
        Processes.Deployments.Should().BeEmpty();
        Synchronizer.HasPendingDeployment(loadout.Rebase()).Should().BeFalse();
    }

    private async Task InstallTool()
    {
        var executable = GameInstallation.Locations.ToAbsolutePath(RedModPath);
        executable.Parent.CreateDirectory();
        await executable.WriteAllTextAsync("The fake process runner handles this executable.");
    }

    private async Task<LoadoutItemGroup.ReadOnly> AddRedMod(Loadout.ReadOnly loadout, string folder, LoadoutItemGroupId? parent = null)
    {
        using var tx = Connection.BeginTransaction();
        var groupId = AddEmptyGroup(tx, loadout, folder, parent);
        RelativePath[] paths =
        [
            RelativePath.FromUnsanitizedInput($"mods/{folder}/info.json"),
            RelativePath.FromUnsanitizedInput($"mods/{folder}/archives/content.archive"),
        ];
        var backups = new List<ArchivedFileEntry>();
        foreach (var path in paths)
        {
            var content = path.ToString();
            AddFile(tx, loadout, groupId, new GamePath(LocationId.Game, path), content, out var hash, out var size);
            if (!await FileStore.HaveFile(hash))
                backups.Add(new ArchivedFileEntry(new MemoryStreamFactory(path, new MemoryStream(Encoding.UTF8.GetBytes(content))), hash, size));
        }

        // These lifecycle tests need backed-up files, not an archive path. Keep the real
        // Nx store behind the extraction observer without requiring its concrete type here.
        if (backups.Count != 0) await FileStore.BackupFiles(backups);
        await tx.Commit();
        return LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemGroup()
            .Single(group => group.AsLoadoutItem().Name == folder);
    }

    private async Task<LoadoutSynchronizerState> GetStatus(LoadoutId loadoutId, LoadoutSynchronizerState expected = LoadoutSynchronizerState.NeedsSync)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await (await SynchronizerService.StatusForLoadout(loadoutId))
            .FirstAsync(status => status == expected)
            .ToTask(timeout.Token);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<string> Deployments { get; } = [];
        public List<string> LaunchArguments { get; } = [];
        public int DeployExitCode { get; set; }
        public bool WaitForCancellation { get; set; }
        public Func<Task>? DuringDeployment { get; set; }
        public TaskCompletionSource DeploymentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeploymentStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = Path.GetFileName(command.TargetFilePath);
            if (filename.Equals("Cyberpunk2077.exe", StringComparison.OrdinalIgnoreCase))
            {
                LaunchArguments.Add(command.Arguments);
                return new CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            }
            if (!filename.Equals("redMod.exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("External processes are unavailable in this test.", command.TargetFilePath);

            const string modlistPrefix = "-modlist=";
            var modlistIndex = command.Arguments.IndexOf(modlistPrefix, StringComparison.Ordinal);
            modlistIndex.Should().BeGreaterThanOrEqualTo(0);
            var modlist = command.Arguments[(modlistIndex + modlistPrefix.Length)..].Trim('"');
            Deployments.Add(await File.ReadAllTextAsync(modlist, cancellationToken));
            DeploymentStarted.TrySetResult();
            try
            {
                if (WaitForCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (DuringDeployment is not null)
                    await DuringDeployment();
                return new CommandResult(DeployExitCode, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            }
            finally
            {
                DeploymentStopped.TrySetResult();
            }
        }

        public void Run(Command command, bool logOutput = false)
            => throw new NotSupportedException("External processes are unavailable in this test.");

        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("External processes are unavailable in this test.");
    }

    private sealed class InterceptingFileStore(NxFileStore inner) : IFileStore
    {
        public Func<Task>? AfterExtract { get; set; }

        public async Task ExtractFiles(IEnumerable<(Hash Hash, AbsolutePath Dest)> files, CancellationToken token = default,
            Action<(int Current, int Max)>? progress = null)
        {
            await inner.ExtractFiles(files, token, progress);
            if (AfterExtract is not null) await AfterExtract();
        }

        public ValueTask<bool> HaveFile(Hash hash) => inner.HaveFile(hash);
        public Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default)
            => inner.BackupFiles(backups, deduplicate, token);
        public Task<Dictionary<Hash, byte[]>> ExtractFiles(IEnumerable<Hash> files, CancellationToken token = default)
            => inner.ExtractFiles(files, token);
        public Task<Stream> GetFileStream(Hash hash, CancellationToken token = default) => inner.GetFileStream(hash, token);
        public Task<byte[]> Load(Hash hash, CancellationToken token = default) => inner.Load(hash, token);
        public AsyncFriendlyReaderWriterLock.WriteLockDisposable WriteLock() => inner.WriteLock();
        public void ReloadCaches() => inner.ReloadCaches();
    }
}
