using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk;
using Xunit.Abstractions;
using static NexusMods.Games.RedEngine.Constants;

namespace NexusMods.Games.RedEngine.Tests;

public class Cyberpunk2077ProtontricksDiagnosticTests(ITestOutputHelper outputHelper)
    : ACyberpunkIsolatedGameTest<Cyberpunk2077ProtontricksDiagnosticTests>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<IProcessRunner, ProtontricksNotInstalledProcessRunner>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingProtontricksIsReportedOnlyOnLinuxWithRedModInstalled(bool redModInstalled)
    {
        var loadout = await CreateLoadout();
        if (redModInstalled)
        {
            var redModPath = GameInstallation.Locations.ToAbsolutePath(RedModPath);
            redModPath.Parent.CreateDirectory();
            await redModPath.WriteAllTextAsync("stub redMod.exe");
        }

        var emitter = Game.DiagnosticEmitters.OfType<MissingProtontricksForRedModEmitter>().Single();
        var diagnostics = await emitter.Diagnose(loadout, CancellationToken.None).ToListAsync();

        if (OperatingSystem.IsLinux() && redModInstalled)
            diagnostics.Should().ContainSingle("the REDmod tool needs Protontricks to run on Linux");
        else
            diagnostics.Should().BeEmpty("Protontricks is only needed for REDmod on Linux");
    }

    private sealed class ProtontricksNotInstalledProcessRunner : IProcessRunner
    {
        public void Run(Command command, bool logOutput = false)
            => throw new NotSupportedException("This test must not start external processes.");

        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException($"{command.TargetFilePath} is unavailable in this test.");

        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test must not start external processes.");
    }
}
