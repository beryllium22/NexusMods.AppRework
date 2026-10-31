using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Backend.Games;

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal class GameRegistry : IGameRegistry
{
    private readonly ILogger _logger;
    private readonly IConnection _connection;

    private readonly Lazy<IGameLocator[]> _locators;
    private readonly Lock _lock = new();

    private Optional<ImmutableArray<GameInstallation>> _cachedInstallations = Optional<ImmutableArray<GameInstallation>>.None;

    public GameRegistry(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<GameRegistry>>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _locators = new Lazy<IGameLocator[]>(() => serviceProvider.GetServices<IGameLocator>().ToArray());
    }

    public void ClearCache()
    {
        _cachedInstallations = Optional<ImmutableArray<GameInstallation>>.None;
    }

    public ImmutableArray<GameInstallation> LocateGameInstallations()
    {
        if (_cachedInstallations.HasValue) return _cachedInstallations.Value;

        lock (_lock)
        {
            if (_cachedInstallations.HasValue) return _cachedInstallations.Value;

            var results = new List<GameInstallation>();

            _logger.LogDebug("Using {Count} game locators", _locators.Value.Length);
            foreach (var locator in _locators.Value)
            {
                try
                {
                    foreach (var locatorResult in locator.Locate())
                    {
                        try
                        {
                            var resolvedLocations = locatorResult.Game.GetLocations(locatorResult.Path.FileSystem, locatorResult);
                            var gameLocations = GameLocations.Create(resolvedLocations);

                            var installation = new GameInstallation(locatorResult, gameLocations);
                            results.Add(installation);

                            if (_logger.IsEnabled(LogLevel.Information))
                            {
                                var locatorIds = installation.LocatorResult.LocatorIds
                                    .Select(x => x.Value)
                                    .Order(StringComparer.OrdinalIgnoreCase)
                                    .Aggregate((a,b) => $"{a}, {b}");

                                _logger.LogInformation("Found game installation for '{Game}' installed using '{Store}' at '{Path}' with locator IDs {LocatorIds}", installation.Game.DisplayName, installation.LocatorResult.Store, installation.LocatorResult.Path, locatorIds);
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "Exception while locating a game with locator {Locator}", locator.GetType());
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Exception while locating games with locator {Locator}", locator.GetType());
                }
            }

            var installations = results.ToImmutableArray();
            _cachedInstallations = installations;

            _logger.LogInformation("Found {Count} game installations", installations.Length);
            return installations;
        }
    }

    public bool TryGetGameInstallation(Loadout.ReadOnly loadout, [NotNullWhen(true)] out GameInstallation? gameInstallation)
    {
        gameInstallation = null;
        if (!loadout.IsValid()) return false;

        var metadata = loadout.Installation;
        if (!metadata.IsValid()) return false;

        var candidates = LocateGameInstallations()
            .Where(installation => installation.Game.NexusModsGameId == metadata.GameId && installation.LocatorResult.Store == metadata.Store)
            .DistinctBy(InstallationPath)
            .ToArray();

        foreach (var installation in candidates)
        {
            if (!MatchesPath(metadata.Path, InstallationPath(installation))) continue;

            gameInstallation = installation;
            return true;
        }

        // Preserve relocation only when neither discovered copies nor saved metadata can
        // refer to a different installation. An orphan must not silently attach to one.
        if (candidates.Length == 1 && GameInstallMetadata.FindByGameId(_connection.Db, metadata.GameId)
                .Count(candidate => candidate.IsValid() && candidate.Store == metadata.Store) == 1)
        {
            gameInstallation = candidates[0];
            return true;
        }

        return false;
    }

    public bool TryGetMetadata(GameInstallation installation, out GameInstallMetadata.ReadOnly result)
    {
        // TODO: use game id instead of nexus mods game id
        var gameId = installation.Game.NexusModsGameId;
        if (!gameId.HasValue)
        {
            result = default(GameInstallMetadata.ReadOnly);
            return false;
        }

        var allMetadata = GameInstallMetadata.FindByGameId(_connection.Db, gameId.Value)
            .Where(metadata => metadata.IsValid() && metadata.Store == installation.LocatorResult.Store)
            .ToArray();
        var path = InstallationPath(installation);
        foreach (var metadata in allMetadata)
        {
            if (!MatchesPath(metadata.Path, path)) continue;
            result = metadata;
            return true;
        }

        // A new second copy needs its own metadata even when only the first copy has
        // been managed. Relocation is safe only if discovery now finds this copy alone.
        if (allMetadata.Length == 1)
        {
            var candidates = LocateGameInstallations()
                .Where(candidate => candidate.Game.NexusModsGameId == gameId && candidate.LocatorResult.Store == installation.LocatorResult.Store)
                .DistinctBy(InstallationPath)
                .ToArray();
            if (candidates.Length == 1 && InstallationPath(candidates[0]) == path)
            {
                result = allMetadata[0];
                return true;
            }
        }

        result = default(GameInstallMetadata.ReadOnly);
        return false;
    }

    private static AbsolutePath InstallationPath(GameInstallation installation)
        => installation.Locations[LocationId.Game].Path;

    private static bool MatchesPath(string savedPath, AbsolutePath path)
        => path.FileSystem.FromUnsanitizedFullPath(savedPath) == path;
}
