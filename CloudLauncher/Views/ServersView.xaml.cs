using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The Servers page: the multiplayer servers across every instance, their live status, and a console
/// for the ones the user administers.
/// </summary>
/// <remarks>
/// <para>Minecraft keeps a server list per instance in <c>servers.dat</c>. This page reads all of them,
/// merges entries with the same address into one row, pings them, and lets servers be added, edited,
/// copied between instances, joined directly and driven over RCON.</para>
/// <para><c>servers.dat</c> is ignored by the pack rules, so nothing written here is synced to a
/// shared pack; the add/edit card says so.</para>
/// <para>Scans, pings and RCON run off the UI thread, and an unreachable server is a row state, never
/// a dialog. The lists are local files and pings go to the game servers, so nothing here is disabled
/// when CloudLauncher's own server is offline.</para>
/// <para>Counts, empty panels and errors go through <see cref="PageState"/>, and filling the instance
/// ComboBox is guarded by <see cref="Reentrancy"/> so its SelectionChanged can't rebuild the page
/// before anything has been read.</para>
/// </remarks>
public partial class ServersView : Page, IReusablePage, IRefreshablePage
{
    /// <summary>How many servers are pinged at once. High enough that a list of thirty finishes in a
    /// couple of seconds, low enough not to open thirty sockets at a stroke on a home connection.</summary>
    private const int PingConcurrency = 8;

    /// <summary>Commands kept per server for the up-arrow history. Long enough to cover a session's
    /// worth of admin, short enough that settings.json stays small.</summary>
    private const int MaxCommandHistory = 60;

    /// <summary>The size the server-list-ping protocol specifies for a favicon, and the width every
    /// one of them is decoded at. See <see cref="DecodeFavicon"/>.</summary>
    private const int FaviconPixelSize = 64;

    /// <summary>The longest base64 favicon this will try to decode, the same limit the ping applies.
    /// A 64x64 PNG is a few kilobytes.</summary>
    private const int MaxFaviconBase64Length = MinecraftServerPing.MaxFaviconLength;

    /// <summary>The largest source image decoded, on either side. A favicon is 64x64 by protocol; this
    /// allows for bigger ones while keeping a hostile one cheap to decode.</summary>
    private const int MaxFaviconSourcePixels = 1024;

    private readonly MainWindow _shell;
    private readonly ServerListService _servers = new(App.State.Packs);
    private readonly ObservableCollection<ServerRow> _rows = new();

    /// <summary>Owns every count, empty panel, error and busy affordance on this page.</summary>
    private readonly PageState _state;

    /// <summary>Held while the instance ComboBox is being filled, so the SelectionChanged it raises
    /// synchronously is not mistaken for the user picking an instance.</summary>
    private readonly Reentrancy _filling = new();

    /// <summary>True once the server lists have actually been read. Until then <see cref="Refresh"/>
    /// does nothing and leaves the screen to <see cref="_state"/>.</summary>
    private bool _scanned;

    /// <summary>The standing note under the list: an unreadable instance, or why the instance list
    /// is stale. Re-stated on every rebuild so a filter change can't drop it.</summary>
    private string? _note;

    private List<PackSummary> _packs = new();
    private List<ServerEntry> _entries = new();

    /// <summary>Last ping per address, so re-filtering and re-sorting keep the statuses already
    /// gathered instead of blanking the page and starting the sweep again.</summary>
    private readonly Dictionary<string, ServerPingResult> _pings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.Ordinal);

    /// <summary>What the last scan could not read, if anything. Folded into <see cref="_note"/>
    /// because the ping sweep writes over the status line.</summary>
    private string _scanWarning = "";

    private CancellationTokenSource? _pingCts;
    private ServerSort _sort = ServerSort.List;
    private bool _showHidden;
    private bool _loading;

    /// <summary>When the last ping sweep finished, so reopening the page does not re-ping
    /// everything it already has an answer for.</summary>
    /// <remarks>The page is reused (<see cref="IReusablePage"/>), so <c>Loaded</c> fires on every
    /// return. Refresh always sweeps.</remarks>
    private DateTimeOffset _lastPingSweep = DateTimeOffset.MinValue;

    /// <summary>How long a sweep's answers are treated as still true when the page is reopened.</summary>
    private static readonly TimeSpan PingFreshFor = TimeSpan.FromSeconds(45);

    // ── console state ──
    private RconClient? _rcon;
    private ServerRow? _consoleRow;
    private CancellationTokenSource? _consoleCts;
    private List<string> _history = new();
    private int _historyIndex = -1;

    public ServersView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        ServerList.ItemsSource = _rows;

        _state = new PageState(ServerList, PageStateHost, nameof(ServersView))
            .Copy(PageCopy.Servers)
            .Slots(CountLabel, StatusLabel, BusyBar)
            .DisableWhileBusy(RefreshButton, SortButton, HiddenToggle, PackFilterBox,
                              AddServerButton, OpenFolderButton);
        _state.RetryRequested += () => _ = LoadAsync();

        // Filters rows already read out of servers.dat, so every keystroke is affordable.
        SearchBox.DebounceMilliseconds = 200;
        SearchBox.TextChanged += (_, _) => Refresh();

        // A reopen is a quiet refresh: nothing on screen moves unless the rescan finds a change.
        Loaded += async (_, _) => await LoadAsync(quiet: true);
        // The page can be navigated away from while a sweep or an RCON session is live; both hold a
        // socket, so both are torn down here rather than left to a finaliser.
        Unloaded += (_, _) =>
        {
            _pingCts?.Cancel();
            CloseConsoleConnection();
        };
    }

    private enum ServerSort { List, Name, Address, Status, Players }

    /// <summary>Where a row's status has got to. Also the value the status pill's colour triggers on.</summary>
    public enum PingState { Unknown, Pinging, Online, Offline }

    // ── loading ──

    /// <summary>Re-runs the load in place, for the offline banner's Retry and a page reopen.
    /// <see cref="PageState"/> keeps the rows on screen and shows the thin refresh bar.</summary>
    public Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Reads the instance list, then reads every instance's <c>servers.dat</c> and pings what it
    /// found.
    /// </summary>
    /// <remarks>The scan runs even when the instance list can't be fetched, since the server lists are
    /// local files.</remarks>
    /// <param name="quiet">True when nobody asked (the page opening or reopening): nothing shows unless
    /// it runs long, and the instance list another page just fetched is reused (see
    /// <see cref="ApiClient.ListPacksQuickAsync"/>). Refresh and Retry ask the server afresh.</param>
    private async Task LoadAsync(bool quiet = false)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            _state.Begin(null, null, quiet);

            // ListPacksAsync already falls back to its own cache and then to the folders on this PC,
            // so it only throws when there is no instance list at all.
            Exception? listFailed = null;
            try
            {
                _packs = (await (quiet ? App.State.Api.ListPacksQuickAsync() : App.State.Api.ListPacksAsync()))
                    .Where(p => !App.State.Settings.IsPackHidden(p.Id))
                    .ToList();
            }
            catch (Exception ex) { listFailed = ex; }

            using (_filling.Hold()) RebuildPackFilter();

            // The only caller that may skip the ping sweep: every edit path below changes the row
            // set, and a server that was just added has no answer to reuse.
            await ScanAsync(allowSkipPing: true);

            if (listFailed is OfflineException offline)
                _state.Offline(offline.Reason ?? "the server is not answering");
            else if (listFailed is { } ex2 && _entries.Count == 0)
                _state.Error("Your instance list could not be read, so there is no multiplayer list "
                           + "to open.", ex2);
            else if (listFailed is { } ex3)
                Failed("Your instance list could not be refreshed - these servers come from the "
                     + "instances this page already knew about.", ex3);
            else if (App.State.Api.PackListStale is { Length: > 0 } why)
                Say($"Showing your last known instances - the server is not answering ({why}). "
                  + "Pinging the servers themselves still works.");
        }
        finally { _loading = false; }
    }

    /// <summary>
    /// Re-reads every instance's server list off the UI thread, then rebuilds and re-pings.
    /// </summary>
    /// <remarks>Unreadable instances are skipped but counted in the note, so an unreadable server isn't
    /// mistaken for a deleted one.</remarks>
    /// <param name="note">What the triggering action did ("Added Foo to MyPack"). Goes into the standing
    /// note, since the ping sweep would overwrite the status line.</param>
    /// <param name="allowSkipPing">Lets the sweep be skipped while the answers on screen are fresh
    /// (<see cref="PingFreshFor"/>). Only the page load passes it; after an edit the rows have
    /// changed.</param>
    private async Task ScanAsync(string? note = null, bool allowSkipPing = false)
    {
        var packs = _packs.ToList();
        var unreadable = new List<string>();
        try
        {
            _entries = await Task.Run(() =>
            {
                var found = _servers.ScanAll(packs, out var skipped);
                unreadable = skipped;
                return found;
            });
            _scanWarning = unreadable.Count == 0
                ? ""
                : $"Could not read the server list for {unreadable.Count} instance(s): " +
                  $"{string.Join(", ", unreadable)}. Their servers are not shown.";
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ServersView), ex);
            _scanWarning = "The multiplayer lists could not be read. The launcher log has the detail.";
            _entries = new List<ServerEntry>();
        }
        // Scanned, whatever the result, so the empty panel may show now.
        _scanned = true;
        var parts = new[] { note, _scanWarning }.Where(p => !string.IsNullOrEmpty(p)).ToList();
        _note = parts.Count == 0 ? null : string.Join("  ·  ", parts);
        Refresh();

        // Skip the sweep when the answers on screen are seconds old (in practice, a page reopen).
        if (allowSkipPing && DateTimeOffset.UtcNow - _lastPingSweep < PingFreshFor && _rows.Count > 0)
        {
            Say($"{_rows.Count} server(s) · pinged {TimeFormat.Ago(_lastPingSweep)}");
            return;
        }

        await PingVisibleAsync();
    }

    /// <summary>Fills the instance ComboBox. Callers wrap this in <see cref="_filling"/>: setting
    /// ItemsSource and SelectedItem raises SelectionChanged synchronously, which isn't the user
    /// choosing an instance.</summary>
    private void RebuildPackFilter()
    {
        var items = new List<PackFilterItem> { new(null, "All instances") };
        items.AddRange(_packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .Select(p => new PackFilterItem(p.Id, p.Name)));
        var previous = (PackFilterBox.SelectedItem as PackFilterItem)?.Id;
        PackFilterBox.ItemsSource = items;
        PackFilterBox.SelectedItem = items.FirstOrDefault(i => i.Id == previous) ?? items[0];
    }

    private Guid? SelectedPackId => (PackFilterBox.SelectedItem as PackFilterItem)?.Id;

    /// <summary>
    /// Rebuilds the rows from the entries already read, applying the instance filter, the search box,
    /// the hidden toggle and the sort.
    /// </summary>
    /// <remarks>Entries that share an address become one row, which remembers every instance it came
    /// from (for "Join with..." and the tooltip).</remarks>
    private void Refresh()
    {
        // Everything below writes a count and picks an empty state, so it has to wait for a scan.
        if (!_scanned) return;

        var keepSelected = (ServerList.SelectedItem as ServerRow)?.Key;
        var query = SearchBox.Text?.Trim() ?? "";
        var packFilter = SelectedPackId;

        var visible = _entries.Where(e => packFilter is null || e.SourcePackId == packFilter);

        var grouped = visible
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .Select(g => new ServerRow(g.Key, g.ToList()))
            .ToList();

        foreach (var row in grouped)
        {
            // Cached icons only; the ping sweep decodes favicons off the UI thread.
            if (_pings.TryGetValue(row.Key, out var cached)) row.Apply(cached, CachedIcon(row.Key));
            else row.ApplyIcon(CachedIcon(row.Key));
        }

        IEnumerable<ServerRow> filtered = grouped;
        if (!_showHidden) filtered = filtered.Where(r => !r.IsHidden);
        if (query.Length > 0) filtered = filtered.Where(r => r.Matches(query));

        var sorted = _sort switch
        {
            ServerSort.Name => filtered.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
            ServerSort.Address => filtered.OrderBy(r => r.Address, StringComparer.OrdinalIgnoreCase),
            // Online first, then the ones still being asked, then never-asked, then offline: the
            // order in which a row is worth looking at.
            ServerSort.Status => filtered.OrderBy(r => r.State switch
                                         {
                                             PingState.Online => 0,
                                             PingState.Pinging => 1,
                                             PingState.Unknown => 2,
                                             _ => 3
                                         })
                                         .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
            ServerSort.Players => filtered.OrderByDescending(r => r.PlayersOnline)
                                          .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
            // The game's own order, which is the order the player arranged in the multiplayer screen.
            _ => filtered.OrderBy(r => r.Primary.SourcePackName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(r => r.Primary.Index)
        };

        _rows.Clear();
        foreach (var row in sorted) _rows.Add(row);

        if (keepSelected is not null)
            ServerList.SelectedItem = _rows.FirstOrDefault(r => r.Key == keepSelected);

        if (_rows.Count == 0) ChooseEmptyCopy(query);
        // Content(0) is the only way into the empty panel, so it always means a scan happened.
        RestateCount();
    }

    /// <summary>Writes the count slot and the standing note without rebuilding the list, for a ping
    /// landing, which changes how many are online but not which rows exist.</summary>
    private void RestateCount() =>
        _state.Content(_rows.Count, countText: ScopeLabel(_rows.Count), note: _note);

    /// <summary>The count slot. Only ever called with a number a completed scan produced.</summary>
    private string ScopeLabel(int total)
    {
        if (total == 0) return "";
        var online = _rows.Count(r => r.State == PingState.Online);
        var instances = SelectedPackId is null
            ? _entries.Select(e => e.SourcePackId).Distinct().Count()
            : 1;
        var consoles = _rows.Count(r => r.HasConsole);
        return $"{total} server(s) across {instances} instance(s) · {online} online" +
               (consoles > 0 ? $" · {consoles} with a console" : "");
    }

    /// <summary>
    /// Picks the empty-state wording for the pass about to finish.
    /// </summary>
    /// <remarks>Set per pass, so "nothing matches that" doesn't stick after the search is
    /// cleared.</remarks>
    private void ChooseEmptyCopy(string query)
    {
        if (_packs.Count == 0)
            _state.EmptyNext("No instances yet",
                "Servers are read from each instance's multiplayer list, so there is nothing to show "
                + "until you have an instance.",
                PageCopy.Servers.Glyph);
        else if (query.Length > 0)
            _state.EmptyFiltered();
        else if (_entries.Count > 0 && !_showHidden)
            _state.EmptyNext("Only hidden entries here",
                "Every server here is a hidden direct-connect entry - one Minecraft keeps but never "
                + "draws.", PageCopy.Servers.Glyph,
                actionLabel: "Show hidden entries",
                action: () => { HiddenToggle.IsChecked = true; _showHidden = true; Refresh(); });
        // Otherwise the default wording from PageCopy.Servers applies, which explains where servers
        // come from.
    }

    // ── pinging ──

    /// <summary>
    /// Pings every row on screen, concurrently but capped, and paints each result as it lands.
    /// </summary>
    /// <remarks>Each row updates as its own ping returns, so one dead address doesn't hold up the list.
    /// A previous sweep is cancelled first so two sweeps never write over each other.</remarks>
    private async Task PingVisibleAsync()
    {
        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        var ct = _pingCts.Token;

        var rows = _rows.ToList();
        if (rows.Count == 0) return;
        foreach (var row in rows) row.SetPinging();
        // A note rather than a loading state: the rows stay real and clickable while pinging.
        Say($"Pinging {rows.Count} server(s)...");

        using var gate = new SemaphoreSlim(PingConcurrency);
        var tasks = rows.Select(async row =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var result = await MinecraftServerPing.PingAsync(row.Address, ct: ct);
                    // Decoded and frozen on the pool; doing it on the UI thread for every row would
                    // hitch on a long list.
                    var icon = await Task.Run(() => DecodeFavicon(result.FaviconBase64)
                                                    ?? DecodeFavicon(row.Primary.IconBase64), ct);
                    if (ct.IsCancellationRequested) return;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _pings[row.Key] = result;
                        if (icon is not null) _icons[row.Key] = icon;
                        var shown = icon ?? CachedIcon(row.Key);
                        row.Apply(result, shown);
                        // Refresh() (typing in the search box, say) replaces the ServerRow objects,
                        // so also paint the live row with this address.
                        foreach (var live in _rows)
                            if (!ReferenceEquals(live, row) && live.Key == row.Key)
                                live.Apply(result, shown);
                    });
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { /* the page moved on; the row keeps its last state */ }
            catch (Exception ex)
            {
                // An unexpected failure on one row becomes its offline reason, not a dialog or a
                // failed sweep.
                await Dispatcher.InvokeAsync(() => row.Apply(ServerPingResult.Failed(ex.Message), null));
            }
        });

        await Task.WhenAll(tasks);

        if (ct.IsCancellationRequested) return;

        // Only stamped when the sweep completes. A cancelled one leaves rows unpinged, so a reopen
        // should still sweep.
        _lastPingSweep = DateTimeOffset.UtcNow;

        if (_sort == ServerSort.Status || _sort == ServerSort.Players) Refresh();
        else RestateCount();
    }

    /// <summary>The icon already decoded for this address, if any. Never decodes; that only happens
    /// off the UI thread.</summary>
    private ImageSource? CachedIcon(string key) => _icons.TryGetValue(key, out var icon) ? icon : null;

    /// <summary>
    /// Turns a base64 PNG into a frozen image, or null for anything that is not one.
    /// </summary>
    /// <remarks>
    /// <para>Frozen so it can pass from the ping's thread to the UI thread. Both sources (a server's
    /// favicon, servers.dat) are other people's data, so every failure is "no icon", never an
    /// exception.</para>
    /// <para>Bounded because the data comes straight from an untrusted host, and a few KB of PNG can
    /// claim 30000x30000 pixels. <see cref="MaxFaviconBase64Length"/> caps the input, the header is
    /// checked against <see cref="MaxFaviconSourcePixels"/> before decoding, and the decode is capped
    /// at <see cref="FaviconPixelSize"/> (the protocol's favicon size) on the longer side.</para>
    /// </remarks>
    private static ImageSource? DecodeFavicon(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        if (base64.Length > MaxFaviconBase64Length) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());

            // Only the header: with no caching, no pixels are decoded until something asks for them.
            var header = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation,
                                            BitmapCacheOption.None);
            if (header.PixelWidth > MaxFaviconSourcePixels || header.PixelHeight > MaxFaviconSourcePixels)
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (header.PixelHeight > header.PixelWidth) image.DecodePixelHeight = FaviconPixelSize;
            else image.DecodePixelWidth = FaviconPixelSize;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        // The imaging codecs report a bad image as many exception types (format, IO, overflow, COM,
        // argument...). Each means "no icon".
        catch (Exception) { return null; }
    }

    // ── toolbar ──

    /// <summary>
    /// The page's refresh: re-reads every instance's servers.dat and pings every server again.
    /// </summary>
    /// <remarks>Disabled while it runs by <see cref="PageState.DisableWhileBusy"/>. Don't also set
    /// IsEnabled by hand; two writers can leave the button stuck disabled.</remarks>
    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        // The ping sweep carries on after PageState has re-enabled this button, so say what's happening.
        if (_loading) { Say("Still working on the last refresh - the servers are being pinged."); return; }
        await LoadAsync();
    }

    /// <remarks>Guarded by <see cref="_filling"/> rather than <c>IsLoaded</c>, which is already true
    /// inside the Loaded handler that fills the ComboBox.</remarks>
    private async void OnPackFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        try
        {
            Refresh();
            await PingVisibleAsync();
        }
        catch (Exception ex) { Failed("That instance's servers could not be shown.", ex); }
    }

    /// <summary>Opens the folder the selected instance's <c>servers.dat</c> lives in. With the filter
    /// on "All instances" that is the first one, the same rule the Config page uses.</summary>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if ((SelectedPackId ?? _packs.FirstOrDefault()?.Id) is not { } packId)
        {
            Say("There is no instance to open.");
            return;
        }
        try
        {
            var dir = App.State.Packs.GameDir(packId);
            if (!Directory.Exists(dir))
            {
                var name = _packs.FirstOrDefault(p => p.Id == packId)?.Name ?? "That instance";
                Say($"{name} has no game folder yet - launch or sync it once.");
                return;
            }
            // SafeLaunch logs its own failures and never throws.
            if (!SafeLaunch.OpenFolder(dir)) Say("That folder could not be opened in Explorer.");
        }
        catch (Exception ex) { Failed("That folder could not be opened in Explorer.", ex); }
    }

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        if (SortButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = SortButton;
        menu.IsOpen = true;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e)
    {
        SortListMenuItem.IsChecked = _sort == ServerSort.List;
        SortNameMenuItem.IsChecked = _sort == ServerSort.Name;
        SortAddressMenuItem.IsChecked = _sort == ServerSort.Address;
        SortStatusMenuItem.IsChecked = _sort == ServerSort.Status;
        SortPlayersMenuItem.IsChecked = _sort == ServerSort.Players;
    }

    private void OnSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (Enum.TryParse<ServerSort>(tag, out var sort)) _sort = sort;
        Refresh();
    }

    private void OnToggleHidden(object sender, RoutedEventArgs e)
    {
        _showHidden = HiddenToggle.IsChecked == true;
        HiddenToggle.ToolTip = _showHidden
            ? "Hide the direct-connect entries again"
            : "Show the hidden direct-connect entries Minecraft keeps but never draws";
        Refresh();
    }

    /// <summary>
    /// Page-level shortcuts: F5 re-reads everything, Ctrl+F opens the search, Delete removes the
    /// selected rows.
    /// </summary>
    /// <remarks>Delete is ignored while a text box (search, console) has focus.</remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox or PasswordBox;
        if (e.Key == Key.F5) { OnRefresh(this, e); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !typing && ServerList.SelectedItems.Count > 0)
        {
            _ = RemoveSelectedAsync();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ── row actions ──

    private void OnServerSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private async void OnServerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (ServerList.SelectedItem is ServerRow row) await JoinAsync(row, ServerList);
        }
        catch (Exception ex) { Failed("That server could not be joined.", ex); }
    }

    private async void OnAddServer(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_packs.Count == 0) { Say("Create an instance first - a server list lives inside one."); return; }
            var result = await ServerEditDialog.AddAsync(_shell, _packs, SelectedPackId ?? _packs[0].Id);
            if (result is null) return;

            await Task.Run(() => _servers.Add(result.PackId, result.Name, result.Address));
            var into = _packs.FirstOrDefault(p => p.Id == result.PackId)?.Name ?? "the instance";
            await ScanAsync($"Added {result.Name} to {into}.");
        }
        catch (Exception ex) { Failed("That server could not be added.", ex); }
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: ServerRow row } anchor) return;
            await JoinAsync(row, anchor);
        }
        catch (Exception ex) { Failed("That server could not be joined.", ex); }
    }

    private async void OnCtxJoin(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await JoinAsync(row, ServerList);
        }
        catch (Exception ex) { Failed("That server could not be joined.", ex); }
    }

    /// <summary>
    /// Launches an instance straight into this server, asking which instance when the address is in
    /// more than one and the page is not already filtered to one.
    /// </summary>
    private async Task JoinAsync(ServerRow row, FrameworkElement anchor)
    {
        var candidates = row.Sources.Select(s => s.SourcePackId).Distinct().ToList();
        if (SelectedPackId is { } filtered && candidates.Contains(filtered))
        {
            await JoinWithAsync(row, filtered);
            return;
        }
        if (candidates.Count == 1)
        {
            await JoinWithAsync(row, candidates[0]);
            return;
        }

        var menu = new ContextMenu();
        foreach (var packId in candidates)
        {
            var name = _packs.FirstOrDefault(p => p.Id == packId)?.Name ?? "instance";
            var item = new MenuItem { Header = $"Join with {name}" };
            var id = packId;
            item.Click += async (_, _) =>
            {
                try { await JoinWithAsync(row, id); }
                catch (Exception ex) { Failed("That server could not be joined.", ex); }
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private async Task JoinWithAsync(ServerRow row, Guid packId)
    {
        // The address goes on the game's command line. If ParseAddress rejects it the game would just
        // stop at the title screen, so say why here.
        if (MinecraftServerPing.ParseAddress(row.Address).Host.Length == 0)
        {
            Say("That address cannot be joined directly. Edit the entry to a plain host name or IP.");
            return;
        }

        if (App.State.MinecraftAccounts.Current is null)
        {
            _shell.OpenMcAccount();
            Say("Sign in to a Minecraft account first.");
            return;
        }

        Say($"Launching into {row.DisplayName}...");
        try
        {
            // GetPackAsync falls back to cached details, so this works offline for an instance on this
            // PC. It can still throw otherwise, and an exception escaping this async void handler's
            // callee would crash the launcher.
            var pack = await App.State.Api.GetPackAsync(packId);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            var proc = await App.State.Launcher.LaunchTrackedAsync(
                pack, new Progress<string>(_ => { }), joinServerAddress: row.Address);
            _shell.OpenMinecraftHost(pack, proc);
            Say($"{pack.Name} is starting and will connect to {row.Address}.");
        }
        catch (OperationCanceledException) { Say("Launch cancelled."); }
        catch (OfflineException)
        {
            Say("That instance has never been downloaded on this PC and the server is not "
              + "answering, so there is nothing to launch yet. The instances you already have "
              + "still start normally.");
        }
        catch (Exception ex) { Failed("That instance could not be launched.", ex); }
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ServerRow row }) await EditAsync(row);
        }
        catch (Exception ex) { Failed("That entry could not be edited.", ex); }
    }

    private async void OnCtxEdit(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await EditAsync(row);
        }
        catch (Exception ex) { Failed("That entry could not be edited.", ex); }
    }

    /// <summary>
    /// Edits the entry in one instance.
    /// </summary>
    /// <remarks>A row can cover several instances; only this instance's copy changes, and the status
    /// line says which.</remarks>
    private async Task EditAsync(ServerRow row)
    {
        var entry = row.Primary;
        var result = await ServerEditDialog.EditAsync(_shell, _packs, entry);
        if (result is null) return;

        var changed = await Task.Run(() => _servers.Update(entry, result.Name, result.Address));
        await ScanAsync(changed
            ? $"Updated {result.Name} in {entry.SourcePackName}." +
              (row.Sources.Count > 1 ? $" The other {row.Sources.Count - 1} instance(s) still have the old entry." : "")
            : "That entry is no longer in the instance's list - refreshed.");
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ServerRow row }) await RemoveAsync(new[] { row });
        }
        catch (Exception ex) { Failed("That server could not be removed.", ex); }
    }

    private async void OnCtxRemove(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await RemoveAsync(new[] { row });
        }
        catch (Exception ex) { Failed("That server could not be removed.", ex); }
    }

    private async Task RemoveSelectedAsync()
    {
        try
        {
            var rows = ServerList.SelectedItems.OfType<ServerRow>().ToList();
            if (rows.Count > 0) await RemoveAsync(rows);
        }
        catch (Exception ex) { Failed("Those servers could not be removed.", ex); }
    }

    /// <summary>
    /// Removes the rows' entries from their instances' lists, after one confirmation covering all of
    /// them.
    /// </summary>
    /// <remarks>A row covering several instances removes every copy; the confirmation names them.</remarks>
    private async Task RemoveAsync(IReadOnlyList<ServerRow> rows)
    {
        var entries = rows.SelectMany(r => r.Sources).ToList();
        var instances = entries.Select(e => e.SourcePackName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var what = rows.Count == 1 ? $"'{rows[0].DisplayName}'" : $"{rows.Count} servers";
        var where = instances.Count == 1 ? instances[0] : $"{instances.Count} instances ({string.Join(", ", instances)})";

        if (!await AppDialog.ConfirmAsync(_shell, "Remove server",
                $"Remove {what} from {where}?\n\n" +
                "This only edits your own copy of the multiplayer list - servers.dat is never synced to a " +
                "shared pack. Nothing on the server itself changes.",
                "Remove", "Cancel", danger: true))
            return;

        var removed = await Task.Run(() => entries.Count(entry => _servers.Remove(entry)));
        await ScanAsync(removed == 0
            ? "Nothing was removed - those entries were already gone."
            : $"Removed {removed} entr{(removed == 1 ? "y" : "ies")}.");
    }

    private async void OnCopyTo(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ServerRow row }) await CopyToAsync(row);
        }
        catch (Exception ex) { Failed("That server could not be copied.", ex); }
    }

    private async void OnCtxCopyTo(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await CopyToAsync(row);
        }
        catch (Exception ex) { Failed("That server could not be copied.", ex); }
    }

    /// <summary>Copies the entry (name, address and favicon) into another instance's list.</summary>
    private async Task CopyToAsync(ServerRow row)
    {
        var already = row.Sources.Select(s => s.SourcePackId).ToHashSet();
        var targets = _packs.Where(p => !already.Contains(p.Id)).ToList();
        if (targets.Count == 0)
        {
            Say("Every instance already has this server.");
            return;
        }

        var picker = new PackPickerDialog(targets, "Copy server to...",
            $"Pick the instance to add {row.DisplayName} to. Its name and icon come with it.", "Copy")
        { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is not { } targetId) return;

        var entry = row.Primary;
        var copied = await Task.Run(() => _servers.CopyTo(entry, targetId));
        var name = targets.First(p => p.Id == targetId).Name;
        await ScanAsync(copied
            ? $"Copied {row.DisplayName} into {name}."
            : $"{name} already lists that address.");
    }

    private void OnCtxCopyAddress(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        ClipboardHelper.TrySetText(row.Address);
        Say($"Copied {row.Address}.");
    }

    private async void OnCtxPing(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is not { } row) return;
            row.SetPinging();
            var result = await MinecraftServerPing.PingAsync(row.Address);
            _pings[row.Key] = result;
            var icon = await Task.Run(() => DecodeFavicon(result.FaviconBase64)
                                            ?? DecodeFavicon(row.Primary.IconBase64));
            if (icon is not null) _icons[row.Key] = icon;
            row.Apply(result, icon ?? CachedIcon(row.Key));
            RestateCount();
            Say(result.Online
                ? $"{row.DisplayName}: {result.PlayersOnline}/{result.PlayersMax} online, {result.LatencyMs} ms."
                : $"{row.DisplayName}: {result.Error}.");
        }
        catch (Exception ex) { Failed("That server could not be pinged.", ex); }
    }

    private async void OnCtxMoveUp(object sender, RoutedEventArgs e) => await MoveAsync(sender, -1);
    private async void OnCtxMoveDown(object sender, RoutedEventArgs e) => await MoveAsync(sender, +1);

    /// <summary>Moves the entry within its instance's list, which is the order the multiplayer screen
    /// shows. Only meaningful while the page is sorted that way, so it says so when it is not.</summary>
    private async Task MoveAsync(object sender, int delta)
    {
        try
        {
            if (RowFromMenu(sender) is not { } row) return;
            var entry = row.Primary;
            var moved = await Task.Run(() => _servers.Move(entry, delta));
            if (!moved) { Say("That entry is already at the end of the list."); return; }
            await ScanAsync(_sort == ServerSort.List
                ? $"Moved {row.DisplayName} in {entry.SourcePackName}'s list."
                : $"Moved {row.DisplayName} in {entry.SourcePackName}'s list - sort by 'Order in the game's list' to see it.");
        }
        catch (Exception ex) { Failed("That entry could not be moved.", ex); }
    }

    /// <summary>The row a context-menu item belongs to. Menus are declared inside the item template,
    /// so the row is the placement target's data context.</summary>
    private ServerRow? RowFromMenu(object sender)
    {
        if (sender is not MenuItem item) return null;
        var parent = item.Parent;
        while (parent is MenuItem p) parent = p.Parent;
        return parent is ContextMenu { PlacementTarget: FrameworkElement target }
            ? target.DataContext as ServerRow
            : null;
    }

    // ── console ──

    private async void OnConsole(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ServerRow row }) await OpenConsoleAsync(row);
        }
        catch (Exception ex) { Failed("That console could not be opened.", ex); }
    }

    private async void OnCtxConsole(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await OpenConsoleAsync(row);
        }
        catch (Exception ex) { Failed("That console could not be opened.", ex); }
    }

    private async void OnCtxConsoleSetup(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await ConfigureConsoleAsync(row);
        }
        catch (Exception ex) { Failed("Those console settings could not be saved.", ex); }
    }

    /// <summary>
    /// Opens the console pane for a server, asking for its RCON details first if it has none.
    /// </summary>
    /// <remarks>RCON is off by default on every server, so a console only opens once the user has
    /// entered RCON details for it.</remarks>
    private async Task OpenConsoleAsync(ServerRow row)
    {
        var admin = App.State.Settings.GetServerAdmin(row.Address);
        if (admin?.HasConsole != true)
        {
            if (!await ConfigureConsoleAsync(row)) return;
            admin = App.State.Settings.GetServerAdmin(row.Address);
            if (admin?.HasConsole != true) return;
        }

        if (_consoleRow is not null && _consoleRow.Key != row.Key) CloseConsoleConnection();
        _consoleRow = row;

        ConsolePanel.Visibility = Visibility.Visible;
        ConsoleColumn.Width = new GridLength(460);
        ConsoleTitle.Text = row.DisplayName;
        var (host, port) = ConsoleTargetOf(row, admin!);
        ConsoleSubtitle.Text = $"RCON {host}:{port}";

        _history = admin!.CommandHistory.ToList();
        _historyIndex = _history.Count;

        if (_rcon is { IsConnected: true })
        {
            SetConsoleState(connected: true, "Connected.");
            CommandBox.Focus();
            return;
        }

        SetConsoleState(connected: false, "Not connected.");
        await ConnectConsoleAsync();
    }

    /// <summary>Collects (or changes) a server's RCON details and stores them. Returns true when
    /// something was saved.</summary>
    private async Task<bool> ConfigureConsoleAsync(ServerRow row)
    {
        var existing = App.State.Settings.GetServerAdmin(row.Address);
        var result = await ServerConsoleSetupDialog.ShowAsync(_shell, row.DisplayName, row.Address, existing);
        if (result is null) return false;

        if (result.Removed)
        {
            App.State.Settings.RemoveServerAdmin(row.Address);
            App.State.Settings.Save();
            if (_consoleRow?.Key == row.Key) CloseConsole();
            Refresh();
            Say($"Forgot the console details for {row.DisplayName}.");
            return false;
        }

        var entry = App.State.Settings.GetOrCreateServerAdmin(row.Address);
        entry.Label = result.Label;
        entry.RconHost = result.RconHost;
        entry.RconPort = result.RconPort;
        entry.RconPassword = result.Password;
        App.State.Settings.Save();

        // A changed password invalidates the open session, so drop it.
        if (_consoleRow?.Key == row.Key) CloseConsoleConnection();
        Refresh();
        Say($"Saved console details for {row.DisplayName}.");
        return true;
    }

    /// <summary>Where the console actually dials: the RCON host override when set, the game host
    /// otherwise.</summary>
    private static (string Host, int Port) ConsoleTargetOf(ServerRow row, ServerAdminEntry admin)
    {
        var host = string.IsNullOrWhiteSpace(admin.RconHost)
            ? MinecraftServerPing.ParseAddress(row.Address).Host
            : admin.RconHost.Trim();
        var port = admin.RconPort is > 0 and <= 65535 ? admin.RconPort : RconClient.DefaultPort;
        return (host, port);
    }

    private async void OnConsoleConnectToggle(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_rcon is { IsConnected: true })
            {
                CloseConsoleConnection();
                AppendConsole("- disconnected -");
                SetConsoleState(connected: false, "Not connected.");
                return;
            }
            await ConnectConsoleAsync();
        }
        catch (Exception ex) { SetConsoleState(connected: false, "Could not connect: " + ex.Message); }
    }

    private async Task ConnectConsoleAsync()
    {
        if (_consoleRow is not { } row) return;
        var admin = App.State.Settings.GetServerAdmin(row.Address);
        if (admin?.HasConsole != true) { SetConsoleState(false, "No RCON password saved for this server."); return; }

        var (host, port) = ConsoleTargetOf(row, admin);
        _consoleCts?.Cancel();
        _consoleCts = new CancellationTokenSource();
        ConnectButton.IsEnabled = false;
        SetConsoleState(connected: false, $"Connecting to {host}:{port}...");
        try
        {
            _rcon = await RconClient.ConnectAsync(host, port, admin.RconPassword!, ct: _consoleCts.Token);
            AppendConsole($"- connected to {host}:{port} -");
            SetConsoleState(connected: true, "Connected. Commands run as the server console.");
            CommandBox.Focus();
        }
        catch (OperationCanceledException) { SetConsoleState(false, "Connection cancelled."); }
        catch (RconAuthenticationException ex)
        {
            SetConsoleState(false, ex.Message + " Open the console settings to fix it.");
        }
        catch (TimeoutException ex)
        {
            SetConsoleState(false, ex.Message + " Check that enable-rcon=true and the port is reachable.");
        }
        catch (Exception ex) { SetConsoleState(false, "Could not connect: " + ex.Message); }
        finally { ConnectButton.IsEnabled = true; }
    }

    private async void OnConsoleSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_consoleRow is { } row) await ConfigureConsoleAsync(row);
        }
        catch (Exception ex) { SetConsoleStatus("Could not save those console settings: " + ex.Message); }
    }

    private void OnConsoleCopy(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.TrySetText(ConsoleLog.Text);
        SetConsoleStatus("Copied the scrollback.");
    }

    private void OnConsoleClose(object sender, RoutedEventArgs e) => CloseConsole();

    private void CloseConsole()
    {
        CloseConsoleConnection();
        _consoleRow = null;
        ConsolePanel.Visibility = Visibility.Collapsed;
        ConsoleColumn.Width = new GridLength(0);
        ConsoleLog.Clear();
    }

    /// <summary>Drops the RCON socket without touching the pane, so reconnecting keeps the scrollback.</summary>
    private void CloseConsoleConnection()
    {
        _consoleCts?.Cancel();
        _consoleCts = null;
        _rcon?.Dispose();
        _rcon = null;
    }

    private async void OnQuickCommand(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: string command }) await RunCommandAsync(command);
        }
        catch (Exception ex) { SetConsoleStatus("Command failed: " + ex.Message); }
    }

    private async void OnConsoleSend(object sender, RoutedEventArgs e)
    {
        try { await SendTypedCommandAsync(); }
        catch (Exception ex) { SetConsoleStatus("Command failed: " + ex.Message); }
    }

    /// <summary>
    /// Enter runs the command; up and down walk the history for this server.
    /// </summary>
    /// <remarks>The history index sits one past the end when nothing is recalled, so Up reaches the
    /// latest command and Down walks back out to an empty box, like a shell.</remarks>
    private async void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    await SendTypedCommandAsync();
                    break;

                case Key.Up when _history.Count > 0:
                    e.Handled = true;
                    _historyIndex = Math.Max(0, _historyIndex - 1);
                    SetCommandText(_history[_historyIndex]);
                    break;

                case Key.Down when _history.Count > 0:
                    e.Handled = true;
                    _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                    SetCommandText(_historyIndex >= _history.Count ? "" : _history[_historyIndex]);
                    break;
            }
        }
        catch (Exception ex) { SetConsoleStatus("Command failed: " + ex.Message); }
    }

    private void SetCommandText(string text)
    {
        CommandBox.Text = text;
        CommandBox.CaretIndex = text.Length;
    }

    private async Task SendTypedCommandAsync()
    {
        var command = CommandBox.Text?.Trim() ?? "";
        if (command.Length == 0) return;
        CommandBox.Clear();
        await RunCommandAsync(command);
    }

    /// <summary>
    /// Runs one command against the open console and prints what came back.
    /// </summary>
    /// <remarks>A command with no output still gets a line, so the console doesn't look broken. A
    /// leading slash is stripped, since RCON commands don't take one.</remarks>
    private async Task RunCommandAsync(string command)
    {
        if (_rcon is not { IsConnected: true })
        {
            SetConsoleStatus("Not connected - press Connect first.");
            return;
        }

        var clean = command.TrimStart('/');
        RememberCommand(command);
        AppendConsole("> " + clean);
        SetConsoleStatus("Running...");
        try
        {
            var reply = await _rcon.SendCommandAsync(clean, ct: _consoleCts?.Token ?? CancellationToken.None);
            AppendConsole(string.IsNullOrWhiteSpace(reply)
                ? "(the server said nothing)"
                : MinecraftServerPing.StripFormatting(reply));
            // A half-finished command leaves the rest of its reply in the socket and RconClient marks the
            // connection unusable; otherwise the next command would read this one's tail.
            if (_rcon is { IsConnected: false }) DropDesynchronisedConsole("That command did not finish.");
            else SetConsoleStatus("");
        }
        catch (OperationCanceledException) { SetConsoleStatus("Cancelled."); }
        catch (TimeoutException ex) { DropDesynchronisedConsole(ex.Message); }
        catch (Exception ex)
        {
            // Usually a dead socket, so put the pane back into its disconnected state.
            CloseConsoleConnection();
            SetConsoleState(connected: false, "The console connection dropped: " + ex.Message);
        }
    }

    /// <summary>
    /// Drops an RCON connection that can no longer be read in step, and says so in the pane.
    /// </summary>
    /// <remarks>A timed-out command's reply (and its sentinel) is still in flight, so the next command
    /// would get this one's output. The scrollback is kept for reconnecting.</remarks>
    private void DropDesynchronisedConsole(string why)
    {
        CloseConsoleConnection();
        AppendConsole("- disconnected: the server did not finish answering -");
        SetConsoleState(connected: false, why + " The console disconnected so it cannot show you a " +
                                                "stale answer - press Connect to carry on.");
    }

    /// <summary>Stores the command in this server's history, newest last, without duplicating the one
    /// before it. Only what was typed into the command box is stored, never the password.</summary>
    private void RememberCommand(string command)
    {
        if (_consoleRow is not { } row) return;
        var admin = App.State.Settings.GetServerAdmin(row.Address);
        if (admin is null) return;

        if (admin.CommandHistory.Count == 0 ||
            !string.Equals(admin.CommandHistory[^1], command, StringComparison.Ordinal))
        {
            admin.CommandHistory.Add(command);
            while (admin.CommandHistory.Count > MaxCommandHistory) admin.CommandHistory.RemoveAt(0);
            App.State.Settings.Save();
        }

        _history = admin.CommandHistory.ToList();
        _historyIndex = _history.Count;
    }

    private void AppendConsole(string text)
    {
        ConsoleLog.Append(text + Environment.NewLine);
        ConsoleLog.ScrollToEnd();
    }

    private void SetConsoleState(bool connected, string status)
    {
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
        ConnectButton.ToolTip = connected
            ? "Close the RCON connection"
            : "Log in to this server's RCON console";
        CommandBox.IsEnabled = connected;
        SetConsoleStatus(status);
    }

    private void SetConsoleStatus(string status) => ConsoleStatus.Text = status;

    // ── saying things ──

    /// <summary>A one-line note about what just happened, under the list. The next rebuild replaces
    /// it with the standing note.</summary>
    private void Say(string? text) => _state.Note(text);

    /// <summary>A user action failed. They get one sentence they can act on; the exception goes to
    /// the launcher log.</summary>
    private void Failed(string plain, Exception ex)
    {
        AppLog.LogError(nameof(ServersView), ex);
        Say(plain);
    }

    // ── rows ──

    private sealed record PackFilterItem(Guid? Id, string Label);

    /// <summary>
    /// One server as the page shows it: an address, every instance that lists it, and whatever the
    /// last ping said.
    /// </summary>
    /// <remarks>Raises change notifications so a ping repaints one row instead of rebuilding the list.
    /// Exposes no <see cref="Brush"/>: the status colour is a template trigger, so it follows theme
    /// changes.</remarks>
    public sealed class ServerRow : INotifyPropertyChanged
    {
        public ServerRow(string key, List<ServerEntry> sources)
        {
            Key = key;
            Sources = sources;
        }

        public string Key { get; }

        /// <summary>Every list entry pointing at this address, one per instance that has it.</summary>
        public List<ServerEntry> Sources { get; }

        /// <summary>The entry the single-instance actions (edit, move, copy-from) work on.</summary>
        public ServerEntry Primary => Sources[0];

        public string Address => Primary.Address;

        /// <summary>The label saved in the launcher wins over the name in servers.dat.</summary>
        public string DisplayName
        {
            get
            {
                var label = App.State.Settings.GetServerAdmin(Address)?.Label;
                return !string.IsNullOrWhiteSpace(label) ? label
                     : !string.IsNullOrWhiteSpace(Primary.Name) ? Primary.Name
                     : Address;
            }
        }

        public bool IsHidden => Sources.All(s => s.Hidden);
        public bool HasConsole => App.State.Settings.GetServerAdmin(Address)?.HasConsole == true;

        public PingState State { get; private set; } = PingState.Unknown;
        public int PlayersOnline { get; private set; }

        private ServerPingResult? _result;
        private ImageSource? _icon;

        public ImageSource? Icon => _icon;
        public Visibility IconVisibility => _icon is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility LetterVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;
        public string Letter => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

        public Visibility ConsoleBadgeVisibility => HasConsole ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HiddenBadgeVisibility => IsHidden ? Visibility.Visible : Visibility.Collapsed;

        public string StatusText => State switch
        {
            PingState.Online => $"{_result?.PlayersOnline ?? 0}/{_result?.PlayersMax ?? 0}",
            PingState.Pinging => "...",
            PingState.Offline => "OFFLINE",
            _ => "-"
        };

        /// <summary>The line under the name: where it is, which instances have it, and what the last
        /// ping made of it.</summary>
        public string MetaLabel
        {
            get
            {
                var instances = Sources.Select(s => s.SourcePackName)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .ToList();
                var where = instances.Count switch
                {
                    1 => instances[0],
                    2 => $"{instances[0]} + 1 more",
                    _ => $"{instances[0]} + {instances.Count - 1} more"
                };
                var status = State switch
                {
                    PingState.Online =>
                        $"{_result?.VersionName ?? "online"}{(_result?.LatencyMs is { } ms ? $" · {ms} ms" : "")}",
                    PingState.Offline => _result?.Error ?? "offline",
                    PingState.Pinging => "pinging...",
                    _ => "not pinged yet"
                };
                return $"{Address}  ·  {where}  ·  {status}";
            }
        }

        public string MotdLabel => _result?.Motd?.Replace('\n', ' ') ?? "";
        public Visibility MotdVisibility =>
            string.IsNullOrWhiteSpace(MotdLabel) ? Visibility.Collapsed : Visibility.Visible;

        public string JoinTooltip
        {
            get
            {
                var instances = Sources.Select(s => s.SourcePackName)
                                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return instances.Count == 1
                    ? $"Launch {instances[0]} and connect straight to {Address}"
                    : $"Launch one of {instances.Count} instances and connect straight to {Address}";
            }
        }

        public string ConsoleTooltip => HasConsole
            ? "Open this server's RCON console"
            : "Set up an RCON console for this server";

        public string ConsoleSetupLabel => HasConsole ? "Console settings..." : "Set up console...";

        public string RemoveTooltip => Sources.Count == 1
            ? $"Remove this server from {Primary.SourcePackName}'s list"
            : $"Remove this server from all {Sources.Count} instances that list it";

        /// <summary>Everything known about the server, for the row's tooltip, including who is
        /// online.</summary>
        public string Tooltip
        {
            get
            {
                var lines = new List<string> { DisplayName, Address };
                lines.Add("In: " + string.Join(", ", Sources.Select(s => s.SourcePackName)
                                                            .Distinct(StringComparer.OrdinalIgnoreCase)));
                if (_result is { Online: true } r)
                {
                    lines.Add($"{r.PlayersOnline}/{r.PlayersMax} players" +
                              (r.LatencyMs is { } ms ? $" · {ms} ms" : ""));
                    if (r.VersionName is { Length: > 0 } v)
                        lines.Add($"Version: {v}{(r.Protocol is { } p ? $" (protocol {p})" : "")}");
                    if (r.Motd is { Length: > 0 } motd) lines.Add(motd);
                    if (r.PlayerSample.Count > 0) lines.Add("Online: " + string.Join(", ", r.PlayerSample));
                }
                else if (_result is { } offline)
                {
                    lines.Add("Offline - " + offline.Error);
                }
                if (IsHidden) lines.Add("A hidden direct-connect entry.");
                return string.Join(Environment.NewLine, lines);
            }
        }

        public bool Matches(string query) =>
            DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Address.Contains(query, StringComparison.OrdinalIgnoreCase)
            || MotdLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Sources.Any(s => s.SourcePackName.Contains(query, StringComparison.OrdinalIgnoreCase));

        public void SetPinging()
        {
            State = PingState.Pinging;
            RaiseAll();
        }

        public void Apply(ServerPingResult result, ImageSource? icon)
        {
            _result = result;
            State = result.Online ? PingState.Online : PingState.Offline;
            PlayersOnline = result.PlayersOnline;
            if (icon is not null) _icon = icon;
            RaiseAll();
        }

        public void ApplyIcon(ImageSource? icon)
        {
            if (icon is null) return;
            _icon = icon;
            RaiseAll();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Almost every displayed property derives from the ping or the settings, so one
        /// null-name notification (which WPF reads as "everything changed") is simpler than listing
        /// them.</summary>
        private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
