using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using Entry = CloudLauncher.Services.ConfigHubService.Entry;

namespace CloudLauncher.Views;

/// <summary>An in-window card that compares one config file between two instances, line by line,
/// and can copy either side over the other.</summary>
/// <remarks>When the files match, the diff is replaced by a sentence saying so. Both sides are
/// re-read from disk on every selection change, so edits made elsewhere show up.</remarks>
public partial class ConfigCompareCard : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private readonly Window _owner;
    private readonly List<Entry> _copies;
    private readonly ObservableCollection<DiffRow> _rows = new();

    /// <summary>Guards against a slow diff landing after the user has switched sides again.</summary>
    private int _diffToken;

    /// <summary>Set by <see cref="Embed"/>. Suppresses the three things that only make sense over a
    /// backdrop: the fixed card size, the focus grab, and Esc-to-close.</summary>
    private bool _embedded;

    /// <summary>True when this card wrote a file, so the page knows to re-scan on close.</summary>
    public bool CopiedSomething { get; private set; }

    /// <summary>Raised after this card overwrote one of the two files, so a host pane can re-read its
    /// list. Modal callers check <see cref="CopiedSomething"/> on close instead.</summary>
    public event Action? Copied;

    public Task Completion => _tcs.Task;

    /// <param name="copies">Every instance that has a file at this relative path.</param>
    /// <param name="left">The copy the page was showing (the left side by default).</param>
    /// <param name="right">The copy to compare against, or null to pick the first other instance.</param>
    public ConfigCompareCard(IReadOnlyList<Entry> copies, Entry left, Entry? right, Window owner)
    {
        InitializeComponent();
        _owner = owner;
        _copies = copies.OrderBy(c => c.PackName, StringComparer.OrdinalIgnoreCase).ToList();
        DiffList.ItemsSource = _rows;

        TitleLabel.Text = left.FileName;
        PathLabel.Text = left.RelativePath;

        var items = _copies.Select(c => new SideItem(c, $"{c.PackName}  ·  {ConfigHubService.FormatSize(c.Size)}"))
                           .ToList();
        LeftBox.ItemsSource = items;
        RightBox.ItemsSource = items;
        LeftBox.SelectedItem = items.FirstOrDefault(i => i.Entry.FullPath == left.FullPath) ?? items[0];
        RightBox.SelectedItem =
            (right is not null ? items.FirstOrDefault(i => i.Entry.FullPath == right.FullPath) : null)
            ?? items.FirstOrDefault(i => i.Entry.FullPath != left.FullPath)
            ?? items[0];

        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            // Embedded, the card is a pane beside a list the user is arrowing through; grabbing the
            // caret on every selection change would make the list unusable.
            if (!_embedded) Focus();
            _ = RunDiffAsync();
        };
    }

    /// <summary>Turns the floating card into a pane: drops the fixed 1040x680 size and the drop shadow
    /// so it fills its cell, and hides both Close buttons.</summary>
    /// <remarks>Call before the control is loaded. The Surface2 fill and 1px border stay, so it still
    /// reads as a card.</remarks>
    public void Embed()
    {
        _embedded = true;
        CardRoot.Width = double.NaN;
        CardRoot.Height = double.NaN;
        CardRoot.Effect = null;
        CloseIconButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
    }

    public void Close() => _tcs.TrySetResult(true);

    private Entry? LeftEntry => (LeftBox.SelectedItem as SideItem)?.Entry;
    private Entry? RightEntry => (RightBox.SelectedItem as SideItem)?.Entry;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Embedded there is nothing to dismiss, and swallowing Esc would eat the host page's own
        // "clear the search" shortcut.
        if (e.Key == Key.Escape && !_embedded) { Close(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    private void OnSideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) _ = RunDiffAsync();
    }

    private void OnSwap(object sender, RoutedEventArgs e)
    {
        (LeftBox.SelectedItem, RightBox.SelectedItem) = (RightBox.SelectedItem, LeftBox.SelectedItem);
    }

    // ── the diff ─────────────────────────────────────────────────────────────

    private async Task RunDiffAsync()
    {
        if (LeftEntry is not { } left || RightEntry is not { } right) return;

        var token = ++_diffToken;
        _rows.Clear();
        SameState.Visibility = Visibility.Collapsed;
        SummaryLabel.Text = "Reading both files...";
        CopyLeftButton.IsEnabled = false;
        CopyRightButton.IsEnabled = false;

        try
        {
            // Reading and diffing two large configs would freeze the window, so do it off-thread.
            var result = await Task.Run(() =>
            {
                if (left.FullPath == right.FullPath) return (Diff: (ConfigHubService.DiffResult?)null, Error: (string?)null);
                try
                {
                    var leftText = File.ReadAllText(left.FullPath);
                    var rightText = File.ReadAllText(right.FullPath);
                    return (ConfigHubService.Diff(leftText, rightText), null);
                }
                catch (Exception ex) { return (null, ex.Message); }
            });

            if (token != _diffToken) return;

            if (left.FullPath == right.FullPath)
            {
                ShowSame("Same file on both sides",
                    "Pick a different instance on one side to compare two copies.", success: false);
                return;
            }
            if (result.Error is { } error)
            {
                ShowSame("Could not read one of the files", error, success: false);
                return;
            }

            var diff = result.Diff!;
            CopyLeftButton.IsEnabled = true;
            CopyRightButton.IsEnabled = true;
            CopyLeftButton.Content = $"Copy {left.PackName} over {right.PackName}";
            CopyRightButton.Content = $"Copy {right.PackName} over {left.PackName}";

            if (diff.Added == 0 && diff.Removed == 0)
            {
                if (diff.OnlyLineEndings)
                    ShowSame("Same text, different file",
                        "Every line matches. The files differ only in their line endings or a byte-order " +
                        "mark - harmless for the game, but enough to make a sync see a change.", success: true);
                else
                    ShowSame("These files are identical",
                        $"{left.PackName} and {right.PackName} have exactly the same {left.FileName}.",
                        success: true);
                SummaryLabel.Text = diff.OnlyLineEndings
                    ? "No line differs."
                    : $"No differences · {diff.Lines.Count:N0} lines.";
                return;
            }

            foreach (var line in diff.Lines) _rows.Add(new DiffRow(line));
            SummaryLabel.Text =
                $"{diff.Added:N0} line(s) only in {right.PackName}, {diff.Removed:N0} only in {left.PackName}" +
                (diff.Truncated
                    ? " · the file is too large for a full comparison, so lines are matched by position " +
                      "and an inserted line makes everything below it look changed."
                    : "");
        }
        catch (Exception ex)
        {
            if (token != _diffToken) return;
            SummaryLabel.Text = "Compare failed: " + ex.Message;
        }
    }

    private void ShowSame(string title, string body, bool success)
    {
        SameState.Visibility = Visibility.Visible;
        SameTitle.Text = title;
        SameBody.Text = body;
        SameGlyph.Text = success ? "" : "";
        SameGlyph.SetResourceReference(TextBlock.ForegroundProperty,
            success ? "SuccessBrush" : "TextTertiaryBrush");
    }

    // ── copying one side over the other ──────────────────────────────────────

    private async void OnCopyLeftToRight(object sender, RoutedEventArgs e)
    {
        try { await CopyAsync(LeftEntry, RightEntry); }
        catch (Exception ex) { SummaryLabel.Text = "Copy failed: " + ex.Message; }
    }

    private async void OnCopyRightToLeft(object sender, RoutedEventArgs e)
    {
        try { await CopyAsync(RightEntry, LeftEntry); }
        catch (Exception ex) { SummaryLabel.Text = "Copy failed: " + ex.Message; }
    }

    private async Task CopyAsync(Entry? from, Entry? to)
    {
        if (from is null || to is null || from.FullPath == to.FullPath) return;

        if (!await AppDialog.ConfirmAsync(_owner, "Replace this file",
                $"Replace {to.RelativePath} in {to.PackName} with the copy from {from.PackName}?\n\n" +
                $"{to.PackName}'s current file is backed up next to itself as " +
                $"{to.FileName}.bak-<timestamp> first, so this is reversible.",
                "Replace", "Cancel", danger: true))
            return;

        var source = from.FullPath;
        var dest = to.FullPath;
        await Task.Run(() =>
        {
            // Invariant: ConfigHubService.BackupTakenUtc parses this suffix back, and the Cleanup
            // page orders and dates the backups from it.
            File.Copy(dest, $"{dest}.bak-{TimeFormat.StampNow()}", overwrite: true);
            File.Copy(source, dest, overwrite: true);
        });

        ConfigHubService.Invalidate(to.PackId);
        CopiedSomething = true;
        AppLog.Log("files", $"Copied {from.PackName}/{from.RelativePath} over {to.PackName}'s copy.");
        SummaryLabel.Text = $"Replaced {to.FileName} in {to.PackName} with {from.PackName}'s copy " +
                            "(the old one was backed up).";
        Copied?.Invoke();
        await RunDiffAsync();
    }

    // ── rows ─────────────────────────────────────────────────────────────────

    private sealed record SideItem(Entry Entry, string Label);

    private sealed class DiffRow(ConfigHubService.DiffLine line)
    {
        /// <summary>The string the XAML's DataTriggers switch on, so the tint needs no converter.</summary>
        public string KindName { get; } = line.Kind.ToString();

        public string Sign { get; } = line.Kind switch
        {
            ConfigHubService.DiffKind.Added => "+",
            ConfigHubService.DiffKind.Removed => "-",
            _ => " "
        };

        public string LeftNumberLabel { get; } = line.LeftNumber?.ToString() ?? "";
        public string RightNumberLabel { get; } = line.RightNumber?.ToString() ?? "";

        /// <summary>Tabs expanded to spaces: a WPF TextBlock renders a tab as a single glyph, which
        /// ruins the indentation of a JSON or TOML diff.</summary>
        public string Text { get; } = line.Text.Replace("\t", "    ");
    }
}
