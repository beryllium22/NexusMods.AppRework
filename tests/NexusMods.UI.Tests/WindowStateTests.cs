using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NSubstitute;

namespace NexusMods.UI.Tests;

public class WindowStateTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task SavedWindowStateIsCommittedBeforeSaveReturns()
    {
        using var manager = new WindowManager(NullLogger<WindowManager>.Instance, Connection);
        var workspace = Substitute.For<IWorkspaceController>();
        workspace.ToData().Returns(new WindowData { ActiveWorkspaceId = null, Workspaces = [] });
        var window = Substitute.For<IWorkspaceWindow>();
        window.WorkspaceController.Returns(workspace);

        await OnUi(() => manager.SaveWindowState(window));

        var saved = WindowDataAttributes.All(Connection.Db).Single();
        saved.WindowData.Workspaces.Should().BeEmpty();
        saved.WindowData.ActiveWorkspaceId.Should().BeNull();
    }

    [Fact]
    public async Task RegisteredWindowIdsTrackRegistrationAndRemoval()
    {
        using var manager = new WindowManager(NullLogger<WindowManager>.Instance, Connection);
        var window = Substitute.For<IWorkspaceWindow>();
        window.WindowId.Returns(WindowId.NewId());

        await OnUi(() => manager.RegisterWindow(window));
        await EventuallyOnUi(() => manager.AllWindowIds.Should().Contain(window.WindowId));
        await OnUi(() => manager.UnregisterWindow(window));
        await EventuallyOnUi(() => manager.AllWindowIds.Should().BeEmpty());
    }
}
