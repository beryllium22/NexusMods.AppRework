using System.ComponentModel;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NSubstitute;
using ProcessRunner = NexusMods.Backend.Process.ProcessRunner;
using SystemProcess = System.Diagnostics.Process;

namespace NexusMods.Backend.Tests.Process;

public class ProcessRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Test]
    [Arguments(0, false)]
    [Arguments(23, false)]
    [Arguments(0, true)]
    [Arguments(23, true)]
    public async Task ExitCodeRemainsReadableUntilTheCallerDisposes(int exitCode, bool useShell)
    {
        var runner = CreateRunner();
        using var process = CreateShellProcess($"exit {exitCode}", useShell);
        await runner.RunAsync(process).WaitAsync(Timeout);

        process.HasExited.Should().BeTrue();
        process.ExitCode.Should().Be(exitCode, "the caller still needs to observe the exit result before disposing the process");
    }

    [Test]
    public async Task StartupFailurePreservesTheNativeException()
    {
        var runner = CreateRunner();
        using var process = new SystemProcess
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Path.GetTempPath(), $"app-rework-missing-{Guid.NewGuid():N}.exe"),
                UseShellExecute = false,
            },
        };

        var run = () => runner.RunAsync(process).WaitAsync(Timeout);
        await run.Should().ThrowAsync<Win32Exception>();
    }

    [Test]
    public async Task CancellationBeforeStartupDoesNotCreateAProcess()
    {
        var runner = CreateRunner();
        using var process = CreateWaitingProcess();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var task = runner.RunAsync(process, cancellation.Token);
            var run = () => task.WaitAsync(Timeout);
            var failure = await run.Should().ThrowAsync<OperationCanceledException>();
            failure.Which.CancellationToken.Should().Be(cancellation.Token);
            task.IsCanceled.Should().BeTrue();

            Action readProcessId = () => _ = process.Id;
            readProcessId.Should().Throw<InvalidOperationException>("a pre-cancelled operation must never call Start");
        }
        finally
        {
            await StopIfRunning(process);
        }
    }

    [Test]
    public async Task CancellationWaitsForExitAndLeavesOtherProcessesRunning()
    {
        var runner = CreateRunner();
        using var process = CreateWaitingProcess();
        using var otherProcess = CreateWaitingProcess();
        using var cancellation = new CancellationTokenSource();

        try
        {
            otherProcess.Start().Should().BeTrue();
            (await otherProcess.StandardOutput.ReadLineAsync().WaitAsync(Timeout)).Should().Be("ready");

            var task = runner.RunAsync(process, cancellation.Token);
            (await process.StandardOutput.ReadLineAsync().WaitAsync(Timeout)).Should().Be("ready");
            cancellation.Cancel();

            var run = () => task.WaitAsync(Timeout);
            var failure = await run.Should().ThrowAsync<OperationCanceledException>();
            failure.Which.CancellationToken.Should().Be(cancellation.Token);
            task.IsCanceled.Should().BeTrue();
            process.HasExited.Should().BeTrue("cancellation must finish stopping the process before returning");
            otherProcess.HasExited.Should().BeFalse("only the process started by this operation may be stopped");
        }
        finally
        {
            await StopIfRunning(process);
            await StopIfRunning(otherProcess);
        }
    }

    [Test]
    public async Task CancellationRacingWithNaturalExitHasOneTerminalOutcome()
    {
        var runner = CreateRunner();
        using var process = CreateShellProcess("exit 0", useShell: false);
        using var cancellation = new CancellationTokenSource();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => cancellation.Cancel();

        var task = runner.RunAsync(process, cancellation.Token);
        try
        {
            await task.WaitAsync(Timeout);
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            exception.CancellationToken.Should().Be(cancellation.Token);
        }

        process.HasExited.Should().BeTrue();
        process.ExitCode.Should().Be(0);
        (task.IsCompletedSuccessfully || task.IsCanceled).Should().BeTrue();
    }

    private static SystemProcess CreateWaitingProcess()
    {
        // Shell built-ins block on stdin without creating a child process that could be orphaned.
        var command = OperatingSystem.IsWindows()
            ? "echo ready& set /p input="
            : "printf 'ready\\n'; read input";
        var process = CreateShellProcess(command, useShell: false);
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        return process;
    }

    private static SystemProcess CreateShellProcess(string command, bool useShell)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
                : "/bin/sh",
            UseShellExecute = useShell,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/q");
            startInfo.ArgumentList.Add("/c");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
        }

        startInfo.ArgumentList.Add(command);
        return new SystemProcess { StartInfo = startInfo };
    }

    private static async Task StopIfRunning(SystemProcess process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            return; // A pre-cancelled or failed start has no associated process.
        }

        await process.WaitForExitAsync().WaitAsync(Timeout);
    }

    private static ProcessRunner CreateRunner()
    {
        var memory = new InMemoryFileSystem(OSInformation.FakeUnix);
        var fs = Substitute.For<IFileSystem>();
        fs.OS.Returns(OSInformation.FakeUnix);
        fs.GetKnownPath(Arg.Any<KnownPath>()).Returns(memory.FromUnsanitizedFullPath("/process-runner-tests"));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(ILogger<ProcessRunner>)).Returns(NullLogger<ProcessRunner>.Instance);
        services.GetService(typeof(IFileSystem)).Returns(fs);
        services.GetService(typeof(ISettingsManager)).Returns(Substitute.For<ISettingsManager>());
        return new ProcessRunner(services);
    }
}
