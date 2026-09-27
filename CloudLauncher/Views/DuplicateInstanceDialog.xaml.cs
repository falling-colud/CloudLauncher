using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Asks what to call a copy of an instance and what to bring along (see
/// <see cref="InstanceDuplicateService"/>).</summary>
public partial class DuplicateInstanceDialog : Window
{
    public InstanceDuplicateService.Options? Result { get; private set; }

    public DuplicateInstanceDialog(string sourceName, bool sourceIsHosted)
    {
        InitializeComponent();
        SourceLabel.Text = "A copy of " + sourceName;
        NameBox.Text = sourceName + " (copy)";
        NoteLabel.Text = (App.State.Api.IsSignedIn
                             ? "The copy is a new private instance on your account"
                             : "The copy is a new instance on this PC")
                         + (sourceIsHosted
                             ? ", with the files as they are on this PC. It is not hosted or shared, whatever the original is."
                             : ", with the same mods, configs and settings.");
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        var length = NameBox.Text.Trim().Length;
        OkButton.IsEnabled = length is > 0 and <= 128;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length is 0 or > 128) return;
        Result = new InstanceDuplicateService.Options(name, WorldsBox.IsChecked == true, ScreenshotsBox.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
