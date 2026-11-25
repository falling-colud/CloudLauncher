using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The instance list: every instance this person can play, as cards. Paints the list this computer
/// remembers first, then the server's answer onto the same cards. Signed out it lists this computer's
/// own instances (the API client answers from <see cref="LocalPackStore"/>) and offers to sign in.
/// The Windows launcher's PackListView, with its calls, checks and wording.
/// </summary>
public partial class InstancesPage : UserControl, IShellPage, IRefreshable
{
    /// <summary>Narrowest a card gets before a column is dropped.</summary>
    private const double CardMinWidth = 228;

    /// <summary>One card per instance for the page's lifetime: progress and game state are put on the
    /// card on screen, found by instance id.</summary>
    private readonly Dictionary<Guid, InstanceCard> _cards = new();

    /// <summary>The last progress report per instance, for cards made after it arrived.</summary>
    private readonly Dictionary<Guid, ProgressInfo> _progress = new();

    /// <summary>Every instance listed (hidden ones left out), in no particular order.</summary>
    private List<PackSummary> _packs = new();

    /// <summary>True once the server (or this computer, signed out) has answered at least once.</summary>
    private bool _hasData;
    private bool _refreshing;
    private bool _refreshAgain;
    private bool _attached;
    private DispatcherTimer? _staleRetry;
    private Action? _stateAction;

    public InstancesPage()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        ListScroll.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
        // The page is made anew on every sign-in and sign-out (MainWindow.OnSignedIn/OnSignedOut).
        SignedOutBar.IsVisible = !App.State.Api.IsSignedIn;
        PaintRemembered();
    }

    public ShellNav Nav => ShellNav.Instances;

    /// <summary>Coming on screen: nobody asked, so the refresh stays quiet.</summary>
    public void OnShown() => _ = RefreshCoreAsync(quiet: true);

    /// <summary>F5 and the refresh button.</summary>
    public Task RefreshAsync() => RefreshCoreAsync(quiet: false);

    // ── service events ───────────────────────────────────────────────────────

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        ProgressHub.ProgressChanged += OnProgressChanged;
        ProgressHub.ProgressCleared += OnProgressCleared;
        PackJobs.Changed += OnPackJobChanged;
        App.State.Instances.StateChanged += OnInstanceStateChanged;
        App.State.ModpackDownload.PackAdded += OnPackAdded;
        // Whatever changed while the page was away (a game exiting, a transfer ending). Progress heard
        // before then may have ended unheard: a transfer still running reports again within moments.
        _progress.Clear();
        foreach (var card in _cards.Values)
        {
            card.SetProgress(null);
            card.SetStatus(App.State.Instances.GetStatus(card.Id));
            card.RefreshUsage();
        }
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        ProgressHub.ProgressChanged -= OnProgressChanged;
        ProgressHub.ProgressCleared -= OnProgressCleared;
        PackJobs.Changed -= OnPackJobChanged;
        App.State.Instances.StateChanged -= OnInstanceStateChanged;
        App.State.ModpackDownload.PackAdded -= OnPackAdded;
        _staleRetry?.Stop();
    }

    private void OnProgressChanged(ProgressInfo info)
    {
        _progress[info.PackId] = info;
        if (_cards.TryGetValue(info.PackId, out var card)) card.SetProgress(info);
    }

    private void OnProgressCleared(Guid packId)
    {
        _progress.Remove(packId);
        if (_cards.TryGetValue(packId, out var card)) card.SetProgress(null);
    }

    private void OnPackJobChanged(Guid packId)
    {
        if (_cards.TryGetValue(packId, out var card)) card.RefreshJob();
    }

    private void OnInstanceStateChanged(Guid packId)
    {
        if (_cards.TryGetValue(packId, out var card)) card.SetStatus(App.State.Instances.GetStatus(packId));
    }

    /// <summary>An instance was made or changed elsewhere (a modpack import, a download).</summary>
    private void OnPackAdded(PackSummary pack)
    {
        App.State.Settings.UnhidePack(pack.Id);
        var list = _packs.Where(p => p.Id != pack.Id).ToList();
        list.Add(pack);
        _hasData = true;
        SetPacks(list);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>Draws the list this computer last had (the account's last answer plus this computer's
    /// own instances) before asking the server, so the first screen doesn't load twice.</summary>
    private void PaintRemembered()
    {
        List<PackSummary>? remembered;
        try { remembered = App.State.Api.PeekPacks(); }
        catch (Exception ex)
        {
            AppLog.LogError("instances", ex);
            remembered = null;
        }
        if (remembered is { Count: > 0 }) SetPacks(remembered);
        else ShowState(PageState.Loading);
    }

    private async Task RefreshCoreAsync(bool quiet)
    {
        if (_refreshing)
        {
            // Something changed while a refresh was on its way (a delete, a new instance): ask again
            // once it is back, so the answer on screen is the newer one.
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        _staleRetry?.Stop();
        RefreshButton.IsEnabled = false;
        RefreshBar.IsVisible = !quiet && _cards.Count > 0;
        try
        {
            do
            {
                _refreshAgain = false;
                await RefreshOnceAsync();
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            RefreshBar.IsVisible = false;
        }
    }

    private async Task RefreshOnceAsync()
    {
        try
        {
            var packs = await App.State.Api.ListPacksAsync();
            _hasData = true;
            SetPacks(packs);

            // A list served from the cache is still a list: say where it came from and retry shortly.
            // Offline is not an error, since the instances on this computer still play.
            if (App.State.Api.PackListStale is { Length: > 0 } why)
            {
                ShowOffline($"The server can't be reached, so this is the list it gave last: {why}.");
                ScheduleStaleRetry();
            }
            else OfflineBar.IsVisible = false;
        }
        catch (OfflineException ex)
        {
            var reason = ex.Reason ?? "the server is unreachable";
            if (_cards.Count == 0)
                ShowState(PageState.Offline, reason);
            else
                ShowOffline($"The server can't be reached ({reason}), so this is the list from "
                            + (PackListCache.AgeInWords() ?? "earlier") + ".");
            ScheduleStaleRetry();
        }
        catch (SessionExpiredException)
        {
            // The shell has its own answer for this (back to Sign In); nothing to add here.
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances", ex);
            if (_cards.Count == 0) ShowState(PageState.Error);
            else SetStatus("The list could not be refreshed: " + Plain(ex), error: true);
        }
    }

    /// <summary>One delayed re-fetch after the server failed: not a poll, it stops at the first real
    /// answer and every refresh reschedules it.</summary>
    private void ScheduleStaleRetry()
    {
        if (!_attached) return;
        if (_staleRetry is null)
        {
            _staleRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _staleRetry.Tick += (_, _) =>
            {
                _staleRetry.Stop();
                if (_attached) _ = RefreshCoreAsync(quiet: true);
            };
        }
        _staleRetry.Stop();
        _staleRetry.Start();
    }

    private void ShowOffline(string text)
    {
        OfflineText.Text = text;
        OfflineBar.IsVisible = true;
    }

    /// <summary>Puts <paramref name="packs"/> on screen: cards kept for instances still listed, made for
    /// new ones, dropped for the rest.</summary>
    private void SetPacks(IEnumerable<PackSummary> packs)
    {
        _packs = packs
            .Where(p => !App.State.Settings.IsPackHidden(p.Id))
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .ToList();
        var listed = _packs.Select(p => p.Id).ToHashSet();
        foreach (var gone in _cards.Keys.Where(id => !listed.Contains(id)).ToList())
            _cards.Remove(gone);

        foreach (var pack in _packs)
        {
            if (_cards.TryGetValue(pack.Id, out var card))
            {
                card.Rebind(pack);
                continue;
            }
            card = new InstanceCard(pack, OpenInstance, c => _ = PlayAsync(c), StopInstance, BuildMenu);
            if (_progress.TryGetValue(pack.Id, out var info)) card.SetProgress(info);
            _cards[pack.Id] = card;
        }
        ApplyFilter();
    }

    /// <summary>The search and the sort, then the count and which state the page is in. The only place
    /// that decides the page is empty, so it never says so before it has looked.</summary>
    private void ApplyFilter()
    {
        var filter = (SearchBox.Text ?? "").Trim();
        IEnumerable<PackSummary> source = _packs;
        if (filter.Length > 0)
            source = source.Where(p => Has(p.Name, filter) || Has(p.Summary, filter) || Has(p.Description, filter));
        var shown = Sort(source).ToList();

        CardGrid.Children.Clear();
        foreach (var pack in shown) CardGrid.Children.Add(_cards[pack.Id]);
        UpdateColumns(ListScroll.Bounds.Width);

        CountText.Text = _packs.Count == 0 ? ""
            : shown.Count == _packs.Count ? Instances(_packs.Count)
            : $"{shown.Count} of {Instances(_packs.Count)}";

        if (_packs.Count == 0)
        {
            if (_hasData) ShowState(PageState.Empty);
        }
        else if (shown.Count == 0) ShowState(PageState.NoMatch, filter);
        else ShowState(PageState.Content);

        static bool Has(string? text, string part) => text?.Contains(part, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private static string Instances(int n) => n == 1 ? "1 instance" : $"{n:N0} instances";

    /// <summary>Pinned first, then the sort chosen on Windows (Settings.PackSortMode), then by name.</summary>
    private static IEnumerable<PackSummary> Sort(IEnumerable<PackSummary> packs)
    {
        var settings = App.State.Settings;
        var ordered = packs.OrderByDescending(p => settings.GetPackUsage(p.Id).IsPinned);
        ordered = settings.PackSortMode switch
        {
            PackSortMode.Version => ordered
                .ThenByDescending(p => VersionSortValue(p.MinecraftVersion))
                .ThenByDescending(p => p.MinecraftVersion ?? ""),
            PackSortMode.PlayCount => ordered.ThenByDescending(p => settings.GetPackUsage(p.Id).PlayCount),
            PackSortMode.TimePlayed => ordered.ThenByDescending(p => settings.GetPackUsage(p.Id).TotalPlayTimeSeconds),
            _ => ordered
                .ThenByDescending(p => settings.GetPackUsage(p.Id).LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(p => p.UpdatedAt)
        };
        return ordered.ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static long VersionSortValue(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.Split('.', '-', '_');
        long value = 0;
        for (var i = 0; i < 4; i++)
        {
            value *= 1000;
            if (i >= parts.Length) continue;
            if (!int.TryParse(parts[i], out var part)) break;
            value += Math.Clamp(part, 0, 999);
        }
        return value;
    }

    /// <summary>As many columns as fit at <see cref="CardMinWidth"/>, so rows are always full width.</summary>
    private void UpdateColumns(double width)
    {
        var usable = width - CardGrid.Margin.Left - CardGrid.Margin.Right;
        if (usable <= 0) return;
        var spacing = CardGrid.ColumnSpacing;
        var columns = Math.Max(1, (int)Math.Floor((usable + spacing) / (CardMinWidth + spacing)));
        if (CardGrid.Columns != columns) CardGrid.Columns = columns;
    }

    // ── loading / empty / error ──────────────────────────────────────────────

    private enum PageState { Content, Loading, Empty, NoMatch, Error, Offline }

    private void ShowState(PageState state, string? detail = null)
    {
        ListScroll.IsVisible = state == PageState.Content;
        StatePanel.IsVisible = state != PageState.Content;
        StateProgress.IsVisible = state == PageState.Loading;
        _stateAction = null;
        string glyph = "", title = "", body = "", button = "";
        switch (state)
        {
            case PageState.Loading:
                body = "Loading your instances...";
                break;
            case PageState.Empty:
                glyph = "\uE7C3";
                title = "No Instances Yet";
                body = "Create one, import a modpack file, or find one under Browse Modpacks.";
                button = "Create Instance";
                _stateAction = () => _ = CreateAsync();
                break;
            case PageState.NoMatch:
                glyph = "\uE721";
                title = "Nothing Found";
                body = $"No instance matches “{detail}”.";
                button = "Clear Search";
                _stateAction = () => SearchBox.Text = "";
                break;
            case PageState.Error:
                glyph = "\uE783";
                title = "No List to Show";
                body = "Neither the server nor this computer had a list of instances to show. The launcher log has the details.";
                button = "Try Again";
                _stateAction = () => _ = RefreshAsync();
                break;
            case PageState.Offline:
                glyph = "\uE7BA";
                title = "Can't Reach the Server";
                body = $"CloudLauncher could not reach its server ({detail}). Your instances show here once it answers.";
                button = "Try Again";
                _stateAction = () => _ = RefreshAsync();
                break;
        }
        StateGlyph.Text = glyph;
        StateGlyph.IsVisible = glyph.Length > 0;
        StateTitle.Text = title;
        StateTitle.IsVisible = title.Length > 0;
        StateBody.Text = body;
        StateButton.Content = button;
        StateButton.IsVisible = button.Length > 0;
    }

    private void OnStateButton(object? sender, RoutedEventArgs e) => _stateAction?.Invoke();

    /// <summary>A plain sentence for the page: errors stay on the page's line, the rest go to the
    /// status bar for a few seconds.</summary>
    private void SetStatus(string text, bool error = false)
    {
        if (error)
        {
            StatusText.Text = text;
            StatusText.IsVisible = text.Length > 0;
            return;
        }
        StatusText.IsVisible = false;
        if (text.Length > 0) MainWindow.Current.Toast(text);
    }

    private static string Plain(Exception ex) => InstancePlay.Sentence(
        ex is ApiException api ? ApiClient.ServerSentence(api) ?? api.Message : ex.Message);

    // ── header actions ───────────────────────────────────────────────────────

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnCreate(object? sender, RoutedEventArgs e) => await CreateAsync();

    private void OnSignIn(object? sender, RoutedEventArgs e) => MainWindow.Current.Navigate(new SignInPage());

    /// <summary>The create card, then the new instance in the list (Windows CreatePackAsync).</summary>
    private async Task CreateAsync()
    {
        PackSummary? created;
        try { created = await CreateInstanceCard.ShowAsync(MainWindow.Current); }
        catch (Exception ex)
        {
            AppLog.LogError("instances.create", ex);
            SetStatus("The instance could not be created: " + Plain(ex), error: true);
            return;
        }
        if (created is null) return;

        // The shared defaults (resource packs, shaders and so on) go into a new instance at once, so
        // they don't have to be added by hand; a failure here never fails the creation.
        _ = ApplyDefaultsToNewInstanceAsync(created);
        OnPackAdded(created);
        SetStatus($"Created “{created.Name}”.");
        await RefreshAsync();
    }

    private async Task ApplyDefaultsToNewInstanceAsync(PackSummary pack)
    {
        try
        {
            var result = await App.State.ContentDefaults.ReconcileAsync(pack);
            if (result.DidAnything) SetStatus($"{pack.Name}: {result.Summary()}");
        }
        catch (Exception ex) { AppLog.LogError("content-defaults", ex); }
    }

    /// <summary>A .mrpack or CurseForge .zip from this computer, imported into a new instance whose card
    /// shows the import's progress (Windows OnImportModpack).</summary>
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import a Modpack",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Modpack files") { Patterns = ["*.mrpack", "*.zip"] },
                    new FilePickerFileType("Modrinth pack") { Patterns = ["*.mrpack"] },
                    new FilePickerFileType("CurseForge pack") { Patterns = ["*.zip"] }
                ]
            });
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances.import", ex);
            SetStatus("The file picker could not be opened: " + Plain(ex), error: true);
            return;
        }
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            SetStatus("Pick a modpack file that is on this computer.", error: true);
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        try
        {
            // Makes the instance and returns; the import runs on, reporting on the new card.
            await App.State.ModpackDownload.StartLocalFileImportAsync(path, name);
            SetStatus($"Importing “{name}”...");
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances.import", ex);
            SetStatus($"“{name}” could not be imported: {Plain(ex)}", error: true);
        }
    }

    /// <summary>The folder every instance lives in, made first so opening it can't fail on a new
    /// computer.</summary>
    private void OnOpenInstancesFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.State.Settings.PacksRoot;
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir))
                SetStatus("That folder could not be opened. The launcher log has the details.", error: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances", ex);
            SetStatus("That folder could not be opened: " + Plain(ex), error: true);
        }
    }

    // ── a card's actions ─────────────────────────────────────────────────────

    private void OpenInstance(InstanceCard card) =>
        MainWindow.Current.Navigate(new InstancePage(card.Pack.Id, card.Pack.Name));

    /// <summary>Instances between a Play press and the game starting (the checks before a launch), so a
    /// second press doesn't start a second launch.</summary>
    private readonly HashSet<Guid> _starting = new();

    /// <summary>Play from the card: the instance page's launch, from here.</summary>
    private async Task PlayAsync(InstanceCard card)
    {
        var id = card.Id;
        if (!_starting.Add(id)) return;
        try
        {
            SetStatus("Preparing to launch...");
            PackDetail pack;
            try { pack = await App.State.Api.GetPackAsync(id); }
            catch (Exception ex)
            {
                AppLog.LogError("instances", ex);
                SetStatus("That instance could not be launched: " + Plain(ex), error: true);
                return;
            }
            var started = await InstancePlay.PlayAsync(pack, (text, error) => SetStatus(text, error));
            if (!started) return;
            // Played just now: the last-played sort moves it to the front, as on Windows.
            card.RefreshUsage();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppLog.LogError("launch", ex);
            SetStatus("That instance could not be launched: " + Plain(ex), error: true);
        }
        finally { _starting.Remove(id); }
    }

    private void StopInstance(InstanceCard card) => InstancePlay.Stop(card.Id, (text, error) => SetStatus(text, error));

    /// <summary>The card's menu, built fresh each time so it matches the instance's state.</summary>
    private MenuFlyout BuildMenu(InstanceCard card)
    {
        var pack = card.Pack;
        var busy = App.State.Instances.IsBusy(pack.Id);
        var items = new List<object>
        {
            busy
                ? MenuItemFor("Stop", "\uE711", () => StopInstance(card))
                : MenuItemFor("Play", "\uE768", () => _ = PlayAsync(card), enabled: !pack.IsEmpty),
            MenuItemFor("Open", "\uE8A7", () => OpenInstance(card)),
            MenuItemFor("Open Folder", "\uE838", () => OpenGameFolder(pack))
        };
        if (pack.IsShared)
            items.Add(MenuItemFor("Download Latest", "\uE896", () => _ = QuickUpdateAsync(pack.Id)));
        var pinned = App.State.Settings.GetPackUsage(pack.Id).IsPinned;
        items.Add(MenuItemFor(pinned ? "Unpin" : "Pin", "\uE718", () => TogglePinned(card)));
        if (App.State.Api.IsLocalPack(pack.Id) && App.State.Api.IsSignedIn)
            items.Add(MenuItemFor("Add to Account", "\uE753", () => _ = AddToAccountAsync(card)));
        items.Add(new Separator());
        var owner = App.State.OwnsPack(pack.Id, pack.OwnerId);
        items.Add(MenuItemFor(owner ? "Delete" : "Remove", "\uE74D", () => _ = DeleteAsync(card), danger: true));
        return new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight };
    }

    private static MenuItem MenuItemFor(string header, string glyph, Action click, bool enabled = true, bool danger = false)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13 };
        icon.Classes.Add("icon");
        var item = new MenuItem { Header = header, Icon = icon, IsEnabled = enabled };
        if (danger)
        {
            item.Bind(MenuItem.ForegroundProperty, item.GetResourceObservable("DangerBrush"));
            icon.Bind(TextBlock.ForegroundProperty, item.GetResourceObservable("DangerBrush"));
        }
        item.Click += (_, _) => click();
        return item;
    }

    private void OpenGameFolder(PackSummary pack)
    {
        try
        {
            var dir = App.State.Packs.GameDir(pack.Id, pack.Name);
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir))
                SetStatus("That folder could not be opened. The launcher log has the details.", error: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances", ex);
            SetStatus("That folder could not be opened: " + Plain(ex), error: true);
        }
    }

    private void TogglePinned(InstanceCard card)
    {
        var usage = App.State.Settings.GetPackUsage(card.Id);
        App.State.Settings.SetPackPinned(card.Id, !usage.IsPinned);
        App.State.Settings.Save();
        card.RefreshUsage();
        ApplyFilter();
    }

    /// <summary>Downloads a shared instance's latest files, as a job the card's bar can pause and stop
    /// (Windows QuickUpdateAsync).</summary>
    private async Task QuickUpdateAsync(Guid id)
    {
        if (PackJobs.For(id) is { } running)
        {
            SetStatus(running.Kind == PackJobKind.Export
                ? "An export is reading that instance. Update it once that has finished."
                : $"That instance is already busy with a {running.KindLabel}. Use the bar on its card to pause or stop it.",
                error: true);
            return;
        }

        SetStatus("Updating from the server...");
        var job = PackJobs.Start(id, PackJobKind.Sync);
        try
        {
            var pack = await App.State.Api.GetPackAsync(id, job.Token);
            if (!pack.IsShared)
            {
                SetStatus("That instance is not hosted on the server, so there is nothing to update from.", error: true);
                return;
            }
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);
            await App.State.Packs.DownloadSharedAsync(pack.Id, InstanceLog.ProgressFor(pack.Id), job.Token, job);
            SetStatus($"“{pack.Name}” is up to date.");
        }
        catch (OperationCanceledException)
        {
            var removed = job.RollbackCreatedFiles();
            SetStatus(removed > 0
                ? $"Update stopped. The {removed} file(s) it had downloaded were removed."
                : "Update stopped.");
            ProgressHub.Clear(id);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances", ex);
            SetStatus("The update did not finish: " + Plain(ex), error: true);
        }
        finally { PackJobs.Finish(job); }
    }

    /// <summary>Moves an instance made without an account onto the signed-in one; its files stay where
    /// they are (Windows OnCtxAddToAccount).</summary>
    private async Task AddToAccountAsync(InstanceCard card)
    {
        var name = card.Pack.Name;
        var ok = await MainWindow.Current.ConfirmAsync("Add to Account",
            $"“{name}” goes onto your CloudLauncher account ({App.State.Settings.Username}): it is listed on "
            + "every computer you sign in on and can be shared. Its files stay on this computer.",
            "Add to Account");
        if (!ok) return;

        SetStatus($"Adding “{name}” to your account...");
        try
        {
            var added = await App.State.Api.AddLocalPackToAccountAsync(card.Id);
            await RefreshAsync();
            SetStatus($"“{added.Name}” is on your account now.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances", ex);
            SetStatus($"“{name}” could not be added to your account: {Plain(ex)}", error: true);
        }
    }

    /// <summary>
    /// The owner deletes an instance (an instance on this computer only goes to the trash, folder and
    /// all); anyone else removes it from their list, which leaves the original alone. The Windows
    /// OnCtxDelete, step for step.
    /// </summary>
    private async Task DeleteAsync(InstanceCard card)
    {
        var pack = card.Pack;
        var packId = pack.Id;
        var isLocal = App.State.Api.IsLocalPack(packId);
        var isOwner = App.State.OwnsPack(packId, pack.OwnerId);

        // Minecraft holds the instance's files open: stopping it is the player's decision, not a side
        // effect of a delete.
        if (App.State.Instances.GetStatus(packId) == MinecraftInstanceStatus.Running)
        {
            SetStatus("Minecraft is running from that instance. Close the game first, then delete it.", error: true);
            return;
        }

        var (title, message, yes) = isLocal
            ? ("Delete Instance?",
               $"“{pack.Name}” is only on this computer, so deleting it deletes its folder: mods, worlds, "
               + "settings and everything else in it. The folder goes to the trash.", "Delete")
            : isOwner
                ? ("Delete Instance?",
                   $"“{pack.Name}” is removed from the server and everyone you shared it with loses it. "
                   + "Its files on this computer stay.", "Delete")
                : ("Remove Instance?",
                   $"“{pack.Name}” leaves your instance list. The original and everyone else's copies are not affected.",
                   "Remove");
        if (!await MainWindow.Current.ConfirmAsync(title, message, yes, "Cancel", danger: true)) return;

        // A copy that is stopped takes its unfinished instance back by itself, which is the delete.
        if (PackJobs.For(packId) is { Kind: PackJobKind.Copy })
        {
            SetStatus($"Stopping the copy into “{pack.Name}”...");
            if (!await PackJobs.StopAndWaitAsync(packId, TimeSpan.FromSeconds(30)))
            {
                SetStatus($"The copy into “{pack.Name}” has not stopped yet. Try again in a moment.", error: true);
                return;
            }
            await RefreshAsync();
            SetStatus($"Stopped the copy; “{pack.Name}” was removed.");
            return;
        }

        // Stop anything still downloading into it, and wait for the stop to take its files back, or
        // the transfer would keep writing into a folder nothing lists.
        if (PackJobs.IsRunning(packId) || App.State.Instances.IsBusy(packId))
        {
            SetStatus($"Stopping what “{pack.Name}” was downloading...");
            App.State.Instances.Stop(packId);
            if (!await PackJobs.StopAndWaitAsync(packId, TimeSpan.FromSeconds(30)))
            {
                SetStatus($"The download into “{pack.Name}” has not stopped yet, so it was not deleted. Try again in a moment.", error: true);
                return;
            }
            ProgressHub.Clear(packId);
        }

        // Set when the instance left this list but the server was not told: a partial success.
        var syncWarning = false;
        try
        {
            if (isOwner)
            {
                await App.State.Api.DeletePackAsync(packId);
                App.State.Settings.UnhidePack(packId);
            }
            else
            {
                App.State.Settings.HidePack(packId);
                try { await App.State.Api.UnsubscribePackAsync(packId); }
                catch (Exception ex)
                {
                    AppLog.LogError("instances", ex);
                    syncWarning = true;
                }
            }

            // Only once it is really gone: a failed delete keeps its place in the folders.
            foreach (var folder in App.State.Settings.PackFolders.Values) folder.Remove(packId);
            App.State.Settings.Save();
            _packs.RemoveAll(p => p.Id == packId);
            SetPacks(_packs.ToList());
            await RefreshAsync();

            if (syncWarning)
                SetStatus("Removed from your list, but the server was not told, so it may come back on the next refresh.", error: true);
            else
                SetStatus(isLocal ? "Instance deleted. Its folder is in the trash."
                    : isOwner ? "Instance deleted." : "Instance removed.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("The instance was not deleted.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances", ex);
            SetStatus(ex is IOException && isLocal ? ex.Message
                : isOwner ? "That instance could not be deleted: " + Plain(ex)
                : "That instance could not be removed: " + Plain(ex),
                error: true);
        }
    }
}
