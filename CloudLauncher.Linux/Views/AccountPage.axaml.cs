using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The CloudLauncher account (who is signed in, Sign Out; Sign In when nobody is) and the saved
/// Minecraft accounts: the one the game plays as, switching, adding Microsoft (the device code, which
/// <see cref="DeviceCodeSignIn"/> puts on screen by itself) and offline accounts, removing. The Windows
/// launcher's AccountPanel and MinecraftAccountPanel on one page.
/// </summary>
/// <remarks>Paints from what this computer remembers first, then fills in what <c>auth/me</c> says, so it
/// works offline too.</remarks>
public partial class AccountPage : UserControl, IShellPage
{
    private CancellationTokenSource _cts = new();
    private bool _busy;

    public AccountPage()
    {
        InitializeComponent();
        SettingsLook.UseSlateDisabledLook(this);
        App.State.MinecraftAccounts.AccountsChanged += OnAccountsChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            App.State.MinecraftAccounts.AccountsChanged -= OnAccountsChanged;
            _cts.Cancel();
        };
        AttachedToVisualTree += (_, _) =>
        {
            // Back on screen after being away (the subscription went with it).
            App.State.MinecraftAccounts.AccountsChanged -= OnAccountsChanged;
            App.State.MinecraftAccounts.AccountsChanged += OnAccountsChanged;
        };
    }

    public ShellNav Nav => ShellNav.Account;

    public void OnShown()
    {
        if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
        ShowAccount();
        ShowMinecraftAccounts();
        if (App.State.Api.IsSignedIn) _ = RefreshFromServerAsync(_cts.Token);
    }

    // ── the CloudLauncher account ───────────────────────────────────────────

    private void ShowAccount()
    {
        var signedIn = App.State.Api.IsSignedIn;
        SignedInCard.IsVisible = signedIn;
        SignedOutCard.IsVisible = !signedIn;
        AccountStatus.IsVisible = false;
        // One accent button per screen: Sign In while signed out, Add Microsoft Account otherwise.
        if (signedIn) AddMicrosoftButton.Classes.Add("primary");
        else AddMicrosoftButton.Classes.Remove("primary");
        if (!signedIn) return;

        ShowUsername(App.State.Settings.Username);
        ServerText.Text = ServerName(App.State.Api.ServerUrl);
    }

    private void ShowUsername(string? username)
    {
        UsernameText.Text = string.IsNullOrEmpty(username) ? "Signed In" : username;
        InitialText.Text = string.IsNullOrEmpty(username) ? "?" : char.ToUpperInvariant(username[0]).ToString();
    }

    /// <summary>The server as a short name ("api.cloudlauncher.co"), with the port when it has one.</summary>
    private static string ServerName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}") : url;

    /// <summary>The name, address and verified state as the server has them. Offline or refused, the
    /// remembered name stays on screen.</summary>
    private async Task RefreshFromServerAsync(CancellationToken ct)
    {
        try
        {
            var me = await App.State.Api.RefreshAccountAsync(ct);
            if (ct.IsCancellationRequested || !App.State.Api.IsSignedIn) return;
            ShowUsername(me.Username);
            var email = string.IsNullOrWhiteSpace(me.Email) ? null : me.Email.Trim();
            EmailText.Text = email ?? "";
            EmailText.IsVisible = email is not null;
            // Verified is the normal state, so it stays quiet; unverified is the one to notice.
            VerifiedText.Text = me.EmailConfirmed ? "Email Verified" : "Email Unverified";
            if (me.EmailConfirmed) VerifiedPill.Classes.Remove("warning");
            else VerifiedPill.Classes.Add("warning");
            ToolTip.SetTip(VerifiedPill, me.EmailConfirmed
                ? "Your email address has been confirmed."
                : "Until the address is confirmed you cannot recover this account by email.");
            VerifiedPill.IsVisible = true;
            MainWindow.Current.UpdateChrome();
        }
        catch (OperationCanceledException) { /* the page went away */ }
        catch (Exception ex)
        {
            AppLog.Log("account", "Could not read the account details: " + ex.Message);
            VerifiedPill.IsVisible = false;
        }
    }

    private void OnSignIn(object? sender, RoutedEventArgs e) => MainWindow.Current.Navigate(new SignInPage());

    /// <summary>Signs out the way the Windows launcher does: the server revokes the refresh token, and
    /// the tokens here are forgotten either way.</summary>
    private async void OnSignOut(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await MainWindow.Current.ConfirmAsync("Sign Out",
                    "Sign out of CloudLauncher on this computer?\n\n"
                    + "Your account's instances are hidden until you sign in again (their files stay on this computer). "
                    + "Instances that are only on this computer stay, and so does everything that needs no account.",
                    "Sign Out", "Cancel", danger: true))
                return;

            SignOutButton.IsEnabled = false;
            try { await App.State.Api.LogoutAsync(); }
            catch (Exception ex) { AppLog.LogError("SignOut", ex); }
            AppLog.Log("auth", "Signed out.");
            MainWindow.Current.OnSignedOut();
        }
        catch (Exception ex)
        {
            Say(AccountStatus, ex.Message, danger: true);
            SignOutButton.IsEnabled = true;
        }
    }

    // ── Minecraft accounts ───────────────────────────────────────────────────

    private void OnAccountsChanged() => Dispatcher.UIThread.Post(ShowMinecraftAccounts);

    private void ShowMinecraftAccounts()
    {
        var service = App.State.MinecraftAccounts;
        var current = service.Current;
        AccountsList.Children.Clear();
        foreach (var account in service.Accounts)
            AccountsList.Children.Add(BuildRow(account, current?.Id == account.Id));
        EmptyState.IsVisible = service.Accounts.Count == 0;
        AccountsList.IsVisible = service.Accounts.Count > 0;

        // The Windows gate: a new offline account needs a Microsoft account here that owns the game.
        var canAddOffline = service.HasJavaEditionAccount;
        OfflineNameBox.IsVisible = canAddOffline;
        AddOfflineButton.IsVisible = canAddOffline;
        OfflineHint.IsVisible = canAddOffline;
        OfflineGate.IsVisible = !canAddOffline;
        OfflineGate.Text = MinecraftAccountService.OfflineNeedsMicrosoftReason;
        OfflineHint.Text = "Any name, for servers in offline mode.";
        UpdateOfflineButton();
        AddMicrosoftButton.IsEnabled = !_busy;
    }

    private Control BuildRow(StoredMinecraftAccount account, bool isCurrent)
    {
        var microsoft = account.Kind == MinecraftAccountKind.Microsoft;

        var glyph = new TextBlock { Text = microsoft ? "" : "", FontSize = 15 };
        glyph.Classes.Add("icon");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var tile = new Border { Width = 36, Height = 36, CornerRadius = new(3), Child = glyph, VerticalAlignment = VerticalAlignment.Center };
        Brush(tile, Border.BackgroundProperty, microsoft ? "AccentSoftBrush" : "Surface4Brush");
        Brush(glyph, TextBlock.ForegroundProperty, microsoft ? "AccentTextBrush" : "TextSecondaryBrush");

        var name = new TextBlock { Text = account.Username, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var kind = new TextBlock { Text = microsoft ? "Microsoft" : "Offline", TextTrimming = TextTrimming.CharacterEllipsis };
        kind.Classes.Add("caption");
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 12, 0), Children = { name, kind } };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (isCurrent)
        {
            var check = new TextBlock { Text = "", FontSize = 12 };
            check.Classes.Add("icon");
            var label = new TextBlock { Text = "In Use", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var inUse = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children = { check, label }
            };
            Brush(check, TextBlock.ForegroundProperty, "AccentTextBrush");
            Brush(label, TextBlock.ForegroundProperty, "AccentTextBrush");
            ToolTip.SetTip(inUse, "The account the game plays as");
            actions.Children.Add(inUse);
        }
        else
        {
            var use = new Button { Content = "Use This One" };
            ToolTip.SetTip(use, "Play as this account from now on");
            use.Click += (_, _) => Use(account);
            actions.Children.Add(use);
        }
        var remove = new Button { Content = "" };
        remove.Classes.Add("icon");
        remove.Classes.Add("danger");
        ToolTip.SetTip(remove, "Remove");
        remove.Click += async (_, _) => await RemoveAsync(account);
        actions.Children.Add(remove);

        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        grid.Children.Add(tile);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        var row = new Border { Padding = new(10, 8), CornerRadius = new(3), BorderThickness = new(1), Child = grid };
        Brush(row, Border.BackgroundProperty, isCurrent ? "AccentSoftBrush" : "Surface2Brush");
        Brush(row, Border.BorderBrushProperty, isCurrent ? "AccentBrush" : "BorderSubtleBrush");
        ToolTip.SetTip(row, $"{account.Username} · {(microsoft ? "Microsoft account" : "Offline account")}"
                            + (isCurrent ? "\nThe game plays as this account." : "")
                            + (string.IsNullOrEmpty(account.Uuid) ? "" : $"\nUUID {account.Uuid}"));
        return row;
    }

    /// <summary>A theme brush that follows the theme (the accent is replaced at startup).</summary>
    private static void Brush(Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, control.GetResourceObservable(key));

    private void Use(StoredMinecraftAccount account)
    {
        try
        {
            App.State.MinecraftAccounts.Switch(account.Id);
            Say(McStatus, $"Now playing as {account.Username}.");
        }
        catch (Exception ex) { Say(McStatus, ex.Message, danger: true); }
    }

    private async Task RemoveAsync(StoredMinecraftAccount account)
    {
        try
        {
            var consequence = account.Kind == MinecraftAccountKind.Microsoft
                ? "You will have to sign in to Microsoft again to use it."
                : "An offline account can be added back at any time by typing the name again.";
            if (!await MainWindow.Current.ConfirmAsync("Remove Account",
                    $"Remove {account.Username} from this launcher?\n\n{consequence}", "Remove", "Cancel", danger: true))
                return;
            await App.State.MinecraftAccounts.RemoveAsync(account.Id);
            Say(McStatus, $"Removed {account.Username}.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("RemoveAccount", ex);
            Say(McStatus, "Could not remove the account: " + ex.Message, danger: true);
        }
    }

    private async void OnAddMicrosoft(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        AddMicrosoftButton.IsEnabled = false;
        Say(McStatus, "Waiting for the Microsoft sign-in...");
        try
        {
            var account = await App.State.MinecraftAccounts.AddMicrosoftAsync();
            Say(McStatus, $"Signed in as {account.Username}. The game plays as this account now.");
        }
        catch (Exception ex) when (IsCancel(ex) is { } cancel)
        {
            // Closing the code card is not a failure; a decline on Microsoft's page only gets said.
            Say(McStatus, cancel.Message == "The sign-in was declined." ? "The sign-in was declined." : "");
        }
        catch (Exception ex)
        {
            AppLog.LogError("AddMicrosoft", ex);
            Say(McStatus, SignInFailure(ex), danger: true);
        }
        finally
        {
            _busy = false;
            AddMicrosoftButton.IsEnabled = true;
        }
    }

    /// <summary>The cancel somewhere in <paramref name="ex"/>'s chain, if it is one.</summary>
    private static OperationCanceledException? IsCancel(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is OperationCanceledException cancel) return cancel;
        return ex is AggregateException agg ? agg.InnerExceptions.Select(IsCancel).FirstOrDefault(c => c is not null) : null;
    }

    /// <summary>A failed sign-in in a sentence. CmlLib's Xbox and Minecraft errors are technical, so the
    /// common ones are named.</summary>
    private static string SignInFailure(Exception ex)
    {
        var text = ex.Message;
        if (ex is TimeoutException) return text;
        if (text.Contains("2148916233", StringComparison.Ordinal))
            return "This Microsoft account has no Xbox profile yet. Sign in once at xbox.com, then try again.";
        if (text.Contains("2148916238", StringComparison.Ordinal))
            return "This is a child account: an adult has to add it to a Microsoft family before it can play.";
        if (text.Contains("NOT_FOUND", StringComparison.Ordinal) || text.Contains("does not own", StringComparison.OrdinalIgnoreCase))
            return "This Microsoft account does not own Minecraft: Java Edition.";
        return "The sign-in did not finish: " + text.TrimEnd('.') + ".";
    }

    private void OnOfflineNameChanged(object? sender, TextChangedEventArgs e) => UpdateOfflineButton();

    private void UpdateOfflineButton() =>
        AddOfflineButton.IsEnabled = OfflineNameBox.IsEnabled && (OfflineNameBox.Text?.Trim().Length ?? 0) >= 3;

    private void OnOfflineNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddOffline();
    }

    private void OnAddOffline(object? sender, RoutedEventArgs e) => AddOffline();

    private void AddOffline()
    {
        try
        {
            var account = App.State.MinecraftAccounts.AddOffline(OfflineNameBox.Text ?? "");
            OfflineNameBox.Text = "";
            Say(McStatus, $"Added the offline account {account.Username}. The game plays as it now.");
        }
        catch (ArgumentException)
        {
            Say(McStatus, "An offline username is 3 to 16 characters long.", danger: true);
        }
        catch (Exception ex) { Say(McStatus, ex.Message, danger: true); }
    }

    // ── status lines ─────────────────────────────────────────────────────────

    private static void Say(TextBlock line, string text, bool danger = false)
    {
        line.Text = text;
        line.IsVisible = text.Length > 0;
        if (danger) { line.Classes.Remove("muted"); line.Classes.Add("danger"); }
        else { line.Classes.Remove("danger"); line.Classes.Add("muted"); }
    }
}
