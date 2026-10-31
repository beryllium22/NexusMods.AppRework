using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Steam;
using NexusMods.Backend;
using NexusMods.CrossPlatform;
using NexusMods.DataModel;
using NexusMods.Games.FileHashes;
using NexusMods.Networking.Steam;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.Settings;
using NexusMods.SingleProcess;
using NSubstitute;

namespace NexusMods.CLI.Tests;

public class ForkStorageIsolationTests
{
    [Theory]
    [InlineData("WINDOWS")]
    [InlineData("LINUX")]
    [InlineData("OSX")]
    public async Task DefaultPathsUseForkStorageIncludingTheSyncFileName(string platform)
    {
        await using var services = CreateServices(platform);
        var fs = services.GetRequiredService<IFileSystem>();
        var dataDirectory = fs.GetKnownPath(fs.OS.IsLinux ? KnownPath.XDG_DATA_HOME : KnownPath.LocalApplicationDataDirectory)
            .Combine("NexusMods.AppRework");

        var data = DataModelSettings.CreateDefault(services);
        data.MnemonicDBPath.ToPath(fs).Should().Be(dataDirectory.Combine("DataModel/MnemonicDB.rocksdb"));
        data.ArchiveLocations.Should().ContainSingle().Which.ToPath(fs).Should().Be(dataDirectory.Combine("DataModel/Archives"));
        DataModelSettings.GetLocalApplicationDataDirectory(fs).Should().Be(dataDirectory);
        DataModelSettings.GetStandardDataModelFolder(fs).Should().Be(dataDirectory.Combine("DataModel"));
        FileHashesServiceSettings.CreateDefault(services).HashDatabaseLocation.ToPath(fs)
            .Should().Be(dataDirectory.Combine("FileHashesDatabase"));

        var logDirectory = fs.GetKnownPath(fs.OS.IsLinux ? KnownPath.XDG_STATE_HOME : KnownPath.LocalApplicationDataDirectory)
            .Combine("NexusMods.AppRework/Logs");
        var logging = LoggingSettings.CreateDefault(fs.OS);
        LoggingSettings.GetLogBaseFolder(fs.OS, fs).Should().Be(logDirectory);
        logging.MainProcessLogFilePath.ToPath(fs).Parent.Should().Be(logDirectory);
        logging.MainProcessArchiveFilePath.ToPath(fs).Parent.Should().Be(logDirectory);
        logging.SlimProcessLogFilePath.ToPath(fs).Parent.Should().Be(logDirectory);
        logging.SlimProcessArchiveFilePath.ToPath(fs).Parent.Should().Be(logDirectory);

        var tempDirectory = fs.GetKnownPath(fs.OS.IsLinux ? KnownPath.XDG_STATE_HOME : KnownPath.TempDirectory)
            .Combine("NexusMods.AppRework/Temp");
        FileExtractorSettings.CreateDefault(services).TempFolderLocation.ToPath(fs).Should().Be(tempDirectory);

        var syncDirectory = fs.OS.MatchPlatform(
            () => fs.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Combine("NexusMods.AppRework"),
            () => fs.GetKnownPath(KnownPath.XDG_RUNTIME_DIR),
            () => fs.GetKnownPath(KnownPath.ApplicationDataDirectory).Combine("NexusMods.AppRework")
        );
        var cli = services.GetRequiredService<ISettingsManager>().GetDefault<CliSettings>();
        cli.SyncFile.Should().Be(syncDirectory.Combine("NexusMods.AppRework-sync_file.sync"));
    }

    [Theory]
    [InlineData("WINDOWS")]
    [InlineData("LINUX")]
    [InlineData("OSX")]
    public async Task JsonStorageIgnoresOriginalConfigsAndPreservesExplicitForkPaths(string platform)
    {
        await using var services = CreateServices(platform);
        var fs = services.GetRequiredService<IFileSystem>();
        var baseDirectory = fs.GetKnownPath(fs.OS.IsLinux ? KnownPath.XDG_DATA_HOME : KnownPath.LocalApplicationDataDirectory);
        var legacyDirectory = baseDirectory.Combine(fs.OS.IsOSX ? "NexusMods_App" : "NexusMods.App");
        var configName = $"{typeof(DataModelSettings).FullName}.json";
        var legacyConfig = legacyDirectory.Combine("Configs").Combine(configName);
        var existing = new DataModelSettings
        {
            MnemonicDBPath = new ConfigurablePath(null, legacyDirectory.Combine("original-database").ToString()),
            ArchiveLocations = [new ConfigurablePath(null, legacyDirectory.Combine("original-archives").ToString())],
        };
        var originalJson = JsonSerializer.Serialize(existing);
        legacyConfig.Parent.CreateDirectory();
        await legacyConfig.WriteAllTextAsync(originalJson);

        using var backend = new JsonStorageBackend(NullLogger<JsonStorageBackend>.Instance, services);
        backend.Load<DataModelSettings>(key: null).Should().BeNull("the original app's settings belong to a different profile");

        // Explicit paths are user choices, even when they point outside the fork's default directory.
        var configured = new DataModelSettings
        {
            MnemonicDBPath = new ConfigurablePath(null, baseDirectory.Combine("chosen-database").ToString()),
            ArchiveLocations = [new ConfigurablePath(null, baseDirectory.Combine("chosen-archives").ToString())],
        };
        backend.Save(configured, key: null);

        var loaded = backend.Load<DataModelSettings>(key: null);
        loaded.Should().NotBeNull();
        loaded!.MnemonicDBPath.Should().Be(configured.MnemonicDBPath);
        loaded.ArchiveLocations.Should().Equal(configured.ArchiveLocations);
        baseDirectory.Combine("NexusMods.AppRework/Configs").Combine(configName).FileExists.Should().BeTrue();
        (await legacyConfig.ReadAllTextAsync()).Should().Be(originalJson);
    }

    [Theory]
    [InlineData("WINDOWS")]
    [InlineData("LINUX")]
    [InlineData("OSX")]
    public async Task SteamAuthDoesNotReadOrOverwriteOriginalCredentials(string platform)
    {
        await using var services = CreateServices(platform);
        var fs = services.GetRequiredService<IFileSystem>();
        var baseDirectory = fs.GetKnownPath(KnownPath.LocalApplicationDataDirectory);
        var legacyAuth = baseDirectory.Combine(fs.OS.IsOSX ? "NexusMods_App" : "NexusMods.App").Combine("steam/auth");
        byte[] originalCredentials = [1, 2, 3];
        legacyAuth.Parent.CreateDirectory();
        await legacyAuth.WriteAllBytesAsync(originalCredentials);

        var storage = services.GetRequiredService<IAuthStorage>();
        (await storage.TryLoad()).Success.Should().BeFalse();

        byte[] forkCredentials = [4, 5, 6];
        await storage.SaveAsync(forkCredentials);
        var loaded = await storage.TryLoad();
        loaded.Success.Should().BeTrue();
        loaded.Data.Should().Equal(forkCredentials);
        (await baseDirectory.Combine("NexusMods.AppRework/steam/auth").ReadAllBytesAsync()).Should().Equal(forkCredentials);
        (await legacyAuth.ReadAllBytesAsync()).Should().Equal(originalCredentials);
    }

    private static ServiceProvider CreateServices(string platform)
    {
        IOSInformation os = new OSInformation(OSPlatform.Create(platform));
        var memory = new InMemoryFileSystem(os);
        var root = os.IsWindows ? "C:/storage-isolation" : "/storage-isolation";
        var fs = Substitute.For<IFileSystem>();
        fs.OS.Returns(os);

        // Every known path stays in this in-memory filesystem, regardless of the host OS or drive.
        fs.GetKnownPath(Arg.Any<KnownPath>()).Returns(call =>
            memory.FromUnsanitizedFullPath($"{root}/{call.Arg<KnownPath>()}"));

        return new ServiceCollection()
            .AddSingleton(fs)
            .AddSingleton(new JsonSerializerOptions())
            .AddLogging()
            .AddSettingsManager()
            .AddSettings<CliSettings>()
            .AddLocalAuthFileStorage()
            .BuildServiceProvider();
    }
}
