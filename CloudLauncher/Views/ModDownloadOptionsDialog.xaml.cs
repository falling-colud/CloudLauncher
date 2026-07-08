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

    public ModDownloadOptionsDialog(string modName)
    {
        InitializeComponent();
        TitleText.Text = modName;
        Focusable = true;
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
    public static async Task<ModDownloadOptionsDialog?> ShowAsync(MainWindow host, string modName)
    {
        var card = new ModDownloadOptionsDialog(modName);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result ? card : null;
    }

    /// <summary>Applies every chosen option onto the mod's saved flags.</summary>
    public void ApplyTo(ModMeta meta)
    {
        meta.Priority = PriorityBox.SelectedIndex < 0 ? 0 : PriorityBox.SelectedIndex; // 0=Normal, 1..5=P1..P5
        meta.Side = SideClient.IsChecked == true ? ModSide.Client
                  : SideServer.IsChecked == true ? ModSide.Server
                  : ModSide.Both;
        meta.IsLibrary = LibraryBox.IsChecked == true;
        meta.IsTesting = TestingBox.IsChecked == true;
        meta.IsExtra = ExtraBox.IsChecked == true;
    }
}
