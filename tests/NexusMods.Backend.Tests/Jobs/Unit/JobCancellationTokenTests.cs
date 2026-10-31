using NexusMods.Sdk.Jobs;

namespace NexusMods.Backend.Tests.Jobs.Unit;

public class JobCancellationTokenTests
{
    [Test]
    public async Task Should_Initialize_In_Running_State()
    {
        // Arrange & Act
        var token = new JobCancellationToken();

        // Assert
        await Assert.That(token.IsPaused).IsFalse();
        await Assert.That(token.Token.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Should_Support_Basic_Cancellation()
    {
        // Arrange
        var token = new JobCancellationToken();

        // Act
        token.Cancel();

        // Assert
        await Assert.That(token.Token.IsCancellationRequested).IsTrue();
        await Assert.That(token.IsCancelled).IsTrue();
    }

    [Test]
    public async Task Should_Cancel_And_Set_Cancellation_Token()
    {
        // Arrange
        var token = new JobCancellationToken();

        // Act
        token.Cancel();

        // Assert
        await Assert.That(token.Token.IsCancellationRequested).IsTrue();
        await Assert.That(token.IsPaused).IsFalse(); // Cancelled jobs are not considered paused
    }

    [Test]
    public async Task Should_Cancel_And_Recycle_Token_on_Pause()
    {
        // Arrange
        var token = new JobCancellationToken();

        // Act
        var originalToken = token.Token;
        token.Pause();

        // Assert
        await Assert.That(token.IsPaused).IsTrue();
        
        // Original token should request cancellation,
        // while new token should be recycled
        await Assert.That(originalToken.IsCancellationRequested).IsTrue();
        await Assert.That(token.Token.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Should_Resume_From_Paused_State()
    {
        // Arrange
        var token = new JobCancellationToken();
        token.Pause();

        // Act
        token.Resume();

        // Assert
        await Assert.That(token.IsPaused).IsFalse();
    }

    [Test]
    public async Task Should_Ignore_Resume_When_Not_Paused()
    {
        // Arrange
        var token = new JobCancellationToken();

        // Act
        token.Resume();

        // Assert
        await Assert.That(token.IsPaused).IsFalse();
    }

    [Test]
    public async Task Should_Ignore_Resume_When_Cancelled()
    {
        // Arrange
        var token = new JobCancellationToken();
        token.Cancel();

        // Act
        token.Resume();

        // Assert
        await Assert.That(token.Token.IsCancellationRequested).IsTrue();
        await Assert.That(token.IsPaused).IsFalse();
    }

    [Test]
    public async Task CancellationFromPauseCallbackCannotBeReplacedByAFreshToken()
    {
        using var token = new JobCancellationToken();
        var originalToken = token.Token;
        using var registration = originalToken.Register(token.Cancel);

        token.Pause();
        token.Resume();
        token.Pause();

        await Assert.That(originalToken.IsCancellationRequested).IsTrue();
        await Assert.That(token.IsCancelled).IsTrue();
        await Assert.That(token.IsPaused).IsFalse();
        await Assert.That(token.Token.IsCancellationRequested).IsTrue();
        Action throwIfCancelled = token.ThrowIfCancellationRequested;
        await Assert.That(throwIfCancelled).ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task ConcurrentCancelWhilePauseCallbackIsRunningRemainsTerminal()
    {
        using var token = new JobCancellationToken();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The test did not release the pause callback.");
        });
        var pausing = Task.Run(token.Pause);
        Task? cancelling = null;
        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancelling = Task.Run(token.Cancel);
            // Cancel must not wait for a state lock held by the blocked callback.
            await cancelling.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseCallback.Set();
            await Task.WhenAll(pausing, cancelling ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
        }

        token.Resume();
        await Assert.That(token.IsCancelled).IsTrue();
        await Assert.That(token.IsPaused).IsFalse();
        await Assert.That(token.Token.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task RepeatedPauseKeepsThePreparedTokenUntilResume()
    {
        using var token = new JobCancellationToken();
        token.Pause();
        var preparedToken = token.Token;
        token.Pause();

        await Assert.That(token.Token).IsEqualTo(preparedToken);
        await Assert.That(preparedToken.IsCancellationRequested).IsFalse();
        token.Resume();
        await Assert.That(token.IsPaused).IsFalse();
        await Assert.That(token.Token).IsEqualTo(preparedToken);

        token.Cancel();
        token.Pause();
        token.Resume();
        await Assert.That(token.Token).IsEqualTo(preparedToken);
        await Assert.That(token.Token.IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposalFromCancellationCallbackWaitsForCallbackCompletion(bool pause)
    {
        using var token = new JobCancellationToken();
        var originalToken = token.Token;
        using var registration = originalToken.Register(() =>
        {
            token.Dispose();
            // The old source must remain alive while its callback is executing, even when
            // Dispose is called reentrantly. Accessing this throws if it was disposed early.
            _ = originalToken.WaitHandle;
        });

        if (pause) token.Pause();
        else token.Cancel();

        await Assert.That(originalToken.IsCancellationRequested).IsTrue();
        Action readCurrentToken = () => _ = token.Token;
        Action pauseAfterDisposal = token.Pause;
        await Assert.That(readCurrentToken).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(pauseAfterDisposal).ThrowsExactly<ObjectDisposedException>();
    }
}
