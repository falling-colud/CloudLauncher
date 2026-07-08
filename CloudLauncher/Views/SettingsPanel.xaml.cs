using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            UseCustomGameWindowBox.IsChecked = s.UseCustomGameWindow;
            CustomGameWindowOptions.IsEnabled = s.UseCustomGameWindow;
            MinecraftWindowKey.Bound = s.MinecraftWindowToggleKey;
            MinecraftWindowFullscreenKey.Bound = s.MinecraftWindowFullscreenKey;
            MinecraftWindowPackPageCollapsedBox.IsChecked = s.MinecraftWindowPackPageCollapsedByDefault;
            MinecraftWindowBorderlessFullscreenBox.IsChecked = s.MinecraftWindowBorderlessFullscreenByDefault;
            MinecraftWindowKeyHint.Text =
                $"{LauncherKeybinds.PrettyName(s.MinecraftWindowToggleKey)} collapses bars; "
                + $"{LauncherKeybinds.PrettyName(s.MinecraftWindowFullscreenKey)} toggles borderless fullscreen.";
            PacksFolderLabel.Text = s.PacksRoot;
            RuntimeFolderLabel.Text = AppSettings.RuntimeRoot;
            VersionLabel.Text = $"Installed version {AppVersion.CurrentString}.";
        }
        finally { _suppress = false; }
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
