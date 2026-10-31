using NexusMods.App.UI.Controls.MarkdownRenderer;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Icons;
using ReactiveUI.Fody.Helpers;
using ReactiveUI;
using ReactiveCommand = R3.ReactiveCommand;

namespace NexusMods.App.UI.Dialog;

public class DialogStandardContentViewModel : AViewModel<IDialogStandardContentViewModel>, IDialogStandardContentViewModel
{
    public string Text { get; }
    public string Heading { get; }
    public IconValue? Icon { get; }
    public IMarkdownRendererViewModel? MarkdownRenderer { get; }
    public bool ShowMarkdownCopyButton { get; }
    private readonly Func<string?, string?>? _inputValidation;
    private string _inputText = string.Empty;
    public string InputText
    {
        get => _inputText;
        set
        {
            this.RaiseAndSetIfChanged(ref _inputText, value ?? string.Empty);
            this.RaisePropertyChanged(nameof(InputValidationError));
            this.RaisePropertyChanged(nameof(IsInputValid));
        }
    }
    public string InputValidationError => _inputValidation?.Invoke(InputText) ?? string.Empty;
    public bool IsInputValid => string.IsNullOrEmpty(InputValidationError);
    public string InputLabel { get; set; }
    public string InputWatermark { get; set; }
    public string BottomText { get; }
    public ReactiveCommand ClearInputCommand { get; set; }

    public DialogStandardContentViewModel(StandardDialogParameters standardDialogParameters)
    {
        Text = standardDialogParameters.Text;
        Heading = standardDialogParameters.Heading;
        Icon = standardDialogParameters.Icon;
        MarkdownRenderer = standardDialogParameters.Markdown;
        InputLabel = standardDialogParameters.InputLabel;
        _inputValidation = standardDialogParameters.InputValidation;
        InputText = standardDialogParameters.InputText;
        InputWatermark = standardDialogParameters.InputWatermark;
        BottomText = standardDialogParameters.BottomText;
        ShowMarkdownCopyButton = standardDialogParameters.ShowMarkdownCopyButton;

        ClearInputCommand = new R3.ReactiveCommand(
            executeAsync: (_, cancellationToken) =>
            {
                InputText = string.Empty;
                return ValueTask.CompletedTask;
            }
        );
    }
}
