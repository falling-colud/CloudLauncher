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
        UpdateSummaryCounter();
        Loaded += async (_, _) => await LoadMinecraftVersionsAsync();
    }

    private void OnSummaryChanged(object sender, TextChangedEventArgs e) => UpdateSummaryCounter();

    /// <summary>Counts down to the server's summary limit, and turns red once over it.</summary>
    /// <remarks>The server truncates silently, so without this the first sign that a long summary
    /// was cut is seeing it cut in the browser.</remarks>
    private void UpdateSummaryCounter()
    {
        if (SummaryCounter is null || SummaryBox is null) return;
        var used = SummaryBox.Text.Length;
        var over = used > PackText.SummaryMaxLength;
        SummaryCounter.Text = over
            ? $"{used - PackText.SummaryMaxLength} over the limit"
            : $"{used}/{PackText.SummaryMaxLength}";
        SummaryCounter.SetResourceReference(TextBlock.ForegroundProperty,
            over ? "DangerBrush" : "TextTertiaryBrush");
    }

    private void OnVisibilityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VisibilityNote is null) return;
        VisibilityNote.Visibility = GetSelectedVisibility() == PackVisibility.Private
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private PackVisibility GetSelectedVisibility() =>
        VisibilityBox?.SelectedItem is ComboBoxItem { Tag: string tag }
        && Enum.TryParse<PackVisibility>(tag, out var v)
            ? v
            : PackVisibility.Private;

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

        var summary = SummaryBox.Text.Trim();
        if (summary.Length > PackText.SummaryMaxLength)
        {
            StatusLabel.Text = $"The summary is {summary.Length - PackText.SummaryMaxLength} characters too long.";
            SummaryBox.Focus();
            return;
        }

        var visibility = GetSelectedVisibility();

        _isCreating = true;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            // Pressing Create again after the publish step failed must not make a second instance:
            // the first press already created one and only the visibility call went wrong.
            if (CreatedPack is null)
            {
                StatusLabel.Text = "Creating…";
                CreatedPack = await App.State.Api.CreatePackAsync(
                    new CreatePackRequest(name, null, isEmpty, mc, loader, loaderVersion,
                        Summary: summary.Length == 0 ? null : summary));
                App.State.Packs.EnsurePackFolder(CreatedPack.Id, CreatedPack.Name, CreatedPack.IsShared);
            }

            if (visibility != PackVisibility.Private && !await TryPublishAsync(CreatedPack, visibility))
                return; // message is on the status line; Create now retries just the publish

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

    /// <summary>
    /// Applies a non-Private visibility to the instance that was just created, turning server
    /// hosting on with it.
    /// </summary>
    /// <remarks>
    /// Create has no visibility field, so publishing is a second call. Hosting has to go with it:
    /// visibility and hosting are independent flags, and a pack that is Public but not hosted shows
    /// up in everyone's browser with an Add button that hands them an empty instance.
    /// Returning false leaves the created instance in place and Private, which is the safe end of
    /// the mistake, and lets the user press Create again to retry only this step.
    /// </remarks>
    private async Task<bool> TryPublishAsync(PackSummary pack, PackVisibility visibility)
    {
        StatusLabel.Text = "Publishing…";
        try
        {
            var published = await App.State.Api.UpdatePackAsync(pack.Id,
                new UpdatePackRequest(null, null, visibility, IsShared: true, null, null, null, null));
            App.State.Packs.EnsurePackFolder(published.Id, published.Name, published.IsShared);
            CreatedPack = published;
            return true;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"'{pack.Name}' was created but couldn't be published: {ex.Message} "
                               + "It is private for now — press Create to try publishing again, "
                               + "or Cancel and publish it later from its Options.";
            return false;
        }
    }

    /// <remarks>Once the instance exists on the server, Cancel can only decline the publish step —
    /// it cannot un-create it. Report success so the caller still lists the new instance instead of
    /// leaving it invisible until the next refresh.</remarks>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = CreatedPack is not null;
        Close();
    }
}
