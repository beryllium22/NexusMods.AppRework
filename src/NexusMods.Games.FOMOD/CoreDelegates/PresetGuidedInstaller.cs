using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Games.FOMOD.CoreDelegates;

/// <summary>Replays a collection's recorded choices against the named installer steps.</summary>
public sealed class PresetGuidedInstaller(FomodOption[] steps) : IGuidedInstaller
{
    private List<FomodOption> _remainingSteps = steps.ToList();

    public void Dispose() { }
    public void SetupInstaller(string name) => _remainingSteps = steps.ToList();
    public void CleanupInstaller() { }

    public Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Conditional steps can be hidden on this profile. Match names instead of advancing a
        // positional index, while retaining occurrence order for legitimate repeated names.
        var presetIndex = _remainingSteps.FindIndex(step => step.name == installationStep.Name);
        if (presetIndex < 0)
            throw new InvalidDataException($"The collection has no FOMOD choices for step '{installationStep.Name}'.");
        var preset = _remainingSteps[presetIndex];
        var remainingGroups = preset.groups.ToList();
        var choices = new List<SelectedOption>();

        foreach (var group in installationStep.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupIndex = remainingGroups.FindIndex(candidate => candidate.name == group.Name);
            var recordedChoices = groupIndex < 0 ? [] : remainingGroups[groupIndex].choices;
            if (groupIndex >= 0) remainingGroups.RemoveAt(groupIndex);

            foreach (var choice in recordedChoices)
            {
                var matches = group.Options.Where(option => option.Name == choice.name).ToArray();
                var option = matches.Length == 1 ? matches[0]
                    : choice.idx >= 0 && choice.idx < group.Options.Length && group.Options[choice.idx].Name == choice.name
                        ? group.Options[choice.idx] : null;
                if (option is null)
                    throw new InvalidDataException($"The collection's FOMOD choice '{choice.name}' does not match step '{installationStep.Name}', group '{group.Name}'.");
                if (option.Type == OptionType.Disabled)
                    throw new InvalidDataException($"The collection's FOMOD choice '{choice.name}' is unavailable in this profile (step '{installationStep.Name}').");

                choices.Add(new SelectedOption(group.Id, option.Id));
            }

            // The executor always enables required options, even if older presets omitted them.
            choices.AddRange(group.Options.Where(option => option.Type == OptionType.Required)
                .Select(option => new SelectedOption(group.Id, option.Id)));
        }

        if (remainingGroups.Count > 0)
            throw new InvalidDataException($"The collection's FOMOD group '{remainingGroups[0].name}' was not found in step '{installationStep.Name}'.");

        var selected = choices.Distinct().ToArray();
        var invalidGroups = GuidedInstallerValidation.ValidateStepSelections(installationStep, selected);
        if (invalidGroups.Length > 0)
        {
            var group = installationStep.Groups.First(group => group.Id == invalidGroups[0]);
            throw new InvalidDataException($"The collection's choices do not satisfy FOMOD group '{group.Name}' in step '{installationStep.Name}'.");
        }

        _remainingSteps.RemoveAt(presetIndex);
        return Task.FromResult(new UserChoice(new UserChoice.GoToNextStep(selected)));
    }
}
