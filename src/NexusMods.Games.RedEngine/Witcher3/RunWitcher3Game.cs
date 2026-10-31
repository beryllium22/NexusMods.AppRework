using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Paths;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.RedEngine.Witcher3;

public sealed class RunWitcher3Game(IServiceProvider provider, Witcher3Game game) : RunGameTool<Witcher3Game>(provider, game)
{
    public override Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken, string[]? commandLineArgs)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return base.Execute(loadout, cancellationToken, commandLineArgs);
    }

    protected override ValueTask<AbsolutePath> GetGamePath(Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        var executable = installation.Locations.ToAbsolutePath(game.GetPrimaryFile(installation));
        if (!executable.FileExists)
            throw new FileNotFoundException("The Witcher 3 executable is missing. Verify the game files in Steam or GOG, then try launching again.", executable.ToString());

        return ValueTask.FromResult(executable);
    }
}
