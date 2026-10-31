using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using FluentAssertions;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.App.UI.Pages.MyLoadouts;
using System.Reactive.Linq;

namespace NexusMods.UI.Tests;

public class ProfilesViewTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task CardActionsDoNotAlsoOpenTheProfileAndBusyStateDisablesThem()
    {
        await using var host = await App.GetControl<LoadoutCardView, LoadoutCardDesignViewModel, ILoadoutCardViewModel>();
        var opened = 0;
        var renamed = 0;
        var duplicated = 0;
        using var openSubscription = host.ViewModel.VisitLoadoutCommand.Subscribe(_ => opened++);
        using var renameSubscription = host.ViewModel.RenameLoadoutCommand.Subscribe(_ => renamed++);
        using var duplicateSubscription = host.ViewModel.CloneLoadoutCommand.Subscribe(_ => duplicated++);
        var rename = await host.GetViewControl<StandardButton>("RenameButton");
        var duplicate = await host.GetViewControl<StandardButton>("CreateCopyButton");
        await Click(rename);
        await Click(duplicate);
        await EventuallyOnUi(() =>
        {
            renamed.Should().Be(1);
            duplicated.Should().Be(1);
            opened.Should().Be(0);
            rename.GetVisualAncestors().OfType<Button>().Should().BeEmpty();
            duplicate.GetVisualAncestors().OfType<Button>().Should().BeEmpty();
            host.ViewModel.IsBusy = true;
        });
        await EventuallyOnUi(() =>
        {
            rename.IsEffectivelyEnabled.Should().BeFalse();
            duplicate.IsEffectivelyEnabled.Should().BeFalse();
            host.View.FindControl<StackPanel>("OverlayStack")!.IsVisible.Should().BeTrue();
        });
        await OnUi(() => host.ViewModel.IsBusy = false);
        await EventuallyOnUi(() => rename.IsEffectivelyEnabled.Should().BeTrue());
    }

    [Theory]
    [InlineData(680)]
    [InlineData(1100)]
    public async Task ProfilesPageLaysOutItsToolbarAndSeparateActionsAtDesktopWidths(int width)
    {
        await using var host = await App.GetControl<MyLoadoutsView, MyLoadoutsDesignViewModel, IMyLoadoutsViewModel>();
        await OnUi(() =>
        {
            host.Window.Width = width;
            host.Window.Height = 1100;
            host.Window.Background = host.View.FindResource("SurfaceLowBrush") as IBrush;
            host.Window.UpdateLayout();
        });
        await EventuallyOnUi(() =>
        {
            var cards = host.View.GetVisualDescendants().OfType<LoadoutCardView>().ToArray();
            cards.Should().HaveCount(4);
            foreach (var card in cards)
            {
                card.Bounds.Width.Should().BeGreaterThan(0).And.BeLessThan(width);
                card.FindControl<StandardButton>("RenameButton")!.Bounds.Width.Should().BeGreaterThan(40);
                card.FindControl<StandardButton>("CreateCopyButton")!.Bounds.Width.Should().BeGreaterThan(40);
            }
            host.View.FindControl<TextBox>("ProfileSearchTextBox")!.IsEffectivelyVisible.Should().BeTrue();
            host.View.FindControl<ComboBox>("ProfileSortComboBox")!.IsEffectivelyVisible.Should().BeTrue();
        });

        // Optional native Avalonia rendering for review; CI does not depend on a
        // platform-specific screenshot or change expected images automatically.
        var captureDirectory = Environment.GetEnvironmentVariable("NEXUS_REWORK_UI_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(captureDirectory))
        {
            await OnUi(() =>
            {
                Directory.CreateDirectory(captureDirectory);
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, 1100), new Vector(96, 96));
                bitmap.Render(host.Window);
                bitmap.Save(Path.Combine(captureDirectory, $"profiles-{width}.png"));
            });
        }
    }
}
