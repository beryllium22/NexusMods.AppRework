using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Downloads;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Library.Tests.DownloadsService.Helpers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.NexusModsApi;
using Xunit;

namespace NexusMods.Library.Tests.DownloadsService;

public class DownloadsServiceTests(
    IJobMonitor jobMonitor,
    IServiceProvider serviceProvider)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private readonly DownloadJobFactory _jobFactory = new(jobMonitor, serviceProvider);

    // A job's result is published before JobMonitor removes it. Observe removal
    // explicitly so assertions run after DownloadsService has processed that event.
    private IDisposable ObserveRemoval(JobId jobId, out Task removed)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        removed = completion.Task;
        return jobMonitor.GetObservableChangeSet<INexusModsDownloadJob>()
            .Subscribe(changes =>
            {
                if (changes.Any(change => change.Reason == ChangeReason.Remove && change.Key == jobId))
                    completion.TrySetResult();
            });
    }

    private Library.DownloadsService CreateService() =>
        new(jobMonitor, serviceProvider.GetRequiredService<IConnection>());

    private static Task WaitForActiveDownload(IObservableCache<DownloadInfo, DownloadId> downloads) => downloads.CountChanged
        .FirstAsync(count => count == 1)
        .ToTask()
        .WaitAsync(TestTimeout);

    [Fact]
    public async Task Validate_Download_Jobs_Lifetime()
    {
        using var service = CreateService();
        var gameId = NexusModsGameId.From(1234u);
        using var allDownloads = service.AllDownloads.AsObservableCache();
        using var completedDownloads = service.CompletedDownloads.AsObservableCache();
        using var activeDownloads = service.ActiveDownloads.AsObservableCache();
        using var gameDownloads = service.GetDownloadsForGame(gameId).AsObservableCache();

        allDownloads.Count.Should().Be(0);
        completedDownloads.Count.Should().Be(0);
        activeDownloads.Count.Should().Be(0);
        gameDownloads.Count.Should().Be(0);

        var context = _jobFactory.CreateAndStartDownloadJob(gameId);
        using var removalSubscription = ObserveRemoval(context.JobTask.Job.Id, out var removed);
        context.SignalJobsToStart();
        await WaitForActiveDownload(activeDownloads);

        allDownloads.Count.Should().Be(1);
        gameDownloads.Count.Should().Be(1);
        activeDownloads.Count.Should().Be(1);
        completedDownloads.Count.Should().Be(0);

        await context.CompleteJobAsync().WaitAsync(TestTimeout);
        await context.JobTask.Job.WaitAsync().WaitAsync(TestTimeout);
        await removed.WaitAsync(TestTimeout);

        allDownloads.Count.Should().Be(1, "completed downloads remain in the library view");
        completedDownloads.Count.Should().Be(1);
        activeDownloads.Count.Should().Be(0);
        gameDownloads.Count.Should().Be(1);
        var download = completedDownloads.Items.Single();
        download.Status.Value.Should().Be(JobStatus.Completed);
        download.Progress.Value.Should().Be(Percent.One);
        download.TransferRate.Value.Should().Be(Size.Zero);
        download.CompletedAt.Value.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task CompletedParent_FinalizesDownloadBeforeChildNotification()
    {
        using var service = CreateService();
        using var allDownloads = service.AllDownloads.AsObservableCache();
        using var completedDownloads = service.CompletedDownloads.AsObservableCache();
        using var activeDownloads = service.ActiveDownloads.AsObservableCache();
        var context = _jobFactory.CreateAndStartDownloadJob(NexusModsGameId.From(1234u));
        using var removalSubscription = ObserveRemoval(context.JobTask.Job.Id, out var removed);
        context.SignalJobsToStart();
        await WaitForActiveDownload(activeDownloads);

        try
        {
            // Deliberately invert the normal dependency: hold the HTTP job open
            // until the parent has been removed and the cache has finalized it.
            context.CompletionSource.TrySetResult(context.NexusJob.Destination);
            await removed.WaitAsync(TestTimeout);
            context.HttpJobTask.Job.WaitAsync().IsCompleted.Should().BeFalse();

            var download = allDownloads.Items.Single();
            download.Status.Value.Should().Be(JobStatus.Completed);
            download.Progress.Value.Should().Be(Percent.One);
            download.TransferRate.Value.Should().Be(Size.Zero);
            download.CompletedAt.Value.HasValue.Should().BeTrue();
            completedDownloads.Count.Should().Be(1);
            activeDownloads.Count.Should().Be(0);
        }
        finally
        {
            context.HttpCompletionSource.TrySetResult(context.HttpJob.Destination);
            await context.HttpJobTask.Job.WaitAsync().WaitAsync(TestTimeout);
        }

        allDownloads.Count.Should().Be(1);
        completedDownloads.Count.Should().Be(1, "a late child completion must not lose the retained download");
        activeDownloads.Count.Should().Be(0);
    }

    [Fact]
    public async Task CancelledJobs_ShouldBeCompletelyRemoved_FromAllCollections()
    {
        using var service = CreateService();
        using var allDownloads = service.AllDownloads.AsObservableCache();
        using var completedDownloads = service.CompletedDownloads.AsObservableCache();
        using var activeDownloads = service.ActiveDownloads.AsObservableCache();

        var context = _jobFactory.CreateAndStartDownloadJob(NexusModsGameId.From(1234u));
        using var removalSubscription = ObserveRemoval(context.JobTask.Job.Id, out var removed);
        await WaitForActiveDownload(activeDownloads);
        allDownloads.Count.Should().Be(1);
        activeDownloads.Count.Should().Be(1);

        context.CancelJob();
        context.SignalJobsToStart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.JobTask.Job.WaitAsync().WaitAsync(TestTimeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.HttpJobTask.Job.WaitAsync().WaitAsync(TestTimeout));
        await removed.WaitAsync(TestTimeout);

        allDownloads.Count.Should().Be(0);
        completedDownloads.Count.Should().Be(0, "cancellation must never be reported as successful completion");
        activeDownloads.Count.Should().Be(0);
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services) => DIHelpers.ConfigureServices(services);
    }
}
