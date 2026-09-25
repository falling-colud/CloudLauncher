using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The Minecraft options stamped into every new instance's <c>options.txt</c>.
/// </summary>
/// <remarks>Saves on every change, like the Settings page, so nothing is lost on navigating away.</remarks>
public partial class MCDefaultsPanel : Page
{
    private readonly MainWindow _shell;
    private bool _suppress;

    public MCDefaultsPanel(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        Loaded += (_, _) => LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _suppress = true;
        try
        {
            var d = App.State.Settings.McDefaults;
            FovSlider.Value             = d.Fov;
            RenderDistanceSlider.Value  = d.RenderDistance;
            SimDistanceSlider.Value     = d.SimulationDistance;
            BrightnessSlider.Value      = d.Brightness * 100;
            GuiScaleBox.SelectedIndex   = Math.Clamp(d.GuiScale, 0, 4);
            FullscreenBox.IsChecked     = d.Fullscreen;
            VSyncBox.IsChecked          = d.VSync;
            ViewBobbingBox.IsChecked    = d.ViewBobbing;
            AutoJumpBox.IsChecked       = d.AutoJump;
            MasterVolumeSlider.Value    = d.MasterVolume * 100;
            MusicVolumeSlider.Value     = d.MusicVolume * 100;
            SoundFxSlider.Value         = d.SoundFxVolume * 100;

            KeyForward.Bound   = d.KeyForward;
            KeyBack.Bound      = d.KeyBack;
            KeyLeft.Bound      = d.KeyLeft;
            KeyRight.Bound     = d.KeyRight;
            KeyJump.Bound      = d.KeyJump;
            KeySneak.Bound     = d.KeySneak;
            KeySprint.Bound    = d.KeySprint;
            KeyInventory.Bound = d.KeyInventory;
            KeyChat.Bound      = d.KeyChat;
            KeyDrop.Bound      = d.KeyDrop;
            KeyAttack.Bound    = d.KeyAttack;
            KeyUse.Bound       = d.KeyUse;
            KeyTogglePerspective.Bound = d.KeyTogglePerspective;
        }
        finally { _suppress = false; }
        UpdateLabels();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppress) return;
        UpdateLabels();
        SaveNow();
    }

    /// <summary>Checkbox clicks (fullscreen, VSync, view bobbing, auto-jump).</summary>
    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        SaveNow();
    }

    /// <summary>The GUI scale picker.</summary>
    private void OnOptionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        SaveNow();
    }

    /// <summary>Any of the thirteen keybind rows finishing a rebind.</summary>
    private void OnKeybindChanged(object sender, EventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        SaveNow();
    }

    private void UpdateLabels()
    {
        FovValue.Text             = $"{(int)FovSlider.Value}°";
        RenderDistanceValue.Text  = $"{(int)RenderDistanceSlider.Value} chunks";
        SimDistanceValue.Text     = $"{(int)SimDistanceSlider.Value} chunks";
        BrightnessValue.Text      = $"{(int)BrightnessSlider.Value}%";
        MasterVolumeValue.Text    = $"{(int)MasterVolumeSlider.Value}%";
        MusicVolumeValue.Text     = $"{(int)MusicVolumeSlider.Value}%";
        SoundFxValue.Text         = $"{(int)SoundFxSlider.Value}%";
    }

    /// <summary>Writes every control's current value into the stored defaults.</summary>
    /// <remarks>Called from each control's own handler, so the page never has unsaved state.</remarks>
    private void SaveNow()
    {
        var d = App.State.Settings.McDefaults;
        d.Fov                = (int)FovSlider.Value;
        d.RenderDistance     = (int)RenderDistanceSlider.Value;
        d.SimulationDistance = (int)SimDistanceSlider.Value;
        d.Brightness         = BrightnessSlider.Value / 100.0;
        d.GuiScale           = Math.Max(0, GuiScaleBox.SelectedIndex);
        d.Fullscreen         = FullscreenBox.IsChecked == true;
        d.VSync              = VSyncBox.IsChecked == true;
        d.ViewBobbing        = ViewBobbingBox.IsChecked == true;
        d.AutoJump           = AutoJumpBox.IsChecked == true;
        d.MasterVolume       = MasterVolumeSlider.Value / 100.0;
        d.MusicVolume        = MusicVolumeSlider.Value / 100.0;
        d.SoundFxVolume      = SoundFxSlider.Value / 100.0;

        d.KeyForward   = KeyForward.Bound;
        d.KeyBack      = KeyBack.Bound;
        d.KeyLeft      = KeyLeft.Bound;
        d.KeyRight     = KeyRight.Bound;
        d.KeyJump      = KeyJump.Bound;
        d.KeySneak     = KeySneak.Bound;
        d.KeySprint    = KeySprint.Bound;
        d.KeyInventory = KeyInventory.Bound;
        d.KeyChat      = KeyChat.Bound;
        d.KeyDrop      = KeyDrop.Bound;
        d.KeyAttack    = KeyAttack.Bound;
        d.KeyUse       = KeyUse.Bound;
        d.KeyTogglePerspective = KeyTogglePerspective.Bound;

        App.State.Settings.Save();
        StatusLabel.Text = "Saved.";
    }

    private async void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Reset defaults",
                    "Put every option on this page back to vanilla Minecraft's defaults?\n\n"
                    + "Instances you already created keep their own options.txt - only new ones are affected.",
                    "Reset", "Cancel", danger: true))
                return;

            App.State.Settings.McDefaults = new McDefaults();
            App.State.Settings.Save();
            LoadFromSettings();
            StatusLabel.Text = "Reset to vanilla defaults.";
        }
        catch (Exception ex) { StatusLabel.Text = "Could not reset: " + ex.Message; }
    }

    private async void OnApplyToPack(object sender, RoutedEventArgs e)
    {
        try
        {
            var packs = await App.State.Api.ListPacksAsync();
            var picker = new PackPickerDialog(packs) { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not Guid id) return;

            // The page saves as it is edited, so the stored defaults are already current.
            var pack = packs.First(p => p.Id == id);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            var gameDir = App.State.Packs.GameDir(pack.Id);
            OptionsTxtService.MergeIntoExisting(gameDir, App.State.Settings.McDefaults);
            StatusLabel.Text = $"Applied defaults to {pack.Name}.";
        }
        catch (Exception ex) { StatusLabel.Text = "Failed: " + ex.Message; }
    }
}
