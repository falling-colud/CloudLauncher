using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>One kind of content (instances, worlds, resource packs, mods) and what it costs.</summary>
public sealed record CloudKindVm(string Name, string SubLine, Brush Swatch, string SizeLabel,
                                 string PercentLabel, GridLength Unique, GridLength Shared,
                                 GridLength Rest);

/// <summary>One thing the user owns, in the ranked list.</summary>
/// <param name="Item">The row it was built from, so the Open button knows what to open.</param>
public sealed record CloudItemVm(string Rank, string Name, string SubLine, string SizeLabel,
                                 bool IsShared, string SharedTip, Brush Swatch,
                                 bool CanOpen, string OpenHint, CloudStorageItem Item);

/// <summary>One entry of the kind filter above the ranked list.</summary>
/// <param name="Kind">Null is "all kinds", which is the entry the list opens on.</param>
public sealed record CloudKindChoice(string? Kind, string Label);

/// <summary>
/// The Cloud storage tab of the sharing hub: what the copies of your content on the server cost, and
/// how much a delete would free.
/// </summary>
/// <remarks>
/// <para>The Storage page counts this PC's disk; this tab counts the server. Both count files that
/// several things share, so both use the same cards, meters and "unique" wording.</para>
/// <para>The headline figure is <see cref="CloudStorageItem.UniqueBytes"/>. The server stores blobs by
/// content, so <see cref="CloudStorageItem.SharedBytes"/> is charged in full to every item that
/// references it, and the per-item columns add up to more than the account uses. The top card and
/// the line above the list spell that out so the list isn't read as a total.</para>
/// <para>Signed out or offline there is nothing to show: every figure comes from one call and
/// nothing is cached locally. The tab says which case it is and never shows a zero.</para>
/// <para>The usage bar is built in its own try, and the line under it gives the same numbers in
/// words, so a failing meter can't take the tab down.</para>
/// </remarks>
public partial class SharingCloudStoragePanel : UserControl, ISharingHubTab
{
    /// <summary>Rows drawn in the ranked list before the rest collapse into one summary line. The big
    /// ones sort to the top; "Show all" is beside the filter.</summary>
    private const int ItemDisplayCap = 25;

    /// <summary>
    /// Data colours, fixed rather than derived from the theme accent.
    /// </summary>
    /// <remarks>Mods, worlds and resource packs use the Storage page's hues so the same content has the
    /// same colour on both screens. Instances have no counterpart there and take the remaining hue that
    /// isn't close to the other three.</remarks>
    private static readonly Dictionary<string, Brush> Swatches = Build(new[]
    {
        (CloudStorageKinds.Pack,         "#F2A03D"),
        (CloudStorageKinds.World,        "#3FB27F"),
        (CloudStorageKinds.ResourcePack, "#B57BEE"),
        (CloudStorageKinds.Mod,          "#5B8DEF"),
        (OtherKind,                      "#5A6274")
    });

    /// <summary>Where a kind this launcher doesn't know is filed. Kinds are strings on the wire so a
    /// newer server can add one; dropping its rows would leave bytes in the total that nothing on screen
    /// explains.</summary>
    private const string OtherKind = "other";

    private readonly MainWindow? _shell;
    private readonly PageState _state;

    private readonly ObservableCollection<CloudKindVm> _kinds = [];
    private readonly ObservableCollection<CloudItemVm> _items = [];

    private CancellationTokenSource? _work;

    /// <summary>The answer on screen, kept so the kind filter and "Show all" can redraw the ranked
    /// list without asking the server for the same count again.</summary>
    private CloudStorageBreakdown? _breakdown;

    /// <summary>Which kind the ranked list is showing, or null for all of them.</summary>
    private string? _kindFilter;

    /// <summary>Whether the ranked list is past <see cref="ItemDisplayCap"/>. Reset whenever the
    /// filter changes: "show all" of four hundred mods is not a decision about instances.</summary>
    private bool _showAll;

    /// <summary>True while the filter box is being refilled. A <c>ComboBox</c> raises
    /// SelectionChanged as its items are assigned, and without this the first paint would redraw
    /// the list it is halfway through building.</summary>
    private bool _filling;

    /// <param name="shell">The window rows navigate into. Null only in the offscreen layout
    /// harness, which has no window; every row still renders, and Open simply does nothing.</param>
    public SharingCloudStoragePanel(MainWindow? shell = null)
    {
        InitializeComponent();
        _shell = shell;

        KindList.ItemsSource = _kinds;
        ItemList.ItemsSource = _items;

        _state = new PageState(ContentScroller, PageStateHost, nameof(SharingCloudStoragePanel))
            .Copy(CloudCopy)
            .Slots(SubLabel, StatusLabel, BusyBar)
            .DisableWhileBusy(RefreshButton);
        _state.RetryRequested += () => _ = LoadAsync();

        Loaded += OnLoaded;
        Unloaded += (_, _) => _work?.Cancel();
    }

    /// <summary>Page-state copy for this tab.</summary>
    private static readonly PageCopy CloudCopy = new()
    {
        Glyph = "",
        FilteredGlyph = "",
        Verb = "Asking",
        Noun = "thing(s)",
        LoadingLine = "Asking the server what your instances, worlds, resource packs and mods are "
                    + "costing it.",
        EmptyTitle = "Nothing of yours is on the server",
        EmptyBody = "You own no instances, worlds, resource packs or mods here yet, so nothing is "
                  + "using your quota. Sharing an instance is what puts the first one up.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Nothing matched that filter.",
        ErrorTitle = "Could not read your server storage",
        OfflineTitle = "This one needs the server",
        OfflineBody = "The server is not answering ({0}), and none of this is kept on your PC, so "
                    + "there is nothing to show you until it does. The Storage page in the sidebar "
                    + "measures this PC and does not need it."
    };

    // ── loading ──────────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_state.Kind == PageStateKind.Idle) await LoadAsync();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = LoadAsync(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>The hub's Refresh button. Always a real call; nothing is cached here.</summary>
    public Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Asks the server, and paints whatever it says.
    /// </summary>
    /// <remarks>Safe to call again while one is running; the first is cancelled.</remarks>
    public async Task LoadAsync()
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;

        // Signed out: say so, rather than drawing zeroes that would read as "you own nothing".
        if (!App.State.Settings.IsLoggedIn)
        {
            ShowNothingKnown();
            _state.EmptyNext("Sign in to see this",
                             "Every figure on this tab is the server's own count of what you have "
                             + "put there, so it has nothing to answer with until this launcher is "
                             + "signed in. The Storage page in the sidebar measures this PC and "
                             + "needs no account.", CloudCopy.Glyph);
            _state.Content(0, note: "Signed out - this tab is the server's copy of your content.");
            return;
        }

        _state.Begin();
        try
        {
            var breakdown = await App.State.Api.GetMyStorageBreakdownAsync(ct);
            if (ct.IsCancellationRequested) return;
            OnUi(() =>
            {
                Render(breakdown);
                Settle(breakdown);
            });
        }
        catch (OperationCanceledException)
        {
            // A newer call cancelled this one and has already called Begin; saying anything here
            // would paint a stopped page over a call that is still running.
        }
        catch (OfflineException ex)
        {
            OnUi(() => _state.Offline(ex.Reason ?? "the server is unreachable"));
        }
        catch (Exception ex)
        {
            if (!IsCurrent(ct)) { AppLog.LogError(nameof(SharingCloudStoragePanel), ex); return; }
            OnUi(() => _state.Error("the server could not say what your content is costing", ex));
        }
    }

    /// <summary>True while this call is still the one the tab is waiting on, so an overtaken one
    /// cannot paint over the call that replaced it.</summary>
    private bool IsCurrent(CancellationToken ct) => _work is { } work && work.Token == ct;

    /// <summary>
    /// Runs UI work on the UI thread, whichever thread the continuation came back on.
    /// </summary>
    /// <remarks>The hub's Refresh calls in from outside an event handler, so continuations can land on
    /// the thread pool, where the first Visibility assignment would throw.</remarks>
    private void OnUi(Action work)
    {
        if (Dispatcher.CheckAccess()) work();
        else Dispatcher.Invoke(work);
    }

    /// <summary>Empties every list and figure, since what was on screen may belong to an account we're
    /// no longer signed into.</summary>
    private void ShowNothingKnown()
    {
        _breakdown = null;
        _kinds.Clear();
        _items.Clear();
        UsageHost.Children.Clear();
        UsageHost.ColumnDefinitions.Clear();
        KindBox.ItemsSource = null;
        ShowAllButton.Visibility = Visibility.Collapsed;
        foreach (var label in new[] { TotalLabel, TotalNote, SharedLabel, SharedNote, QuotaLabel,
                                      QuotaNote, UsageNote, KindCountLabel, ItemCountLabel,
                                      ItemsSumNote, MethodLabel })
            label.Text = "";
        SetNote(ItemNote, null);
    }

    private void Settle(CloudStorageBreakdown breakdown) =>
        _state.Content(breakdown.Items.Count,
                       countText: $"{StorageUsageService.Size(breakdown.UsedBytes)} on the server · "
                                + $"{breakdown.Items.Count:N0} thing(s)",
                       note: "Counted by the server just now. Nothing here is read from this PC.");

    // ── painting ─────────────────────────────────────────────────────────────

    /// <summary>Paints one answer over whatever is on screen.</summary>
    public void Render(CloudStorageBreakdown breakdown)
    {
        _breakdown = breakdown;
        _showAll = false;

        RenderHeadline(breakdown);
        RenderKinds(breakdown);
        RebuildKindFilter(breakdown);
        RenderItems();
        RenderMethod(breakdown);
    }

    private void RenderHeadline(CloudStorageBreakdown breakdown)
    {
        TotalLabel.Text = StorageUsageService.Size(breakdown.UsedBytes);
        TotalNote.Text = "on the server, counting every file once · "
                       + Plural(breakdown.Items.Count, "thing")
                       + " across your instances, worlds, resource packs and mods.";

        if (breakdown.SharedOnceBytes > 0)
        {
            SharedLabel.Text = StorageUsageService.Size(breakdown.SharedOnceBytes);
            SharedNote.Text = "is shared between two or more of your things. No single delete "
                            + "frees any of it.";
        }
        else
        {
            SharedLabel.Text = "Nothing shared";
            SharedNote.Text = "No file up there is held by two of your things, so every row below "
                            + "frees all of itself.";
        }

        RenderQuota(breakdown);
        RenderUsage(breakdown);
    }

    /// <summary>
    /// Used against the quota, and how much is left.
    /// </summary>
    /// <remarks>No quota is <c>0</c> on the wire. Without a quota the figure is shown without a
    /// percentage, as on the Account panel. Anything below that divides by the quota must go through
    /// here.</remarks>
    private void RenderQuota(CloudStorageBreakdown breakdown)
    {
        if (breakdown.QuotaBytes <= 0)
        {
            QuotaLabel.Text = "No limit";
            QuotaNote.Text = "This account has no storage quota, so nothing here is a fraction of "
                           + "anything.";
            QuotaNote.SetResourceReference(ForegroundProperty, "TextTertiaryBrush");
            return;
        }

        var fraction = Math.Clamp((double)breakdown.UsedBytes / breakdown.QuotaBytes, 0, 1);
        var left = Math.Max(0, breakdown.QuotaBytes - breakdown.UsedBytes);

        QuotaLabel.Text = $"{fraction * 100:0.#}% used";
        QuotaNote.Text = left == 0
            ? $"of your {StorageUsageService.Size(breakdown.QuotaBytes)} quota. It is full - uploads "
              + "are refused until you delete something."
            : $"of your {StorageUsageService.Size(breakdown.QuotaBytes)} quota · "
              + $"{StorageUsageService.Size(left)} left.";
        QuotaNote.SetResourceReference(ForegroundProperty,
                                       fraction >= 0.9 ? "DangerBrush" : "TextTertiaryBrush");
    }

    /// <summary>
    /// The page's meter: what only one item uses, what several share, and what the quota still allows.
    /// </summary>
    /// <remarks>
    /// <c>Items.Sum(UniqueBytes) + SharedOnceBytes == UsedBytes</c>; the shared band is why the rows
    /// below don't add up to the total. Built by hand because the bands are columns of one Grid (one
    /// denominator), which an ItemsControl can't fill. The caller wraps it in a try.
    /// </remarks>
    private void RenderUsage(CloudStorageBreakdown breakdown)
    {
        var unique = breakdown.Items.Sum(i => i.UniqueBytes);
        var shared = breakdown.SharedOnceBytes;
        var free = breakdown.QuotaBytes > 0
            ? Math.Max(0, breakdown.QuotaBytes - breakdown.UsedBytes)
            : 0;

        var bits = new List<string>();
        if (unique > 0)
            bits.Add($"{StorageUsageService.Size(unique)} that only one of your things uses");
        if (shared > 0)
            bits.Add($"{StorageUsageService.Size(shared)} that more than one of them shares");
        if (free > 0)
            bits.Add($"{StorageUsageService.Size(free)} of your quota still free");

        UsageNote.Text = bits.Count == 0
            ? "Nothing of yours is taking any space on the server."
            : "Left to right: " + string.Join(" · ", bits) + ".";

        try
        {
            BuildUsageBands(unique, shared, free);
        }
        catch (Exception ex)
        {
            // One band failing is not the tab failing, and the line above says it in words anyway.
            AppLog.LogError(nameof(SharingCloudStoragePanel) + ".bar", ex);
            UsageHost.Children.Clear();
            UsageHost.ColumnDefinitions.Clear();
        }
    }

    private void BuildUsageBands(long unique, long shared, long free)
    {
        UsageHost.ColumnDefinitions.Clear();
        UsageHost.Children.Clear();

        // The accent at partial opacity for the shared band, not AccentSoftBrush, which is darker than
        // the empty track and would make "shared" look like less than nothing.
        var bands = new (long Bytes, string Key, double Opacity, string Tip)[]
        {
            (unique, "AccentBrush", 1.0,
                "Bytes only one of your things references. This is what deleting things can free."),
            (shared, "AccentBrush", 0.45,
                "Bytes more than one of your things references. Stored once, and no single delete "
                + "frees any of them."),
            (free, "Surface4Brush", 1.0,
                "What your quota still allows.")
        };

        var drawn = bands.Where(b => b.Bytes > 0).ToList();
        if (drawn.Count == 0) return;

        for (var i = 0; i < drawn.Count; i++)
        {
            var band = drawn[i];
            UsageHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(band.Bytes, GridUnitType.Star)
            });

            var first = i == 0;
            var last = i == drawn.Count - 1;
            var block = new CloudLauncher.Controls.SlateBorder
            {
                Opacity = band.Opacity,
                CornerRadius = new CornerRadius(first ? 5 : 0, last ? 5 : 0, last ? 5 : 0, first ? 5 : 0),
                Margin = new Thickness(0, 0, last ? 0 : 1, 0),
                ToolTip = band.Tip
            };
            block.SetResourceReference(BackgroundProperty, band.Key);
            Grid.SetColumn(block, i);
            UsageHost.Children.Add(block);
        }
    }

    private void RenderKinds(CloudStorageBreakdown breakdown)
    {
        _kinds.Clear();

        var groups = Group(breakdown);
        var widest = groups.Count == 0 ? 0 : groups.Max(g => g.Unique + g.Shared);
        var totalUnique = groups.Sum(g => g.Unique);

        foreach (var group in groups)
        {
            var bits = new List<string> { Plural(group.Count, KindNoun(group.Kind)) };
            if (group.Versions > 0) bits.Add(Plural(group.Versions, RowNoun(group.Kind)));
            bits.Add(group.Shared > 0
                ? $"{StorageUsageService.Size(group.Shared)} shared with something else"
                : "nothing shared");

            _kinds.Add(new CloudKindVm(
                Name: KindTitle(group.Kind),
                SubLine: string.Join(" · ", bits),
                Swatch: SwatchFor(group.Kind),
                SizeLabel: StorageUsageService.Size(group.Unique),
                PercentLabel: totalUnique <= 0 ? "" : $"{group.Unique * 100.0 / totalUnique:0.#}%",
                Unique: new GridLength(group.Unique, GridUnitType.Star),
                Shared: new GridLength(group.Shared, GridUnitType.Star),
                Rest: new GridLength(Math.Max(0, widest - group.Unique - group.Shared),
                                     GridUnitType.Star)));
        }

        KindCountLabel.Text = $"{groups.Count:N0} kind(s)";
    }

    /// <summary>One entry per kind that actually has rows, biggest first, so the filter is a list of
    /// what is there rather than of what the wire contract allows.</summary>
    private void RebuildKindFilter(CloudStorageBreakdown breakdown)
    {
        var groups = Group(breakdown);
        var choices = new List<CloudKindChoice>
        {
            new(null, $"All kinds ({breakdown.Items.Count:N0})")
        };
        foreach (var group in groups)
            choices.Add(new CloudKindChoice(group.Kind, $"{KindTitle(group.Kind)} ({group.Count:N0})"));

        // Keep the chosen kind across a refresh, so F5 on a tab filtered to mods stays on mods.
        var keep = choices.FindIndex(c => c.Kind == _kindFilter);
        if (keep < 0) { keep = 0; _kindFilter = null; }

        _filling = true;
        try
        {
            KindBox.ItemsSource = choices;
            KindBox.SelectedIndex = keep;
        }
        finally { _filling = false; }
    }

    private void OnKindFilter(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        _kindFilter = (KindBox.SelectedItem as CloudKindChoice)?.Kind;
        _showAll = false;
        RenderItems();
    }

    private void OnShowAll(object sender, RoutedEventArgs e)
    {
        _showAll = !_showAll;
        RenderItems();
    }

    /// <summary>
    /// The ranked list, for whichever kind is selected.
    /// </summary>
    /// <remarks>Ranked by unique bytes, the only column a delete frees. The line above the list says
    /// what the rows add up to and, when unfiltered, what none of them include.</remarks>
    private void RenderItems()
    {
        _items.Clear();
        if (_breakdown is not { } breakdown) return;

        var ranked = breakdown.Items
            .Where(i => _kindFilter is null || Normalise(i.Kind) == _kindFilter)
            .OrderByDescending(i => i.UniqueBytes)
            .ThenByDescending(i => i.SharedBytes)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var shown = _showAll ? ranked.Count : Math.Min(ItemDisplayCap, ranked.Count);
        for (var rank = 0; rank < shown; rank++)
        {
            var item = ranked[rank];
            var kind = Normalise(item.Kind);
            var bits = new List<string>
            {
                kind == OtherKind && item.Kind.Length > 0 ? item.Kind : KindNoun(kind),
                item.Versions > 0
                    ? Plural(item.Versions, RowNoun(kind))
                    : "nothing uploaded yet"
            };
            if (item.SharedBytes > 0)
                bits.Add($"{StorageUsageService.Size(item.SharedBytes)} shared");

            _items.Add(new CloudItemVm(
                Rank: $"{rank + 1}.",
                Name: item.Name,
                SubLine: string.Join(" · ", bits),
                SizeLabel: StorageUsageService.Size(item.UniqueBytes),
                IsShared: item.SharedBytes > 0,
                SharedTip: $"{StorageUsageService.Size(item.SharedBytes)} of what this references is "
                         + "referenced by something else of yours too. Deleting this frees none of "
                         + "that.",
                Swatch: SwatchFor(kind),
                CanOpen: HasPage(kind),
                OpenHint: OpenHint(kind),
                Item: item));
        }

        RenderItemCounts(breakdown, ranked, shown);
    }

    private void RenderItemCounts(CloudStorageBreakdown breakdown,
                                  IReadOnlyList<CloudStorageItem> ranked, int shown)
    {
        var noun = _kindFilter is null ? "thing" : KindNoun(_kindFilter);

        // Only say "top N" when something was left out.
        ItemCountLabel.Text = ranked.Count == 0 ? ""
            : shown >= ranked.Count ? Plural(ranked.Count, noun)
            : $"top {shown:N0} of {ranked.Count:N0}";

        var over = ranked.Count > ItemDisplayCap;
        ShowAllButton.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
        ShowAllButton.Content = _showAll
            ? $"Show the top {ItemDisplayCap}"
            : $"Show all {ranked.Count:N0}";

        var hidden = ranked.Skip(shown).ToList();
        var hiddenBytes = hidden.Sum(i => i.UniqueBytes);
        SetNote(ItemNote, hidden.Count == 0
            ? null
            : hiddenBytes > 0
                ? $"{hidden.Count:N0} smaller {noun}(s) are not listed, "
                  + $"{StorageUsageService.Size(hiddenBytes)} between them."
                : $"{hidden.Count:N0} {noun}(s) are not listed. None of them has any bytes of its "
                  + "own - they are either empty or made entirely of files something else keeps.");

        RenderSumNote(breakdown, ranked);
    }

    /// <summary>
    /// What the rows below add up to, and what is not in any of them.
    /// </summary>
    /// <remarks>Keeps the ranked list from being read as a total. Computed from the same two figures
    /// the top card shows, so they can't drift apart.</remarks>
    private void RenderSumNote(CloudStorageBreakdown breakdown, IReadOnlyList<CloudStorageItem> ranked)
    {
        var totalUnique = breakdown.Items.Sum(i => i.UniqueBytes);
        var shownUnique = ranked.Sum(i => i.UniqueBytes);

        if (_kindFilter is not null)
        {
            ItemsSumNote.Text = $"These {KindNoun(_kindFilter)} rows add up to "
                              + $"{StorageUsageService.Size(shownUnique)} of the "
                              + $"{StorageUsageService.Size(totalUnique)} that deleting things "
                              + "could free.";
            return;
        }

        ItemsSumNote.Text = breakdown.SharedOnceBytes > 0
            ? $"Every row below added together is {StorageUsageService.Size(totalUnique)}. The "
              + $"{StorageUsageService.Size(breakdown.SharedOnceBytes)} that more than one of your "
              + "things shares is in none of them, because no single delete frees it. "
              + Dominance(ranked, totalUnique)
            : $"Every row below added together is {StorageUsageService.Size(totalUnique)}, which is "
              + "all of it - nothing on the server is held by two of your things. "
              + Dominance(ranked, totalUnique);
    }

    /// <summary>The one sentence that says whether this is a few big things or a long tail.</summary>
    private static string Dominance(IReadOnlyList<CloudStorageItem> ranked, long totalUnique)
    {
        if (ranked.Count < 3 || totalUnique <= 0) return "";
        var topBytes = ranked.Take(3).Sum(i => i.UniqueBytes);
        if (topBytes <= 0) return "";

        return $"Your three biggest are {StorageUsageService.Size(topBytes)} of that - "
             + $"{topBytes * 100.0 / totalUnique:0.#}%.";
    }

    private void RenderMethod(CloudStorageBreakdown breakdown)
    {
        // What adding up the two per-item columns would claim, and how much of that is double-counted.
        // It goes in the receipt, not beside the total: it explains the arithmetic.
        var gross = breakdown.Items.Sum(i => i.UniqueBytes + i.SharedBytes);
        var twice = gross - breakdown.UsedBytes;

        MethodLabel.Text =
            "The server stores every file by its contents, so a file that two of your things both "
            + "contain is stored once and kept alive by both of them. Those bytes are charged to "
            + "each thing that references them, which is why 'unique' is the only column that "
            + "answers what a delete gives back."
            + (twice > 0
               ? $" Adding both columns of every row up would report {StorageUsageService.Size(gross)}, "
                 + $"which is {StorageUsageService.Size(twice)} of the same bytes counted more than "
                 + "once.\n\n"
               : "\n\n")
            + "Instances, worlds, resource packs and mods are counted. Content bundles are not, and "
            + "are not in the total either: they keep files alive like everything else, but your "
            + "quota has never been charged for them. Instances you have never shared are not here "
            + "either - nothing of theirs has ever been uploaded.\n\n"
            + "This is the server's own count, asked for when this tab was opened. The Storage page "
            + "in the sidebar answers the same question about this PC - the two have nothing to say "
            + "about each other, because what is on your disk is not what is on the server.";
    }

    // ── opening items ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the page that owns the item on this row.
    /// </summary>
    /// <remarks>This tab never deletes: a delete button next to a figure that may be stale invites
    /// removing the wrong thing. The owning page has the full context.</remarks>
    private void OnOpenItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudItemVm row) return;
        if (_shell is null) return;

        switch (Normalise(row.Item.Kind))
        {
            case CloudStorageKinds.Pack:
                _shell.OpenPackDetail(row.Item.Id, row.Item.Name);
                break;
            case CloudStorageKinds.Mod:
                _shell.OpenModDetail(row.Item.Id, row.Item.Name);
                break;
            case CloudStorageKinds.ResourcePack:
                _shell.OpenResourcePackDetail(row.Item.Id, row.Item.Name);
                break;
            case CloudStorageKinds.World:
                // The save it came from when this PC has one, else the world's entry in Download a
                // world, same as the Overview's Open.
                SharingActions.Open(_shell, SharedFamily.World, row.Item.Id, row.Item.Name);
                break;
        }
    }

    /// <summary>Whether this launcher has a page for a kind. Every kind the server counts has one; a
    /// hosted world opens as its save, or in Download a world.</summary>
    private static bool HasPage(string kind) =>
        kind is CloudStorageKinds.Pack or CloudStorageKinds.Mod or CloudStorageKinds.ResourcePack
             or CloudStorageKinds.World;

    private static string OpenHint(string kind) => kind switch
    {
        CloudStorageKinds.Pack =>
            "Open this instance. Its Share & sync tab is what puts these files on the server.",
        CloudStorageKinds.Mod =>
            "Open this mod. Its uploaded versions are listed there, and deleting one stops it "
            + "costing you this.",
        CloudStorageKinds.ResourcePack =>
            "Open this resource pack. Its uploaded versions are listed there, and deleting one "
            + "stops it costing you this.",
        CloudStorageKinds.World =>
            "Open this world. Its uploaded versions are listed there, and deleting one stops it "
            + "costing you this.",
        _ => "This launcher has no page for that kind of thing."
    };

    // ── labels ───────────────────────────────────────────────────────────────

    /// <summary>Kinds folded to the five this page draws, ordered by what deleting each would free.
    /// Shared by the list, the filter and the by-kind card so the three agree.</summary>
    private static List<(string Kind, long Unique, long Shared, int Count, long Versions)> Group(
        CloudStorageBreakdown breakdown) =>
        breakdown.Items
            .GroupBy(i => Normalise(i.Kind))
            .Select(g => (
                Kind: g.Key,
                Unique: g.Sum(i => i.UniqueBytes),
                Shared: g.Sum(i => i.SharedBytes),
                Count: g.Count(),
                Versions: g.Sum(i => (long)i.Versions)))
            .OrderByDescending(g => g.Unique)
            .ThenByDescending(g => g.Shared)
            .ToList();

    private static string Normalise(string kind) =>
        Swatches.ContainsKey(kind) ? kind : OtherKind;

    /// <summary>The card heading for a kind. An unknown kind is filed under one heading rather than
    /// having the server's own spelling drawn as a title.</summary>
    private static string KindTitle(string kind) => kind switch
    {
        CloudStorageKinds.Pack => "Instances",
        CloudStorageKinds.World => "Worlds",
        CloudStorageKinds.ResourcePack => "Resource packs",
        CloudStorageKinds.Mod => "Mods",
        _ => "Other"
    };

    /// <summary>What one of them is called, for counting.</summary>
    private static string KindNoun(string kind) => kind switch
    {
        CloudStorageKinds.Pack => "instance",
        CloudStorageKinds.World => "world",
        CloudStorageKinds.ResourcePack => "resource pack",
        CloudStorageKinds.Mod => "mod",
        _ => "other thing"
    };

    /// <summary>What the rows behind one of them are. An instance's are the files in its manifest;
    /// everything else's are uploaded versions.</summary>
    private static string RowNoun(string kind) =>
        kind == CloudStorageKinds.Pack ? "file" : "version";

    /// <summary>Spelled-out plural ("3 mods", "1 version") rather than "(s)", since these read inside
    /// a sentence.</summary>
    private static string Plural(long n, string one) => $"{n:N0} {one}{(n == 1 ? "" : "s")}";

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static void SetNote(TextBlock target, string? text)
    {
        target.Text = text ?? "";
        target.Visibility = text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush SwatchFor(string kind) =>
        Swatches.TryGetValue(kind, out var brush) ? brush : Swatches[OtherKind];

    private static Dictionary<string, Brush> Build((string Key, string Hex)[] pairs)
    {
        var map = new Dictionary<string, Brush>(StringComparer.Ordinal);
        foreach (var (key, hex) in pairs)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            brush.Freeze();   // shared across every row and every repaint
            map[key] = brush;
        }
        return map;
    }
}
