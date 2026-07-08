using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class CreatePackDialog : Window
{
    public PackSummary? CreatedPack { get; private set; }

    private Dictionary<string, List<string>>? _forgeByMc;
    private List<string>? _neoforgeVersions;

    public CreatePackDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadMinecraftVersionsAsync();
    }

    private async Task LoadMinecraftVersionsAsync()
    {
        StatusLabel.Text = "Loading version list...";
        try
        {
            var versions = await App.State.Versions.ListMinecraftVersionsAsync();
            McVersionBox.ItemsSource = versions.Select(v => v.Id).ToList();
            McVersionBox.SelectedIndex = 0;
            StatusLabel.Text = "";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Couldn't load Minecraft versions: " + ex.Message;
        }
    }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (PlayablePanel is null) return;
        PlayablePanel.IsEnabled = PlayableRadio.IsChecked == true;
    }

    private async void OnMcChanged(object sender, SelectionChangedEventArgs e) => await RefreshLoaderVersionsAsync();
    private async void OnLoaderChanged(object sender, SelectionChangedEventArgs e) => await RefreshLoaderVersionsAsync();

    private async Task RefreshLoaderVersionsAsync()
    {
        if (LoaderVersionBox is null || LoaderBox is null || McVersionBox is null) return;
        var loader = GetSelectedLoader();
        var mc = McVersionBox.SelectedItem as string;
        LoaderVersionBox.ItemsSource = null;

        if (loader == LoaderKind.None || mc is null)
        {
            LoaderVersionRow.IsEnabled = false;
            return;
        }
        LoaderVersionRow.IsEnabled = true;
        StatusLabel.Text = "Loading loader versions...";
        try
        {
            switch (loader)
            {
                case LoaderKind.Fabric:
                {
                    var v = await App.State.Versions.ListFabricLoaderVersionsAsync(mc);
                    LoaderVersionBox.ItemsSource = v;
                    if (v.Count > 0) LoaderVersionBox.SelectedIndex = 0;
                    break;
                }
                case LoaderKind.Forge:
                {
                    _forgeByMc ??= await App.State.Versions.ListForgeVersionsByMinecraftAsync();
                    var list = _forgeByMc.TryGetValue(mc, out var fv) ? fv : new();
                    LoaderVersionBox.ItemsSource = list;
                    if (list.Count > 0) LoaderVersionBox.SelectedIndex = 0;
                    else StatusLabel.Text = $"Forge doesn't publish builds for Minecraft {mc}.";
                    break;
                }
                case LoaderKind.NeoForge:
                {
                    _neoforgeVersions ??= await App.State.Versions.ListNeoForgeVersionsAsync();
                    var list = _neoforgeVersions
                        .Where(v => Services.VersionService.NeoForgeVersionForMinecraft(v) == mc)
                        .ToList();
                    LoaderVersionBox.ItemsSource = list;
                    if (list.Count > 0) LoaderVersionBox.SelectedIndex = 0;
                    else StatusLabel.Text = $"NeoForge doesn't publish builds for Minecraft {mc}.";
                    break;
                }
            }
            if (LoaderVersionBox.ItemsSource is System.Collections.IEnumerable src && src.Cast<object>().Any())
                StatusLabel.Text = "";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Loader version load failed: " + ex.Message;
        }
    }

    private LoaderKind GetSelectedLoader()
    {
        if (LoaderBox.SelectedItem is ComboBoxItem cbi && cbi.Tag is string s
            && Enum.TryParse<LoaderKind>(s, out var k))
            return k;
        return LoaderKind.None;
    }

    private bool _isCreating;

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_isCreating) return; // guard against double-submit (Enter is the default button)
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Enter an instance name."; return; }

        var isEmpty = EmptyRadio.IsChecked == true;
        string? mc = null;
        LoaderKind loader = LoaderKind.None;
        string? loaderVersion = null;

        if (!isEmpty)
        {
            mc = McVersionBox.SelectedItem as string;
            if (string.IsNullOrEmpty(mc)) { StatusLabel.Text = "Pick a Minecraft version."; return; }
            loader = GetSelectedLoader();
            if (loader != LoaderKind.None)
            {
                loaderVersion = LoaderVersionBox.SelectedItem as string;
                if (string.IsNullOrEmpty(loaderVersion)) { StatusLabel.Text = "Pick a loader version."; return; }
            }
        }

        _isCreating = true;
        StatusLabel.Text = "Creating…";
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            CreatedPack = await App.State.Api.CreatePackAsync(
                new CreatePackRequest(name, null, isEmpty, mc, loader, loaderVersion));
            App.State.Packs.EnsurePackFolder(CreatedPack.Id, CreatedPack.Name, CreatedPack.IsShared);
            DialogResult = true;
            Close();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally
        {
            _isCreating = false;
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
