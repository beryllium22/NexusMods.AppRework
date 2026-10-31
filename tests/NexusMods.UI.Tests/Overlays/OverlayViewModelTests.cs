using FluentAssertions;
using NexusMods.App.UI.Overlays;
using Avalonia.Threading;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.UI.Tests.Overlays;

public class OverlayViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]

    public void SetOverlayContent_CanGetLastOverlayViewModel()
    {
        // Arrange
        var overlay = new MockOverlayViewModel();
        var controller = new OverlayController();
        
        // Act
        controller.Enqueue(overlay);
        
        // Assert that the last overlay is the one we just added
        controller.CurrentOverlay.Should().Be(overlay);
    }
    
    [Fact]

    public void SetOverlayContent_WithTwoOverlays_CanGetLastOverlayViewModel()
    {
        // Arrange
        var overlay1 = new MockOverlayViewModel();
        var overlay2 = new MockOverlayViewModel();
        
        var controller = new OverlayController();

        // Act
        controller.Enqueue(overlay1);
        controller.Enqueue(overlay2);
        
        // Check if first overlay is loaded.
        controller.CurrentOverlay.Should().Be(overlay1);
        overlay1.Status.Should().Be(Status.Visible);
        overlay2.Status.Should().Be(Status.Hidden);
        
        overlay1.Close();
        
        // Assert next modal got loaded.
        controller.CurrentOverlay.Should().Be(overlay2);
        overlay2.Status.Should().Be(Status.Visible);
        
        // Assert last modal is closed
        overlay1.Status.Should().Be(Status.Closed);
    }
    
    [Fact]

    public void SetOverlayContent_WithThreeOverlays_CanGetLastOverlayViewModel()
    {
        // Arrange
        var overlay1 = new MockOverlayViewModel();
        var overlay2 = new MockOverlayViewModel();
        var overlay3 = new MockOverlayViewModel();
        
        var controller = new OverlayController();

        // Act
        controller.Enqueue(overlay1);
        controller.Enqueue(overlay2);
        controller.Enqueue(overlay3);
        
        // Check if first overlay is loaded.
        controller.CurrentOverlay.Should().Be(overlay1);
        overlay1.Status.Should().Be(Status.Visible);
        overlay2.Status.Should().Be(Status.Hidden);
        overlay3.Status.Should().Be(Status.Hidden);
        controller.CurrentOverlay!.Close();
        
        // Assert next modal got loaded.
        controller.CurrentOverlay.Should().Be(overlay2);
        overlay1.Status.Should().Be(Status.Closed);
        overlay2.Status.Should().Be(Status.Visible);
        overlay3.Status.Should().Be(Status.Hidden);
        controller.CurrentOverlay!.Close();
        
        // Assert next modal got loaded.
        controller.CurrentOverlay.Should().Be(overlay3);
        overlay1.Status.Should().Be(Status.Closed);
        overlay2.Status.Should().Be(Status.Closed);
        overlay3.Status.Should().Be(Status.Visible);
    }
    
    [Fact]
    public async Task SetOverlayViewModel_WhenActiveIsFalse_TaskIsCompleted()
    {
        // Arrange
        var overlay = new MockOverlayViewModel();
        var tcs = new TaskCompletionSource<bool>();
        
        var controller = new OverlayController();
        var updated = false;

        // Act
        using var sub = overlay.CompletionTask.ContinueWith(t =>
        {
            updated = true;
            tcs.SetResult(t.IsCompletedSuccessfully);
        });
        controller.Enqueue(overlay);
        overlay.Close();

        // Assert
        await tcs.Task;
        updated.Should().BeTrue();
        await sub;
    }
    
    [Fact]
    public async Task ConcurrentBackgroundEnqueueAndCloseCompleteOnTheUiThread()
    {
        var controller = new OverlayController();
        var notificationsOnUi = true;
        controller.PropertyChanged += (_, _) => notificationsOnUi &= Dispatcher.UIThread.CheckAccess();
        var overlays = Enumerable.Range(0, 12).Select(_ => new MockOverlayViewModel()).ToArray();

        await Task.WhenAll(overlays.Select(overlay => Task.Run(() => controller.Enqueue(overlay))))
            .WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(overlays.Select(overlay => Task.Run(overlay.Close)))
            .WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(overlays.Select(overlay => overlay.CompletionTask));

        controller.CurrentOverlay.Should().BeNull();
        notificationsOnUi.Should().BeTrue();
        overlays.Should().OnlyContain(overlay => overlay.Status == Status.Closed);
    }

    [Fact]
    public async Task ClosingAQueuedOverlayTwicePreservesTheRemainingQueue()
    {
        var controller = new OverlayController();
        var first = new MockOverlayViewModel();
        var second = new MockOverlayViewModel();
        var third = new MockOverlayViewModel();
        controller.Enqueue(first);
        controller.Enqueue(second);
        controller.Enqueue(third);

        await Task.WhenAll(Task.Run(second.Close), Task.Run(second.Close)).WaitAsync(TimeSpan.FromSeconds(10));
        second.CompletionTask.IsCompletedSuccessfully.Should().BeTrue();
        controller.CurrentOverlay.Should().BeSameAs(first);
        first.Close();
        controller.CurrentOverlay.Should().BeSameAs(third);
        third.Close();
    }

    [Fact]
    public void AnOverlayClosedBeforeEnqueueIsNotShown()
    {
        var controller = new OverlayController();
        var overlay = new MockOverlayViewModel();
        overlay.Close();

        controller.Enqueue(overlay);

        overlay.CompletionTask.IsCompletedSuccessfully.Should().BeTrue();
        controller.CurrentOverlay.Should().BeNull();
    }

    [Fact]
    public async Task CompletingAnOverlayTwiceKeepsTheFirstResult()
    {
        var controller = new OverlayController();
        var overlay = new ResultOverlayViewModel();
        var completion = controller.EnqueueAndWait(overlay);

        await Task.Run(() =>
        {
            overlay.Complete("First response");
            overlay.Complete("Second response");
        }).WaitAsync(TimeSpan.FromSeconds(10));

        (await completion).Should().Be("First response");
        overlay.Result.Should().Be("First response");
        overlay.Status.Should().Be(Status.Closed);
        controller.CurrentOverlay.Should().BeNull();
    }

    [Fact]
    public async Task CompletingAnAlreadyClosedOverlayDoesNotReplaceItsCancelledResult()
    {
        var controller = new OverlayController();
        var overlay = new ResultOverlayViewModel();
        var completion = controller.EnqueueAndWait(overlay);
        overlay.Close();

        await Task.Run(() => overlay.Complete("Late response")).WaitAsync(TimeSpan.FromSeconds(10));

        (await completion).Should().BeNull();
        overlay.Result.Should().BeNull();
        overlay.CompletionTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    public class MockOverlayViewModel : AOverlayViewModel<IOverlayViewModel>
    {
    }

    public class ResultOverlayViewModel : AOverlayViewModel<IOverlayViewModel<string>, string>
    {
    }
}
