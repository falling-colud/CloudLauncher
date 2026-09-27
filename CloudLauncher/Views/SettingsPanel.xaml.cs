using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class SettingsPanel : Page
{
    private readonly MainWindow _shell;
    private bool _suppress = true;

    public SettingsPanel(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Refresh();
            Window.GetWindow(this)!.PreviewKeyDown += OnShellKeyDown;
        };
        Unloaded += (_, _) =>
        {
            // Events raised while the page is torn down are not user changes.
            _suppress = true;
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= OnShellKeyDown;
        };
    }

    // ── searching the page ───────────────────────────────────────────────────

    /// <summary>Ctrl+F jumps to the filter box, as it does on every list screen.</summary>
    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SettingsSearchBox.Focus();
        SettingsSearchBox.SelectAll();
        e.Handled = true;
    }

    private void OnSettingsSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SettingsSearchBox.Text.Length == 0) return;
        SettingsSearchBox.Text = "";
        e.Handled = true;
    }

    /// <summary>Hides the setting cards that do not mention what was typed.</summary>
    /// <remarks>Matches against each card's visible text (headings, descriptions, labels, button
    /// captions), so new settings are searchable without a keyword list. Only visibility changes, so
    /// clearing the box restores the page.</remarks>
    private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SettingsSearchBox.Text?.Trim();
        var showAll = string.IsNullOrEmpty(query);
        var matches = 0;

        foreach (var child in SettingsStack.Children)
        {
            if (child is not Border card) continue;
            var hit = showAll || CollectText(card).Contains(query!, StringComparison.OrdinalIgnoreCase);
            card.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            if (hit) matches++;
        }

        NoSettingsMatchLabel.Visibility = !showAll && matches == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Every piece of readable text under an element, flattened into one string.</summary>
    private static string CollectText(DependencyObject root)
    {
        var sb = new StringBuilder();
        Walk(root, sb);
        return sb.ToString();

        static void Walk(DependencyObject d, StringBuilder into)
        {
            switch (d)
            {
                case TextBlock tb: into.Append(tb.Text).Append(' '); break;
                case ContentControl { Content: string text }: into.Append(text).Append(' '); break;
                case ComboBox combo:
                    foreach (var item in combo.Items)
                        into.Append(item is ComboBoxItem { Content: string c } ? c : item?.ToString()).Append(' ');
                    break;
            }

            // The visual tree, so text inside templates (check box labels, combo items) counts too.
            // Tooltips are left out; matching them would keep half the page visible for common words.
            var count = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(d, i), into);
        }
    }

    private void Refresh()
    {
        _suppress = true;
        try
        {
            var s = App.State.Settings;
            RamSlider.Value = s.EffectiveDefaultMaxRamMb;
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
            CurseForgeKeyTextBox.Text = s.CurseForgeApiKey ?? "";
            CurseForgeDirectBox.IsChecked = s.CurseForgeDirect;
            RefreshCurseForgeKeyHint();
            UpdateRateSlider.Value = s.EffectiveModUpdateChecksPerSecond;
            UpdateRateLabel.Text = UpdateRateText(s.EffectiveModUpdateChecksPerSecond);
            RefreshThemeControls();
            RefreshLookControls();
            PacksFolderLabel.Text = s.PacksRoot;
            PacksFolderHint.Text = Directory.Exists(s.PacksRoot)
                ? "New instances are created here. Existing ones stay where they are unless you move them."
                : "This folder does not exist yet - it is created the first time an instance needs it.";
            RuntimeFolderLabel.Text = AppSettings.RuntimeRoot;
            RefreshPrivatePaths();
            VersionLabel.Text = $"Installed version {AppVersion.CurrentString}.";
            RefreshAbout();
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

    /// <summary>Stores the user's own CurseForge key on every keystroke. It stays on this PC in its own
    /// encrypted file (see <see cref="AppSettings.SetCurseForgeApiKey"/>) and is sent with each proxied
    /// request; the server uses it for that request only.</summary>
    /// <remarks>WPF clears every PasswordBox on a page when its Frame navigates away
    /// (<c>NavigationService.FireNavigating</c> sets <c>Password = ""</c>), which raises PasswordChanged
    /// like a keystroke. So an empty box only counts as "clear the key" while the box has keyboard
    /// focus.</remarks>
    private void OnCurseForgeKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var typed = CurseForgeKeyBox.Password;
        if (typed.Length == 0 && !CurseForgeKeyBox.IsKeyboardFocusWithin) return;
        StoreCurseForgeKey(typed);
    }

    /// <summary>The revealed twin of the password box: same key, visible, saved the same way.</summary>
    private void OnCurseForgeKeyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        StoreCurseForgeKey(CurseForgeKeyTextBox.Text);
    }

    private bool _curseForgeKeyWriteFailed;

    private void StoreCurseForgeKey(string? typed)
    {
        _curseForgeKeyWriteFailed = !App.State.Settings.SetCurseForgeApiKey(typed);
        _curseForgeKeyTested = null;
        RefreshCurseForgeKeyHint();
    }

    /// <summary>Swaps the dots for the key itself and back, so the user can see what was saved.</summary>
    private void OnRevealCurseForgeKey(object sender, RoutedEventArgs e)
    {
        var reveal = CurseForgeKeyTextBox.Visibility != Visibility.Visible;
        _suppress = true;
        try
        {
            var key = App.State.Settings.CurseForgeApiKey ?? "";
            CurseForgeKeyTextBox.Text = key;
            CurseForgeKeyBox.Password = key;
        }
        finally { _suppress = false; }

        CurseForgeKeyTextBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        CurseForgeKeyBox.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        RevealCurseForgeKeyButton.ToolTip = reveal ? "Hide the key" : "Show the key";
        RevealCurseForgeKeyButton.Content = reveal ? "" : "";
    }

    /// <summary>The last test's verdict for the key that is currently saved; cleared by any edit.</summary>
    private string? _curseForgeKeyTested;
    private bool _testingCurseForgeKey;

    /// <summary>Asks CurseForge, through the launcher server, whether it accepts the saved key.</summary>
    /// <remarks>The search text is random because the server answers repeated requests from its cache
    /// without checking the key, so a fixed query could report a rejected key as working.</remarks>
    private async void OnTestCurseForgeKey(object sender, RoutedEventArgs e)
    {
        var key = App.State.Settings.CurseForgeApiKey;
        if (key is not { Length: > 0 })
        {
            _curseForgeKeyTested = "There is no key to test - paste yours into the box first.";
            RefreshCurseForgeKeyHint();
            return;
        }
        // When sent straight to CurseForge the key never reaches the launcher server, so plain http
        // to the server doesn't matter.
        if (!App.State.Api.IsCurseForgeDirect
            && Uri.TryCreate(App.State.Settings.ServerUrl, UriKind.Absolute, out var server)
            && server.Scheme != Uri.UriSchemeHttps && !server.IsLoopback)
        {
            _curseForgeKeyTested = "The launcher server is on plain http, so your key is not sent to it at all.";
            RefreshCurseForgeKeyHint();
            return;
        }

        _testingCurseForgeKey = true;
        _curseForgeKeyTested = "Testing...";
        RefreshCurseForgeKeyHint();
        try
        {
            var probe = "cl-key-test-" + Guid.NewGuid().ToString("N")[..10];
            using var resp = await App.State.Api.ProxyAsync("curseforge", System.Net.Http.HttpMethod.Get,
                $"mods/search?gameId=432&classId=6&pageSize=1&searchFilter={probe}");
            if (!ReferenceEquals(key, App.State.Settings.CurseForgeApiKey)) return; // edited meanwhile
            var body = resp.IsSuccessStatusCode ? "" : await resp.Content.ReadAsStringAsync();
            _curseForgeKeyTested = (int)resp.StatusCode switch
            {
                >= 200 and < 300 => "✓ CurseForge accepted this key.",
                400 when body.Contains("own_key_rejected", StringComparison.Ordinal)
                    => "✗ CurseForge rejected this key. Check it was copied whole, or create a new one.",
                429 => "CurseForge is busy right now, so the key could not be tested. Try again in a minute.",
                401 => "Sign in to the launcher first, then test the key.",
                var code => $"The test did not get an answer ({code}). Try again in a minute."
            };
        }
        catch (Exception ex)
        {
            _curseForgeKeyTested = "The test did not get an answer: " + ex.Message;
        }
        finally
        {
            _testingCurseForgeKey = false;
            RefreshCurseForgeKeyHint();
        }
    }

    private void RefreshCurseForgeKeyHint()
    {
        var key = App.State.Settings.CurseForgeApiKey;
        var text = key is { Length: > 0 }
            ? $"Saved. Using your own key ({key.Length} characters), "
              + (App.State.Api.IsCurseForgeDirect ? "sent straight to CurseForge" : "through the launcher server")
              + ". Clear the box to go back to the shared one."
            : "Using the launcher's shared key. Set your own if CurseForge searches or updates keep being refused when several people are using the launcher at once.";
        if (_curseForgeKeyWriteFailed || App.State.Settings.CurseForgeKeyInSettingsFile)
            text = "Saved in settings.json instead of the encrypted store, which this PC refused"
                   + (SecretStore.LastError is { } why ? $" ({why})" : "") + ".";
        if (_curseForgeKeyTested is not null) text += "\n" + _curseForgeKeyTested;
        CurseForgeKeyHint.Text = text;
        TestCurseForgeKeyButton.IsEnabled = key is { Length: > 0 } && !_testingCurseForgeKey;
        CurseForgeDirectBox.IsEnabled = key is { Length: > 0 };
    }

    /// <summary>Own key set: go straight to CurseForge or through the launcher server's queue.</summary>
    private void OnCurseForgeDirectToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        App.State.Settings.CurseForgeDirect = CurseForgeDirectBox.IsChecked == true;
        App.State.Settings.Save();
        _curseForgeKeyTested = null;
        RefreshCurseForgeKeyHint();
        StatusLabel.Text = App.State.Settings.CurseForgeDirect
            ? "CurseForge requests now go straight to CurseForge with your key."
            : "CurseForge requests go through the launcher server again.";
    }

    // ── update checks ────────────────────────────────────────────────────────

    private static string UpdateRateText(int perSecond) => $"{perSecond} / s";

    private void OnUpdateRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (UpdateRateLabel is not null) UpdateRateLabel.Text = UpdateRateText((int)UpdateRateSlider.Value);
        if (_suppress) return;
        App.State.Settings.ModUpdateChecksPerSecond = (int)UpdateRateSlider.Value;
        App.State.Settings.Save();
    }

    private void OnOpenCurseForgeConsole(object sender, RoutedEventArgs e) =>
        OpenInBrowser("https://console.curseforge.com/?#/api-keys");

    /// <summary>Opens a web page, saying so on the status line when the browser could not be
    /// started.</summary>
    private void OpenInBrowser(string url)
    {
        if (!SafeLaunch.OpenUrl(url)) StatusLabel.Text = $"Could not open the browser. The address is {url}";
    }

    // ── changelog ────────────────────────────────────────────────────────────

    /// <summary>Opens the release notes for every published version.</summary>
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
        PaintSwatch(DangerSwatch, theme.Danger, "DangerBrush");
        PaintSwatch(SuccessSwatch, theme.Success, "SuccessBrush");
        PaintSwatch(WarningSwatch, theme.Warning, "LogWarningBrush");
        PaintSwatch(LogMutedSwatch, theme.LogMuted, "LogMutedBrush");
        PaintSwatch(LogAccentSwatch, theme.LogAccent, "LogAccentBrush");
        PaintSwatch(LogBgSwatch, theme.LogBackground, "LogBackgroundBrush");
        PaintSwatch(LogTextSwatch, theme.LogText, "LogTextBrush");
        PaintSwatch(LogWarnSwatch, theme.LogWarning, "LogWarningBrush");
        PaintSwatch(LogErrorSwatch, theme.LogError, "LogErrorBrush");

        ThemeHint.Text = theme.IsDefault
            ? "Using the default colours."
            : "Custom colours. Reset puts everything back.";
    }

    /// <summary>Shows a colour on a swatch button: the stored value, or when unset, the colour the
    /// theme currently derives.</summary>
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
        // A preset re-derives every colour it doesn't set instead of keeping the previous theme's
        // overrides, including state and log colours, so nothing clashes with the new accent.
        theme.Danger = theme.Success = theme.Warning = null;
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

    private async void OnPickDanger(object sender, RoutedEventArgs e) =>
        await PickAsync("Danger colour", App.State.Settings.Theme.Danger,
            hex => App.State.Settings.Theme.Danger = hex);

    private async void OnPickSuccess(object sender, RoutedEventArgs e) =>
        await PickAsync("Success colour", App.State.Settings.Theme.Success,
            hex => App.State.Settings.Theme.Success = hex);

    private async void OnPickWarning(object sender, RoutedEventArgs e) =>
        await PickAsync("Warning colour", App.State.Settings.Theme.Warning,
            hex => App.State.Settings.Theme.Warning = hex);

    private async void OnPickLogMuted(object sender, RoutedEventArgs e) =>
        await PickAsync("Muted log text", App.State.Settings.Theme.LogMuted,
            hex => App.State.Settings.Theme.LogMuted = hex);

    private async void OnPickLogAccent(object sender, RoutedEventArgs e) =>
        await PickAsync("Log highlights", App.State.Settings.Theme.LogAccent,
            hex => App.State.Settings.Theme.LogAccent = hex);

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
        ThemeService.ApplyLook(App.State.Settings);
        _suppress = true;
        try { RefreshThemeControls(); }
        finally { _suppress = false; }
    }

    // ── appearance: Slate or Classic ─────────────────────────────────────────

    /// <summary>One accent swatch. The fill is a fixed colour per preset, not a theme brush, so it is
    /// safe to hold (theme brushes are replaced on every apply).</summary>
    public sealed record AccentSwatchRow(string Name, string Hex, Brush Fill, bool IsSelected);

    private void RefreshLookControls()
    {
        var look = App.State.Settings.Look ??= new LookSettings();
        RefreshPixelFontOptions(look);
        StyleSlateRadio.IsChecked = look.IsSlate;
        StyleClassicRadio.IsChecked = !look.IsSlate;
        SlateOptionsPanel.Visibility = look.IsSlate ? Visibility.Visible : Visibility.Collapsed;
        ClassicColoursPanel.Visibility = look.IsSlate ? Visibility.Collapsed : Visibility.Visible;

        SkinDarkRadio.IsChecked = !look.IsVanilla;
        SkinVanillaRadio.IsChecked = look.IsVanilla;

        var accent = ThemeService.Parse(look.Accent, LookSettings.DefaultAccent);
        var swatches = ThemeService.SlateAccents
            .Select(a =>
            {
                var brush = new SolidColorBrush(ThemeService.Parse(a.Hex, LookSettings.DefaultAccent));
                brush.Freeze();
                return new AccentSwatchRow(a.Name, a.Hex, brush, SameColour(a.Hex, look.Accent));
            })
            .ToList();
        AccentSwatches.ItemsSource = swatches;
        var named = swatches.FirstOrDefault(r => r.IsSelected);
        AccentNameLabel.Text = named is not null ? named.Name : $"Custom {ThemeService.ToHex(accent)}";

        // The Vanilla skin is always square, so the radius slider is off for it.
        RadiusSlider.Value = Math.Clamp(look.Radius, 0, LookSettings.MaxRadius);
        RadiusSlider.IsEnabled = !look.IsVanilla;
        RadiusLabel.Text = look.IsVanilla ? "Square" : RadiusText(look.Radius);
        MotionSlider.Value = Math.Clamp(look.Motion, 0, 2);
        MotionLabel.Text = MotionText(look.Motion);

        PixelHeadingsBox.IsChecked = look.PixelHeadings;
        PixelTextBox.IsChecked = look.PixelText;
        PixelIconsBox.IsChecked = look.PixelIcons;
        ShadowsBox.IsChecked = look.Shadows;
        TransitionsBox.IsChecked = look.Transitions;
        UiSoundsBox.IsChecked = look.UiSounds;

        LookHint.Text = look.IsSlate
            ? "Classic keeps its own colours, so you can switch back and forth."
            : "";
    }

    private static bool SameColour(string a, string? b) =>
        ThemeService.TryParse(a) is { } x && ThemeService.TryParse(b) is { } y && x == y;

    private static string RadiusText(int steps) => steps <= 0 ? "Square" : $"{steps} px";

    private static string MotionText(double m) => m switch
    {
        <= 0 => "Off",
        < 0.99 => $"Fast ({m:0.##}×)",
        <= 1.01 => "Normal",
        _ => $"Slow ({m:0.##}×)",
    };

    /// <summary>One segment per pixel font, labelled in that font; built on first use.</summary>
    private void RefreshPixelFontOptions(LookSettings look)
    {
        if (PixelFontOptions.Children.Count == 0)
        {
            foreach (var spec in ThemeService.PixelFonts)
            {
                var option = new RadioButton
                {
                    Style = (Style)FindResource("SegmentRadio"),
                    GroupName = "PixelFont",
                    Tag = spec.Family,
                    Margin = new Thickness(0, 0, 8, 8),
                    ToolTip = spec.Family == LookSettings.PixeloidFont ? spec.Family + " (the default)" : spec.Family,
                    Content = new TextBlock
                    {
                        Text = spec.Family,
                        FontFamily = ThemeService.PixelFamily(spec, "Segoe UI"),
                        FontSize = spec.Brand,
                    },
                };
                option.Checked += OnPixelFontChecked;
                PixelFontOptions.Children.Add(option);
            }
        }
        var chosen = ThemeService.PixelFontFor(look.PixelFont).Family;
        foreach (RadioButton option in PixelFontOptions.Children)
            option.IsChecked = (string)option.Tag == chosen;
    }

    private void OnPixelFontChecked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not RadioButton { Tag: string family }) return;
        App.State.Settings.Look.PixelFont = family;
        ApplyLookChange();
    }

    private void ApplyLookChange()
    {
        App.State.Settings.Save();
        ThemeService.ApplyLook(App.State.Settings);
        _suppress = true;
        try
        {
            RefreshLookControls();
            RefreshThemeControls();
        }
        finally { _suppress = false; }
    }

    private void OnLookStyleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        App.State.Settings.Look.Style = StyleClassicRadio.IsChecked == true
            ? LookSettings.ClassicStyle
            : LookSettings.SlateStyle;
        ApplyLookChange();
        StatusLabel.Text = App.State.Settings.Look.IsSlate
            ? "Slate style on."
            : "Classic style on. Your Slate options are kept for when you switch back.";
    }

    private void OnLookSkinChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        App.State.Settings.Look.Skin = SkinVanillaRadio.IsChecked == true
            ? LookSettings.VanillaSkin
            : LookSettings.DarkSkin;
        ApplyLookChange();
    }

    private void OnPickSlateAccent(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccentSwatchRow row }) return;
        App.State.Settings.Look.Accent = row.Hex;
        ApplyLookChange();
    }

    private async void OnPickCustomSlateAccent(object sender, RoutedEventArgs e)
    {
        try
        {
            var choice = await ColorPickerDialog.ShowAsync(_shell, "Accent colour",
                App.State.Settings.Look.Accent, ThemeService.SlateAccents.Select(a => a.Hex));
            if (choice is null) return;
            App.State.Settings.Look.Accent = choice.Value.Hex ?? LookSettings.DefaultAccent;
            ApplyLookChange();
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open the colour picker: " + ex.Message; }
    }

    private void OnLookRadiusChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RadiusLabel is not null) RadiusLabel.Text = RadiusText((int)RadiusSlider.Value);
        if (_suppress) return;
        App.State.Settings.Look.Radius = (int)RadiusSlider.Value;
        ApplyLookChange();
    }

    private void OnLookMotionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MotionLabel is not null) MotionLabel.Text = MotionText(MotionSlider.Value);
        if (_suppress) return;
        App.State.Settings.Look.Motion = MotionSlider.Value;
        // Motion only changes timings; nothing has to be repainted for it.
        App.State.Settings.Save();
        ThemeService.ApplyLook(App.State.Settings);
    }

    private void OnLookToggle(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var look = App.State.Settings.Look;
        look.PixelHeadings = PixelHeadingsBox.IsChecked == true;
        look.PixelText = PixelTextBox.IsChecked == true;
        look.PixelIcons = PixelIconsBox.IsChecked == true;
        look.Shadows = ShadowsBox.IsChecked == true;
        look.Transitions = TransitionsBox.IsChecked == true;
        look.UiSounds = UiSoundsBox.IsChecked == true;
        ApplyLookChange();
    }

    private void OnResetLook(object sender, RoutedEventArgs e)
    {
        App.State.Settings.Look = new LookSettings();
        ApplyLookChange();
        StatusLabel.Text = "Slate options reset to the defaults.";
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
        UpdateStatusLabel.Text = "Checking for updates...";
        try
        {
            var latest = await App.State.Update.GetLatestAsync();
            if (latest is null)
            {
                UpdateStatusLabel.Text = "No published builds were found on the server.";
            }
            else if (AppVersion.IsNewer(latest.Version))
            {
                UpdateStatusLabel.Text = UpdateVerifier.Verify(latest).IsValid
                    ? $"Version {latest.Version} is available."
                    : $"Version {latest.Version} is out, but it could not be verified.";
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
        App.State.Settings.DefaultMaxRamChosen = true;
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

    private void OnOpenPacksFolder(object sender, RoutedEventArgs e) => OpenFolder(App.State.Settings.PacksRoot);

    private void OnOpenRuntimeFolder(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.RuntimeRoot);

    /// <summary>Opens a folder in Explorer, creating it first so a fresh install has something to
    /// show.</summary>
    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not create {path}: {ex.Message}";
            return;
        }
        if (!SafeLaunch.OpenFolder(path)) StatusLabel.Text = $"Could not open {path} in Explorer.";
    }

    // ── about ────────────────────────────────────────────────────────────────

    private const string WebsiteUrl = "https://cloudlauncher.co";

    private void RefreshAbout()
    {
        AboutVersionLabel.Text = $"Version {AppVersion.CurrentString}";
        AboutWebsiteButton.ToolTip = WebsiteUrl;
        AboutPrivacyButton.ToolTip = App.State.Api.LegalPageUrl(Legal.PrivacyPath);
        AboutTermsButton.ToolTip = App.State.Api.LegalPageUrl(Legal.TermsPath);

        var icon = AppIcon.Value;
        AboutIcon.Source = icon;
        AboutIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        AboutIconFallback.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The icon embedded in the running exe, read once.</summary>
    private static readonly Lazy<ImageSource?> AppIcon = new(LoadAppIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string iconFile, int index, uint flags,
        out IntPtr largeIcon, IntPtr smallIcon, uint iconSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private static ImageSource? LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
            // 64 px, so it stays sharp at the 48 px it is shown at on a scaled display.
            if (SHDefExtractIcon(exe, 0, 0, out var handle, IntPtr.Zero, 64) != 0 || handle == IntPtr.Zero)
                return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DestroyIcon(handle); }
        }
        catch (Exception ex)
        {
            AppLog.LogError("about-icon", ex);
            return null;
        }
    }

    private void OnOpenWebsite(object sender, RoutedEventArgs e) => OpenInBrowser(WebsiteUrl);

    private void OnOpenDiscord(object sender, RoutedEventArgs e) => OpenInBrowser(Legal.DiscordUrl);

    private void OnOpenPrivacy(object sender, RoutedEventArgs e) =>
        OpenInBrowser(App.State.Api.LegalPageUrl(Legal.PrivacyPath));

    private void OnOpenTerms(object sender, RoutedEventArgs e) =>
        OpenInBrowser(App.State.Api.LegalPageUrl(Legal.TermsPath));

    private void OnOpenThirdPartyNotices(object sender, RoutedEventArgs e)
    {
        if (!SafeLaunch.OpenFile(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt")))
            StatusLabel.Text = "The third-party notices file is missing from this installation.";
    }

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        // Whatever is still queued goes to the file first, so the newest lines are there to read.
        AppLog.Flush();
        OpenFolder(AppLog.LogFolder);
    }

    /// <summary>Starts an email to the developer with the version details already filled in.</summary>
    private void OnReportProblem(object sender, RoutedEventArgs e)
    {
        var version = AppVersion.CurrentString;
        var body = $"CloudLauncher version: {version}\r\n"
                   + $"Windows version: {RuntimeInformation.OSDescription}\r\n"
                   + "\r\n"
                   + "What happened?\r\n";
        if (!SafeLaunch.OpenMail(Legal.ContactEmail, $"CloudLauncher {version} problem", body))
            StatusLabel.Text = $"No email app opened. You can write to {Legal.ContactEmail} instead.";
    }

    // -- files that never leave this PC --------------------------------------

    private readonly ObservableCollection<PrivatePathRow> _privatePaths = new();

    /// <summary>Rebuilds the never-upload list: the built-in entries first, then the user's own.</summary>
    private void RefreshPrivatePaths()
    {
        if (PrivatePathsList.ItemsSource is null) PrivatePathsList.ItemsSource = _privatePaths;
        _privatePaths.Clear();
        foreach (var p in PrivateAssetPolicy.BuiltIn) _privatePaths.Add(new PrivatePathRow(p, builtIn: true));
        foreach (var p in App.State.Settings.PrivatePathPatterns ?? [])
        {
            var trimmed = p?.Trim().Replace(Path.DirectorySeparatorChar, '/');
            if (!string.IsNullOrEmpty(trimmed)) _privatePaths.Add(new PrivatePathRow(trimmed, builtIn: false));
        }

        var mine = _privatePaths.Count(r => !r.IsBuiltIn);
        PrivatePathsHint.Text = mine == 0
            ? "You have not added any patterns of your own."
            : $"{mine} pattern{(mine == 1 ? "" : "s")} of your own, on top of the built-in ones.";
    }

    private async void OnAddPrivatePath(object sender, RoutedEventArgs e)
    {
        try
        {
            var pattern = await _shell.PromptAsync("Keep a path on this PC",
                "Path inside an instance's game folder (end with / for a whole folder)");
            if (pattern is null) return;

            var clean = pattern.Trim().Replace('\\', '/').TrimStart('/');
            if (clean.Length == 0) return;
            if (_privatePaths.Any(r => string.Equals(r.Pattern, clean, StringComparison.OrdinalIgnoreCase)))
            {
                StatusLabel.Text = $"{clean} is already on the list.";
                return;
            }

            (App.State.Settings.PrivatePathPatterns ??= []).Add(clean);
            App.State.Settings.Save();
            RefreshPrivatePaths();
            StatusLabel.Text = $"{clean} will stay on this PC.";
        }
        catch (Exception ex) { StatusLabel.Text = "Could not add the pattern: " + ex.Message; }
    }

    private async void OnRemovePrivatePath(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { DataContext: PrivatePathRow row } || row.IsBuiltIn) return;
            if (!await AppDialog.ConfirmAsync(_shell, "Stop protecting this path",
                    $"Files under {row.Pattern} will be uploaded with any shared instance that contains them, "
                    + "and a sync from the server will be able to overwrite them.",
                    "Remove", "Cancel", danger: true))
                return;

            App.State.Settings.PrivatePathPatterns?.RemoveAll(
                p => string.Equals(p?.Trim().Replace('\\', '/'), row.Pattern, StringComparison.OrdinalIgnoreCase));
            App.State.Settings.Save();
            RefreshPrivatePaths();
            StatusLabel.Text = $"{row.Pattern} is no longer protected.";
        }
        catch (Exception ex) { StatusLabel.Text = "Could not remove the pattern: " + ex.Message; }
    }

    // -- moving the instances folder -----------------------------------------

    /// <summary>Points the launcher at a different instances folder, optionally moving the existing
    /// instances.</summary>
    /// <remarks>The copy runs off the UI thread with per-folder progress, since the folder can be tens
    /// of gigabytes. If a folder can't be moved (usually Minecraft has a file open), the move is rolled
    /// back: <see cref="AppSettings.PacksRoot"/> would still point at the old folder, orphaning
    /// whatever had already moved.</remarks>
    private async void OnChangePacksFolder(object sender, RoutedEventArgs e)
    {
        try { await ChangePacksFolderAsync(); }
        catch (Exception ex) { StatusLabel.Text = "Could not change the instances folder: " + ex.Message; }
        finally { ChangePacksFolderButton.IsEnabled = true; }
    }

    private async Task ChangePacksFolderAsync()
    {
        var current = App.State.Settings.PacksRoot;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder for your instances",
            InitialDirectory = Directory.Exists(current) ? current : AppSettings.RuntimeRoot
        };
        if (dialog.ShowDialog(_shell) != true) return;

        var chosen = Path.GetFullPath(dialog.FolderName);
        if (PathsEqual(chosen, current)) { StatusLabel.Text = "That is already the instances folder."; return; }
        if (IsInside(chosen, current))
        {
            await AppDialog.MessageAsync(_shell, "Pick a folder outside this one",
                "The new folder is inside the current instances folder, so moving would copy it into itself.");
            return;
        }

        var sources = Directory.Exists(current)
            ? Directory.GetDirectories(current)
            : [];

        var move = false;
        if (sources.Length > 0)
        {
            move = await AppDialog.ConfirmAsync(_shell, "Move your instances?",
                $"{sources.Length} instance folder{(sources.Length == 1 ? "" : "s")} live in the old location.\n\n"
                + "Move them across, or leave them behind and start fresh in the new folder?",
                "Move them", "Leave them", danger: false);
        }

        ChangePacksFolderButton.IsEnabled = false;
        Directory.CreateDirectory(chosen);

        var duplicated = new List<string>();
        if (move)
        {
            var moved = new List<MovedInstance>();
            try
            {
                await Task.Run(() =>
                {
                    foreach (var source in sources)
                    {
                        var name = Path.GetFileName(source);
                        var destination = Path.Combine(chosen, name);
                        if (Directory.Exists(destination))
                            throw new IOException($"'{name}' already exists in the new folder.");

                        if (!MoveInstanceFolder(source, destination)) duplicated.Add(name);
                        moved.Add(new MovedInstance(name, source, destination));
                        var done = moved.Count;
                        Dispatcher.Invoke(() =>
                            StatusLabel.Text = $"Moving instances... {done} of {sources.Length}");
                    }
                });
            }
            catch (Exception ex)
            {
                // Anything already moved is orphaned while PacksRoot still names the old folder, so
                // put it back. If that fails too, name each one and where it is now. Across volumes
                // the undo is a copy, so tell the user what is happening.
                StatusLabel.Text = "Putting the instances that had already moved back...";
                var stranded = await Task.Run(() => RollBackMove(moved));
                var count = moved.Count;
                StatusLabel.Text = stranded.Count == 0
                    ? $"Stopped after {count} of {sources.Length}; those were moved back. {ex.Message}"
                    : $"Stopped after {count} of {sources.Length}; {stranded.Count} could not be moved back.";
                await AppDialog.MessageAsync(_shell, "The move stopped",
                    $"{count} of {sources.Length} instance folders were moved before this failed:\n\n{ex.Message}\n\n"
                    + (stranded.Count == 0
                        ? "Close Minecraft if an instance is running, then try again. Everything that had "
                          + "already moved has been put back and the instances folder has not been changed, "
                          + "so nothing is lost."
                        : "Close Minecraft if an instance is running, then try again. The instances folder "
                          + "has not been changed, but these could not be put back and are now in the new "
                          + "folder:\n\n" + string.Join("\n", stranded.Take(20))
                          + "\n\nMove them back into\n" + current + "\nby hand, or point the launcher at the "
                          + "new folder and move the rest across too."));
                return;
            }
        }

        App.State.Settings.PacksRoot = chosen;
        App.State.Settings.Save();
        Refresh();

        // Pack folder lookups are cached with paths under the old root. Dropping the cache makes the
        // next lookup re-scan the new root for each pack's .packid marker, so no restart is needed.
        App.State.Packs.InvalidateRootCache();
        // Every content page, not only Instances: they are cached between navigations and hold rows
        // keyed by paths under the old root.
        _shell.OnInstancesRootMoved();

        StatusLabel.Text = $"Instances now live in {chosen}." +
            (duplicated.Count > 0
                ? $" {duplicated.Count} folder(s) could not be cleared out of the old location - a second " +
                  "copy is still there and can be deleted once Minecraft is closed."
                : "");
    }

    /// <summary>One instance folder that has already been moved, kept so the move can be undone.</summary>
    private readonly record struct MovedInstance(string Name, string Source, string Destination);

    /// <summary>Moves one instance folder. Returns false when the data arrived at
    /// <paramref name="destination"/> but the original could not be deleted, leaving a duplicate
    /// behind.</summary>
    /// <remarks><see cref="Directory.Move"/> can't cross volumes (ERROR_NOT_SAME_DEVICE), and moving to
    /// another drive is the common case, so the manual copy below usually runs. A copy that fails
    /// partway deletes its half-written destination, since a partial folder looks like a real
    /// instance.</remarks>
    private static bool MoveInstanceFolder(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (IOException ex) when (IsCrossVolume(ex, source, destination))
        {
            // Fall through to the copy below.
        }

        try
        {
            CopyDirectory(source, destination);
        }
        catch
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true); }
            catch { /* best effort; the exception below is what the user needs to see */ }
            throw;
        }

        // The data is safely at the destination, so a locked file in the old folder is reported as a
        // leftover copy rather than failing the move and triggering a rollback.
        try { Directory.Delete(source, recursive: true); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        return true;
    }

    /// <summary>ERROR_NOT_SAME_DEVICE, or simply two different volumes.</summary>
    /// <remarks>The HResult check is the precise one; the root comparison is there because a mapped
    /// drive or a mount point can surface the same condition under a different code.</remarks>
    private static bool IsCrossVolume(IOException ex, string source, string destination)
    {
        const int ErrorNotSameDevice = unchecked((int)0x80070011);
        if (ex.HResult == ErrorNotSameDevice) return true;
        var from = Path.GetPathRoot(Path.GetFullPath(source));
        var to = Path.GetPathRoot(Path.GetFullPath(destination));
        return !string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recursive copy used when a move has to cross volumes.</summary>
    /// <remarks>Junctions and symlinks are skipped rather than followed: a junction back into the
    /// folder would recurse forever, and the data behind a junction doesn't live here anyway.</remarks>
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source))
        {
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>Puts back everything a failed move had already moved. Returns "name -> where it is
    /// now" for anything that could not be put back, so the message can name them.</summary>
    private static List<string> RollBackMove(IReadOnlyList<MovedInstance> moved)
    {
        var stranded = new List<string>();
        foreach (var item in moved)
        {
            try
            {
                // A cross-volume move that could not delete its source left the original in place,
                // so the destination is the duplicate and dropping it is the whole undo.
                if (Directory.Exists(item.Source) && Directory.Exists(item.Destination))
                {
                    Directory.Delete(item.Destination, recursive: true);
                    continue;
                }
                MoveInstanceFolder(item.Destination, item.Source);
            }
            catch { stranded.Add($"{item.Name} > {item.Destination}"); }
        }
        return stranded;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> sits under <paramref name="parent"/>.</summary>
    private static bool IsInside(string candidate, string parent)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One row of the never-upload list. Built-in entries cannot be removed.</summary>
public sealed class PrivatePathRow(string pattern, bool builtIn)
{
    public string Pattern { get; } = pattern;
    public bool IsBuiltIn { get; } = builtIn;
    public Visibility BuiltInVisibility => IsBuiltIn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => IsBuiltIn ? Visibility.Collapsed : Visibility.Visible;
}
