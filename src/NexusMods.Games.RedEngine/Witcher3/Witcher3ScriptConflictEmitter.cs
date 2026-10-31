using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.RedEngine.Witcher3;

public sealed class Witcher3ScriptConflictEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(
        Loadout.ReadOnly loadout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Use the intended state, including pending changes and pre-management manual
        // mods retained across profiles. Disk alone would describe the previous profile.
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        var intended = synchronizer.BuildSyncTree(Array.Empty<PathPartPair>(), Array.Empty<PathPartPair>(), loadout);
        var files = intended
            .Where(pair => pair.Key.LocationId == LocationId.Game && pair.Value.HaveLoadout
                && pair.Value.SourceItemType is LoadoutSourceItemType.Loadout or LoadoutSourceItemType.Game)
            .Select(pair => (pair.Key.Path, pair.Value.Loadout.Hash));

        foreach (var conflict in FindConflicts(files))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Witcher3Diagnostics.CreateOverlappingScripts(
                ScriptPath: conflict.ScriptPath,
                ModFolders: string.Join(", ", conflict.ModFolders));
        }

        await Task.CompletedTask;
    }

    internal readonly record struct ScriptConflict(string ScriptPath, string[] ModFolders);

    internal static IEnumerable<ScriptConflict> FindConflicts(IEnumerable<(RelativePath Path, Hash Hash)> files)
    {
        var scripts = new Dictionary<string, List<(string Folder, Hash Hash)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var parts = file.Path.ToString().Split('/');
            if (parts.Length < 5 || !parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
                || !parts[1].StartsWith("mod", StringComparison.OrdinalIgnoreCase)
                || !parts[2].Equals("content", StringComparison.OrdinalIgnoreCase)
                || !parts[3].Equals("scripts", StringComparison.OrdinalIgnoreCase)
                || !parts[^1].EndsWith(".ws", StringComparison.OrdinalIgnoreCase)) continue;

            var path = string.Join('/', parts.Skip(3));
            if (!scripts.TryGetValue(path, out var variants)) scripts[path] = variants = [];
            variants.Add((parts[1], file.Hash));
        }

        foreach (var (path, variants) in scripts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folders = variants.Select(variant => variant.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (folders.Length < 2 || variants.Select(variant => variant.Hash).Distinct().Count() < 2) continue;
            yield return new ScriptConflict(path, folders);
        }
    }
}
