using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Aggregation;
using DynamicData.Kernel;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls.LoadoutBadge;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Pages;
using NexusMods.App.UI.Pages.MyLoadouts;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Dialog;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Controls.LoadoutCard;

public class LoadoutCardViewModel : AViewModel<ILoadoutCardViewModel>, ILoadoutCardViewModel, IDisposable
{
    private readonly ILogger<LoadoutCardViewModel> _logger;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IWindowManager _windowManager;
    private readonly IConnection _connection;
    private readonly IServiceProvider _services;
    private readonly CompositeDisposable _lifetime = new();
    private readonly SerialDisposable _activeLifetime = new();
    private int _operationRunning;
    private int _disposed;

    public LoadoutCardViewModel(Loadout.ReadOnly loadout, IConnection conn, IServiceProvider serviceProvider)
    {
        _connection = conn;
        _services = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<LoadoutCardViewModel>>();
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _windowManager = serviceProvider.GetRequiredService<IWindowManager>();
        var applyService = serviceProvider.GetRequiredService<ISynchronizerService>();
        LoadoutVal = loadout;
        LoadoutName = loadout.Name;
        LoadoutBadgeViewModel = serviceProvider.GetRequiredService<ILoadoutBadgeViewModel>();
        LoadoutBadgeViewModel.LoadoutValue = loadout;
        RefreshTimes();

        var canEdit = this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy);
        CloneLoadoutCommand = ReactiveCommand.CreateFromTask(() => RunAction("Duplicating profile…", Duplicate), canEdit);
        RenameLoadoutCommand = ReactiveCommand.CreateFromTask(() => RunAction("Renaming profile…", Rename), canEdit);
        var canDelete = this.WhenAnyValue(vm => vm.IsBusy, vm => vm.IsLastLoadout, vm => vm.IsLoadoutApplied)
            .Select(state => !state.Item1 && !state.Item2 && !state.Item3);
        DeleteLoadoutCommand = ReactiveCommand.CreateFromTask(() => RunAction("Deleting profile…", Delete), canDelete);
        foreach (var command in new[] { CloneLoadoutCommand, RenameLoadoutCommand, DeleteLoadoutCommand })
            command.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "Profile command failed for {Loadout}", loadout.LoadoutId))
                .DisposeWith(_lifetime);

        this.WhenActivated((CompositeDisposable activation) =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var d = new CompositeDisposable();
            _activeLifetime.Disposable = d;
            d.DisposeWith(activation);
            Disposable.Create(() => LoadoutImage = null).DisposeWith(d);
            Observable.FromAsync(async () =>
                {
                    var bitmap = await LoadImage(loadout.InstallationInstance);
                    // Own the bitmap before scheduling UI delivery. If the card
                    // disappeared during decoding, a disposed lifetime releases it.
                    if (bitmap is not null) bitmap.DisposeWith(d);
                    return bitmap;
                })
                .OnUI()
                .Subscribe(bitmap => LoadoutImage = bitmap)
                .DisposeWith(d);

            Loadout.Observe(conn, loadout.Id)
                .Where(current => current.IsValid())
                .OnUI()
                .Subscribe(UpdateLoadout)
                .DisposeWith(d);

            applyService.LastAppliedRevisionFor(loadout.InstallationInstance)
                .Select(revision => revision.Id == loadout.LoadoutId)
                .OnUI()
                .BindToVM(this, vm => vm.IsLoadoutApplied)
                .DisposeWith(d);

            Observable.FromAsync(() => applyService.StatusForLoadout(loadout.LoadoutId))
                .Switch()
                .DistinctUntilChanged()
                .OnUI()
                .Subscribe(state =>
                {
                    HasPendingChanges = state == LoadoutSynchronizerState.NeedsSync;
                    StatusText = state switch
                    {
                        LoadoutSynchronizerState.Current => "Applied · up to date",
                        LoadoutSynchronizerState.NeedsSync => "Changes waiting to apply",
                        LoadoutSynchronizerState.OtherLoadoutSynced => "Ready to apply",
                        _ => "Checking changes…",
                    };
                }, ex =>
                {
                    StatusText = "Status unavailable";
                    _logger.LogError(ex, "Could not read profile status for {Loadout}", loadout.LoadoutId);
                })
                .DisposeWith(d);

            Observable.Interval(TimeSpan.FromMinutes(1)).OnUI().Subscribe(_ => RefreshTimes()).DisposeWith(d);
            LoadoutDataProviderHelper.CountAllLoadoutItems(serviceProvider, loadout.LoadoutId)
                .Select(count => string.Format(Language.LoadoutCardViewModel_FormatNumMods_Mods__0_, count))
                .OnUI().BindToVM(this, vm => vm.LoadoutModCount).DisposeWith(d);

            Loadout.ObserveAll(conn)
                .Filter(current => current.IsVisible() && current.InstallationId == loadout.InstallationId && current.Id != loadout.Id)
                .Count().Select(count => count == 0)
                .OnUI().BindToVM(this, vm => vm.IsLastLoadout).DisposeWith(d);
        });
    }

    public Loadout.ReadOnly LoadoutVal { get; private set; }
    public ILoadoutBadgeViewModel LoadoutBadgeViewModel { get; }
    [Reactive] public string LoadoutName { get; private set; }
    [Reactive] public IImage? LoadoutImage { get; private set; }
    [Reactive] public bool IsLoadoutApplied { get; private set; }
    [Reactive] public bool HasPendingChanges { get; private set; }
    [Reactive] public string StatusText { get; private set; } = "Checking changes…";
    [Reactive] public string HumanizedLoadoutLastApplyTime { get; private set; } = string.Empty;
    [Reactive] public string HumanizedLoadoutCreationTime { get; private set; } = string.Empty;
    [Reactive] public string LoadoutModCount { get; private set; } = string.Empty;
    [Reactive] public bool IsBusy { get; private set; }
    [Reactive] public string BusyText { get; private set; } = string.Empty;
    public bool IsDeleting => IsBusy && BusyText == "Deleting profile…";
    public bool IsSkeleton => false;
    [Reactive] public bool IsLastLoadout { get; private set; } = true;
    public required ReactiveCommand<Unit, Unit> VisitLoadoutCommand { get; init; }
    public ReactiveCommand<Unit, Unit> CloneLoadoutCommand { get; }
    public ReactiveCommand<Unit, Unit> RenameLoadoutCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteLoadoutCommand { get; }

    public void UpdateLoadout(Loadout.ReadOnly loadout)
    {
        LoadoutVal = loadout;
        LoadoutName = loadout.Name;
        LoadoutBadgeViewModel.LoadoutValue = loadout;
        RefreshTimes();
    }

    private void RefreshTimes()
    {
        HumanizedLoadoutCreationTime = string.Format(Language.LoadoutCardViewModel_CreationTimeConverter_Created__0_, LoadoutVal.GetCreatedAt().Humanize());
        var applied = LoadoutVal.LastAppliedDateTime.ValueOr(DateTimeOffset.MinValue);
        HumanizedLoadoutLastApplyTime = string.Format(Language.LoadoutCardViewModel_FormatLastAppliedTime_Last_applied__0_,
            applied == DateTimeOffset.MinValue ? Language.HumanizedDateTime_Never : applied.Humanize());
    }

    private async Task RunAction(string status, Func<Task> action)
    {
        if (Interlocked.CompareExchange(ref _operationRunning, 1, 0) != 0) return;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { BusyText = status; IsBusy = true; });
            await action();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Profile operation cancelled for {Loadout}", LoadoutVal.LoadoutId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Profile operation failed for {Loadout}", LoadoutVal.LoadoutId);
            await MessageBoxOkViewModel.Show(_services, "Could not update profile", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _operationRunning, 0);
            await Dispatcher.UIThread.InvokeAsync(() => { IsBusy = false; BusyText = string.Empty; });
        }
    }

    private async Task Duplicate()
    {
        var result = await _windowManager.ShowDialog(ProfileDialogs.Duplicate(LoadoutName), DialogWindowType.Modal);
        if (result.ButtonId != ButtonDefinitionId.Accept) return;
        await Task.Run(async () => await _loadoutManager.CopyLoadout(LoadoutVal.LoadoutId, suggestedName: result.InputText));
    }

    private async Task Rename()
    {
        var result = await _windowManager.ShowDialog(ProfileDialogs.Rename(LoadoutName), DialogWindowType.Modal);
        if (result.ButtonId != ButtonDefinitionId.Accept) return;
        await Task.Run(async () => await _loadoutManager.RenameLoadout(LoadoutVal.LoadoutId, result.InputText));
    }

    private async Task Delete()
    {
        var result = await _windowManager.ShowDialog(ProfileDialogs.Delete(LoadoutName), DialogWindowType.Modal);
        if (result.ButtonId != ButtonDefinitionId.Accept) return;
        var current = Loadout.Load(_connection.Db, LoadoutVal.Id);
        if (!current.IsValid()) return;
        if (Loadout.FindByInstallation(current.Db, current.InstallationId).Count(profile => profile.IsVisible()) <= 1)
            throw new InvalidOperationException("Keep at least one profile for this game, or remove the game from My Games.");
        if (GameInstallMetadata.LastSyncedLoadout.TryGetValue(current.Installation, out var applied) && applied == current.Id)
            throw new InvalidOperationException("Apply a different profile before deleting this one.");
        await Task.Run(async () => await _loadoutManager.DeleteLoadout(current.LoadoutId));
    }

    private async Task<Bitmap?> LoadImage(GameInstallation installation)
    {
        try
        {
            return await Task.Run(async () =>
            {
                await using var stream = await installation.GetGame().IconImage.GetStreamAsync();
                return Bitmap.DecodeToWidth(stream, (int)ImageSizes.GameThumbnail.Width);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load game image for {Game}", installation.Game.DisplayName);
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Avalonia can release its activation handle after the data source has
        // removed this card. Dispose our resources now while leaving that handle
        // valid for the framework's later deactivation notification.
        _activeLifetime.Dispose();
        _lifetime.Dispose();
        CloneLoadoutCommand.Dispose();
        RenameLoadoutCommand.Dispose();
        DeleteLoadoutCommand.Dispose();
        VisitLoadoutCommand.Dispose();
    }
}
