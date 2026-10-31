using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using Xunit.Abstractions;

namespace NexusMods.DataModel.Tests;

public class GameFileStreamSourceTests : ACyberpunkIsolatedGameTest<GameFileStreamSourceTests>
{
    private readonly GameFileStreamSource _source;

    public GameFileStreamSourceTests(ITestOutputHelper helper) : base(helper)
    {
        _source = ServiceProvider.GetServices<IReadOnlyStreamSource>().OfType<GameFileStreamSource>().Single();
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddSingleton<IProcessRunner, NoExternalProcesses>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveOnlyReturnsUnchangedSyncedGameFiles(bool changeFileAfterScan)
    {
        var loadout = await CreateLoadout();
        loadout = await Synchronizer.Synchronize(loadout);

        var gamePath = new GamePath(LocationId.Game, "bin/resolveTestFile.txt");
        var diskPath = GameInstallation.Locations.ToAbsolutePath(gamePath);
        diskPath.Parent.CreateDirectory();
        await diskPath.WriteAllTextAsync("Hello Resolve!");

        loadout = await Synchronizer.Synchronize(loadout);
        var diskEntry = DiskStateEntry.FindByGame(loadout.Installation.Db, loadout.Installation)
            .Single(file => file.Path.Item2 == gamePath.LocationId && file.Path.Item3 == gamePath.Path);

        if (changeFileAfterScan)
            await diskPath.WriteAllTextAsync("Different content after the disk scan");

        // The disk-state query must map the location and path columns to the typed row correctly.
        // Path.Item1 is a parent entity ID, already selected as Game, and must not shift those columns.
        var resolved = _source.Resolve(diskEntry.Hash);

        if (changeFileAfterScan)
        {
            resolved.Should().BeNull("a changed file no longer has the requested hash");
            _source.Exists(diskEntry.Hash).Should().BeFalse();
        }
        else
        {
            resolved.Should().Be(diskPath);
            _source.Exists(diskEntry.Hash).Should().BeTrue();
            await using var stream = await _source.OpenAsync(diskEntry.Hash);
            stream.Should().NotBeNull();
            using var reader = new StreamReader(stream!);
            (await reader.ReadToEndAsync()).Should().Be("Hello Resolve!");
        }
    }

    [Fact]
    public async Task SkipsUnmanagedHashCandidateAndFindsTheAppliedInstallation()
    {
        var loadout = await CreateLoadout();
        var gamePath = new GamePath(LocationId.Game, "bin/usable-candidate.txt");
        var diskPath = GameInstallation.Locations.ToAbsolutePath(gamePath);
        diskPath.Parent.CreateDirectory();
        await diskPath.WriteAllTextAsync("Usable matching game file");
        loadout = await Synchronizer.Synchronize(loadout);
        var diskEntry = DiskStateEntry.FindByGame(loadout.Installation.Db, loadout.Installation)
            .Single(file => file.Path.Item2 == gamePath.LocationId && file.Path.Item3 == gamePath.Path);

        using (var tx = Connection.BeginTransaction())
        {
            var unmanaged = new GameInstallMetadata.New(tx)
            {
                GameId = loadout.Installation.GameId,
                Store = loadout.Installation.Store,
                Path = loadout.Installation.Path + "-unmanaged",
                Name = "Unmanaged hash candidate",
            };
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                GameId = unmanaged.Id,
                Path = gamePath.ToGamePathParentTuple(unmanaged.Id),
                Hash = diskEntry.Hash,
                Size = diskEntry.Size,
                // Resolve orders candidates by date, so this unusable row is visited first.
                LastModified = diskEntry.LastModified.AddMinutes(1),
            };
            await tx.Commit();
        }

        _source.Resolve(diskEntry.Hash).Should().Be(diskPath);
        await using var stream = await _source.OpenAsync(diskEntry.Hash);
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        (await reader.ReadToEndAsync()).Should().Be("Usable matching game file");
    }

    private sealed class NoExternalProcesses : IProcessRunner
    {
        public void Run(Command command, bool logOutput = false)
            => throw new NotSupportedException("This test must not start external processes.");

        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException($"{command.TargetFilePath} is unavailable in this test.");

        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test must not start external processes.");
    }
}
