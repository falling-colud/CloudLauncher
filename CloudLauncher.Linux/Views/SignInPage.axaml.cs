using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>Sign in to a CloudLauncher account, or make one; or go on with this computer's instances
/// only. The Windows launcher's LoginView, without Google (the server offers it to the Windows
/// launcher's browser window only).</summary>
public partial class SignInPage : UserControl, IShellPage
{
    private bool _register;
    private bool _busy;

    public SignInPage()
    {
        InitializeComponent();
        UsernameBox.Text = App.State.Settings.Username ?? "";
    }

    public ShellNav Nav => ShellNav.Account;

    private void OnToggleMode(object? sender, RoutedEventArgs e)
    {
        _register = !_register;
        TitleText.Text = _register ? "Create Account" : "Sign In";
        SubmitButton.Content = _register ? "Create Account" : "Sign In";
        ModeButton.Content = _register ? "Have an account? Sign in" : "New here? Create an account";
        EmailRow.IsVisible = _register;
        TermsRow.IsVisible = _register;
        ErrorText.IsVisible = false;
    }

    private async void OnSubmit(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var username = UsernameBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        var email = EmailBox.Text?.Trim() ?? "";
        if (username.Length == 0 || password.Length == 0) { Fail("Enter your username and password."); return; }
        if (_register && email.Length == 0) { Fail("Enter an email address."); return; }
        if (_register && TermsBox.IsChecked != true) { Fail("Agree to the Terms and the Privacy Policy to make an account."); return; }

        _busy = true;
        SubmitButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        InfoText.IsVisible = false;
        try
        {
            if (_register)
            {
                try
                {
                    await App.State.Api.RegisterAsync(new RegisterRequest(username, password, email,
                        AcceptTerms: true, TermsVersion: Legal.TermsVersion));
                }
                catch (ApiException ex) when (ex.Status == HttpStatusCode.Forbidden)
                {
                    Fail("Sign-ups are closed at the moment. Try again later.");
                    return;
                }
            }
            var tokens = await App.State.Api.LoginAsync(new LoginRequest(username, password));
            App.State.Settings.PasswordAccountId = tokens.UserId;
            App.State.Api.SetTokens(tokens);
            AppLog.Log("auth", $"Signed in as {tokens.Username}.");
            MainWindow.Current.OnSignedIn();
        }
        catch (ApiException ex)
        {
            Fail(ApiClient.ServerSentence(ex) ?? ex.Message);
        }
        catch (Exception ex) when (App.State.IsOffline)
        {
            Fail("CloudLauncher could not reach its server" + (App.State.OfflineReason is { } why ? $": {why}." : "."));
            InfoText.Text = "Signing in is the one thing that needs the server. Your instances on this computer still open: continue without signing in.";
            InfoText.IsVisible = true;
            AppLog.LogError("auth", ex);
        }
        catch (Exception ex)
        {
            Fail("Could not reach the server: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SubmitButton.IsEnabled = true;
        }
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void OnContinueOffline(object? sender, RoutedEventArgs e)
    {
        AppLog.Log("auth", "Continuing without signing in.");
        MainWindow.Current.NavigateToInstances();
    }

    private void OnOpenTerms(object? sender, RoutedEventArgs e) => SafeLaunch.OpenUrl(App.State.Api.LegalPageUrl(Legal.TermsPath));
    private void OnOpenPrivacy(object? sender, RoutedEventArgs e) => SafeLaunch.OpenUrl(App.State.Api.LegalPageUrl(Legal.PrivacyPath));
}
