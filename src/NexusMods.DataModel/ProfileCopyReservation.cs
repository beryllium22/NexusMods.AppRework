using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.DataModel;

/// <summary>
/// Reserves permanent entity IDs before publishing a profile copy. SortOrder.ParentEntity
/// contains a packed scalar ID that MnemonicDB cannot remap as a temporary reference.
/// Reserved entities have no profile/item attributes and are invisible to their observers.
/// </summary>
public partial class ProfileCopyReservation : IModelDefinition
{
    public static readonly MarkerAttribute Reserved = new("NexusMods.DataModel.ProfileCopyReservation", nameof(Reserved));
}
