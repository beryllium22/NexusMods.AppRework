using System.Reactive;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NexusMods.App.UI.Controls.LoadoutBadge;
using NexusMods.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Controls.LoadoutCard;

public class LoadoutCardDesignViewModel : AViewModel<ILoadoutCardViewModel>, ILoadoutCardViewModel
{
    public ILoadoutBadgeViewModel LoadoutBadgeViewModel { get; init; } = new LoadoutBadgeDesignViewModel();
    public string LoadoutName { get; init; } = "Survival playthrough";
    public IImage LoadoutImage { get; } = Bitmap.DecodeToWidth(AssetLoader.Open(new Uri("avares://NexusMods.App.UI/Assets/DesignTime/thumbnail.webp")), (int)ImageSizes.GameThumbnail.Width);
    public bool IsLoadoutApplied { get; init; }
    public string StatusText { get; init; } = "Changes waiting to apply";
    public bool HasPendingChanges { get; init; } = true;
    public string HumanizedLoadoutLastApplyTime { get; init; } = "Last applied: 2 days ago";
    public string HumanizedLoadoutCreationTime { get; init; } = "Created 3 weeks ago";
    public string LoadoutModCount { get; init; } = "276 mods";
    public bool IsDeleting => false;
    [Reactive] public bool IsBusy { get; set; }
    public string BusyText { get; init; } = "Duplicating profile…";
    public bool IsSkeleton => false;
    public bool IsLastLoadout { get; init; }
    public ReactiveCommand<Unit, Unit> VisitLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> CloneLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> RenameLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> DeleteLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
}
