using FluentAssertions;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Login;
using NexusMods.Sdk.Jobs;
using NSubstitute;
using R3;

namespace NexusMods.UI.Tests.Overlays;

public class NexusLoginOverlayTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task CancelStopsTheOAuthJobAndClosesTheOverlay()
    {
        using var loginUri = new BehaviorSubject<Uri?>(new Uri("https://example.invalid/login"));
        var definition = Substitute.For<IOAuthJob>();
        definition.LoginUriSubject.Returns(loginUri);
        var context = Substitute.For<IJobContext>();
        var job = Substitute.For<IJob>();
        job.Definition.Returns(definition);
        job.CanBeCancelled.Returns(true);
        job.AsContext().Returns(context);
        var overlay = await OnUi(() => Task.FromResult(new NexusLoginOverlayViewModel(job)));
        var controller = new OverlayController();
        controller.Enqueue(overlay);

        await OnUi(() => overlay.Cancel.Execute(Unit.Default));
        await OnUi(() => overlay.Cancel.Execute(Unit.Default));

        context.Received(1).Cancel();
        overlay.CompletionTask.IsCompletedSuccessfully.Should().BeTrue();
        controller.CurrentOverlay.Should().BeNull();
    }

    [Fact]
    public async Task ALateCancelAfterTheOverlayClosesDoesNotCancelTheJob()
    {
        var context = Substitute.For<IJobContext>();
        var job = Substitute.For<IJob>();
        job.CanBeCancelled.Returns(true);
        job.AsContext().Returns(context);
        var overlay = await OnUi(() => Task.FromResult(new NexusLoginOverlayViewModel(job)));
        var controller = new OverlayController();
        controller.Enqueue(overlay);

        overlay.Close();
        await OnUi(() => overlay.Cancel.Execute(Unit.Default));

        context.DidNotReceive().Cancel();
        overlay.CompletionTask.IsCompletedSuccessfully.Should().BeTrue();
    }
}
