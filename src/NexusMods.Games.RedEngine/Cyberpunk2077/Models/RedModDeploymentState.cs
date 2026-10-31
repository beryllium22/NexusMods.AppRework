using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.RedEngine.Cyberpunk2077.Models;

/// <summary>
/// Deployment completion belongs to the loadout, separately from its physical file baseline.
/// An interrupted deployment must remain retryable after the app restarts.
/// </summary>
[Include<Loadout>]
public partial class RedModDeploymentState : IModelDefinition
{
    private const string Namespace = "NexusMods.Games.RedEngine.Cyberpunk2077.RedModDeploymentState";

    public static readonly MarkerAttribute Pending = new(Namespace, nameof(Pending));

    /// <summary>
    /// Fingerprint of the order and winning REDmod files from the last successful deployment.
    /// Empty means that no REDmods were enabled.
    /// </summary>
    public static readonly StringAttribute InputFingerprint = new(Namespace, nameof(InputFingerprint)) { IsOptional = true };
}
