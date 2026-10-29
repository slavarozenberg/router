using System.Windows;

namespace RouteManager.App.Views;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string initial)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string Value => InputBox.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
