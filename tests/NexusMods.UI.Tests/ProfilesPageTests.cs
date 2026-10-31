using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls.LoadoutBadge;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.App.UI.Pages;
using NexusMods.App.UI.Pages.MyLoadouts;
using NexusMods.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using NexusMods.App.UI.Windows;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;

namespace NexusMods.UI.Tests;

public class ProfilesPageTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task SearchAppliedFilterSortOrdersAndClearFiltersUpdateTheVisibleProfiles()
    {
        await using var fixture = await CreateFixture();
        await fixture.Activate();
        await Connection.FlushQueries();
        await EventuallyOnUi(() =>
        {
            fixture.Cards().Should().HaveCount(3);
            fixture.Page.HasProfiles.Should().BeTrue();
            fixture.Page.SummaryText.Should().Be("3 of 3 profiles · 1 game installation");
            fixture.Section.CardViewModels.First().Should().BeAssignableTo<ICreateNewLoadoutCardViewModel>();
        });
        var recent = fixture.Profiles.OrderByDescending(profile => profile.GetCreatedAt()).ThenBy(profile => profile.Id)
            .Select(profile => profile.Id).ToArray();
        await EventuallyOnUi(() => fixture.Cards().Select(card => card.LoadoutVal.Id).Should().Equal(recent));

        await OnUi(() => fixture.Page.SelectedSortIndex = 1);
        await EventuallyOnUi(() => fixture.Cards().Select(card => card.LoadoutName)
            .Should().Equal("Alpha profile", "Middle profile", "Zulu profile"));
        await OnUi(() => fixture.Page.SearchText = " ALPHA ");
        await EventuallyOnUi(() =>
        {
            fixture.Cards().Select(card => card.LoadoutName).Should().Equal("Alpha profile");
            fixture.Page.HasFilters.Should().BeTrue();
            fixture.Page.NoMatchingProfiles.Should().BeFalse();
            fixture.Page.SummaryText.Should().Be("1 of 3 profiles · 1 game installation");
        });

        await OnUi(() => fixture.Page.SearchText = "No matching profile");
        await EventuallyOnUi(() =>
        {
            fixture.Cards().Should().BeEmpty();
            fixture.Page.NoMatchingProfiles.Should().BeTrue();
            fixture.Section.IsVisible.Should().BeFalse();
            fixture.Page.HasProfiles.Should().BeTrue();
        });
        await OnUi(async () => await fixture.Page.ClearFiltersCommand.Execute().ToTask());
        await EventuallyOnUi(() =>
        {
            fixture.Cards().Should().HaveCount(3);
            fixture.Page.HasFilters.Should().BeFalse();
            fixture.Page.NoMatchingProfiles.Should().BeFalse();
            fixture.Page.SelectedSortIndex.Should().Be(1, "clearing filters should retain the chosen sort order");
        });

        // A game-name search matches every profile for that installation.
        await OnUi(() => fixture.Page.SearchText = fixture.Section.HeadingText.ToUpperInvariant());
        await EventuallyOnUi(() => fixture.Cards().Should().HaveCount(3));
        await OnUi(() => { fixture.Page.SearchText = string.Empty; fixture.Page.SelectedSortIndex = 2; });
        fixture.Applied.OnNext(new LoadoutWithTxId(fixture.Profiles[0], default));
        await EventuallyOnUi(() => fixture.Cards().First().LoadoutVal.Id.Should().Be(fixture.Profiles[0].Id));
        await OnUi(() => fixture.Page.OnlyApplied = true);
        await EventuallyOnUi(() => fixture.Cards().Select(card => card.LoadoutVal.Id).Should().Equal(fixture.Profiles[0].Id));
        fixture.Applied.OnNext(new LoadoutWithTxId(fixture.Profiles[2], default));
        await EventuallyOnUi(() =>
        {
            fixture.Cards().Select(card => card.LoadoutVal.Id).Should().Equal(fixture.Profiles[2].Id);
            fixture.Page.SummaryText.Should().Be("1 of 3 profiles · 1 game installation");
        });
    }

    [Fact]
    public async Task LiveChangesRefreshMatchingCardsTimesCountsAndTheEmptyState()
    {
        await using var fixture = await CreateFixture();
        await fixture.Activate();
        await Connection.FlushQueries();
        await EventuallyOnUi(() => fixture.Cards().Should().HaveCount(3));
        LoadoutCardViewModel? originalCard = null;
        IDisposable? cardActivation = null;
        string? originalApplyTime = null;
        await OnUi(() =>
        {
            originalCard = fixture.Cards().Single(card => card.LoadoutVal.Id == fixture.Profiles[0].Id);
            originalApplyTime = originalCard.HumanizedLoadoutLastApplyTime;
            cardActivation = originalCard.Activator.Activate();
            fixture.Page.SelectedSortIndex = 1;
            fixture.Page.SearchText = "Delta";
        });
        try
        {
            await EventuallyOnUi(() => fixture.Page.NoMatchingProfiles.Should().BeTrue());
            var appliedTime = DateTimeOffset.UtcNow.AddMinutes(-5);
            using (var tx = Connection.BeginTransaction())
            {
                tx.Add(fixture.Profiles[0].Id, Loadout.Name, "Delta profile");
                tx.Add(fixture.Profiles[0].Id, Loadout.LastAppliedDateTime, appliedTime);
                await tx.Commit();
            }
            await Connection.FlushQueries();
            await EventuallyOnUi(() =>
            {
                fixture.Cards().Should().ContainSingle().Which.Should().BeSameAs(originalCard);
                originalCard!.LoadoutName.Should().Be("Delta profile");
                originalCard.LoadoutVal.LastAppliedDateTime.Value.Should().BeCloseTo(appliedTime, TimeSpan.FromSeconds(1));
                originalCard.HumanizedLoadoutLastApplyTime.Should().NotBe(originalApplyTime);
                fixture.Page.NoMatchingProfiles.Should().BeFalse();
                fixture.Section.IsVisible.Should().BeTrue();
            });

            fixture.Status.OnNext(LoadoutSynchronizerState.NeedsSync);
            await EventuallyOnUi(() =>
            {
                originalCard!.HasPendingChanges.Should().BeTrue();
                originalCard.StatusText.Should().Be("Changes waiting to apply");
            });
            fixture.Status.OnNext(LoadoutSynchronizerState.Current);
            fixture.Applied.OnNext(new LoadoutWithTxId(fixture.Profiles[0], default));
            await EventuallyOnUi(() =>
            {
                originalCard!.HasPendingChanges.Should().BeFalse();
                originalCard.IsLoadoutApplied.Should().BeTrue();
                originalCard.StatusText.Should().Be("Applied · up to date");
            });

            var added = await fixture.AddProfile("Delta second profile");
            await Connection.FlushQueries();
            await EventuallyOnUi(() =>
            {
                fixture.Cards().Select(card => card.LoadoutName).Should().Equal("Delta profile", "Delta second profile");
                fixture.Page.SummaryText.Should().Be("2 of 4 profiles · 1 game installation");
                fixture.Section.CardViewModels.OfType<ICreateNewLoadoutCardViewModel>().Should().ContainSingle();
            });
            await fixture.DeleteProfile(fixture.Profiles[0]);
            await Connection.FlushQueries();
            await EventuallyOnUi(() =>
            {
                fixture.Cards().Select(card => card.LoadoutVal.Id).Should().Equal(added.Id);
                fixture.Page.SummaryText.Should().Be("1 of 3 profiles · 1 game installation");
                fixture.Status.HasObservers.Should().BeFalse("removing a card must dispose its active status subscription");
            });
            fixture.Status.OnNext(LoadoutSynchronizerState.NeedsSync);
            await OnUi(() =>
            {
                originalCard!.StatusText.Should().Be("Applied · up to date",
                    "a disposed card must stop reacting to later synchronizer changes");
                using var lateActivation = originalCard.Activator.Activate();
                fixture.Status.HasObservers.Should().BeFalse("a removed card must not recreate subscriptions");
                originalCard.Dispose();
            });

            foreach (var profile in fixture.Profiles.ToArray()) await fixture.DeleteProfile(profile);
            await Connection.FlushQueries();
            await EventuallyOnUi(() =>
            {
                fixture.Page.GameSectionViewModels.Should().BeEmpty();
                fixture.Page.HasProfiles.Should().BeFalse();
                fixture.Page.NoMatchingProfiles.Should().BeFalse();
                fixture.Page.SummaryText.Should().Be("No profiles yet");
            });
        }
        finally
        {
            await OnUi(() => cardActivation?.Dispose());
        }
    }

    private async Task<PageFixture> CreateFixture()
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations()
            .First(candidate => candidate.Game.GameId == Cyberpunk2077Game.GameId);
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            GameId = installation.Game.NexusModsGameId.Value, Store = installation.LocatorResult.Store,
            Path = installation.Locations[LocationId.Game].Path.ToString(), Name = "Profiles page test",
        };
        var result = await tx.Commit();
        var fixture = new PageFixture(this, installation, result.Remap(metadata));
        await fixture.AddProfile("Alpha profile");
        await fixture.AddProfile("Middle profile");
        await fixture.AddProfile("Zulu profile");
        await OnUi(fixture.Initialize);
        return fixture;
    }

    private sealed class PageFixture : IAsyncDisposable
    {
        private readonly ProfilesPageTests _owner;
        private readonly GameInstallation _installation;
        private readonly GameInstallMetadata.ReadOnly _metadata;
        private ServiceProvider? _services;
        private IDisposable? _activation;
        public List<Loadout.ReadOnly> Profiles { get; } = [];
        public BehaviorSubject<LoadoutWithTxId> Applied { get; } = new(default);
        public BehaviorSubject<LoadoutSynchronizerState> Status { get; } = new(LoadoutSynchronizerState.OtherLoadoutSynced);
        public MyLoadoutsViewModel Page { get; private set; } = null!;
        public IGameLoadoutsSectionEntryViewModel Section => Page.GameSectionViewModels.Single();
        public LoadoutCardViewModel[] Cards() => Page.GameSectionViewModels.SelectMany(section => section.CardViewModels)
            .OfType<LoadoutCardViewModel>().ToArray();

        public PageFixture(ProfilesPageTests owner, GameInstallation installation, GameInstallMetadata.ReadOnly metadata)
        {
            _owner = owner;
            _installation = installation;
            _metadata = metadata;
        }

        public async Task<Loadout.ReadOnly> AddProfile(string name)
        {
            using var tx = _owner.Connection.BeginTransaction();
            var profile = new Loadout.New(tx)
            {
                Name = name, ShortName = "T", InstallationId = _metadata, LoadoutKind = LoadoutKind.Default,
                Revision = 0, GameVersion = VanityVersion.From("Unknown"), LocatorIds = [LocatorId.From("test-build")],
            };
            var committed = (await tx.Commit()).Remap(profile);
            Profiles.Add(committed);
            return committed;
        }

        public async Task DeleteProfile(Loadout.ReadOnly profile)
        {
            if (!Loadout.Load(_owner.Connection.Db, profile.Id).IsValid()) return;
            using var tx = _owner.Connection.BeginTransaction();
            tx.Delete(profile.Id, false);
            await tx.Commit();
        }

        public void Initialize()
        {
            var registry = Substitute.For<IGameRegistry>();
            // The real DB is shared by UI test classes; resolve only this fixture's installation.
            registry.TryGetGameInstallation(Arg.Any<Loadout.ReadOnly>(), out Arg.Any<GameInstallation?>()).Returns(call =>
            {
                var resolved = call.ArgAt<Loadout.ReadOnly>(0).InstallationId.Value == _metadata.Id;
                call[1] = resolved ? _installation : null;
                return resolved;
            });
            var synchronizer = Substitute.For<ISynchronizerService>();
            synchronizer.LastAppliedRevisionFor(Arg.Any<GameInstallation>()).Returns(Applied);
            synchronizer.StatusForLoadout(Arg.Any<LoadoutId>()).Returns(Task.FromResult((IObservable<LoadoutSynchronizerState>)Status));
            var items = Substitute.For<ILoadoutDataProvider>();
            items.CountLoadoutItems(Arg.Any<LoadoutFilter>()).Returns(Observable.Return(0));
            var windows = Substitute.For<IWindowManager>();
            _services = new ServiceCollection()
                .AddSingleton<IConnection>(_owner.Connection).AddSingleton<IGameRegistry>(registry)
                .AddSingleton<ILoadoutManager>(Substitute.For<ILoadoutManager>()).AddSingleton(windows)
                .AddSingleton<ISynchronizerService>(synchronizer).AddSingleton<ILoadoutDataProvider>(items)
                .AddTransient<ILoadoutBadgeViewModel>(_ => Substitute.For<ILoadoutBadgeViewModel>())
                .AddSingleton<ILogger<LoadoutCardViewModel>>(NullLogger<LoadoutCardViewModel>.Instance)
                .AddSingleton<ILogger<GameLoadoutsSectionEntryViewModel>>(NullLogger<GameLoadoutsSectionEntryViewModel>.Instance)
                .BuildServiceProvider();
            Page = new MyLoadoutsViewModel(windows, _owner.Connection, _services);
        }

        public Task Activate() => _owner.OnUi(() => _activation = Page.Activator.Activate());

        public async ValueTask DisposeAsync()
        {
            await _owner.OnUi(() => { _activation?.Dispose(); Page.ClearFiltersCommand.Dispose(); Page.Activator.Dispose(); });
            Applied.Dispose();
            Status.Dispose();
            _services?.Dispose();
            using var tx = _owner.Connection.BeginTransaction();
            foreach (var profile in Profiles)
                if (Loadout.Load(_owner.Connection.Db, profile.Id).IsValid()) tx.Delete(profile.Id, false);
            tx.Delete(_metadata.Id, false);
            await tx.Commit();
            await _owner.Connection.FlushQueries();
        }
    }
}
