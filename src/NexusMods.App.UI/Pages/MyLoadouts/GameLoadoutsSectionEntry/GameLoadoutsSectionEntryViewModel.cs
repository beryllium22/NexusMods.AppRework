using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Pages.LoadoutPage;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Dialog;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;

public class GameLoadoutsSectionEntryViewModel : AViewModel<IGameLoadoutsSectionEntryViewModel>, IGameLoadoutsSectionEntryViewModel
{
    private readonly CompositeDisposable _lifetime = new();
    private readonly IWindowManager _windowManager;
    private readonly ReadOnlyObservableCollection<IViewModelInterface> _cards;
    private readonly SourceList<IViewModelInterface> _createCard = new();
    private readonly ReadOnlyObservableCollection<LoadoutCardViewModel> _visibleProfiles;
    public string HeadingText { get; }
    public string InstallationText { get; }
    [Reactive] public bool IsVisible { get; private set; } = true;
    [Reactive] public int TotalProfileCount { get; private set; }
    [Reactive] public int MatchingProfileCount { get; private set; }
    public ReadOnlyObservableCollection<IViewModelInterface> CardViewModels => _cards;

    public GameLoadoutsSectionEntryViewModel(GameInstallation installation, IConnection connection, IServiceProvider services,
        IWindowManager windowManager, IObservable<ProfileFilter>? filter = null)
    {
        _windowManager = windowManager;
        var registry = services.GetRequiredService<IGameRegistry>();
        var manager = services.GetRequiredService<ILoadoutManager>();
        var synchronizer = services.GetRequiredService<ISynchronizerService>();
        var logger = services.GetRequiredService<ILogger<GameLoadoutsSectionEntryViewModel>>();
        HeadingText = installation.Game.DisplayName;
        InstallationText = $"{installation.LocatorResult.Store} · {installation.Locations[LocationId.Game].Path}";

        var profiles = Loadout.ObserveAll(connection)
            .Filter(loadout => loadout.IsVisible()
                && registry.TryGetGameInstallation(loadout, out var located) && located.Equals(installation))
            .OnUI()
            .TransformWithInlineUpdate(loadout => new LoadoutCardViewModel(loadout, connection, services)
            {
                VisitLoadoutCommand = ReactiveCommand.Create(() => NavigateToLoadout(loadout)),
            }, (card, loadout) => card.UpdateLoadout(loadout))
            .DisposeMany()
            .AsObservableCache();
        profiles.DisposeWith(_lifetime);

        var selection = (filter ?? Observable.Return(ProfileFilter.Default))
            .CombineLatest(synchronizer.LastAppliedRevisionFor(installation).Select(revision => revision.Id).Prepend(default(LoadoutId)),
                (options, appliedId) => (Options: options, AppliedId: appliedId))
            .OnUI().Replay(1).RefCount();

        profiles.Connect().QueryWhenChanged(query => query.Count)
            .OnUI().Subscribe(count => TotalProfileCount = count).DisposeWith(_lifetime);

        var matching = profiles.Connect()
            .AutoRefresh(card => card.LoadoutName)
            .Filter(selection.Select(state => new Func<LoadoutCardViewModel, bool>(card =>
                (!state.Options.OnlyApplied || card.LoadoutVal.LoadoutId == state.AppliedId)
                && (string.IsNullOrWhiteSpace(state.Options.SearchText)
                    || card.LoadoutName.Contains(state.Options.SearchText.Trim(), StringComparison.OrdinalIgnoreCase)
                    || HeadingText.Contains(state.Options.SearchText.Trim(), StringComparison.OrdinalIgnoreCase)))))
            .AsObservableCache();
        matching.DisposeWith(_lifetime);
        matching.Connect().QueryWhenChanged(query => query.Count)
            .CombineLatest(selection, (count, state) => (Count: count, Visible: count > 0 || !state.Options.IsFiltering))
            .OnUI().Subscribe(state =>
            {
                MatchingProfileCount = state.Count;
                IsVisible = state.Visible;
            }).DisposeWith(_lifetime);

        var comparers = selection.Select(state => (IComparer<LoadoutCardViewModel>)Comparer<LoadoutCardViewModel>.Create(
            (left, right) => CompareProfiles(left, right, state.Options.SortOrder, state.AppliedId)));
        matching.Connect().AutoRefresh(card => card.LoadoutName)
            .Sort(comparers).OnUI().Bind(out _visibleProfiles).SubscribeWithErrorLogging().DisposeWith(_lifetime);

        var create = new CreateNewLoadoutCardViewModel
        {
            AddLoadoutCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                try
                {
                    var result = await windowManager.ShowDialog(ProfileDialogs.Create(installation.Game.DisplayName), DialogWindowType.Modal);
                    if (result.ButtonId != ButtonDefinitionId.Accept) return;
                    var created = await Task.Run(async () => await manager.CreateLoadout(installation, result.InputText));
                    NavigateToLoadout(created);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not create profile for {Game}", installation.Game.DisplayName);
                    await MessageBoxOkViewModel.Show(services, "Could not create profile", ex.Message);
                }
            }),
        };
        create.AddLoadoutCommand.ThrownExceptions.Subscribe(ex => logger.LogError(ex, "Create profile command failed")).DisposeWith(_lifetime);
        create.AddLoadoutCommand.DisposeWith(_lifetime);
        _createCard.DisposeWith(_lifetime);
        _createCard.Add(create);

        // Concatenate the create tile and the sorted profile list without nesting action buttons.
        _createCard.Connect().Merge(_visibleProfiles.ToObservableChangeSet().Transform(card => (IViewModelInterface)card))
            .Sort(selection.Select(state => (IComparer<IViewModelInterface>)Comparer<IViewModelInterface>.Create((left, right) =>
            {
                if (ReferenceEquals(left, right)) return 0;
                if (left is ICreateNewLoadoutCardViewModel) return -1;
                if (right is ICreateNewLoadoutCardViewModel) return 1;
                return CompareProfiles((LoadoutCardViewModel)left, (LoadoutCardViewModel)right, state.Options.SortOrder, state.AppliedId);
            })))
            .OnUI().Bind(out _cards).SubscribeWithErrorLogging().DisposeWith(_lifetime);
    }

    private static int CompareProfiles(LoadoutCardViewModel left, LoadoutCardViewModel right, ProfileSortOrder sort, LoadoutId appliedId)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (sort == ProfileSortOrder.AppliedFirst)
        {
            var applied = (right.LoadoutVal.LoadoutId == appliedId).CompareTo(left.LoadoutVal.LoadoutId == appliedId);
            if (applied != 0) return applied;
        }
        var comparison = sort == ProfileSortOrder.Name
            ? StringComparer.OrdinalIgnoreCase.Compare(left.LoadoutName, right.LoadoutName)
            : right.LoadoutVal.GetCreatedAt().CompareTo(left.LoadoutVal.GetCreatedAt());
        return comparison != 0 ? comparison : left.LoadoutVal.Id.CompareTo(right.LoadoutVal.Id);
    }

    private void NavigateToLoadout(Loadout.ReadOnly loadout)
    {
        var loadoutId = loadout.LoadoutId;
        Dispatcher.UIThread.Invoke(() => _windowManager.ActiveWorkspaceController.ChangeOrCreateWorkspaceByContext(
            context => context.LoadoutId == loadoutId,
            () => new PageData
            {
                FactoryId = LoadoutPageFactory.StaticId,
                Context = new LoadoutPageContext { LoadoutId = loadoutId, GroupScope = Optional<CollectionGroupId>.None },
            },
            () => new LoadoutContext { LoadoutId = loadoutId }));
    }

    public void Dispose() => _lifetime.Dispose();
}
