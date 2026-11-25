using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>A page in the main window. Pages are plain controls; this only adds what the shell asks of
/// them.</summary>
public interface IShellPage
{
    /// <summary>Which side rail item lights up while the page is on screen.</summary>
    ShellNav Nav { get; }

    /// <summary>Called each time the page comes on screen (first time included).</summary>
    void OnShown() { }
}

public enum ShellNav { None, Instances, Modpacks, Account, Settings }

/// <summary>
/// The launcher window: the side rail, the page on show (with a back stack), a layer for popups over
/// the page, and the status line for background work. <see cref="Current"/> is how pages reach it.
/// </summary>
public partial class MainWindow : Window
{
    public static MainWindow Current { get; private set; } = null!;

    private readonly Stack<Control> _back = new();
    private readonly List<CardEntry> _cards = new();
    private readonly Dictionary<Guid, ProgressInfo> _progress = new();

    private sealed record CardEntry(Control Card, Action? Cancel);

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        LinuxTheme.Apply(this, App.State.Settings);

        ProgressHub.ProgressChanged += OnProgressChanged;
        ProgressHub.ProgressCleared += OnProgressCleared;
        App.State.ConnectivityChanged += UpdateChrome;
        App.State.SessionExpired += () => Dispatcher.UIThread.Post(OnSessionExpired);
        App.State.MinecraftAccounts.AccountsChanged += () => Dispatcher.UIThread.Post(UpdateChrome);
        App.State.ManualDownloads.Needed += OnManualDownloadsNeeded;
        DeviceCodeSignIn.CodeReady = ShowDeviceCodeAsync;

        Opened += (_, _) =>
        {
            UpdateChrome();
            // Like the Windows launcher: Instances first, signed in or not (signed out it lists this
            // PC's own instances and offers to sign in).
            NavigateToInstances();
        };
        KeyDown += OnWindowKeyDown;
    }

    // ── pages ─────────────────────────────────────────────────────────────────

    public Control? CurrentPage => PageHost.Content as Control;

    /// <summary>Shows <paramref name="page"/>, keeping the current one to go back to (unless
    /// <paramref name="clearHistory"/>, for the rail's own items).</summary>
    public void Navigate(Control page, bool clearHistory = false)
    {
        if (clearHistory) _back.Clear();
        else if (CurrentPage is { } current) _back.Push(current);
        Show(page);
    }

    /// <summary>Back to the previous page; to Instances when there is none.</summary>
    public void GoBack()
    {
        if (_back.Count > 0) Show(_back.Pop());
        else NavigateToInstances();
    }

    public bool CanGoBack => _back.Count > 0;

    private void Show(Control page)
    {
        PageHost.Content = page;
        var nav = (page as IShellPage)?.Nav ?? ShellNav.None;
        SetActive(NavInstances, nav == ShellNav.Instances);
        SetActive(NavModpacks, nav == ShellNav.Modpacks);
        SetActive(NavAccount, nav == ShellNav.Account);
        SetActive(NavSettings, nav == ShellNav.Settings);
        (page as IShellPage)?.OnShown();

        static void SetActive(Button b, bool on)
        {
            if (on) b.Classes.Add("active");
            else b.Classes.Remove("active");
        }
    }

    public void NavigateToInstances() => Navigate(new InstancesPage(), clearHistory: true);

    private void OnNavInstances(object? sender, RoutedEventArgs e) => NavigateToInstances();
    private void OnNavModpacks(object? sender, RoutedEventArgs e) => Navigate(new ModpackBrowserPage(), clearHistory: true);
    private void OnNavAccount(object? sender, RoutedEventArgs e) => Navigate(new AccountPage(), clearHistory: true);
    private void OnNavSettings(object? sender, RoutedEventArgs e) => Navigate(new SettingsPage(), clearHistory: true);

    /// <summary>After signing in to a CloudLauncher account.</summary>
    public void OnSignedIn()
    {
        UpdateChrome();
        NavigateToInstances();
        _ = App.State.Rules.SyncGlobalDefaultsFromServerAsync();
    }

    /// <summary>After signing out: the sign-in page, which also offers going on without an account.</summary>
    public void OnSignedOut()
    {
        UpdateChrome();
        Navigate(new SignInPage(), clearHistory: true);
    }

    private void OnSessionExpired()
    {
        UpdateChrome();
        Toast("Your CloudLauncher session ended. Sign in again to sync your instances.");
        Navigate(new SignInPage(), clearHistory: true);
    }

    /// <summary>The rail's account lines and the offline mark.</summary>
    public void UpdateChrome()
    {
        var s = App.State.Settings;
        AccountLabel.Text = App.State.Api.IsSignedIn && !string.IsNullOrEmpty(s.Username) ? s.Username! : "Not signed in";
        var mc = App.State.MinecraftAccounts.Current;
        MinecraftLabel.Text = mc is null ? "No Minecraft account" : mc.Username;
        OfflineText.IsVisible = App.State.IsOffline;
        if (App.State.IsOffline && App.State.OfflineReason is { } why)
            ToolTip.SetTip(OfflineText, "The launcher can't reach its server: " + why);
    }

    // ── popups ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows <paramref name="card"/> over the page until <paramref name="closed"/> completes, and
    /// returns its result. Escape or a click on the dimmed page calls <paramref name="cancel"/> (when
    /// there is one: a card without it closes only from its own buttons). Cards stack: a second one
    /// shows over the first and the first comes back when it closes.
    /// </summary>
    public async Task<T> ShowCardAsync<T>(Control card, Task<T> closed, Action? cancel = null)
    {
        var entry = new CardEntry(card, cancel);
        _cards.Add(entry);
        ShowTopCard();
        try { return await closed; }
        finally
        {
            _cards.Remove(entry);
            ShowTopCard();
        }
    }

    private void ShowTopCard()
    {
        var top = _cards.LastOrDefault();
        CardHost.Content = top?.Card;
        CardLayer.IsVisible = top is not null;
        if (top?.Card is { } c) Dispatcher.UIThread.Post(() => c.Focus(), DispatcherPriority.Background);
    }

    public bool IsCardOpen => _cards.Count > 0;

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => _cards.LastOrDefault()?.Cancel?.Invoke();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _cards.LastOrDefault() is { Cancel: { } cancel })
        {
            cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && !IsCardOpen && CurrentPage is IRefreshable refreshable)
        {
            _ = refreshable.RefreshAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.BrowserBack || (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt))
        {
            if (!IsCardOpen && CanGoBack) GoBack();
            e.Handled = true;
        }
    }

    /// <summary>A yes/no question as a card. True for yes.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string yes = "OK", string no = "Cancel", bool danger = false) =>
        MessageCard.ConfirmAsync(this, title, message, yes, no, danger);

    /// <summary>A message with one button.</summary>
    public Task AlertAsync(string title, string message) => MessageCard.AlertAsync(this, title, message);

    // ── status line ───────────────────────────────────────────────────────────

    private DispatcherTimer? _toastTimer;

    /// <summary>A short message on the status line, gone after a few seconds.</summary>
    public void Toast(string text, bool danger = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = danger ? (Avalonia.Media.IBrush?)this.FindResource("DangerBrush") : (Avalonia.Media.IBrush?)this.FindResource("TextSecondaryBrush");
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            RefreshProgressLine();
        };
        _toastTimer.Start();
    }

    private void OnProgressChanged(ProgressInfo info)
    {
        _progress[info.PackId] = info;
        RefreshProgressLine();
    }

    private void OnProgressCleared(Guid packId)
    {
        _progress.Remove(packId);
        RefreshProgressLine();
    }

    private void RefreshProgressLine()
    {
        if (_toastTimer?.IsEnabled == true) return;
        var info = _progress.Values.LastOrDefault();
        StatusProgress.IsVisible = info is not null;
        StatusText.Foreground = (Avalonia.Media.IBrush?)this.FindResource("TextTertiaryBrush");
        if (info is null)
        {
            StatusText.Text = "";
            return;
        }
        var more = _progress.Count > 1 ? $"  (+{_progress.Count - 1} more)" : "";
        StatusText.Text = info.Label + (string.IsNullOrEmpty(info.CurrentLabel) ? "" : "  ·  " + info.CurrentLabel) + more;
        StatusProgress.IsIndeterminate = info.Fraction < 0;
        if (info.Fraction >= 0) StatusProgress.Value = info.Fraction * 100;
    }

    // ── service prompts ───────────────────────────────────────────────────────

    /// <summary>Files to download by hand (a CurseForge author's opt-out): the card that opens their
    /// pages in the browser and moves them in from Downloads.</summary>
    private async void OnManualDownloadsNeeded(Guid packId, string packName, IReadOnlyList<int>? fileIds)
    {
        try { await ManualDownloadsCard.ShowAsync(this, packId, packName, focus: fileIds); }
        catch (Exception ex) { AppLog.LogError("manual-downloads", ex); }
    }

    /// <summary>Microsoft sign-in needs the user: show the code to enter at microsoft.com/link.</summary>
    private Task ShowDeviceCodeAsync(DeviceCodePrompt prompt, CancellationTokenSource cancel) =>
        Dispatcher.UIThread.InvokeAsync(() => DeviceCodeCard.Show(this, prompt, cancel)).GetTask();
}

/// <summary>A page whose data F5 reloads.</summary>
public interface IRefreshable
{
    Task RefreshAsync();
}
