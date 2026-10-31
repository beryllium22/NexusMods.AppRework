using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using NexusMods.HyperDuck;

namespace NexusMods.Sdk.Reactive;

/// <summary>
/// Observable SQL queries whose subscriptions own their live-query registration.
/// </summary>
public static class QueryObservableExtensions
{
    /// <summary>
    /// Observes a query and removes it from the polling worker when unsubscribed.
    /// </summary>
    public static IObservable<IChangeSet<T, TKey>> ObserveOwned<T, TKey>(
        this Query<T> query, Func<T, TKey> keySelector)
        where T : notnull
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(keySelector);

        return Observable.Create<IChangeSet<T, TKey>>(observer =>
        {
            var cache = new SourceCache<T, TKey>(keySelector);
            IDisposable? subscription = null;
            IDisposable? liveQuery = null;
            try
            {
                subscription = cache.Connect().Subscribe(observer);
                // HyperDuck 0.28.2 Query.Observe never disposes its live registration.
                // ObserveInto exposes that registration so its lifetime can follow ours.
                liveQuery = query.ObserveInto(cache);
                return Disposable.Create(Dispose);
            }
            catch
            {
                Dispose();
                throw;
            }

            void Dispose()
            {
                liveQuery?.Dispose();
                subscription?.Dispose();
                // A pulse already executing can finish into this cache. DynamicData's
                // SourceCache.Edit is synchronized; Dispose completes its observable
                // without destroying its storage. Do not wait for the polling worker
                // here: observers are allowed to unsubscribe inside its callback.
                cache.Dispose();
            }
        });
    }
}
