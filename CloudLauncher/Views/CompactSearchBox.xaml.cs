using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CloudLauncher.Views;

/// <summary>The glyph that grows into a search field: a 36x36 rounded box holding a magnifier that
/// expands to <see cref="ExpandedWidth"/> on click and collapses when it loses focus while empty.
/// While it has text, an X at the right (and Escape) empties it without closing it.</summary>
/// <remarks>Use this rather than copying the markup, so every page's search box behaves the
/// same.</remarks>
public partial class CompactSearchBox : UserControl
{
    private const double CollapsedWidth = 36;

    // Created once and frozen (like Animations/Animate.cs), since this control is on every page.
    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Frozen(Freezable f) { f.Freeze(); return (IEasingFunction)f; }

    private DispatcherTimer? _debounce;
    private string _lastFlushed = "";
    private bool _expanded;
    private bool _syncing;
    private double _widthTarget = CollapsedWidth;

    public CompactSearchBox()
    {
        InitializeComponent();
        // A page can restore a saved query before the control is on screen; showing it collapsed
        // would leave the list filtered by something the user cannot see.
        Loaded += (_, _) =>
        {
            if (!_expanded && Box.Text.Length > 0) Expand(animate: false);
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PROPERTIES

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CompactSearchBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextPropertyChanged));

    /// <summary>The current query. Two-way bindable; setting it raises the same events as typing.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? "");
    }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(CompactSearchBox),
        new PropertyMetadata("", (d, e) =>
        {
            var c = (CompactSearchBox)d;
            if (c.PlaceholderText is null) return;
            c.PlaceholderText.Text = (string?)e.NewValue ?? "";
            c.UpdatePlaceholder();
        }));

    /// <summary>Grey prompt shown inside the expanded box while it is empty.</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly DependencyProperty SearchToolTipProperty = DependencyProperty.Register(
        nameof(SearchToolTip), typeof(string), typeof(CompactSearchBox),
        new PropertyMetadata("Search", (d, e) =>
        {
            var c = (CompactSearchBox)d;
            if (c.ToggleButton is null) return;
            c.ToggleButton.ToolTip = e.NewValue;
            c.Box.ToolTip = e.NewValue;
        }));

    /// <summary>Tooltip on both the glyph and the field; say what the page searches.</summary>
    public string SearchToolTip
    {
        get => (string)GetValue(SearchToolTipProperty);
        set => SetValue(SearchToolTipProperty, value);
    }

    public static readonly DependencyProperty ExpandedWidthProperty = DependencyProperty.Register(
        nameof(ExpandedWidth), typeof(double), typeof(CompactSearchBox),
        new PropertyMetadata(260.0, (d, e) =>
        {
            var c = (CompactSearchBox)d;
            if (c._expanded) c.AnimateWidth((double)e.NewValue);
        }));

    /// <summary>Width the box grows to. Keep the default 260 unless the toolbar can't fit it.</summary>
    public double ExpandedWidth
    {
        get => (double)GetValue(ExpandedWidthProperty);
        set => SetValue(ExpandedWidthProperty, value);
    }

    /// <summary>
    /// How long typing has to stop before <see cref="TextChangedDebounced"/> fires. 200ms suits a
    /// local filter; pages that search over the network want more (around 450).
    /// </summary>
    public int DebounceMilliseconds { get; set; } = 200;

    /// <summary>True while the field is showing.</summary>
    public bool IsExpanded => _expanded;

    // ─────────────────────────────────────────────────────────────────────────
    //  EVENTS

    /// <summary>Fires once the user stops typing, with the trimmed query. Use this for reloads.</summary>
    public event EventHandler<string>? TextChangedDebounced;

    /// <summary>Fires on every keystroke, for pages that filter an in-memory list as you type.</summary>
    public event TextChangedEventHandler? TextChanged;

    /// <summary>Enter was pressed.</summary>
    public event EventHandler? SearchSubmitted;

    // ─────────────────────────────────────────────────────────────────────────
    //  PUBLIC API

    /// <summary>Opens the box and puts the caret in it. Ctrl+F should call this.</summary>
    public new void Focus()
    {
        Expand();
        Box.Focus();
        Box.SelectAll();
    }

    /// <summary>Empties the query, tells the page about it immediately, and collapses.</summary>
    public void Clear()
    {
        SetCurrentValue(TextProperty, "");
        // Clearing shouldn't wait out the debounce; the list should come back at once.
        FlushDebounce();
        Collapse();
    }

    /// <summary>Opens the box without stealing the caret.</summary>
    public void Expand() => Expand(animate: true);

    // ─────────────────────────────────────────────────────────────────────────
    //  BEHAVIOUR

    private void Expand(bool animate)
    {
        if (_expanded) return;
        _expanded = true;
        Box.Visibility = Visibility.Visible;
        UpdatePlaceholder();
        if (animate) AnimateWidth(ExpandedWidth);
        else { _widthTarget = ExpandedWidth; Host.Width = ExpandedWidth; }
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        var hadFocus = Box.IsKeyboardFocusWithin;
        Box.Visibility = Visibility.Collapsed;
        PlaceholderText.Visibility = Visibility.Collapsed;
        AnimateWidth(CollapsedWidth);
        // Only drop focus if we still held it; on a lost-focus collapse the caret has already
        // moved on and clearing would yank it away from whatever the user just clicked.
        if (hadFocus) Keyboard.ClearFocus();
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        // The glyph closes an empty box and focuses a non-empty one. It never closes over a live
        // query, because a collapsed box with text in it is a filter the user cannot see.
        if (_expanded && Box.Text.Length == 0) Collapse();
        else Focus();
    }

    private void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing)
        {
            _syncing = true;
            SetCurrentValue(TextProperty, Box.Text);
            _syncing = false;
        }
        UpdatePlaceholder();
        TextChanged?.Invoke(this, e);
        RestartDebounce();
    }

    private void OnBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _debounce?.Stop();
            // Enter means "search now". Pages handling SearchSubmitted search themselves; otherwise flush
            // the debounce. Either way Enter is instant and searches once.
            if (SearchSubmitted is { } submitted)
            {
                // The page has searched for this text now, so clearing it later must still count as
                // a change and bring the default list back.
                _lastFlushed = Box.Text.Trim();
                submitted(this, EventArgs.Empty);
            }
            else FlushDebounce();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // One step per press, like the X: the first empties the box and keeps the caret there,
            // the next closes it.
            if (Box.Text.Length > 0) ClearKeepingFocus();
            else Clear();
        }
    }

    /// <summary>The X was clicked. It has already emptied the box and put the caret back.</summary>
    private void OnClearButtonCleared(object? sender, EventArgs e) => ClearKeepingFocus();

    /// <summary>Empties the query and tells the page at once, but leaves the box open with the caret
    /// in it, ready for the next search.</summary>
    private void ClearKeepingFocus()
    {
        SetCurrentValue(TextProperty, "");
        FlushDebounce();
        Box.Focus();
    }

    private void OnBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking our own glyph counts as staying inside the box.
        if (e.NewFocus is DependencyObject next && IsDescendantOf(next, Host)) return;
        if (Box.Text.Length == 0) Collapse();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (CompactSearchBox)d;
        if (c.Box is null || c._syncing) return;
        c._syncing = true;
        c.Box.Text = (string?)e.NewValue ?? "";
        c._syncing = false;
    }

    private void UpdatePlaceholder()
    {
        if (PlaceholderText is null) return;
        PlaceholderText.Visibility = _expanded && Box.Text.Length == 0 && PlaceholderText.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  DEBOUNCE

    private void RestartDebounce()
    {
        _debounce ??= NewTimer();
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(1, DebounceMilliseconds));
        _debounce.Start();
    }

    private DispatcherTimer NewTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, DebounceMilliseconds)) };
        timer.Tick += (_, _) => FlushDebounce();
        return timer;
    }

    /// <summary>Raises the debounced event, but only when the settled query changed, so typing and
    /// deleting a character doesn't reload the page.</summary>
    private void FlushDebounce()
    {
        _debounce?.Stop();
        var text = Box.Text.Trim();
        if (string.Equals(text, _lastFlushed, StringComparison.Ordinal)) return;
        _lastFlushed = text;
        TextChangedDebounced?.Invoke(this, text);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ANIMATION

    private void AnimateWidth(double to)
    {
        _widthTarget = to;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(170)) { EasingFunction = EaseOut };
        anim.Completed += (_, _) =>
        {
            // Release the animated width once it lands, or the animation keeps holding it and the next plain
            // assignment has no effect. Skipped if a newer toggle set a different target.
            if (_widthTarget != to) return;
            Host.BeginAnimation(FrameworkElement.WidthProperty, null);
            Host.Width = to;
        };
        Host.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }
}
