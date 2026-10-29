using System.Windows;
using RouteManager.App.ViewModels;

namespace RouteManager.App.Views;

public partial class RuleEditorWindow : Window
{
    public RuleEditorWindow(RuleEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (((RuleEditorViewModel)DataContext).HasErrors)
        {
            MessageBox.Show(this, ((RuleEditorViewModel)DataContext).ValidationText, "Fix the rule first",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
