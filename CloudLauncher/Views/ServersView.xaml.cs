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
/// <para>Minecraft keeps its server list per instance, inside <c>servers.dat</c>. With a dozen
/// instances that means a dozen copies of "the same five servers", no way to see which of them are up
/// without starting the game, and no way to add a server to a new instance except by typing it in
/// again. This page reads all of those lists at once, merges entries that point at the same address
/// into one row, pings them, and lets a server be added, edited, copied between instances, joined
/// directly, and — for servers the user runs — driven from an RCON console.</para>
/// <para><c>servers.dat</c> is an ignored file in the pack rules, so it is per-machine: nothing this
/// page writes is ever synced to a shared pack or to a team mate. The add/edit card says so, because
/// "did I just add this server for everyone?" is the obvious worry.</para>
/// <para>Nothing here touches the disk or the network on the UI thread. Scans, pings and RCON all run
/// off-thread; a server being unreachable is a row state, never a dialog.</para>
/// </remarks>
public partial class ServersView : Page
{
    /// <summary>How many servers are pinged at once. High enough that a list of thirty finishes in a
    /// couple of seconds, low enough not to open thirty sockets at a stroke on a home connection.</summary>
    private const int PingConcurrency = 8;

    /// <summary>Commands kept per server for the up-arrow history. Long enough to cover a session's
    /// worth of admin, short enough that settings.json stays small.</summary>
    private const int MaxCommandHistory = 60;

    private readonly MainWindow _shell;
    private readonly ServerListService _servers = new(App.State.Packs);
    private readonly ObservableCollection<ServerRow> _rows = new();

    private List<PackSummary> _packs = new();
    private List<ServerEntry> _entries = new();

    /// <summary>Last ping per address, so re-filtering and re-sorting keep the statuses already
    /// gathered instead of blanking the page and starting the sweep again.</summary>
    private readonly Dictionary<string, ServerPingResult> _pings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.Ordinal);

    private CancellationTokenSource? _pingCts;
    private ServerSort _sort = ServerSort.List;
    private bool _showHidden;
    private bool _loading;

    // ── console state ────────────────────────────────────────────────────────
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
        Loaded += async (_, _) => await LoadAsync();
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

    // ── loading ──────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        StatusLabel.Text = "Loading…";
        try
        {
            _packs = await App.State.Api.ListPacksAsync();
            _packs = _packs.Where(p => !App.State.Settings.IsPackHidden(p.Id)).ToList();
            RebuildPackFilter();
            await ScanAsync();
            if (App.State.Api.PackListStale is { Length: > 0 } why)
                StatusLabel.Text = $"Showing your last known instances — the server is not answering ({why}).";
        }
        catch (Exception ex) { StatusLabel.Text = "Could not load instances: " + ex.Message; }
        finally { _loading = false; }
    }

    /// <summary>Re-reads every instance's server list off the UI thread, then rebuilds and re-pings.</summary>
    private async Task ScanAsync()
    {
        var packs = _packs.ToList();
        try
        {
            _entries = await Task.Run(() => _servers.ScanAll(packs));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not read the server lists: " + ex.Message;
            _entries = new List<ServerEntry>();
        }
        Refresh();
        await PingVisibleAsync();
    }

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
    /// <remarks>Entries that share an address are one row: the same server usually appears in several
    /// instances, and five identical rows differing only in which instance they came from is exactly
    /// the mess this page exists to clear up. The row remembers all of them, which is what makes
    /// "Join with…" and the instance list in the tooltip possible.</remarks>
    private void Refresh()
    {
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
            // Cached icons only. Decoding a favicon is image work, and the ping sweep that follows
            // this already does it off the UI thread for every row, online or not.
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

        UpdateEmptyState(query);
        UpdateSubLabel(_rows.Count);
    }

    private void UpdateEmptyState(string query)
    {
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count != 0) return;

        if (_packs.Count == 0)
        {
            EmptyTitle.Text = "No instances yet";
            EmptyBody.Text = "Servers are read from each instance's multiplayer list, so there is nothing to show " +
                             "until you have an instance.";
        }
        else if (query.Length > 0)
        {
            EmptyTitle.Text = "Nothing matches that";
            EmptyBody.Text = $"No server matches “{query}”. The search looks at the name, the address, the MOTD " +
                             "and the instance it came from.";
        }
        else if (_entries.Count > 0 && !_showHidden)
        {
            EmptyTitle.Text = "Only hidden entries here";
            EmptyBody.Text = "Every server in this instance is a hidden direct-connect entry. Use the eye button " +
                             "to show them.";
        }
        else
        {
            EmptyTitle.Text = "No servers yet";
            EmptyBody.Text = "Servers come from each instance's own multiplayer list. Use “Add server” to put one " +
                             "in, or add it in-game and refresh.";
        }
    }

    private void UpdateSubLabel(int total)
    {
        if (total == 0)
        {
            SubLabel.Text = "Every server your instances can join, in one place.";
            return;
        }
        var online = _rows.Count(r => r.State == PingState.Online);
        var instances = SelectedPackId is null
            ? _entries.Select(e => e.SourcePackId).Distinct().Count()
            : 1;
        var consoles = _rows.Count(r => r.HasConsole);
        SubLabel.Text = $"{total} server(s) across {instances} instance(s) · {online} online" +
                        (consoles > 0 ? $" · {consoles} with a console" : "");
    }

    // ── pinging ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Pings every row on screen, concurrently but capped, and paints each result as it lands.
    /// </summary>
    /// <remarks>Each row updates the moment its own ping comes back rather than at the end of the
    /// sweep, so a list with one dead address in it is not held at "pinging…" for the full timeout.
    /// A previous sweep is cancelled first — pressing refresh twice must not have two sweeps writing
    /// over each other.</remarks>
    private async Task PingVisibleAsync()
    {
        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        var ct = _pingCts.Token;

        var rows = _rows.ToList();
        if (rows.Count == 0) return;
        foreach (var row in rows) row.SetPinging();
        StatusLabel.Text = $"Pinging {rows.Count} server(s)…";

        using var gate = new SemaphoreSlim(PingConcurrency);
        var tasks = rows.Select(async row =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var result = await MinecraftServerPing.PingAsync(row.Address, ct: ct);
                    // Decode the favicon here, on the pool thread, and freeze it — a BitmapImage built
                    // on the UI thread for every row would be a visible hitch on a long list.
                    var icon = DecodeFavicon(result.FaviconBase64) ?? DecodeFavicon(row.Primary.IconBase64);
                    if (ct.IsCancellationRequested) return;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _pings[row.Key] = result;
                        if (icon is not null) _icons[row.Key] = icon;
                        row.Apply(result, icon ?? CachedIcon(row.Key));
                    });
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { /* the page moved on; the row keeps its last state */ }
            catch (Exception ex)
            {
                // One row failing in a way the ping itself did not expect must not sink the sweep,
                // and must not be a dialog either — it becomes that row's offline reason.
                await Dispatcher.InvokeAsync(() => row.Apply(ServerPingResult.Failed(ex.Message), null));
            }
        });

        await Task.WhenAll(tasks);

        if (ct.IsCancellationRequested) return;
        StatusLabel.Text = "";
        UpdateSubLabel(_rows.Count);
        if (_sort == ServerSort.Status || _sort == ServerSort.Players) Refresh();
    }

    /// <summary>The icon already decoded for this address, if any. Never decodes: the only places
    /// that should pay for that are off the UI thread.</summary>
    private ImageSource? CachedIcon(string key) => _icons.TryGetValue(key, out var icon) ? icon : null;

    /// <summary>
    /// Turns a base64 PNG into a frozen image, or null for anything that is not one.
    /// </summary>
    /// <remarks>Frozen so it can be handed to the UI thread from the ping's own thread. Both sources
    /// are other people's data — a server's favicon and whatever the game last wrote into
    /// servers.dat — so every failure mode here is "no icon", never an exception.</remarks>
    private static ImageSource? DecodeFavicon(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (FormatException) { return null; }
        catch (NotSupportedException) { return null; }   // not an image WPF can decode
        catch (IOException) { return null; }
    }

    // ── toolbar ──────────────────────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await LoadAsync(); }
        catch (Exception ex) { StatusLabel.Text = "Refresh failed: " + ex.Message; }
    }

    private async void OnPingAll(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusLabel.Text = "Pinging…";
            await PingVisibleAsync();
            StatusLabel.Text = "";
        }
        catch (Exception ex) { StatusLabel.Text = "Ping failed: " + ex.Message; }
    }

    private async void OnPackFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            Refresh();
            await PingVisibleAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => Refresh();

    /// <summary>Expands the compact search box, or collapses it back when it is already open and empty.</summary>
    private void OnSearchToggle(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Visibility == Visibility.Visible && SearchBox.Text.Length == 0)
        {
            CollapseSearch();
            return;
        }
        SearchBox.Visibility = Visibility.Visible;
        CompactSearchHost.Width = 240;
        SearchBox.Focus();
    }

    private void CollapseSearch()
    {
        SearchBox.Visibility = Visibility.Collapsed;
        CompactSearchHost.Width = 36;
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchBox.Text = "";
        CollapseSearch();
        ServerList.Focus();
        e.Handled = true;
    }

    /// <summary>An empty search box collapses when it loses focus, so the toolbar goes back to its
    /// compact shape without the user having to close it.</summary>
    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) CollapseSearch();
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
        _showHidden = !_showHidden;
        HiddenToggle.ToolTip = _showHidden
            ? "Hide the direct-connect entries again"
            : "Show the hidden direct-connect entries Minecraft keeps but never draws";
        Refresh();
    }

    /// <summary>
    /// Page-level shortcuts: F5 re-reads everything, Ctrl+F opens the search, Delete removes the
    /// selected rows.
    /// </summary>
    /// <remarks>Delete is deliberately ignored while a text box has focus — the console's command box
    /// and the search box both live on this page, and deleting a character there must not delete a
    /// server.</remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox or PasswordBox;
        if (e.Key == Key.F5) { _ = LoadAsync(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Visibility = Visibility.Visible;
            CompactSearchHost.Width = 240;
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

    // ── row actions ──────────────────────────────────────────────────────────

    private void OnServerSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private async void OnServerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (ServerList.SelectedItem is ServerRow row) await JoinAsync(row, ServerList);
        }
        catch (Exception ex) { StatusLabel.Text = "Join failed: " + ex.Message; }
    }

    private async void OnAddServer(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_packs.Count == 0) { StatusLabel.Text = "Create an instance first — a server list lives inside one."; return; }
            var result = await ServerEditDialog.AddAsync(_shell, _packs, SelectedPackId ?? _packs[0].Id);
            if (result is null) return;

            await Task.Run(() => _servers.Add(result.PackId, result.Name, result.Address));
            var into = _packs.FirstOrDefault(p => p.Id == result.PackId)?.Name ?? "the instance";
            StatusLabel.Text = $"Added {result.Name} to {into}.";
            await ScanAsync();
        }
        catch (Exception ex) { StatusLabel.Text = "Could not add that server: " + ex.Message; }
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: ServerRow row } anchor) return;
            await JoinAsync(row, anchor);
        }
        catch (Exception ex) { StatusLabel.Text = "Join failed: " + ex.Message; }
    }

    private async void OnCtxJoin(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await JoinAsync(row, ServerList);
        }
        catch (Exception ex) { StatusLabel.Text = "Join failed: " + ex.Message; }
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
                catch (Exception ex) { StatusLabel.Text = "Join failed: " + ex.Message; }
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private async Task JoinWithAsync(ServerRow row, Guid packId)
    {
        if (App.State.MinecraftAccounts.Current is null)
        {
            _shell.OpenMcAccount();
            StatusLabel.Text = "Sign in to a Minecraft account first.";
            return;
        }

        StatusLabel.Text = $"Launching into {row.DisplayName}…";
        try
        {
            var pack = await App.State.Api.GetPackAsync(packId);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            var proc = await App.State.Launcher.LaunchTrackedAsync(
                pack, new Progress<string>(_ => { }), joinServerAddress: row.Address);
            _shell.OpenMinecraftHost(pack, proc);
            StatusLabel.Text = $"{pack.Name} is starting and will connect to {row.Address}.";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Launch cancelled."; }
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ServerRow row }) await EditAsync(row);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not edit that server: " + ex.Message; }
    }

    private async void OnCtxEdit(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await EditAsync(row);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not edit that server: " + ex.Message; }
    }

    /// <summary>
    /// Edits the entry in one instance.
    /// </summary>
    /// <remarks>A row can stand for the same address in several instances, and editing it in one of
    /// them is not the same as editing it everywhere; the status line says which instance was changed,
    /// and the other copies are left alone rather than silently rewritten.</remarks>
    private async Task EditAsync(ServerRow row)
    {
        var entry = row.Primary;
        var result = await ServerEditDialog.EditAsync(_shell, _packs, entry);
        if (result is null) return;

        var changed = await Task.Run(() => _servers.Update(entry, result.Name, result.Address));
        StatusLabel.Text = changed
            ? $"Updated {result.Name} in {entry.SourcePackName}." +
              (row.Sources.Count > 1 ? $" The other {row.Sources.Count - 1} instance(s) still have the old entry." : "")
            : "That entry is no longer in the instance's list — refreshed.";
        await ScanAsync();
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ServerRow row }) await RemoveAsync(new[] { row });
        }
        catch (Exception ex) { StatusLabel.Text = "Could not remove that server: " + ex.Message; }
    }

    private async void OnCtxRemove(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await RemoveAsync(new[] { row });
        }
        catch (Exception ex) { StatusLabel.Text = "Could not remove that server: " + ex.Message; }
    }

    private async Task RemoveSelectedAsync()
    {
        try
        {
            var rows = ServerList.SelectedItems.OfType<ServerRow>().ToList();
            if (rows.Count > 0) await RemoveAsync(rows);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not remove those servers: " + ex.Message; }
    }

    /// <summary>
    /// Removes the rows' entries from their instances' lists, after one confirmation covering all of
    /// them.
    /// </summary>
    /// <remarks>When a row stands for several instances, every copy goes — that is what the row on
    /// screen means, and the confirmation names the instances so it is not a surprise.</remarks>
    private async Task RemoveAsync(IReadOnlyList<ServerRow> rows)
    {
        var entries = rows.SelectMany(r => r.Sources).ToList();
        var instances = entries.Select(e => e.SourcePackName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var what = rows.Count == 1 ? $"“{rows[0].DisplayName}”" : $"{rows.Count} servers";
        var where = instances.Count == 1 ? instances[0] : $"{instances.Count} instances ({string.Join(", ", instances)})";

        if (!await AppDialog.ConfirmAsync(_shell, "Remove server",
                $"Remove {what} from {where}?\n\n" +
                "This only edits your own copy of the multiplayer list — servers.dat is never synced to a " +
                "shared pack. Nothing on the server itself changes.",
                "Remove", "Cancel", danger: true))
            return;

        var removed = await Task.Run(() => entries.Count(entry => _servers.Remove(entry)));
        StatusLabel.Text = removed == 0
            ? "Nothing was removed — those entries were already gone."
            : $"Removed {removed} entr{(removed == 1 ? "y" : "ies")}.";
        await ScanAsync();
    }

    private async void OnCopyTo(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ServerRow row }) await CopyToAsync(row);
        }
        catch (Exception ex) { StatusLabel.Text = "Copy failed: " + ex.Message; }
    }

    private async void OnCtxCopyTo(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await CopyToAsync(row);
        }
        catch (Exception ex) { StatusLabel.Text = "Copy failed: " + ex.Message; }
    }

    /// <summary>Copies the entry — name, address and favicon — into another instance's list. This is
    /// the thing that is genuinely painful without a launcher: otherwise it is retyping the address
    /// into the multiplayer screen of every instance in turn.</summary>
    private async Task CopyToAsync(ServerRow row)
    {
        var already = row.Sources.Select(s => s.SourcePackId).ToHashSet();
        var targets = _packs.Where(p => !already.Contains(p.Id)).ToList();
        if (targets.Count == 0)
        {
            StatusLabel.Text = "Every instance already has this server.";
            return;
        }

        var picker = new PackPickerDialog(targets, "Copy server to…",
            $"Pick the instance to add {row.DisplayName} to. Its name and icon come with it.", "Copy")
        { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is not { } targetId) return;

        var entry = row.Primary;
        var copied = await Task.Run(() => _servers.CopyTo(entry, targetId));
        var name = targets.First(p => p.Id == targetId).Name;
        StatusLabel.Text = copied
            ? $"Copied {row.DisplayName} into {name}."
            : $"{name} already lists that address.";
        await ScanAsync();
    }

    private void OnCtxCopyAddress(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        ClipboardHelper.TrySetText(row.Address);
        StatusLabel.Text = $"Copied {row.Address}.";
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
            StatusLabel.Text = result.Online
                ? $"{row.DisplayName}: {result.PlayersOnline}/{result.PlayersMax} online, {result.LatencyMs} ms."
                : $"{row.DisplayName}: {result.Error}.";
            UpdateSubLabel(_rows.Count);
        }
        catch (Exception ex) { StatusLabel.Text = "Ping failed: " + ex.Message; }
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
            if (!moved) { StatusLabel.Text = "That entry is already at the end of the list."; return; }
            StatusLabel.Text = _sort == ServerSort.List
                ? $"Moved {row.DisplayName} in {entry.SourcePackName}'s list."
                : $"Moved {row.DisplayName} in {entry.SourcePackName}'s list — sort by “Order in the game's list” to see it.";
            await ScanAsync();
        }
        catch (Exception ex) { StatusLabel.Text = "Could not move that entry: " + ex.Message; }
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

    // ── console ──────────────────────────────────────────────────────────────

    private async void OnConsole(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: ServerRow row }) await OpenConsoleAsync(row);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open the console: " + ex.Message; }
    }

    private async void OnCtxConsole(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await OpenConsoleAsync(row);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open the console: " + ex.Message; }
    }

    private async void OnCtxConsoleSetup(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromMenu(sender) is { } row) await ConfigureConsoleAsync(row);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not save those console settings: " + ex.Message; }
    }

    /// <summary>
    /// Opens the console pane for a server, asking for its RCON details first if it has none.
    /// </summary>
    /// <remarks>Only servers the user has configured get a console: RCON is off by default on every
    /// server and there is nothing to connect to otherwise, so offering a console on all of them
    /// would be offering a button that cannot work.</remarks>
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
            StatusLabel.Text = $"Forgot the console details for {row.DisplayName}.";
            Refresh();
            return false;
        }

        var entry = App.State.Settings.GetOrCreateServerAdmin(row.Address);
        entry.Label = result.Label;
        entry.RconHost = result.RconHost;
        entry.RconPort = result.RconPort;
        entry.RconPassword = result.Password;
        App.State.Settings.Save();

        // A password change invalidates the open session, so drop it rather than leave a console that
        // is authenticated with something the user has just replaced.
        if (_consoleRow?.Key == row.Key) CloseConsoleConnection();
        StatusLabel.Text = $"Saved console details for {row.DisplayName}.";
        Refresh();
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
                AppendConsole("— disconnected —");
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
        SetConsoleState(connected: false, $"Connecting to {host}:{port}…");
        try
        {
            _rcon = await RconClient.ConnectAsync(host, port, admin.RconPassword!, ct: _consoleCts.Token);
            AppendConsole($"— connected to {host}:{port} —");
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
    /// <remarks>The history index sits one past the end when nothing is being recalled, so the first
    /// press of Up reaches the most recent command and Down walks back out to an empty box — the same
    /// behaviour as a shell, which is what anyone typing server commands expects.</remarks>
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
    /// <remarks>A command with no output still gets a line, because a silent console is
    /// indistinguishable from a broken one. Minecraft's leading slash is stripped: RCON commands are
    /// entered without it and a stray "/" is the commonest reason a pasted command does nothing.</remarks>
    private async Task RunCommandAsync(string command)
    {
        if (_rcon is not { IsConnected: true })
        {
            SetConsoleStatus("Not connected — press Connect first.");
            return;
        }

        var clean = command.TrimStart('/');
        RememberCommand(command);
        AppendConsole("> " + clean);
        SetConsoleStatus("Running…");
        try
        {
            var reply = await _rcon.SendCommandAsync(clean, ct: _consoleCts?.Token ?? CancellationToken.None);
            AppendConsole(string.IsNullOrWhiteSpace(reply)
                ? "(the server said nothing)"
                : MinecraftServerPing.StripFormatting(reply));
            SetConsoleStatus("");
        }
        catch (OperationCanceledException) { SetConsoleStatus("Cancelled."); }
        catch (TimeoutException ex) { SetConsoleStatus(ex.Message); }
        catch (Exception ex)
        {
            // A dead socket is the usual cause; say so and put the pane back into its offline state
            // rather than leaving a Disconnect button that no longer means anything.
            CloseConsoleConnection();
            SetConsoleState(connected: false, "The console connection dropped: " + ex.Message);
        }
    }

    /// <summary>Stores the command in this server's history, newest last, without duplicating the one
    /// before it. The password is never part of this — only what the user typed into the command box.</summary>
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

    // ── rows ─────────────────────────────────────────────────────────────────

    private sealed record PackFilterItem(Guid? Id, string Label);

    /// <summary>
    /// One server as the page shows it: an address, every instance that lists it, and whatever the
    /// last ping said.
    /// </summary>
    /// <remarks>Raises change notifications rather than being rebuilt, so a ping landing repaints one
    /// row instead of rebuilding the list under the user's cursor. It deliberately exposes no
    /// <see cref="Brush"/> — the status colour is a trigger in the template, so it follows a theme
    /// change; a brush captured here would be frozen at whatever theme was current when the row was
    /// built.</remarks>
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

        /// <summary>The saved label wins over the name in servers.dat: someone who has named the
        /// server in the launcher has told us what they call it.</summary>
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
            PingState.Pinging => "…",
            PingState.Offline => "OFFLINE",
            _ => "—"
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
                    PingState.Pinging => "pinging…",
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

        public string ConsoleSetupLabel => HasConsole ? "Console settings…" : "Set up console…";

        public string RemoveTooltip => Sources.Count == 1
            ? $"Remove this server from {Primary.SourcePackName}'s list"
            : $"Remove this server from all {Sources.Count} instances that list it";

        /// <summary>Everything known about the server, for the row's tooltip — including who is on it,
        /// which is the one thing worth hovering a server list for.</summary>
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
                    lines.Add("Offline — " + offline.Error);
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

        /// <summary>Almost every displayed property is derived from the ping or the settings, so one
        /// null-name notification (which WPF reads as "everything changed") is both cheaper and less
        /// error-prone than listing them.</summary>
        private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
