using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls.LoadoutBadge;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Pages;
using NexusMods.App.UI.Windows;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Dialog;
using NSubstitute;
using R3;
using ReactiveUI;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace NexusMods.UI.Tests;

public class ProfileCommandTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InvalidProfileNamesCannotBeAcceptedAndCancelRemainsAvailable()
    {
        await OnUi(() =>
        {
            foreach (var invalid in new[] { "", "   ", "Two\nlines", new string('x', LoadoutName.MaximumLength + 1) })
            {
                var content = new DialogStandardContentViewModel(new StandardDialogParameters
                {
                    InputLabel = "Profile name", InputText = invalid, InputValidation = LoadoutName.GetValidationError,
                });
                var dialog = new DialogViewModel("Rename profile", [DialogStandardButtons.Cancel, DialogStandardButtons.Ok],
                    content, DialogWindowSize.Medium, showChrome: true);
                var results = new List<ButtonDefinitionId>();
                using var subscription = dialog.ButtonPressCommand.Subscribe(results.Add);
                try
                {
                    content.IsInputValid.Should().BeFalse();
                    content.InputValidationError.Should().NotBeEmpty();
                    dialog.ButtonPressCommand.Execute(ButtonDefinitionId.Accept);
                    dialog.Result.ButtonId.Should().Be(ButtonDefinitionId.None);
                    results.Should().Equal(ButtonDefinitionId.None);

                    dialog.ButtonPressCommand.Execute(ButtonDefinitionId.Cancel);
                    dialog.Result.ButtonId.Should().Be(ButtonDefinitionId.Cancel);
                    content.InputText = "Survival playthrough";
                    content.IsInputValid.Should().BeTrue();
                    content.InputValidationError.Should().BeEmpty();
                    dialog.ButtonPressCommand.Execute(ButtonDefinitionId.Accept);
                    dialog.Result.ButtonId.Should().Be(ButtonDefinitionId.Accept);
                    dialog.Result.InputText.Should().Be("Survival playthrough");
                }
                finally
                {
                    dialog.ButtonPressCommand.Dispose();
                    dialog.Activator.Dispose();
                    content.ClearInputCommand.Dispose();
                    content.Activator.Dispose();
                }
            }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RenameAndDuplicateOnlyPassAcceptedNamesToTheManager(bool duplicate, bool accept)
    {
        await using var fixture = await CreateFixture();
        const string chosenName = "Survival with friends";
        fixture.Windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal)
            .Returns(Task.FromResult(new StandardDialogResult
            {
                ButtonId = accept ? ButtonDefinitionId.Accept : ButtonDefinitionId.Cancel, InputText = chosenName,
            }));
        fixture.Manager.CopyLoadout(fixture.Loadout.LoadoutId, Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(ValueTask.FromResult(fixture.Loadout));
        var command = duplicate ? fixture.Card.CloneLoadoutCommand : fixture.Card.RenameLoadoutCommand;
        await Task.Run(() => command.Execute().ToTask()).WaitAsync(Timeout);

        if (accept && duplicate)
            await fixture.Manager.Received(1).CopyLoadout(fixture.Loadout.LoadoutId, Arg.Any<CancellationToken>(), suggestedName: chosenName);
        else
            await fixture.Manager.DidNotReceiveWithAnyArgs().CopyLoadout(default);
        if (accept && !duplicate)
            await fixture.Manager.Received(1).RenameLoadout(fixture.Loadout.LoadoutId, chosenName, Arg.Any<CancellationToken>());
        else
            await fixture.Manager.DidNotReceiveWithAnyArgs().RenameLoadout(default, string.Empty);

        await OnUi(() =>
        {
            fixture.Card.IsBusy.Should().BeFalse();
            fixture.Card.BusyText.Should().BeEmpty();
            fixture.Card.LoadoutName.Should().Be(fixture.Loadout.Name, "the view only changes names after a committed database update");
        });
        fixture.Messages.Should().BeEmpty();
        fixture.PropertiesChangedOnUi.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandFailuresShowFeedbackAndRestoreBusyState(bool duplicate)
    {
        await using var fixture = await CreateFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("The profile database could not be updated.");
        fixture.Windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal)
            .Returns(Task.FromResult(new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept, InputText = "New profile name" }));
        fixture.Manager.RenameLoadout(fixture.Loadout.LoadoutId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Fail()));
        fixture.Manager.CopyLoadout(fixture.Loadout.LoadoutId, Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(_ => new ValueTask<Loadout.ReadOnly>(FailCopy()));
        async Task Fail()
        {
            entered.TrySetResult();
            await release.Task;
            throw failure;
        }
        async Task<Loadout.ReadOnly> FailCopy()
        {
            await Fail();
            return default;
        }

        var command = duplicate ? fixture.Card.CloneLoadoutCommand : fixture.Card.RenameLoadoutCommand;
        var errors = new List<Exception>();
        using var errorSubscription = command.ThrownExceptions.Subscribe(errors.Add);
        var execution = Task.Run(() => command.Execute().ToTask());
        try
        {
            await entered.Task.WaitAsync(Timeout);
            await OnUi(() =>
            {
                fixture.Card.IsBusy.Should().BeTrue();
                fixture.Card.BusyText.Should().Be(duplicate ? "Duplicating profile…" : "Renaming profile…");
            });
            release.TrySetResult();
            await execution.WaitAsync(Timeout);
            await command.CanExecute.FirstAsync(canExecute => canExecute).ToTask().WaitAsync(Timeout);
            await OnUi(() =>
            {
                fixture.Card.IsBusy.Should().BeFalse();
                fixture.Card.BusyText.Should().BeEmpty();
                fixture.Overlays.CurrentOverlay.Should().BeNull();
            });
            fixture.Messages.Should().ContainSingle();
            fixture.Messages[0].Title.Should().Be("Could not update profile");
            fixture.Messages[0].Description.Should().Be(failure.Message);
            fixture.PropertiesChangedOnUi.Should().BeTrue();
            fixture.FeedbackShownOnUi.Should().BeTrue();
            errors.Should().BeEmpty();
        }
        finally
        {
            release.TrySetResult();
            await execution.WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRechecksLastAndAppliedProfileProtectionAfterTheConfirmation(bool applied)
    {
        await using var fixture = await CreateFixture();
        var confirmationShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<StandardDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(_ =>
        {
            confirmationShown.TrySetResult();
            return response.Task;
        });
        await fixture.Activate();
        await Connection.FlushQueries();
        await fixture.Card.DeleteLoadoutCommand.CanExecute.FirstAsync(canExecute => canExecute).ToTask().WaitAsync(Timeout);
        var execution = Task.Run(() => fixture.Card.DeleteLoadoutCommand.Execute().ToTask());
        try
        {
            await confirmationShown.Task.WaitAsync(Timeout);
            using (var tx = Connection.BeginTransaction())
            {
                if (applied) tx.Add(fixture.Loadout.InstallationId, GameInstallMetadata.LastSyncedLoadout, fixture.Loadout.Id);
                else tx.Delete(fixture.OtherLoadout.Id, false);
                await tx.Commit();
            }
            response.TrySetResult(new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept });
            await execution.WaitAsync(Timeout);
            await fixture.Manager.DidNotReceiveWithAnyArgs().DeleteLoadout(default);
            Loadout.Load(Connection.Db, fixture.Loadout.Id).IsValid().Should().BeTrue();
            fixture.Messages.Should().ContainSingle();
            fixture.Messages[0].Description.Should().Contain(applied ? "Apply a different profile" : "Keep at least one profile");

            if (applied) fixture.Applied.OnNext(new LoadoutWithTxId(fixture.Loadout.LoadoutId, default));
            await Connection.FlushQueries();
            await EventuallyOnUi(() =>
            {
                fixture.Card.IsBusy.Should().BeFalse();
                fixture.Card.BusyText.Should().BeEmpty();
                if (applied) fixture.Card.IsLoadoutApplied.Should().BeTrue();
                else fixture.Card.IsLastLoadout.Should().BeTrue();
            });
            (await fixture.Card.DeleteLoadoutCommand.CanExecute.FirstAsync().ToTask()).Should().BeFalse();
            fixture.PropertiesChangedOnUi.Should().BeTrue();
            fixture.FeedbackShownOnUi.Should().BeTrue();
        }
        finally
        {
            response.TrySetResult(new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel });
            await execution.WaitAsync(Timeout);
        }
    }

    private async Task<CardFixture> CreateFixture()
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations()
            .First(candidate => candidate.Game.GameId == Cyberpunk2077Game.GameId);
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            GameId = installation.Game.NexusModsGameId.Value, Store = installation.LocatorResult.Store,
            Path = installation.Locations[LocationId.Game].Path.ToString(), Name = "Profile command test",
        };
        Loadout.New Profile(string name) => new(tx)
        {
            Name = name, ShortName = "T", InstallationId = metadata, LoadoutKind = LoadoutKind.Default,
            Revision = 0, GameVersion = VanityVersion.From("Unknown"), LocatorIds = [LocatorId.From("test-build")],
        };
        var first = Profile("Original profile");
        var second = Profile("Other profile");
        var result = await tx.Commit();
        var fixture = new CardFixture(this, result.Remap(first), result.Remap(second));
        await OnUi(fixture.Initialize);
        return fixture;
    }

    private sealed class CardFixture : IAsyncDisposable
    {
        private readonly ProfileCommandTests _owner;
        private ServiceProvider? _services;
        private IDisposable? _activation;
        public Loadout.ReadOnly Loadout { get; }
        public Loadout.ReadOnly OtherLoadout { get; }
        public ILoadoutManager Manager { get; } = Substitute.For<ILoadoutManager>();
        public IWindowManager Windows { get; } = Substitute.For<IWindowManager>();
        public OverlayController Overlays { get; } = new();
        public System.Reactive.Subjects.BehaviorSubject<LoadoutWithTxId> Applied { get; } = new(default);
        public List<MessageBoxOkViewModel> Messages { get; } = [];
        public LoadoutCardViewModel Card { get; private set; } = null!;
        public bool PropertiesChangedOnUi { get; private set; } = true;
        public bool FeedbackShownOnUi { get; private set; } = true;

        public CardFixture(ProfileCommandTests owner, Loadout.ReadOnly loadout, Loadout.ReadOnly otherLoadout)
        {
            _owner = owner;
            Loadout = loadout;
            OtherLoadout = otherLoadout;
        }

        public void Initialize()
        {
            var synchronizer = Substitute.For<ISynchronizerService>();
            synchronizer.LastAppliedRevisionFor(Arg.Any<GameInstallation>()).Returns(Applied);
            synchronizer.StatusForLoadout(Arg.Any<LoadoutId>())
                .Returns(Task.FromResult(System.Reactive.Linq.Observable.Return(LoadoutSynchronizerState.OtherLoadoutSynced)));
            var items = Substitute.For<ILoadoutDataProvider>();
            items.CountLoadoutItems(Arg.Any<LoadoutFilter>()).Returns(System.Reactive.Linq.Observable.Return(0));
            _services = new ServiceCollection()
                .AddSingleton<IConnection>(_owner.Connection)
                .AddSingleton(Manager).AddSingleton(Windows).AddSingleton<ISynchronizerService>(synchronizer)
                .AddSingleton<IOverlayController>(Overlays)
                .AddSingleton<ILoadoutBadgeViewModel>(Substitute.For<ILoadoutBadgeViewModel>())
                .AddSingleton<ILoadoutDataProvider>(items)
                .AddSingleton<Microsoft.Extensions.Logging.ILogger<LoadoutCardViewModel>>(NullLogger<LoadoutCardViewModel>.Instance)
                .BuildServiceProvider();
            Card = new LoadoutCardViewModel(Loadout, _owner.Connection, _services)
            {
                VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
            };
            Card.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(Card.IsBusy) or nameof(Card.BusyText) or nameof(Card.IsLastLoadout) or nameof(Card.IsLoadoutApplied))
                    PropertiesChangedOnUi &= Dispatcher.UIThread.CheckAccess();
            };
            Overlays.PropertyChanged += (_, _) =>
            {
                FeedbackShownOnUi &= Dispatcher.UIThread.CheckAccess();
                if (Overlays.CurrentOverlay is not MessageBoxOkViewModel message) return;
                Messages.Add(message);
                message.Close();
            };
        }

        public Task Activate() => _owner.OnUi(() => _activation = Card.Activator.Activate());

        public async ValueTask DisposeAsync()
        {
            await _owner.OnUi(() => { _activation?.Dispose(); Card.Dispose(); });
            Applied.Dispose();
            _services?.Dispose();
            using var tx = _owner.Connection.BeginTransaction();
            tx.Delete(Loadout.Id, false);
            if (NexusMods.Sdk.Loadouts.Loadout.Load(_owner.Connection.Db, OtherLoadout.Id).IsValid()) tx.Delete(OtherLoadout.Id, false);
            tx.Delete(Loadout.InstallationId, false);
            await tx.Commit();
            await _owner.Connection.FlushQueries();
        }
    }
}
