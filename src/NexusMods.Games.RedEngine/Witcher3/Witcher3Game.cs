using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Games.RedEngine.Witcher3;

/// <summary>
/// Initial support for ordinary Mods and custom DLC packages. User documents, Workshop
/// subscriptions, menu merging and mods.settings are deliberately outside managed locations.
/// </summary>
public sealed class Witcher3Game : IGame, IGameData<Witcher3Game>
{
    public static GameId GameId { get; } = GameId.From("RedEngine.Witcher3");
    public static string DisplayName => "The Witcher 3: Wild Hunt";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(952);

    // Store identifiers and executable paths checked against Nexus-Mods/vortex-games,
    // game-witcher3/index.ts at cf98b44ab56b958660d4a688fd95beacd726ddff (GPL-3.0).
    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [292030u],
        GOGProductIds = [1207664643L, 1495134320L],
    };

    // Reuse App.UI/Assets/mod-thumbnail-fallback.png; this neutral repository asset
    // is a placeholder, not official game artwork.
    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<Witcher3Game>("NexusMods.Games.RedEngine.Resources.Witcher3.placeholder.png");
    public IStreamFactory TileImage { get; } = new EmbeddedResourceStreamFactory<Witcher3Game>("NexusMods.Games.RedEngine.Resources.Witcher3.placeholder.png");

    internal static readonly GamePath DirectX11Executable = new(LocationId.Game, "bin/x64/witcher3.exe");
    internal static readonly GamePath DirectX12Executable = new(LocationId.Game, "bin/x64_DX12/witcher3.exe");

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    private readonly Lazy<ISortOrderManager> _sortOrderManager;

    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public Witcher3Game(IServiceProvider provider)
    {
        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new Witcher3Synchronizer(provider));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var manager = provider.GetRequiredService<SortOrderManager>();
            manager.RegisterSortOrderVarieties([], this);
            return manager;
        });

        LibraryItemInstallers = [new Witcher3ArchiveInstaller(provider)];
        DiagnosticEmitters =
        [
            new NoWayToSourceFilesOnDisk(),
            new UndeployableLoadoutDueToMissingGameFiles(provider),
            new Witcher3ScriptConflictEmitter(),
        ];
    }

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => new Dictionary<LocationId, AbsolutePath>
        {
            [LocationId.Game] = gameLocatorResult.Path,
        }.ToImmutableDictionary();

    public GamePath GetPrimaryFile(GameInstallation installation)
    {
        // Classic installs only have x64. Updated installs may have both renderers, or
        // only x64_DX12. Do not select a missing classic executable on a DX12-only install.
        if (installation.Locations.ToAbsolutePath(DirectX11Executable).FileExists)
            return DirectX11Executable;

        return installation.Locations.ToAbsolutePath(DirectX12Executable).FileExists
            ? DirectX12Executable
            : DirectX11Executable;
    }
}
