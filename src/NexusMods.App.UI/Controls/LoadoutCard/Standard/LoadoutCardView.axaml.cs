using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.LoadoutCard;

public partial class LoadoutCardView : ReactiveUserControl<ILoadoutCardViewModel>
{
    public LoadoutCardView()
    {
        InitializeComponent();
        this.WhenActivated(d =>
        {
            this.OneWayBind(ViewModel, vm => vm.LoadoutBadgeViewModel, view => view.LoadoutBadge.ViewModel).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.LoadoutImage, view => view.GameImage.Source).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.LoadoutName, view => view.LoadoutNameTextBlock.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.StatusText, view => view.StatusTextBlock.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.HumanizedLoadoutLastApplyTime, view => view.LastAppliedTimeTextBlock.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.HumanizedLoadoutCreationTime, view => view.CreatedTimeTextBlock.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.LoadoutModCount, view => view.NumberOfModsTextBlock.Text).DisposeWith(d);

            this.WhenAnyValue(view => view.ViewModel!.IsLastLoadout, view => view.ViewModel!.IsLoadoutApplied)
                .Select(state => !state.Item1 && !state.Item2).OnUI()
                .BindToView(this, view => view.DeleteButton.IsVisible).DisposeWith(d);
            this.WhenAnyValue(view => view.ViewModel!.HasPendingChanges).OnUI()
                .Subscribe(pending => StatusBorder.Classes.Set("pending", pending)).DisposeWith(d);
            this.WhenAnyValue(view => view.ViewModel!.IsBusy, view => view.ViewModel!.IsSkeleton, view => view.ViewModel!.BusyText)
                .OnUI().Subscribe(state =>
                {
                    var busy = state.Item1 || state.Item2;
                    CardBody.IsEnabled = !busy;
                    CardBody.Opacity = busy ? 0.25 : 1;
                    OverlayStack.IsVisible = busy;
                    OverlayTextBlock.Text = state.Item2 ? "Creating profile…" : state.Item3;
                }).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.CloneLoadoutCommand, view => view.CreateCopyButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.RenameLoadoutCommand, view => view.RenameButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.DeleteLoadoutCommand, view => view.DeleteButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.VisitLoadoutCommand, view => view.CardOuterButton).DisposeWith(d);
        });
    }
}
