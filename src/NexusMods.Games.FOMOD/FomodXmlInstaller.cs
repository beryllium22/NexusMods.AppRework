using System.Collections.Frozen;
using System.Diagnostics;
using System.Xml.Linq;
using FomodInstaller.Interface;
using FomodInstaller.Scripting;
using FomodInstaller.Scripting.XmlScript;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FOMOD.CoreDelegates;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using IFileSystem = NexusMods.Paths.IFileSystem;
using FomodMod = FomodInstaller.Interface.Mod;

namespace NexusMods.Games.FOMOD;

public class FomodXmlInstaller : ALibraryArchiveInstaller
{
    private readonly XmlScriptType _scriptType = new();
    private readonly ILogger<FomodXmlInstaller> _logger;
    private readonly GamePath _fomodInstallationPath;
    private readonly IFileSystem _fileSystem;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IFileStore _fileStore;

    /// <summary>
    /// Creates a new instance of <see cref="FomodXmlInstaller"/> given the provided <paramref name="provider"/> and <paramref name="fomodInstallationPath"/>.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="fomodInstallationPath"></param>
    /// <returns></returns>
    public static FomodXmlInstaller Create(IServiceProvider provider, GamePath fomodInstallationPath)
    {
        return new FomodXmlInstaller(provider, provider.GetRequiredService<ILogger<FomodXmlInstaller>>(), fomodInstallationPath);
    }

    private FomodXmlInstaller(
        IServiceProvider serviceProvider,
        ILogger<FomodXmlInstaller> logger,
        GamePath fomodInstallationPath) : base(serviceProvider, logger)
    {
        _logger = logger;
        _fomodInstallationPath = fomodInstallationPath;

        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        _fileStore = serviceProvider.GetRequiredService<IFileStore>();
    }

    public override async ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(libraryArchive, loadoutGroup, transaction, loadout, null, cancellationToken);
    }

    public async ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        FomodOption[]? options,
        CancellationToken cancellationToken,
        LoadoutItemGroupId? installingCollection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!libraryArchive.Children.TryGetFirst(x => x.Path.EndsWith(FomodConstants.XmlConfigRelativePath), out var xmlFile))
            return new NotSupported(Reason: "Found no FOMOD data in the archive");

        // `foo/bar/baz/fomod/ModuleConfig.xml` -> `foo/bar/baz`
        // `fomod/ModuleConfig.xml` -> empty string
        var fomodPathPrefix = xmlFile.Path.Parent.Parent;
        var pathPrefixDropCount = fomodPathPrefix.Length == 0 ? 0 : fomodPathPrefix.Depth + 1;

        var fomodArchiveFiles = libraryArchive.Children
            .Where(x => x.Path.InFolder(fomodPathPrefix))
            .Select(x => new KeyValuePair<RelativePath, LibraryArchiveFileEntry.ReadOnly>(x.Path.DropFirst(pathPrefixDropCount), x))
            .DistinctBy(kv => kv.Key)
            .ToFrozenDictionary(kv => kv.Key, kv => kv.Value);

        var mod = new FomodMod(
            listModFiles: fomodArchiveFiles.Keys.Select(static x => x.ToString()).ToList(),
            stopPatterns: FomodConstants.StopPattern,
            installScriptPath: xmlFile.Path.DropFirst(pathPrefixDropCount), 
            tempFolderPath: string.Empty,
            scriptType: _scriptType
        );

        // NOTE(erri120): We're loading the script manually, otherwise the FOMOD library will load the script from the file system
        await mod.InitializeWithoutLoadingScript();

        // Games reuse their installer instances. Delegates, presets, dialog callbacks and
        // dependency snapshots must belong to this execution, including parallel installs.
        using var delegateScope = ServiceProvider.CreateScope();
        var coreDelegates = delegateScope.ServiceProvider.GetRequiredService<ICoreDelegates>();
        var installerDelegates = coreDelegates as InstallerDelegates;
        FomodDependencySnapshot? dependencies = null;
        if (installerDelegates is not null)
        {
            dependencies = delegateScope.ServiceProvider.GetRequiredService<IFomodDependencyResolver>().Capture(loadout, installingCollection);
            installerDelegates.Initialize(FomodDependencyState.Create(dependencies, _fomodInstallationPath), options, cancellationToken);
            installerDelegates.UiDelegates.CurrentFomodArchiveFiles = fomodArchiveFiles;
        }

        var rawScript = await LoadScript(xmlFile.AsLibraryFile().Hash, cancellationToken);

        var executor = _scriptType.CreateExecutor(mod, coreDelegates);
        var installScript = _scriptType.LoadScript(rawScript, true);
        FixScript(installScript, fomodArchiveFiles, _logger);
        if (dependencies is { GameVersion: null } && XDocument.Parse(rawScript).Descendants()
            .Any(element => element.Name.LocalName is "gameDependency" or "falloutDependency"))
            throw new NotSupportedException("This FOMOD checks the game version, but the primary executable's version could not be read. Verify the game installation and try again.");

        IList<Instruction> instructions;
        try
        {
            instructions = await executor.Execute(installScript, "", null);
        }
        catch
        {
            installerDelegates?.UiDelegates.ThrowIfFailed();
            throw;
        }
        finally
        {
            installerDelegates?.UiDelegates.EndDialog();
        }

        installerDelegates?.UiDelegates.ThrowIfFailed();
        cancellationToken.ThrowIfCancellationRequested();

        var errors = instructions.Where(instruction => instruction.type == "error").ToArray();
        if (errors.Length != 0) throw new InvalidDataException(string.Join("; ", errors.Select(err => err.source)));

        foreach (var warning in instructions.Where(instruction => instruction.type == "unsupported"))
            _logger.LogWarning("Installer uses unsupported function: {}", warning.source);

        InstructionsToLoadoutItems(transaction, loadout, loadoutGroup,instructions, fomodArchiveFiles, _fomodInstallationPath);
        return new Success();
    }

    private async ValueTask<string> LoadScript(Hash hash, CancellationToken cancellationToken = default)
    {
        await using var stream = await _fileStore.GetFileStream(hash, cancellationToken);
        using var streamReader = new StreamReader(stream);
        return await streamReader.ReadToEndAsync(cancellationToken);
    }

    private static void FixScript(
        IScript script,
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        ILogger? logger = null)
    {
        Debug.Assert(script is XmlScript);
        if (script is not XmlScript xmlScript) return;

        FixPaths(fomodArchiveFiles, xmlScript.RequiredInstallFiles);
        FixPaths(fomodArchiveFiles, xmlScript.ConditionallyInstalledFileSets.SelectMany(x => x.Files));

        foreach (var option in xmlScript.InstallSteps.SelectMany(x => x.OptionGroups).SelectMany(x => x.Options))
        {
            option.ImagePath = FixPath(option.ImagePath, fomodArchiveFiles, logger: logger);
            FixPaths(fomodArchiveFiles, option.Files, logger: logger);
        }
    }

    private static void FixPaths(
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        IEnumerable<InstallableFile> installableFiles,
        ILogger? logger = null)
    {
        foreach (var installableFile in installableFiles)
        {
            ValidateRelativePath(RemoveRoot(installableFile.Source).ToString());
            ValidateRelativePath(RemoveRoot(installableFile.Destination).ToString());
            installableFile.Source = FixPath(installableFile.Source, fomodArchiveFiles, isDirectory: installableFile.IsFolder, logger: logger);
            installableFile.Destination = RelativePath.FromUnsanitizedInput(RemoveRoot(installableFile.Destination));
        }
    }

    private static void ValidateRelativePath(string input)
    {
        var normalized = input.Replace('\\', '/');
        if (normalized.Contains(':') || normalized.Split('/').Any(segment => segment == ".."))
            throw new InvalidDataException($"The FOMOD installer references a path outside its installation directory: '{input}'.");
    }

    private static readonly char[] TrimChars = ['\\', '/'];
    internal static ReadOnlySpan<char> RemoveRoot(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> span = input;
        return span.TrimStart(TrimChars);
    }

    internal static string FixPath(
        string? input,
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        bool isDirectory = false,
        ILogger? logger = null)
    {
        var span = RemoveRoot(input);
        if (span.IsEmpty) return string.Empty;

        var path = RelativePath.FromUnsanitizedInput(span);
        if (isDirectory || fomodArchiveFiles.ContainsKey(path)) return path.ToString();

        logger?.LogWarning("Didn't find matching archive file for referenced file in FOMOD `{OldPath}` -> `{NewPath}`", input, path);
        return input ?? string.Empty;
    }

    private void InstructionsToLoadoutItems(
        ITransaction transaction,
        LoadoutId loadoutId,
        LoadoutItemGroup.New loadoutGroup,
        IList<Instruction> instructions,
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        GamePath gamePath)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.type == "copy")
            {
                ConvertInstructionCopy(transaction, instruction, loadoutGroup, loadoutId, fomodArchiveFiles, gamePath);
            }
            // TODO: "mkdir" - not sure if we need/want this
            // TODO: "enableallplugins"
            // "iniedit" - only supported by c# script and modscript installers atm
            // "generatefile" - only supported by c# script installers
            // "enableplugin" - supported in the fomod-installer module but doesn't seem to be emitted anywhere
            else
            {
                _logger.LogWarning("Unknown FOMOD instruction type: {Type}", instruction.type);
            }
        }
    }

    private void ConvertInstructionCopy(
        ITransaction transaction,
        Instruction instruction,
        LoadoutItemGroup.New loadoutGroup,
        LoadoutId loadoutId,
        FrozenDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly> fomodArchiveFiles,
        GamePath gamePath)
    {
        var src = RelativePath.FromUnsanitizedInput(instruction.source);
        var dest = RelativePath.FromUnsanitizedInput(instruction.destination);

        if (!fomodArchiveFiles.TryGetValue(src, out var libraryArchiveFile))
        {
            throw new InvalidDataException($"The FOMOD installer references missing archive file '{src}'.");
        }

        _ = new LoadoutFile.New(transaction, out var id)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
            {
                LoadoutItem = new LoadoutItem.New(transaction, id)
                {
                    LoadoutId = loadoutId,
                    Name = libraryArchiveFile.Path.FileName,
                    ParentId = loadoutGroup,
                },
                TargetPath = new GamePath(gamePath.LocationId, gamePath.Path.Join(dest)).ToGamePathParentTuple(loadoutId),
            },
            Hash = libraryArchiveFile.AsLibraryFile().Hash,
            Size = libraryArchiveFile.AsLibraryFile().Size,
        };
    }
}
