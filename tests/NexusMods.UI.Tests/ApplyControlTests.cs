using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI;
using NexusMods.App.UI.LeftMenu.Items;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.Backend.Jobs;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using NSubstitute;

namespace NexusMods.UI.Tests;

public class ApplyControlTests(IServiceProvider provider) : AUiTest(provider)
{
    public enum ApplyOutcome { Failed, Cancelled, Completed }

    [Theory]
    [InlineData(ApplyOutcome.Failed)]
    [InlineData(ApplyOutcome.Cancelled)]
    [InlineData(ApplyOutcome.Completed)]
    public async Task ApplyReportsItsOutcomeAndRecoversAfterTheRealJobFinishes(ApplyOutcome outcome)
    {
        const string status = "Deploying REDmods...";
        var timeout = TimeSpan.FromSeconds(10);
        var loadout = await CreateLoadout();
        using var monitor = new JobMonitor(NullLogger<JobMonitor>.Instance);
        var jobRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobRemoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Subscribe before starting the command: an already-empty DynamicData
        // cache need not emit an initial changeset to a late subscriber.
        using var monitorSubscription = monitor.GetObservableChangeSet<SynchronizeLoadoutJob>()
            .QueryWhenChanged(jobs => jobs.Count)
            .Subscribe(count =>
            {
                if (count > 0) jobRegistered.TrySetResult();
                else if (jobRegistered.Task.IsCompleted) jobRemoved.TrySetResult();
            });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<IJobContext<SynchronizeLoadoutJob>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("REDmod deployment failed. Check that the REDmod DLC is installed and try Apply again.");
        IDisposable? activation = null;
        Task? execution = null;
        Exception? testFailure = null;

        try
        {
            var synchronizer = Substitute.For<ISynchronizerService>();
            synchronizer.StatusForGame(loadout.InstallationId).Returns(Observable.Return(GameSynchronizerState.Idle));
            synchronizer.StatusForLoadout(loadout.LoadoutId)
                .Returns(Task.FromResult(Observable.Return(LoadoutSynchronizerState.NeedsSync)));
            synchronizer.Synchronize(loadout.LoadoutId).Returns(_ => RunJob());

            async Task RunJob()
            {
                await monitor.Begin<SynchronizeLoadoutJob, R3.Unit>(new SynchronizeLoadoutJob(loadout.LoadoutId), async context =>
                {
                    context.Definition.SetStatus(status);
                    entered.TrySetResult(context);
                    await release.Task.WaitAsync(context.CancellationToken);
                    if (outcome == ApplyOutcome.Failed) throw failure;
                    return R3.Unit.Default;
                });
            }

            var controller = new OverlayController();
            var messages = new List<MessageBoxOkViewModel>();
            var notificationsOnUi = true;
            controller.PropertyChanged += (_, _) =>
            {
                notificationsOnUi &= Dispatcher.UIThread.CheckAccess();
                if (controller.CurrentOverlay is not MessageBoxOkViewModel message) return;
                messages.Add(message);
                message.Close();
            };
            var notifications = Substitute.For<IWindowNotificationService>();
            var toasts = new List<ToastNotificationVariant>();
            notifications.When(service => service.ShowToast(Arg.Any<string>(), Arg.Any<ToastNotificationVariant>()))
                .Do(call =>
                {
                    notificationsOnUi &= Dispatcher.UIThread.CheckAccess();
                    toasts.Add(call.ArgAt<ToastNotificationVariant>(1));
                });
            var logger = Substitute.For<ILogger<ApplyControlViewModel>>();
            using var services = new ServiceCollection()
                .AddSingleton<IConnection>(Connection)
                .AddSingleton<ISynchronizerService>(synchronizer)
                .AddSingleton<IJobMonitor>(monitor)
                .AddSingleton<IOverlayController>(controller)
                .AddSingleton<IWindowNotificationService>(notifications)
                .AddSingleton<ILogger<ApplyControlViewModel>>(logger)
                .AddSingleton(Provider.GetRequiredService<IGameRegistry>())
                .AddSingleton(Substitute.For<IWindowManager>())
                .AddSingleton(Substitute.For<ILaunchButtonViewModel>())
                .BuildServiceProvider();
            var viewModel = new ApplyControlViewModel(loadout.LoadoutId, services, monitor, controller, new GameRunningTracker(monitor));
            var propertiesChangedOnUi = true;
            viewModel.PropertyChanged += (_, args) =>
            {
                propertiesChangedOnUi &= Dispatcher.UIThread.CheckAccess();
                if (args.PropertyName == nameof(viewModel.IsApplying))
                {
                    if (viewModel.IsApplying) applying.TrySetResult();
                    else if (applying.Task.IsCompleted) recovered.TrySetResult();
                }
                if (args.PropertyName == nameof(viewModel.ProcessingText))
                {
                    if (viewModel.ProcessingText == status) progressShown.TrySetResult();
                    else if (viewModel.ProcessingText == "" && progressShown.Task.IsCompleted) progressCleared.TrySetResult();
                }
            };
            var commandErrors = new List<Exception>();
            using var errorSubscription = viewModel.ApplyCommand.ThrownExceptions.Subscribe(commandErrors.Add);
            await OnUi(() => activation = viewModel.Activator.Activate());
            await viewModel.ApplyCommand.CanExecute.FirstAsync(canExecute => canExecute).ToTask().WaitAsync(timeout);

            // Executing off the UI thread proves that both dialog and success toast
            // delivery are marshalled, rather than depending on a button click's context.
            execution = Task.Run(() => viewModel.ApplyCommand.Execute().ToTask());
            var job = await entered.Task.WaitAsync(timeout);
            await Task.WhenAll(applying.Task, progressShown.Task).WaitAsync(timeout);
            await OnUi(() =>
            {
                viewModel.IsApplying.Should().BeTrue();
                viewModel.ProcessingText.Should().Be(status);
            });

            if (outcome == ApplyOutcome.Cancelled) job.Cancel();
            else release.TrySetResult();

            await execution.WaitAsync(timeout);
            await Task.WhenAll(recovered.Task, progressCleared.Task).WaitAsync(timeout);
            await viewModel.ApplyCommand.CanExecute.FirstAsync(canExecute => canExecute).ToTask().WaitAsync(timeout);
            await OnUi(() =>
            {
                viewModel.IsApplying.Should().BeFalse();
                viewModel.IsProcessing.Should().BeFalse();
                viewModel.ProcessingText.Should().BeEmpty();
                viewModel.ApplyButtonText.Should().Be(Language.ApplyControlViewModel__APPLY);
                controller.CurrentOverlay.Should().BeNull();
            });
            propertiesChangedOnUi.Should().BeTrue();
            notificationsOnUi.Should().BeTrue();
            commandErrors.Should().BeEmpty();

            var errorLogs = logger.ReceivedCalls()
                .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
                .Select(call => call.GetArguments())
                .Where(arguments => (LogLevel)arguments[0]! == LogLevel.Error)
                .ToArray();
            if (outcome == ApplyOutcome.Failed)
            {
                messages.Should().ContainSingle();
                messages[0].Title.Should().Be("Could not apply changes");
                messages[0].Description.Should().Be(failure.Message);
                errorLogs.Should().ContainSingle();
                errorLogs[0][3].Should().BeSameAs(failure);
                toasts.Should().BeEmpty();
                ((IJob)job).Status.Should().Be(JobStatus.Failed);
            }
            else
            {
                messages.Should().BeEmpty();
                errorLogs.Should().BeEmpty();
                if (outcome == ApplyOutcome.Cancelled)
                {
                    toasts.Should().BeEmpty();
                    ((IJob)job).Status.Should().Be(JobStatus.Cancelled);
                }
                else
                {
                    toasts.Should().Equal(ToastNotificationVariant.Success);
                    ((IJob)job).Status.Should().Be(JobStatus.Completed);
                }
            }
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            release.TrySetResult();
            var cleanupFailures = new List<Exception>();
            await Cleanup(async () =>
            {
                if (execution is not null) await execution.WaitAsync(timeout);
            });
            // JobMonitor removes a completed job after completing its result task.
            // The early subscription also retains a removal that already happened.
            await Cleanup(async () =>
            {
                if (jobRegistered.Task.IsCompleted) await jobRemoved.Task.WaitAsync(timeout);
            });
            await Cleanup(() => OnUi(() => activation?.Dispose()));
            await Cleanup(async () =>
            {
                using var tx = Connection.BeginTransaction();
                tx.Delete(loadout.Id, false);
                tx.Delete(loadout.Installation.Id, false);
                await tx.Commit();
            });

            if (cleanupFailures.Count > 0)
            {
                if (testFailure is not null) cleanupFailures.Insert(0, testFailure);
                throw new AggregateException("Apply test cleanup failed; any original test failure is included.", cleanupFailures);
            }

            async Task Cleanup(Func<Task> action)
            {
                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
        }
    }

    private async Task<Loadout.ReadOnly> CreateLoadout()
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations()
            .First(candidate => candidate.Game.GameId == Cyberpunk2077Game.GameId);
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            GameId = installation.Game.NexusModsGameId.Value,
            Store = installation.LocatorResult.Store,
            Path = installation.Locations[LocationId.Game].Path.ToString(),
            Name = "Apply outcome test",
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Apply outcome test",
            ShortName = "T",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
            LocatorIds = [LocatorId.From("test-build")],
        };
        return (await tx.Commit()).Remap(loadout);
    }
}
