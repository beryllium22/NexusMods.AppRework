using FomodInstaller.Interface;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk;

namespace NexusMods.Games.FOMOD.CoreDelegates;

internal sealed class ContextDelegates(ILogger<ContextDelegates> logger, FomodDependencyState dependencies) : IContextDelegates
{
    public Task<string> GetAppVersion() => Task.FromResult(ApplicationConstants.Version.ToString());

    public Task<string> GetCurrentGameVersion()
    {
        if (dependencies.GameVersion is not { } version)
            throw new NotSupportedException("The primary executable's version could not be read for this FOMOD installation.");
        return Task.FromResult(version.ToString());
    }

    public Task<string> GetExtenderVersion(string extender)
    {
        logger.LogWarning("Version detection for script extender {Extender} is not available", extender);
        return Task.FromResult("0.0.0.0");
    }

    public Task<bool> IsExtenderPresent()
    {
        logger.LogWarning("Script extender detection is not available for this FOMOD installation");
        return Task.FromResult(false);
    }

    public Task<bool> CheckIfFileExists(string fileName) => Task.FromResult(dependencies.IsActive(fileName));

    public Task<byte[]> GetExistingDataFile(string dataFile)
        => throw new NotSupportedException("FOMOD scripts that read existing data files are not supported.");

    public Task<string[]> GetExistingDataFileList(string folderPath, string searchFilter, bool isRecursive)
        => throw new NotSupportedException("FOMOD scripts that enumerate existing data files are not supported.");
}
