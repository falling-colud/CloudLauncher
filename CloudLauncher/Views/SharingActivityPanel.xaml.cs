using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>A day's heading in the feed. Its own row type so the list is one flat collection.</summary>
public sealed class ActivityDayHeader(string heading)
{
    public string Heading { get; } = heading;
}

/// <summary>
/// One entry as a sentence, split around the thing it is about so that part can be a link.
/// </summary>
/// <remarks>
/// The subject name is stored in the entry on the server, so entries about deleted things still
/// read. Since the subject may be gone, whether it can be opened is carried by the row.
/// </remarks>
public sealed class ActivityRowView
{
    public ActivityRowView(ActivityFeedEntry entry, bool offline)
    {
        Entry = entry;
        var sentence = ActivityFeedService.Describe(entry);
        SubjectName = entry.SubjectName;

        var at = SubjectName.Length == 0 ? -1 : sentence.IndexOf(SubjectName, StringComparison.Ordinal);
        if (at >= 0)
        {
            Before = sentence[..at];
            After = sentence[(at + SubjectName.Length)..];
            SubjectVisibility = Visibility.Visible;
        }
        else
        {
            Before = sentence;
            After = "";
            SubjectVisibility = Visibility.Collapsed;
        }

        Glyph = entry.Kind switch
        {
            ActivityKind.Shared or ActivityKind.Unshared => "",
            ActivityKind.VersionPublished or ActivityKind.Uploaded => "",
            ActivityKind.MemberAdded or ActivityKind.MemberRemoved or ActivityKind.RoleChanged => "",
            ActivityKind.InviteSent or ActivityKind.InviteAccepted => "",
            ActivityKind.OwnershipTransferred => "",
            ActivityKind.VisibilityChanged => "",
            _ => ""
        };

        When = ActivityFeedService.When(entry.CreatedAt);
        ExactTime = TimeFormat.LongDateTime(entry.CreatedAt);

        // Every subject can be opened: a bundle opens its page, a hosted world its save or its
        // entry in Download a world. Those two read from the server, so they wait for it.
        CanOpenSubject = entry.SubjectType switch
        {
            ActivitySubjectType.Pack or ActivitySubjectType.Mod or ActivitySubjectType.ResourcePack => true,
            ActivitySubjectType.Team => true,
            ActivitySubjectType.Bundle or ActivitySubjectType.World => !offline,
            _ => false
        };
        SubjectHint = CanOpenSubject
            ? $"Open this {ActivityFeedService.SubjectLabel(entry.SubjectType).ToLowerInvariant()}"
            : entry.SubjectType == ActivitySubjectType.Bundle
                ? "Opening a bundle reads it from the server, which is not answering."
                : "There is no page in the launcher for this one yet.";
    }

    public ActivityFeedEntry Entry { get; }
    public string Before { get; }
    public string SubjectName { get; }
    public string After { get; }
    public Visibility SubjectVisibility { get; }
    public string Glyph { get; }
    public string When { get; }
    public string ExactTime { get; }
    public bool CanOpenSubject { get; }
    public string SubjectHint { get; }
}

/// <summary>
/// The sharing hub's Activity tab: who shared what with whom, newest first, grouped by day.
/// </summary>
/// <remarks>
/// <para>Reads <c>GET /activity/me</c> (what I did, what was done to me, and what happened to
/// things I own), or one subject's history when <see cref="ShowSubject"/> is used.</para>
/// <para>The first page is mirrored into <see cref="ActivityFeedCache"/> so the tab still works
/// offline, labelled as the last known feed. "Show older" is disabled offline, since older entries
/// only exist on the server.</para>
/// </remarks>
public partial class SharingActivityPanel : UserControl
{
    private const int PageSize = 50;

    private readonly MainWindow _shell;
    private readonly ActivityFeedService _activity;
    private readonly PageState _state;

    private readonly List<ActivityFeedEntry> _entries = [];
    private CancellationTokenSource? _cts;
    private int _total;
    private string _query = "";
    private string? _offlineWhy;
    private string? _cacheAge;

    private ActivitySubjectType? _subjectType;
    private Guid _subjectId;
    private string _subjectName = "";

    public SharingActivityPanel(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _activity = new ActivityFeedService(App.State.Api, App.State.Settings);

        _state = new PageState(FeedScroller, PageStateHost, nameof(SharingActivityPanel))
            .Copy(ActivityCopy)
            .Slots(SubLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton);
        _state.RetryRequested += () => _ = LoadAsync(reset: true);
        _state.CancelRequested += () => _cts?.Cancel();

        SearchBox.DebounceMilliseconds = 200; // filters entries already fetched
        SearchBox.TextChangedDebounced += (_, text) => { _query = text ?? ""; Render(); };

        ToolTipService.SetShowOnDisabled(LoadMoreButton, true);
        ToolTipService.SetShowOnDisabled(RefreshButton, true);

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            App.State.ConnectivityChanged -= OnConnectivityChanged;
            _cts?.Cancel();
        };
    }

    private static readonly PageCopy ActivityCopy = new()
    {
        Glyph = "",
        FilteredGlyph = "",
        Verb = "Loading",
        Noun = "entr(ies)",
        LoadingLine = "Reading what has happened lately.",
        EmptyTitle = "Nothing recorded yet",
        EmptyBody = "The launcher started keeping this log in 1.6.0, so anything shared before this "
                  + "release is not in it. Share something, publish a version or add somebody to a "
                  + "team and it will show up here.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Every entry is still here - the filter above is hiding them.",
        ErrorTitle = "Could not load the activity feed",
        OfflineTitle = "Showing the last feed you were given",
        OfflineBody = "The server is not answering ({0}). Activity is only kept server-side, so this "
                    + "is as far back as this PC can see."
    };

    // ── lifecycle ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.ConnectivityChanged += OnConnectivityChanged;
        if (_state.Kind == PageStateKind.Idle) _ = LoadAsync(reset: true);
    }

    /// <summary>Re-reads the feed. The hub's Refresh rebuilds the panel; this is for a caller that
    /// kept one.</summary>
    public void Reload() => _ = LoadAsync(reset: true);

    /// <summary>
    /// Narrows the feed to one thing's history (used by the bundle and pack pages).
    /// </summary>
    /// <remarks>The server refuses subjects the caller may not view, including deleted ones, so a
    /// deleted private pack's history cannot be read by guessing its id. A refusal here is
    /// expected.</remarks>
    public void ShowSubject(ActivitySubjectType type, Guid id, string name)
    {
        _subjectType = type;
        _subjectId = id;
        _subjectName = name;
        TitleLabel.Text = $"What has happened to {name}";
        BackToMineButton.Visibility = Visibility.Visible;
        _ = LoadAsync(reset: true);
    }

    private void OnBackToMine(object sender, RoutedEventArgs e)
    {
        _subjectType = null;
        _subjectId = Guid.Empty;
        _subjectName = "";
        TitleLabel.Text = "What has been happening";
        BackToMineButton.Visibility = Visibility.Collapsed;
        _ = LoadAsync(reset: true);
    }

    private void OnConnectivityChanged()
    {
        if (!App.State.IsOffline && _offlineWhy is not null) _ = LoadAsync(reset: true);
        else Render();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = LoadAsync(reset: true); e.Handled = true; }
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Expand();
            SearchBox.Focus();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync(reset: true);

    private void OnLoadMore(object sender, RoutedEventArgs e) => _ = LoadAsync(reset: false);

    private async Task LoadAsync(bool reset)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;

        if (reset) { _entries.Clear(); _total = 0; }
        _state.Begin(reset ? null : "Reading older entries.", refreshing: !reset && _entries.Count > 0);

        try
        {
            var page = _subjectType is { } type
                ? await _activity.ForSubjectAsync(type, _subjectId, _entries.Count, PageSize, ct)
                : await _activity.MineAsync(_entries.Count, PageSize, ct);
            if (ct.IsCancellationRequested) return;

            _offlineWhy = null;
            _cacheAge = null;
            _total = page.Total;

            // The server pages newest-first, so appending keeps the order the feed is drawn in.
            var known = _entries.Select(x => x.Id).ToHashSet();
            foreach (var entry in page.Items)
                if (known.Add(entry.Id)) _entries.Add(entry);

            // Only cache the first page of the personal feed, which is what the tab opens on.
            if (reset && _subjectType is null) ActivityFeedCache.Save(page);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (OfflineException ex)
        {
            _offlineWhy = ex.Reason ?? "the server is unreachable";
            if (_entries.Count == 0 && _subjectType is null && ActivityFeedCache.Load() is { } cached)
            {
                _entries.AddRange(cached.Items);
                _total = cached.Total;
                _cacheAge = ActivityFeedCache.AgeInWords();
                AppLog.Log("sharing", $"Activity unavailable ({_offlineWhy}); showing the last known {_entries.Count} entr(ies).");
            }
        }
        catch (SessionExpiredException)
        {
            _state.Error("your session has expired - sign in again.");
            return;
        }
        catch (Exception ex)
        {
            _state.Error(ContentBundleService.Explain(ex, "the server refused the request"), ex);
            return;
        }

        // Render after the catches rather than inside one: nothing awaits this method, so an
        // exception while rendering the offline view would escape and leave the loading bar spinning.
        try { Render(); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(SharingActivityPanel), ex);
            _state.Error("the feed could not be drawn.", ex);
        }
    }

    // ── rendering ────────────────────────────────────────────────────────────

    private void Render()
    {
        var offline = App.State.IsOffline || _offlineWhy is not null;
        var query = _query.Trim();

        var visible = query.Length == 0
            ? _entries
            : _entries.Where(e =>
                ActivityFeedService.Describe(e).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var items = new List<object>();
        string? day = null;
        foreach (var entry in visible)
        {
            var heading = ActivityFeedService.DayHeading(entry.CreatedAt);
            if (heading != day)
            {
                day = heading;
                items.Add(new ActivityDayHeader(heading));
            }
            items.Add(new ActivityRowView(entry, offline));
        }
        FeedList.ItemsSource = items;

        var more = _entries.Count < _total && query.Length == 0;
        LoadMoreButton.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = more && !offline;
        LoadMoreButton.ToolTip = offline
            ? "Older entries are only kept on the server, which is not answering."
            : $"Show the next {PageSize} entries";

        if (query.Length > 0 && visible.Count == 0 && _entries.Count > 0) _state.EmptyFiltered();

        if (visible.Count == 0 && _offlineWhy is not null)
        {
            _state.Offline(_offlineWhy, _cacheAge);
            return;
        }

        var note = _total > _entries.Count && query.Length == 0
            ? $"Showing the newest {_entries.Count:N0} of {_total:N0}."
            : null;
        _state.Content(visible.Count, note: note);
        if (_offlineWhy is not null) _state.Offline(_offlineWhy, _cacheAge);
    }

    // ── opening what an entry is about ───────────────────────────────────────

    private void OnSubjectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityRowView row) return;
        var entry = row.Entry;

        switch (entry.SubjectType)
        {
            case ActivitySubjectType.Pack:
                _shell.OpenPackDetail(entry.SubjectId, entry.SubjectName);
                break;
            case ActivitySubjectType.Mod:
                _shell.OpenModDetail(entry.SubjectId, entry.SubjectName);
                break;
            case ActivitySubjectType.ResourcePack:
                _shell.OpenResourcePackDetail(entry.SubjectId, entry.SubjectName);
                break;
            case ActivitySubjectType.Team:
                _shell.OpenTeams();
                break;
            case ActivitySubjectType.Bundle:
                SharingActions.Open(_shell, SharedFamily.Bundle, entry.SubjectId, entry.SubjectName);
                break;
            case ActivitySubjectType.World:
                SharingActions.Open(_shell, SharedFamily.World, entry.SubjectId, entry.SubjectName);
                break;
            default:
                _ = _shell.ShowMessageAsync(entry.SubjectName,
                    $"This is a {ActivityFeedService.SubjectLabel(entry.SubjectType).ToLowerInvariant()}, "
                    + "and there is no page for it from here.");
                break;
        }
    }
}
