using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Games.FOMOD;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Generic;

/// <summary>Uses the same intended file winners and retained base files as deployment.</summary>
public sealed class FomodDependencyResolver : IFomodDependencyResolver
{
    public FomodDependencySnapshot Capture(Loadout.ReadOnly loadout, LoadoutItemGroupId? installingCollection = null)
    {
        var installation = loadout.InstallationInstance;
        var game = installation.GetGame();
        var files = LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().ToArray();
        var ancestry = files.ToDictionary(file => file.Id, file => ReadAncestry(file, loadout, installingCollection));
        var tree = game.Synchronizer.BuildSyncTree(Array.Empty<PathPartPair>(), Array.Empty<PathPartPair>(), loadout);
        var active = tree.Where(pair => pair.Value.HaveLoadout).Select(pair => pair.Key).ToHashSet();
        var deleted = loadout.Db.Connection.Query<(LocationId Location, RelativePath Path)>(
                $"SELECT Path.Location, Path.Path FROM synchronizer.WinningFiles({loadout.Db}) WHERE Loadout = {loadout.Id} AND ItemType = 'Deleted'")
            .Select(pair => new GamePath(pair.Location, pair.Path)).ToHashSet();

        if (installingCollection.HasValue)
        {
            var collection = LoadoutItemGroup.Load(loadout.Db, installingCollection.Value);
            if (!collection.IsValid() || collection.AsLoadoutItem().LoadoutId != loadout.LoadoutId)
                throw new InvalidDataException("The installing collection does not belong to the target profile.");
            var pendingPaths = files.Where(file => ancestry[file.Id].IsInInstallingCollection)
                .Select(file => (GamePath)file.TargetPath).ToHashSet();
            var priorities = LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout)
                .GroupBy(priority => priority.TargetId).ToDictionary(group => group.Key, group => group.Max(priority => priority.Priority.Value));

            // The published sync tree deliberately excludes a disabled staged collection.
            // Resolve only its affected paths again, retaining the same override layer and
            // mod priorities, while ignoring this one temporary disabled parent. No DB or
            // on-disk state is changed to evaluate collection prerequisites.
            var candidates = files.Where(file => pendingPaths.Contains((GamePath)file.TargetPath)
                    && (file.TryGetAsLoadoutFile(out _) || file.TryGetAsDeletedFile(out _))
                    && ancestry[file.Id].IsEnabled)
                .GroupBy(file => (GamePath)file.TargetPath);
            foreach (var group in candidates)
            {
                if (tree.TryGetValue(group.Key, out var existing) && existing.SourceItemType == LoadoutSourceItemType.Intrinsic) continue;
                var winner = group.OrderByDescending(file => file.AsLoadoutItem().Parent.Contains(LoadoutOverridesGroup.OverridesFor))
                    .ThenByDescending(file => priorities.GetValueOrDefault(file.AsLoadoutItem().ParentId))
                    .ThenBy(file => file.Id).First();
                if (winner.TryGetAsDeletedFile(out _))
                {
                    active.Remove(group.Key);
                    deleted.Add(group.Key);
                }
                else
                {
                    active.Add(group.Key);
                    deleted.Remove(group.Key);
                }
            }
        }

        var present = active.ToHashSet();
        foreach (var file in files)
        {
            var path = (GamePath)file.TargetPath;
            if (file.TryGetAsLoadoutFile(out _) && !deleted.Contains(path)) present.Add(path);
        }
        var version = game.GetLocalVersion(installation);
        return new FomodDependencySnapshot(present, active, version.HasValue ? version.Value : null);
    }

    private readonly record struct Ancestry(bool IsEnabled, bool IsInInstallingCollection);

    private static Ancestry ReadAncestry(LoadoutItemWithTargetPath.ReadOnly file, Loadout.ReadOnly loadout,
        LoadoutItemGroupId? installingCollection)
    {
        if (file.TargetPath.Item1 != loadout.Id)
            throw new InvalidDataException($"File '{file.AsLoadoutItem().Name}' has a target path owned by another profile.");

        var visited = new HashSet<EntityId>();
        var current = file.AsLoadoutItem();
        var enabled = true;
        var inCollection = false;
        while (true)
        {
            if (!visited.Add(current.Id))
                throw new InvalidDataException($"The profile contains a parent cycle involving '{current.Name}'. Repair the affected mod group before installing this FOMOD.");
            if (!current.IsValid() || current.LoadoutId != loadout.LoadoutId)
                throw new InvalidDataException($"File '{file.AsLoadoutItem().Name}' has a missing parent or a parent from another profile.");

            var isInstallingCollection = installingCollection.HasValue && current.Id == installingCollection.Value.Value;
            inCollection |= isInstallingCollection;
            enabled &= !current.IsDisabled || isInstallingCollection;
            if (!LoadoutItem.Parent.TryGetValue(current, out var parentId)) break;
            var parent = LoadoutItemGroup.Load(loadout.Db, parentId);
            if (!parent.IsValid())
                throw new InvalidDataException($"File '{file.AsLoadoutItem().Name}' references a missing or invalid parent group.");
            current = parent.AsLoadoutItem();
        }
        return new Ancestry(enabled, inCollection);
    }

}
