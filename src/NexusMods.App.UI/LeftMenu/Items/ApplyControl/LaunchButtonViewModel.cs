using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Resources;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.Telemetry;
using NexusMods.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.LeftMenu.Items;

public class LaunchButtonViewModel : AViewModel<ILaunchButtonViewModel>, ILaunchButtonViewModel
{
    [Reactive] public LoadoutId LoadoutId { get; set; } = Initializers.LoadoutId;

    [Reactive] public ReactiveCommand<Unit, Unit> Command { get; set; }

    public IObservable<bool> IsRunningObservable => _gameRunningTracker.GetWithCurrentStateAsStarting();

    [Reactive] public string Label { get; set; } = Language.LaunchButtonViewModel_LaunchGame_LAUNCH;

    [Reactive] public Percent? Progress { get; set; }

    private readonly ILogger<ILaunchButtonViewModel> _logger;
    private readonly IToolManager _toolManager;
    private readonly IConnection _conn;
    private readonly IJobMonitor _monitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly GameRunningTracker _gameRunningTracker;

    public LaunchButtonViewModel(ILogger<ILaunchButtonViewModel> logger, IToolManager toolManager, IConnection conn, IJobMonitor monitor, IServiceProvider serviceProvider, GameRunningTracker gameRunningTracker)
    {
        _logger = logger;
        _toolManager = toolManager;
        _conn = conn;
        _monitor = monitor;
        _serviceProvider = serviceProvider;
        _gameRunningTracker = gameRunningTracker;
        
        this.WhenActivated(cd =>
        {
            _gameRunningTracker.GetWithCurrentStateAsStarting().OnUI().Subscribe(isRunning =>
            {
                if (isRunning)
                    SetLabelToRunning();
                else
                    SetLabelToLaunch();
            }).DisposeWith(cd);
        });

        Command = ReactiveCommand.CreateFromObservable(() => Observable.StartAsync(LaunchGame, RxApp.TaskpoolScheduler));
    }

    private async Task LaunchGame(CancellationToken token)
    {
        var marker = default(Sdk.Loadouts.Loadout.ReadOnly);
        await Dispatcher.UIThread.InvokeAsync(SetLabelToRunning);
        try
        {
            token.ThrowIfCancellationRequested();
            marker = Sdk.Loadouts.Loadout.Load(_conn.Db, LoadoutId);
            if (!marker.IsValid()) throw new InvalidOperationException("This loadout is no longer available. Select an existing loadout and try again.");

            var tool = _toolManager.GetTools(marker).OfType<IRunGameTool>().FirstOrDefault()
                ?? throw new InvalidOperationException("No launch tool is available for this game.");
            await Task.Run(async () =>
            {
                var installation = marker.InstallationInstance;
                var sw = Stopwatch.StartNew();
                try
                {
                    Tracking.AddEvent(Events.Game.LaunchGame, new EventMetadata(name: $"{installation.Game.DisplayName} - {installation.LocatorResult.Store}"));
                    await _toolManager.RunTool(tool, marker, _monitor, token: token);
                }
                finally
                {
                    var duration = sw.Elapsed;
                    Tracking.AddEvent(Events.Game.ExitGame, EventMetadata.Create(name: $"{installation.Game.DisplayName} - {installation.LocatorResult.Store}", value: duration.TotalSeconds));
                }
            }, token);
        }
        catch (ExecutableInUseException)
        {
            await MessageBoxOkViewModel.ShowGameAlreadyRunningError(_serviceProvider, marker.Installation.Name);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Game launch was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error launching game");
            await MessageBoxOkViewModel.Show(_serviceProvider, "Could not launch the game", ex.Message);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(SetLabelToLaunch);
        }
    }
    private void SetLabelToRunning() => Label = Language.LaunchButtonViewModel_LaunchGame_RUNNING;
    private void SetLabelToLaunch() => Label = Language.LaunchButtonViewModel_LaunchGame_LAUNCH;
}
