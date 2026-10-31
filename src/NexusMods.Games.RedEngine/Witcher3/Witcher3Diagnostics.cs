using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Games.RedEngine.Witcher3;

public static partial class Witcher3Diagnostics
{
    public const string Source = "NexusMods.Games.RedEngine.Witcher3";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OverlappingScripts = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Overlapping Witcher scripts")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Different versions of '{ScriptPath}' are enabled in {ModFolders}")
        .WithDetails("""
The enabled mod folders **{ModFolders}** contain different versions of `{ScriptPath}`.

### How to resolve
Check the mod authors' compatibility instructions for your installed game version. Use a matching compatibility patch or a script merge when required, or disable one of the conflicting mods and apply the profile again.

### What the app checked
This checks loose `.ws` files that will be deployed after Apply, including pending installs and enabled states. It does not inspect compiled bundles, merge scripts, or validate an existing merge. A reviewed compatibility patch or merge may already resolve this overlap.

Classic, next-generation and later game releases can require different mod versions. A successful file installation does not verify script compatibility with the installed game version.
""")
        .WithMessageData(builder => builder
            .AddValue<string>("ScriptPath")
            .AddValue<string>("ModFolders"))
        .Finish();
}
