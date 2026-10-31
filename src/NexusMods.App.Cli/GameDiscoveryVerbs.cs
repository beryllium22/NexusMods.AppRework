using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.CLI;

/// <summary>
/// Read-only checks for game discovery without opening the UI or managing an installation.
/// </summary>
public static class GameDiscoveryVerbs
{
    public const string CommandName = "diagnose-game-discovery";
    public const string SuccessMarker = "GameDiscovery: OK";

    public static IServiceCollection AddGameDiscoveryVerbs(this IServiceCollection services)
        => services.AddVerb(() => DiagnoseGameDiscovery);

    [Verb(CommandName, "Check that game locators can be constructed and enumerate installed games.")]
    private static async Task<int> DiagnoseGameDiscovery(
        [Injected] IEnumerable<IGameLocator> locators,
        [Injected] IRenderer renderer,
        [Injected] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var locatorCount = 0;
        var installationCount = 0;
        foreach (var locator in locators)
        {
            token.ThrowIfCancellationRequested();
            locatorCount++;
            foreach (var _ in locator.Locate())
            {
                token.ThrowIfCancellationRequested();
                installationCount++;
            }
        }

        // The CLI command handler converts construction/enumeration exceptions to a
        // nonzero exit code. Print success only after every locator has completed.
        token.ThrowIfCancellationRequested();
        await renderer.TextLine($"{SuccessMarker} Locators: {locatorCount} Installations: {installationCount}");
        return 0;
    }
}
