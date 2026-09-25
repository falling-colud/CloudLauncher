using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The in-window "change username" card, shown through <c>MainWindow.ShowCardAsync</c>.
/// </summary>
/// <remarks>
/// Like <see cref="ChangePasswordCard"/>, the card makes the request itself, so a name that is taken
/// or not allowed is reported next to the box and can be fixed without starting over.
/// <see cref="Result"/> completes with the renamed account, or null when the card was cancelled.
/// </remarks>
public partial class AccountUsernameCard : UserControl
{
    private readonly TaskCompletionSource<UserSummary?> _tcs = new();
    private readonly string _current;
    private bool _busy;

    public AccountUsernameCard(string currentUsername)
    {
        InitializeComponent();
        _current = currentUsername;
        UsernameBox.Text = currentUsername;
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            UsernameBox.Focus();
            UsernameBox.SelectAll();
        };
    }

    public Task<UserSummary?> Result => _tcs.Task;

    /// <summary>Dismisses the card from outside (the backdrop click).</summary>
    public void Cancel()
    {
        if (!_busy) _tcs.TrySetResult(null);
    }

    private void OnTyping(object sender, TextChangedEventArgs e) => HideError();

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
            // The shell has already gone back to the login screen.
            _tcs.TrySetResult(null);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task SubmitAsync()
    {
        var wanted = UsernameBox.Text.Trim();
        if (wanted.Length == 0) { ShowError("Enter a username."); UsernameBox.Focus(); return; }
        if (string.Equals(wanted, _current, StringComparison.Ordinal))
        {
            ShowError("That is already your username.");
            UsernameBox.SelectAll();
            UsernameBox.Focus();
            return;
        }

        SetBusy(true);
        HideError();
        try
        {
            _tcs.TrySetResult(await App.State.Api.ChangeUsernameAsync(wanted));
        }
        catch (ApiException ex)
        {
            ShowError(Describe(ex));
            UsernameBox.SelectAll();
            UsernameBox.Focus();
        }
    }

    /// <summary>The server's own sentence when it sent one, otherwise a plain one for the status.</summary>
    private static string Describe(ApiException ex)
    {
        var sentence = ApiClient.ServerSentence(ex);
        return ex.Status switch
        {
            HttpStatusCode.Conflict => sentence ?? "That username is already taken.",
            HttpStatusCode.BadRequest => sentence ?? "That username is not allowed.",
            HttpStatusCode.NotFound => "This server cannot change usernames yet.",
            _ => sentence ?? ex.Message
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SubmitButton.IsEnabled = !busy;
        SubmitButton.Content = busy ? "Changing..." : "Change username";
        CancelButton.IsEnabled = !busy;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter) { OnSubmit(this, new RoutedEventArgs()); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
