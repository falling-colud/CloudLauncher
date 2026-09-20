using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// A read-only log view that colours each line by what it is — error, warning, debug noise, ordinary
/// output — using the user's log colours.
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
/// </remarks>
public sealed class LogTextView : ListBox
{
    /// <summary>Lines retained. Minecraft streams its whole session through here; past this the
    /// oldest go, which is also what <see cref="AppLog"/> does with its own buffer.</summary>
    private const int MaxLines = 20_000;

    private readonly ObservableCollection<LogLine> _lines = new();

    public LogTextView()
    {
        ItemsSource = _lines;
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
    }

    /// <summary>The whole log as text — for copying, and for callers that still think in strings.</summary>
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

    public void SetText(string? text)
    {
        _lines.Clear();
        if (string.IsNullOrEmpty(text)) return;
        foreach (var line in text.Split('\n'))
            Add(line.TrimEnd('\r'));
        ScrollToEnd();
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
    }

    public void Clear() => _lines.Clear();

    public void ScrollToEnd()
    {
        if (_lines.Count == 0) return;
        ScrollIntoView(_lines[^1]);
    }

    private void Add(string line)
    {
        _lines.Add(new LogLine(line));
        // Trim in chunks: removing one line per append on a hot log is a lot of collection churn.
        if (_lines.Count > MaxLines + 1000)
            for (var i = 0; i < 1000; i++) _lines.RemoveAt(0);
    }

    /// <summary>Ctrl+C copies the selected lines (or everything, when nothing is selected).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var selected = SelectedItems.Count > 0
                ? SelectedItems.Cast<LogLine>().Select(l => l.Text)
                : _lines.Select(l => l.Text);
            ClipboardHelper.TrySetText(string.Join(Environment.NewLine, selected));
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
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
