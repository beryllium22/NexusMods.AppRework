using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;

namespace NexusMods.Abstractions.Games;

/// <summary>
///     Adds games related serialization services.
/// </summary>
public static class Services
{
    /// <summary>
    ///     Adds known Game entity related serialization services.
    /// </summary>
    public static IServiceCollection AddGames(this IServiceCollection services)
    {
        return services
            // Each game's lazy initializer retains its own manager and varieties. A shared
            // singleton lets opening another game replace existing subscriptions and orders.
            .AddTransient<SortOrderManager>()
            .AddSortOrderItemModel()
            .AddSortOrderQueriesSql()
            .AddLoadoutItemGroupPriorityModel();
    }
}
