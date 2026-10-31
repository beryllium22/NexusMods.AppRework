using System.Collections.ObjectModel;
using System.Reactive;
using NexusMods.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.MyLoadouts;

public class MyLoadoutsDesignViewModel : APageViewModel<IMyLoadoutsViewModel>, IMyLoadoutsViewModel
{
    public MyLoadoutsDesignViewModel() : base(new DesignWindowManager()) { }
    public MyLoadoutsDesignViewModel(IWindowManager windowManager) : base(windowManager) { }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool OnlyApplied { get; set; }
    [Reactive] public int SelectedSortIndex { get; set; }
    public string SummaryText => "4 of 4 profiles · 2 game installations";
    public bool HasProfiles => true;
    public bool HasFilters => false;
    public bool NoMatchingProfiles => false;
    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; } = ReactiveCommand.Create(() => { });
    public ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels { get; } = new([
        new GameLoadoutsSectionEntryDesignViewModel(),
        new GameLoadoutsSectionEntryDesignViewModel { HeadingText = "Cyberpunk 2077", InstallationText = @"GOG · D:\Games\Cyberpunk 2077" },
    ]);
}
