using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using Xunit.Abstractions;

namespace NexusMods.DataModel.Synchronizer.Tests;

public class NestedEnabledStateTests(ITestOutputHelper output) : ACyberpunkIsolatedGameTest<NestedEnabledStateTests>(output)
{
    [Fact]
    public async Task DisablingAnyNestedAncestorRemovesItsFilesAndReenablingRestoresThem()
    {
        var loadout = await CreateLoadout();
        var outer = await CreateCollection(loadout, "Outer collection");
        var inner = await CreateCollection(loadout, "Inner collection");
        LoadoutItemGroupId folder;
        var nested = new GamePath(LocationId.Game, "bin/nested/example.txt");
        var sibling = new GamePath(LocationId.Game, "bin/independent/example.txt");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(inner.Id, LoadoutItem.Parent, outer.Id);
            folder = AddEmptyGroup(tx, loadout, "Nested folder", inner.AsLoadoutItemGroup().LoadoutItemGroupId);
            await AddModAsync(tx, [nested.Path], loadout, "Nested mod", parentGroup: folder);
            await AddModAsync(tx, [sibling.Path], loadout, "Independent mod");
            var result = await tx.Commit();
            folder = LoadoutItemGroupId.From(result[folder]);
        }
        await SynchronizerService.Synchronize(loadout.Rebase());

        var nestedPath = GameInstallation.Locations.ToAbsolutePath(nested);
        var siblingPath = GameInstallation.Locations.ToAbsolutePath(sibling);
        (await nestedPath.ReadAllTextAsync()).Should().Be(nested.Path.ToString());

        foreach (var ancestor in new[] { outer.Id, inner.Id, folder.Value })
        {
            await DisableItem(ancestor);
            await SynchronizerService.Synchronize(loadout.Rebase());
            nestedPath.FileExists.Should().BeFalse("every ancestor participates in effective enabled state");
            (await siblingPath.ReadAllTextAsync()).Should().Be(sibling.Path.ToString());

            await EnableItem(ancestor);
            await SynchronizerService.Synchronize(loadout.Rebase());
            (await nestedPath.ReadAllTextAsync()).Should().Be(nested.Path.ToString());
            siblingPath.FileExists.Should().BeTrue();
        }
    }

    [Fact]
    public async Task CyclicAndCrossProfileParentsAreExcludedFromEnabledGroupsAndWinners()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        LoadoutItemGroupId first;
        LoadoutItemGroupId second;
        LoadoutItemGroupId foreign;
        using (var tx = Connection.BeginTransaction())
        {
            first = AddEmptyGroup(tx, loadout, "Cycle first");
            second = AddEmptyGroup(tx, loadout, "Cycle second", first);
            foreign = AddEmptyGroup(tx, other, "Foreign parent");
            tx.Add(first, LoadoutItem.Parent, second);
            await AddModAsync(tx, [(RelativePath)"bin/cyclic.txt"], loadout, "Cyclic child", parentGroup: first);
            await AddModAsync(tx, [(RelativePath)"bin/cross-profile.txt"], loadout, "Foreign child", parentGroup: foreign);
            var result = await tx.Commit();
            first = LoadoutItemGroupId.From(result[first]);
            second = LoadoutItemGroupId.From(result[second]);
        }

        var current = loadout.Rebase();
        var enabled = Connection.Query<(EntityId Id, bool IsEnabled)>(
            $"SELECT Id, IsEnabled FROM synchronizer.LoadoutGroups({current.Db}) WHERE Loadout = {current.LoadoutId}").ToArray();
        // Framework-managed roots such as My Mods and Overrides remain valid. Check the
        // malformed groups directly instead of assuming how many framework roots exist.
        enabled.Should().Contain(group => group.Id == first.Value && !group.IsEnabled);
        enabled.Should().Contain(group => group.Id == second.Value && !group.IsEnabled);
        var tree = Synchronizer.BuildSyncTree(Array.Empty<NexusMods.Abstractions.Loadouts.Synchronizers.PathPartPair>(),
            Array.Empty<NexusMods.Abstractions.Loadouts.Synchronizers.PathPartPair>(), current);
        tree.Keys.Should().NotContain(new GamePath(LocationId.Game, "bin/cyclic.txt"));
        tree.Keys.Should().NotContain(new GamePath(LocationId.Game, "bin/cross-profile.txt"));
    }
}
