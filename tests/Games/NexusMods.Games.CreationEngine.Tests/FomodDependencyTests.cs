using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FOMOD;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit.Abstractions;

namespace NexusMods.Games.CreationEngine.Tests;

public class FomodDependencyTests(ITestOutputHelper output)
    : AIsolatedGameTest<FomodDependencyTests, CreationEngine.SkyrimSE.SkyrimSE>(output)
{
    private static readonly GamePath MasterPath = new(LocationId.Game, "Data/Skyrim.esm");

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddCreationEngine().AddAdapters()
            .AddIsolatedCreationEngineLocator<CreationEngine.SkyrimSE.SkyrimSE>();

    protected override async Task GenerateGameFiles()
    {
        var path = GameInstallation.Locations.ToAbsolutePath(MasterPath);
        path.Parent.CreateDirectory();
        await path.WriteAllBytesAsync(MasterBytes());
    }

    [Fact]
    public async Task ABaseMasterIsPresentInAnotherProfileWithoutAnInstalledModRow()
    {
        await CreateLoadout();
        var target = await CreateLoadout();
        LoadoutItem.FindByLoadout(Connection.Db, target).OfTypeLoadoutItemWithTargetPath()
            .Where(file => (GamePath)file.TargetPath == MasterPath).Should().BeEmpty();

        var snapshot = ServiceProvider.GetRequiredService<IFomodDependencyResolver>().Capture(target.Rebase());
        snapshot.PresentFiles.Should().Contain(MasterPath);
        snapshot.ActiveFiles.Should().Contain(MasterPath);
    }

    [Fact]
    public async Task AWinningDeletionSuppressesTheBaseMasterAndAnOlderModContender()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Older master replacement", MasterPath);
        await LoadoutManager.InstallItemWrapper(loadout, tx =>
        {
            var group = AddEmptyGroup(tx, loadout, "Removed master");
            _ = new DeletedFile.New(tx, out var id)
            {
                Reason = "Profile deletion fixture",
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = MasterPath.ToGamePathParentTuple(loadout),
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Skyrim.esm", LoadoutId = loadout, ParentId = group },
                },
            };
            return Task.FromResult(group);
        });

        var snapshot = ServiceProvider.GetRequiredService<IFomodDependencyResolver>().Capture(loadout.Rebase());
        snapshot.PresentFiles.Should().NotContain(MasterPath);
        snapshot.ActiveFiles.Should().NotContain(MasterPath);
        GameInstallation.Locations.ToAbsolutePath(MasterPath).FileExists.Should().BeTrue(
            "capturing installer dependencies must not apply the profile's deletion to disk");
    }

    [Fact]
    public async Task StagedCollectionDependenciesIgnoreOnlyTheTemporaryDisabledParent()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout, "Staged collection");
        var enabledPath = new GamePath(LocationId.Game, "Data/Enabled.esp");
        var disabledPath = new GamePath(LocationId.Game, "Data/Disabled.esp");
        await AddMod(loadout, "Enabled prerequisite", enabledPath, collection.AsLoadoutItemGroup().LoadoutItemGroupId);
        var disabled = await AddMod(loadout, "Disabled prerequisite", disabledPath, collection.AsLoadoutItemGroup().LoadoutItemGroupId);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(collection.Id, LoadoutItem.Disabled, Null.Instance);
            tx.Add(disabled.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        var resolver = ServiceProvider.GetRequiredService<IFomodDependencyResolver>();
        var ordinary = resolver.Capture(loadout.Rebase());
        ordinary.ActiveFiles.Should().NotContain(enabledPath);
        var staged = resolver.Capture(loadout.Rebase(), collection.AsLoadoutItemGroup().LoadoutItemGroupId);
        staged.ActiveFiles.Should().Contain(enabledPath);
        staged.PresentFiles.Should().Contain(disabledPath);
        staged.ActiveFiles.Should().NotContain(disabledPath);
        LoadoutItem.Load(Connection.Db, collection.Id).IsDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task CorruptParentCyclesFailBeforeDependencyQueriesInsteadOfHanging()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout, "Cyclic prerequisite", new GamePath(LocationId.Game, "Data/Cyclic.esp"));
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(mod.Id, LoadoutItem.ParentId, mod.LoadoutItemGroupId);
            await tx.Commit();
        }

        var resolver = ServiceProvider.GetRequiredService<IFomodDependencyResolver>();
        var action = () => resolver.Capture(loadout.Rebase());
        action.Should().Throw<InvalidDataException>().WithMessage("*parent cycle*");
    }

    private Task<LoadoutItemGroup.ReadOnly> AddMod(LoadoutId loadout, string name, GamePath path, LoadoutItemGroupId? parent = null)
        => LoadoutManager.InstallItemWrapper(loadout, tx =>
        {
            var group = AddEmptyGroup(tx, loadout, name, parent);
            var bytes = MasterBytes();
            _ = new LoadoutFile.New(tx, out var id)
            {
                Hash = bytes.AsSpan().xxHash3(), Size = Size.FromLong(bytes.Length),
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = path.ToGamePathParentTuple(loadout),
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = path.FileName, LoadoutId = loadout, ParentId = group },
                },
            };
            return Task.FromResult(group);
        });

    private static byte[] MasterBytes()
    {
        using var plugin = new MemoryStream();
        using var writer = new BinaryWriter(plugin, Encoding.UTF8, leaveOpen: true);
        writer.Write("TES4"u8);
        writer.Write(18u);
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)44);
        writer.Write((ushort)0);
        writer.Write("HEDR"u8);
        writer.Write((ushort)12);
        writer.Write(1.7f);
        writer.Write(0u);
        writer.Write(0x800u);
        writer.Flush();
        return plugin.ToArray();
    }
}
