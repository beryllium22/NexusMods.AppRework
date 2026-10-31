using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests;

internal static class CreationEngineTestEnvironment
{
    public static IServiceCollection AddIsolatedCreationEngineLocator<TGame>(this IServiceCollection services)
        where TGame : class, ICreationEngineGame
    {
        return services
            .AddSingleton<IFileSystem>(provider =>
            {
                var root = provider.GetRequiredService<TemporaryFileManager>().CreateFolder("CreationEngineKnownPaths").Path;
                return NexusMods.Paths.FileSystem.Shared.CreateOverlayFileSystem(pathMappings: [], knownPathMappings: new()
                {
                    [KnownPath.LocalApplicationDataDirectory] = root.Combine("AppData/Local"),
                    [KnownPath.MyGamesDirectory] = root.Combine("Documents/My Games"),
                });
            })
            .AddSingleton<IGameLocator, IsolatedLocator<TGame>>();
    }

    private sealed class IsolatedLocator<TGame> : IGameLocator, IDisposable where TGame : class, ICreationEngineGame
    {
        private readonly TGame _game;
        private readonly TemporaryPath _directory;
        private readonly AbsolutePath _gamePath;

        public IsolatedLocator(TGame game, IFileSystem fileSystem, TemporaryFileManager temporaryFileManager)
        {
            _game = game;
            _directory = temporaryFileManager.CreateFolder(typeof(TGame).Name);
            // GameRegistry deliberately uses LocatorResult.Path.FileSystem, so the path
            // must carry the overlay as well as the service provider registration.
            _gamePath = fileSystem.FromUnsanitizedFullPath(_directory.Path.GetFullPath());
        }

        public IEnumerable<GameLocatorResult> Locate()
        {
            yield return new GameLocatorResult
            {
                Game = _game,
                Locator = this,
                LocatorIds = [LocatorId.From("StubbedGameState.zip")],
                Store = GameStore.Unknown,
                StoreIdentifier = "StubbedGameState.zip",
                Path = _gamePath,
            };
        }

        public void Dispose() => _directory.Dispose();
    }
}
