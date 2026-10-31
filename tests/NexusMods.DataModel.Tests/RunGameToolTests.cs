using System.ComponentModel;
using System.Diagnostics;
using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit.Abstractions;

namespace NexusMods.DataModel.Tests;

public class RunGameToolTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<RunGameToolTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services)
            .AddSingleton<ShellProcessRunner>()
            .AddSingleton<IProcessRunner>(provider => provider.GetRequiredService<ShellProcessRunner>());

    [Fact]
    public async Task ShellStartupFailureIsPreservedAndArgumentsReachTheRunner()
    {
        var loadout = await CreateLoadout();
        var runner = ServiceProvider.GetRequiredService<ShellProcessRunner>();
        var expected = new Win32Exception(2, "The test launcher does not exist.");
        runner.RunShell = (_, _) => Task.FromException(expected);

        var executable = GameInstallation.Locations[LocationId.Game].Path.Combine("test shell launcher.exe");
        var tool = new ShellRunGameTool(ServiceProvider, Game, executable);
        string[] arguments = ["--profile", "a profile with spaces", "quoted \"value\""];
        var run = () => tool.Execute(loadout, CancellationToken.None, arguments);

        var failure = await run.Should().ThrowAsync<Win32Exception>();
        failure.Which.Should().BeSameAs(expected, "an unstarted Process has no ExitCode to inspect");
        runner.FileName.Should().Be(executable.ToString());
        runner.WorkingDirectory.Should().Be(executable.Parent.ToString());
        runner.UseShell.Should().BeTrue();
        runner.Arguments.Should().Equal(arguments);
        runner.CapturedProcess.Should().NotBeNull();
        runner.CapturedProcess!.Invoking(process => process.Start()).Should().Throw<ObjectDisposedException>(
            "the caller owns the process even when startup fails");
    }

    [Fact]
    public async Task ShellCancellationIsPreservedAndTheCallerDisposesTheProcess()
    {
        var loadout = await CreateLoadout();
        var runner = ServiceProvider.GetRequiredService<ShellProcessRunner>();
        using var cancellation = new CancellationTokenSource();
        runner.RunShell = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };

        var executable = GameInstallation.Locations[LocationId.Game].Path.Combine("test shell launcher.exe");
        var tool = new ShellRunGameTool(ServiceProvider, Game, executable);
        var run = () => tool.Execute(loadout, cancellation.Token, []);

        var failure = await run.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        runner.CapturedProcess.Should().NotBeNull();
        runner.CapturedProcess!.Invoking(process => process.Start()).Should().Throw<ObjectDisposedException>();
    }

    private sealed class ShellRunGameTool(IServiceProvider provider, Cyberpunk2077Game game, AbsolutePath executable)
        : RunGameTool<Cyberpunk2077Game>(provider, game)
    {
        protected override bool UseShell { get; set; } = true;

        protected override ValueTask<AbsolutePath> GetGamePath(Loadout.ReadOnly loadout)
            => ValueTask.FromResult(executable);
    }

    private sealed class ShellProcessRunner : IProcessRunner
    {
        public Func<Process, CancellationToken, Task> RunShell { get; set; } = (_, _) =>
            throw new NotSupportedException("The test must configure shell execution.");

        public string? FileName { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public bool UseShell { get; private set; }
        public string[] Arguments { get; private set; } = [];
        public Process? CapturedProcess { get; private set; }

        public Task RunAsync(Process process, CancellationToken cancellationToken = default)
        {
            FileName = process.StartInfo.FileName;
            WorkingDirectory = process.StartInfo.WorkingDirectory;
            UseShell = process.StartInfo.UseShellExecute;
            Arguments = process.StartInfo.ArgumentList.ToArray();
            CapturedProcess = process;
            return RunShell(process, cancellationToken);
        }

        public void Run(Command command, bool logOutput = false)
            => throw new NotSupportedException("This test must not start external processes.");

        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException($"{command.TargetFilePath} is unavailable in this test.");
    }
}
