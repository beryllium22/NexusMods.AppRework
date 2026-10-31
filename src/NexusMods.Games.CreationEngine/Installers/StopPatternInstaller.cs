using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Installers;

/// <summary>
/// Installer class for Creation Engine games (Skyrim, Fallout, etc.) that handles mod installation based on file/folder patterns.
/// 
/// This installer analyzes mod archives and installs files to appropriate locations based on configured patterns:
/// - Most mod files are installed to the game's Data folder
/// - Engine files (like script extenders) are installed to the game's root folder
/// - Handles common mod packaging variations like nested Data folders or game folder aliases
/// 
/// Key features:
/// - Detects and properly handles various mod archive structures
/// - Supports game-specific file extensions (.esp, .esm, etc.)
/// - Handles script extender and engine file installations
/// - Configurable patterns for determining file destinations
/// - Supports multiple game folder name aliases
/// - Handles common mod folder structures (meshes, textures, etc.)
/// 
/// Usage:
/// 1. Configure the installer with game-specific settings (file patterns, folders, etc.)
/// 2. Call Build() to optimize the installer
/// 3. The installer will automatically handle mod installations based on the configured patterns
/// </summary>
public class StopPatternInstaller(IServiceProvider serviceProvider) : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<StopPatternInstaller>>())
{
#region Customizable Properties

    /// <summary>
    /// Unique identifier for the game this installer handles.
    /// Required property that must be set when configuring the installer.
    /// </summary>
    public required GameId GameId { get; init; }
    
    /// <summary>
    /// Defines where mod data files should be installed, typically in the game's "Data" folder.
    /// Defaults to {Game}/Data directory. Used as the base installation path for most mod files.
    /// </summary>
    public GamePath DataPath { get; init; } = new(LocationId.Game, "Data");
    
    /// <summary>
    /// Defines the root game folder path where engine-related files (like script extenders) should be installed.
    /// Defaults to the base game folder. Used for files that need to be installed alongside the game executable.
    /// </summary>
    public GamePath EnginePath { get; init; } = new(LocationId.Game, RelativePath.Empty);

    /// <summary>
    /// The name of the data folder used for plugin/mod files, defaults to "Data".
    /// Used to detect and handle mods that may have nested "Data/Data" folder structures.
    /// </summary>
    public string DataFolderName { get; init; } = "Data";
    
    /// <summary>
    /// Array of common folder names used for the game installation.
    /// Required property listing known alternative names for the game folder (e.g. ["Skyrim Special Edition", "SkyrimSE"]).
    /// Used to properly handle mods packaged with different game folder names.
    /// </summary>
    public required string[] GameAliases { get; init; }
    
    /// <summary>
    /// Array of folder names that should be installed directly in the game's data folder.
    /// Required property listing directories like "meshes", "textures", etc.
    /// When these folders are found at the root of a mod, their contents are installed to Data/.
    /// </summary>
    public required string[] TopLevelDirs { get; init; }
    
    /// <summary>
    /// Array of regex patterns identifying folders that should be installed to the data directory.
    /// Required property used to match folder names that should trigger installation to the Data folder.
    /// Supplements TopLevelDirs with more complex pattern matching.
    /// </summary>
    public required string[] StopPatterns { get; init; }
    
    /// <summary>
    /// Array of regex patterns identifying files that should be installed to the game's root folder.
    /// Required property used to match engine files like script extenders.
    /// Files matching these patterns are installed to the base game folder instead of the Data folder.
    /// </summary>
    public required string[] EngineFiles { get; init; }
    
    /// <summary>
    /// Array of file extensions that indicate plugin files.
    /// Defaults to [.esm, .esp, .esl] for Creation Engine games.
    /// Used to identify and properly handle plugin files during installation.
    /// </summary>
    public Extension[] PluginLikeExtensions { get; init; } = [KnownCEExtensions.ESM, KnownCEExtensions.ESP, KnownCEExtensions.ESL];
    
    /// <summary>
    /// Array of file extensions that indicate archive files.
    /// Defaults to [.bsa, .ba2] for Creation Engine games.
    /// Used to identify and properly handle archive files during installation.
    /// </summary>
    public Extension[] ArchiveLikeExtensions { get; init; } = [KnownCEExtensions.BSA, KnownCEExtensions.BA2];

#endregion

    private bool _isBuilt;
    private RelativePath _dataPrefix;
    private RelativePath[] _gameFolders = [];
    private RelativePath[] _topLevelPaths = [];
    private Regex _stopPatterns = null!;
    private Regex _engineFiles = null!;

    /// <summary>
    /// Compiles the game-specific archive layout rules.
    /// </summary>
    public StopPatternInstaller Build()
    {
        _dataPrefix = RelativePath.FromUnsanitizedInput(DataFolderName);
        _gameFolders = GameAliases.Select(folder => RelativePath.FromUnsanitizedInput(folder)).ToArray();
        _topLevelPaths = TopLevelDirs.Select(folder => RelativePath.FromUnsanitizedInput(folder)).ToArray();
        _engineFiles = CombineRegexes(EngineFiles.Select(pattern => $"^(?:{pattern})$").ToArray());

        var patterns = PluginLikeExtensions.Concat(ArchiveLikeExtensions)
            .Select(extension => $"{Regex.Escape(extension.ToString())}$")
            .Concat(TopLevelDirs.Select(directory => $"(^|/){Regex.Escape(directory)}(/|$)"))
            .Concat(StopPatterns)
            .ToArray();
        _stopPatterns = CombineRegexes(patterns);
        _isBuilt = true;
        return this;
    }

    private static Regex CombineRegexes(string[] patterns)
    {
        var combined = patterns.Length == 0 ? "(?!)" : string.Join("|", patterns.Select(pattern => $"(?:{pattern})"));
        return new Regex(combined, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        if (!_isBuilt) throw new InvalidOperationException("The installer has not been built yet.");
        cancellationToken.ThrowIfCancellationRequested();
        var files = libraryArchive.Children.ToArray();
        if (files.Length == 0)
            return ValueTask.FromResult(FailWithReason("The provided library archive has no files"));

        if (!TryComputeEffectiveRootPrefix(files, out var root))
            return ValueTask.FromResult(FailWithReason("The archive has no unambiguous Creation Engine mod folder. Select its files with a manual installer."));

        // Resolve all destinations before adding anything to the installation transaction.
        // Data/Foo.esp and Foo.esp, including case variants, must not silently overwrite one another.
        var destinations = new Dictionary<GamePath, LibraryArchiveFileEntry.ReadOnly>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.Path.InFolder(root)) continue;
            var relative = StripLeadingData(file.Path.RelativeTo(root));
            var targetBase = relative.Parent == RelativePath.Empty && _engineFiles.IsMatch(relative.ToString()) ? EnginePath : DataPath;
            var target = new GamePath(targetBase.LocationId, targetBase.Path / relative);
            if (!destinations.TryAdd(target, file))
                return ValueTask.FromResult(FailWithReason($"More than one archive file maps to {target}. Select the intended variant with a manual installer."));
        }

        if (destinations.Count == 0)
            return ValueTask.FromResult(FailWithReason("The selected mod folder has no installable files"));

        foreach (var (target, file) in destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraryFile = file.AsLibraryFile();
            _ = new LoadoutFile.New(transaction, out var id)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
                {
                    TargetPath = (loadout.Id, target.LocationId, target.Path),
                    LoadoutItem = new LoadoutItem.New(transaction, id)
                    {
                        Name = file.Path.FileName,
                        LoadoutId = loadout.Id,
                        ParentId = loadoutGroup.Id,
                    },
                },
                Hash = libraryFile.Hash,
                Size = libraryFile.Size,
            };
        }
        return ValueTask.FromResult<InstallerResult>(new Success());
    }

    private bool TryComputeEffectiveRootPrefix(LibraryArchiveFileEntry.ReadOnly[] files, out RelativePath root)
    {
        root = RelativePath.Empty;
        while (true)
        {
            var prefix = root;
            var relative = files.Where(file => file.Path.InFolder(prefix)).Select(file => file.Path.RelativeTo(prefix)).ToArray();
            if (relative.Any(HasRootContent)) return true;

            // Repeated wrappers are common. Descend only when one folder contains mod content;
            // archives with multiple optional variants require an explicit choice.
            var candidates = relative.Where(path => path.Parent != RelativePath.Empty)
                .Select(path => path.TopParent).Distinct()
                .Where(folder => relative.Where(path => path.InFolder(folder))
                    .Any(path => HasRecognizableContent(path.RelativeTo(folder))))
                .ToArray();
            if (candidates.Length != 1) return false;
            root /= candidates[0];
        }
    }

    private bool HasRootContent(RelativePath path)
    {
        if (path.Parent == RelativePath.Empty)
            return PluginLikeExtensions.Contains(path.Extension) || ArchiveLikeExtensions.Contains(path.Extension) || _engineFiles.IsMatch(path.ToString());
        var top = path.TopParent;
        return top == _dataPrefix || _topLevelPaths.Contains(top) || _stopPatterns.IsMatch(top + "/");
    }

    private bool HasRecognizableContent(RelativePath path)
    {
        return _stopPatterns.IsMatch(path.ToString()) || _engineFiles.IsMatch(path.FileName.ToString()) ||
               path.ToString().Split('/').SkipLast(1).Any(folder =>
                   string.Equals(folder, DataFolderName, StringComparison.OrdinalIgnoreCase) ||
                   _gameFolders.Contains(RelativePath.FromUnsanitizedInput(folder)));
    }

    private RelativePath StripLeadingData(RelativePath path)
    {
        while (path.InFolder(_dataPrefix))
            path = path.RelativeTo(_dataPrefix);
        return path;
    }
}
