using System.IO;
using System.Windows;
using Microsoft.Win32;
using RouteManager.App.ViewModels;
using RouteManager.Core.Storage;

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

    public string? PickRoulerRulesFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Rouler rules",
            Filter = "Rouler rules (rules.json)|rules.json|JSON files (*.json)|*.json",
            FileName = "rules.json",
        };
        if (Directory.Exists(RoulerImporter.DefaultDirectory))
            dialog.InitialDirectory = RoulerImporter.DefaultDirectory;
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
