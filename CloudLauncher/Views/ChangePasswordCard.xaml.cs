using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The in-window "change password" card, shown through <c>MainWindow.ShowCardAsync</c>.
/// </summary>
/// <remarks>
/// <para>The card runs the request itself rather than handing three strings back to the caller. A
/// wrong current password is the common case, and the only useful place to say so is next to the box
/// that holds it — returning to the Account page and printing the refusal on a status line would mean
/// re-typing all three fields to try again.</para>
/// <para><see cref="Result"/> completes with true only when the server accepted the change; Cancel
/// and Escape complete it with false.</para>
/// </remarks>
public partial class ChangePasswordCard : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private bool _busy;

    /// <summary>The shortest new password worth sending. The server's own policy is stricter or
    /// equal; this only saves a round trip on an obviously empty attempt.</summary>
    private const int MinPasswordLength = 6;

    public ChangePasswordCard()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            CurrentBox.Focus();
        };
    }

    public Task<bool> Result => _tcs.Task;

    /// <summary>Dismisses the card from outside (the backdrop click).</summary>
    public void Cancel()
    {
        if (!_busy) _tcs.TrySetResult(false);
    }

    /// <summary>Clears a stale mismatch warning as soon as the user starts fixing it.</summary>
    private void OnTyping(object sender, RoutedEventArgs e) => HideError();

    private void HideError() => ErrorLabel.Visibility = Visibility.Collapsed;

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        try { await SubmitAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task SubmitAsync()
    {
        if (_busy) return;
        var current = CurrentBox.Password;
        var wanted = NewBox.Password;

        if (string.IsNullOrEmpty(current)) { ShowError("Enter your current password."); CurrentBox.Focus(); return; }
        if (wanted.Length < MinPasswordLength)
        {
            ShowError($"The new password needs at least {MinPasswordLength} characters.");
            NewBox.Focus();
            return;
        }
        if (wanted != ConfirmBox.Password) { ShowError("The two new passwords don't match."); ConfirmBox.Focus(); return; }
        if (wanted == current) { ShowError("That is already your password."); NewBox.Focus(); return; }

        SetBusy(true);
        HideError();
        try
        {
            // The call swaps in the fresh token pair the server issues, so this launcher stays
            // signed in while every other device is cut off.
            await App.State.Api.ChangePasswordAsync(new ChangePasswordRequest(current, wanted));
            _tcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            CurrentBox.SelectAll();
            CurrentBox.Focus();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SubmitButton.IsEnabled = !busy;
        SubmitButton.Content = busy ? "Changing…" : "Change password";
        CancelButton.IsEnabled = !busy;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter) { OnSubmit(this, new RoutedEventArgs()); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
