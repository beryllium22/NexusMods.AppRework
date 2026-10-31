using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.RedEngine.Cyberpunk2077.Extensions;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

public class RunCyberpunk2077Game(IServiceProvider provider, Cyberpunk2077Game game) : RunGameTool<Cyberpunk2077Game>(provider, game)
{
    public override Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken, string[]? commandLineArgs)
    {
        cancellationToken.ThrowIfCancellationRequested();
        loadout = loadout.Rebase();
        if (game.Synchronizer.HasPendingDeployment(loadout))
            throw new InvalidOperationException("REDmod deployment is incomplete or has changed. Apply this loadout successfully before launching Cyberpunk 2077.");

        commandLineArgs ??= [];
        if (RedModExtensions.RetrieveEnabledRedModFolders(loadout).Length != 0 &&
            !commandLineArgs.Contains("-modded", StringComparer.OrdinalIgnoreCase))
            commandLineArgs = commandLineArgs.Append("-modded").ToArray();
        return base.Execute(loadout, cancellationToken, commandLineArgs);
    }
}
