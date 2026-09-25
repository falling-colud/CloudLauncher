using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Which of the seven states a page is currently in.</summary>
public enum PageStateKind
{
    /// <summary>Before the first load starts. Must not survive the first frame.</summary>
    Idle,
    /// <summary>Work in flight and nothing to show yet. No counts, no empty panel.</summary>
    Loading,
    /// <summary>Work in flight over data that is already on screen and still true.</summary>
    Refreshing,
    /// <summary>Rows, and a real number beside them.</summary>
    Content,
    /// <summary>We looked and there is nothing. Only reachable from a completed look.</summary>
    Empty,
    /// <summary>Something failed. A sentence and a Retry; the exception is in the launcher log.</summary>
    Error,
    /// <summary>The server is not answering. Not an error: say what still works.</summary>
    Offline
}

/// <summary>
/// The page-state contract as one object a page owns. It writes the count slot, the status line,
/// the busy bar and <see cref="PageStateView"/>, so pages do not each decide these.
/// </summary>
/// <remarks>
/// <para>Rules, enforced by construction:</para>
/// <list type="number">
/// <item>Empty is unreachable from Loading. The only way into <see cref="PageStateKind.Empty"/> is
/// <see cref="Content"/> with a count of zero, called once the load has returned, so Empty always
/// means "we looked".</item>
/// <item>A count is a real number or absent, never a placeholder zero. While loading, the slot
/// shows the verb ("Scanning...").</item>
/// <item>Refreshing keeps the data and only draws a hairline at the top of the list.</item>
/// <item>Errors are a sentence plus Retry. The exception goes to <see cref="AppLog"/>; raw
/// <c>ex.Message</c> never reaches the screen.</item>
/// <item>Offline is its own state and says what still works.</item>
/// </list>
/// <para>Everything here runs on the UI thread. Call the transitions from the awaited load method,
/// not from a background task.</para>
/// </remarks>
/// <example>
/// Wiring it up in the page's constructor:
/// <code>
/// _state = new PageState(FileList, PageStateHost, nameof(ConfigHubView))
///     .Copy(PageCopy.ConfigFiles)
///     .Slots(SubLabel, StatusLabel, BusyBar, BusyCancelButton)
///     .DisableWhileBusy(RefreshButton, SortButton, PackFilterBox);
/// _state.RetryRequested  += () =&gt; _ = LoadAsync(force: true);
/// _state.CancelRequested += () =&gt; _workCts?.Cancel();
/// </code>
/// </example>
public sealed class PageState
{
    private readonly FrameworkElement? _content;
    private readonly PageStateView _view;
    private readonly string _logName;
    private readonly List<UIElement> _disable = [];

    private TextBlock? _count;
    private TextBlock? _status;
    private ProgressBar? _busy;
    private ButtonBase? _cancel;

    private PageCopy _copy = PageCopy.Results;

    /// <summary>Whatever line Begin was given, so Progress can relabel the detail underneath it
    /// without throwing away the page's own wording.</summary>
    private string _busyLine = "";

    // The page's standing empty answer...
    private string _baseTitle = "";
    private string _baseBody = "";
    private string _baseGlyph = "";
    private string? _baseActionLabel;
    private Action? _baseAction;

    // ...and the one in force for the current look, which EmptyNext overrides for a single
    // Content() call. It reverts afterwards so one filtered look does not leave "nothing
    // matched" in place for good.
    private string _emptyTitle = "";
    private string _emptyBody = "";
    private string _emptyGlyph = "";
    private string? _emptyActionLabel;
    private Action? _emptyAction;

    /// <summary>The handler behind the button currently drawn, captured when the panel is shown so
    /// the per-pass override reverting underneath it cannot re-point a visible button.</summary>
    private Action? _shownAction;

    /// <summary>The count slot's text from the last <see cref="Content"/>, so Refreshing and
    /// Offline can keep showing a number that is still true rather than blanking it.</summary>
    private string _lastCountText = "";

    /// <summary>How long a quiet refresh runs before it shows. See
    /// <see cref="Begin(string?, bool?, bool, Action?)"/>.</summary>
    /// <remarks>Covers a server round trip and a warm rescan, while a slow refresh still shows well
    /// within a second.</remarks>
    public static TimeSpan QuietRefreshDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>True while a refresh is running but has not been announced yet: nothing on screen has
    /// changed, and if it finishes in time nothing will.</summary>
    private bool _quiet;
    private System.Windows.Threading.DispatcherTimer? _revealTimer;
    private Action? _onReveal;

    /// <summary>The latest <see cref="Progress"/> line held back during a quiet refresh.</summary>
    private string? _heldProgress;

    /// <param name="content">The list, grid or card panel the overlay shares a cell with. Hidden
    /// while a full-area panel is up and always visible while Refreshing. May be null when the
    /// overlay has the cell to itself.</param>
    /// <param name="view">The <see cref="PageStateView"/> placed last in that same cell.</param>
    /// <param name="logName">The AppLog category for failures, normally
    /// <c>nameof(ThisView)</c>.</param>
    public PageState(FrameworkElement? content, PageStateView view, string logName)
    {
        _content = content;
        _view = view;
        _logName = logName;
        _view.RetryRequested += () => RetryRequested?.Invoke();
        _view.CancelRequested += () => CancelRequested?.Invoke();
        _view.EmptyActionRequested += () => _shownAction?.Invoke();
        ApplyCopy(_copy);
    }

    /// <summary>What the page is currently saying.</summary>
    public PageStateKind Kind { get; private set; } = PageStateKind.Idle;

    /// <summary>True once a completed look found at least one row. Decides between Loading (blank
    /// the area, show the verb) and Refreshing (leave everything alone).</summary>
    public bool HasData { get; private set; }

    /// <summary>The Retry button on the Error and Offline panels. The button shows even with no
    /// handler, so always wire it.</summary>
    public event Action? RetryRequested;

    /// <summary>Raised by the Cancel button under the loading bar and by the status-bar Cancel
    /// passed to <see cref="Slots"/>. Both stay hidden while nothing is subscribed.</summary>
    public event Action? CancelRequested;

    // ── wiring ───────────────────────────────────────────────────────────────

    /// <summary>The page's existing labels and bar. All optional.</summary>
    /// <param name="count">The count slot: a subtitle or a "{n} shown" label.</param>
    /// <param name="status">The row-4 status line.</param>
    /// <param name="busy">The row-4 progress bar, shown while loading or refreshing.</param>
    /// <param name="cancel">The row-4 Cancel button. Its Click is routed to
    /// <see cref="CancelRequested"/>, so remove any Click handler in the page's XAML or cancel
    /// fires twice.</param>
    public PageState Slots(TextBlock? count, TextBlock? status = null,
                           ProgressBar? busy = null, ButtonBase? cancel = null)
    {
        _count = count;
        _status = status;
        _busy = busy;
        _cancel = cancel;
        if (_cancel is not null) _cancel.Click += (_, _) => CancelRequested?.Invoke();
        Paint();
        return this;
    }

    /// <summary>Controls disabled while work is in flight: the Refresh button, the sort menu, the
    /// filter combo. Leave the search box out; the debounce handles typing during a scan.</summary>
    /// <remarks>Do not also set these elements' <c>IsEnabled</c> from the page, or whichever runs
    /// last wins.</remarks>
    public PageState DisableWhileBusy(params UIElement[] elements)
    {
        _disable.AddRange(elements);
        return this;
    }

    /// <summary>The house strings for this kind of page. See <see cref="PageCopy"/>.</summary>
    public PageState Copy(PageCopy copy)
    {
        _copy = copy;
        ApplyCopy(copy);
        return this;
    }

    /// <summary>
    /// The page's standing empty answer, replacing the one its <see cref="PageCopy"/> carries.
    /// Call it once, when wiring up.
    /// </summary>
    /// <param name="actionLabel">Label for one accent button on the empty panel ("Get shaders"), or
    /// null for no button.</param>
    public PageState EmptyCopy(string title, string body, string? actionLabel = null,
                               Action? action = null, string? glyph = null)
    {
        _baseTitle = title;
        _baseBody = body;
        _baseActionLabel = actionLabel;
        _baseAction = action;
        _baseGlyph = glyph ?? _copy.Glyph;
        RevertEmpty();
        return this;
    }

    /// <summary>
    /// Overrides the empty wording for the next <see cref="Content"/> only, for when the empty
    /// answer depends on the active filter ("no config files yet" vs "nothing matched that search").
    /// </summary>
    public PageState EmptyNext(string title, string body, string? glyph = null,
                               string? actionLabel = null, Action? action = null)
    {
        _emptyTitle = title;
        _emptyBody = body;
        _emptyGlyph = glyph ?? _copy.FilteredGlyph;
        _emptyActionLabel = actionLabel;
        _emptyAction = action;
        return this;
    }

    /// <summary>Uses the page's own "nothing matched the filter" wording for the next
    /// <see cref="Content"/>.</summary>
    public PageState EmptyFiltered() =>
        EmptyNext(_copy.FilteredTitle, _copy.FilteredBody, _copy.FilteredGlyph);

    private void RevertEmpty()
    {
        _emptyTitle = _baseTitle;
        _emptyBody = _baseBody;
        _emptyGlyph = _baseGlyph;
        _emptyActionLabel = _baseActionLabel;
        _emptyAction = _baseAction;
    }

    // ── transitions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Work has started. Picks Loading or Refreshing from <see cref="HasData"/>.
    /// </summary>
    /// <param name="what">The line under the bar, e.g. "Reading the saves folder in each instance."
    /// Null uses the page's <see cref="PageCopy.LoadingLine"/>.</param>
    /// <param name="refreshing">Force the choice. Pass false for a new search in a browser, whose
    /// old results are not worth keeping on screen.</param>
    public void Begin(string? what = null, bool? refreshing = null) => Begin(what, refreshing, quiet: false);

    /// <inheritdoc cref="Begin(string?, bool?)"/>
    /// <param name="quiet">For a refresh nobody asked for, such as a page reopening. The hairline, the
    /// "updating" count, the disabled buttons and the progress lines are held back for
    /// <see cref="QuietRefreshDelay"/>, so a refresh that finishes in time only changes what actually
    /// changed. Pass false for a refresh the user started (Refresh, F5). Ignored for Loading.</param>
    /// <param name="onReveal">Runs only if the quiet refresh outlives the delay, e.g. to show a
    /// "remembered from the last scan" note.</param>
    public void Begin(string? what, bool? refreshing, bool quiet, Action? onReveal = null)
    {
        StopQuiet();
        var refresh = refreshing ?? HasData;
        Kind = refresh ? PageStateKind.Refreshing : PageStateKind.Loading;
        if (!refresh) HasData = false;
        _busyLine = what ?? _copy.LoadingLine;
        if (Kind == PageStateKind.Loading)
            _view.ShowBusy(_busyLine, null, CancelRequested is not null);
        else if (quiet)
        {
            // Leave the screen as it is. A refresh only starts over data, so the view is
            // already just the rows.
            _quiet = true;
            _onReveal = onReveal;
            _revealTimer ??= CreateRevealTimer();
            _revealTimer.Interval = QuietRefreshDelay;
            _revealTimer.Start();
        }
        else
        {
            _view.ShowRefreshing();
            onReveal?.Invoke();
        }
        Paint();
    }

    private System.Windows.Threading.DispatcherTimer CreateRevealTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, _view.Dispatcher);
        timer.Tick += (_, _) => RevealQuietRefresh();
        return timer;
    }

    /// <summary>The quiet refresh ran past its delay: from here on it looks like any
    /// other refresh.</summary>
    private void RevealQuietRefresh()
    {
        _revealTimer?.Stop();
        if (!_quiet) return;
        _quiet = false;
        if (Kind != PageStateKind.Refreshing) { _onReveal = null; _heldProgress = null; return; }

        _view.ShowRefreshing();
        var reveal = _onReveal;
        _onReveal = null;
        reveal?.Invoke();
        if (_heldProgress is { } held) SetStatus(held, danger: false);
        _heldProgress = null;
        Paint();
    }

    /// <summary>Ends a quiet refresh without announcing it.</summary>
    private void StopQuiet()
    {
        _revealTimer?.Stop();
        _quiet = false;
        _onReveal = null;
        _heldProgress = null;
    }

    /// <summary>Relabels the work in flight without restarting the bar. Feed an
    /// <c>IProgress&lt;string&gt;</c> straight into this.</summary>
    public void Progress(string detail)
    {
        if (Kind is not (PageStateKind.Loading or PageStateKind.Refreshing)) return;
        // A quiet refresh holds the latest line until it is announced.
        if (_quiet) { _heldProgress = detail; return; }
        if (Kind == PageStateKind.Loading) _view.UpdateBusy(_busyLine, detail);
        SetStatus(detail, danger: false);
    }

    /// <summary>
    /// The look finished. A count of zero enters <see cref="PageStateKind.Empty"/>, anything else
    /// is content. This is the only way into Empty.
    /// </summary>
    /// <param name="noun">Written with the "(s)" plural form: "world(s)", "result(s)". Null uses
    /// the page's <see cref="PageCopy.Noun"/>.</param>
    /// <param name="note">The status line: a stale-cache age, an offline note, "3 instances could
    /// not be read". Null clears it.</param>
    /// <param name="countText">The whole count slot, when the page words it itself ("4,812 distinct
    /// path(s) across 20 instances"). The number in it must be real.</param>
    public void Content(int count, string? noun = null, string? note = null, string? countText = null)
    {
        StopQuiet();
        HasData = count > 0;
        _lastCountText = count == 0 ? "" : countText ?? $"{count:N0} {noun ?? _copy.Noun}";

        if (count == 0)
        {
            Kind = PageStateKind.Empty;
            ShowEmptyPanel();
        }
        else
        {
            Kind = PageStateKind.Content;
            _view.ShowNothing();
        }
        RevertEmpty();
        SetStatus(note, danger: false);
        Paint();
    }

    /// <summary>
    /// The look failed. <paramref name="plain"/> is one plain clause ("the folder could not be
    /// read"), never the exception's own words.
    /// </summary>
    /// <param name="ex">Logged to <see cref="AppLog"/> with its stack. Nothing from it is shown.</param>
    /// <param name="title">Overrides the page's <see cref="PageCopy.ErrorTitle"/>.</param>
    /// <remarks>A refresh that fails over rows already on screen keeps them and puts the sentence
    /// on the status line; the page's Refresh button is the retry.</remarks>
    public void Error(string plain, Exception? ex = null, string? title = null)
    {
        StopQuiet();
        if (ex is not null) AppLog.LogError(_logName, ex);
        Kind = PageStateKind.Error;
        var headline = title ?? _copy.ErrorTitle;

        if (HasData)
            _view.ShowNothing();
        else
            _view.ShowError(headline,
                            plain.Length == 0 ? "" : plain + " The full error is in the launcher log.");

        SetStatus(plain.Length == 0 ? headline : headline + " - " + plain, danger: true);
        Paint();
    }

    /// <summary>
    /// The server is not answering. With data on screen this is a note above the rows; otherwise a
    /// panel saying what still works. Not an Error, since nothing on this PC is broken.
    /// </summary>
    /// <param name="why">The transport phrase from
    /// <see cref="Connectivity.DescribeTransportFailure"/>, substituted into the page's
    /// <see cref="PageCopy.OfflineBody"/>.</param>
    /// <param name="age">How old the data on screen is, from <see cref="PackListCache.AgeInWords"/>
    /// or <see cref="PackListCache.Describe"/>.</param>
    public void Offline(string why, string? age = null)
    {
        StopQuiet();
        Kind = PageStateKind.Offline;
        var body = string.Format(_copy.OfflineBody, why);
        var line = _copy.OfflineTitle + " - " + body;
        if (age is { Length: > 0 }) line += $" Last answer {age}.";

        if (HasData)
        {
            _view.ShowNothing();
            SetStatus(line, danger: false);
        }
        else
        {
            _view.ShowOffline(_copy.OfflineTitle, body);
            SetStatus(line, danger: false);
        }
        Paint();
    }

    /// <summary>
    /// The user cancelled. Whatever is on screen stays; only the work stops. A page with nothing
    /// yet falls back to its empty panel.
    /// </summary>
    public void Cancelled(string? note = null)
    {
        StopQuiet();
        if (HasData)
        {
            Kind = PageStateKind.Content;
            _view.ShowNothing();
        }
        else
        {
            Kind = PageStateKind.Empty;
            ShowEmptyPanel();
        }
        SetStatus(note, danger: false);
        Paint();
    }

    /// <summary>Writes the status line without changing state, e.g. a provenance note such as
    /// "Scanned 3 hours ago. Refresh to re-read the folders."</summary>
    public void Note(string? text) => SetStatus(text, danger: false);

    // ── painting ─────────────────────────────────────────────────────────────

    private void ApplyCopy(PageCopy copy)
    {
        _baseTitle = copy.EmptyTitle;
        _baseBody = copy.EmptyBody;
        _baseGlyph = copy.Glyph;
        RevertEmpty();
    }

    private void ShowEmptyPanel()
    {
        _shownAction = _emptyAction;
        _view.ShowEmpty(_emptyGlyph, _emptyTitle, _emptyBody, _emptyActionLabel);
    }

    private void SetStatus(string? text, bool danger)
    {
        if (_status is null) return;
        _status.Text = text ?? "";
        // SetResourceReference rather than a fetched brush so the line re-colours with the theme.
        // Pages must not hard-code DangerBrush on their status label for the same reason.
        _status.SetResourceReference(TextBlock.ForegroundProperty,
                                     danger ? "DangerBrush" : "TextSecondaryBrush");
    }

    private void Paint()
    {
        // A quiet refresh paints the same as Content: same count, bars off, buttons enabled.
        var busy = Kind is PageStateKind.Loading || (Kind is PageStateKind.Refreshing && !_quiet);

        if (_count is not null)
            _count.Text = Kind switch
            {
                // The verb, never a number.
                PageStateKind.Loading => _copy.Verb + "...",
                PageStateKind.Refreshing => _lastCountText.Length == 0
                    ? _copy.Verb + "..."
                    : _quiet ? _lastCountText : _lastCountText + " · updating",
                PageStateKind.Content => _lastCountText,
                PageStateKind.Offline => _lastCountText.Length == 0
                    ? ""
                    : _lastCountText + " · last known",
                // Empty, Error and Idle have no count to show; the panel says it.
                _ => ""
            };

        if (_busy is not null)
            _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (_cancel is not null)
            _cancel.Visibility = busy && CancelRequested is not null
                ? Visibility.Visible : Visibility.Collapsed;

        if (_content is not null)
        {
            // Hidden only when a full-area panel has taken over, or while the first load runs with
            // nothing to show. Refreshing never touches it.
            var hide = Kind is PageStateKind.Loading or PageStateKind.Empty
                    || (Kind is PageStateKind.Error or PageStateKind.Offline && !HasData);
            _content.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        }

        foreach (var element in _disable) element.IsEnabled = !busy;
    }
}

/// <summary>
/// The user-facing strings for one kind of page, kept together so the wording stays consistent.
/// </summary>
/// <remarks>
/// Plain and specific, no exclamation marks. A count is formatted <c>N0</c>.
/// <see cref="OfflineBody"/> is a format string whose <c>{0}</c> is the transport phrase from
/// <see cref="Connectivity.DescribeTransportFailure"/> ("the server did not answer in time").
/// </remarks>
public sealed record PageCopy
{
    /// <summary>Present participle for the count slot while loading: "Scanning" renders
    /// "Scanning...".</summary>
    public required string Verb { get; init; }

    /// <summary>The counted noun, with the "(s)" plural form: "world(s)".</summary>
    public required string Noun { get; init; }

    /// <summary>The sentence under the loading bar. A full sentence, because it is the only thing
    /// on screen at that moment.</summary>
    public required string LoadingLine { get; init; }

    public required string EmptyTitle { get; init; }
    public required string EmptyBody { get; init; }

    public required string FilteredTitle { get; init; }
    public required string FilteredBody { get; init; }

    public required string ErrorTitle { get; init; }

    public required string OfflineTitle { get; init; }

    /// <summary>What still works, with <c>{0}</c> for the reason the server is unreachable.</summary>
    public required string OfflineBody { get; init; }

    /// <summary>Segoe MDL2 glyph for the empty panel.</summary>
    public string Glyph { get; init; } = "";

    /// <summary>Glyph for "nothing matched the filter": the magnifier, since it is about
    /// the search.</summary>
    public string FilteredGlyph { get; init; } = "";

    public static readonly PageCopy Instances = new()
    {
        Glyph = "",
        Verb = "Loading",
        Noun = "instance(s)",
        LoadingLine = "Loading your instances...",
        EmptyTitle = "No instances yet",
        EmptyBody = "Create one, or download a modpack from the Browse page.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "No instance matched that search or folder. Clear the search to see them all.",
        ErrorTitle = "Could not load your instances",
        OfflineTitle = "Showing your last known instances",
        OfflineBody = "The server is not answering ({0}). Your downloaded instances are still on "
                    + "this PC and still playable."
    };

    public static readonly PageCopy Worlds = new()
    {
        Glyph = "",
        Verb = "Scanning",
        Noun = "world(s)",
        LoadingLine = "Reading the saves folder in each instance.",
        EmptyTitle = "No worlds yet",
        EmptyBody = "Import a save, or launch an instance and create a world - either shows up here. "
                  + "A world the launcher keeps becomes a template new instances can start from.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Every world is still there - the search, folder or instance filter is hiding them.",
        ErrorTitle = "Could not read your saves",
        OfflineTitle = "Showing worlds for your last known instances",
        OfflineBody = "The server is not answering ({0}). The saves themselves are on this PC."
    };

    public static readonly PageCopy ResourcePacks = new()
    {
        Glyph = "",
        Verb = "Scanning",
        Noun = "resource pack(s)",
        LoadingLine = "Opening each instance's resourcepacks folder.",
        EmptyTitle = "No resource packs yet",
        EmptyBody = "Nothing in any instance's resourcepacks folder yet. Add a zip you already "
                  + "have, or find one in the store.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Every resource pack is still there - the search, folder or instance filter "
                     + "is hiding them.",
        ErrorTitle = "Could not read the resourcepacks folders",
        OfflineTitle = "Showing packs for your last known instances",
        OfflineBody = "The server is not answering ({0}). The packs themselves are on this PC."
    };

    public static readonly PageCopy Shaders = new()
    {
        Glyph = "",
        Verb = "Scanning",
        Noun = "shader pack(s)",
        LoadingLine = "Reading the shaderpacks folder in each instance.",
        EmptyTitle = "No shader packs yet",
        EmptyBody = "Find one in the store, add a zip you already have, or drop a zip straight "
                  + "onto this list. Shaders need Iris (or Oculus on Forge) installed in the "
                  + "instance to do anything.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Every shader pack is still there - the search, folder or instance filter "
                     + "is hiding them.",
        ErrorTitle = "Could not read the shaderpacks folders",
        OfflineTitle = "Showing shaders for your last known instances",
        OfflineBody = "The server is not answering ({0}). The packs themselves are on this PC."
    };

    public static readonly PageCopy Servers = new()
    {
        Glyph = "",
        Verb = "Reading",
        Noun = "server(s)",
        LoadingLine = "Reading each instance's multiplayer list.",
        EmptyTitle = "No servers yet",
        EmptyBody = "Servers come from each instance's own multiplayer list. Use 'Add "
                  + "server' to put one in, or add it in-game and refresh.",
        FilteredTitle = "Nothing matches that",
        FilteredBody = "No server matches that search. The search looks at the name, the address, "
                     + "the MOTD and the instance it came from.",
        ErrorTitle = "Could not read the server lists",
        OfflineTitle = "Showing servers from your last known instances",
        OfflineBody = "The server is not answering ({0}). Pinging the servers themselves still works."
    };

    public static readonly PageCopy ConfigFiles = new()
    {
        Glyph = "",
        Verb = "Scanning",
        Noun = "file(s)",
        LoadingLine = "Reading the config, kubejs and defaultconfigs folders in each instance.",
        EmptyTitle = "No config files here yet",
        EmptyBody = "None of your instances has a config, kubejs or defaultconfigs folder yet. "
                  + "That is normal until an instance has been launched or synced once.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "No file matched that filter. Clear the search or pick a different kind.",
        ErrorTitle = "Could not read the config folders",
        OfflineTitle = "Showing your last known instances",
        OfflineBody = "The server is not answering ({0}). The files on this PC are still readable "
                    + "and editable."
    };

    public static readonly PageCopy Mods = new()
    {
        Glyph = "",
        Verb = "Scanning",
        Noun = "mod(s)",
        LoadingLine = "Reading the mods folder in each instance.",
        EmptyTitle = "No mods installed",
        EmptyBody = "No mod files found in any mods/ folder. Browse CurseForge and Modrinth from "
                  + "the Mod browsing tab, or add jars you already have - you can also drop them "
                  + "straight onto this list.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Every mod is still there - the search or filter is hiding them.",
        ErrorTitle = "Could not scan the mods folder",
        OfflineTitle = "Showing what is on this PC",
        OfflineBody = "The server is not answering ({0}). Update checks are paused; the jars "
                    + "themselves are still here."
    };

    /// <summary>The store and browse pages, where a look is a search rather than a scan.</summary>
    public static readonly PageCopy Results = new()
    {
        Glyph = "",
        Verb = "Searching",
        Noun = "result(s)",
        LoadingLine = "Searching...",
        EmptyTitle = "No results",
        EmptyBody = "Try a different search, filter, or source.",
        FilteredTitle = "No results",
        FilteredBody = "Try a different search, filter, or source.",
        ErrorTitle = "Could not search",
        OfflineTitle = "You are offline",
        OfflineBody = "The store cannot be searched ({0}). What you have already downloaded is "
                    + "still on the Instances page."
    };
}

/// <summary>
/// An "I am filling this control, ignore what it raises" guard.
/// </summary>
/// <remarks>
/// <para>Assigning <c>ItemsSource</c> and then <c>SelectedItem</c> on a ComboBox raises
/// <c>SelectionChanged</c> synchronously, twice, and <c>IsLoaded</c> is already true inside a
/// <c>Loaded</c> handler, so an <c>IsLoaded</c> check does not help.</para>
/// <para>Counted so nested fills unwind correctly, and disposable so an exception mid-fill cannot
/// leave the guard stuck on.</para>
/// </remarks>
/// <example>
/// <code>
/// private readonly Reentrancy _filling = new();
///
/// private void RebuildPackFilter()
/// {
///     using (_filling.Hold())
///     {
///         PackFilterBox.ItemsSource = items;
///         PackFilterBox.SelectedItem = pick;
///     }
/// }
///
/// private void OnPackFilterChanged(object s, SelectionChangedEventArgs e)
/// {
///     if (_filling.Busy) return;   // we are populating the box, not reacting to the user
///     Refresh();
/// }
/// </code>
/// </example>
public sealed class Reentrancy
{
    private int _depth;

    /// <summary>True while a fill is in progress. Every handler the fill can raise
    /// returns on it.</summary>
    public bool Busy => _depth > 0;

    /// <summary>Holds the guard until disposed. UI thread only, where the suppressed events are
    /// raised, so no interlocking is needed.</summary>
    public IDisposable Hold()
    {
        _depth++;
        return new Release(this);
    }

    private sealed class Release(Reentrancy owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._depth--;
        }
    }
}
