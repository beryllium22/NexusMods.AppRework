using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;

namespace NexusMods.App.UI.Pages.MyLoadouts;

public static class ProfileDialogs
{
    public static IDialog Create(string gameName) => NameDialog(
        "Create a profile", $"Start a new mod setup for {gameName}.", "Create profile", string.Empty);

    public static IDialog Rename(string name) => NameDialog(
        "Rename profile", "Choose a name that makes this mod setup easy to recognize.", "Rename", name);

    public static IDialog Duplicate(string name) => NameDialog(
        "Duplicate profile",
        "Copy installed mods, collections, conflict priorities and load orders. Your library downloads are shared.",
        "Duplicate", name[..Math.Min(name.Length, LoadoutName.MaximumLength - 5)] + " copy");

    private static IDialog NameDialog(string title, string description, string action, string name)
        => DialogFactory.CreateStandardDialog(title, new StandardDialogParameters
        {
            Text = description,
            InputLabel = "Profile name",
            InputText = name,
            InputWatermark = "e.g. Survival playthrough",
            InputValidation = LoadoutName.GetValidationError,
            BottomText = "Profiles keep separate mod setups. Game saves can still be shared between profiles.",
        }, [DialogStandardButtons.Cancel, new DialogButtonDefinition(action, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary)]);

    public static IDialog Delete(string name) => DialogFactory.CreateStandardDialog(
        "Delete profile?", new StandardDialogParameters
        {
            Text = $"Remove \"{name}\"? Other profiles keep their mods, and downloaded files remain in your library.",
        }, [DialogStandardButtons.Cancel, new DialogButtonDefinition("Delete profile", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive)]);
}
