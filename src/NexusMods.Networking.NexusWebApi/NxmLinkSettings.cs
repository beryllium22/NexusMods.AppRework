using NexusMods.Sdk.Settings;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Controls whether the app registers itself as the default handler for Nexus Mods links.
/// </summary>
public record NxmLinkSettings : ISettings
{
    public bool RegisterAsDefaultHandler { get; set; }

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                settings => settings.RegisterAsDefaultHandler,
                new PropertyOptions<NxmLinkSettings, bool>
                {
                    Section = Sections.General,
                    DisplayName = "Open Nexus Mods links in this app",
                    DescriptionFactory = _ => "Use this app for Nexus Mods downloads and browser sign-in links. " +
                        "Enabling this replaces your current nxm link handler, such as Vortex or Mod Organizer 2, when you save these settings. " +
                        "Turning it off stops automatic registration; to switch back, choose your preferred mod manager as the link handler from that app.",
                },
                new BooleanContainerOptions()
            );
    }
}
