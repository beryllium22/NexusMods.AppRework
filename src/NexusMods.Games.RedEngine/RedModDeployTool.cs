using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CliWrap;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Games.RedEngine.Cyberpunk2077.Extensions;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using R3;
using static NexusMods.Games.RedEngine.Constants;

namespace NexusMods.Games.RedEngine;

public class RedModDeployTool : ITool
{
    private readonly GameToolRunner _toolRunner;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly ILogger _logger;
    private readonly RedModSortOrderVariety _redModSortOrderVariety;
    private readonly IOSInformation _os;

    public RedModDeployTool(GameToolRunner toolRunner, TemporaryFileManager temporaryFileManager, ILogger<RedModDeployTool> logger, RedModSortOrderVariety sortOrderVariety, IOSInformation os)
    {
        _logger = logger;
        _toolRunner = toolRunner;
        _temporaryFileManager = temporaryFileManager;
        _redModSortOrderVariety = sortOrderVariety;
        _os = os;
    }
    
    public IEnumerable<GameId> GameIds => [Cyberpunk2077.Cyberpunk2077Game.GameId];

    public async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exe = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModPath);
        if (!exe.FileExists)
            throw new FileNotFoundException("REDmod deployment requires the REDmod DLC. Install it, then Apply the loadout again.", exe.ToString());
        var deployFolder = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModDeployFolder);

        await using var loadorderFile = _temporaryFileManager.CreateFile();
        await WriteLoadOrderFile(loadorderFile.Path, loadout, cancellationToken);
        
        // RedMod deploys to this folder and freaks out if it doesn't exist, but our synchronizer will
        // delete it if it's empty, so we need to recreate it here.
        if (!deployFolder.DirectoryExists())
            deployFolder.CreateDirectory();

        // Note (halgari): When we change the redmod order, we need to force redmod to re-run. This is due to a bug in redmod
        // not recognizing order as a change in the mod configuration. If that bug ever gets fixed (does an order check) then  
        // we can resolve this by removing the -force flag.
        if (_os.IsWindows)
        {
            var command = Cli.Wrap(exe.ToString())
                .WithArguments(["deploy", "-force", "-modlist=" + loadorderFile.Path], true)
                .WithWorkingDirectory(exe.Parent.ToString());
            await ExecuteDeployment(loadout, command, cancellationToken);
        }
        else
        {
            if (loadout.Installation.Store == GameStore.Steam)
            {
                var linuxCompatibilityDataProvider = loadout.InstallationInstance.LocatorResult.LinuxCompatabilityDataProvider;
                if (linuxCompatibilityDataProvider is null)
                    throw new InvalidOperationException("REDmod deployment requires the game's Steam compatibility prefix. Start the game through Steam once, then Apply again.");

                var wineDirectory = linuxCompatibilityDataProvider.WinePrefixDirectoryPath;
                var cDriveDirectory = wineDirectory.Combine("drive_c");

                if (!cDriveDirectory.DirectoryExists()) cDriveDirectory.CreateDirectory();
                var tmpFile = cDriveDirectory.Combine("modlist.txt");
                await using (var input = loadorderFile.Path.Read())
                await using (var output = tmpFile.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    await input.CopyToAsync(output, cancellationToken: cancellationToken);
                }

                await using var batchPath = await ExtractTemporaryDeployScript();
                var command = Cli.Wrap(batchPath.ToString()).WithArguments(["deploy", "-force", "-modlist=C:\\modlist.txt"], escape: true);
                await ExecuteDeployment(loadout, command, cancellationToken);
            }            
            else
            {
                throw new NotSupportedException("REDmod deployment is currently supported on Windows and Steam on Linux.");
            }
        }
    }

    private async Task ExecuteDeployment(Loadout.ReadOnly loadout, Command command, CancellationToken cancellationToken)
    {
        var result = await _toolRunner.ExecuteAsync(loadout, command.WithValidation(CommandResultValidation.None), true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
            throw new InvalidOperationException($"REDmod deployment failed with exit code {result.ExitCode}. Review the REDmod process logs, correct the reported problem, then Apply again.");
    }

    internal IReadOnlyList<string> GetModOrder(Loadout.ReadOnly loadout)
    {
        var enabled = RedModExtensions.RetrieveEnabledRedModFolders(loadout).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = _redModSortOrderVariety.GetRedModOrder(loadout, Optional.None<CollectionGroupId>(), loadout.Db)
            .Where(enabled.Contains).ToList();
        // Include an ingested manifest even if a sort-order observer has not processed it yet.
        var included = ordered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(enabled.Where(folder => !included.Contains(folder)).Order(StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    internal string GetInputFingerprint(Loadout.ReadOnly loadout)
    {
        var order = GetModOrder(loadout);
        if (order.Count == 0) return string.Empty;
        var files = GetInputFiles(loadout, order)
            .Select(file => new { Path = file.Path.ToString(), Hash = file.Hash.ToString(), Size = file.Size.Value })
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Order = order, Files = files })));
    }

    internal bool InputFilesMatchDisk(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> diskState)
    {
        // Include inactive folders too: disabling the final REDmod during copying must not
        // turn this check into an empty comparison while its old files remain on disk.
        var expected = GetFilesUnderMods(loadout).OrderBy(file => file.Path);
        var actual = diskState
            .Where(file => file.Path.LocationId == LocationId.Game && file.Path.Path.InFolder(RedModInstallFolder.Path))
            .Select(file => (Path: file.Path.Path, file.Part.Hash, file.Part.Size))
            .OrderBy(file => file.Path);
        return expected.SequenceEqual(actual);
    }

    private static RelativePath[] GetInputFolders(IReadOnlyList<string> order)
        => order.Select(folder => RedModInstallFolder.Path.Join(RelativePath.FromUnsanitizedInput(folder))).ToArray();

    private static (RelativePath Path, Hash Hash, Size Size)[] GetInputFiles(Loadout.ReadOnly loadout, IReadOnlyList<string> order)
    {
        if (order.Count == 0) return [];
        var folders = GetInputFolders(order);
        return GetFilesUnderMods(loadout)
            .Where(file => folders.Any(folder => file.Path.InFolder(folder)))
            .OrderBy(file => file.Path)
            .ToArray();
    }

    private static IEnumerable<(RelativePath Path, Hash Hash, Size Size)> GetFilesUnderMods(Loadout.ReadOnly loadout)
        => loadout.Db.Connection.Query<(RelativePath Path, Hash Hash, Size Size)>($"""
            SELECT Path.Path, Hash, Size FROM synchronizer.WinningFiles({loadout.Db})
            WHERE Loadout = {loadout.Id} AND Path.Location = {LocationId.Game.Value}
                AND ItemType IN ('Loadout', 'Game') AND starts_with(lower(Path.Path), 'mods/')
            """);

    internal async Task WriteLoadOrderFile(AbsolutePath loadorderFilePath, Loadout.ReadOnly loadout, CancellationToken cancellationToken = default)
    {
        // TODO: this currently uses the loadout sort order, change this to use the "Active" sort order,
        // once we support switching between collection and loadout sort orders
        // Note(AL12rs): this will get the Load Order for the specific revision of the DB of loadout, which might not be the latest
        var order = GetModOrder(loadout);
        
        // NOTE(erri120): redmod only accepts CRLR line breaks, everything else breaks the program
        // and results in getting errors like `Non-existant mod selected`
        var output = order.Count > 0 ? string.Join("\r\n", order) : string.Empty;
        
        cancellationToken.ThrowIfCancellationRequested();
        await loadorderFilePath.WriteAllTextAsync(output, cancellationToken);
    }
    public string Name => "RedMod Deploy";

    public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
    {
        return monitor.Begin<ITool, Unit>(this, async context =>
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken);
            await Execute(loadout, linkedCancellation.Token);
            return Unit.Default;
        });
    }

    private async Task<TemporaryPath> ExtractTemporaryDeployScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "deploy_redmod.bat";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Resource {resourceName} not found in assembly.");

        using var reader = new StreamReader(stream);
        var file = _temporaryFileManager.CreateFile((Extension?)".bat");
        await file.Path.WriteAllTextAsync(await reader.ReadToEndAsync());
        return file;
    }
}
