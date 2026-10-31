using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using ReactiveUI;

namespace NexusMods.App.UI.Overlays;

/// <inheritdoc />
public class OverlayController : ReactiveObject, IOverlayController
{
    public IOverlayViewModel? CurrentOverlay { get; private set; }
    
    private readonly Queue<IOverlayViewModel> _queue = new();
    public void Enqueue(IOverlayViewModel overlayViewModel)
    {
        // Marshal before accessing the queue. Holding a worker-thread lock while
        // invoking the dispatcher deadlocks if the UI is closing another overlay.
        Dispatcher.UIThread.Invoke(() =>
        {
            if (overlayViewModel.Status == Status.Closed || ReferenceEquals(CurrentOverlay, overlayViewModel) || _queue.Contains(overlayViewModel)) return;

            overlayViewModel.Controller = this;
            _queue.Enqueue(overlayViewModel);
            
            ProcessNext();
        });
    }

    public async Task<TResult?> EnqueueAndWait<TResult>(IOverlayViewModel<TResult> overlayViewModel)
    {
        Enqueue(overlayViewModel);
        await overlayViewModel.CompletionTask;
        return overlayViewModel.Result;
    }

    private void ProcessNext()
    {
        if (CurrentOverlay != null)
        {
            return;
        }
        if (_queue.Count == 0)
        {
            return;
        }
        
        CurrentOverlay = _queue.Dequeue();
        CurrentOverlay.Status = Status.Visible;
        NotifyCurrentOverlayChanged();
    }

    private void NotifyCurrentOverlayChanged()
    {
        this.RaisePropertyChanged(nameof(CurrentOverlay));
    }

    public void Remove(IOverlayViewModel model)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (CurrentOverlay == model)
            {
                var oldOverlay = CurrentOverlay;
                oldOverlay.Status = Status.Closed;
                CurrentOverlay = null;
                NotifyCurrentOverlayChanged();
                ProcessNext();
            }
            else
            {
                var items = _queue.ToArray();
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item == model)
                        continue;
                    _queue.Enqueue(item);
                }
            }
        });
    }
}
