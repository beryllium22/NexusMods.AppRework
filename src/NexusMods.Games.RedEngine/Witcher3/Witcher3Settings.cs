using NexusMods.Sdk.Settings;

namespace NexusMods.Games.RedEngine.Witcher3;

public sealed class Witcher3Settings : ISettings
{
    public bool DoFullGameBackup { get; set; }

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
        => settingsBuilder.ConfigureProperty(
            x => x.DoFullGameBackup,
            new PropertyOptions<Witcher3Settings, bool>
            {
                Section = Sections.Experimental,
                DisplayName = "Full game backup: The Witcher 3",
                DescriptionFactory = _ => "Back up the base game Content and original DLC folders too. This greatly increases disk space usage. Change this before managing the game.",
            },
            new BooleanContainerOptions());
}
