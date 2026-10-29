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

    // The tab's content leaves the visual tree when another tab is selected or the window hides to the tray,
    // so polling runs only while the connections are on screen.
    // The pane's own DataContext binding may not be resolved yet when it first becomes visible, so go through the window's.
    private void OnConnectionsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        (DataContext as MainViewModel)?.Connections.SetActive((bool)e.NewValue);

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
