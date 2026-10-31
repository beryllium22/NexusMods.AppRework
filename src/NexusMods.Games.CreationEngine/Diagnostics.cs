using JetBrains.Annotations;
using Mutagen.Bethesda.Plugins;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Abstractions.Diagnostics.Values;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Games.CreationEngine;

internal static partial class Diagnostics
{
    private const string Source = "NexusMods.Games.CreationEngine";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingMaster = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Missing Master")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{PluginName} requires {MissingMasterPluginName} which is not installed")
        .WithDetails("""
The mod **{PluginName}** requires **{MissingMasterPluginName}** to function, but **{MissingMasterPluginName}** is not provided by any mods.

This means the plugin can’t find one of its required “master files.” Masters contain important data that other mods depend on—without them, the plugin is left pointing to records that don’t exist.

Applying this profile is blocked until the required master is available. Install the missing master, or disable the plugin that requires it, before playing.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Mod")
            .AddValue<ModKey>("PluginName")
            .AddValue<ModKey>("MissingMasterPluginName")
        )
        .Finish();

    
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingMasterIsDisabled = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Disabled Master")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{PluginName} requires {MissingMasterPluginName} which is installed in {MissingMasterMod} but is disabled")
        .WithDetails("""
The mod **{PluginName}** requires **{MissingMasterPluginName}** to function, but it is not provided by any enabled mods. The 
mod **{MissingMasterMod}** provides this file, but it is disabled. Enable the mod to fix this issue.

This means the plugin can’t find one of its required “master files.” Masters contain important data that other mods depend on—without them, the plugin is left pointing to records that don’t exist.

Applying this profile is blocked until the required master is available. Enable the mod or plugin that supplies the master, or disable the plugin that requires it, before playing.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Mod")
            .AddValue<ModKey>("PluginName")
            .AddValue<ModKey>("MissingMasterPluginName")
            .AddDataReference<LoadoutItemGroupReference>("MissingMasterMod")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate PluginLoadOrderProblem = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 3))
        .WithTitle("Plugin load order needs attention")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{PluginName}: {Problem}")
        .WithDetails("""
{Problem}

The profile cannot be applied until this plugin problem is resolved. Check the mod's requirements and installation instructions, restore a valid copy of an unreadable plugin, or disable the affected mod.

Header checks cover required masters and the game's fixed master ordering. They do not replace a compatibility review or LOOT metadata.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutReference>("Loadout")
            .AddValue<string>("PluginName")
            .AddValue<string>("Problem"))
        .Finish();
}
