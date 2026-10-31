using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.UI.Sdk.Icons;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.MyLoadouts;

public class MyLoadoutsViewModel : APageViewModel<IMyLoadoutsViewModel>, IMyLoadoutsViewModel
{
    private ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> _sections = new([]);
    public ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels => _sections;
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool OnlyApplied { get; set; }
    [Reactive] public int SelectedSortIndex { get; set; }
    [Reactive] public string SummaryText { get; private set; } = "No profiles yet";
    [Reactive] public bool HasProfiles { get; private set; }
    [Reactive] public bool HasFilters { get; private set; }
    [Reactive] public bool NoMatchingProfiles { get; private set; }
    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }

    public MyLoadoutsViewModel(IWindowManager windowManager, IConnection connection, IServiceProvider services) : base(windowManager)
    {
        var registry = services.GetRequiredService<IGameRegistry>();
        TabTitle = Language.MyLoadoutsPageTitle;
        TabIcon = IconValues.Package;
        ClearFiltersCommand = ReactiveCommand.Create(() => { SearchText = string.Empty; OnlyApplied = false; });

        this.WhenActivated((CompositeDisposable lifetime) =>
        {
            var filters = this.WhenAnyValue(vm => vm.SearchText, vm => vm.OnlyApplied, vm => vm.SelectedSortIndex)
                .Select(state => new ProfileFilter(state.Item1 ?? string.Empty, state.Item2, (ProfileSortOrder)Math.Clamp(state.Item3, 0, 2)))
                .DistinctUntilChanged().Replay(1).RefCount();
            filters.OnUI().Subscribe(filter => HasFilters = filter.IsFiltering).DisposeWith(lifetime);

            Loadout.ObserveAll(connection)
                .Filter(loadout => loadout.IsVisible())
                .Transform(loadout => (Resolved: registry.TryGetGameInstallation(loadout, out var installation), Installation: installation))
                .Filter(tuple => tuple.Resolved && tuple.Installation is not null)
                .Transform(tuple => tuple.Installation!)
                .DistinctValues(installation => installation)
                .OnUI()
                .Transform(installation => (IGameLoadoutsSectionEntryViewModel)new GameLoadoutsSectionEntryViewModel(
                    installation, connection, services, windowManager, filters))
                .AutoRefresh(section => section.TotalProfileCount)
                .AutoRefresh(section => section.MatchingProfileCount)
                .Sort(Comparer<IGameLoadoutsSectionEntryViewModel>.Create((left, right) =>
                {
                    var name = StringComparer.OrdinalIgnoreCase.Compare(left.HeadingText, right.HeadingText);
                    return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(left.InstallationText, right.InstallationText);
                }))
                .Bind(out _sections)
                .DisposeMany()
                .Do(_ =>
                {
                    this.RaisePropertyChanged(nameof(GameSectionViewModels));
                    var total = _sections.Sum(section => section.TotalProfileCount);
                    var matching = _sections.Sum(section => section.MatchingProfileCount);
                    HasProfiles = total > 0;
                    NoMatchingProfiles = total > 0 && matching == 0;
                    var profileNoun = total == 1 ? "profile" : "profiles";
                    var installationNoun = _sections.Count == 1 ? "game installation" : "game installations";
                    SummaryText = total == 0 ? "No profiles yet" : $"{matching} of {total} {profileNoun} · {_sections.Count} {installationNoun}";
                })
                .SubscribeWithErrorLogging()
                .DisposeWith(lifetime);
        });
    }
}
