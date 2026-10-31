using CliWrap;
using JetBrains.Annotations;

namespace NexusMods.Sdk;

/// <summary>
/// Process abstraction.
/// </summary>
[PublicAPI]
public interface IProcessRunner
{
    /// <summary>
    /// Runs a command.
    /// </summary>
    /// <remarks>
    /// This method exists immediately and doesn't wait for the process to exit.
    /// </remarks>
    void Run(Command command, bool logOutput = false);

    /// <summary>
    /// Runs a command.
    /// </summary>
    /// <returns>A task that completes when the spawned process exists.</returns>
    Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an unstarted process and waits for it to exit.
    /// </summary>
    /// <remarks>
    /// The caller owns <paramref name="process"/> and must dispose it after awaiting this method.
    /// Startup failures propagate to the caller. A token cancelled before startup prevents the
    /// process from starting. Cancellation after startup stops only the started process and
    /// waits for it to exit before propagating cancellation; a failure to stop it propagates instead.
    /// </remarks>
    /// <returns>A task that completes when the spawned process exits.</returns>
    Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default);
}
