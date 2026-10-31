using System.Collections.Frozen;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.Emitters;
using NexusMods.Games.CreationEngine.Installers;
using NexusMods.Games.TestFramework;
using NexusMods.DataModel;
using NexusMods.Hashing.xxHash3;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit.Abstractions;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>
/// Offline archives and real TES4 headers exercise the installed game adapters and file
/// synchronizer. These tests do not launch Skyrim/Fallout or establish runtime compatibility.
/// </summary>
public abstract class CreationEnginePipelineTests<TGame>(ITestOutputHelper output)
    : ALibraryArchiveInstallerTests<CreationEnginePipelineTests<TGame>, TGame>(output)
    where TGame : class, ICreationEngineGame
{
    private bool IsFallout4 => typeof(TGame) == typeof(CreationEngine.Fallout4.Fallout4);
    private StopPatternInstaller Installer => Game.LibraryItemInstallers.OfType<StopPatternInstaller>().Single();
    private PluginsFile Plugins => new(ServiceProvider.GetRequiredService<ILogger<PluginsFile>>(), Game);

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddCreationEngine().AddAdapters().AddIsolatedCreationEngineLocator<TGame>();

    [Theory]
    [InlineData("Package/Only.bsa", "Data/Only.bsa")]
    [InlineData("Package/Only.ba2", "Data/Only.ba2")]
    [InlineData("Outer/Inner/data/Example.esp", "Data/Example.esp")]
    [InlineData("Data/Data/Example.esp", "Data/Example.esp")]
    [InlineData("meshes/actors/example.nif", "Data/meshes/actors/example.nif")]
    [InlineData("Outer/Inner/Data/settings.ini", "Data/settings.ini")]
    public async Task InstallsCommonArchiveLayouts(string source, string destination)
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths(source);
        var group = await Install(Installer, loadout, archive);
        ChildrenFilesAndHashes(group).Select(file => file.GamePath)
            .Should().Equal(new GamePath(LocationId.Game, destination));
    }

    [Fact]
    public async Task KeepsExtenderAtGameRootAndItsScriptsAndPluginsInData()
    {
        var extender = IsFallout4 ? "f4se" : "skse64";
        var pluginFolder = IsFallout4 ? "F4SE" : "SKSE";
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths(
            $"Release/{extender}_loader.exe",
            $"Release/{extender}_1_2_3.dll",
            "Release/Data/Scripts/example.pex",
            $"Release/Data/{pluginFolder}/Plugins/example.dll");
        var group = await Install(Installer, loadout, archive);
        ChildrenFilesAndHashes(group).Select(file => file.GamePath).Should().BeEquivalentTo(new[]
        {
            new GamePath(LocationId.Game, $"{extender}_loader.exe"),
            new GamePath(LocationId.Game, $"{extender}_1_2_3.dll"),
            new GamePath(LocationId.Game, "Data/Scripts/example.pex"),
            new GamePath(LocationId.Game, $"Data/{pluginFolder}/Plugins/example.dll"),
        });
    }

    [Theory]
    [InlineData("00 Core/A.esp", "01 Optional/B.esp")]
    [InlineData("Data/Example.esp", "example.ESP")]
    public async Task RejectsAmbiguousVariantsAndDuplicateDestinations(string first, string second)
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths(first, second);
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout, Name = "Unresolved archive" },
        };
        var result = await Installer.ExecuteAsync(archive, group, tx, loadout, CancellationToken.None);
        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RealArchiveInstallAndApplyHonorMasterFlagsDependenciesAndOfficialPlugins()
    {
        var loadout = await CreateLoadout();
        var baseMaster = Game.OfficialPlugins[0].FileName.ToString();
        var installed = await InstallArchive(loadout, new Dictionary<string, byte[]>
        {
            [$"Package/Data/{baseMaster}"] = PluginBytes(master: true),
            ["Package/Data/middle.esm"] = PluginBytes(), // Extension also forces master status.
            ["Package/Data/z-master.esp"] = PluginBytes(master: true, masters: [baseMaster]),
            ["Package/Data/a-light.esl"] = PluginBytes(masters: ["z-master.esp"]),
            ["Package/Data/z-dependent.esp"] = PluginBytes(masters: ["a-light.esl"]),
            ["Package/Data/b-lightflag.esp"] = PluginBytes(light: true),
            ["Package/Data/a-independent.esp"] = PluginBytes(),
            ["Package/Data/Textures/test.dds"] = "texture fixture"u8.ToArray(),
        });

        await SynchronizerService.Synchronize(loadout.Rebase());

        var actual = await PluginListPath.ReadAllBytesAsync();
        actual.Take(3).Should().NotEqual(Encoding.UTF8.GetPreamble());
        Encoding.UTF8.GetString(actual).Should().Be(
            "# Generated by Nexus Mods App Rework\r\n" +
            "*middle.esm\r\n*z-master.esp\r\n*a-light.esl\r\n" +
            "*a-independent.esp\r\n*b-lightflag.esp\r\n*z-dependent.esp\r\n");
        (await OnDisk("Data/Textures/test.dds").ReadAllTextAsync()).Should().Be("texture fixture");
        OnDisk($"Data/{baseMaster}").FileExists.Should().BeTrue();

        // Reordered inputs must produce exactly the same plugin list.
        var current = loadout.Rebase();
        var generatedState = Synchronizer.GetPreviouslyAppliedDiskState(current.Installation)
            .Should().ContainSingle(entry => entry.Path == Game.PluginsFile).Which;
        generatedState.Part.Hash.Should().Be(actual.xxHash3());
        generatedState.Part.Size.Should().Be(PluginListPath.FileInfo.Size);
        generatedState.Part.LastModifiedTicks.Should().Be(PluginListPath.FileInfo.LastWriteTimeUtc.Ticks);

        var tree = await Synchronizer.BuildSyncTree(current);
        Synchronizer.ProcessSyncTree(tree);
        tree[Game.PluginsFile].Actions.Should().HaveFlag(Actions.WriteIntrinsic);
        tree[Game.PluginsFile].Actions.Should().NotHaveFlag(Actions.AdaptLoadout);
        var reversed = tree.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        await using var output = new MemoryStream();
        await Plugins.Write(output, current, reversed);
        output.ToArray().Should().Equal(actual);

        // Each apply must refresh the list, including removal and restoration of entries.
        await DisableItem(installed.Id);
        await SynchronizerService.Synchronize(loadout.Rebase());
        (await PluginListPath.ReadAllTextAsync()).Should().Be("# Generated by Nexus Mods App Rework\r\n");
        OnDisk("Data/a-independent.esp").FileExists.Should().BeFalse();

        await EnableItem(installed.Id);
        await SynchronizerService.Synchronize(loadout.Rebase());
        (await PluginListPath.ReadAllBytesAsync()).Should().Equal(actual);
        OnDisk("Data/a-independent.esp").FileExists.Should().BeTrue();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("corrupt")]
    public async Task InvalidPluginsBlockApplyBeforeChangingExistingFiles(string failure)
    {
        var loadout = await CreateLoadout();
        await SynchronizerService.Synchronize(loadout);
        await PluginListPath.WriteAllTextAsync("Existing plugin list\r\n");
        var keep = OnDisk("Data/Textures/keep.dds");
        keep.Parent.CreateDirectory();
        await keep.WriteAllTextAsync("Existing game content");

        var files = failure switch
        {
            "missing" => new Dictionary<string, byte[]> { ["Data/A.esp"] = PluginBytes(masters: ["Absent.esm"]) },
            "cycle" => new Dictionary<string, byte[]>
            {
                ["Data/A.esp"] = PluginBytes(masters: ["B.esp"]),
                ["Data/B.esp"] = PluginBytes(masters: ["A.esp"]),
            },
            _ => new Dictionary<string, byte[]> { ["Data/A.esp"] = "not a TES4 header"u8.ToArray() },
        };
        await InstallArchive(loadout.Rebase(), files);
        var before = loadout.Rebase();
        Func<Task> apply = () => SynchronizerService.Synchronize(before);
        await apply.Should().ThrowAsync<InvalidDataException>();

        (await PluginListPath.ReadAllTextAsync()).Should().Be("Existing plugin list\r\n");
        (await keep.ReadAllTextAsync()).Should().Be("Existing game content");
        OnDisk("Data/A.esp").FileExists.Should().BeFalse();
        loadout.Rebase().LastAppliedDateTime.Should().Be(before.LastAppliedDateTime);

        var diagnostics = await new MissingMasterEmitter(Game).Diagnose(loadout.Rebase(), CancellationToken.None).ToListAsync();
        diagnostics.Should().NotBeEmpty();
    }

    [Fact]
    public async Task UnknownGameVersionRetainsOriginalMastersAssetsAndManualModsAcrossProfilesAndUnmanage()
    {
        var baseMaster = Game.OfficialPlugins[0].FileName.ToString();
        var basePath = OnDisk($"Data/{baseMaster}");
        basePath.Parent.CreateDirectory();
        var originalMaster = PluginBytes(master: true);
        await basePath.WriteAllBytesAsync(originalMaster);
        await OnDisk("Data/OriginalManual.esp").WriteAllBytesAsync(PluginBytes(masters: [baseMaster]));
        var originalArchive = OnDisk(IsFallout4 ? "Data/Original.ba2" : "Data/Original.bsa");
        await originalArchive.WriteAllTextAsync("Original uncatalogued game archive");
        var executable = GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation));
        await executable.WriteAllTextAsync("Original game executable fixture; never executed");
        const string originalPluginList = "# Original user comments\r\nOriginalManual.esp\r\n";
        PluginListPath.Parent.CreateDirectory();
        await PluginListPath.WriteAllTextAsync(originalPluginList);

        var first = await CreateLoadout();
        await InstallArchive(first, new Dictionary<string, byte[]>
        {
            ["Data/FirstProfile.esp"] = PluginBytes(masters: [baseMaster]),
            [$"Data/{originalArchive.Name}"] = "Replacement mod archive"u8.ToArray(),
        });
        await SynchronizerService.Synchronize(first.Rebase());
        (await originalArchive.ReadAllTextAsync()).Should().Be("Replacement mod archive");

        var second = await CreateLoadout();
        await InstallArchive(second, new Dictionary<string, byte[]>
        {
            ["Data/SecondProfile.esp"] = PluginBytes(masters: [baseMaster]),
        });
        await SynchronizerService.Synchronize(second.Rebase());

        (await basePath.ReadAllBytesAsync()).Should().Equal(originalMaster);
        (await originalArchive.ReadAllTextAsync()).Should().Be("Original uncatalogued game archive");
        OnDisk("Data/OriginalManual.esp").FileExists.Should().BeTrue();
        OnDisk("Data/FirstProfile.esp").FileExists.Should().BeFalse();
        OnDisk("Data/SecondProfile.esp").FileExists.Should().BeTrue();

        await LoadoutManager.UnManage(GameInstallation, runGc: false);
        (await basePath.ReadAllBytesAsync()).Should().Equal(originalMaster);
        (await originalArchive.ReadAllTextAsync()).Should().Be("Original uncatalogued game archive");
        (await executable.ReadAllTextAsync()).Should().Be("Original game executable fixture; never executed");
        OnDisk("Data/OriginalManual.esp").FileExists.Should().BeTrue();
        OnDisk("Data/SecondProfile.esp").FileExists.Should().BeFalse();
        (await PluginListPath.ReadAllTextAsync()).Should().Be(originalPluginList);
    }

    [Fact]
    public async Task ProfileSwitchRefreshesTouchedButUnchangedUnarchivedMaster()
    {
        var masterName = Game.OfficialPlugins[0].FileName.ToString();
        var path = OnDisk($"Data/{masterName}");
        path.Parent.CreateDirectory();
        var bytes = PluginBytes(master: true);
        await path.WriteAllBytesAsync(bytes);
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        await InstallArchive(second, new Dictionary<string, byte[]>
        {
            ["Data/Dependent.esp"] = PluginBytes(masters: [masterName]),
        });

        var hash = bytes.xxHash3();
        (await FileStore.HaveFile(hash)).Should().BeFalse("an untouched retained master is read from the game installation");
        var entry = path.FileInfo;
        entry.LastWriteTime = entry.LastWriteTime.AddMinutes(1);
        var source = ServiceProvider.GetServices<IReadOnlyStreamSource>().OfType<GameFileStreamSource>().Single();
        source.Resolve(hash).Should().BeNull("the last disk scan still has the previous timestamp");

        await SynchronizerService.Synchronize(second.Rebase());

        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(second.LoadoutId);
        (await path.ReadAllBytesAsync()).Should().Equal(bytes);
        OnDisk("Data/Dependent.esp").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task UnmanageRemovesGeneratedPluginListWhenNoOriginalListExisted()
    {
        var loadout = await CreateLoadout();
        await InstallArchive(loadout, new Dictionary<string, byte[]> { ["Data/ManagedOnly.esp"] = PluginBytes() });
        await SynchronizerService.Synchronize(loadout.Rebase());
        (await PluginListPath.ReadAllTextAsync()).Should().Contain("*ManagedOnly.esp");

        await LoadoutManager.UnManage(GameInstallation, runGc: false);

        PluginListPath.FileExists.Should().BeFalse();
        OnDisk("Data/ManagedOnly.esp").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidTargetProfileDoesNotDeactivateTheCurrentProfile()
    {
        var current = await CreateLoadout();
        await InstallArchive(current, new Dictionary<string, byte[]>
        {
            ["Data/Current.esp"] = PluginBytes(),
            ["Data/Textures/current.dds"] = "current profile texture"u8.ToArray(),
        });
        await SynchronizerService.Synchronize(current.Rebase());
        var originalPluginList = await PluginListPath.ReadAllBytesAsync();

        var target = await CreateLoadout();
        await InstallArchive(target, new Dictionary<string, byte[]>
        {
            ["Data/BrokenTarget.esp"] = PluginBytes(masters: ["NotInstalled.esm"]),
        });

        Func<Task> activate = () => SynchronizerService.Synchronize(target.Rebase());
        await activate.Should().ThrowAsync<InvalidDataException>();

        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(current.LoadoutId);
        OnDisk("Data/Current.esp").FileExists.Should().BeTrue();
        OnDisk("Data/BrokenTarget.esp").FileExists.Should().BeFalse();
        (await OnDisk("Data/Textures/current.dds").ReadAllTextAsync()).Should().Be("current profile texture");
        (await PluginListPath.ReadAllBytesAsync()).Should().Equal(originalPluginList);
    }

    [Fact]
    public async Task DisabledMasterDiagnosticUsesCaseInsensitiveNameAndRealOwningGroups()
    {
        var loadout = await CreateLoadout();
        var master = await InstallArchive(loadout, new Dictionary<string, byte[]> { ["Data/Master.esm"] = PluginBytes(master: true) });
        var dependent = await InstallArchive(loadout.Rebase(), new Dictionary<string, byte[]>
        {
            ["Data/Dependent.esp"] = PluginBytes(masters: ["MASTER.ESM"]),
        });
        await DisableItem(master.Id);

        var current = loadout.Rebase();
        var emitter = new MissingMasterEmitter(Game);
        var ordinary = await emitter.Diagnose(current, CancellationToken.None).ToListAsync();
        var tree = Synchronizer.BuildSyncTree(Array.Empty<PathPartPair>(), Array.Empty<PathPartPair>(), current);
        Synchronizer.ProcessSyncTree(tree);
        var supplied = await emitter.Diagnose(current, tree.ToFrozenDictionary(), CancellationToken.None).ToListAsync();

        ordinary.Should().ContainSingle().Which.Title.Should().Be("Disabled Master");
        supplied.Should().ContainSingle().Which.Id.Should().Be(ordinary.Single().Id);
        ordinary.Single().DataReferences.Values.OfType<LoadoutItemGroupReference>().Select(reference => reference.DataId)
            .Should().BeEquivalentTo(new[] { master.LoadoutItemGroupId, dependent.LoadoutItemGroupId });
        foreach (var reference in ordinary.Single().DataReferences.Values.OfType<LoadoutItemGroupReference>())
            reference.ResolveData(ServiceProvider, Connection).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task DisabledCorruptPluginDoesNotBlockEnabledPlugins()
    {
        var loadout = await CreateLoadout();
        var broken = await InstallArchive(loadout, new Dictionary<string, byte[]> { ["Data/Broken.esp"] = "bad"u8.ToArray() });
        await DisableItem(broken.Id);
        await InstallArchive(loadout.Rebase(), new Dictionary<string, byte[]> { ["Data/Good.esp"] = PluginBytes() });
        await SynchronizerService.Synchronize(loadout.Rebase());
        (await PluginListPath.ReadAllTextAsync()).Should().Contain("*Good.esp").And.NotContain("Broken.esp");
    }

    [Fact]
    public async Task DesiredWinnerIsParsedInsteadOfStaleDiskBytes()
    {
        var loadout = await CreateLoadout();
        var archive = await Archive(new Dictionary<string, byte[]>
        {
            ["Good.esp"] = PluginBytes(),
            ["Bad.esp"] = "bad"u8.ToArray(),
        });
        var good = archive.Children.Single(file => file.Path.FileName == (RelativePath)"Good.esp").AsLibraryFile();
        var bad = archive.Children.Single(file => file.Path.FileName == (RelativePath)"Bad.esp").AsLibraryFile();
        var node = new SyncNode
        {
            SourceItemType = LoadoutSourceItemType.Loadout,
            Loadout = new SyncNodePart { Hash = good.Hash, Size = good.Size, LastModifiedTicks = 0 },
            Disk = new SyncNodePart { Hash = bad.Hash, Size = bad.Size, LastModifiedTicks = 0 },
        };
        var tree = new Dictionary<GamePath, SyncNode> { [new GamePath(LocationId.Game, "Data/Good.esp")] = node };
        await using var output = new MemoryStream();
        await Plugins.Write(output, loadout, tree);
        Encoding.UTF8.GetString(output.ToArray()).Should().Contain("*Good.esp");

        // Once an external change is being ingested, its header is the effective header.
        tree[new GamePath(LocationId.Game, "Data/Good.esp")] = node with { Actions = Actions.IngestFromDisk };
        Func<Task> write = () => Plugins.Write(output, loadout, tree);
        await write.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task DiagnosticsHonorCancellation()
    {
        var loadout = await CreateLoadout();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> diagnose = async () => await new MissingMasterEmitter(Game)
            .Diagnose(loadout, cancellation.Token).ToListAsync();
        await diagnose.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IntrinsicWriterFailurePreservesDestinationAndRemovesTemporaryFile()
    {
        var loadout = await CreateLoadout();
        var path = new GamePath(LocationId.AppData, "FailurePreservation.txt");
        var absolute = GameInstallation.Locations.ToAbsolutePath(path);
        absolute.Parent.CreateDirectory();
        await absolute.WriteAllTextAsync("Keep this configuration");
        var before = absolute.Parent.EnumerateFiles().ToArray();
        var synchronizer = new FailingIntrinsicSynchronizer(ServiceProvider, path);
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [path] = new() { SourceItemType = LoadoutSourceItemType.Intrinsic, Actions = Actions.WriteIntrinsic },
        };

        Func<Task> run = () => synchronizer.RunActions(tree, loadout);
        await run.Should().ThrowAsync<InvalidDataException>();
        (await absolute.ReadAllTextAsync()).Should().Be("Keep this configuration");
        absolute.Parent.EnumerateFiles().Should().BeEquivalentTo(before);
    }

    private AbsolutePath PluginListPath => GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile);
    private AbsolutePath OnDisk(string path) => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, path));

    private async Task<LibraryArchive.ReadOnly> Archive(Dictionary<string, byte[]> files)
    {
        await using var archive = await CreateTestArchive(files.ToDictionary(pair => RelativePath.FromUnsanitizedInput(pair.Key), pair => pair.Value));
        return await RegisterLocalArchive(archive);
    }

    private async Task<LoadoutItemGroup.ReadOnly> InstallArchive(Loadout.ReadOnly loadout, Dictionary<string, byte[]> files)
    {
        var archive = await Archive(files);
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        result.LoadoutItemGroup.HasValue.Should().BeTrue();
        return result.LoadoutItemGroup!.Value;
    }

    /// <summary>Minimal on-disk TES4 record, parsed by the production Mutagen reader.</summary>
    private byte[] PluginBytes(bool master = false, bool light = false, string[]? masters = null)
    {
        using var content = new MemoryStream();
        using (var writer = new BinaryWriter(content, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("HEDR"u8);
            writer.Write((ushort)12);
            writer.Write(IsFallout4 ? 1.0f : 1.7f);
            writer.Write(0u);
            writer.Write(0x800u);
            foreach (var dependency in masters ?? [])
            {
                var name = Encoding.UTF8.GetBytes(dependency + "\0");
                writer.Write("MAST"u8);
                writer.Write((ushort)name.Length);
                writer.Write(name);
                writer.Write("DATA"u8);
                writer.Write((ushort)8);
                writer.Write(0UL);
            }
        }
        using var plugin = new MemoryStream();
        using (var writer = new BinaryWriter(plugin, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("TES4"u8);
            writer.Write((uint)content.Length);
            writer.Write((master ? 1u : 0u) | (light ? 0x200u : 0u));
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ushort)(IsFallout4 ? 131 : 44));
            writer.Write((ushort)0);
            writer.Write(content.ToArray());
        }
        return plugin.ToArray();
    }

    private sealed class FailingIntrinsicSynchronizer(IServiceProvider provider, GamePath path) : ALoadoutSynchronizer(provider)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
            => new() { [path] = new FailingIntrinsic(path) };
    }

    private sealed class FailingIntrinsic(GamePath path) : IIntrinsicFile
    {
        public GamePath Path => path;

        public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
        {
            await stream.WriteAsync("partial output"u8.ToArray());
            throw new InvalidDataException("Simulated intrinsic generation failure");
        }

        public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
            => Task.CompletedTask;
    }
}

public class SkyrimPluginPipelineTests(ITestOutputHelper output)
    : CreationEnginePipelineTests<CreationEngine.SkyrimSE.SkyrimSE>(output)
{
    [Fact]
    public void GogInstallationUsesItsOwnPluginListAndPreferences()
    {
        var current = GameInstallation.LocatorResult;
        var gog = new GameLocatorResult
        {
            Game = current.Game, Path = current.Path, Store = GameStore.GOG,
            Locator = current.Locator, LocatorIds = current.LocatorIds, StoreIdentifier = current.StoreIdentifier,
        };
        var locations = Game.GetLocations(FileSystem, gog);
        locations[LocationId.AppData].Should().Be(FileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory) / "Skyrim Special Edition GOG");
        locations[LocationId.Preferences].Should().Be(FileSystem.GetKnownPath(KnownPath.MyGamesDirectory) / "Skyrim Special Edition GOG");
    }
}

public class Fallout4PluginPipelineTests(ITestOutputHelper output)
    : CreationEnginePipelineTests<CreationEngine.Fallout4.Fallout4>(output);
