using System.Reactive.Linq;
using DynamicData;
using FluentAssertions;
using NexusMods.Sdk.Reactive;

namespace NexusMods.DataModel.Tests;

public class QueryObservableLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisposingASubscriptionStopsItsNativeQuery()
    {
        await using var fixture = await QueryLifetimeTestHost.Create();
        var firstResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        using var subscription = fixture.Query.ObserveOwned(value => value).Subscribe(changes =>
        {
            Interlocked.Increment(ref notifications);
            if (changes.Any(change => change.Reason == ChangeReason.Add)) firstResult.TrySetResult();
        }, exception => firstResult.TrySetException(exception));

        await firstResult.Task.WaitAsync(Timeout);
        await fixture.Connection.FlushQueries().WaitAsync(Timeout);
        var executions = fixture.Table.Executions;
        executions.Should().BeGreaterThan(0, "the subscription must have executed its native query");

        subscription.Dispose();
        var notificationsAfterDisposal = Volatile.Read(ref notifications);
        fixture.Table.Change();
        await fixture.Connection.FlushQueries().WaitAsync(Timeout);

        fixture.Table.Executions.Should().Be(executions,
            "unsubscribing must remove the query from the native worker, not only disconnect its observer");
        Volatile.Read(ref notifications).Should().Be(notificationsAfterDisposal);
    }

    [Fact]
    public async Task DisposingASelectedQueryDoesNotWaitForItsNativeCallback()
    {
        await using var fixture = await QueryLifetimeTestHost.Create();
        var firstResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        using var subscription = fixture.Query.ObserveOwned(value => value).Subscribe(changes =>
        {
            Interlocked.Increment(ref notifications);
            if (changes.Any(change => change.Reason == ChangeReason.Add)) firstResult.TrySetResult();
        }, exception => firstResult.TrySetException(exception));

        await firstResult.Task.WaitAsync(Timeout);
        await fixture.Connection.FlushQueries().WaitAsync(Timeout);
        var gate = fixture.Table.BlockNextUpdate();
        Task? unsubscribe = null;
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);

            // The updater has already selected this query. Unsubscription must not
            // synchronously wait for it: an observer can unsubscribe within a pulse.
            unsubscribe = Task.Run(subscription.Dispose);
            await unsubscribe.WaitAsync(Timeout);
            gate.Release.Task.IsCompleted.Should().BeFalse();
            var notificationsAfterDisposal = Volatile.Read(ref notifications);

            gate.Release.TrySetResult();
            await fixture.Connection.FlushQueries().WaitAsync(Timeout);
            var executionsAfterSelectedQuery = fixture.Table.Executions;

            fixture.Table.Change();
            await fixture.Connection.FlushQueries().WaitAsync(Timeout);

            fixture.Table.Executions.Should().Be(executionsAfterSelectedQuery,
                "a query selected before disposal can finish, but future pulses must not execute it");
            Volatile.Read(ref notifications).Should().Be(notificationsAfterDisposal,
                "finishing a selected query must not notify the disposed subscription");
        }
        finally
        {
            gate.Release.TrySetResult();
            if (unsubscribe is not null) await unsubscribe.WaitAsync(Timeout);
        }
    }
}
