using FomodInstaller.Interface;
using FomodInstaller.Interface.ui;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GuidedInstallers;

namespace NexusMods.Games.FOMOD.CoreDelegates;

[UsedImplicitly]
public sealed class InstallerDelegates : ICoreDelegates, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private UiDelegates? _uiDelegates;

    public IContextDelegates context { get; private set; }
    public IIniDelegates ini => throw new NotImplementedException();
    public IPluginDelegates plugin { get; private set; }

    public IUIDelegates ui => UiDelegates;
    public UiDelegates UiDelegates => _uiDelegates ??= new UiDelegates(
        _loggerFactory.CreateLogger<UiDelegates>(), _serviceProvider.GetRequiredService<IGuidedInstaller>(), ownsGuidedInstaller: false);

    public InstallerDelegates(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        context = new ContextDelegates(loggerFactory.CreateLogger<ContextDelegates>(), FomodDependencyState.Empty);
        plugin = new PluginDelegates(FomodDependencyState.Empty);
    }

    internal void Initialize(FomodDependencyState dependencies, FomodOption[]? options, CancellationToken cancellationToken)
    {
        if (_uiDelegates is not null) throw new InvalidOperationException("FOMOD delegates cannot be reused between installations.");

        context = new ContextDelegates(_loggerFactory.CreateLogger<ContextDelegates>(), dependencies);
        plugin = new PluginDelegates(dependencies);
        var guidedInstaller = options is null
            ? _serviceProvider.GetRequiredService<IGuidedInstaller>()
            : new PresetGuidedInstaller(options);
        _uiDelegates = new UiDelegates(_loggerFactory.CreateLogger<UiDelegates>(), guidedInstaller, cancellationToken, ownsGuidedInstaller: options is not null);
    }

    public void Dispose() => _uiDelegates?.Dispose();
}
