using System.Collections.ObjectModel;
using System.Reactive;
using NexusMods.App.UI.Controls.LoadoutBadge;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;

public class GameLoadoutsSectionEntryDesignViewModel : AViewModel<IGameLoadoutsSectionEntryViewModel>, IGameLoadoutsSectionEntryViewModel
{
    public string HeadingText { get; init; } = "Cyberpunk 2077";
    public string InstallationText { get; init; } = @"Steam · C:\Games\Cyberpunk 2077";
    public bool IsVisible => true;
    public int TotalProfileCount => 2;
    public int MatchingProfileCount => 2;

    public ReadOnlyObservableCollection<IViewModelInterface> CardViewModels { get; } = new([
            new CreateNewLoadoutCardViewModel()
            {
                AddLoadoutCommand = ReactiveCommand.Create(() => { }),
            },
            new LoadoutCardDesignViewModel
            {
                LoadoutName = "Everyday adventure", IsLoadoutApplied = true,
                StatusText = "Applied · up to date", HasPendingChanges = false,
                LoadoutBadgeViewModel = new LoadoutBadgeDesignViewModel { LoadoutShortName = "A", IsLoadoutApplied = true },
            },
            new LoadoutCardDesignViewModel { LoadoutName = "Expanded world" },
        ]
    );
    
    void IDisposable.Dispose() { }
}
