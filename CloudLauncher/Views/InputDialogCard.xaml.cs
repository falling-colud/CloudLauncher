using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;

namespace CloudLauncher.Views;

/// <summary>An in-window text prompt card (shown via <c>MainWindow.PromptAsync</c>). The result
/// task yields the entered text, or null if cancelled.</summary>
public partial class InputDialogCard : UserControl
{
    private readonly TaskCompletionSource<string?> _tcs = new();

    public InputDialogCard(string title, string label, string initial)
    {
        InitializeComponent();
        TitleText.Text = title;
        LabelText.Text = label;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public Task<string?> Result => _tcs.Task;

    public void Cancel() => _tcs.TrySetResult(null);

    private void OnOk(object sender, RoutedEventArgs e) => _tcs.TrySetResult(InputBox.Text);
    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { _tcs.TrySetResult(InputBox.Text); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
