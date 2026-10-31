using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.App.UI.Pages.MyLoadouts;
using NexusMods.App.UI.Windows;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NSubstitute;

namespace NexusMods.UI.Tests;

public class UnresolvedLoadoutTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task MyLoadoutsKeepsValidCardsAndLiveUpdatesWhenASavedInstallationIsUnavailable()
    {
        var installation = Provider.GetRequiredService<IGameRegistry>()
            .LocateGameInstallations().First(candidate => candidate.Game.GameId == Cyberpunk2077Game.GameId);
        var created = new List<Loadout.ReadOnly>();
        MyLoadoutsViewModel? viewModel = null;
        IDisposable? activation = null;

        try
        {
            // An unresolved row precedes the valid one, so a terminated enumeration
            // cannot accidentally pass by rendering the valid row first.
            var orphan = await CreateLoadout("Unavailable installation", NexusModsGameId.From(999999), installation);
            created.Add(orphan);
            var valid = await CreateLoadout("Available installation", installation.Game.NexusModsGameId.Value, installation);
            created.Add(valid);

            await OnUi(() =>
            {
                viewModel = new MyLoadoutsViewModel(Substitute.For<IWindowManager>(), Connection, Provider);
                activation = viewModel.Activator.Activate();
            });
            await Connection.FlushQueries();
            await EventuallyOnUi(() => VisibleTestCards(viewModel!, created)
                .Should().Equal(valid.LoadoutId));

            var addedLater = await CreateLoadout("Added after activation", installation.Game.NexusModsGameId.Value, installation);
            created.Add(addedLater);
            await Connection.FlushQueries();
            await EventuallyOnUi(() => VisibleTestCards(viewModel!, created)
                .Should().BeEquivalentTo(new[] { valid.LoadoutId, addedLater.LoadoutId }));

            var savedOrphan = Loadout.Load(Connection.Db, orphan.Id);
            savedOrphan.IsValid().Should().BeTrue("unavailable loadouts remain saved for later recovery");
            savedOrphan.Installation.GameId.Should().Be(NexusModsGameId.From(999999));
        }
        finally
        {
            await OnUi(() => activation?.Dispose());
            using var tx = Connection.BeginTransaction();
            foreach (var loadout in created)
            {
                tx.Delete(loadout.Id, false);
                tx.Delete(loadout.Installation.Id, false);
            }
            await tx.Commit();
            await Connection.FlushQueries();
        }
    }

    private static LoadoutId[] VisibleTestCards(MyLoadoutsViewModel viewModel, IReadOnlyCollection<Loadout.ReadOnly> created)
        => viewModel.GameSectionViewModels
            .SelectMany(section => section.CardViewModels)
            .OfType<LoadoutCardViewModel>()
            .Where(card => created.Any(loadout => loadout.Id == card.LoadoutVal.Id))
            .Select(card => card.LoadoutVal.LoadoutId)
            .ToArray();

    private async Task<Loadout.ReadOnly> CreateLoadout(string name, NexusModsGameId gameId, GameInstallation installation)
    {
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            GameId = gameId,
            Store = installation.LocatorResult.Store,
            Path = installation.Locations[LocationId.Game].Path.ToString(),
            Name = name,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = name,
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
