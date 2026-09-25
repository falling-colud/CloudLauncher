using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Anything on this page that names a place on disk, so one Reveal handler serves all four
/// lists.</summary>
public interface IStoragePathRow
{
    string Path { get; }
}

/// <summary>One category band and its row under the bar.</summary>
public sealed record StorageCategoryVm(string Name, string Note, Brush Swatch, string SizeLabel,
                                       string PercentLabel, GridLength Fill, GridLength Rest);

/// <summary>One instance, or one of the four things that is not an instance.</summary>
public sealed record StorageOwnerVm(string Glyph, string Name, string SubLine, string SizeLabel,
                                    GridLength Unique, GridLength Shared, GridLength Rest,
                                    bool CanOpen, Guid PackId, string Path) : IStoragePathRow;

/// <summary>One row of the biggest-things list.</summary>
public sealed record StorageItemVm(string Rank, string Name, string SubLine, string SizeLabel,
                                   bool IsShared, string Path) : IStoragePathRow;

/// <summary>One folder the launcher would write again, with the reason it is safe to lose.</summary>
public sealed record StorageReclaimVm(string Name, string SubLine, string Evidence, string SizeLabel,
                                      string Path) : IStoragePathRow;

/// <summary>The Storage page: how much of this PC's disk the launcher uses, what it is, and what
/// could be reclaimed.</summary>
/// <remarks><para>The library hard-links one copy of a mod or pack into every instance that uses
/// it, so adding up folder sizes overcounts badly. <see cref="StorageUsageService"/> counts
/// physical files, and the page keeps three numbers apart: on disk, shared, and unique to one
/// instance (the only one that says what deleting it would free).</para>
/// <para>Cached as an <see cref="IReusablePage"/> because a full pass can take minutes. The
/// remembered pass is painted immediately with its age, and a new one only starts on its own after
/// <see cref="AutoRescanAfter"/>. Stopping leaves the screen as it was; a cancelled pass writes
/// nothing.</para>
/// <para>Nothing here deletes files: the reclaimable list only opens Explorer. The safe delete is
/// the Cleanup tab on an instance's Files page, which moves files into that instance's
/// <c>.trash</c>.</para></remarks>
public partial class SharingStoragePanel : Page, IReusablePage, IRefreshablePage
{
    /// <summary>How old a remembered pass may be before opening the page rescans the disk by
    /// itself. Below this the age is shown and Refresh rescans on demand.</summary>
    private static readonly TimeSpan AutoRescanAfter = TimeSpan.FromHours(6);

    /// <summary>Reclaimable rows drawn; the rest collapse into one summary line. The big ones sort
    /// to the top.</summary>
    private const int ReclaimDisplayCap = 25;

    /// <summary>Data colours, fixed rather than derived from the theme accent.</summary>
    /// <remarks>A category should keep its colour between visits; hues derived from the accent
    /// would reshuffle whenever the accent changes. <see cref="PriorityPalette"/> is fixed for the
    /// same reason.</remarks>
    private static readonly Dictionary<StorageCategory, Brush> Swatches = Build(new[]
    {
        (StorageCategory.Mods,          "#5B8DEF"),
        (StorageCategory.Worlds,        "#3FB27F"),
        (StorageCategory.ResourcePacks, "#B57BEE"),
        (StorageCategory.ShaderPacks,   "#F2A03D"),
        (StorageCategory.ConfigScripts, "#3FBFC4"),
        (StorageCategory.Logs,          "#8892A6"),
        (StorageCategory.Screenshots,   "#EE6FA0"),
        (StorageCategory.ServerFiles,   "#6C63D6"),
        (StorageCategory.Runtime,       "#D4785A"),
        (StorageCategory.Java,          "#C2B04A"),
        (StorageCategory.Profile,       "#7D9EA8"),
        (StorageCategory.Other,         "#5A6274")
    });

    private readonly MainWindow? _shell;
    private readonly PageState _state;

    // Created on first load rather than in the field initialiser, so the constructor only builds
    // controls. A constructor that touches global state throws where nothing can report it, and the
    // nav click shows a blank frame.
    private StorageUsageService? _storage;

    private readonly ObservableCollection<StorageCategoryVm> _categories = [];
    private readonly ObservableCollection<StorageOwnerVm> _instances = [];
    private readonly ObservableCollection<StorageOwnerVm> _globals = [];
    private readonly ObservableCollection<StorageItemVm> _items = [];
    private readonly ObservableCollection<StorageReclaimVm> _reclaim = [];

    private CancellationTokenSource? _work;
    private StorageReport? _report;

    /// <param name="shell">Null only when there is no window to navigate; the panel's one
    /// navigation action then hides itself.</param>
    public SharingStoragePanel(MainWindow? shell)
    {
        InitializeComponent();
        _shell = shell;

        CategoryList.ItemsSource = _categories;
        InstanceList.ItemsSource = _instances;
        GlobalList.ItemsSource = _globals;
        ItemList.ItemsSource = _items;
        ReclaimList.ItemsSource = _reclaim;

        _state = new PageState(ContentScroller, PageStateHost, nameof(SharingStoragePanel))
            .Copy(StorageCopy)
            .Slots(CountLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton);
        _state.RetryRequested += () => _ = LoadAsync(force: true);
        _state.CancelRequested += () => _work?.Cancel();

        Loaded += OnLoaded;
        Unloaded += (_, _) => _work?.Cancel();
    }

    /// <summary>The house strings for this page.</summary>
    private static readonly PageCopy StorageCopy = new()
    {
        Glyph = "",
        FilteredGlyph = "",
        Verb = "Counting",
        Noun = "root(s)",
        LoadingLine = "Reading every instance, the shared copies, the Minecraft runtime and the "
                    + "launcher's own folder. This walks the whole disk once, so it is slow the "
                    + "first time.",
        EmptyTitle = "Nothing to measure yet",
        EmptyBody = "No instances, no shared copies and no downloaded runtime - so there is nothing "
                  + "on this PC for the launcher to be using.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Nothing matched that filter.",
        ErrorTitle = "Could not measure your storage",
        OfflineTitle = "Showing what is on this PC",
        OfflineBody = "The server is not answering ({0}), which changes nothing here: every number "
                    + "on this page comes off your own disk."
    };

    // ── loading ──────────────────────────────────────────────────────────────

    /// <remarks>Not guarded on <see cref="PageStateKind.Idle"/>: the page is kept alive, so this
    /// runs on every reopen, and <see cref="LoadAsync"/> with <c>force: false</c> paints the
    /// remembered pass and only rescans once it is older than
    /// <see cref="AutoRescanAfter"/>.</remarks>
    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadAsync(force: false);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = LoadAsync(force: true); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync(force: true);

    /// <summary>The offline banner's Retry, the only caller of <see cref="IRefreshablePage"/> on a
    /// master page.</summary>
    /// <remarks><c>force: false</c>, unlike the page's own Refresh button: the server coming back
    /// changes nothing on this disk, so it isn't worth a long rescan. Implemented so Retry keeps
    /// the page instead of rebuilding it and losing the scroll position.</remarks>
    public Task RefreshAsync() => LoadAsync(force: false);

    /// <summary>Paints the remembered pass, then walks the disk if that pass is missing or
    /// old.</summary>
    /// <remarks>Safe to call while one is running: the earlier one is cancelled.</remarks>
    public async Task LoadAsync(bool force)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;

        try
        {
            var storage = _storage ??= new StorageUsageService(App.State.Settings);
            await StorageUsageService.WarmAsync();
            if (ct.IsCancellationRequested) return;

            var cached = storage.Cached();
            if (cached is not null)
                OnUi(() =>
                {
                    Render(cached);
                    Settle(cached, live: false);
                });

            var fresh = DateTimeOffset.UtcNow - (cached?.TakenUtc ?? DateTimeOffset.MinValue) < AutoRescanAfter;
            if (!force && cached is not null && fresh)
            {
                OnUi(() => _state.Note(Provenance(cached) + " Refresh re-reads every folder."));
                return;
            }

            OnUi(() =>
            {
                BusyBar.Value = 0;   // or the bar opens where the last pass left it
                _state.Begin(StorageCopy.LoadingLine);
            });

            var progress = new Progress<StorageProgress>(p => OnUi(() => ShowProgress(p)));
            var report = await storage.ScanAsync(progress, ct);
            if (ct.IsCancellationRequested) return;

            OnUi(() =>
            {
                Render(report);
                Settle(report, live: true);
            });
        }
        catch (OperationCanceledException)
        {
            // A newer pass cancelled this one and has already called Begin, so showing "stopped"
            // here would paint over a running scan.
            if (!IsCurrent(ct)) return;

            // Stopped. What was painted was true when read, so it stays; the service wrote nothing,
            // so the remembered pass is untouched too.
            OnUi(() =>
            {
                // With nothing on screen, Cancelled would fall back to the empty panel and claim
                // there are no instances, library or runtime. Say it was stopped instead.
                if (_report is null)
                    _state.EmptyNext("Stopped before anything was counted",
                                     "Nothing was read, so there is nothing to show. Refresh starts "
                                     + "the count again.", StorageCopy.Glyph);

                _state.Cancelled(_report is null
                    ? "Stopped before anything had been counted."
                    : Provenance(_report) + " Stopped before the new count finished.");
            });
        }
        catch (Exception ex)
        {
            if (!IsCurrent(ct)) { AppLog.LogError(nameof(SharingStoragePanel), ex); return; }
            OnUi(() => _state.Error("your folders could not all be read", ex));
        }
    }

    /// <summary>True while this pass is still the one the page is waiting on. Everything a pass
    /// paints goes through this, so an overtaken pass can't paint over its replacement.</summary>
    private bool IsCurrent(CancellationToken ct) => _work is { } work && work.Token == ct;

    /// <summary>Runs UI work on the UI thread, whichever thread the continuation came back on.</summary>
    /// <remarks>The offline banner's Retry calls in from outside an event handler, so the
    /// continuation can land on the thread pool (same as in
    /// <see cref="SharingOverviewPanel"/>).</remarks>
    private void OnUi(Action work)
    {
        if (Dispatcher.CheckAccess()) work();
        else Dispatcher.Invoke(work);
    }

    private void ShowProgress(StorageProgress p)
    {
        if (p.Total > 0) BusyBar.Value = Math.Clamp((double)p.Done / p.Total, 0, 1);
        _state.Progress($"{p.What} - {p.Done:N0} of {p.Total:N0} · {p.FilesSoFar:N0} file(s), "
                        + $"{StorageUsageService.Size(p.BytesSoFar)} seen so far");
    }

    /// <summary>The count slot, the status line and the receipt, once a pass has an answer.</summary>
    private void Settle(StorageReport report, bool live)
    {
        var instances = report.Instances.Count();
        _state.Content(report.Owners.Count,
                       countText: $"{StorageUsageService.Size(report.OnDiskBytes)} on disk · "
                                + $"{instances:N0} instance(s)",
                       note: Provenance(report) + (live ? "" : " Refresh re-reads every folder."));
    }

    private static string Provenance(StorageReport report) =>
        $"Counted {report.AgeInWords ?? "just now"}."
        + (report.Approximate ? " Part of the shared-file check was skipped, so the total may read "
                              + "a little high." : "");

    // ── painting ─────────────────────────────────────────────────────────────

    /// <summary>Paints one report over whatever is on screen.</summary>
    /// <remarks>Separate from the load so remembered and fresh passes share one code path and
    /// render the same.</remarks>
    public void Render(StorageReport report)
    {
        _report = report;

        RenderHeadline(report);
        RenderCategories(report);
        RenderOwners(report);
        RenderItems(report);
        RenderReclaimable(report);
        RenderMethod(report);
    }

    private void RenderHeadline(StorageReport report)
    {
        TotalLabel.Text = StorageUsageService.Size(report.OnDiskBytes);

        var instances = report.Instances.Count();
        TotalNote.Text = $"on disk, counting every file once · {report.TotalFiles:N0} file(s) across "
                       + $"{instances:N0} instance(s), the shared copies, the Minecraft runtime and "
                       + "the launcher's own folder.";

        if (report.SharedBytes > 0)
        {
            SharedLabel.Text = StorageUsageService.Size(report.SharedBytes) + " is shared";
            SharedNote.Text = $"{report.SharedFiles:N0} file(s) that more than one instance - or the "
                            + "launcher's own copy and an instance - reach. They exist once. Adding the folders "
                            + $"up instead would report {StorageUsageService.Size(report.GrossBytes)}, "
                            + $"which is {StorageUsageService.Size(report.SavedByLinks)} of the same "
                            + "bytes counted twice.";
        }
        else
        {
            SharedLabel.Text = "Nothing is shared yet";
            SharedNote.Text = "No file on this PC is reachable from two places. Putting a mod or a "
                            + "pack in the shared copies and applying it to several instances is "
                            + "what starts changing that.";
        }

        DriveLabel.Text = report.Volumes.Count == 0
            ? ""
            : string.Join("\n", report.Volumes.Select(
                v => $"{v.Name.TrimEnd('\\')}  {StorageUsageService.Size(v.FreeBytes)} free of "
                   + StorageUsageService.Size(v.TotalBytes)));
    }

    /// <summary>The stacked bar, one band per category.</summary>
    /// <remarks>Built by hand: the bands are columns of one Grid so they share one denominator,
    /// which an ItemsControl can't do. Wrapped in its own try so a failure in this decorative part
    /// doesn't take down the page.</remarks>
    private void BuildComposition(IReadOnlyList<StorageCategoryRow> bands)
    {
        CompositionHost.ColumnDefinitions.Clear();
        CompositionHost.Children.Clear();
        if (bands.Count == 0) return;

        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            CompositionHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(band.Bytes, GridUnitType.Star)
            });

            var first = i == 0;
            var last = i == bands.Count - 1;
            var block = new CloudLauncher.Controls.SlateBorder
            {
                Background = SwatchFor(band.Category),
                CornerRadius = new CornerRadius(first ? 7 : 0, last ? 7 : 0, last ? 7 : 0, first ? 7 : 0),
                Margin = new Thickness(0, 0, last ? 0 : 1, 0),
                ToolTip = $"{band.Name} - {StorageUsageService.Size(band.Bytes)}"
            };
            Grid.SetColumn(block, i);
            CompositionHost.Children.Add(block);
        }
    }

    private void RenderCategories(StorageReport report)
    {
        _categories.Clear();

        var rows = report.Categories.Where(c => c.Bytes > 0).ToList();
        var biggest = rows.Count == 0 ? 0 : rows.Max(c => c.Bytes);
        var total = rows.Sum(c => c.Bytes);

        foreach (var row in rows)
            _categories.Add(new StorageCategoryVm(
                row.Name, row.Note, SwatchFor(row.Category),
                StorageUsageService.Size(row.Bytes),
                total <= 0 ? "" : $"{row.Bytes * 100.0 / total:0.#}%",
                new GridLength(row.Bytes, GridUnitType.Star),
                new GridLength(Math.Max(0, biggest - row.Bytes), GridUnitType.Star)));

        CategoryCountLabel.Text = $"{rows.Count:N0} kind(s)";

        try { BuildComposition(rows); }
        catch (Exception ex)
        {
            // One band failing is not the page failing. The rows below say the same thing in words.
            AppLog.LogError(nameof(SharingStoragePanel) + ".bar", ex);
            CompositionHost.Children.Clear();
        }

        CompositionNote.Text = rows.Count == 0
            ? ""
            : "The bands are the rows below, in the same colours."
              + (report.Approximate
                 ? " Part of the shared-file check was skipped on this pass, so the total may read a "
                 + "little high."
                 : "");
    }

    private void RenderOwners(StorageReport report)
    {
        _instances.Clear();
        _globals.Clear();

        var all = report.Owners.ToList();
        var widest = all.Count == 0 ? 0 : all.Max(o => o.UniqueBytes + o.SharedBytes);

        foreach (var owner in report.Instances) _instances.Add(ToVm(owner, widest));
        foreach (var owner in report.Globals) _globals.Add(ToVm(owner, widest));

        InstanceCountLabel.Text = $"{_instances.Count:N0} instance(s)";
        GlobalCard.Visibility = _globals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SetNote(InstanceNote, _instances.Count == 0
            ? "No instance folders were found under your instances folder. That is what the launcher "
            + "measures, so everything above is the shared runtime and the launcher's own folder."
            : null);
    }

    private StorageOwnerVm ToVm(StorageOwnerRow owner, long widest)
    {
        var isInstance = owner.Kind == StorageOwnerKind.Instance;
        var rest = Math.Max(0, widest - owner.UniqueBytes - owner.SharedBytes);

        var bits = new List<string> { $"{owner.Files:N0} file(s)" };
        if (owner.SharedBytes > 0)
            bits.Add($"{StorageUsageService.Size(owner.SharedBytes)} shared with something else");
        if (!isInstance)
            bits.Add(owner.Kind switch
            {
                StorageOwnerKind.Library => "one copy of everything you share into instances",
                StorageOwnerKind.Runtime => "global - every instance runs on it",
                StorageOwnerKind.Java => "global - downloaded when a pack needs a Java you do not have",
                _ => "settings and the launcher's own caches"
            });

        return new StorageOwnerVm(
            Glyph: owner.Kind switch
            {
                StorageOwnerKind.Instance => "",
                StorageOwnerKind.Library => "",
                StorageOwnerKind.Runtime => StorageUsageService.Glyph(StorageCategory.Runtime),
                StorageOwnerKind.Java => StorageUsageService.Glyph(StorageCategory.Java),
                _ => StorageUsageService.Glyph(StorageCategory.Profile)
            },
            Name: owner.Name,
            SubLine: string.Join(" · ", bits),
            SizeLabel: StorageUsageService.Size(owner.UniqueBytes),
            Unique: new GridLength(owner.UniqueBytes, GridUnitType.Star),
            Shared: new GridLength(owner.SharedBytes, GridUnitType.Star),
            Rest: new GridLength(rest, GridUnitType.Star),
            CanOpen: isInstance && _shell is not null && owner.PackId != Guid.Empty,
            PackId: owner.PackId,
            Path: owner.Path);
    }

    private void RenderItems(StorageReport report)
    {
        _items.Clear();
        var rank = 0;
        foreach (var item in report.Items.Where(i => i.Bytes > 0))
        {
            rank++;
            _items.Add(new StorageItemVm(
                $"{rank}.", item.Name,
                item.Where.Length == 0 ? "" : "in " + item.Where + $" · {item.Files:N0} file(s)",
                StorageUsageService.Size(item.Bytes), item.IsShared, item.Path));
        }
        ItemCountLabel.Text = _items.Count == 0 ? "" : $"top {_items.Count:N0}";
    }

    private void RenderReclaimable(StorageReport report)
    {
        _reclaim.Clear();

        var rows = report.Reclaimable.Where(r => r.Bytes > 0).ToList();
        foreach (var row in rows.Take(ReclaimDisplayCap))
            _reclaim.Add(new StorageReclaimVm(
                row.Name,
                row.Where + $" · {row.Files:N0} file(s)",
                row.Evidence,
                StorageUsageService.Size(row.Bytes),
                row.Path));

        ReclaimTotalLabel.Text = rows.Count == 0
            ? "nothing to clear"
            : StorageUsageService.Size(report.ReclaimableBytes) + " in total";

        var hidden = rows.Skip(ReclaimDisplayCap).ToList();
        SetNote(ReclaimMore, hidden.Count == 0
            ? null
            : $"{hidden.Count:N0} smaller folder(s) of the same kinds are not listed, "
              + $"{StorageUsageService.Size(hidden.Sum(r => r.Bytes))} between them.");
    }

    private void RenderMethod(StorageReport report)
    {
        MethodLabel.Text =
            "Every file is counted once and is identified by where it sits on the volume rather than "
            + "by its name, so a mod the launcher hard-linked into six instances is one copy "
            + "here and not six. Everything in the shared copies and under each instance's local/ "
            + "folder is checked; inside game/ and server-run/ anything "
            + $"{StorageUsageService.Size(StorageUsageService.ProbeFloorBytes)} or larger is checked, "
            + "along with anything the launch overlay put there. Saves, logs, screenshots, the "
            + "Minecraft runtime and the Java folders are not checked, because nothing links into "
            + "them - a world is copied into an instance and never shared, which is the one rule "
            + "that makes saves safe to add up plainly. The effect of all that is a total that can "
            + "read slightly high and can never read low.\n\n"
            + $"This pass was taken {TimeFormat.DateTime(report.TakenUtc)} and read "
            + $"{report.TotalFiles:N0} file(s). Nothing on this page watches the disk: a launch, a "
            + "sync or an afternoon of playing will move these numbers and the launcher will not "
            + "notice until you press Refresh.";

        ProblemsLabel.Text = string.Join("  ", report.Problems);
    }

    private static void SetNote(TextBlock target, string? text)
    {
        target.Text = text ?? "";
        target.Visibility = text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush SwatchFor(StorageCategory category) =>
        Swatches.TryGetValue(category, out var brush) ? brush : Swatches[StorageCategory.Other];

    private static Dictionary<StorageCategory, Brush> Build((StorageCategory Key, string Hex)[] pairs)
    {
        var map = new Dictionary<StorageCategory, Brush>();
        foreach (var (key, hex) in pairs)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            brush.Freeze();   // shared across every row and every repaint
            map[key] = brush;
        }
        return map;
    }

    // ── the two verbs ────────────────────────────────────────────────────────

    private void OnOpenOwner(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StorageOwnerVm row) return;
        if (_shell is null || row.PackId == Guid.Empty) return;
        _shell.OpenPackDetail(row.PackId, row.Name);
    }

    /// <summary>Opens the folder in Explorer; this page never deletes anything itself.</summary>
    /// <remarks>If the folder is gone, opens the nearest parent that still exists, since the
    /// figures are not live and the folder may have been deleted since the count.</remarks>
    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IStoragePathRow row) return;

        try
        {
            if (File.Exists(row.Path))
            {
                if (!SafeLaunch.RevealFile(row.Path))
                    _state.Note("Explorer would not open that folder. The error is in the launcher log.");
                return;
            }

            var dir = row.Path;
            while (dir.Length > 0 && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir) ?? "";

            if (dir.Length == 0)
            {
                _state.Note("That folder is not there any more. Refresh to count again.");
                return;
            }

            if (!SafeLaunch.OpenFolder(dir))
                _state.Note("Explorer would not open that folder. The error is in the launcher log.");
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(SharingStoragePanel), ex);
            _state.Note("Explorer would not open that folder. The error is in the launcher log.");
        }
    }
}
