using System.Windows;

namespace CloudLauncher.Views;

public partial class SimpleInputDialog : Window
{
    public string? Result { get; private set; }

    public SimpleInputDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        TitleLabel.Text = title;
        PromptLabel.Content = prompt;
        InputBox.Text = initialValue ?? "";
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
