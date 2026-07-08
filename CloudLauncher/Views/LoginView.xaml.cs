using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class LoginView : Page
{
    private readonly MainWindow _shell;
    private bool _registering;

    public LoginView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        UpdateModeUi();
    }

    private void UpdateModeUi()
    {
        InfoLabel.Visibility = Visibility.Collapsed;
        ResendButton.Visibility = Visibility.Collapsed;

        ModeLabel.Text = _registering ? "Create account" : "Sign in";
        SubmitButton.Content = _registering ? "Register" : "Sign in";
        SwitchButton.Content = _registering ? "Have an account?" : "Need an account? Create one";
        EmailLabel.Visibility = _registering ? Visibility.Visible : Visibility.Collapsed;
        EmailBox.Visibility = _registering ? Visibility.Visible : Visibility.Collapsed;
        EmailLabel.Content = _registering ? "Email" : "Email (optional)";
    }

    private void OnSwitchMode(object sender, RoutedEventArgs e)
    {
        _registering = !_registering;
        ErrorLabel.Text = "";
        UpdateModeUi();
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Text = "";
        InfoLabel.Visibility = Visibility.Collapsed;
        SubmitButton.IsEnabled = false;
        GoogleButton.IsEnabled = false;
        try
        {
            var username = UsernameBox.Text.Trim();
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                ErrorLabel.Text = "Username and password are required.";
                return;
            }

            if (_registering)
            {
                var email = EmailBox.Text.Trim();
                if (string.IsNullOrEmpty(email) || !email.Contains('@'))
                {
                    ErrorLabel.Text = "A valid email address is required.";
                    return;
                }

                var pending = await App.State.Api.RegisterAsync(new RegisterRequest(username, password, email));
                _registering = false;
                UpdateModeUi();
                InfoLabel.Text = pending.Message;
                InfoLabel.Visibility = Visibility.Visible;
                ResendButton.Visibility = Visibility.Visible;
                EmailBox.Text = email;
                EmailLabel.Visibility = Visibility.Visible;
                EmailBox.Visibility = Visibility.Visible;
                return;
            }

            var tokens = await App.State.Api.LoginAsync(new LoginRequest(username, password));
            App.State.Api.SetTokens(tokens);
            _shell.NavigateToPacks();
        }
        catch (ApiException ex)
        {
            ErrorLabel.Text = ExtractErrorMessage(ex);
            if (ErrorLabel.Text.Contains("verify your email", StringComparison.OrdinalIgnoreCase))
                ResendButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Could not reach the server: " + ex.Message;
        }
        finally
        {
            SubmitButton.IsEnabled = true;
            GoogleButton.IsEnabled = true;
        }
    }

    private async void OnResendVerification(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Text = "";
        var email = EmailBox.Text.Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            ErrorLabel.Text = "Enter the email you used when registering.";
            return;
        }

        ResendButton.IsEnabled = false;
        try
        {
            await App.State.Api.ResendVerificationAsync(new ResendVerificationRequest(email));
            InfoLabel.Text = "If that account is unverified, a new confirmation email was sent.";
            InfoLabel.Visibility = Visibility.Visible;
        }
        catch (ApiException ex)
        {
            ErrorLabel.Text = ExtractErrorMessage(ex);
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Could not reach the server: " + ex.Message;
        }
        finally
        {
            ResendButton.IsEnabled = true;
        }
    }

    private async void OnGoogleSignIn(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Text = "";
        InfoLabel.Visibility = Visibility.Collapsed;
        SubmitButton.IsEnabled = false;
        GoogleButton.IsEnabled = false;
        GoogleButton.Content = "Waiting for Google…";
        try
        {
            var start = await App.State.Api.GoogleAuthStartAsync();
            Process.Start(new ProcessStartInfo(start.AuthUrl) { UseShellExecute = true });

            const int maxAttempts = 120;
            for (var i = 0; i < maxAttempts; i++)
            {
                await Task.Delay(1000);
                var poll = await App.State.Api.GoogleAuthPollAsync(start.State);
                if (!poll.Complete)
                    continue;

                if (poll.Tokens is not null)
                {
                    App.State.Api.SetTokens(poll.Tokens);
                    _shell.NavigateToPacks();
                    return;
                }

                ErrorLabel.Text = poll.Error ?? "Google sign-in failed.";
                return;
            }

            ErrorLabel.Text = "Google sign-in timed out. Try again.";
        }
        catch (ApiException ex)
        {
            ErrorLabel.Text = ExtractErrorMessage(ex);
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Could not reach the server: " + ex.Message;
        }
        finally
        {
            GoogleButton.Content = "Continue with Google";
            SubmitButton.IsEnabled = true;
            GoogleButton.IsEnabled = true;
        }
    }

    private static string ExtractErrorMessage(ApiException ex)
    {
        var body = ex.Message;
        const string marker = "\"error\":\"";
        var idx = body.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return body;
        var start = idx + marker.Length;
        var end = body.IndexOf('"', start);
        if (end <= start) return body;
        return body[start..end].Replace("\\u0026", "&");
    }
}
