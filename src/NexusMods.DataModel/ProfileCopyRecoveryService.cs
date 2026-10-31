using Microsoft.Extensions.Hosting;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;

namespace NexusMods.DataModel;

/// <summary>Reclaims unpublished copy reservations after an interrupted app session.</summary>
internal sealed class ProfileCopyRecoveryService(IConnection connection) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var db = connection.Db;
        var reservations = ProfileCopyReservation.All(db).Select(row => row.Id).ToArray();
        if (reservations.Length == 0) return;

        var reservedAttribute = db.AttributeCache.GetAttributeId(ProfileCopyReservation.Reserved.Id);
        using var tx = connection.BeginTransaction();
        foreach (var id in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A published entity should never retain the marker. If an older or
            // interrupted migration did leave one, preserve its data conservatively.
            var hasPublishedData = false;
            foreach (var datom in db.Get(id))
                if (datom.A != reservedAttribute) hasPublishedData = true;
            if (hasPublishedData) tx.Retract(id, ProfileCopyReservation.Reserved, Null.Instance);
            else tx.Delete(id, recursive: false);
        }
        await tx.Commit();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
