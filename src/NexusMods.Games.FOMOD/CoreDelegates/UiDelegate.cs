using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.GuidedInstallers.ValueObjects;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using GroupId = NexusMods.Abstractions.GuidedInstallers.ValueObjects.GroupId;
using Option = NexusMods.Abstractions.GuidedInstallers.Option;
using OptionGroup = NexusMods.Abstractions.GuidedInstallers.OptionGroup;
using OptionGroupType = NexusMods.Abstractions.GuidedInstallers.OptionGroupType;
using OptionType = NexusMods.Abstractions.GuidedInstallers.OptionType;

namespace NexusMods.Games.FOMOD.CoreDelegates;

public sealed class UiDelegates : FomodInstaller.Interface.ui.IUIDelegates, IDisposable
{
    /// <summary>
    /// Created by the executor, this delegate notifies the executor about which options the
    /// user selected.
    /// </summary>
    /// <param name="stepId">
    /// The ID of the step where the user selected the options.
    /// This is most likely going to be the current step.
    /// </param>
    /// <param name="selectedGroupId">The ID of the selected group.</param>
    /// <param name="selectedOptionIds">The IDs of the selected options.</param>
    private delegate void SelectOptions(int stepId, int selectedGroupId, int[] selectedOptionIds);

    private static void DummySelectOptions(int stepId, int selectedGroupId, int[] selectedOptionIds) { }

    /// <summary>
    /// Created by the executor, this delegate notifies the executor that the installer should
    /// proceed to the next or previous step.
    /// </summary>
    /// <param name="forward">Whether to move forward or backwards in the installer.</param>
    /// <param name="currentStepId">The ID of the current step.</param>
    private delegate void ContinueToNextStep(bool forward, int currentStepId);

    private static void DummyContinueToNextStep(bool forward, int currentStepId) { }

    /// <summary>
    /// Created by the executor, this delegate notifies the executor that the installer
    /// should exit.
    /// </summary>
    private delegate void CancelInstaller();

    private static void DummyCancelInstaller() { }

    private readonly ILogger<UiDelegates> _logger;
    private readonly IGuidedInstaller _guidedInstaller;

    private SelectOptions _selectOptions = DummySelectOptions;
    private ContinueToNextStep _continueToNextStep = DummyContinueToNextStep;
    private CancelInstaller _cancelInstaller = DummyCancelInstaller;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationToken _externalCancellationToken;
    private readonly bool _ownsGuidedInstaller;
    private CancellationTokenRegistration _cancellationRegistration;
    private TaskCompletionSource? _selectionUpdated;
    private ExceptionDispatchInfo? _failure;
    private bool _requestInProgress;
    private int _dialogStarted;
    private int _dialogEnded;
    private int _cancelRequested;
    private int _disposed;

    public IReadOnlyDictionary<RelativePath, LibraryArchiveFileEntry.ReadOnly>? CurrentFomodArchiveFiles;

    public UiDelegates(ILogger<UiDelegates> logger, IGuidedInstaller guidedInstaller,
        CancellationToken cancellationToken = default, bool ownsGuidedInstaller = true)
    {
        _logger = logger;
        _guidedInstaller = guidedInstaller;
        _externalCancellationToken = cancellationToken;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationToken = _lifetime.Token;
        _ownsGuidedInstaller = ownsGuidedInstaller;
    }

    public void StartDialog(string? moduleName, FomodInstaller.Interface.HeaderImage image,
        Action<int, int, int[]> select, Action<bool, int> cont, Action cancel)
    {
        _selectOptions = new SelectOptions(select);
        _continueToNextStep = new ContinueToNextStep(cont);
        _cancelInstaller = new CancelInstaller(cancel);
        Interlocked.Exchange(ref _dialogStarted, 1);
        _guidedInstaller.SetupInstaller(moduleName ?? "FOMOD Installer");
        _cancellationRegistration = _externalCancellationToken.Register(() =>
            Abort(new OperationCanceledException(_externalCancellationToken)));
    }

    public void EndDialog()
    {
        if (Interlocked.Exchange(ref _dialogEnded, 1) != 0) return;
        _lifetime.Cancel();
        _selectOptions = DummySelectOptions;
        _continueToNextStep = DummyContinueToNextStep;
        _cancelInstaller = DummyCancelInstaller;
        CurrentFomodArchiveFiles = null;
        if (Volatile.Read(ref _dialogStarted) == 0) return;
        try
        {
            _guidedInstaller.CleanupInstaller();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to close the FOMOD installer");
        }
    }

    public void ReportError(string title, string message, string details)
        => _logger.LogError("Reporting an error: {Title}: {Message}\n{Details}", title, message, details);

    /// <summary>Surfaces callback failures after the executor's cancellation has settled.</summary>
    public void ThrowIfFailed() => _failure?.Throw();

    private void Abort(Exception exception)
    {
        if (Volatile.Read(ref _dialogEnded) != 0 || Interlocked.Exchange(ref _cancelRequested, 1) != 0) return;
        _failure = ExceptionDispatchInfo.Capture(exception);
        var cancel = _cancelInstaller;
        EndDialog();
        cancel();
    }

    public void UpdateState(FomodInstaller.Interface.ui.InstallerStep[] installSteps, int currentStepId)
    {
        if (Volatile.Read(ref _dialogEnded) != 0) return;
        lock (_gate)
        {
            // The library acknowledges each selection with another state callback. Wait for
            // that callback before continuing, rather than relying on a 200ms timing window.
            if (_selectionUpdated is not null)
            {
                _selectionUpdated.TrySetResult();
                return;
            }
            if (_requestInProgress) return;
            _requestInProgress = true;
        }

        // HandleStepAsync observes every exception and cancels the executor on failure.
        _ = HandleStepAsync(installSteps, currentStepId);
    }

    private async Task HandleStepAsync(FomodInstaller.Interface.ui.InstallerStep[] installSteps, int currentStepId)
    {
        try
        {
            if (currentStepId < 0 || currentStepId >= installSteps.Length)
                throw new InvalidDataException("The FOMOD installer requested an invalid installation step.");

            var groupMappings = new List<KeyValuePair<int, GroupId>>();
            var optionMappings = new List<KeyValuePair<int, OptionId>>();
            var step = ToGuidedInstallationStep(installSteps, currentStepId, groupMappings, optionMappings);
            var progress = Percent.Create(current: currentStepId + 1,
                maximum: currentStepId + 1 + installSteps.Skip(currentStepId + 1).Count(candidate => candidate.visible) + 1);
            var result = await _guidedInstaller.RequestUserChoice(step, progress, _cancellationToken)
                .WaitAsync(_cancellationToken);
            _cancellationToken.ThrowIfCancellationRequested();

            if (result.IsCancelInstallation)
            {
                Abort(new OperationCanceledException("The FOMOD installation was cancelled."));
                return;
            }

            if (result.IsGoToNextStep)
            {
                // Send every group, including an empty choice. Otherwise the executor keeps
                // recommended/default options that a collection preset explicitly omitted.
                foreach (var (groupIndex, groupId) in groupMappings)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var selected = result.AsT2.SelectedOptions.Where(option => option.GroupId == groupId)
                        .Select(option => optionMappings.First(mapping => mapping.Value == option.OptionId).Key).ToArray();
                    var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate) _selectionUpdated = acknowledgement;
                    try
                    {
                        _selectOptions(currentStepId, groupIndex, selected);
                        await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(30), _cancellationToken);
                    }
                    finally
                    {
                        lock (_gate) _selectionUpdated = null;
                    }
                }
            }

            _cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _requestInProgress = false;
            _continueToNextStep(forward: result.IsGoToNextStep, currentStepId);
        }
        catch (Exception exception)
        {
            Abort(exception);
        }
    }

    private GuidedInstallationStep ToGuidedInstallationStep(
        IList<FomodInstaller.Interface.ui.InstallerStep> installSteps,
        int currentStepId,
        ICollection<KeyValuePair<int, GroupId>> groupIdMapping,
        ICollection<KeyValuePair<int, OptionId>> optionIdMappings)
    {
        var groups = installSteps[currentStepId].optionalFileGroups;

        var stepGroups = groups.group.Select(group =>
        {
            var groupId = GroupId.From(Guid.NewGuid());
            groupIdMapping.Add(new KeyValuePair<int, GroupId>(group.id, groupId));

            var groupType = ConvertOptionGroupType(group.type);

            return new OptionGroup
            {
                Id = groupId,
                Type = groupType,
                Name = group.name,
                Options = ToOptions(group.options, optionIdMappings, groupType, group.type == "SelectAll").ToArray(),
            };
        });

        return new GuidedInstallationStep
        {
            Id = StepId.From(Guid.NewGuid()),
            Name = installSteps[currentStepId].name ?? string.Empty,
            Groups = stepGroups.ToArray(),
            HasPreviousStep = currentStepId != 0,
            HasNextStep = currentStepId != installSteps.Count - 1,
        };
    }

    private IEnumerable<Option> ToOptions(
        IReadOnlyCollection<FomodInstaller.Interface.ui.Option> options,
        ICollection<KeyValuePair<int, OptionId>> optionIdMappings,
        OptionGroupType optionGroupType, bool selectAll)
    {
        return options.Select(option =>
        {
            var optionId = OptionId.From(Guid.NewGuid());
            optionIdMappings.Add(new KeyValuePair<int, OptionId>(option.id, optionId));

            var optionType = MakeOptionType(option);

            // NOTE(erri120): If the group only contains a single option that is required by the group type,
            // the option type should be set to required. Some authors and FOMOD builders aren't doing this
            // automatically, so we have to change the type to required afterwards.
            if (optionType != OptionType.Disabled && (selectAll || options.Count == 1 && optionGroupType is (OptionGroupType.ExactlyOne or OptionGroupType.AtLeastOne)))
            {
                optionType = OptionType.Required;
            }

            return new Option
            {
                Id = optionId,
                Name = option.name,
                Description = string.IsNullOrWhiteSpace(option.description) ? null : option.description,
                Image = CreateOptionImage(option),
                Type = optionType,
            };
        });
    }

    private OptionImage? CreateOptionImage(FomodInstaller.Interface.ui.Option option)
    {
        var image = option.image;
        if (image is null) return null;
        if (Uri.TryCreate(image, UriKind.Absolute, out var uri)) return new OptionImage(uri);

        var path = RelativePath.FromUnsanitizedInput(image);

        if (CurrentFomodArchiveFiles is null) return null;
        if (!CurrentFomodArchiveFiles.TryGetValue(path, out var libraryArchiveEntry))
        {
            _logger.LogWarning("Image at `{Path}` doesn't exist in the archive", path);
            return null;
        }

        return new OptionImage(new OptionImage.ImageStoredFile(libraryArchiveEntry.AsLibraryFile().Hash));
    }

    private static OptionType MakeOptionType(FomodInstaller.Interface.ui.Option option)
    {
        var state = option.type switch
        {
            "Required" => OptionType.Required,
            "NotUsable" => OptionType.Disabled,
            "Recommended" => OptionType.PreSelected,
            _ => OptionType.Available,
        };

        return state;
    }

    private static OptionGroupType ConvertOptionGroupType(string input)
    {
        return input switch
        {
            "SelectAtLeastOne" => OptionGroupType.AtLeastOne,
            "SelectAtMostOne" => OptionGroupType.AtMostOne,
            "SelectExactlyOne" => OptionGroupType.ExactlyOne,
            _ => OptionGroupType.Any,
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellationRegistration.Dispose();
        EndDialog();
        if (_ownsGuidedInstaller) _guidedInstaller.Dispose();
        _lifetime.Dispose();
    }
}
