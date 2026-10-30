using NexusMods.Paths;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Sdk.Tests.Jobs;

public class StreamProgressWrapperTests
{
    [Test]
    public async Task AnImmediateFirstTickPreservesTheResumedOffset()
    {
        using var stream = new MemoryStream(new byte[10]);
        stream.Position = 3;
        var notifications = new List<(Size Bytes, double Speed)>();

        using var wrapper = new StreamProgressWrapper<List<(Size, double)>>(
            stream,
            notifications,
            static (state, progress) => state.Add(progress),
            timeProvider: new ImmediateTimeProvider());

        await Assert.That(notifications.Count).IsEqualTo(1);
        await Assert.That(notifications[0].Bytes).IsEqualTo(Size.FromLong(3));
        await Assert.That(notifications[0].Speed).IsEqualTo(0);
    }

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Model the earliest possible scheduling: the first tick runs before the
            // constructor's CreateTimer call returns, without timing-dependent stress loops.
            callback(state);
            return new NoopTimer();
        }
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
