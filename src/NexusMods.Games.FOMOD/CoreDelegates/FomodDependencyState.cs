using System.Collections.Frozen;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FOMOD.CoreDelegates;

/// <summary>
/// An installation sees the target profile, including mods that have not been deployed yet.
/// Reading the game directory instead would inspect whichever profile was last applied.
/// </summary>
internal sealed class FomodDependencyState
{
    public static readonly FomodDependencyState Empty = new([], [], null);

    private readonly FrozenSet<RelativePath> _present;
    private readonly FrozenSet<RelativePath> _active;

    public Version? GameVersion { get; }

    private FomodDependencyState(IEnumerable<RelativePath> present, IEnumerable<RelativePath> active, Version? gameVersion)
    {
        _present = present.ToFrozenSet();
        _active = active.ToFrozenSet();
        GameVersion = gameVersion;
    }

    public static FomodDependencyState Create(FomodDependencySnapshot snapshot, GamePath installationPath)
    {
        IEnumerable<RelativePath> RelativePaths(IEnumerable<GamePath> paths) => paths
            .Where(path => path.LocationId == installationPath.LocationId && path.Path.InFolder(installationPath.Path))
            .Select(path => path.Path.RelativeTo(installationPath.Path));
        return new FomodDependencyState(RelativePaths(snapshot.PresentFiles), RelativePaths(snapshot.ActiveFiles), snapshot.GameVersion);
    }

    public bool IsPresent(string path) => TryNormalize(path, out var relativePath) && _present.Contains(relativePath);
    public bool IsActive(string path) => TryNormalize(path, out var relativePath) && _active.Contains(relativePath);

    public string[] GetPlugins(bool activeOnly) => (activeOnly ? _active : _present)
        .Where(path => path.Parent == RelativePath.Empty && path.Extension.ToString().ToLowerInvariant() is ".esp" or ".esm" or ".esl")
        .Select(path => path.ToString()).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool TryNormalize(string path, out RelativePath relativePath)
    {
        relativePath = default;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(part => part == "..")) return false;
        relativePath = RelativePath.FromUnsanitizedInput(normalized);
        return true;
    }
}
