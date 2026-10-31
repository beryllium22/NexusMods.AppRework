using Avalonia.Threading;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Sdk.Jobs;
using R3;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Overlays.Login;

public class NexusLoginOverlayViewModel : AOverlayViewModel<INexusLoginOverlayViewModel>, INexusLoginOverlayViewModel
{
    private readonly IDisposable? _loginUriSubscription;

    public NexusLoginOverlayViewModel(IJob job)
    {
        if (job.Definition is IOAuthJob oAuthJob)
        {
            _loginUriSubscription = oAuthJob.LoginUriSubject
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (uri, self) => self.Uri = uri);
        }

        Cancel = new ReactiveCommand(execute: _ =>
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                // A queued click must not cancel a job after this overlay has closed.
                if (Status == Status.Closed) return;

                try
                {
                    if (job.CanBeCancelled) job.AsContext().Cancel();
                }
                finally
                {
                    Close();
                }
            });
        });
    }

    public override void Close()
    {
        _loginUriSubscription?.Dispose();
        base.Close();
    }

    public ReactiveCommand Cancel { get; }
    [Reactive] public Uri? Uri { get; private set; }
}
