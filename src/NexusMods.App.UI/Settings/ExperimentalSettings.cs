using System.Text.Json.Serialization;
using JetBrains.Annotations;
using NexusMods.Sdk.Settings;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.App.UI.Settings;

/// <summary>
/// Settings that give access to experimental features in the UI.
/// </summary>
public record ExperimentalSettings : ISettings
{
    /// <summary>
    /// Enables games that are not enabled by default.
    /// </summary>
    public bool EnableAllGames { get; [UsedImplicitly] set; } = ApplicationConstants.IsDebug;

    // TODO: remove for GA
    public bool EnableCollectionSharing { get; [UsedImplicitly] set; }

    [JsonIgnore]
    public readonly GameId[] SupportedGames =
    [
        GameId.From("StardewValley"),
        GameId.From("RedEngine.Cyberpunk2077"),
    ];

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.EnableAllGames,
                new PropertyOptions<ExperimentalSettings, bool>
                {
                    Section = Sections.Experimental,
                    DisplayName = "Show experimental games",
                    DescriptionFactory = _ => "Shows game integrations that are still in development. Mod and collection compatibility may be incomplete. My Games updates immediately.",
                    RequiresRestart = false,
                },
                new BooleanContainerOptions()
            )
            .ConfigureProperty(
                x => x.EnableCollectionSharing,
                new PropertyOptions<ExperimentalSettings, bool>
                {
                    Section = Sections.Experimental,
                    DisplayName = "Enable sharing collections",
                    DescriptionFactory = _ => "Allows uploading of collections",
                },
                new BooleanContainerOptions()
            );
    }
}
