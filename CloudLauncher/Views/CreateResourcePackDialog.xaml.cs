using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class CreateResourcePackDialog : Window
{
    public HostedResourcePackSummary? Created { get; private set; }
    private bool _isCreating;

    public CreateResourcePackDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadMinecraftVersionsAsync();
        UpdateCreateButton();
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private async Task LoadMinecraftVersionsAsync()
    {
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
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
            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            StatusLabel.Text = "Couldn't load Minecraft versions: " + ex.Message;
        }
        finally { UpdateCreateButton(); }
    }

    private void OnFormTextChanged(object sender, TextChangedEventArgs e) => UpdateCreateButton();
    private void OnFormSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCreateButton();

    private void UpdateCreateButton()
    {
        if (CreateButton is null) return;
        CreateButton.IsEnabled = !_isCreating && IsFormComplete();
    }

    private bool IsFormComplete() =>
        !string.IsNullOrWhiteSpace(NameBox.Text)
        && !string.IsNullOrWhiteSpace(SummaryBox.Text)
        && VisibilityBox.SelectedItem is not null
        && McVersionBox.SelectedItem is string mc && !string.IsNullOrWhiteSpace(mc);

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (!IsFormComplete())
        {
            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            StatusLabel.Text = "Fill in the name, summary, Minecraft version, and visibility.";
            UpdateCreateButton();
            return;
        }

        var name = NameBox.Text.Trim();
        var summary = SummaryBox.Text.Trim();
        var mcVersion = (McVersionBox.SelectedItem as string)!.Trim();

        var visText = (VisibilityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Private";
        var vis = Enum.TryParse<PackVisibility>(visText, out var v) ? v : PackVisibility.Private;

        var req = new CreateResourcePackRequest(
            name,
            summary,
            null,
            vis,
            mcVersion);

        _isCreating = true;
        UpdateCreateButton();
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        StatusLabel.Text = "Creating...";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Created = await App.State.Api.CreateResourcePackAsync(req);
            DialogResult = true;
            Close();
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            StatusLabel.Text = "Failed: hosted resource packs are not deployed on this server.";
        }
        catch (Exception ex)
        {
            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
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
