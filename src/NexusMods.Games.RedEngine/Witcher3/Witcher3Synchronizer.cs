using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.RedEngine.Witcher3;

public sealed class Witcher3Synchronizer(IServiceProvider provider) : ALoadoutSynchronizer(provider)
{
    private readonly Witcher3Settings _settings = provider.GetRequiredService<ISettingsManager>().Get<Witcher3Settings>();
    protected override bool PreserveInitialDiskState => true;
    protected override bool BackupRetainedBaselineFiles => _settings.DoFullGameBackup;
    private static readonly HashSet<string> OriginalDlcFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bob", "ep1", "dlc1", "dlc2", "dlc3", "dlc4", "dlc5", "dlc6", "dlc7", "dlc8",
        "dlc9", "dlc10", "dlc11", "dlc12", "dlc13", "dlc14", "dlc15", "dlc16",
    };

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_settings.DoFullGameBackup || path.LocationId != LocationId.Game) return false;

        var parts = path.Path.ToString().Split('/');
        return parts.Length > 1 && parts[0].Equals("content", StringComparison.OrdinalIgnoreCase)
            || parts.Length > 2 && parts[0].Equals("dlc", StringComparison.OrdinalIgnoreCase) && IsOriginalDlc(parts[1]);
    }

    /// <summary>
    /// CDPR documents these as original DLC, rather than custom DLC mod folders:
    /// https://cdprojektred.atlassian.net/wiki/spaces/W3REDkit/pages/6328346
    /// </summary>
    internal static bool IsOriginalDlc(string folder)
        => OriginalDlcFolders.Contains(folder);
}
