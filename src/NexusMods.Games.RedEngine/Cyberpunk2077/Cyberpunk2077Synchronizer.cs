using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Settings;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Games.RedEngine.Cyberpunk2077.Models;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using R3;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

public class Cyberpunk2077Synchronizer : ALoadoutSynchronizer
{
    private Cyberpunk2077Settings _settings;
    
    /// <summary>
    /// Redmod deploys combined mods to the redmod cache folder
    /// </summary>
    private static GamePath RedModCacheFolder => new(LocationId.Game, "r6/cache/modded");
    
    /// <summary>
    /// Redmod stages the scripts in the redmod/scripts folder
    /// </summary>
    private static GamePath RedModScriptsFolder => new(LocationId.Game, "tools/redmod/scripts");
    
    /// <summary>
    /// Redmod stages the tweaks in the redmod/tweaks folder
    /// </summary>
    private static GamePath RedModTweaksFolder => new(LocationId.Game, "tools/redmod/tweaks");
    
    private static GamePath ArchivePcContentFolder => new(LocationId.Game, "archive/pc/content");
    
    private static GamePath ArchivePcEp1Folder => new(LocationId.Game, "archive/pc/ep1");
    
    
    private readonly RedModDeployTool _redModTool;

    protected override bool RecordAppliedDateTime => false;

    public override bool HasPendingDeployment(Loadout.ReadOnly loadout)
    {
        if (loadout.Contains(RedModDeploymentState.Pending)) return true;
        var currentInputs = _redModTool.GetInputFingerprint(loadout);
        var deployedInputs = RedModDeploymentState.InputFingerprint.TryGetValue(loadout, out var fingerprint)
            ? fingerprint
            : string.Empty;
        return currentInputs != deployedInputs;
    }
    
    protected internal Cyberpunk2077Synchronizer(IServiceProvider provider) : base(provider)
    {
        var settingsManager = provider.GetRequiredService<ISettingsManager>();

        _settings = settingsManager.Get<Cyberpunk2077Settings>();
        settingsManager.GetChanges<Cyberpunk2077Settings>(prependCurrent: false).Subscribe(value => _settings = value);
        _redModTool = provider.GetServices<ITool>().OfType<RedModDeployTool>().First();
    }

    private static readonly GamePath[] IgnoredBackupFolders =
    [
        ArchivePcContentFolder,
        ArchivePcEp1Folder,
    ];

    public override async Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job)
    {
        var cancellationToken = job?.CancellationToken ?? CancellationToken.None;
        cancellationToken.ThrowIfCancellationRequested();
        loadout = loadout.Rebase();

        // Check the intended enabled mods before changing game files. Physical directories do
        // not describe a pending install, a disabled mod, or a different loadout.
        if (MissingRedModEmitter.HasRedMods(loadout, out _, out _) &&
            !MissingRedModEmitter.HasRedModToolInstalled(loadout, out var redModPath))
            throw new FileNotFoundException("This loadout requires the REDmod DLC. Install it, then Apply again, or disable the REDmods in this loadout.", redModPath.ToString());

        // File synchronization must retain its truthful disk baseline if deployment later fails.
        // Keep completion separate so an empty file diff cannot hide a failed/interrupted deploy.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, RedModDeploymentState.Pending, Null.Instance);
            await tx.Commit();
        }

        loadout = await base.Synchronize(loadout, job);
        cancellationToken.ThrowIfCancellationRequested();
        // The file transaction uses an immutable input snapshot, but its returned loadout is
        // rebased. An edit during copying must not deploy old bytes and record the newer inputs.
        if (!_redModTool.InputFilesMatchDisk(loadout, GetDiskStateForGame(loadout.Installation)))
            throw new InvalidOperationException("The REDmod files changed while Apply was copying them. Apply again to deploy the updated loadout.");
        var deployedInputs = _redModTool.GetInputFingerprint(loadout);
        if (deployedInputs.Length != 0)
        {
            job?.SetStatus("Deploying REDmods");
            await _redModTool.Execute(loadout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Ingest the generated files only after REDmod exits successfully.
            loadout = await base.Synchronize(loadout, job);
        }

        cancellationToken.ThrowIfCancellationRequested();
        loadout = loadout.Rebase();
        if (_redModTool.GetInputFingerprint(loadout) != deployedInputs)
            throw new InvalidOperationException("The REDmod files or load order changed during deployment. Apply again to deploy the updated loadout.");

        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(loadout.Id, RedModDeploymentState.Pending, Null.Instance);
            tx.Add(loadout.Id, RedModDeploymentState.InputFingerprint, deployedInputs);
            tx.Add(loadout.Id, Loadout.LastAppliedDateTime, DateTime.UtcNow);
            await tx.Commit();
        }

        return loadout.Rebase();
    }


    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_settings.DoFullGameBackup)
            return false;
        
        if (path.LocationId != LocationId.Game)
            return false;
        
        return IgnoredBackupFolders.Any(ignore => path.Path.InFolder(ignore.Path));
    }

}
