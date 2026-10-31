using System.Collections.Frozen;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FOMOD;

/// <summary>Captures the intended target profile without applying files to the game directory.</summary>
public interface IFomodDependencyResolver
{
    /// <summary>The optional collection is staged disabled while its children are installed.</summary>
    FomodDependencySnapshot Capture(Loadout.ReadOnly loadout, LoadoutItemGroupId? installingCollection = null);
}

/// <summary>An immutable dependency view owned by one installer execution.</summary>
public sealed class FomodDependencySnapshot(IEnumerable<GamePath> presentFiles, IEnumerable<GamePath> activeFiles, Version? gameVersion)
{
    /// <summary>Available files, including disabled mods, excluding effective deletions.</summary>
    public FrozenSet<GamePath> PresentFiles { get; } = presentFiles.ToFrozenSet();
    /// <summary>Files that the profile would deploy.</summary>
    public FrozenSet<GamePath> ActiveFiles { get; } = activeFiles.ToFrozenSet();
    /// <summary>The primary executable's version, or null if it cannot be determined.</summary>
    public Version? GameVersion { get; } = gameVersion;
}
