using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine;

/// <summary>
/// The plugin files that will survive synchronization, and the hard ordering constraints
/// encoded in their headers. This does not apply LOOT metadata or resolve record conflicts.
/// </summary>
internal sealed class PluginLoadOrder
{
    internal sealed record Plugin(GamePath Path, SyncNode Node, IMod Header)
    {
        public ModKey Key => Header.ModKey;

        // In SSE and Fallout 4 the extension forces .esm/.esl files to load as masters.
        // An .esp may also be a master, but its light flag alone does not make it one.
        public bool IsMaster => Header.IsMaster || Path.Extension == KnownCEExtensions.ESM || Path.Extension == KnownCEExtensions.ESL;
    }

    internal sealed record Problem(GamePath Path, SyncNode Node, string Message, ModKey? MissingMaster = null, Exception? Error = null);

    public List<Problem> Problems { get; } = [];
    public List<Plugin> SortedPlugins { get; } = [];

    public static async Task<PluginLoadOrder> Read(
        ICreationEngineGame game,
        IReadOnlyDictionary<GamePath, SyncNode> syncTree,
        GameLocations locations,
        CancellationToken cancellationToken = default)
    {
        var result = new PluginLoadOrder();
        var plugins = new Dictionary<ModKey, Plugin>();

        foreach (var (path, node) in syncTree.OrderBy(pair => pair.Key.Path.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Parent != KnownPaths.Data || !KnownCEExtensions.PluginFiles.Contains(path.Extension)) continue;

            // A disabled/deleted file is not active just because it still exists on disk.
            // External changes that the synchronizer is about to ingest must be read from disk.
            if (node.Actions.HasFlag(Actions.AddReifiedDelete)) continue;
            if (!node.HaveLoadout && !node.Actions.HasFlag(Actions.IngestFromDisk)) continue;
            var hash = node.Actions.HasFlag(Actions.IngestFromDisk) ? node.Disk.Hash : node.Loadout.Hash;

            try
            {
                // Newly discovered game/manual files have not necessarily reached the file
                // store yet. Read those headers without requiring a previous applied loadout.
                var header = node.HaveDisk && node.Disk.Hash == hash
                    ? await game.ParsePlugin(locations.ToAbsolutePath(path))
                    : await game.ParsePlugin(hash, path.Path.FileName);
                cancellationToken.ThrowIfCancellationRequested();
                if (header is null)
                    throw new InvalidDataException("The plugin has no readable header.");

                if (!plugins.TryAdd(header.ModKey, new Plugin(path, node, header)))
                    result.Problems.Add(new Problem(path, node, $"More than one active file has the plugin name {header.ModKey.FileName}."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                result.Problems.Add(new Problem(path, node,
                    $"{path.FileName} has an unreadable plugin header or its archived contents are missing. Reinstall it from a valid archive before applying this profile.", Error: exception));
            }
        }

        var dependencies = plugins.Keys.ToDictionary(key => key, _ => new HashSet<ModKey>());
        var masters = plugins.Values.Where(plugin => plugin.IsMaster).Select(plugin => plugin.Key).ToArray();
        var official = game.OfficialPlugins.Where(plugins.ContainsKey).ToArray();
        var officialSet = official.ToHashSet();

        foreach (var plugin in plugins.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var required = dependencies[plugin.Key];
            foreach (var master in plugin.Header.MasterReferences)
            {
                if (plugins.ContainsKey(master.Master))
                    required.Add(master.Master);
                else
                    result.Problems.Add(new Problem(plugin.Path, plugin.Node,
                        $"{plugin.Key.FileName} requires {master.Master.FileName}, which is not active in this profile.", master.Master));
            }

            if (!plugin.IsMaster)
                required.UnionWith(masters);

            // Installed base-game/DLC plugins have a fixed order, ahead of user plugins.
            if (!officialSet.Contains(plugin.Key) && official.Length > 0)
                required.Add(official[^1]);
        }

        for (var index = 1; index < official.Length; index++)
            dependencies[official[index]].Add(official[index - 1]);

        var dependants = plugins.Keys.ToDictionary(key => key, _ => new List<ModKey>());
        foreach (var (key, required) in dependencies)
        foreach (var dependency in required)
            dependants[dependency].Add(key);

        // Explicit tie-breaking makes output independent of database/dictionary enumeration.
        var ready = new SortedSet<ModKey>(Comparer<ModKey>.Create((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.FileName.ToString(), right.FileName.ToString())));
        foreach (var (key, required) in dependencies)
            if (required.Count == 0) ready.Add(key);

        while (ready.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = ready.Min;
            ready.Remove(next);
            result.SortedPlugins.Add(plugins[next]);
            foreach (var dependant in dependants[next])
            {
                dependencies[dependant].Remove(next);
                if (dependencies[dependant].Count == 0) ready.Add(dependant);
            }
        }

        if (result.SortedPlugins.Count != plugins.Count)
        {
            var blocked = plugins.Values.Where(plugin => dependencies[plugin.Key].Count > 0)
                .OrderBy(plugin => plugin.Path.FileName.ToString(), StringComparer.OrdinalIgnoreCase).ToArray();
            result.Problems.Add(new Problem(blocked[0].Path, blocked[0].Node,
                $"Plugin dependencies contain a cycle or conflict with the game's master ordering. Check these plugins: {string.Join(", ", blocked.Select(plugin => plugin.Path.FileName))}."));
        }

        return result;
    }

    public void ThrowIfInvalid()
    {
        if (Problems.Count > 0)
            throw new InvalidDataException("The plugin load order could not be applied. " + string.Join(" ", Problems.Select(problem => problem.Message)),
                Problems.Select(problem => problem.Error).FirstOrDefault(error => error is not null));
    }
}
