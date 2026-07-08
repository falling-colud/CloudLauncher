using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using CloudLauncher.Animations;

namespace CloudLauncher.Views;

/// <summary>A themed, in-window modal dialog (backdrop + card) — the replacement for OS message
/// boxes. Configured for a confirm (two buttons) or a message (one button), awaited via
/// <see cref="Result"/>.</summary>
public partial class DialogOverlay : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public DialogOverlay()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += OnLoaded;
    }

    public Task<bool> Result => _tcs.Task;

    public void Configure(string title, string message, string confirmText, string? cancelText, bool danger)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Style = (Style)FindResource(danger ? "DangerButton" : "AccentButton");
        if (cancelText is null) CancelButton.Visibility = Visibility.Collapsed;
        else CancelButton.Content = cancelText;
    }

    private bool HasCancel => CancelButton.Visibility == Visibility.Visible;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Animate.SlideFadeIn(Card, 0, 14, 200);
        Backdrop.Opacity = 0;
        Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        ConfirmButton.Focus();
        Keyboard.Focus(ConfirmButton);
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => _tcs.TrySetResult(true);
    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(false);

    private void OnBackdrop(object sender, MouseButtonEventArgs e)
    {
        // Click-outside cancels a confirm; for a one-button message it just dismisses it.
        _tcs.TrySetResult(!HasCancel);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HasCancel) { _tcs.TrySetResult(false); e.Handled = true; }
        else if (e.Key is Key.Enter) { _tcs.TrySetResult(true); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
