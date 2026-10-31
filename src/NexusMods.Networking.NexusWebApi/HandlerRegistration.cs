using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using R3;

namespace NexusMods.Networking.NexusWebApi;

internal class HandlerRegistration : BackgroundService
{
    private readonly ILogger _logger;
    private readonly IOSInterop _osInterop;
    private readonly ISettingsManager _settingsManager;

    public HandlerRegistration(
        ILogger<HandlerRegistration> logger,
        IOSInterop osInterop,
        ISettingsManager settingsManager)
    {
        _logger = logger;
        _osInterop = osInterop;
        _settingsManager = settingsManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _settingsManager.GetChanges<NxmLinkSettings>(prependCurrent: true)
            .Select(settings => settings.RegisterAsDefaultHandler)
            .DistinctUntilChanged()
            .SubscribeAwait(async (enabled, cancellationToken) =>
                {
                    if (!enabled) return;

                    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancellationToken);
                    await RegisterHandler(linkedCancellation.Token);
                },
                awaitOperation: AwaitOperation.Switch,
                configureAwait: false
            );

        // Keep observing saved settings so opting in applies without restarting the app.
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async ValueTask RegisterHandler(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _osInterop.RegisterUriSchemeHandler(scheme: "nxm", setAsDefaultHandler: true, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the app or disabling the setting cancels pending registration.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while registering handler for nxm links");
        }
    }
}
