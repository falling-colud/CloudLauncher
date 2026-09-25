using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>The in-window "delete account" card, shown via <c>MainWindow.ShowCardAsync</c>.</summary>
/// <remarks>
/// <para>Delete stays disabled until DELETE is typed, which the server also checks.</para>
/// <para>The password is required if this PC has seen the account use one; a Google account may have
/// none, and the server says when one is needed. It is read once when Delete is pressed and never
/// stored (WPF empties a PasswordBox on navigation anyway).</para>
/// <para><see cref="Result"/> completes with true only when the server deleted the account, by which
/// point this PC is signed out.</para>
/// </remarks>
public partial class AccountDeleteCard : UserControl
{
    private const string ConfirmWord = "DELETE";

    private readonly TaskCompletionSource<bool> _tcs = new();
    private readonly bool _passwordRequired;
    private bool _busy;

    public AccountDeleteCard(bool passwordRequired)
    {
        InitializeComponent();
        _passwordRequired = passwordRequired;
        PasswordLabel.Text = passwordRequired
            ? "Password"
            : "Password (leave it empty if you only sign in with Google)";
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            PasswordInput.Focus();
        };
    }

    public Task<bool> Result => _tcs.Task;

    /// <summary>Dismisses the card from outside (the backdrop click).</summary>
    public void Cancel()
    {
        if (!_busy) _tcs.TrySetResult(false);
    }

    private bool Confirmed => string.Equals(ConfirmBox.Text.Trim(), ConfirmWord, StringComparison.Ordinal);

    private void OnConfirmChanged(object sender, TextChangedEventArgs e)
    {
        HideError();
        DeleteButton.IsEnabled = Confirmed && !_busy;
    }

    private void HideError() => ErrorLabel.Visibility = Visibility.Collapsed;

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try { await SubmitAsync(); }
        catch (SessionExpiredException)
        {
            // The shell has already gone back to the login screen; there is nothing left to delete from here.
            _tcs.TrySetResult(false);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task SubmitAsync()
    {
        if (!Confirmed) return;
        var password = PasswordInput.Password;
        if (_passwordRequired && password.Length == 0)
        {
            ShowError("Enter your password.");
            PasswordInput.Focus();
            return;
        }

        SetBusy(true);
        HideError();
        try
        {
            await App.State.Api.DeleteAccountAsync(
                new DeleteAccountRequest(password.Length == 0 ? null : password, ConfirmWord));
            _tcs.TrySetResult(true);
        }
        catch (ApiException ex)
        {
            ShowError(Describe(ex));
            PasswordInput.Focus();
        }
    }

    /// <summary>The server's own sentence when it sent one, otherwise a plain one for the status.</summary>
    private static string Describe(ApiException ex)
    {
        var sentence = ApiClient.ServerSentence(ex);
        return ex.Status switch
        {
            HttpStatusCode.NotFound => sentence ?? "This server cannot delete accounts yet.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                sentence ?? "The server did not accept that password.",
            _ => sentence ?? ex.Message
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DeleteButton.IsEnabled = !busy && Confirmed;
        DeleteButton.Content = busy ? "Deleting..." : "Delete account";
        CancelButton.IsEnabled = !busy;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            if (DeleteButton.IsEnabled) OnSubmit(this, new RoutedEventArgs());
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
