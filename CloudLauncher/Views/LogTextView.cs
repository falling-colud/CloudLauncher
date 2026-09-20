using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>How much of a log the view is showing: everything, or only the lines that matter.</summary>
/// <remarks>Deliberately not <see cref="ThemeService.LogLevel"/> with a "minimum": that enum is
/// ordered <c>Normal, Muted, Warning, Error</c> — Muted is quieter than Normal, not louder — so
/// "&gt;= level" would mean nothing. These three are the questions people actually ask of a log.</remarks>
public enum LogFilterMode
{
    /// <summary>Every line.</summary>
    All,

    /// <summary>Warnings and errors only — the two levels that mean something went wrong.</summary>
    Problems,

    /// <summary>Errors, stack traces and "Caused by:" only.</summary>
    Errors
}

/// <summary>
/// A read-only log view that colours each line by what it is — error, warning, debug noise, ordinary
/// output — using the user's log colours, and can narrow itself to the lines being looked for.
/// </summary>
/// <remarks>
/// <para>This replaces a plain <see cref="TextBox"/>. A TextBox can only be one colour, and in a
/// modded Minecraft log the one line that matters is a stack trace buried in twenty thousand lines of
/// mod chatter; colour is what makes it findable. It also could not be themed, which is half of what
/// was asked for.</para>
/// <para>It is a virtualising <see cref="ListBox"/>, so only the visible lines exist as elements —
/// a 200,000-line log costs the same as a screenful. Lines keep a reference to the theme's brush
/// objects rather than copies of their colours, so changing the log colours in Settings repaints an
/// open log immediately.</para>
/// <para>Searching is done by <em>filtering</em> rather than by hopping the caret between matches:
/// the question being asked of a crash log is almost always "show me every line that mentions this
/// mod", and a list of the nine matching lines answers it in one look where nine presses of F3 does
/// not. <see cref="FilterText"/> and <see cref="FilterMode"/> combine, so "errors only" plus a mod id
/// is one gesture.</para>
/// </remarks>
public sealed class LogTextView : ListBox
{
    /// <summary>Lines retained. Minecraft streams its whole session through here; past this the
    /// oldest go, which is also what <see cref="AppLog"/> does with its own buffer.</summary>
    private const int MaxLines = 20_000;

    private readonly ObservableCollection<LogLine> _lines = new();
    private readonly ICollectionView _view;

    private string _filterText = "";
    private LogFilterMode _filterMode = LogFilterMode.All;

    public LogTextView()
    {
        _view = CollectionViewSource.GetDefaultView(_lines);
        _view.Filter = o => Passes((LogLine)o);
        ItemsSource = _view;
        SelectionMode = SelectionMode.Extended;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Lucida Console");
        FontSize = 11;
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        VirtualizingStackPanel.SetIsVirtualizing(this, true);
        VirtualizingStackPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        ItemTemplate = BuildTemplate();
        ItemContainerStyle = BuildContainerStyle();
        ContextMenu = BuildContextMenu();
    }

    /// <summary>Raised whenever the counts behind <see cref="VisibleLineCount"/>,
    /// <see cref="TotalLineCount"/> or <see cref="TrimmedLineCount"/> change, so a host can keep a
    /// "12 of 8,431 lines" caption honest without polling.</summary>
    public event Action? StatsChanged;

    /// <summary>Substring every shown line must contain (case-insensitive). Empty shows everything.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            var next = value ?? "";
            if (_filterText == next) return;
            _filterText = next;
            ApplyFilter();
        }
    }

    /// <summary>Which levels are shown. Combines with <see cref="FilterText"/>.</summary>
    public LogFilterMode FilterMode
    {
        get => _filterMode;
        set
        {
            if (_filterMode == value) return;
            _filterMode = value;
            ApplyFilter();
        }
    }

    /// <summary>True when either half of the filter is narrowing the view.</summary>
    public bool IsFiltered => _filterText.Length > 0 || _filterMode != LogFilterMode.All;

    /// <summary>Lines currently on screen after filtering.</summary>
    public int VisibleLineCount => IsFiltered ? _lines.Count(Passes) : _lines.Count;

    /// <summary>Lines held in memory, filtered or not.</summary>
    public int TotalLineCount => _lines.Count;

    /// <summary>How many lines have been dropped off the start of the buffer to stay under the cap.
    /// Worth surfacing: on a very long session the beginning of the log — where the mod list and the
    /// first failure usually are — is exactly what silently went missing.</summary>
    public int TrimmedLineCount { get; private set; }

    /// <summary>The whole log as text — for copying, and for callers that still think in strings.
    /// Always the complete buffer, never just what the filter is showing.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var line in _lines) sb.AppendLine(line.Text);
            return sb.ToString();
        }
        set => SetText(value);
    }

    /// <summary>The lines the filter is letting through, as text. What "Copy" means when a filter
    /// is on — copying the whole file would throw away the narrowing the user just did.</summary>
    public string VisibleText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var line in _lines)
                if (Passes(line)) sb.AppendLine(line.Text);
            return sb.ToString();
        }
    }

    public void SetText(string? text)
    {
        _lines.Clear();
        TrimmedLineCount = 0;
        if (!string.IsNullOrEmpty(text))
        {
            foreach (var line in text.Split('\n'))
                Add(line.TrimEnd('\r'));
        }
        ScrollToEnd();
        StatsChanged?.Invoke();
    }

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 && text.EndsWith('\n')) continue;
            Add(trimmed);
        }
        StatsChanged?.Invoke();
    }

    public void Clear()
    {
        _lines.Clear();
        TrimmedLineCount = 0;
        StatsChanged?.Invoke();
    }

    public void ScrollToEnd()
    {
        var last = LastVisibleLine();
        if (last is not null) ScrollIntoView(last);
    }

    private LogLine? LastVisibleLine()
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
            if (Passes(_lines[i])) return _lines[i];
        return null;
    }

    private void Add(string line)
    {
        _lines.Add(new LogLine(line));
        // Trim in chunks: removing one line per append on a hot log is a lot of collection churn.
        if (_lines.Count > MaxLines + 1000)
        {
            for (var i = 0; i < 1000; i++) _lines.RemoveAt(0);
            TrimmedLineCount += 1000;
        }
    }

    private void ApplyFilter()
    {
        _view.Refresh();
        StatsChanged?.Invoke();
    }

    private bool Passes(LogLine line)
    {
        if (_filterMode == LogFilterMode.Errors && line.Level != ThemeService.LogLevel.Error) return false;
        if (_filterMode == LogFilterMode.Problems
            && line.Level is not (ThemeService.LogLevel.Error or ThemeService.LogLevel.Warning)) return false;
        return _filterText.Length == 0
               || line.Text.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ctrl+C copies the selected lines (or everything the filter shows, when nothing is
    /// selected), Ctrl+A selects them all.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelectionOrVisible();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void CopySelectionOrVisible()
    {
        var text = SelectedItems.Count > 0
            ? string.Join(Environment.NewLine, SelectedItems.Cast<LogLine>().Select(l => l.Text))
            : VisibleText;
        ClipboardHelper.TrySetText(text);
    }

    /// <summary>Right-click menu. A log is read and quoted far more often than it is navigated, and
    /// until now the only way to get a line out of here was to select it and know that Ctrl+C worked.</summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var copySelected = new MenuItem { Header = "Copy selected lines", InputGestureText = "Ctrl+C" };
        copySelected.Click += (_, _) => CopySelectionOrVisible();
        menu.Items.Add(copySelected);

        var copyShown = new MenuItem { Header = "Copy everything shown" };
        copyShown.Click += (_, _) => ClipboardHelper.TrySetText(VisibleText);
        menu.Items.Add(copyShown);

        var copyAll = new MenuItem { Header = "Copy the whole log" };
        copyAll.Click += (_, _) => ClipboardHelper.TrySetText(Text);
        menu.Items.Add(copyAll);

        menu.Items.Add(new Separator());

        var selectAll = new MenuItem { Header = "Select all", InputGestureText = "Ctrl+A" };
        selectAll.Click += (_, _) => SelectAll();
        menu.Items.Add(selectAll);

        menu.Opened += (_, _) =>
        {
            copySelected.IsEnabled = SelectedItems.Count > 0;
            copyShown.Visibility = IsFiltered ? Visibility.Visible : Visibility.Collapsed;
            copyAll.Header = IsFiltered ? "Copy the whole log" : "Copy all";
        };
        return menu;
    }

    /// <summary>
    /// One TextBlock per line, coloured by the line's level through <c>DynamicResource</c> so that
    /// changing the log colours in Settings repaints an open log immediately.
    /// </summary>
    /// <remarks>The colour lives in a <see cref="Style"/> rather than on the element: a value set
    /// directly on a templated element outranks a DataTemplate trigger, so setting the default
    /// foreground on the TextBlock itself would silently make every line the same colour.</remarks>
    private static DataTemplate BuildTemplate()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("LogTextBrush")));
        foreach (var (level, key) in new[]
                 {
                     (ThemeService.LogLevel.Error, "LogErrorBrush"),
                     (ThemeService.LogLevel.Warning, "LogWarningBrush"),
                     (ThemeService.LogLevel.Muted, "LogMutedBrush"),
                 })
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(LogLine.Level)), Value = level };
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(key)));
            style.Triggers.Add(trigger);
        }

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogLine.Text)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        text.SetValue(FrameworkElement.StyleProperty, style);
        return new DataTemplate { VisualTree = text };
    }

    /// <summary>Tight rows and no selection chrome beyond a subtle highlight: this is a log, not a list
    /// of things to click.</summary>
    private static Style BuildContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 0, 6, 0)));
        style.Setters.Add(new Setter(MinHeightProperty, 0.0));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Left));

        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("LogSelectionBrush")));
        style.Triggers.Add(selected);
        return style;
    }

    /// <summary>One line and what it reads as. The template turns the level into a colour.</summary>
    private sealed class LogLine(string text)
    {
        public string Text { get; } = text;
        public ThemeService.LogLevel Level { get; } = ThemeService.LevelOf(text);
    }
}
