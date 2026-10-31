using FomodInstaller.Interface;

namespace NexusMods.Games.FOMOD.CoreDelegates;

/// <summary>
/// FOMOD file conditions are evaluated against the profile being modified. For the current
/// Creation Engine integration, enabled plugin files are also the active plugins.
/// </summary>
internal sealed class PluginDelegates(FomodDependencyState dependencies) : IPluginDelegates
{
    public Task<string[]> GetAll(bool activeOnly) => Task.FromResult(dependencies.GetPlugins(activeOnly));
    public Task<bool> IsActive(string pluginName) => Task.FromResult(dependencies.IsActive(pluginName));
    public Task<bool> IsPresent(string pluginName) => Task.FromResult(dependencies.IsPresent(pluginName));
}
