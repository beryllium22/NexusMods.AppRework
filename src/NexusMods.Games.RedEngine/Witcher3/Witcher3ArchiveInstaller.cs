using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.RedEngine.Witcher3;

/// <summary>
/// Installs complete, unambiguous Mods and custom DLC packages. Unsupported packages
/// are left intact for the advanced installer, including menu patches and optional variants.
/// </summary>
public sealed class Witcher3ArchiveInstaller(IServiceProvider provider)
    : ALibraryArchiveInstaller(provider, provider.GetRequiredService<ILogger<Witcher3ArchiveInstaller>>())
{
    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction tx,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Read entries directly: a case-insensitive tree can hide duplicate source paths.
        var entries = libraryArchive.Children.ToArray();
        if (!TryCreatePlan(entries.Select(entry => entry.Path.ToString()).ToArray(), out var plan, out var reason, cancellationToken))
            return ValueTask.FromResult<InstallerResult>(new NotSupported(reason));

        // Validate the entire package before adding any files to the transaction.
        foreach (var mapping in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = entries[mapping.Index].AsLibraryFile();
            _ = new LoadoutFile.New(tx, out var id)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = (loadout.Id, LocationId.Game, mapping.TargetPath),
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        Name = mapping.TargetPath.Name,
                        LoadoutId = loadout.Id,
                        ParentId = loadoutGroup.Id,
                    },
                },
                Hash = source.Hash,
                Size = source.Size,
            };
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }

    internal readonly record struct FileMapping(int Index, RelativePath TargetPath);
    private readonly record struct ModRoot(string Source, string Target);

    internal static bool TryCreatePlan(
        IReadOnlyList<string> paths,
        out FileMapping[] plan,
        out string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        plan = [];
        reason = "The archive has no supported Witcher 3 Mods or custom DLC content folders.";
        var parts = new string[paths.Count][];
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parts[index] = paths[index].Replace('\\', '/').Split('/');
            if (parts[index].Any(segment => !IsSafeWindowsSegment(segment)))
            {
                reason = $"The archive contains an unsafe or invalid Windows path: {paths[index]}";
                return false;
            }
        }

        // Accept a single common wrapper, never choose one variant from several folders.
        var offset = parts.Any(segments => TryFindRoot(segments, 0, out _)) ? 0 : 1;
        var candidates = parts.Where(segments => TryFindRoot(segments, offset, out _)).ToArray();
        if (candidates.Length == 0) return false;

        var wrapper = offset == 0 ? string.Empty : candidates[0][0];
        if (offset == 1 && (IsGameRoot(wrapper) || candidates.Any(segments => !segments[0].Equals(wrapper, StringComparison.OrdinalIgnoreCase))))
        {
            reason = "The archive contains multiple installation variants or an unsupported game folder. Select the intended files in the advanced installer.";
            return false;
        }

        var roots = new Dictionary<string, ModRoot>(StringComparer.OrdinalIgnoreCase);
        var destinationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segments in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryFindRoot(segments, offset, out var root);
            var destinationParts = root.Target.Split('/');
            if (destinationParts[0] == "DLC" && Witcher3Synchronizer.IsOriginalDlc(destinationParts[1]))
            {
                reason = "The package targets an original game DLC folder. Original DLC replacements require review in the advanced installer.";
                return false;
            }

            if (destinationRoots.TryGetValue(root.Target, out var source) && !source.Equals(root.Source, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Different archive folders target the same mod. Select one installation layout in the advanced installer.";
                return false;
            }

            roots.TryAdd(root.Source, root);
            destinationRoots[root.Target] = root.Source;
        }

        var mappings = new List<FileMapping>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < parts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = parts[index];
            var inWrapper = offset == 0 || segments[0].Equals(wrapper, StringComparison.OrdinalIgnoreCase);
            var relative = inWrapper ? string.Join('/', segments.Skip(offset)) : string.Empty;
            var root = roots.Values.FirstOrDefault(candidate => relative.StartsWith(candidate.Source + '/', StringComparison.OrdinalIgnoreCase));
            if (root.Source is null)
            {
                if (IsDocumentation(segments[^1])) continue;

                reason = $"The package contains files outside supported mod folders ({paths[index]}). Review the complete package in the advanced installer.";
                return false;
            }

            var target = root.Target + relative[root.Source.Length..];
            if (!targets.Add(target))
            {
                reason = $"Multiple archive files target the same Windows path: {target}";
                return false;
            }

            mappings.Add(new FileMapping(index, target));
        }

        // A file cannot also be a parent directory of another file on Windows.
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var separator = target.IndexOf('/'); separator >= 0; separator = target.IndexOf('/', separator + 1))
            {
                if (!targets.Contains(target[..separator])) continue;
                reason = $"The archive contains a file/directory collision: {target[..separator]}";
                return false;
            }
        }

        if (mappings.Count == 0) return false;
        plan = mappings.ToArray();
        reason = string.Empty;
        return true;
    }

    private static bool TryFindRoot(string[] parts, int offset, out ModRoot root)
    {
        root = default;
        if (parts.Length <= offset + 2) return false;

        var first = parts[offset];
        if (first.Equals("Mods", StringComparison.OrdinalIgnoreCase) || first.Equals("DLC", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length <= offset + 3 || !parts[offset + 2].Equals("content", StringComparison.OrdinalIgnoreCase)) return false;
            var isMods = first.Equals("Mods", StringComparison.OrdinalIgnoreCase);
            if (isMods && !parts[offset + 1].StartsWith("mod", StringComparison.OrdinalIgnoreCase)) return false;
            root = new ModRoot($"{first}/{parts[offset + 1]}", $"{(isMods ? "Mods" : "DLC")}/{parts[offset + 1]}");
            return true;
        }

        if (!parts[offset + 1].Equals("content", StringComparison.OrdinalIgnoreCase)) return false;
        if (first.StartsWith("mod", StringComparison.OrdinalIgnoreCase))
            root = new ModRoot(first, $"Mods/{first}");
        else if (first.StartsWith("dlc", StringComparison.OrdinalIgnoreCase))
            root = new ModRoot(first, $"DLC/{first}");
        else
            return false;

        return true;
    }

    private static bool IsGameRoot(string segment)
        => segment.Equals("Mods", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("DLC", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("content", StringComparison.OrdinalIgnoreCase);

    private static bool IsDocumentation(string name)
    {
        if (name.EndsWith(".part.txt", StringComparison.OrdinalIgnoreCase)) return false;
        var extension = Path.GetExtension(name);
        if (!new[] { "", ".txt", ".md", ".pdf", ".rtf", ".rst" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Equals("readme", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("license", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("changelog", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeWindowsSegment(string segment)
    {
        if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')) return false;
        if (segment.Any(character => character < 32 || "<>:\"|?*".Contains(character))) return false;

        var name = segment.Split('.')[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return false;

        return name.Length != 4
            || (!name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            || !"123456789¹²³".Contains(name[3]);
    }
}
