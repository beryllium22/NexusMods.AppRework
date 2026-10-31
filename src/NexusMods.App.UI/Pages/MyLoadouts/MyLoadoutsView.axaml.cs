using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyLoadouts;

public partial class MyLoadoutsView : ReactiveUserControl<IMyLoadoutsViewModel>
{
    public MyLoadoutsView()
    {
        InitializeComponent();
        this.WhenActivated(d =>
        {
            this.OneWayBind(ViewModel, vm => vm.GameSectionViewModels, view => view.GameSectionsItemsControl.ItemsSource).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.SearchText, view => view.ProfileSearchTextBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.OnlyApplied, view => view.OnlyAppliedCheckBox.IsChecked).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.SelectedSortIndex, view => view.ProfileSortComboBox.SelectedIndex).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SummaryText, view => view.ProfileSummaryTextBlock.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.HasProfiles, view => view.ProfilesToolbar.IsVisible).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.HasFilters, view => view.ClearFiltersButton.IsEnabled).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.NoMatchingProfiles, view => view.NoMatchingProfilesPanel.IsVisible).DisposeWith(d);
            this.WhenAnyValue(view => view.ViewModel!.HasProfiles).Select(hasProfiles => !hasProfiles)
                .BindToView(this, view => view.MyLoadoutsEmptyState.IsActive).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ClearFiltersCommand, view => view.ClearFiltersButton).DisposeWith(d);
        });
    }
}
