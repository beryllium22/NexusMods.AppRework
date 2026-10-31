using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Backend;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NSubstitute;
using Xunit.Abstractions;

namespace NexusMods.DataModel.Tests;

public class GameRegistryInstallationTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GameRegistryInstallationTests>(helper)
{
    private static readonly NexusModsGameId TestGameId = NexusModsGameId.From(999999);

    [Fact]
    public async Task SameGameAndStoreCopiesResolveTheirOwnLoadoutsAndMetadata()
    {
        var firstPath = TestPath("first");
        var secondPath = TestPath("second");
        using var provider = CreateRegistry(firstPath, secondPath);
        var registry = provider.GetRequiredService<IGameRegistry>();
        var installations = registry.LocateGameInstallations();
        // Reverse insertion order so selecting the first matching metadata cannot pass.
        var second = await CreateSavedLoadout(secondPath);
        var first = await CreateSavedLoadout(firstPath);

        foreach (var loadout in new[] { first, second })
        {
            registry.TryGetGameInstallation(loadout, out var found).Should().BeTrue();
            found!.Locations[LocationId.Game].Path.ToString().Should().Be(loadout.Installation.Path);

            var installation = installations.Single(candidate => candidate.Locations[LocationId.Game].Path.ToString() == loadout.Installation.Path);
            registry.TryGetMetadata(installation, out var metadata).Should().BeTrue();
            metadata.Id.Should().Be(loadout.Installation.Id);
        }
    }

    [Fact]
    public async Task AnUnmanagedSecondCopyDoesNotReuseTheFirstCopiesMetadata()
    {
        var firstPath = TestPath("first");
        var secondPath = TestPath("second");
        using var provider = CreateRegistry(firstPath, secondPath);
        var registry = provider.GetRequiredService<IGameRegistry>();
        var first = await CreateSavedLoadout(firstPath);
        var secondInstallation = registry.LocateGameInstallations().Single(candidate => candidate.Locations[LocationId.Game].Path == secondPath);

        registry.TryGetMetadata(secondInstallation, out _).Should().BeFalse("the first copy is still installed and owns its saved metadata");
        registry.TryGetGameInstallation(first, out var found).Should().BeTrue();
        found!.Locations[LocationId.Game].Path.Should().Be(firstPath);
    }

    [Fact]
    public async Task AnAmbiguousMissingPathDoesNotSelectTheFirstInstalledCopy()
    {
        using var provider = CreateRegistry(TestPath("first"), TestPath("second"));
        var registry = provider.GetRequiredService<IGameRegistry>();
        var orphan = await CreateSavedLoadout(TestPath("missing"));

        registry.TryGetGameInstallation(orphan, out var found).Should().BeFalse();
        found.Should().BeNull();
        Loadout.Load(Connection.Db, orphan.Id).IsValid().Should().BeTrue("resolution must preserve the saved loadout");
    }

    [Fact]
    public async Task TheRemainingCopyDoesNotClaimAMissingCopiesLoadout()
    {
        var remainingPath = TestPath("remaining");
        using var provider = CreateRegistry(remainingPath);
        var registry = provider.GetRequiredService<IGameRegistry>();
        var orphan = await CreateSavedLoadout(TestPath("missing"));
        var remaining = await CreateSavedLoadout(remainingPath);

        registry.TryGetGameInstallation(orphan, out _).Should().BeFalse("the remaining installation already has distinct saved metadata");
        registry.TryGetGameInstallation(remaining, out var found).Should().BeTrue();
        registry.TryGetMetadata(found!, out var metadata).Should().BeTrue();
        metadata.Id.Should().Be(remaining.Installation.Id);
    }

    [Fact]
    public async Task AMovedCopyDoesNotChooseBetweenMultipleSavedInstallations()
    {
        using var provider = CreateRegistry(TestPath("new"));
        var registry = provider.GetRequiredService<IGameRegistry>();
        await CreateSavedLoadout(TestPath("old-first"));
        await CreateSavedLoadout(TestPath("old-second"));

        registry.TryGetMetadata(registry.LocateGameInstallations().Single(), out _).Should().BeFalse(
            "neither old installation can be identified as the owner of the new path");
    }

    [Fact]
    public async Task ASingleMovedInstallationKeepsItsUnambiguousSavedLoadout()
    {
        var oldPath = TestPath("old");
        var newPath = TestPath("new");
        using var provider = CreateRegistry(newPath);
        var registry = provider.GetRequiredService<IGameRegistry>();
        var loadout = await CreateSavedLoadout(oldPath);

        registry.TryGetGameInstallation(loadout, out var found).Should().BeTrue();
        found!.Locations[LocationId.Game].Path.Should().Be(newPath);
        registry.TryGetMetadata(found, out var metadata).Should().BeTrue();
        metadata.Id.Should().Be(loadout.Installation.Id);
        metadata.Path.Should().Be(oldPath.ToString(), "lookup does not rewrite saved installation state");
    }

    private AbsolutePath TestPath(string name)
        => FileSystem.GetKnownPath(KnownPath.TempDirectory).Combine($"registry-installation-tests/{name}");

    private ServiceProvider CreateRegistry(params AbsolutePath[] paths)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Installation resolution test game");
        game.NexusModsGameId.Returns((DynamicData.Kernel.Optional<NexusModsGameId>)TestGameId);
        game.GetLocations(Arg.Any<IFileSystem>(), Arg.Any<GameLocatorResult>())
            .Returns(call => ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, call.Arg<GameLocatorResult>().Path));

        var locator = Substitute.For<IGameLocator>();
        locator.Locate().Returns(paths.Select(path => new GameLocatorResult
        {
            Game = game,
            Path = path,
            Store = GameStore.Steam,
            Locator = locator,
            LocatorIds = [LocatorId.From("test-build")],
            StoreIdentifier = "test-game",
        }).ToArray());

        var services = new ServiceCollection()
            .AddLogging()
            // The outer fixture owns this real database connection.
            .AddSingleton(Connection)
            .AddGameServices();
        services.RemoveAll<IGameLocator>();
        services.AddSingleton(locator);
        return services.BuildServiceProvider();
    }

    private async Task<Loadout.ReadOnly> CreateSavedLoadout(AbsolutePath path)
    {
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            GameId = TestGameId,
            Store = GameStore.Steam,
            Path = path.ToString(),
            Name = "Installation resolution test game",
        };
        var loadout = new Loadout.New(tx)
        {
            Name = path.FileName,
            ShortName = "T",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
            LocatorIds = [LocatorId.From("test-build")],
        };
        return (await tx.Commit()).Remap(loadout);
    }
}
