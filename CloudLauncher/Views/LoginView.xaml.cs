using System.Net;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class LoginView : Page
{
    private readonly MainWindow _shell;
    private bool _registering;

    // True when this PC has signed in before. The tokens may have expired, but the library on disk
    // belongs to a known user, so it can still be opened.
    private readonly bool _knownMachine;

    public LoginView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _knownMachine = !string.IsNullOrWhiteSpace(App.State.Settings.Username)
                        || App.State.Settings.UserId is not null;
        OfflineButton.Visibility = _knownMachine ? Visibility.Visible : Visibility.Collapsed;
        UpdateModeUi();
    }

    /// <summary>
    /// Opens the library straight from disk and cache.
    /// </summary>
    /// <remarks>
    /// For users who signed out or whose refresh token expired. They still own every instance in the
    /// folder, and this screen can't sign them in while offline.
    /// </remarks>
    private void OnContinueOffline(object sender, RoutedEventArgs e)
    {
        AppLog.Log("auth", "Continuing offline - signed out, opening the local library.");
        _shell.NavigateToPacks();
    }

    /// <summary>
    /// Says the server could not be reached, naming the cause and what to do instead of showing the
    /// raw exception text.
    /// </summary>
    private void ShowOfflineError(string? reason)
    {
        ErrorLabel.Text = reason is { Length: > 0 }
            ? $"CloudLauncher could not reach the server: {reason}."
            : "CloudLauncher could not reach the server.";
        InfoLabel.Text = _knownMachine
            ? "Signing in is the one thing that needs the server. Everything already on this PC still opens - use Continue offline below."
            : "Signing in needs the server. Try again once you are back online.";
        InfoLabel.Visibility = Visibility.Visible;
    }

    /// <summary>The offline reason behind <paramref name="ex"/>, or null if the server did answer.</summary>
    /// <remarks>
    /// Auth endpoints don't go through <c>EnsureTokenAsync</c>, so a dead network arrives here as a raw
    /// <see cref="System.Net.Http.HttpRequestException"/> instead of an <see cref="OfflineException"/>.
    /// </remarks>
    private static string? OfflineReasonFor(Exception ex) => ex switch
    {
        OfflineException off => off.Reason ?? App.State.OfflineReason,
        _ => Connectivity.DescribeTransportFailure(ex, CancellationToken.None)
    };

    private void UpdateModeUi()
    {
        InfoLabel.Visibility = Visibility.Collapsed;
        ResendButton.Visibility = Visibility.Collapsed;

        ModeLabel.Text = _registering ? "Create account" : "Sign in";
        SubmitButton.Content = _registering ? "Create account" : "Sign in";
        SwitchButton.Content = _registering ? "Have an account?" : "Need an account? Create one";
        EmailLabel.Visibility = _registering ? Visibility.Visible : Visibility.Collapsed;
        EmailBox.Visibility = _registering ? Visibility.Visible : Visibility.Collapsed;
        EmailLabel.Content = _registering ? "Email" : "Email (optional)";
        TermsBox.Visibility = _registering ? Visibility.Visible : Visibility.Collapsed;
        UpdateSubmitEnabled();
    }

    /// <summary>Sign in is always available; Create account (and Google, which can also create an
    /// account) only once the terms are accepted.</summary>
    private void UpdateSubmitEnabled()
    {
        var allowed = !_registering || TermsBox.IsChecked == true;
        SubmitButton.IsEnabled = allowed;
        GoogleButton.IsEnabled = allowed;
    }

    private void OnSwitchMode(object sender, RoutedEventArgs e)
    {
        _registering = !_registering;
        ErrorLabel.Text = "";
        UpdateModeUi();
    }

    private void OnTermsToggled(object sender, RoutedEventArgs e) => UpdateSubmitEnabled();

    private void OnOpenTerms(object sender, RoutedEventArgs e) => OpenLegalPage(Legal.TermsPath);

    private void OnOpenPrivacy(object sender, RoutedEventArgs e) => OpenLegalPage(Legal.PrivacyPath);

    private void OpenLegalPage(string path)
    {
        if (!SafeLaunch.OpenUrl(App.State.Api.LegalPageUrl(path)))
            ErrorLabel.Text = "The page could not be opened in your browser.";
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
                if (TermsBox.IsChecked != true)
                {
                    ErrorLabel.Text = "Tick the box to agree to the Terms and the Privacy Policy.";
                    return;
                }

                try
                {
                    await App.State.Api.RegisterAsync(new RegisterRequest(username, password, email,
                        AcceptTerms: true, TermsVersion: Legal.TermsVersion));
                }
                catch (ApiException ex) when (ex.Status == HttpStatusCode.Forbidden)
                {
                    ErrorLabel.Text = "Sign-ups are closed at the moment. Try again later.";
                    return;
                }
                // Email verification is off, so the new account works right away: sign in now instead of
                // showing a "check your email" screen.
                var newTokens = await App.State.Api.LoginAsync(new LoginRequest(username, password));
                SignedInWithPassword(newTokens);
                return;
            }

            var tokens = await App.State.Api.LoginAsync(new LoginRequest(username, password));
            SignedInWithPassword(tokens);
        }
        catch (ApiException ex)
        {
            ErrorLabel.Text = ExtractErrorMessage(ex);
        }
        catch (Exception ex) when (OfflineReasonFor(ex) is { } reason)
        {
            ShowOfflineError(reason);
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Could not reach the server: " + ex.Message;
        }
        finally
        {
            UpdateSubmitEnabled();
        }
    }

    /// <summary>Stores the session and remembers that this account has a password, which is what the
    /// Delete account card asks for.</summary>
    private void SignedInWithPassword(TokenResponse tokens)
    {
        App.State.Settings.PasswordAccountId = tokens.UserId;
        App.State.Api.SetTokens(tokens);
        _shell.NavigateToPacks();
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
        catch (Exception ex) when (OfflineReasonFor(ex) is { } reason)
        {
            ShowOfflineError(reason);
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

        // Don't open a browser tab for a handshake that can't start. The flow polls for two minutes
        // before it would notice.
        if (App.State.IsOffline)
        {
            ShowOfflineError(App.State.OfflineReason);
            return;
        }

        SubmitButton.IsEnabled = false;
        GoogleButton.IsEnabled = false;
        GoogleButton.Content = "Waiting for Google...";
        try
        {
            // A first Google sign-in creates the account, and the line under the button says that
            // continuing accepts the terms.
            var start = await App.State.Api.GoogleAuthStartAsync(acceptTerms: true);
            if (!SafeLaunch.OpenUrl(start.AuthUrl))
            {
                ErrorLabel.Text = "The Google sign-in page could not be opened in your browser.";
                return;
            }

            const int maxAttempts = 120;
            for (var i = 0; i < maxAttempts; i++)
            {
                await Task.Delay(1000);

                // The connection can drop mid-handshake. Stop as soon as the launcher knows it is offline
                // instead of failing once a second until the two minutes are up.
                if (App.State.IsOffline)
                {
                    ShowOfflineError(App.State.OfflineReason);
                    return;
                }

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
        catch (Exception ex) when (OfflineReasonFor(ex) is { } reason)
        {
            ShowOfflineError(reason);
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Could not reach the server: " + ex.Message;
        }
        finally
        {
            GoogleButton.Content = "Continue with Google";
            UpdateSubmitEnabled();
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
