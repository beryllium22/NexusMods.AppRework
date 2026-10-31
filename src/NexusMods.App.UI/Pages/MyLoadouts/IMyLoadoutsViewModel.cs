using System.Collections.ObjectModel;
using NexusMods.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using NexusMods.App.UI.WorkspaceSystem;
using System.Reactive;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyLoadouts;


public interface IMyLoadoutsViewModel : IPageViewModelInterface
{
    ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels { get; }
    string SearchText { get; set; }
    bool OnlyApplied { get; set; }
    int SelectedSortIndex { get; set; }
    string SummaryText { get; }
    bool HasProfiles { get; }
    bool HasFilters { get; }
    bool NoMatchingProfiles { get; }
    ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }
}
