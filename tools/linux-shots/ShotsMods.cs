using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using CloudLauncher.Views;
using static LinuxShots.Program;

namespace LinuxShots;

/// <summary>Scenarios for installed mods, the mod browser, the modpack browser and the download-by-hand
/// card: "mods", "mods-empty", "mod-browser", "mod-browser-offline", "modpacks", "modpacks-offline",
/// "manual-card", "manual-card-import" (with the instance's chip and the check before Play) and
/// "manual-card-watch" (a file landing in the watched folder goes into the instance) and
/// "manual-card-queued" (the service's Needed event brings the card up through the main window).</summary>
/// <remarks>The fake server cannot reach the stores, so the populated store pages are fed by
/// <see cref="FakeCatalog"/>; the "-offline" ones show what the real stores' failure looks like. The
/// download-by-hand card watches a folder under the output folder, never the real Downloads folder, and
/// no scenario clicks a button that opens a browser.</remarks>
public static class ShotsMods
{
    // FakeWorld's instances (tools/smoke/showcase/FakeWorld.cs): G(n) = 00000000-0000-4000-8000-{n:D12}.
    private static readonly Guid Hearthstone = new("00000000-0000-4000-8000-000000000100");
    private static readonly Guid VanillaPlus = new("00000000-0000-4000-8000-000000000104");

    /// <summary>Runs scenario <paramref name="name"/> if it is one of this file's; false otherwise.</summary>
    public static bool TryRun(string name)
    {
        // Awaits in the pages come back to the UI thread through this, as in the real app.
        AvaloniaSynchronizationContext.InstallIfNeeded();
        switch (name)
        {
            case "mods": Mods(); return true;
            case "mods-empty": ModsEmpty(); return true;
            case "mod-browser": ModBrowser(offline: false); return true;
            case "mod-browser-offline": ModBrowser(offline: true); return true;
            case "modpacks": Modpacks(offline: false); return true;
            case "modpacks-offline": Modpacks(offline: true); return true;
            case "manual-card": ManualCard(); return true;
            case "manual-card-import": ManualCardImport(); return true;
            case "manual-card-watch": ManualCardWatch(); return true;
            case "manual-card-queued": ManualCardQueued(); return true;
            default: return false;
        }
    }

    // ── installed mods ───────────────────────────────────────────────────────

    private static void Mods()
    {
        var pack = Pack(Hearthstone);
        SeedMods(pack.Id);
        var view = new InstanceModsView(pack);
        Shell.Navigate(new ModsHost(pack, view), clearHistory: true);
        Pump(4000);
        Log("CHECK rows=" + Rows(view).Count + " (want 9)");

        // Two updates, as the update check would find them (the stores are out of reach here).
        foreach (var mod in Rows(view).Where(m => m.DisplayName is "Jade" or "Create"))
            mod.LatestVersion = Version(mod.PrimaryMod!, mod.DisplayName == "Jade" ? "15.10.2+neoforge" : "6.0.6", 1);
        Pump(800);
        Shoot("mods");

        // Filtering, and a mod switched off from its row.
        if (view.FindControl<TextBox>("SearchBox") is { } search)
        {
            search.Text = "zzz";
            Pump(500);
            Shoot("mods-filtered-none");
            search.Text = "";
            Pump(300);
        }
        var supplementaries = Rows(view).FirstOrDefault(m => m.DisplayName == "Supplementaries");
        if (supplementaries is not null)
        {
            var ok = App.State.ModInventory.SetEnabled(supplementaries, false);
            Pump(600);
            Log($"CHECK switched off {ok}, on disk {File.Exists(supplementaries.FilePath)} {supplementaries.FilePath.EndsWith(".disabled")}");
            App.State.ModInventory.SetEnabled(supplementaries, true);
            Pump(300);
        }
    }

    private static void ModsEmpty()
    {
        var pack = Pack(VanillaPlus);
        var view = new InstanceModsView(pack);
        Shell.Navigate(new ModsHost(pack, view), clearHistory: true);
        Pump(2500);
        Shoot("mods-empty");
    }

    private static List<PackMod> Rows(InstanceModsView view) =>
        Find<ListBox>(view).FirstOrDefault()?.ItemsSource?.Cast<PackMod>().ToList() ?? [];

    /// <summary>Jars in the instance's mods folder, each with the identity the fingerprint cache would
    /// hold after a real identity pass (one jar stays unknown, one is switched off).</summary>
    private static void SeedMods(Guid packId)
    {
        var dir = Path.Combine(App.State.Packs.GameDir(packId), "mods");
        Directory.CreateDirectory(dir);
        var n = 0;
        foreach (var m in FakeInstalled)
        {
            var path = Path.Combine(dir, m.Enabled ? m.File : m.File + ".disabled");
            if (!File.Exists(path))
            {
                var bytes = new byte[4096 + 977 * n++];
                new Random(m.File.GetHashCode()).NextBytes(bytes);
                File.WriteAllBytes(path, bytes);
            }
            if (m.Mod is not { } mod) continue;
            var (sha512, fingerprint) = ModFingerprintCache.ComputeHashes(path);
            App.State.ModFingerprints.Store(path, sha512, fingerprint, new CachedModMatch { Mod = mod, Version = Version(mod, m.Version, 40) });
        }
        App.State.ModFingerprints.Flush();
    }

    private sealed record Installed(string File, ModSummary? Mod, string Version, bool Enabled = true);

    private static readonly Installed[] FakeInstalled =
    [
        new("farmersdelight-1.21.1-1.2.7.jar", CF("398521", "farmers-delight", "Farmer's Delight", "vectorwing"), "1.2.7"),
        new("supplementaries-1.21-3.1.21-neoforge.jar", MR("fFEIiSDQ", "supplementaries", "Supplementaries", "MehVahdJukaar"), "1.21-3.1.21"),
        new("Jade-1.21.1-NeoForge-15.10.0.jar", MR("nvQzSEkH", "jade", "Jade", "Snownee"), "15.10.0+neoforge"),
        new("create-1.21.1-6.0.4.jar", CF("328085", "create", "Create", "simibubi"), "6.0.4"),
        new("sophisticatedbackpacks-1.21.1-3.23.4.jar", CF("422301", "sophisticated-backpacks", "Sophisticated Backpacks", "P3pp3rF1y"), "3.23.4.1197"),
        new("appleskin-neoforge-mc1.21-3.0.6.jar", MR("EsAfCjCV", "appleskin", "AppleSkin", "squeek502"), "3.0.6+mc1.21"),
        new("jei-1.21.1-neoforge-19.21.0.247.jar", CF("238222", "jei", "Just Enough Items (JEI)", "mezz"), "19.21.0.247"),
        new("AmbientSounds_NEOFORGE_v6.1.4_mc1.21.1.jar", MR("fM515JnW", "ambientsounds", "AmbientSounds", "CreativeMD"), "6.1.4", Enabled: false),
        new("lanterntweaks-0.4.jar", null, ""),
    ];

    private static ModSummary MR(string id, string slug, string name, string author, string? text = null, long downloads = 0) =>
        new(id, slug, name, author, text, downloads, IconServer.Instance.Url(name), ModSource.Modrinth, []);

    private static ModSummary CF(string id, string slug, string name, string author, string? text = null, long downloads = 0) =>
        new(id, slug, name, author, text, downloads, IconServer.Instance.Url(name), ModSource.CurseForge, []);

    /// <summary>A believable version of <paramref name="mod"/>, <paramref name="daysAgo"/> old.</summary>
    private static ModVersion Version(ModSummary mod, string number, int daysAgo, string mc = "1.21.1",
        string loader = "neoforge", string channel = "release") =>
        new(mod.Source == ModSource.CurseForge ? $"{mod.Id}:{5_000_000 + number.GetHashCode() % 900_000}" : mod.Id + "-" + number,
            number, number, [mc], [loader], channel, DateTimeOffset.UtcNow.AddDays(-daysAgo), 1000, null, mod.Source,
            [new ModVersionFile($"{mod.Slug}-{number}.jar", "", 180_000 + Math.Abs(number.GetHashCode() % 4_000_000), null, true)], []);

    /// <summary>A page that holds the Mods list the way the instance page's tab does.</summary>
    private sealed class ModsHost : UserControl, IShellPage
    {
        public ModsHost(PackDetail pack, Control body)
        {
            var title = new TextBlock { Text = pack.Name };
            title.Classes.Add("h1");
            var sub = new TextBlock { Text = $"Minecraft {pack.MinecraftVersion}  ·  {pack.Loader}  ·  Mods", Margin = new(0, 4, 0, 18) };
            sub.Classes.Add("muted");
            var header = new StackPanel { Children = { title, sub } };
            DockPanel.SetDock(header, Dock.Top);
            Content = new DockPanel { Margin = new(28, 22, 28, 18), Children = { header, body } };
        }

        public ShellNav Nav => ShellNav.Instances;
    }

    // ── the mod browser ──────────────────────────────────────────────────────

    private static void ModBrowser(bool offline)
    {
        var pack = Pack(Hearthstone);
        SeedMods(pack.Id);
        var page = offline ? new ModBrowserPage(pack) : new ModBrowserPage(pack, new FakeCatalog());
        Shell.Navigate(page, clearHistory: true);
        Pump(offline ? 9000 : 3000);
        Shoot(offline ? "mod-browser-offline" : "mod-browser");
        if (offline) return;

        // A second mod, already in the instance.
        if (Find<ListBox>(page).FirstOrDefault(l => l.Name == "ResultsList") is { } results)
        {
            var installed = results.Items.OfType<ModBrowseRow>().FirstOrDefault(r => r.IsInstalled);
            Log("CHECK installed badges=" + results.Items.OfType<ModBrowseRow>().Count(r => r.IsInstalled) + " (want 4)");
            if (installed is not null)
            {
                results.SelectedItem = installed;
                Pump(1200);
                Shoot("mod-browser-installed");
            }
        }
        ShootNarrow("mod-browser-narrow");
    }

    // ── the modpack browser ──────────────────────────────────────────────────

    private static void Modpacks(bool offline)
    {
        var page = offline ? new ModpackBrowserPage() : new ModpackBrowserPage(new FakeCatalog());
        Shell.Navigate(page, clearHistory: true);
        Pump(offline ? 9000 : 3000);
        Shoot(offline ? "modpacks-offline" : "modpacks");
        if (!offline) ShootNarrow("modpacks-narrow");
    }

    /// <summary>The page in the smallest window the launcher allows (820 x 540).</summary>
    private static void ShootNarrow(string name)
    {
        var (w, h) = (Shell.Width, Shell.Height);
        Shell.Width = 820;
        Shell.Height = 560;
        Pump(1200);
        Shoot(name);
        Shell.Width = w;
        Shell.Height = h;
        Pump(800);
    }

    // ── download by hand ─────────────────────────────────────────────────────

    /// <summary>The card as the mod browser brings it up for a mod CurseForge will not hand over: one
    /// file, then a second picked while it is open (it joins the card), then closed after Download was
    /// clicked (the launcher keeps looking in the background).</summary>
    private static void ManualCard()
    {
        var id = Hearthstone;
        ShowModsOf(id);
        var md = WatchScratchFolder();
        // An import's leftover that the picked card must not show.
        md.Record(id, "Hearthstone Valley 2.4.zip", [Jei()]);
        md.Add(id, [SkinLayers(), EntityCulling(9752280, disabled: false)]);

        _ = ManualDownloadsCard.ShowAsync(Shell, id, "Hearthstone Valley", focus: [9243656]);
        Pump(2000);
        Shoot("manual-card");
        var card = Find<ManualDownloadsCard>(Shell).FirstOrDefault();
        if (card is null) { Log("FAIL no card"); return; }
        Log("CHECK rows=" + CardRows(card).Count + " (want 1)");

        _ = ManualDownloadsCard.ShowAsync(Shell, id, "Hearthstone Valley", focus: [9752280]);
        Pump(1500);
        Log("CHECK rows=" + CardRows(card).Count + " (want 2) cards=" + Find<ManualDownloadsCard>(Shell).Count() + " (want 1)");
        if (CardRows(card).FirstOrDefault(r => r.Entry.FileId == 9243656) is { } first) first.Opened = true;
        Pump(400);
        Shoot("manual-card-two");

        Close(card);
        Log("CHECK cards after close=" + Find<ManualDownloadsCard>(Shell).Count() + " (want 0); the opened file is watched in the background");
        md.StopBackgroundWatch(id);
    }

    /// <summary>The card as a CurseForge modpack import leaves it: several files, one already added, one
    /// whose download failed for another reason; then the check before Play.</summary>
    private static void ManualCardImport()
    {
        var id = Hearthstone;
        var md = WatchScratchFolder();
        md.Record(id, "Hearthstone Valley 2.4.zip",
        [
            SkinLayers(),
            EntityCulling(9100286, disabled: true),
            new ManualDownloadEntry
            {
                ProjectId = 453763, FileId = 9254421, Name = "Fresh Animations", OptedOut = true, IconUrl = IconServer.Instance.Url("Fresh Animations"),
                FileName = "FreshAnimations_v1.11_26.1+.zip", Size = 777123, Fingerprint = 2960052495,
                Folder = "resourcepacks", ProjectUrl = "https://www.curseforge.com/minecraft/texture-packs/fresh-animations",
                State = ManualDownloadState.Added
            },
            Jei(),
        ]);
        // Fresh Animations counts as added only if it is really there.
        var rp = Path.Combine(App.State.Packs.GameDir(id), "resourcepacks");
        Directory.CreateDirectory(rp);
        File.WriteAllText(Path.Combine(rp, "FreshAnimations_v1.11_26.1+.zip"), "x");
        ShowModsOf(id);
        Shoot("mods-byhand-chip");

        _ = ManualDownloadsCard.ShowAsync(Shell, id, "Hearthstone Valley");
        Pump(2000);
        Shoot("manual-card-import");
        var card = Find<ManualDownloadsCard>(Shell).FirstOrDefault();
        if (card is null) { Log("FAIL no card"); return; }
        Log("CHECK rows=" + CardRows(card).Count + " (want 4), added=" + CardRows(card).Count(r => r.State == ManualDownloadState.Added) + " (want 1)");
        Close(card);

        _ = ManualDownloadsCard.ConfirmLaunchAsync(Shell, id, "Hearthstone Valley");
        Pump(2000);
        Shoot("manual-card-launch");
        if (Find<ManualDownloadsCard>(Shell).FirstOrDefault() is { } launch) Close(launch);
        md.Record(id, null, []);
    }

    /// <summary>A file the browser saves into the watched folder (written under a partial name, then
    /// renamed, the way browsers do) is recognised and moved into the instance while the card is open.</summary>
    private static void ManualCardWatch()
    {
        var id = Hearthstone;
        ShowModsOf(id);
        var md = WatchScratchFolder();
        var watchDir = md.WatchFolder;
        // No fingerprint or hash from CurseForge for this one, so its name and size recognise it.
        var entry = new ManualDownloadEntry
        {
            ProjectId = 900001, FileId = 9900001, Name = "Lantern Glow", OptedOut = true,
            FileName = "lanternglow-neoforge-1.2.0.jar", Size = 23456, Folder = "mods",
            ProjectUrl = "https://www.curseforge.com/minecraft/mc-mods/lantern-glow",
            IconUrl = IconServer.Instance.Url("Lantern Glow")
        };
        md.Record(id, null, []);
        md.Add(id, [entry]);
        var target = Path.Combine(App.State.Packs.GameDir(id), "mods", entry.FileName);
        if (File.Exists(target)) File.Delete(target);

        _ = ManualDownloadsCard.ShowAsync(Shell, id, "Hearthstone Valley", focus: [entry.FileId]);
        Pump(1500);
        var card = Find<ManualDownloadsCard>(Shell).FirstOrDefault();
        if (card is null) { Log("FAIL no card"); return; }
        if (CardRows(card).FirstOrDefault() is { } row) row.Opened = true;   // as after its Download click

        var partial = Path.Combine(watchDir, entry.FileName + ".part");
        var bytes = new byte[entry.Size];
        new Random(5).NextBytes(bytes);
        File.WriteAllBytes(partial, bytes);
        Pump(1200);
        Log("CHECK while partial: in instance " + File.Exists(target) + " (want False)");
        File.Move(partial, Path.Combine(watchDir, entry.FileName), overwrite: true);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(target) && DateTime.UtcNow < until) Pump(200);
        Pump(1500);
        Shoot("manual-card-watch");
        Log("CHECK after download: in instance " + File.Exists(target) + " (want True), row "
            + CardRows(card).FirstOrDefault()?.State + " (want Added), still in Downloads "
            + File.Exists(Path.Combine(watchDir, entry.FileName)) + " (want True: removing is off)");
        Close(card);
        Shoot("mods-after-watch");
        Log("CHECK list shows it: " + Find<InstanceModsView>(Shell).SelectMany(v => Rows(v)).Any(m => m.FileName == entry.FileName));
        md.Record(id, null, []);
        File.Delete(Path.Combine(watchDir, entry.FileName));
    }

    /// <summary>What the mod browser does with a file CurseForge will not hand over: the service lists it
    /// (its CurseForge lookup fails here, so the store's own listing describes it) and raises Needed, and
    /// the main window brings the card up on its own, focused on that file.</summary>
    private static void ManualCardQueued()
    {
        var id = Hearthstone;
        var pack = Pack(id);
        SeedMods(pack.Id);
        Shell.Navigate(new ModBrowserPage(pack, new FakeCatalog()), clearHistory: true);
        Pump(2500);
        var md = WatchScratchFolder();
        md.Record(id, null, []);
        var mod = CF("521480", "skin-layers-3d", "Skin Layers 3D", "tr7zw", "3D skin layers.", 52_000_000);
        var version = new ModVersion("521480:9243656", "1.11.4", "1.11.4", ["1.21.1"], ["neoforge"], "release",
            DateTimeOffset.UtcNow.AddDays(-12), 0, null, ModSource.CurseForge,
            [new ModVersionFile("skinlayers3d-neoforge-1.11.4-mc1.21.1.jar", "", 1905514, null, true)], []);
        var queued = md.QueueAsync(id, "Hearthstone Valley", App.State.CurseForge,
            [new ManualDownloadWant(mod, version, version.Files[0])]);
        var listed = Await(queued, 20000);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!Find<ManualDownloadsCard>(Shell).Any() && DateTime.UtcNow < until) Pump(100);
        Pump(1200);
        Shoot("manual-card-queued");
        var card = Find<ManualDownloadsCard>(Shell).FirstOrDefault();
        Log($"CHECK queued={listed?.Count} (want 1), card up={card is not null} (want True), rows={(card is null ? 0 : CardRows(card).Count)} (want 1)");
        if (card is not null) Close(card);
        md.Record(id, null, []);
    }

    /// <summary>The instance's Mods list on screen behind a card.</summary>
    private static void ShowModsOf(Guid id)
    {
        var pack = Pack(id);
        SeedMods(pack.Id);
        Shell.Navigate(new ModsHost(pack, new InstanceModsView(pack)), clearHistory: true);
        Pump(2500);
    }

    private static ManualDownloadService WatchScratchFolder()
    {
        var md = App.State.ManualDownloads;
        // Never the real Downloads folder: the watch would take files out of it.
        var watchDir = Path.Combine(Out, "watched-downloads");
        Directory.CreateDirectory(watchDir);
        md.SetWatchFolder(watchDir);
        md.RemoveAfterAdding = false;
        Log("watch folder " + md.WatchFolder);
        return md;
    }

    private static List<ManualDownloadRow> CardRows(ManualDownloadsCard card) =>
        card.FindControl<ItemsControl>("FileList")?.ItemsSource?.Cast<ManualDownloadRow>().ToList() ?? [];

    private static void Close(ManualDownloadsCard card)
    {
        card.FindControl<Button>("CloseButton")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(800);
    }

    private static ManualDownloadEntry SkinLayers() => new()
    {
        ProjectId = 521480, FileId = 9243656, Name = "Skin Layers 3D", OptedOut = true, IconUrl = IconServer.Instance.Url("Skin Layers 3D"),
        FileName = "skinlayers3d-neoforge-1.11.4-mc1.21.1.jar", Size = 1905514, Fingerprint = 3196735172,
        Folder = "mods", ProjectUrl = "https://www.curseforge.com/minecraft/mc-mods/skin-layers-3d"
    };

    private static ManualDownloadEntry EntityCulling(int fileId, bool disabled) => new()
    {
        ProjectId = 448233, FileId = fileId, Name = "Entity Culling Fabric/Forge", OptedOut = true, IconUrl = IconServer.Instance.Url("Entity Culling"),
        FileName = "entityculling-neoforge-1.11.4-mc1.21.1.jar", Size = 1510075, Fingerprint = 737333498,
        Folder = "mods", Disabled = disabled, ProjectUrl = "https://www.curseforge.com/minecraft/mc-mods/entityculling"
    };

    private static ManualDownloadEntry Jei() => new()
    {
        ProjectId = 238222, FileId = 9254760, Name = "Just Enough Items (JEI)", OptedOut = false, IconUrl = IconServer.Instance.Url("Just Enough Items (JEI)"),
        Reason = "CurseForge is busy right now (HTTP 503).",
        FileName = "jei-1.21.1-neoforge-19.69.0.456.jar", Size = 7350892, Fingerprint = 1,
        Folder = "mods", ProjectUrl = "https://www.curseforge.com/minecraft/mc-mods/jei"
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    private static PackDetail Pack(Guid id) => Await(App.State.Api.GetPackAsync(id), 8000)
        ?? throw new InvalidOperationException("The fake server did not answer for " + id);

    /// <summary>Runs the dispatcher until <paramref name="task"/> is done (or the time is up).</summary>
    private static T? Await<T>(Task<T> task, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!task.IsCompleted && DateTime.UtcNow < until) Pump(50);
        return task.IsCompletedSuccessfully ? task.Result : default;
    }

    /// <summary>
    /// A small web server on 127.0.0.1 that hands out made-up pixel icons for the fake projects, so the
    /// lists show pictures and the launcher's icon loader is exercised end to end without the internet.
    /// </summary>
    private sealed class IconServer
    {
        private static IconServer? _instance;
        public static IconServer Instance => _instance ??= new IconServer();

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentDictionary<string, byte[]> _pngs = new();

        private IconServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
            Log($"icon server on 127.0.0.1:{Port}");
        }

        private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>The icon's address; drawn now, on the UI thread, and served from memory.</summary>
        public string Url(string name)
        {
            var key = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            _pngs.GetOrAdd(key, _ => Draw(name));
            return $"http://127.0.0.1:{Port}/icons/{key}.png";
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var request = await reader.ReadLineAsync() ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var key = Path.GetFileNameWithoutExtension(request.Split(' ').ElementAtOrDefault(1) ?? "");
                    var found = _pngs.TryGetValue(key, out var png);
                    var head = found
                        ? $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png!.Length}\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    if (found) await stream.WriteAsync(png);
                    await stream.FlushAsync();
                }
                catch { /* the loader gave up on it */ }
            }
        }

        /// <summary>A symmetric 8x8 pixel pattern in colours of the name, 96 px square.</summary>
        private static byte[] Draw(string name)
        {
            var rng = new Random(name.Aggregate(17, (h, c) => unchecked(h * 31 + c)));
            var hue = rng.NextDouble();
            var back = Hsv(hue, 0.42, 0.24);
            var fore = Hsv(hue, 0.50, 0.88);
            var deep = Hsv((hue + 0.07) % 1, 0.65, 0.55);
            const int cells = 8, scale = 12, size = cells * scale;
            var grid = new int[cells, cells];
            for (var y = 1; y < cells - 1; y++)
                for (var x = 1; x < cells / 2; x++)
                {
                    var v = rng.Next(5) switch { 0 or 1 => 1, 2 => 2, _ => 0 };
                    grid[x, y] = v;
                    grid[cells - 1 - x, y] = v;
                }
            var pixels = new byte[size * size * 4];
            for (var py = 0; py < size; py++)
                for (var px = 0; px < size; px++)
                {
                    var c = grid[px / scale, py / scale] switch { 1 => fore, 2 => deep, _ => back };
                    var i = (py * size + px) * 4;
                    pixels[i] = c.B;
                    pixels[i + 1] = c.G;
                    pixels[i + 2] = c.R;
                    pixels[i + 3] = 255;
                }
            using var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var frame = bitmap.Lock())
                for (var row = 0; row < size; row++)
                    Marshal.Copy(pixels, row * size * 4, frame.Address + row * frame.RowBytes, size * 4);
            using var png = new MemoryStream();
            bitmap.Save(png);
            return png.ToArray();
        }

        private static (byte R, byte G, byte B) Hsv(double h, double s, double v)
        {
            var i = (int)(h * 6) % 6;
            var f = h * 6 - Math.Floor(h * 6);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            var (r, g, b) = i switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q)
            };
            return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }

    /// <summary>Store answers for the browser pages, which cannot reach the stores from here.</summary>
    private sealed class FakeCatalog : ModCatalog
    {
        private static readonly List<ModSummary> ModrinthMods =
        [
            MR("LOpKHB2A", "waystones", "Waystones", "BlayTheNinth", "Teleport between stones you find in villages and build yourself.", 41_200_000),
            MR("fFEIiSDQ", "supplementaries", "Supplementaries", "MehVahdJukaar", "Jars, signposts, clocks and a hundred small blocks that fit vanilla.", 38_500_000),
            MR("nvQzSEkH", "jade", "Jade", "Snownee", "Shows what you are looking at, with its tool, its state and its owner.", 62_000_000),
            MR("EsAfCjCV", "appleskin", "AppleSkin", "squeek502", "Food values and saturation on the hunger bar.", 75_300_000),
            MR("uXXizFIs", "ferrite-core", "FerriteCore", "malte0811", "Uses less memory, with nothing to set up.", 88_900_000),
            MR("xGdtZczs", "comforts", "Comforts", "TheIllusiveC4", "Sleeping bags and hammocks that skip the night or the day.", 9_400_000),
        ];

        private static readonly List<ModSummary> CurseMods =
        [
            CF("317780", "waystones", "Waystones", "BlayTheNinth", "Teleport between stones you find in villages and build yourself.", 180_000_000),
            CF("398521", "farmers-delight", "Farmer's Delight", "vectorwing", "Farming and cooking, with cutting boards, stoves and many meals.", 95_000_000),
            CF("351725", "macaws-bridges", "Macaw's Bridges", "sketch_macaw", "Rope, log and stone bridges that look hand built.", 61_000_000),
            CF("263420", "xaeros-minimap", "Xaero's Minimap", "xaero96", "A minimap that keeps the vanilla look, with waypoints.", 120_000_000),
        ];

        private static readonly List<ModSummary> Packs =
        [
            CF("910001", "lantern-valley", "Lantern Valley", "mara", "Slow, cosy survival with farming, cooking and lanterns everywhere.", 2_450_000),
            CF("910002", "skyward-engineering", "Skyward Engineering", "jun", "Trains, cranes and floating islands to build on.", 1_120_000),
            CF("910003", "hollow-depths", "Hollow Depths", "tobi", "A cave expedition pack with deep dungeons and little light.", 860_000),
            CF("910004", "copper-and-steam", "Copper & Steam", "ivy", "Machines, pipes and a sky full of smoke.", 640_000),
            CF("910005", "seaside-cottage", "Seaside Cottage", "sol", "Fishing, boats and a small house by the water.", 310_000),
            CF("910006", "starfall-expedition", "Starfall Expedition", "mara", "Rockets, moons and the long trip there.", 98_000),
        ];

        public override Task<List<ModSummary>> SearchAsync(ModSource store, string query, string? mc, string? loader,
            ModSort sort, int offset, int limit, bool modpacks, CancellationToken ct)
        {
            var source = modpacks
                ? store == ModSource.CurseForge ? Packs : Packs.Select(p => p with { Source = ModSource.Modrinth, Id = "mr" + p.Id }).Take(3).ToList()
                : store == ModSource.CurseForge ? CurseMods : ModrinthMods;
            var hits = source.Where(m => query.Length == 0 || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (sort == ModSort.Downloads) hits = hits.OrderByDescending(m => m.DownloadCount);
            return Task.FromResult(hits.Skip(offset).Take(limit).ToList());
        }

        public override Task<IReadOnlyList<ModVersion>> VersionsAsync(ModSummary mod, string? mc, string? loader, CancellationToken ct)
        {
            IReadOnlyList<ModVersion> all =
            [
                Version(mod, "21.1.12", 3),
                Version(mod, "21.1.11-beta", 9, channel: "beta"),
                Version(mod, "21.1.10", 21),
                Version(mod, "21.1.9", 44),
                Version(mod, "20.1.18", 30, mc: "1.20.1", loader: "forge"),
                Version(mod, "20.1.17", 70, mc: "1.20.1", loader: "forge"),
            ];
            return Task.FromResult<IReadOnlyList<ModVersion>>(all
                .Where(v => (mc is null || v.GameVersions.Contains(mc)) && (loader is null || v.Loaders.Contains(loader)))
                .ToList());
        }

        public override Task<ModProjectDetail> DetailAsync(ModSummary mod, CancellationToken ct) =>
            Task.FromResult(new ModProjectDetail(
                $"## {mod.Name}\n\n{mod.Description}\n\n**What it adds**\n\n- Things to build and find\n- Settings in the config file\n- Works on servers too\n\n" +
                "Questions and bug reports go to the project's issue tracker.",
                new ModProjectLinks(null, null, null, null, null), [], IsMarkdown: true));

        public override Task<ModSummary?> CounterpartAsync(ModSummary mod, CancellationToken ct) =>
            Task.FromResult(mod.Slug == "waystones"
                ? (mod.Source == ModSource.Modrinth ? CurseMods[0] : ModrinthMods[0])
                : null);
    }
}
