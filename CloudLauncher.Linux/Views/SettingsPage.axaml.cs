using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The launcher's settings that mean something on Linux, read and saved the way the Windows launcher's
/// SettingsPanel does (through <see cref="AppSettings"/>, saved as they change): Java, default memory,
/// downloads and the download-by-hand watch folder, the instances folder, the user's own CurseForge key,
/// the server address, and About (updates, release notes, links, logs, the app menu entry).
/// </summary>
public partial class SettingsPage : UserControl, IShellPage
{
    /// <summary>True while the page writes its own controls, so their change events are not taken as
    /// the user's.</summary>
    private bool _suppress = true;

    private CancellationTokenSource _cts = new();
    private DispatcherTimer? _saveTimer;
    private bool _javaLoading;
    private List<string?> _javaPaths = new();

    public SettingsPage()
    {
        InitializeComponent();
        SettingsLook.UseSlateDisabledLook(this);
        DetachedFromVisualTree += (_, _) =>
        {
            SaveNow();
            _cts.Cancel();
        };
    }

    public ShellNav Nav => ShellNav.Settings;

    public void OnShown()
    {
        if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
        Refresh();
    }

    private static AppSettings Settings => App.State.Settings;

    private void Refresh()
    {
        _suppress = true;
        try
        {
            var s = Settings;
            RamSlider.Maximum = AppSettings.RamSliderMaximum(s.EffectiveDefaultMaxRamMb);
            RamSlider.Value = s.EffectiveDefaultMaxRamMb;
            ShowRam();

            ConcurrencySlider.Value = s.EffectiveModDownloadConcurrency;
            ConcurrencyLabel.Text = ConcurrencyText(s.EffectiveModDownloadConcurrency);
            ShowWatchFolder();
            RemoveAfterBox.IsChecked = App.State.ManualDownloads.RemoveAfterAdding;

            PacksFolderText.Text = s.PacksRoot;
            ToolTip.SetTip(PacksFolderText, s.PacksRoot);
            PacksFolderHint.Text = Directory.Exists(s.PacksRoot)
                ? "New instances are made here."
                : "New instances are made here. The folder is created when the first one needs it.";

            CurseForgeKeyBox.Text = s.CurseForgeApiKey ?? "";
            DirectBox.IsChecked = s.CurseForgeDirect;
            ShowCurseForgeKey();

            ServerBox.Text = s.ServerUrl;
            ShowServer();

            VersionText.Text = $"Version {AppVersion.CurrentString} for Linux";
            ToolTip.SetTip(TermsButton, App.State.Api.LegalPageUrl(Legal.TermsPath));
            ToolTip.SetTip(PrivacyButton, App.State.Api.LegalPageUrl(Legal.PrivacyPath));
            if (LinuxUpdater.Available is { } known && AppVersion.IsNewer(known.Version))
                Say(UpdateStatus, $"CloudLauncher {known.Version} is out. Check for Updates installs it.");
            ShowAppMenu();
        }
        finally { _suppress = false; }
        _ = LoadJavaAsync();
    }

    /// <summary>Saves a moment after the last change, so dragging a slider does not write the settings
    /// file for every step it passes.</summary>
    private void SaveSoon()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick -= OnSaveTick;
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTick(object? sender, EventArgs e) => SaveNow();

    private void SaveNow()
    {
        if (_saveTimer is not { IsEnabled: true }) return;
        _saveTimer.Stop();
        Save();
    }

    private static void Save()
    {
        try { Settings.Save(); }
        catch (Exception ex)
        {
            AppLog.LogError("settings", ex);
            MainWindow.Current.Toast("The settings could not be saved: " + ex.Message, danger: true);
        }
    }

    // ── Java ─────────────────────────────────────────────────────────────────

    /// <summary>Automatic, then every Java the launcher can find (as the Windows picker lists them), and
    /// the chosen one when it is somewhere the search does not look.</summary>
    private async Task LoadJavaAsync(bool refresh = false)
    {
        _javaLoading = true;
        JavaBox.IsEnabled = false;
        JavaHint.Text = "Looking for Java on this computer...";
        try
        {
            IReadOnlyList<LaunchService.JavaInstall> installs;
            try { installs = await LaunchService.DetectJavaInstallsAsync(refresh); }
            catch (Exception ex)
            {
                AppLog.LogError("java", ex);
                installs = Array.Empty<LaunchService.JavaInstall>();
            }

            var labels = new List<string> { "Automatic" };
            var paths = new List<string?> { null };
            foreach (var j in installs)
            {
                labels.Add($"Java {j.Major}  ·  {j.Path}" + (j.Managed ? "  (downloaded by the launcher)" : ""));
                paths.Add(j.Path);
            }
            var current = Settings.DefaultJavaPath;
            if (!string.IsNullOrWhiteSpace(current) && !paths.Any(p => SamePath(p, current)))
            {
                labels.Add($"Java  ·  {current}");
                paths.Add(current);
            }

            _suppress = true;
            try
            {
                _javaPaths = paths;
                JavaBox.ItemsSource = labels;
                var index = string.IsNullOrWhiteSpace(current) ? 0 : paths.FindIndex(p => SamePath(p, current));
                JavaBox.SelectedIndex = Math.Max(0, index);
            }
            finally { _suppress = false; }
            ShowJavaHint(installs.Count);
        }
        finally
        {
            _javaLoading = false;
            JavaBox.IsEnabled = true;
        }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void ShowJavaHint(int found)
    {
        var path = Settings.DefaultJavaPath;
        JavaHint.Text = string.IsNullOrWhiteSpace(path)
            ? "Each instance uses the Java its Minecraft version needs (8, 17 or 21), downloaded when it is missing. "
              + "An instance can choose its own."
              + (found == 0 ? " No Java is installed on this computer, which is fine." : "")
            : File.Exists(path)
                ? $"Instances without a choice of their own launch with {path}."
                : $"{path} is gone, so instances pick their Java automatically until you choose another.";
    }

    private void OnJavaChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _javaLoading || JavaBox.SelectedIndex < 0 || JavaBox.SelectedIndex >= _javaPaths.Count) return;
        var path = _javaPaths[JavaBox.SelectedIndex];
        if (SamePath(path, Settings.DefaultJavaPath) || (path is null && string.IsNullOrWhiteSpace(Settings.DefaultJavaPath))) return;
        Settings.DefaultJavaPath = path;
        Save();
        ShowJavaHint(_javaPaths.Count - 1);
        MainWindow.Current.Toast(path is null ? "Java is chosen automatically for each instance." : "Default Java saved.");
    }

    private async void OnBrowseJava(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a Java Program",
                AllowMultiple = false
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

            var name = Path.GetFileName(path);
            var isJava = OperatingSystem.IsWindows()
                ? name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)
                : name == "java";
            if (!isJava)
            {
                JavaHint.Text = "Pick the java program itself: it is in the bin folder of a Java installation.";
                return;
            }
            Settings.DefaultJavaPath = path;
            Save();
            await LoadJavaAsync();
            MainWindow.Current.Toast("Default Java saved.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("java", ex);
            JavaHint.Text = "Could not choose that Java: " + ex.Message;
        }
    }

    // ── memory ───────────────────────────────────────────────────────────────

    private static string RamText(int mb) => (mb / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " GB";

    private void ShowRam()
    {
        var s = Settings;
        var recommended = AppSettings.RecommendedRamMb();
        RamLabel.Text = RamText((int)RamSlider.Value);
        RamHint.Text = s.DefaultMaxRamChosen
            ? $"For instances without a setting of their own. Recommended for this computer: {RamText(recommended)}."
            : "For instances without a setting of their own. This is the recommendation for this computer.";
        RamRecommendedButton.IsVisible = s.DefaultMaxRamChosen;
    }

    private void OnRamChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (RamLabel is null) return;
        RamLabel.Text = RamText((int)RamSlider.Value);
        if (_suppress) return;
        Settings.DefaultMaxRamMb = (int)RamSlider.Value;
        Settings.DefaultMaxRamChosen = true;
        SaveSoon();
        ShowRam();
    }

    /// <summary>Back to following the recommendation, which moves with this computer's memory.</summary>
    private void OnRamRecommended(object? sender, RoutedEventArgs e)
    {
        Settings.DefaultMaxRamChosen = false;
        Save();
        _suppress = true;
        try { RamSlider.Value = Settings.EffectiveDefaultMaxRamMb; }
        finally { _suppress = false; }
        ShowRam();
        MainWindow.Current.Toast($"New instances get {RamText(Settings.EffectiveDefaultMaxRamMb)}, the recommendation for this computer.");
    }

    // ── downloads ────────────────────────────────────────────────────────────

    private static string ConcurrencyText(int n) => n == 1 ? "1 file" : $"{n} files";

    private void OnConcurrencyChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (ConcurrencyLabel is null) return;
        var n = (int)Math.Round(ConcurrencySlider.Value);
        ConcurrencyLabel.Text = ConcurrencyText(n);
        if (_suppress) return;
        Settings.ModDownloadConcurrency = n;
        SaveSoon();
    }

    private void ShowWatchFolder()
    {
        var folder = App.State.ManualDownloads.WatchFolder;
        WatchFolderText.Text = folder;
        ToolTip.SetTip(WatchFolderText, folder);
        WatchDefaultButton.IsVisible = Settings.ManualDownloadFolder is { Length: > 0 } chosen && Directory.Exists(chosen);
    }

    private async void OnChangeWatchFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var start = await storage.TryGetFolderFromPathAsync(App.State.ManualDownloads.WatchFolder);
            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the Watch Folder",
                AllowMultiple = false,
                SuggestedStartLocation = start
            });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            App.State.ManualDownloads.SetWatchFolder(path);
            ShowWatchFolder();
            MainWindow.Current.Toast($"Files you download by hand are picked up from {App.State.ManualDownloads.WatchFolder}.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("settings", ex);
            MainWindow.Current.Toast("Could not change the watch folder: " + ex.Message, danger: true);
        }
    }

    private void OnWatchFolderDefault(object? sender, RoutedEventArgs e)
    {
        App.State.ManualDownloads.SetWatchFolder(null);
        ShowWatchFolder();
        MainWindow.Current.Toast("Files you download by hand are picked up from Downloads again.");
    }

    private void OnOpenWatchFolder(object? sender, RoutedEventArgs e) =>
        OpenFolder(App.State.ManualDownloads.WatchFolder, create: false);

    private void OnRemoveAfterChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        App.State.ManualDownloads.RemoveAfterAdding = RemoveAfterBox.IsChecked == true;
    }

    // ── storage ──────────────────────────────────────────────────────────────

    private void OnOpenPacksFolder(object? sender, RoutedEventArgs e) => OpenFolder(Settings.PacksRoot, create: true);

    /// <summary>Opens a folder in the file manager; the instances folder is made first, as on Windows, so
    /// a fresh install has something to show.</summary>
    private static void OpenFolder(string path, bool create)
    {
        try
        {
            if (create) Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            MainWindow.Current.Toast($"Could not create {path}: {ex.Message}", danger: true);
            return;
        }
        if (!Directory.Exists(path)) MainWindow.Current.Toast($"{path} does not exist.", danger: true);
        else if (!SafeLaunch.OpenFolder(path)) MainWindow.Current.Toast($"Could not open {path} in the file manager.", danger: true);
    }

    // ── mod stores ───────────────────────────────────────────────────────────

    private bool _keyWriteFailed;
    private string? _keyTested;
    private bool _testingKey;

    /// <summary>Stores the key as it is typed, like the Windows launcher: in its own file that only this
    /// user can read, sent with each store request and never kept by the server.</summary>
    private void OnCurseForgeKeyChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        _keyWriteFailed = !Settings.SetCurseForgeApiKey(CurseForgeKeyBox.Text);
        _keyTested = null;
        ShowCurseForgeKey();
    }

    private void OnRevealKey(object? sender, RoutedEventArgs e)
    {
        CurseForgeKeyBox.RevealPassword = !CurseForgeKeyBox.RevealPassword;
        RevealKeyButton.Content = CurseForgeKeyBox.RevealPassword ? "Hide" : "Show";
    }

    private void ShowCurseForgeKey()
    {
        var key = Settings.CurseForgeApiKey;
        var text = key is { Length: > 0 }
            ? "Your own key is saved, and every CurseForge search, update check and download uses it. "
              + "Clear the box to go back to the shared one."
            : "Searches, update checks and downloads use the launcher's shared key. Your own avoids its refusals when many people use it at once.";
        if (_keyWriteFailed || Settings.CurseForgeKeyInSettingsFile)
            text = "Saved in settings.json, because the key store could not be written"
                   + (SecretStore.LastError is { } why ? $" ({why})" : "") + ".";
        if (_keyTested is not null) text += "\n" + _keyTested;
        CurseForgeHint.Text = text;
        // These only mean something with a key of your own.
        var own = key is { Length: > 0 };
        RevealKeyButton.IsVisible = own;
        if (!own && CurseForgeKeyBox.RevealPassword)
        {
            CurseForgeKeyBox.RevealPassword = false;
            RevealKeyButton.Content = "Show";
        }
        TestKeyButton.IsVisible = own;
        TestKeyButton.IsEnabled = !_testingKey;
        DirectPanel.IsVisible = own;
    }

    /// <summary>Asks CurseForge whether it takes the saved key, as the Windows launcher does: a search
    /// for random text, so no cache answers in the key's place.</summary>
    private async void OnTestKey(object? sender, RoutedEventArgs e)
    {
        var key = Settings.CurseForgeApiKey;
        if (key is not { Length: > 0 }) return;
        if (!App.State.Api.IsCurseForgeDirect
            && Uri.TryCreate(Settings.ServerUrl, UriKind.Absolute, out var server)
            && server.Scheme != Uri.UriSchemeHttps && !server.IsLoopback)
        {
            _keyTested = "The launcher server is on plain http, so your key is not sent to it at all.";
            ShowCurseForgeKey();
            return;
        }

        _testingKey = true;
        _keyTested = "Testing...";
        ShowCurseForgeKey();
        try
        {
            var probe = "cl-key-test-" + Guid.NewGuid().ToString("N")[..10];
            using var resp = await App.State.Api.ProxyAsync("curseforge", System.Net.Http.HttpMethod.Get,
                $"mods/search?gameId=432&classId=6&pageSize=1&searchFilter={probe}");
            if (!ReferenceEquals(key, Settings.CurseForgeApiKey)) return; // edited meanwhile
            var body = resp.IsSuccessStatusCode ? "" : await resp.Content.ReadAsStringAsync();
            _keyTested = (int)resp.StatusCode switch
            {
                >= 200 and < 300 => "CurseForge accepted this key.",
                400 when body.Contains("own_key_rejected", StringComparison.Ordinal)
                    => "CurseForge refused this key. Check it was copied whole, or make a new one.",
                429 => "CurseForge is busy, so the key could not be tested. Try again in a minute.",
                401 => "Sign in to the launcher first, then test the key.",
                var code => $"The test got no answer ({code}). Try again in a minute."
            };
        }
        catch (Exception ex)
        {
            _keyTested = "The test got no answer: " + ex.Message;
        }
        finally
        {
            _testingKey = false;
            ShowCurseForgeKey();
        }
    }

    private void OnDirectChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        Settings.CurseForgeDirect = DirectBox.IsChecked == true;
        Save();
        _keyTested = null;
        ShowCurseForgeKey();
        MainWindow.Current.Toast(Settings.CurseForgeDirect
            ? "CurseForge requests go straight to CurseForge with your key."
            : "CurseForge requests go through the launcher server again.");
    }

    private void OnGetKey(object? sender, RoutedEventArgs e) => OpenInBrowser("https://console.curseforge.com/?#/api-keys");

    // ── advanced ─────────────────────────────────────────────────────────────

    private static string Normalize(string? url) => (url ?? "").Trim().TrimEnd('/');

    private void ShowServer(string? error = null)
    {
        var typed = Normalize(ServerBox.Text);
        var current = Normalize(Settings.ServerUrl);
        ServerApplyButton.IsEnabled = typed.Length > 0 && !string.Equals(typed, current, StringComparison.OrdinalIgnoreCase);
        ServerDefaultButton.IsVisible = !string.Equals(typed, AppSettings.DefaultServerUrl, StringComparison.OrdinalIgnoreCase);
        ServerHint.Classes.Remove("danger");
        if (error is not null)
        {
            ServerHint.Classes.Add("danger");
            ServerHint.Text = error;
        }
        else
        {
            ServerHint.Text = AppSettings.IsSecureServerUrl(current)
                ? "Only change it for a CloudLauncher server of your own. The launcher restarts to use a new address."
                : "This address is plain http to another computer: the launcher sends no password or session there.";
        }
    }

    private void OnServerTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        ShowServer();
    }

    private void OnServerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !ServerApplyButton.IsEnabled) return;
        e.Handled = true;
        OnApplyServer(sender, e);
    }

    private void OnServerDefault(object? sender, RoutedEventArgs e) => ServerBox.Text = AppSettings.DefaultServerUrl;

    /// <summary>
    /// Points the launcher at another server. The address is read once at start, so the launcher
    /// restarts. Signed in, it signs out first: the session belongs to the old server, and its token must
    /// not be sent to the new one.
    /// </summary>
    private async void OnApplyServer(object? sender, RoutedEventArgs e)
    {
        try
        {
            var url = Normalize(ServerBox.Text);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || string.IsNullOrEmpty(uri.Host) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                ShowServer($"That is not a server address. It looks like {AppSettings.DefaultServerUrl}.");
                return;
            }
            if (string.Equals(url, Normalize(Settings.ServerUrl), StringComparison.OrdinalIgnoreCase)) { ShowServer(); return; }

            var signedIn = App.State.Api.IsSignedIn;
            var canRestart = OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.ProcessPath);
            var message = (signedIn ? "You are signed out on this computer, and the launcher " : "The launcher ")
                          + (canRestart ? $"restarts to use {url}." : $"uses {url} once you restart it.")
                          + (AppSettings.IsSecureServerUrl(url)
                              ? ""
                              : "\n\nThis address is plain http to another computer, so the launcher will not send your password or session there.");
            if (!await MainWindow.Current.ConfirmAsync("Change Server", message,
                    canRestart ? "Change and Restart" : "Change", "Cancel"))
                return;

            ServerApplyButton.IsEnabled = false;
            if (signedIn)
            {
                try { await App.State.Api.LogoutAsync(); }
                catch (Exception ex) { AppLog.LogError("SignOut", ex); }
            }
            Settings.ServerUrl = url;
            Save();
            AppLog.Log("settings", $"Server address changed to {url}.");
            if (canRestart)
            {
                LinuxUpdater.Restart();
                return;
            }
            if (signedIn) MainWindow.Current.UpdateChrome();
            ShowServer();
            ServerHint.Text = "Saved. Restart the launcher to use the new address.";
        }
        catch (Exception ex)
        {
            AppLog.LogError("settings", ex);
            ShowServer("Could not change the server: " + ex.Message);
        }
    }

    // ── about ────────────────────────────────────────────────────────────────

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        Say(UpdateStatus, "Checking for updates...");
        try
        {
            var latest = await LinuxUpdater.GetLatestAsync(_cts.Token);
            if (latest is null)
                Say(UpdateStatus, "There is no Linux release on the server yet.");
            else if (!AppVersion.IsNewer(latest.Version))
                Say(UpdateStatus, $"You're up to date (latest is {latest.Version}).");
            else
            {
                var check = LinuxUpdater.Verify(latest);
                Say(UpdateStatus, check.IsValid
                    ? $"CloudLauncher {latest.Version} is available."
                    : $"CloudLauncher {latest.Version} is out, but it could not be verified.");
                await SettingsUpdateCard.ShowAsync(MainWindow.Current, latest, check);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Log("update", "Check for updates: " + ex.Message);
            Say(UpdateStatus, "Couldn't check for updates: " + LinuxUpdater.Describe(ex), danger: true);
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private async void OnWhatsNew(object? sender, RoutedEventArgs e)
    {
        try { await SettingsChangelogCard.ShowAsync(MainWindow.Current); }
        catch (Exception ex) { Say(UpdateStatus, "Could not open the release notes: " + ex.Message, danger: true); }
    }

    private void ShowAppMenu()
    {
        var installed = DesktopIntegration.IsInstalled;
        AppMenuButton.Content = installed ? "Remove from App Menu" : "Add to App Menu";
        var other = installed && !DesktopIntegration.PointsHere ? DesktopIntegration.InstalledTarget : null;
        AppMenuHint.Text = !DesktopIntegration.IsSupported
            ? "For Linux desktops."
            : other is not null
                ? $"The menu entry starts the copy in {Path.GetDirectoryName(other)}."
                : installed ? "CloudLauncher is in your applications menu." : "";
    }

    private async void OnAppMenu(object? sender, RoutedEventArgs e)
    {
        var remove = DesktopIntegration.IsInstalled;
        AppMenuButton.IsEnabled = false;
        try
        {
            if (remove) await Task.Run(DesktopIntegration.Uninstall);
            else await Task.Run(DesktopIntegration.Install);
            MainWindow.Current.Toast(remove ? "CloudLauncher is no longer in the app menu." : "CloudLauncher is in your app menu now.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("desktop", ex);
            MainWindow.Current.Toast((remove ? "Could not remove the menu entry: " : "Could not add the menu entry: ") + ex.Message, danger: true);
        }
        finally
        {
            AppMenuButton.IsEnabled = true;
            ShowAppMenu();
        }
    }

    private void OnOpenLogs(object? sender, RoutedEventArgs e)
    {
        // Whatever is still queued goes to the file first, so the newest lines are there to read.
        AppLog.Flush();
        OpenFolder(AppLog.LogFolder, create: true);
    }

    private void OnWebsite(object? sender, RoutedEventArgs e) => OpenInBrowser(LinuxUpdater.WebsiteUrl);
    private void OnDiscord(object? sender, RoutedEventArgs e) => OpenInBrowser(Legal.DiscordUrl);
    private void OnTerms(object? sender, RoutedEventArgs e) => OpenInBrowser(App.State.Api.LegalPageUrl(Legal.TermsPath));
    private void OnPrivacy(object? sender, RoutedEventArgs e) => OpenInBrowser(App.State.Api.LegalPageUrl(Legal.PrivacyPath));
    private void OnLicence(object? sender, RoutedEventArgs e) => OpenInstalledFile("LICENSE.txt", "licence");
    private void OnNotices(object? sender, RoutedEventArgs e) => OpenInstalledFile("THIRD-PARTY-NOTICES.txt", "third-party notices");

    private void OpenInstalledFile(string name, string what)
    {
        if (!SafeLaunch.OpenFile(Path.Combine(AppContext.BaseDirectory, name)))
            Say(AboutStatus, $"The {what} file is missing from this installation.", danger: true);
    }

    private void OpenInBrowser(string url)
    {
        if (!SafeLaunch.OpenUrl(url)) MainWindow.Current.Toast($"Your browser could not be opened. The address is {url}", danger: true);
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
