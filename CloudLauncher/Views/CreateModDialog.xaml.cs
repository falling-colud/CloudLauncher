using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class CreateModDialog : Window
{
    public HostedModSummary? Created { get; private set; }

    /// <summary>The jar the user picked to publish as this mod's first version, if any.</summary>
    /// <remarks>Not uploaded here: a version needs the mod to exist first, and the upload card lives in
    /// the shell's dialog layer. The caller opens that card with this file after a successful
    /// create.</remarks>
    public string? FirstJarPath { get; private set; }

    private bool _isCreating;

    public CreateModDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadMinecraftVersionsAsync();
        UpdateCreateButton();
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void OnPickFirstJar(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick the jar to upload as the first version",
            Filter = "Mod jars (*.jar)|*.jar|All files|*"
        };
        if (dlg.ShowDialog(this) != true) return;
        FirstJarPath = dlg.FileName;
        FirstJarLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
        ClearJarButton.Visibility = Visibility.Visible;
    }

    private void OnClearFirstJar(object sender, RoutedEventArgs e)
    {
        FirstJarPath = null;
        FirstJarLabel.Text = "Pick a jar and the upload dialog opens once the mod exists.";
        ClearJarButton.Visibility = Visibility.Collapsed;
    }

    private async Task LoadMinecraftVersionsAsync()
    {
        StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = "Loading Minecraft versions...";
        UpdateCreateButton();
        try
        {
            var versions = await App.State.Versions.ListMinecraftVersionsAsync();
            McVersionBox.ItemsSource = versions.Select(v => v.Id).ToList();
            if (McVersionBox.Items.Count > 0)
                McVersionBox.SelectedIndex = 0;
            StatusLabel.Text = "";
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Couldn't load Minecraft versions: " + ex.Message;
        }
        finally
        {
            UpdateCreateButton();
        }
    }

    private void OnFormTextChanged(object sender, TextChangedEventArgs e) => UpdateCreateButton();
    private void OnFormSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCreateButton();
    private void OnFormCheckedChanged(object sender, RoutedEventArgs e) => UpdateCreateButton();

    private void UpdateCreateButton()
    {
        if (CreateButton is null) return;
        CreateButton.IsEnabled = !_isCreating && IsFormComplete();
    }

    private bool IsFormComplete() =>
        !string.IsNullOrWhiteSpace(NameBox.Text)
        && !string.IsNullOrWhiteSpace(SummaryBox.Text)
        && VisibilityBox.SelectedItem is not null
        && McVersionBox.SelectedItem is string
        && AnyLoaderSelected();

    private bool AnyLoaderSelected() =>
        LoaderFabric.IsChecked == true
        || LoaderForge.IsChecked == true
        || LoaderNeoForge.IsChecked == true
        || LoaderQuilt.IsChecked == true;

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (!IsFormComplete())
        {
            StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Fill in the name, summary, Minecraft version, and at least one loader.";
            UpdateCreateButton();
            return;
        }

        var name = NameBox.Text.Trim();
        var summary = SummaryBox.Text.Trim();
        var mcVersion = McVersionBox.SelectedItem as string;

        var visText = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
        var vis = Enum.TryParse<PackVisibility>(visText, out var v) ? v : PackVisibility.Private;

        var loaders = new List<string>();
        if (LoaderFabric.IsChecked == true)   loaders.Add("fabric");
        if (LoaderForge.IsChecked == true)    loaders.Add("forge");
        if (LoaderNeoForge.IsChecked == true) loaders.Add("neoforge");
        if (LoaderQuilt.IsChecked == true)    loaders.Add("quilt");

        var description = DescriptionBox.Text.Trim();

        var req = new CreateModRequest(
            name,
            summary,
            string.IsNullOrWhiteSpace(description) ? null : description,
            vis,
            mcVersion,
            loaders.Count > 0 ? string.Join(',', loaders) : null);

        _isCreating = true;
        UpdateCreateButton();
        StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        StatusLabel.Text = "Creating...";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Created = await App.State.Api.CreateModAsync(req);
            DialogResult = true;
            Close();
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Failed: the server does not have hosted mod support deployed. Update/restart CloudLauncher.Server.";
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Failed: " + ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _isCreating = false;
            UpdateCreateButton();
        }
    }
}
