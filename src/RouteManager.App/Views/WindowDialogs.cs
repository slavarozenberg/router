using System.Windows;
using Microsoft.Win32;
using RouteManager.App.ViewModels;

namespace RouteManager.App.Views;

public sealed class WindowDialogs(Window owner) : IDialogs
{
    private const string Caption = "Route Manager";

    public bool EditRule(RuleEditorViewModel editor) =>
        new RuleEditorWindow(editor) { Owner = owner }.ShowDialog() == true;

    public string? PromptText(string title, string label, string initial)
    {
        var prompt = new PromptWindow(title, label, initial) { Owner = owner };
        return prompt.ShowDialog() == true ? prompt.Value : null;
    }

    public bool Confirm(string message) =>
        MessageBox.Show(owner, message, Caption, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void Show(string message, bool isWarning = false) =>
        MessageBox.Show(owner, message, Caption, MessageBoxButton.OK, isWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    private const string JsonFilter = "Rules (*.json)|*.json|All files (*.*)|*.*";

    // Import also reads the app's own rules.json backups and Rouler's rules.json.
    public string? PickImportFile()
    {
        var dialog = new OpenFileDialog { Title = "Import rules", Filter = JsonFilter };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public string? PickExportFile(string suggestedName)
    {
        var dialog = new SaveFileDialog { Title = "Export rules", Filter = JsonFilter, FileName = suggestedName, DefaultExt = ".json" };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public ImportMode? AskImportMode(string message) =>
        MessageBox.Show(owner, message, "Import rules", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => ImportMode.Replace,
            MessageBoxResult.No => ImportMode.Add,
            _ => null,
        };
}
