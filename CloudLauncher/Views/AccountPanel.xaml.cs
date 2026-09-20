using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The CloudLauncher account page: who you are signed in as, how much server storage you are using,
/// and the three things you can do to the account itself — change the password, sign out here, or
/// sign out everywhere.
/// </summary>
/// <remarks>
/// Everything the server tells us is optional. The page renders from the cached settings first and
/// then fills in whatever the profile and usage calls return, so an offline launcher (or an older
/// server without <c>auth/me/usage</c>) still shows a usable Account page instead of an error.
/// </remarks>
public partial class AccountPanel : Page
{
    private readonly MainWindow _shell;

    /// <summary>Cancels an in-flight profile/usage load when the page goes away.</summary>
    private readonly CancellationTokenSource _cts = new();

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
        UsernameLabel.Text = s.Username ?? "Signed out";
        ProfileInitial.Text = !string.IsNullOrEmpty(s.Username) ? char.ToUpper(s.Username[0]).ToString() : "?";
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
            // Two independent calls: a server too old for the usage route must not cost us the
            // profile, and vice versa.
            try
            {
                var me = await App.State.Api.MeAsync(_cts.Token);
                UsernameLabel.Text = me.Username;
                UserIdLabel.Text = me.Id.ToString();
                CopyIdButton.Visibility = Visibility.Visible;
                ApplyVerifiedState(me.EmailConfirmed);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Offline or refused — the cached identity from settings is already on screen.
                VerifiedPill.Visibility = Visibility.Collapsed;
                ResendVerificationButton.Visibility = Visibility.Collapsed;
            }

            try { ApplyUsage(await App.State.Api.GetMyStorageUsageAsync(_cts.Token)); }
            catch (OperationCanceledException) { return; }
            catch { StorageSection.Visibility = Visibility.Collapsed; }
        }
        finally { if (!_cts.IsCancellationRequested) RefreshButton.IsEnabled = true; }
    }

    private void ApplyVerifiedState(bool confirmed)
    {
        VerifiedPill.Visibility = Visibility.Visible;
        VerifiedPillText.Text = confirmed ? "email verified" : "email unverified";
        VerifiedPill.ToolTip = confirmed
            ? "Your email address has been confirmed."
            : "Until the address is confirmed you cannot recover this account by email.";
        ResendVerificationButton.Visibility = confirmed ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Paints the storage meter. An account with no quota gets the figure without a bar,
    /// because a progress bar with no maximum is a bar that always looks empty.</summary>
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
            ? "You are out of space — uploads will be refused until you delete a version or a shared instance."
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
            // The profile does not carry the address, so ask for it. Typing it again is also the
            // fix when the original sign-up address was the thing that was wrong.
            var email = await _shell.PromptAsync("Resend verification email",
                "Which address did you register with?");
            if (string.IsNullOrWhiteSpace(email)) return;

            ResendVerificationButton.IsEnabled = false;
            await App.State.Api.ResendVerificationAsync(new ResendVerificationRequest(email.Trim()));
            Okay($"Verification email sent to {email.Trim()}.");
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
            if (await card.Result) Okay("Password changed. Other devices have been signed out.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>
    /// Ends this session properly: the server revokes the refresh token, so a machine you signed out
    /// of can no longer refresh itself back in. Local tokens are cleared either way — the user asked
    /// to be signed out of this PC and that part does not depend on the network.
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
                // The account may be left signed in elsewhere, so say so rather than pretending.
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
