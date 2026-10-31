using System.Diagnostics;
using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.RedEngine.Witcher3;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit.Abstractions;

namespace NexusMods.Games.RedEngine.Tests.Witcher3;

/// <summary>Real archive ingestion and file deployment with a fake game and process runner.</summary>
public class Witcher3IntegrationTests(ITestOutputHelper output)
    : ALibraryArchiveInstallerTests<Witcher3IntegrationTests, Witcher3Game>(output)
{
    private RecordingProcessRunner Processes => ServiceProvider.GetRequiredService<RecordingProcessRunner>();
    private RunWitcher3Game LaunchTool => ServiceProvider.GetServices<ITool>().OfType<RunWitcher3Game>().Single();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services)
            .AddRedEngineGames()
            .AddUniversalGameLocator<Witcher3Game>(new Version("4.04"))
            .AddSingleton<RecordingProcessRunner>()
            .AddSingleton<IProcessRunner>(provider => provider.GetRequiredService<RecordingProcessRunner>());

    protected override async Task GenerateGameFiles()
    {
        await WriteGameFile("bin/x64/witcher3.exe", "Fake game executable; never run");
        await WriteGameFile("content/content0/bundles/base.bundle", "Original game content");
        await WriteGameFile("DLC/bob/content/bundles/expansion.bundle", "Original expansion content");
        await WriteGameFile("Mods/modExisting/content/bundles/existing.bundle", "Existing manual mod");
        await WriteGameFile("DLC/dlcExisting/content/bundles/existing.bundle", "Existing custom DLC");
    }

    [Fact]
    public async Task MixedPackageAppliesAndDisablesWithoutRemovingExistingContent()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("Release/Mods/modNew/content/bundles/new.bundle", "Release/DLC/dlcNew/content/bundles/new.bundle", "Release/README.txt");
        var group = await Install<Witcher3ArchiveInstaller>(loadout, archive);
        ChildrenFilesAndHashes(group).Select(file => file.GamePath).Should().BeEquivalentTo(new[] {
            new GamePath(LocationId.Game, "Mods/modNew/content/bundles/new.bundle"),
            new GamePath(LocationId.Game, "DLC/dlcNew/content/bundles/new.bundle") });

        await SynchronizerService.Synchronize(loadout.Rebase());
        OnDisk("Mods/modNew/content/bundles/new.bundle").FileExists.Should().BeTrue();
        OnDisk("DLC/dlcNew/content/bundles/new.bundle").FileExists.Should().BeTrue();
        OnDisk("README.txt").FileExists.Should().BeFalse();

        await DisableItem(group.Id);
        await SynchronizerService.Synchronize(loadout.Rebase());
        OnDisk("Mods/modNew/content/bundles/new.bundle").FileExists.Should().BeFalse();
        OnDisk("DLC/dlcNew/content/bundles/new.bundle").FileExists.Should().BeFalse();
        (await OnDisk("Mods/modExisting/content/bundles/existing.bundle").ReadAllTextAsync()).Should().Be("Existing manual mod");
        (await OnDisk("DLC/dlcExisting/content/bundles/existing.bundle").ReadAllTextAsync()).Should().Be("Existing custom DLC");
        (await OnDisk("content/content0/bundles/base.bundle").ReadAllTextAsync()).Should().Be("Original game content");
        (await OnDisk("DLC/bob/content/bundles/expansion.bundle").ReadAllTextAsync()).Should().Be("Original expansion content");
    }

    [Fact]
    public async Task SwitchingProfilesAndUnmanagingRestoreUncataloguedOriginalFiles()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        Dictionary<string, string> originalFiles = new()
        {
            ["bin/x64/witcher3.exe"] = "Fake game executable; never run",
            ["content/content0/bundles/base.bundle"] = "Original game content",
            ["DLC/bob/content/bundles/expansion.bundle"] = "Original expansion content",
            ["Mods/modExisting/content/bundles/existing.bundle"] = "Existing manual mod",
            ["DLC/dlcExisting/content/bundles/existing.bundle"] = "Existing custom DLC",
        };
        const string firstOnly = "Mods/modOnlyFirst/content/bundles/new.bundle";

        // Seed the same target records that a reviewed manual installation can create,
        // including original files outside the automatic archive installer's scope.
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, originalFiles.Keys.Append(firstOnly).Select(path => (RelativePath)path), first, "Reviewed replacements");
            await tx.Commit();
        }
        await SynchronizerService.Synchronize(first.Rebase());
        foreach (var path in originalFiles.Keys)
            (await OnDisk(path).ReadAllTextAsync()).Should().Be(path);

        // Even Content and official DLC, normally excluded from bulk backup, must be
        // recoverable before a mod can replace them.
        (await FileStore.HaveFile("Original game content".xxHash3AsUtf8())).Should().BeTrue();
        (await FileStore.HaveFile("Original expansion content".xxHash3AsUtf8())).Should().BeTrue();

        await SynchronizerService.Synchronize(second.Rebase());
        foreach (var (path, contents) in originalFiles)
            (await OnDisk(path).ReadAllTextAsync()).Should().Be(contents);
        OnDisk(firstOnly).FileExists.Should().BeFalse();

        await SynchronizerService.Synchronize(first.Rebase());
        OnDisk(firstOnly).FileExists.Should().BeTrue();
        await LoadoutManager.UnManage(GameInstallation);
        foreach (var (path, contents) in originalFiles)
            (await OnDisk(path).ReadAllTextAsync()).Should().Be(contents);
        OnDisk(firstOnly).FileExists.Should().BeFalse();
        GameRegistry.TryGetMetadata(GameInstallation, out var metadata).Should().BeTrue();
        metadata.Contains(GameInstallMetadata.LastSyncedLoadout).Should().BeFalse();
        metadata.Contains(GameInstallMetadata.InitialDiskStateTransaction).Should().BeFalse();
        Loadout.FindByInstallation(metadata.Db, metadata).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedOriginalBackupFailsBeforeDeletionOrIntrinsicReplacement(bool intrinsic)
    {
        var loadout = await CreateLoadout();
        var path = new GamePath(LocationId.Game, "content/content0/bundles/base.bundle");
        // Exercise the indexed-size preflight without allocating a multi-gigabyte fixture.
        var disk = new SyncNodePart
        {
            Hash = "Original game content".xxHash3AsUtf8(),
            Size = Size.GB * 6,
            LastModifiedTicks = 0,
        };
        Dictionary<GamePath, SyncNode> tree = new()
        {
            [path] = new SyncNode
            {
                Disk = disk,
                Previous = disk,
                SourceItemType = intrinsic ? LoadoutSourceItemType.Intrinsic : LoadoutSourceItemType.Game,
            },
        };
        Synchronizer.ProcessSyncTree(tree);
        tree[path].Actions.Should().HaveFlag(Actions.BackupFile);
        tree[path].Actions.Should().HaveFlag(intrinsic ? Actions.WriteIntrinsic : Actions.DeleteFromDisk);

        var apply = () => Synchronizer.RunActions(tree, loadout);
        await apply.Should().ThrowAsync<Exception>().WithMessage("*larger than the maximum*");
        (await OnDisk(path.Path.ToString()).ReadAllTextAsync()).Should().Be("Original game content");
    }

    [Fact]
    public async Task ExcludedFilesSurviveActionsAndCleanupOfTheirLastManagedSibling()
    {
        var loadout = await CreateLoadout();
        var removedPath = new GamePath(LocationId.Game, "scratch/delete.bundle");
        var excludedPath = new GamePath(LocationId.Game, "scratch/keep.user");
        await WriteGameFile(removedPath.Path.ToString(), "Removable mod file");
        await WriteGameFile(excludedPath.Path.ToString(), "Unmanaged user data");

        var synchronizer = new FilteredPreservingSynchronizer(ServiceProvider);
        var metadata = await synchronizer.ReindexState(GameInstallation);
        var disk = synchronizer.GetDiskStateForGame(metadata);
        disk.Should().NotContain(file => file.Path == excludedPath);
        var tree = synchronizer.BuildSyncTree(disk, disk, loadout.Rebase());

        // A stale indexed entry must not defeat the current filter either.
        var excludedPart = new SyncNodePart
        {
            Hash = "Unmanaged user data".xxHash3AsUtf8(),
            Size = Size.FromLong(19),
            LastModifiedTicks = 0,
        };
        tree[excludedPath] = new SyncNode { Disk = excludedPart, Previous = excludedPart };
        synchronizer.ProcessSyncTree(tree);
        tree[excludedPath].Actions.Should().Be(Actions.DoNothing);
        tree[removedPath].Actions.Should().HaveFlag(Actions.DeleteFromDisk);
        await synchronizer.RunActions(tree, GameInstallation);

        OnDisk(removedPath.Path.ToString()).FileExists.Should().BeFalse();
        (await OnDisk(excludedPath.Path.ToString()).ReadAllTextAsync()).Should().Be("Unmanaged user data");
    }

    [Fact]
    public async Task UnsupportedCompanionFilesDoNotPartiallyInstall()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("Mods/modValid/content/a.bundle", "bin/config/r4game/user_config_matrix/pc/input.xml");
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Unsupported mixed package", LoadoutId = loadout.Id },
        };

        var result = await new Witcher3ArchiveInstaller(ServiceProvider).ExecuteAsync(archive, group, tx, loadout, CancellationToken.None);
        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("outside supported mod folders");
        var commit = await tx.Commit();
        commit.Remap(group).Children.Should().BeEmpty("declining an installer must not leave a partially installed package");
    }

    [Theory]
    [InlineData(true, false, "bin/x64/witcher3.exe")]
    [InlineData(false, true, "bin/x64_DX12/witcher3.exe")]
    [InlineData(true, true, "bin/x64/witcher3.exe")]
    public async Task LaunchSelectsAnExistingExecutableAndPreservesArguments(bool directX11, bool directX12, string expected)
    {
        var loadout = await CreateLoadout();
        OnDisk("bin/x64/witcher3.exe").Delete();
        if (directX11) await WriteGameFile("bin/x64/witcher3.exe", "Fake DirectX 11 executable");
        if (directX12) await WriteGameFile("bin/x64_DX12/witcher3.exe", "Fake DirectX 12 executable");
        string[] arguments = ["--test", "a value with spaces", "quoted \"value\""];

        await LaunchTool.Execute(loadout, CancellationToken.None, arguments);

        var command = Processes.Commands.Should().ContainSingle().Which;
        command.TargetFilePath.Should().Be(OnDisk(expected).ToString());
        command.WorkingDirPath.Should().Be(OnDisk(expected).Parent.ToString());
        command.Arguments.Should().Be(new Command("unused").WithArguments(arguments).Arguments);
    }

    [Fact]
    public async Task MissingExecutableFailsBeforeCallingProcessRunner()
    {
        var loadout = await CreateLoadout();
        OnDisk("bin/x64/witcher3.exe").Delete();
        var launch = () => LaunchTool.Execute(loadout, CancellationToken.None, []);
        await launch.Should().ThrowAsync<FileNotFoundException>().WithMessage("*Verify the game files*");
        Processes.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelledLaunchNeverCallsProcessRunner()
    {
        var loadout = await CreateLoadout();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var launch = () => LaunchTool.Execute(loadout, cancellation.Token, []);
        await launch.Should().ThrowAsync<OperationCanceledException>();
        Processes.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task GameDefinitionUsesKnownStoresAndKeepsUserDocumentsOutsideManagement()
    {
        Game.StoreIdentifiers.SteamAppIds.Should().Equal(292030u);
        Game.StoreIdentifiers.GOGProductIds.Should().Equal(1207664643L, 1495134320L);
        GameInstallation.Locations.Keys.Should().Equal(LocationId.Game);
        GameInstallation.Game.NexusModsGameId.Value.Value.Should().Be(952u);
        await using var image = await Game.IconImage.GetStreamAsync();
        image.Length.Should().BeGreaterThan(0, "the game tile must refer to an embedded resource available at runtime");
    }

    [Theory]
    [InlineData("content/content0/bundles/base.bundle", true)]
    [InlineData("DLC/BOB/content/bundles/expansion.bundle", true)]
    [InlineData("dlc/dlc16/content/bundles/official.bundle", true)]
    [InlineData("Mods/modExisting/content/bundles/existing.bundle", false)]
    [InlineData("DLC/dlcExisting/content/bundles/existing.bundle", false)]
    [InlineData("DLC/dlc17/content/bundles/custom.bundle", false)]
    [InlineData("bin/x64/witcher3.exe", false)]
    public void DefaultBackupRulesOnlySkipBaseContentAndDocumentedOriginalDlc(string path, bool ignored)
    {
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Game, path)).Should().Be(ignored);
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.AppData, path)).Should().BeFalse();
    }

    [Fact]
    public void FullBackupSettingIncludesOriginalGameContent()
    {
        ServiceProvider.GetRequiredService<ISettingsManager>().Get<Witcher3Settings>().DoFullGameBackup = true;
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Game, "content/content0/bundles/base.bundle")).Should().BeFalse();
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Game, "DLC/bob/content/bundles/expansion.bundle")).Should().BeFalse();
    }

    private AbsolutePath OnDisk(string relative) => GameInstallation.Locations[LocationId.Game].Path.Combine(relative);

    private async Task WriteGameFile(string relative, string contents)
    {
        var path = OnDisk(relative);
        path.Parent.CreateDirectory();
        await path.WriteAllTextAsync(contents);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<Command> Commands { get; } = [];

        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Path.GetFileName(command.TargetFilePath).Should().Be("witcher3.exe");
            Commands.Add(command);
            return Task.FromResult(new CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public void Run(Command command, bool logOutput = false) => throw new NotSupportedException("Tests must not start external processes.");
        public Task RunAsync(Process process, CancellationToken cancellationToken = default) => throw new NotSupportedException("Tests must not start external processes.");
    }

    private sealed class FilteredPreservingSynchronizer(IServiceProvider provider) : ALoadoutSynchronizer(provider)
    {
        protected override bool PreserveInitialDiskState => true;
        protected override IGamePathFilter GamePathFilter { get; } = GamePathFilters.Create(path => path.Path.ToString().EndsWith(".user", StringComparison.OrdinalIgnoreCase));
    }
}
