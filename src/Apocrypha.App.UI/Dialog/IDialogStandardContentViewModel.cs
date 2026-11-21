using Apocrypha.App.UI.Controls.MarkdownRenderer;
using Apocrypha.UI.Sdk;
using Apocrypha.UI.Sdk.Icons;

namespace Apocrypha.App.UI.Dialog;

public interface IDialogStandardContentViewModel : IViewModelInterface
{
    AffectedFile[]? AffectedFiles => null;
    string FileCount => $"{AffectedFiles?.Length ?? 0} affected files";
    string ExportFileList() => AffectedFile.Export(AffectedFiles ?? []);
    string Text { get; }
    string Heading { get; }
    IconValue? Icon { get; }
    IMarkdownRendererViewModel? MarkdownRenderer { get; }
    bool ShowMarkdownCopyButton { get; }
    string InputText { get; set; }
    string InputLabel { get; set; }
    string InputWatermark { get; set; }
    string BottomText { get; }
    public R3.ReactiveCommand ClearInputCommand { get; set; }
}
