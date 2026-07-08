using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

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
        }
        finally { _suppress = false; }
        UpdateLabels();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppress) return;
        UpdateLabels();
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

    private void OnSave(object sender, RoutedEventArgs e)
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

        App.State.Settings.Save();
        StatusLabel.Text = "Defaults saved.";
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(_shell,
                "Reset every option above to vanilla Minecraft defaults?",
                "Reset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        App.State.Settings.McDefaults = new McDefaults();
        App.State.Settings.Save();
        LoadFromSettings();
        StatusLabel.Text = "Reset to vanilla defaults.";
    }

    private async void OnApplyToPack(object sender, RoutedEventArgs e)
    {
        try
        {
            var packs = await App.State.Api.ListPacksAsync();
            var picker = new PackPickerDialog(packs) { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not Guid id) return;

            // Save current values first, then merge into the chosen instance's options.txt.
            OnSave(sender, e);
            var pack = packs.First(p => p.Id == id);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            var gameDir = App.State.Packs.GameDir(pack.Id);
            OptionsTxtService.MergeIntoExisting(gameDir, App.State.Settings.McDefaults);
            StatusLabel.Text = $"Applied defaults to {pack.Name}.";
        }
        catch (Exception ex) { StatusLabel.Text = "Failed: " + ex.Message; }
    }
}
