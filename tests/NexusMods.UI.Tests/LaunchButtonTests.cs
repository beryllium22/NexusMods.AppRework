using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI;
using NexusMods.App.UI.LeftMenu.Items;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Resources;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Jobs;
using NSubstitute;

namespace NexusMods.UI.Tests;

public class LaunchButtonTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task ALoadoutReadFailureIsShownAndTheButtonRecoversOnTheUiThread()
    {
        var connection = Substitute.For<IConnection>();
        connection.Db.Returns(_ => throw new InvalidOperationException("The saved loadout could not be read."));
        var controller = new OverlayController();
        MessageBoxOkViewModel? message = null;
        controller.PropertyChanged += (_, _) =>
        {
            if (controller.CurrentOverlay is not MessageBoxOkViewModel current) return;
            message = current;
            current.Close();
        };
        using var services = new ServiceCollection().AddSingleton<IOverlayController>(controller).BuildServiceProvider();
        var monitor = Provider.GetRequiredService<IJobMonitor>();
        var viewModel = new LaunchButtonViewModel(NullLogger<ILaunchButtonViewModel>.Instance,
            Substitute.For<IToolManager>(), connection, monitor, services, new GameRunningTracker(monitor));
        var labelChangesOnUi = true;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.Label)) labelChangesOnUi &= Dispatcher.UIThread.CheckAccess();
        };

        await viewModel.Command.Execute().ToTask().WaitAsync(TimeSpan.FromSeconds(10));

        message.Should().NotBeNull();
        message!.Title.Should().Be("Could not launch the game");
        message.Description.Should().Be("The saved loadout could not be read.");
        viewModel.Label.Should().Be(Language.LaunchButtonViewModel_LaunchGame_LAUNCH);
        labelChangesOnUi.Should().BeTrue();
    }
}
