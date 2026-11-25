using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using CloudLauncher.Views;
using static LinuxShots.Program;

namespace LinuxShots;

/// <summary>Scenarios for the account page, Settings and the updater:
/// "account" (signed in, no Minecraft account), "account-signed-out" (run with -SignedOut),
/// "account-list" (two made-up Minecraft accounts, in memory only), "settings" (the whole page, shot in
/// parts), "settings-whatsnew" and "settings-update" (the cards, fed by a small local feed),
/// "settings-key" (Mod Stores with a key of your own).</summary>
public static class ShotsAccount
{
    /// <summary>Runs scenario <paramref name="name"/> if it is one of this file's; false otherwise.</summary>
    public static bool TryRun(string name)
    {
        switch (name)
        {
            case "account":
            case "account-signed-out":
                Shell.Navigate(new AccountPage(), clearHistory: true);
                Pump(1500);
                Shoot(name);
                return true;

            case "account-list":
                AccountList();
                return true;

            case "settings":
                Settings();
                return true;

            case "settings-whatsnew":
                WithFeed(() =>
                {
                    Shell.Navigate(new SettingsPage(), clearHistory: true);
                    Pump(600);
                    _ = SettingsChangelogCard.ShowAsync(Shell);
                    Pump(1500);
                    Shoot("settings-whatsnew");
                    CloseCard();
                });
                return true;

            case "settings-update":
                SettingsUpdate();
                return true;

            case "settings-key":
                SettingsKey();
                return true;

            case "updater-check":
                UpdaterCheck();
                return true;

            case "narrow":
                // Both pages at the window's smallest size.
                var (width, height) = (Shell.Width, Shell.Height);
                Shell.Width = Shell.MinWidth;
                Shell.Height = Shell.MinHeight;
                try
                {
                    Shell.Navigate(new AccountPage(), clearHistory: true);
                    Pump(1500);
                    Shoot("narrow-account");
                    var page = new SettingsPage();
                    Shell.Navigate(page, clearHistory: true);
                    Pump(1500);
                    Shoot("narrow-settings-1");
                    ScrollToEnd(page);
                    Shoot("narrow-settings-2");
                }
                finally
                {
                    Shell.Width = width;
                    Shell.Height = height;
                    Pump(300);
                }
                return true;

            default:
                return false;
        }
    }

    // ── account ──────────────────────────────────────────────────────────────

    /// <summary>The Minecraft list with two accounts in it. The service only adds accounts after a real
    /// Microsoft sign-in, so they go into its in-memory list for the shot (never saved) and come out
    /// again after.</summary>
    private static void AccountList()
    {
        var service = App.State.MinecraftAccounts;
        var list = (List<StoredMinecraftAccount>)typeof(MinecraftAccountService)
            .GetField("_accounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        var currentField = typeof(MinecraftAccountService).GetField("_currentId", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var before = list.ToList();
        var beforeCurrent = currentField.GetValue(service);
        try
        {
            var ms = new StoredMinecraftAccount { Kind = MinecraftAccountKind.Microsoft, Username = "MaraBuilds", Uuid = "5f0c3e7a-1b2d-4c8e-9a6f-2d1e0b9c8a71" };
            var offline = new StoredMinecraftAccount { Kind = MinecraftAccountKind.Offline, Username = "mara_lan" };
            list.Add(ms);
            list.Add(offline);
            currentField.SetValue(service, (Guid?)ms.Id);
            Shell.Navigate(new AccountPage(), clearHistory: true);
            Pump(1500);
            Shoot("account-list");
        }
        finally
        {
            list.Clear();
            list.AddRange(before);
            currentField.SetValue(service, beforeCurrent);
        }
    }

    // ── settings ─────────────────────────────────────────────────────────────

    /// <summary>The whole page, one window-full at a time from the top.</summary>
    private static void Settings()
    {
        var page = new SettingsPage();
        Shell.Navigate(page, clearHistory: true);
        // Java detection asks each Java it finds for its version.
        Pump(6000);
        var scroller = page.FindControl<ScrollViewer>("Scroller")!;
        if (page.FindControl<ComboBox>("JavaBox") is { } java)
            Log("settings: java choices " + string.Join(" | ", java.Items.Cast<object?>()));
        var step = Math.Max(200, scroller.Viewport.Height - 80);
        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        Log($"settings: extent {scroller.Extent.Height:0}, viewport {scroller.Viewport.Height:0}");
        var part = 1;
        for (double y = 0; ; y += step)
        {
            scroller.Offset = new Vector(0, Math.Min(y, max));
            Pump(400);
            Shoot($"settings-{part++}");
            if (y >= max) break;
        }
    }

    /// <summary>The update card twice: for a release that verifies (drawn as it would be, nothing is
    /// downloaded) and, through Check for Updates against the local feed, for one that does not.</summary>
    private static void SettingsUpdate()
    {
        var release = FeedReleases()[0];
        Shell.Navigate(new SettingsPage(), clearHistory: true);
        Pump(600);
        var valid = new UpdateVerification(new UpdateManifest(release.Version, release.Sha256, release.Size), null);
        _ = SettingsUpdateCard.ShowAsync(Shell, release, valid);
        Pump(800);
        Shoot("settings-update");
        CloseCard();

        WithFeed(() =>
        {
            var page = new SettingsPage();
            Shell.Navigate(page, clearHistory: true);
            Pump(600);
            var check = page.FindControl<Button>("CheckUpdatesButton")!;
            check.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump(1500);
            Shoot("settings-update-unverified");
            CloseCard();
            ScrollToEnd(page);
            Shoot("settings-update-status");
        });
    }

    /// <summary>Mod Stores with a key of your own typed in (a made-up one, in this scratch profile
    /// only; cleared again after).</summary>
    private static void SettingsKey()
    {
        var page = new SettingsPage();
        Shell.Navigate(page, clearHistory: true);
        Pump(600);
        var box = page.FindControl<TextBox>("CurseForgeKeyBox")!;
        box.Text = "not-a-real-key-0123456789abcdef";
        Pump(300);
        ScrollToEnd(page);
        Shoot("settings-key");
        box.Text = "";
        Pump(300);
    }

    private static void ScrollToEnd(SettingsPage page)
    {
        var scroller = page.FindControl<ScrollViewer>("Scroller")!;
        scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
        Pump(400);
    }

    private static void CloseCard()
    {
        // Escape closes the top card, as it does for the user.
        Shell.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.Escape
        });
        Pump(300);
    }

    // ── the updater's checks, without a picture ─────────────────────────────

    /// <summary>
    /// Runs the parts of the updater that work on any system against made-up packages and logs
    /// PASS/FAIL per case: unpacking (a good package, a path out of the folder, a link, two top folders),
    /// and the download's size and SHA-256 checks against a local feed. Also logs the desktop entry for
    /// an awkward path, to compare with install.sh's.
    /// </summary>
    private static void UpdaterCheck()
    {
        var root = Path.Combine(Out, "_updater-check");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var extract = typeof(LinuxUpdater).GetMethod("Extract", BindingFlags.NonPublic | BindingFlags.Static)!;

        void Expect(string name, Func<(System.Formats.Tar.TarEntryType Type, string Name, string? Link)[]> entries, bool shouldPass)
        {
            var package = Path.Combine(root, name + ".tar.gz");
            using (var file = File.Create(package))
            using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
            using (var tar = new System.Formats.Tar.TarWriter(gz, System.Formats.Tar.TarEntryFormat.Pax))
            {
                foreach (var (type, entryName, link) in entries())
                {
                    var entry = new System.Formats.Tar.PaxTarEntry(type, entryName);
                    if (link is not null) entry.LinkName = link;
                    if (type == System.Formats.Tar.TarEntryType.RegularFile)
                        entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("content of " + entryName));
                    tar.WriteEntry(entry);
                }
            }
            var staging = Path.Combine(root, name);
            string outcome;
            try
            {
                var folder = (string)extract.Invoke(null, [package, staging, CancellationToken.None])!;
                outcome = "unpacked to " + Path.GetFileName(folder);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is PlatformNotSupportedException)
            {
                // Everything is unpacked and checked by then; only the Unix file modes are left.
                outcome = "unpacked (file modes are Unix-only)";
            }
            catch (TargetInvocationException ex)
            {
                outcome = "refused: " + ((ex.InnerException as UpdateVerificationException)?.Reason ?? ex.InnerException?.Message);
            }
            var passed = outcome.StartsWith("unpacked", StringComparison.Ordinal) == shouldPass;
            var escaped = File.Exists(Path.Combine(root, "evil.txt")) || File.Exists(Path.Combine(Out, "evil.txt"));
            Log($"{(passed && !escaped ? "PASS" : "FAIL")} extract {name}: {outcome}{(escaped ? " (A FILE GOT OUT)" : "")}");
        }

        const System.Formats.Tar.TarEntryType Dir = System.Formats.Tar.TarEntryType.Directory;
        const System.Formats.Tar.TarEntryType FileEntry = System.Formats.Tar.TarEntryType.RegularFile;
        Expect("good", () =>
        [
            (Dir, "cloudlauncher/", null), (FileEntry, "cloudlauncher/cloudlauncher", null),
            (FileEntry, "cloudlauncher/install.sh", null), (FileEntry, "cloudlauncher/README.txt", null)
        ], shouldPass: true);
        Expect("dot-slash", () =>
        [
            (Dir, "./cloudlauncher/", null), (FileEntry, "./cloudlauncher/cloudlauncher", null)
        ], shouldPass: true);
        Expect("traversal", () =>
        [
            (Dir, "cloudlauncher/", null), (FileEntry, "cloudlauncher/cloudlauncher", null),
            (FileEntry, "cloudlauncher/../../evil.txt", null)
        ], shouldPass: false);
        Expect("absolute", () =>
        [
            (FileEntry, "/tmp/evil.txt", null)
        ], shouldPass: false);
        Expect("symlink", () =>
        [
            (Dir, "cloudlauncher/", null), (FileEntry, "cloudlauncher/cloudlauncher", null),
            (System.Formats.Tar.TarEntryType.SymbolicLink, "cloudlauncher/link", "/etc/passwd")
        ], shouldPass: false);
        Expect("two-tops", () =>
        [
            (FileEntry, "cloudlauncher/cloudlauncher", null), (FileEntry, "other/cloudlauncher", null)
        ], shouldPass: false);
        Expect("no-program", () =>
        [
            (Dir, "cloudlauncher/", null), (FileEntry, "cloudlauncher/README.txt", null)
        ], shouldPass: false);

        // The download against its signed size and hash.
        var download = typeof(LinuxUpdater).GetMethod("DownloadVerifiedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var payload = Encoding.UTF8.GetBytes(new string('x', 200_000));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        _download = payload;
        WithFeed(() =>
        {
            void Try(string name, UpdateManifest manifest, bool shouldPass)
            {
                var target = Path.Combine(root, name + ".bin");
                string outcome;
                try
                {
                    // On the pool: blocking the UI thread on a task that resumes there would never end.
                    Task.Run(() => (Task)download.Invoke(null, [manifest, target, null, CancellationToken.None])!)
                        .GetAwaiter().GetResult();
                    outcome = "accepted";
                }
                catch (UpdateVerificationException ex) { outcome = "refused: " + ex.Reason; }
                catch (Exception ex) { outcome = "failed: " + ex.Message; }
                Log($"{((outcome == "accepted") == shouldPass ? "PASS" : "FAIL")} download {name}: {outcome}");
            }
            Try("matching", new UpdateManifest("9.9.9", sha, payload.Length), shouldPass: true);
            Try("wrong-hash", new UpdateManifest("9.9.9", new string('0', 64), payload.Length), shouldPass: false);
            Try("smaller-signed", new UpdateManifest("9.9.9", sha, payload.Length - 1), shouldPass: false);
            Try("larger-signed", new UpdateManifest("9.9.9", sha, payload.Length + 1), shouldPass: false);
        });
        _download = null;

        Log("desktop entry:\n" + DesktopIntegration.BuildEntry("/home/mara/My Games/a$b \"c\" `d` back\\slash 100%/cloudlauncher"));
    }

    /// <summary>What the local feed answers to <c>launcher/linux/download</c>, when set.</summary>
    private static byte[]? _download;

    // ── a local release feed ─────────────────────────────────────────────────

    private static IReadOnlyList<LauncherReleaseInfo> FeedReleases()
    {
        var now = DateTimeOffset.UtcNow;
        var next = new Version(AppVersion.Current.Major, AppVersion.Current.Minor, Math.Max(0, AppVersion.Current.Build) + 1);
        return
        [
            new(next.ToString(3), $"CloudLauncher-{next.ToString(3)}-linux-x64.tar.gz", 98_765_432, new string('a', 64),
                "Instances you play together now show who is online.\nThe mod list opens faster in big packs.\nFixed: a modpack import could stop at 99%.",
                now.AddDays(-1)),
            new(AppVersion.CurrentString, $"CloudLauncher-{AppVersion.CurrentString}-linux-x64.tar.gz", 98_000_000, new string('b', 64),
                "The first Linux release: sync, launching, mods and modpacks, with Microsoft sign-in by code.",
                now.AddDays(-9)),
            new("0.9.18", "CloudLauncher-0.9.18-linux-x64.tar.gz", 97_000_000, new string('c', 64),
                "Drawing on a big planning board no longer lags.", now.AddDays(-20)),
        ];
    }

    /// <summary>Runs <paramref name="body"/> with the launcher's server address pointed (in memory only)
    /// at a small feed on this machine that serves <see cref="FeedReleases"/> the way the server's Linux
    /// routes do. The address is put back after; nothing is saved.</summary>
    private static void WithFeed(Action body)
    {
        // A plain socket answering one request per connection: enough for HttpClient, and no http.sys
        // reservation needed.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { return; }
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var requestLine = await reader.ReadLineAsync() ?? "";
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { /* headers */ }
                        var path = requestLine.Split(' ') is { Length: >= 2 } parts ? parts[1].Split('?')[0].TrimEnd('/') : "";
                        object? answer = path switch
                        {
                            "/launcher/linux/releases" => FeedReleases(),
                            "/launcher/linux/latest" => FeedReleases()[0],
                            "/launcher/linux/download" => _download,
                            _ => null
                        };
                        var bytes = answer switch
                        {
                            null => Array.Empty<byte>(),
                            byte[] raw => raw,
                            _ => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer, json))
                        };
                        var type = answer is byte[] ? "application/gzip" : "application/json";
                        var head = (answer is null ? "HTTP/1.1 404 Not Found\r\n" : $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\n")
                                   + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                        await stream.WriteAsync(bytes);
                        await stream.FlushAsync();
                    }
                    catch (Exception ex) { Log("feed: " + ex.Message); }
                }
            }
        });

        var settings = App.State.Settings;
        var server = settings.ServerUrl;
        settings.ServerUrl = $"http://127.0.0.1:{port}";
        try { body(); }
        finally
        {
            settings.ServerUrl = server;
            listener.Stop();
            try { serving.Wait(2000); } catch { /* stopped */ }
        }
    }
}
