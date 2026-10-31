namespace NexusMods.Sdk.Jobs;

/// <summary>
/// A cancellation token that supports both cancellation and pause/resume
/// functionality for jobs. This class wraps a standard <see cref="CancellationToken"/>
/// and adds pause/resume capabilities.
/// </summary>
/// <remarks>
/// <para>
/// Jobs are paused immediately by calling <see cref="Pause()"/>, which cancels the current token.
/// A fresh token is prepared during pause and used when the job is explicitly resumed.
/// This is usually called as a result of calling <see cref="IJobMonitor.Resume"/>
/// </para>
/// </remarks>
public class JobCancellationToken : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _currentTokenSource;
    private CancellationReason _cancellationReason = CancellationReason.None;
    private readonly List<CancellationTokenSource> _retiredSources = [];
    private int _activeCancellationCalls;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="JobCancellationToken"/>.
    /// </summary>
    public JobCancellationToken()
    {
        _currentTokenSource = new CancellationTokenSource();
    }

    /// <summary>
    /// Gets a value indicating whether the job is currently paused.
    /// </summary>
    /// <remarks>
    /// A paused job will resume execution when <see cref="Resume()"/> is called.
    /// </remarks>
    public bool IsPaused
    {
        get { lock (_gate) return _cancellationReason == CancellationReason.Paused; }
    }
    
    /// <summary>
    /// Gets a value indicating whether the job has been cancelled.
    /// </summary>
    public bool IsCancelled
    {
        get { lock (_gate) return _cancellationReason == CancellationReason.Cancelled; }
    }
    
    /// <summary>
    /// Gets the underlying <see cref="CancellationToken"/> that can be used with standard .NET APIs.
    /// Pausing cancels the current token and prepares a fresh token for a later resume.
    /// </summary>
    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _currentTokenSource.Token;
            }
        }
    }
    
    /// <summary>
    /// Implicitly converts this <see cref="JobCancellationToken"/> to a <see cref="CancellationToken"/>
    /// for compatibility with existing APIs.
    /// </summary>
    /// <param name="jobToken">The job cancellation token to convert.</param>
    /// <returns>The underlying cancellation token.</returns>
    public static implicit operator CancellationToken(JobCancellationToken jobToken) 
        => jobToken.Token;
    
    /// <summary>
    /// Throws an <see cref="OperationCanceledException"/> if cancellation has been requested.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// The token has been canceled.
    /// </exception>
    /// <remarks>
    /// This method provides the same functionality as <see cref="CancellationToken.ThrowIfCancellationRequested"/>
    /// for convenience when working with <see cref="JobCancellationToken"/>.
    /// </remarks>
    public void ThrowIfCancellationRequested() 
    {
        // Note(sewer): The source of truth in cancellation is the _cancellationReason field.
        // The inner cancellation token is only used for compatibility with 
        // external APIs which are unaware of features like pause.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancellationReason != CancellationReason.None)
                throw new OperationCanceledException();
        }
    }
    
    /// <summary>
    /// Cancels the job, preventing it from resuming if paused.
    /// This operation cannot be undone.
    /// </summary>
    /// <remarks>
    /// Once cancelled, the job cannot be resumed and will throw <see cref="OperationCanceledException"/>
    /// when <see cref="IJobContext.YieldAsync()"/> is called.
    /// </remarks>
    public void Cancel()
    {
        CancellationTokenSource source;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancellationReason == CancellationReason.Cancelled) return;
            _cancellationReason = CancellationReason.Cancelled;
            source = _currentTokenSource;
            _activeCancellationCalls++;
        }

        try
        {
            source.Cancel();
        }
        finally
        {
            FinishCancellation();
        }
    }
    
    /// <summary>
    /// Requests the job to pause immediately by cancelling the current token.
    /// </summary>
    /// <remarks>
    /// This operation immediately cancels the current token, allowing instant pause response.
    /// A fresh token is prepared immediately for eventual resume.
    /// If the job is already cancelled, this operation has no effect.
    /// </remarks>
    public void Pause()
    {
        CancellationTokenSource previousSource;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancellationReason != CancellationReason.None) return;

            _cancellationReason = CancellationReason.Paused;
            previousSource = _currentTokenSource;
            // Publish the resume token before callbacks run. A callback can cancel this job;
            // that cancellation must target the new source and must never be overwritten.
            _currentTokenSource = new CancellationTokenSource();
            _activeCancellationCalls++;
        }

        try
        {
            // Cancellation callbacks may re-enter this object or wait on another thread.
            // Never invoke them while holding the state gate.
            previousSource.Cancel();
        }
        finally
        {
            FinishCancellation(previousSource);
        }
    }
    
    /// <summary>
    /// Resumes a paused job by clearing the pause flag.
    /// </summary>
    /// <remarks>
    /// This operation clears the pause flag, allowing the job to be restarted.
    /// This method is for job framework internal use - external callers should use <see cref="IJobMonitor.Resume"/> instead.
    /// If the job is not paused or has been cancelled, this operation is ignored.
    /// </remarks>
    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancellationReason != CancellationReason.Paused) return;
            _cancellationReason = CancellationReason.None;
            // Pause already prepared the token; terminal cancellation is never cleared.
        }
    }

    private void FinishCancellation(CancellationTokenSource? retiredSource = null)
    {
        CancellationTokenSource[] sourcesToDispose;
        lock (_gate)
        {
            if (retiredSource is not null) _retiredSources.Add(retiredSource);
            _activeCancellationCalls--;
            sourcesToDispose = TakeRetiredSources();
        }
        foreach (var source in sourcesToDispose) source.Dispose();
    }

    // Caller holds the state gate. Disposal is deferred while any Cancel callback is running,
    // including when a callback itself calls Dispose().
    private CancellationTokenSource[] TakeRetiredSources()
    {
        if (_activeCancellationCalls != 0 || _retiredSources.Count == 0) return [];
        var sources = _retiredSources.ToArray();
        _retiredSources.Clear();
        return sources;
    }

    /// <summary>
    /// Releases all resources used by this <see cref="JobCancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// After disposal, this token should not be used. Any pending operations may throw exceptions.
    /// </remarks>
    public void Dispose()
    {
        CancellationTokenSource[] sourcesToDispose;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _retiredSources.Add(_currentTokenSource);
            sourcesToDispose = TakeRetiredSources();
        }
        foreach (var source in sourcesToDispose) source.Dispose();
    }
}

/// <summary>
/// Represents the reason why a job's execution was interrupted.
/// </summary>
internal enum CancellationReason
{
    /// <summary>
    /// The job is running normally and has not been interrupted.
    /// </summary>
    None = 0,
    
    /// <summary>
    /// The job has been cancelled and will not resume.
    /// </summary>
    Cancelled = 1,
    
    /// <summary>
    /// The job has been paused and can be resumed later.
    /// </summary>
    Paused = 2,
}
