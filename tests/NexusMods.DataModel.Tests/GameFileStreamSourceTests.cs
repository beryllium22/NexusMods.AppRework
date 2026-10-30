using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
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
