using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Microsoft.Win32;

namespace CloudLauncher.Views;

/// <summary>
/// The CloudLauncher account page: who you are signed in as, server storage used, and account
/// actions (rename, change password, sign out here or everywhere, download your data, delete).
/// </summary>
/// <remarks>
/// Renders from cached settings first, then fills in whatever the profile and usage calls return, so
/// it still works offline or against an older server without <c>auth/me/usage</c>.
/// </remarks>
public partial class AccountPanel : Page
{
    private readonly MainWindow _shell;

    /// <summary>Cancels an in-flight profile/usage load when the page goes away.</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The address <c>auth/me</c> reported, or null when it didn't send one.</summary>
    /// <remarks>Used by the resend action. Older servers don't send it, and resend then asks for the
    /// address.</remarks>
    private string? _email;

    public AccountPanel(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        Loaded += (_, _) =>
        {
            RefreshLocal();
            _ = RefreshFromServerAsync();
        };
        Unloaded += (_, _) => _cts.Cancel();
    }

    // ── status ───────────────────────────────────────────────────────────────

    private void Okay(string message)
    {
        StatusLabel.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        StatusLabel.Text = message;
    }

    private void Fail(string message)
    {
        StatusLabel.SetResourceReference(ForegroundProperty, "DangerBrush");
        StatusLabel.Text = message;
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>The part of the page that needs nothing from the network.</summary>
    private void RefreshLocal()
    {
        var s = App.State.Settings;
        ApplyUsername(s.Username);
        ServerLabel.Text = App.State.Api.ServerUrl;
        UserIdLabel.Text = s.UserId?.ToString() ?? "";
        CopyIdButton.Visibility = s.UserId is null ? Visibility.Collapsed : Visibility.Visible;

        var acc = App.State.MinecraftAccounts.Current;
        McAccountLine.Text = acc is null ? "Not signed in" : acc.Username;
        McAccountKind.Text = acc is null ? "Click Manage to sign in" : $"{acc.Kind} account";
    }

    /// <summary>Fills in the parts only the server knows: the verified state and the storage meter.</summary>
    private async Task RefreshFromServerAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            // Two independent calls, so an older server without the usage route still gives us the
            // profile, and vice versa.
            try
            {
                // Also refreshes the cached name and admin flag, so the sidebar follows a rename made
                // on another device.
                var me = await App.State.Api.RefreshAccountAsync(_cts.Token);
                ApplyUsername(me.Username);
                UserIdLabel.Text = me.Id.ToString();
                CopyIdButton.Visibility = Visibility.Visible;
                ApplyEmail(me.Email);
                ApplyVerifiedState(me.EmailConfirmed);
                _shell.UpdateChrome();
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Offline or refused; the cached identity from settings is already on screen.
                VerifiedPill.Visibility = Visibility.Collapsed;
                ResendVerificationButton.Visibility = Visibility.Collapsed;
            }

            try { ApplyUsage(await App.State.Api.GetMyStorageUsageAsync(_cts.Token)); }
            catch (OperationCanceledException) { return; }
            catch { StorageSection.Visibility = Visibility.Collapsed; }
        }
        finally { if (!_cts.IsCancellationRequested) RefreshButton.IsEnabled = true; }
    }

    private void ApplyUsername(string? username)
    {
        UsernameLabel.Text = string.IsNullOrEmpty(username) ? "Signed out" : username;
        ProfileInitial.Text = !string.IsNullOrEmpty(username) ? char.ToUpper(username[0]).ToString() : "?";
    }

    /// <summary>Shows the registered address beside the verified pill, so it's clear which address
    /// needs verifying.</summary>
    private void ApplyEmail(string? email)
    {
        _email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        EmailLabel.Text = _email ?? "";
        EmailLabel.Visibility = _email is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyVerifiedState(bool confirmed)
    {
        VerifiedPill.Visibility = Visibility.Visible;
        VerifiedPillText.Text = confirmed ? "email verified" : "email unverified";
        VerifiedPill.ToolTip = confirmed
            ? "Your email address has been confirmed."
            : "Until the address is confirmed you cannot recover this account by email.";
        ResendVerificationButton.Visibility = confirmed ? Visibility.Collapsed : Visibility.Visible;
        ResendVerificationButton.ToolTip = _email is null
            ? "Sends the confirmation link again to the address you registered with"
            : $"Sends the confirmation link again to {_email}";
    }

    /// <summary>Paints the storage meter. An account with no quota gets the figure without a bar,
    /// since a bar with no maximum would always look empty.</summary>
    private void ApplyUsage(UserStorageUsage usage)
    {
        StorageSection.Visibility = Visibility.Visible;
        if (usage.QuotaBytes is not { } quota || quota <= 0)
        {
            StorageLabel.Text = $"{FormatBytes(usage.UsedBytes)} used";
            StorageBar.Visibility = Visibility.Collapsed;
            StorageHint.Text = "This account has no storage limit.";
            return;
        }

        var fraction = Math.Clamp((double)usage.UsedBytes / quota, 0, 1);
        StorageBar.Visibility = Visibility.Visible;
        StorageBar.Value = fraction * 100;
        StorageLabel.Text = $"{FormatBytes(usage.UsedBytes)} of {FormatBytes(quota)} used";

        var remaining = Math.Max(0, quota - usage.UsedBytes);
        StorageHint.Text = remaining == 0
            ? "You are out of space - uploads will be refused until you delete a version or a shared instance."
            : $"{FormatBytes(remaining)} left. Uploads are refused once this is full.";
        StorageHint.SetResourceReference(ForegroundProperty,
            fraction >= 0.9 ? "DangerBrush" : "TextTertiaryBrush");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B"
    };

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshLocal();
            await RefreshFromServerAsync();
            Okay("Refreshed.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    // ── actions ──────────────────────────────────────────────────────────────

    private void OnManageMcAccount(object sender, RoutedEventArgs e)
    {
        _shell.OpenMcAccount();
        RefreshLocal();
    }

    private void OnCopyUserId(object sender, RoutedEventArgs e)
    {
        var id = UserIdLabel.Text;
        if (string.IsNullOrEmpty(id)) return;
        Okay(ClipboardHelper.TrySetText(id) ? "User ID copied." : "The clipboard is in use by another program.");
    }

    private async void OnResendVerification(object sender, RoutedEventArgs e)
    {
        try
        {
            // Usually the profile has the address, so this is one click. The prompt covers older
            // servers that don't send it, and the case where the sign-up address itself was wrong.
            var email = _email;
            if (email is null)
            {
                email = await _shell.PromptAsync("Resend verification email",
                    "Which address did you register with?");
                if (string.IsNullOrWhiteSpace(email)) return;
                email = email.Trim();
            }

            ResendVerificationButton.IsEnabled = false;
            await App.State.Api.ResendVerificationAsync(new ResendVerificationRequest(email));
            Okay($"Verification email sent to {email}.");
        }
        catch (Exception ex) { Fail("Could not send the email: " + ex.Message); }
        finally { ResendVerificationButton.IsEnabled = true; }
    }

    private async void OnChangePassword(object sender, RoutedEventArgs e)
    {
        try
        {
            var card = new ChangePasswordCard();
            await _shell.ShowCardAsync(card, card.Result, card.Cancel);
            if (!await card.Result) return;
            // The account certainly has a password now, which the Delete account card relies on.
            if (App.State.Settings.UserId is { } id && App.State.Settings.PasswordAccountId != id)
            {
                App.State.Settings.PasswordAccountId = id;
                App.State.Settings.Save();
            }
            Okay("Password changed. Other devices have been signed out.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async void OnChangeUsername(object sender, RoutedEventArgs e)
    {
        try
        {
            var card = new AccountUsernameCard(App.State.Settings.Username ?? "");
            await _shell.ShowCardAsync(card, card.Result, card.Cancel);
            if (await card.Result is not { } me) return;
            ApplyUsername(me.Username);
            _shell.UpdateChrome();
            Okay($"Your username is now {me.Username}. Use it the next time you sign in.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>Saves the server's export of the account to a file the user picks.</summary>
    private async void OnDownloadData(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = App.State.Settings.Username is { Length: > 0 } u ? u : "account";
            foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dialog = new SaveFileDialog
            {
                Title = "Save your CloudLauncher data",
                FileName = $"cloudlauncher-data-{name}-{date}.json",
                DefaultExt = ".json",
                Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(_shell) != true) return;

            DownloadDataButton.IsEnabled = false;
            Okay("Downloading your data...");
            await App.State.Api.ExportMyDataAsync(dialog.FileName, _cts.Token);
            Okay($"Your data was saved to {dialog.FileName}.");
        }
        catch (OperationCanceledException) { /* the page closed */ }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            Fail("This server cannot export account data yet.");
        }
        catch (ApiException ex) { Fail("Could not download your data: " + (ApiClient.ServerSentence(ex) ?? ex.Message)); }
        catch (Exception ex) { Fail("Could not download your data: " + ex.Message); }
        finally { DownloadDataButton.IsEnabled = true; }
    }

    /// <summary>
    /// Deletes the account on the server, then signs this PC out the way Sign out does and goes back
    /// to the login screen. The card itself asks for the password and the typed confirmation.
    /// </summary>
    private async void OnDeleteAccount(object sender, RoutedEventArgs e)
    {
        try
        {
            var card = new AccountDeleteCard(passwordRequired: App.State.Settings.AccountHasPassword);
            await _shell.ShowCardAsync(card, card.Result, card.Cancel);
            if (!await card.Result) return;

            // The card's call has already forgotten the tokens and the account's cached data.
            AppLog.Log("account", "Account deleted; signed out on this PC.");
            _shell.NavigateToLogin();
            await AppDialog.MessageAsync(_shell, "Account deleted",
                "Your CloudLauncher account has been deleted. Instances on this PC are still here.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>
    /// Signs out properly: the server revokes the refresh token, so this machine can't refresh back in.
    /// Local tokens are cleared either way, since that part doesn't depend on the network.
    /// </summary>
    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Sign out",
                    "Sign out of CloudLauncher on this device?\n\n"
                    + "Your instances stay on this PC; you will need to sign in again to sync or share them.",
                    "Sign out", "Cancel", danger: true))
                return;

            SignOutButton.IsEnabled = false;
            try { await App.State.Api.LogoutAsync(); }
            catch (Exception ex) { AppLog.LogError("SignOut", ex); }
            _shell.NavigateToLogin();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            SignOutButton.IsEnabled = true;
        }
    }

    private async void OnSignOutEverywhere(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Sign out everywhere",
                    "Sign out on every device, including this one?\n\n"
                    + "Every signed-in launcher, including any you no longer have, has to sign in again.",
                    "Sign out everywhere", "Cancel", danger: true))
                return;

            SignOutEverywhereButton.IsEnabled = false;
            try { await App.State.Api.LogoutAllAsync(); }
            catch (Exception ex)
            {
                // Other devices may still be signed in, so tell the user.
                AppLog.LogError("SignOutEverywhere", ex);
                await AppDialog.MessageAsync(_shell, "Other devices may still be signed in",
                    "The server could not be reached: " + ex.Message
                    + "\n\nThis device has been signed out. Try again once you are back online.");
                App.State.Api.ClearTokens();
            }
            _shell.NavigateToLogin();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            SignOutEverywhereButton.IsEnabled = true;
        }
    }
}
