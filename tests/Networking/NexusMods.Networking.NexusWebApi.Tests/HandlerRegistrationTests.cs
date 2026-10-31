using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NSubstitute;
using R3;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class HandlerRegistrationTests
{
    [Fact]
    public async Task DefaultSettingsLeaveExistingLinkHandlerUntouched()
    {
        using var changes = new Subject<NxmLinkSettings>();
        var os = Substitute.For<IOSInterop>();
        using var service = CreateService(os, new NxmLinkSettings(), changes);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        await os.DidNotReceiveWithAnyArgs().RegisterUriSchemeHandler(default!, default, default);
    }

    [Fact]
    public async Task SavedOptInRegistersTheHandlerOnStartup()
    {
        using var changes = new Subject<NxmLinkSettings>();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var os = Substitute.For<IOSInterop>();
        os.RegisterUriSchemeHandler("nxm", true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                registered.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using var service = CreateService(os, new NxmLinkSettings { RegisterAsDefaultHandler = true }, changes);
        await service.StartAsync(CancellationToken.None);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        await os.Received(1).RegisterUriSchemeHandler("nxm", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptInAppliesWithoutRestartAndStopsObservingAfterShutdown()
    {
        using var changes = new Subject<NxmLinkSettings>();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var os = Substitute.For<IOSInterop>();
        os.RegisterUriSchemeHandler("nxm", true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                registered.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using var service = CreateService(os, new NxmLinkSettings(), changes);
        await service.StartAsync(CancellationToken.None);
        await os.DidNotReceiveWithAnyArgs().RegisterUriSchemeHandler(default!, default, default);

        changes.OnNext(new NxmLinkSettings { RegisterAsDefaultHandler = true });
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Saving the same value again must not repeatedly reclaim the association.
        changes.OnNext(new NxmLinkSettings { RegisterAsDefaultHandler = true });
        changes.OnNext(new NxmLinkSettings());

        await service.StopAsync(CancellationToken.None);
        changes.OnNext(new NxmLinkSettings { RegisterAsDefaultHandler = true });

        await os.Received(1).RegisterUriSchemeHandler("nxm", true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisablingOrStoppingCancelsPendingRegistration(bool stopService)
    {
        using var changes = new Subject<NxmLinkSettings>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var os = Substitute.For<IOSInterop>();
        os.RegisterUriSchemeHandler("nxm", true, Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask(RegisterUntilCancelled(call.ArgAt<CancellationToken>(2))));

        using var service = CreateService(os, new NxmLinkSettings { RegisterAsDefaultHandler = true }, changes);
        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (stopService)
            await service.StopAsync(CancellationToken.None);
        else
            changes.OnNext(new NxmLinkSettings());

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        await os.Received(1).RegisterUriSchemeHandler("nxm", true, Arg.Any<CancellationToken>());

        async Task RegisterUntilCancelled(CancellationToken cancellationToken)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancelled.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task FailedRegistrationCanBeRetriedByOptingInAgain()
    {
        using var changes = new Subject<NxmLinkSettings>();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var os = Substitute.For<IOSInterop>();
        var attempts = 0;
        os.RegisterUriSchemeHandler("nxm", true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstAttempt.TrySetResult();
                    throw new IOException("The association could not be updated.");
                }

                secondAttempt.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using var service = CreateService(os, new NxmLinkSettings { RegisterAsDefaultHandler = true }, changes);
        await service.StartAsync(CancellationToken.None);
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        changes.OnNext(new NxmLinkSettings());
        changes.OnNext(new NxmLinkSettings { RegisterAsDefaultHandler = true });
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        await os.Received(2).RegisterUriSchemeHandler("nxm", true, Arg.Any<CancellationToken>());
    }

    private static HandlerRegistration CreateService(IOSInterop os, NxmLinkSettings current, Observable<NxmLinkSettings> changes)
    {
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.GetChanges<NxmLinkSettings>(key: null, prependCurrent: true)
            .Returns(changes.Prepend(current));

        return new HandlerRegistration(NullLogger<HandlerRegistration>.Instance, os, settingsManager);
    }
}
