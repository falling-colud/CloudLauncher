using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The orange "Custom" download options card (in-window overlay). The user sets priority, side and
/// flags independently, then clicks Download; <see cref="ApplyTo"/> writes them onto the mod's
/// metadata once it's downloaded.
/// </summary>
public partial class ModDownloadOptionsDialog : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    /// <summary>The pack's category names, in the order the Categories page lists them. Index 0 of
    /// <c>CategoryBox</c> is the "no category" entry, so this is offset by one.</summary>
    private readonly List<string> _categories = new();

    /// <param name="packId">The pack being downloaded into, so the Category box can offer that
    /// pack's own categories. Null leaves the box out entirely.</param>
    public ModDownloadOptionsDialog(string modName, Guid? packId = null)
    {
        InitializeComponent();
        TitleText.Text = modName;
        Focusable = true;

        for (var s = 0; s <= ModContentSize.Max; s++)
            ContentSizeBox.Items.Add(new ComboBoxItem
            {
                Content = s == 0 ? "Unset" : ModContentSize.Label(s),
                ToolTip = ModContentSize.Describe(s)
            });
        ContentSizeBox.SelectedIndex = 0;

        if (packId is { } id)
        {
            _categories.AddRange(App.State.ModMetadata.Categories(id).Select(c => c.Name));
            CategoryBox.Items.Add(new ComboBoxItem { Content = "No category" });
            foreach (var name in _categories) CategoryBox.Items.Add(new ComboBoxItem { Content = name });
            CategoryBox.SelectedIndex = 0;
        }
        // A pack with no categories has nothing to offer, so the row would only be a dead control.
        CategoryPanel.Visibility = _categories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => { Animate.SlideFadeIn(this, 0, 14, 200); Focus(); };
    }

    public Task<bool> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(false);

    private void OnDownload(object sender, RoutedEventArgs e) => _tcs.TrySetResult(true);
    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(false);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(false); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Shows the card as an in-window overlay. Returns the dialog if Download was clicked, else null.</summary>
    /// <param name="packId">The pack being downloaded into — supplies the Category box's options.</param>
    public static async Task<ModDownloadOptionsDialog?> ShowAsync(MainWindow host, string modName, Guid? packId = null)
    {
        var card = new ModDownloadOptionsDialog(modName, packId);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result ? card : null;
    }

    /// <summary>Applies every chosen option onto the mod's saved flags.</summary>
    /// <remarks>The category is appended rather than assigned: a mod can be in several, and this
    /// runs against metadata that a previous download of the same project id may already have
    /// filed.</remarks>
    public void ApplyTo(ModMeta meta)
    {
        meta.Priority = PriorityBox.SelectedIndex < 0 ? 0 : PriorityBox.SelectedIndex; // 0=Normal, 1..5=P1..P5
        meta.ContentSize = ModContentSize.Clamp(Math.Max(0, ContentSizeBox.SelectedIndex));
        meta.Side = SideClient.IsChecked == true ? ModSide.Client
                  : SideServer.IsChecked == true ? ModSide.Server
                  : ModSide.Both;
        meta.IsLibrary = LibraryBox.IsChecked == true;
        meta.IsTesting = TestingBox.IsChecked == true;
        meta.IsExtra = ExtraBox.IsChecked == true;

        var categoryIndex = CategoryBox.SelectedIndex - 1;   // index 0 is "No category"
        if (categoryIndex >= 0 && categoryIndex < _categories.Count)
        {
            var name = _categories[categoryIndex];
            if (!meta.Categories.Contains(name, StringComparer.OrdinalIgnoreCase)) meta.Categories.Add(name);
        }

        var note = NoteBox.Text?.Trim();
        if (!string.IsNullOrEmpty(note)) meta.Note = note;
    }
}
