using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine;

public abstract class ACreationEngineSynchronizer : ALoadoutSynchronizer
{
    private readonly Dictionary<GamePath, IIntrinsicFile> _intrinsicFiles;
    private readonly PluginsFile _pluginsFile;
    protected override bool PreserveInitialDiskState => true;
    protected ACreationEngineSynchronizer(IServiceProvider provider, ICreationEngineGame game) : base(provider)
    {
        _pluginsFile = new PluginsFile(provider.GetRequiredService<ILogger<PluginsFile>>(), game);
        _intrinsicFiles = new Dictionary<GamePath, IIntrinsicFile>()
        {
            {_pluginsFile.Path, _pluginsFile},
        };
    }

    
    private static readonly GamePath SavesPath = new GamePath(LocationId.Preferences, "Saves");
    protected override IGamePathFilter GamePathFilter { get; } = NexusMods.Abstractions.Loadouts.Synchronizers.GamePathFilters.Create(path => path.InFolder(SavesPath));

    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly _) => _intrinsicFiles;

    protected override Task ValidateSyncTree(Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken)
        => _pluginsFile.Validate(loadout, syncTree, cancellationToken);
    
    public override bool IsIgnoredBackupPath(GamePath path)
    {
        // Don't backup BSA files by default
        return path.Extension == KnownCEExtensions.BSA || path.Extension == KnownCEExtensions.BA2;
    }
}
