using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Mutagen.Bethesda.Plugins;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Emitters;

public class MissingMasterEmitter(ICreationEngineGame game) : ILoadoutDiagnosticEmitter
{
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var synchronizer = game.Synchronizer;
        var latest = synchronizer.GetLastScannedDiskState(loadout.Installation);
        var previous = GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(loadout.Installation, out _)
            ? synchronizer.GetPreviouslyAppliedDiskState(loadout.Installation)
            : latest;
        var tree = synchronizer.BuildSyncTree(latest, previous, loadout);
        synchronizer.ProcessSyncTree(tree);
        return Diagnose(loadout, tree.ToFrozenDictionary(), cancellationToken);
    }

    public async IAsyncEnumerable<Diagnostic> Diagnose(
        Loadout.ReadOnly loadout,
        FrozenDictionary<GamePath, SyncNode> syncTree,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var order = await PluginLoadOrder.Read(game, syncTree, loadout.InstallationInstance.Locations, cancellationToken);
        var disabledPlugins = new Dictionary<string, LoadoutItemGroupReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = file.AsLoadoutItemWithTargetPath().AsLoadoutItem();
            var path = (GamePath)file.AsLoadoutItemWithTargetPath().TargetPath;
            if (path.Parent != KnownPaths.Data || !KnownCEExtensions.PluginFiles.Contains(path.Extension)) continue;
            if (item.IsEnabled() || !item.HasParent()) continue;
            disabledPlugins.TryAdd(path.FileName.ToString(), new LoadoutItemGroupReference
            {
                TxId = loadout.Db.BasisTxId,
                DataId = item.ParentId,
            });
        }

        foreach (var problem in order.Problems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = GetOwningGroup(loadout, problem.Node);
            if (problem.MissingMaster is { } missingMaster && owner is not null)
            {
                var plugin = ModKey.FromFileName(problem.Path.FileName.ToString());
                if (disabledPlugins.TryGetValue(missingMaster.FileName.ToString(), out var disabled))
                    yield return Diagnostics.CreateMissingMasterIsDisabled(owner, plugin, missingMaster, disabled);
                else
                    yield return Diagnostics.CreateMissingMaster(owner, plugin, missingMaster);
            }
            else
            {
                // Game files and newly discovered disk files do not necessarily have a mod group.
                // A loadout reference remains valid in those cases instead of inventing a group ID.
                yield return Diagnostics.CreatePluginLoadOrderProblem(new LoadoutReference
                {
                    TxId = loadout.Db.BasisTxId,
                    DataId = loadout.LoadoutId,
                }, problem.Path.FileName.ToString(), problem.Message);
            }
        }
    }

    private static LoadoutItemGroupReference? GetOwningGroup(Loadout.ReadOnly loadout, SyncNode node)
    {
        if (!node.Loadout.HaveEntityId) return null;
        var item = LoadoutItem.Load(loadout.Db, node.Loadout.EntityId);
        if (!item.IsValid() || !item.HasParent() || !item.Parent.IsValid()) return null;
        return new LoadoutItemGroupReference
        {
            TxId = loadout.Db.BasisTxId,
            DataId = item.ParentId,
        };
    }
}
