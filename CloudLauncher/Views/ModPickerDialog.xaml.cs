using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>An in-window searchable picker over the pack's installed mods, for adding a manual
/// dependency. The result task yields the chosen <see cref="PackMod"/>, or null if cancelled.</summary>
public partial class ModPickerDialog : UserControl
{
    private readonly List<PackMod> _all;
    private readonly TaskCompletionSource<PackMod?> _tcs = new();

    public ModPickerDialog(string title, IEnumerable<PackMod> mods)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        _all = mods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        List.ItemsSource = _all;
        Loaded += (_, _) => { Animate.SlideFadeIn(this, 0, 14, 200); Search.Focus(); };
    }

    public Task<PackMod?> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(null);

    /// <summary>Shows the picker as an in-window overlay and returns the chosen mod, or null.</summary>
    public static async Task<PackMod?> ShowAsync(MainWindow host, string title, IEnumerable<PackMod> mods)
    {
        var card = new ModPickerDialog(title, mods);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
        var q = Search.Text?.Trim();
        List.ItemsSource = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(m => m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnSearchFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdatePlaceholder();

    private void UpdatePlaceholder() =>
        SearchPlaceholder.Visibility =
            string.IsNullOrEmpty(Search.Text) && !Search.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void OnAdd(object sender, RoutedEventArgs e) => Accept();
    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    private void Accept()
    {
        if (List.SelectedItem is PackMod pick) _tcs.TrySetResult(pick);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
