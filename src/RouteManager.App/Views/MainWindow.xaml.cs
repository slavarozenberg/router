using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RouteManager.App.ViewModels;

namespace RouteManager.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnRulesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row edits; ignore headers, scrollbars and the on/off switch.
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is CheckBox)
                return;
            if (element is DataGridRow)
            {
                if (DataContext is MainViewModel vm && vm.EditRuleCommand.CanExecute(null))
                    vm.EditRuleCommand.Execute(null);
                return;
            }
        }
    }
}
