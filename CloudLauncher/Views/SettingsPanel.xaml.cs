using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

public partial class SettingsPanel : Page
{
    private readonly MainWindow _shell;
    private bool _suppress = true;

    public SettingsPanel(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _suppress = true;
        try
        {
            var s = App.State.Settings;
            RamSlider.Value = s.DefaultMaxRamMb;
            RamValueLabel.Text = $"{(int)RamSlider.Value} MB";
            UseSidePanelBox.IsChecked = s.UseSidePanel;
            LauncherScaleSlider.Value = UiScale.Launcher;
            LauncherScaleLabel.Text = $"{UiScale.Launcher * 100:0}%";
            ModScaleSlider.Value = UiScale.ModList;
            ModScaleLabel.Text = $"{UiScale.ModList * 100:0}%";
            ModChannelBox.SelectedIndex = ModUpdateChannel.Normalize(s.ModVersionChannel) switch
            {
                ModUpdateChannel.Beta => 1,
                ModUpdateChannel.Release => 2,
                _ => 0
            };
            ModRowWidthSlider.Value = s.EffectiveModRowContentWidth;
            ModRowWidthLabel.Text = $"{(int)ModRowWidthSlider.Value} px";
            ModRowActionsRightBox.IsChecked = s.ModRowActionsAtRight;
            UseCustomGameWindowBox.IsChecked = s.UseCustomGameWindow;
            CustomGameWindowOptions.IsEnabled = s.UseCustomGameWindow;
            MinecraftWindowKey.Bound = s.MinecraftWindowToggleKey;
            MinecraftWindowFullscreenKey.Bound = s.MinecraftWindowFullscreenKey;
            MinecraftWindowPackPageCollapsedBox.IsChecked = s.MinecraftWindowPackPageCollapsedByDefault;
            MinecraftWindowBorderlessFullscreenBox.IsChecked = s.MinecraftWindowBorderlessFullscreenByDefault;
            MinecraftWindowKeyHint.Text =
                $"{LauncherKeybinds.PrettyName(s.MinecraftWindowToggleKey)} collapses bars; "
                + $"{LauncherKeybinds.PrettyName(s.MinecraftWindowFullscreenKey)} toggles borderless fullscreen.";
            ConcurrencySlider.Value = s.EffectiveModDownloadConcurrency;
            ConcurrencyLabel.Text = ConcurrencyText((int)ConcurrencySlider.Value);
            CurseForgeKeyBox.Password = s.CurseForgeApiKey ?? "";
            RefreshCurseForgeKeyHint();
            RefreshThemeControls();
            PacksFolderLabel.Text = s.PacksRoot;
            RuntimeFolderLabel.Text = AppSettings.RuntimeRoot;
            VersionLabel.Text = $"Installed version {AppVersion.CurrentString}.";
        }
        finally { _suppress = false; }
        _ = LoadDefaultJavaChoicesAsync();
    }

    // ── mods ─────────────────────────────────────────────────────────────────

    /// <summary>The channel every instance without one of its own follows, for downloads and for
    /// update checks. Packs and single mods can still override it.</summary>
    private void OnModChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        App.State.Settings.ModVersionChannel = ModChannelBox.SelectedIndex switch
        {
            1 => ModUpdateChannel.Beta,
            2 => ModUpdateChannel.Release,
            _ => ModUpdateChannel.Alpha
        };
        App.State.Settings.Save();
    }

    private void OnModRowWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ModRowWidthLabel is not null) ModRowWidthLabel.Text = $"{(int)ModRowWidthSlider.Value} px";
        if (_suppress) return;
        App.State.Settings.ModRowContentWidth = ModRowWidthSlider.Value;
        App.State.Settings.Save();
    }

    private void OnModRowActionsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        App.State.Settings.ModRowActionsAtRight = ModRowActionsRightBox.IsChecked == true;
        App.State.Settings.Save();
    }

    // ── downloads ────────────────────────────────────────────────────────────

    private static string ConcurrencyText(int value) =>
        value == 1 ? "1 file" : $"{value} files";

    private void OnConcurrencyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConcurrencyLabel is not null) ConcurrencyLabel.Text = ConcurrencyText((int)ConcurrencySlider.Value);
        if (_suppress) return;
        App.State.Settings.ModDownloadConcurrency = (int)ConcurrencySlider.Value;
        App.State.Settings.Save();
    }

    // ── mod stores ───────────────────────────────────────────────────────────

    /// <summary>
    /// Stores the user's own CurseForge key. It is kept in this machine's settings.json and sent with
    /// each proxied request; the server uses it for that request and does not keep it.
    /// </summary>
    private void OnCurseForgeKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var key = CurseForgeKeyBox.Password.Trim();
        App.State.Settings.CurseForgeApiKey = key.Length == 0 ? null : key;
        App.State.Settings.Save();
        RefreshCurseForgeKeyHint();
    }

    private void RefreshCurseForgeKeyHint()
    {
        var key = App.State.Settings.CurseForgeApiKey;
        CurseForgeKeyHint.Text = key is { Length: > 0 }
            ? $"Using your own key ({key.Length} characters). Clear the box to go back to the shared one."
            : "Using the launcher's shared key. Set your own if CurseForge searches or updates keep being refused when several people are using the launcher at once.";
    }

    private void OnOpenCurseForgeConsole(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://console.curseforge.com/?#/api-keys",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open the browser: " + ex.Message; }
    }

    // ── changelog ────────────────────────────────────────────────────────────

    /// <summary>Opens the release notes for every published version. Sits next to Check for updates
    /// because "what changed?" and "is there an update?" are the same trip.</summary>
    private async void OnOpenChangelog(object sender, RoutedEventArgs e)
    {
        try { await ChangelogDialog.ShowAsync(_shell); }
        catch (Exception ex) { StatusLabel.Text = "Could not open the changelog: " + ex.Message; }
    }

    // ── colours ──────────────────────────────────────────────────────────────

    private void RefreshThemeControls()
    {
        var theme = App.State.Settings.Theme;
        if (ThemePresetBox.ItemsSource is null) ThemePresetBox.ItemsSource = ThemeService.Presets;
        ThemePresetBox.SelectedItem = ThemeService.Presets.FirstOrDefault(p => p.Name == theme.PresetName);

        PaintSwatch(AccentSwatch, theme.Accent ?? ThemeService.Default.Accent);
        PaintSwatch(SurfaceSwatch, theme.Surface ?? ThemeService.Default.Surface);
        PaintSwatch(LogBgSwatch, theme.LogBackground, "LogBackgroundBrush");
        PaintSwatch(LogTextSwatch, theme.LogText, "LogTextBrush");
        PaintSwatch(LogWarnSwatch, theme.LogWarning, "LogWarningBrush");
        PaintSwatch(LogErrorSwatch, theme.LogError, "LogErrorBrush");

        ThemeHint.Text = theme.IsDefault
            ? "Using the default colours."
            : "Custom colours. Reset puts everything back.";
    }

    /// <summary>Shows a colour on a swatch button — the stored value, or, when it is unset, whatever
    /// the theme currently derives (so an unset swatch still shows what you will get).</summary>
    private static void PaintSwatch(Button button, string? hex, string? fallbackResourceKey = null)
    {
        Brush brush;
        if (ThemeService.TryParse(hex) is { } color) brush = new SolidColorBrush(color);
        else if (fallbackResourceKey is not null && Application.Current.Resources[fallbackResourceKey] is Brush existing)
            brush = existing;
        else brush = Brushes.Transparent;
        button.Background = brush;
        button.BorderBrush = (Brush)Application.Current.Resources["BorderStrongBrush"];
        button.BorderThickness = new Thickness(1);
    }

    private void OnThemePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (ThemePresetBox.SelectedItem is not ThemeService.Preset preset) return;
        var theme = App.State.Settings.Theme;
        theme.PresetName = preset.Name;
        theme.Accent = preset.Accent;
        theme.Surface = preset.Surface;
        // A preset re-derives the log colours rather than keeping the previous theme's overrides,
        // which is what "pick a preset" means to anyone choosing one.
        theme.LogBackground = theme.LogText = theme.LogMuted = theme.LogWarning = theme.LogError = null;
        theme.LogAccent = preset.LogAccent;
        ApplyThemeChange();
    }

    private async void OnPickAccent(object sender, RoutedEventArgs e) =>
        await PickAsync("Accent colour", App.State.Settings.Theme.Accent ?? ThemeService.Default.Accent,
            hex => App.State.Settings.Theme.Accent = hex ?? ThemeService.Default.Accent);

    private async void OnPickSurface(object sender, RoutedEventArgs e) =>
        await PickAsync("Background colour", App.State.Settings.Theme.Surface ?? ThemeService.Default.Surface,
            hex => App.State.Settings.Theme.Surface = hex ?? ThemeService.Default.Surface);

    private async void OnPickLogBackground(object sender, RoutedEventArgs e) =>
        await PickAsync("Log background", App.State.Settings.Theme.LogBackground,
            hex => App.State.Settings.Theme.LogBackground = hex);

    private async void OnPickLogText(object sender, RoutedEventArgs e) =>
        await PickAsync("Log text", App.State.Settings.Theme.LogText,
            hex => App.State.Settings.Theme.LogText = hex);

    private async void OnPickLogWarning(object sender, RoutedEventArgs e) =>
        await PickAsync("Log warnings", App.State.Settings.Theme.LogWarning,
            hex => App.State.Settings.Theme.LogWarning = hex);

    private async void OnPickLogError(object sender, RoutedEventArgs e) =>
        await PickAsync("Log errors", App.State.Settings.Theme.LogError,
            hex => App.State.Settings.Theme.LogError = hex);

    /// <summary>Runs the shared colour picker and applies the result. "No colour" clears the override,
    /// which for a log colour means "derive it again".</summary>
    private async Task PickAsync(string title, string? initial, Action<string?> assign)
    {
        var choice = await ColorPickerDialog.ShowAsync(_shell, title, initial,
            ThemeService.Presets.Select(p => p.Accent));
        if (choice is null) return;
        assign(choice.Value.Hex);
        // Once a colour is set by hand it is no longer that preset.
        App.State.Settings.Theme.PresetName = null;
        ApplyThemeChange();
    }

    private void OnResetTheme(object sender, RoutedEventArgs e)
    {
        App.State.Settings.Theme = new ThemeSettings();
        ApplyThemeChange();
    }

    /// <summary>Saves, repaints the whole application, and re-reads the controls so unset swatches
    /// show the newly derived colours.</summary>
    private void ApplyThemeChange()
    {
        App.State.Settings.Save();
        ThemeService.Apply(App.State.Settings.Theme);
        _suppress = true;
        try { RefreshThemeControls(); }
        finally { _suppress = false; }
    }

    // ── default Java ─────────────────────────────────────────────────────────

    private bool _javaLoading;

    private async Task LoadDefaultJavaChoicesAsync()
    {
        _javaLoading = true;
        try
        {
            var choices = await JavaPicker.BuildChoicesAsync("Automatic  ·  the Java each Minecraft version needs (recommended)");
            JavaPicker.Apply(DefaultJavaBox, choices, App.State.Settings.DefaultJavaPath);
            UpdateDefaultJavaHint();
        }
        catch (Exception ex) { DefaultJavaHint.Text = "Could not list Java installations: " + ex.Message; }
        finally { _javaLoading = false; }
    }

    private void OnDefaultJavaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_javaLoading || _suppress) return;
        if (DefaultJavaBox.SelectedItem is not JavaChoice choice) return;

        if (choice.Kind == JavaChoiceKind.Browse)
        {
            var picked = JavaPicker.Browse(_shell);
            if (picked is not null)
            {
                App.State.Settings.DefaultJavaPath = picked;
                App.State.Settings.Save();
            }
            _ = LoadDefaultJavaChoicesAsync();
            return;
        }

        App.State.Settings.DefaultJavaPath = choice.Path;
        App.State.Settings.Save();
        UpdateDefaultJavaHint();
        StatusLabel.Text = choice.Path is null ? "Java is chosen automatically per instance." : "Default Java saved.";
    }

    private void UpdateDefaultJavaHint()
    {
        var path = App.State.Settings.DefaultJavaPath;
        DefaultJavaHint.Text = string.IsNullOrWhiteSpace(path)
            ? "Each instance launches with the Java its Minecraft version needs (Java 8, 17 or 21), downloaded on demand."
            : $"Instances without their own choice launch with {path}.";
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusLabel.Text = "Checking for updates…";
        try
        {
            var latest = await App.State.Update.GetLatestAsync();
            if (latest is null)
            {
                UpdateStatusLabel.Text = "No published builds were found on the server.";
            }
            else if (AppVersion.IsNewer(latest.Version))
            {
                UpdateStatusLabel.Text = $"Version {latest.Version} is available.";
                new UpdateDialog(latest) { Owner = _shell }.ShowDialog();
            }
            else
            {
                UpdateStatusLabel.Text = $"You're up to date (latest is {latest.Version}).";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusLabel.Text = "Couldn't check for updates: " + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnUseSidePanelToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        App.State.Settings.UseSidePanel = UseSidePanelBox.IsChecked == true;
        App.State.Settings.Save();
        _shell.RelayoutSidePanel();
        StatusLabel.Text = App.State.Settings.UseSidePanel
            ? "Pages will now open on the right."
            : "Pages will now cover the main content.";
    }

    private void OnUseCustomGameWindowToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var enabled = UseCustomGameWindowBox.IsChecked == true;
        App.State.Settings.UseCustomGameWindow = enabled;
        App.State.Settings.Save();
        CustomGameWindowOptions.IsEnabled = enabled;
        StatusLabel.Text = enabled
            ? "Minecraft will launch inside CloudLauncher's custom window."
            : "Minecraft will launch in its own native window.";
    }

    private void OnRamChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;

        var ramMb = (int)RamSlider.Value;
        RamValueLabel.Text = $"{ramMb} MB";
        App.State.Settings.DefaultMaxRamMb = ramMb;
        App.State.Settings.Save();
        StatusLabel.Text = "Default memory saved automatically.";
    }

    private void OnLauncherScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        var v = UiScale.Clamp(LauncherScaleSlider.Value);
        LauncherScaleLabel.Text = $"{v * 100:0}%";
        App.State.Settings.LauncherScale = v;
        App.State.Settings.Save();
        UiScale.NotifyChanged(); // re-applies the global zoom (and any open mod views) live
        StatusLabel.Text = "Launcher scale saved automatically.";
    }

    private void OnModScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        var v = UiScale.Clamp(ModScaleSlider.Value);
        ModScaleLabel.Text = $"{v * 100:0}%";
        App.State.Settings.ModListScale = v;
        App.State.Settings.Save();
        UiScale.NotifyChanged(); // re-applies to open list/browse views
        StatusLabel.Text = "Mod list scale saved automatically.";
    }

    private void OnMinecraftWindowKeyChanged(object sender, EventArgs e)
    {
        if (_suppress) return;
        SaveMinecraftWindowKey();
    }

    private void OnMinecraftWindowFullscreenKeyChanged(object sender, EventArgs e)
    {
        if (_suppress) return;
        SaveMinecraftWindowFullscreenKey();
    }

    private void SaveMinecraftWindowKey()
    {
        if (!LauncherKeybinds.TryGetVirtualKey(MinecraftWindowKey.Bound, out _))
        {
            StatusLabel.Text = "Choose a keyboard key for the Minecraft window shortcut.";
            return;
        }

        App.State.Settings.MinecraftWindowToggleKey = MinecraftWindowKey.Bound;
        App.State.Settings.Save();
        MinecraftWindowKeyHint.Text =
            $"{LauncherKeybinds.PrettyName(MinecraftWindowKey.Bound)} collapses bars; "
            + $"{LauncherKeybinds.PrettyName(App.State.Settings.MinecraftWindowFullscreenKey)} toggles borderless fullscreen.";
        StatusLabel.Text = "Minecraft window shortcut saved.";
    }

    private void SaveMinecraftWindowFullscreenKey()
    {
        if (!LauncherKeybinds.TryGetVirtualKey(MinecraftWindowFullscreenKey.Bound, out _))
        {
            StatusLabel.Text = "Choose a keyboard key for the borderless fullscreen shortcut.";
            return;
        }

        App.State.Settings.MinecraftWindowFullscreenKey = MinecraftWindowFullscreenKey.Bound;
        App.State.Settings.Save();
        MinecraftWindowKeyHint.Text =
            $"{LauncherKeybinds.PrettyName(App.State.Settings.MinecraftWindowToggleKey)} collapses bars; "
            + $"{LauncherKeybinds.PrettyName(MinecraftWindowFullscreenKey.Bound)} toggles borderless fullscreen.";
        StatusLabel.Text = "Borderless fullscreen shortcut saved.";
    }

    private void OnMinecraftWindowPackPageCollapsedToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        App.State.Settings.MinecraftWindowPackPageCollapsedByDefault =
            MinecraftWindowPackPageCollapsedBox.IsChecked == true;
        App.State.Settings.Save();
        StatusLabel.Text = App.State.Settings.MinecraftWindowPackPageCollapsedByDefault
            ? "Minecraft windows will open with launcher chrome hidden."
            : "Minecraft windows will open with launcher chrome visible.";
    }

    private void OnMinecraftWindowBorderlessFullscreenToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        App.State.Settings.MinecraftWindowBorderlessFullscreenByDefault =
            MinecraftWindowBorderlessFullscreenBox.IsChecked == true;
        App.State.Settings.Save();
        StatusLabel.Text = App.State.Settings.MinecraftWindowBorderlessFullscreenByDefault
            ? "Minecraft windows will open borderless fullscreen by default."
            : "Minecraft windows will open windowed by default.";
    }

    private void OnOpenMcDefaults(object sender, RoutedEventArgs e) => _shell.OpenMcDefaults();

    private void OnOpenPacksFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.State.Settings.PacksRoot);
        Process.Start(new ProcessStartInfo { FileName = App.State.Settings.PacksRoot, UseShellExecute = true });
    }

    private void OnOpenRuntimeFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.RuntimeRoot);
        Process.Start(new ProcessStartInfo { FileName = AppSettings.RuntimeRoot, UseShellExecute = true });
    }
}
