using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CloudLauncher.Server.Net;
using CloudLauncher.Services;

namespace CloudLauncher.ProxyCheck;

/// <summary>One request the fake launcher server saw.</summary>
internal sealed record Seen(string Method, string Platform, string Path, string Query, string Body);

/// <summary>One scripted answer: status, body and headers, consumed in order per path.</summary>
internal sealed record Answer(int Status, string Body, Dictionary<string, string>? Headers = null);

internal static class Program
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  | " + detail : "")}");
    }

    private static async Task<int> Main()
    {
        // Own profile, so nothing here reads or writes the real launcher's settings, secrets or caches.
        // Set before any launcher type is touched: AppSettings fixes its data folder when first used.
        var profile = "proxy-check-" + Environment.ProcessId;
        Environment.SetEnvironmentVariable("CL_PROFILE", profile);
        var profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudLauncher", profile);

        using var server = new FakeServer();
        await server.StartAsync();
        try
        {
            var settings = new AppSettings { ServerUrl = server.BaseUrl, AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
            settings.SetSessionTokens("proxy-check-access-token", "proxy-check-refresh-token");
            var api = new ApiClient(settings);
            var curseForge = new CurseForgeService(api);
            var modrinth = new ModrinthService(api);
            var waits = new ConcurrentQueue<StoreWait>();
            api.StoreWaiting += waits.Enqueue;

            await CheckAllowlistAsync(server, api, curseForge, modrinth);
            await CheckLimitsAsync();
            await CheckRetriesAsync(server, api, curseForge, modrinth, waits);
        }
        finally
        {
            await server.StopAsync();
            try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true); } catch { /* best effort */ }
        }

        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ── 1. every client path through the allowlist ────────────────────────

    /// <summary>The allowlist's entries, so the check also fails when the launcher stopped exercising
    /// one (a method removed or rerouted) rather than only when it built a path the server refuses.</summary>
    private static readonly (string Platform, string Method, string Pattern)[] AllowlistShapes =
    [
        ("curseforge", "GET", @"^mods/search$"),
        ("curseforge", "GET", @"^categories$"),
        ("curseforge", "GET", @"^mods/\d+$"),
        ("curseforge", "GET", @"^mods/\d+/description$"),
        ("curseforge", "GET", @"^mods/\d+/files$"),
        ("curseforge", "GET", @"^mods/\d+/files/\d+$"),
        ("curseforge", "GET", @"^mods/\d+/files/\d+/changelog$"),
        ("curseforge", "GET", @"^mods/\d+/files/\d+/download-url$"),
        ("curseforge", "POST", @"^mods$"),
        ("curseforge", "POST", @"^mods/files$"),
        ("curseforge", "POST", @"^fingerprints$"),
        ("modrinth", "GET", @"^search$"),
        ("modrinth", "GET", @"^tag/category$"),
        ("modrinth", "GET", @"^project/[A-Za-z0-9_-]+$"),
        ("modrinth", "GET", @"^project/[A-Za-z0-9_-]+/version$"),
        ("modrinth", "GET", @"^version/[A-Za-z0-9_-]+$"),
        ("modrinth", "GET", @"^projects$"),
        ("modrinth", "POST", @"^version_files$"),
        ("modrinth", "POST", @"^version_files/update$"),
    ];

    private static async Task CheckAllowlistAsync(FakeServer server, ApiClient api, CurseForgeService cf, ModrinthService mr)
    {
        Console.WriteLine("-- allowlist: every path the launcher builds --");
        server.Seen.Clear();
        var failed = new List<string>();
        async Task Call(string what, Func<Task> call)
        {
            try { await call(); }
            catch (Exception ex) { failed.Add($"{what}: {ex.Message}"); }
        }

        // CurseForge: every public method, with every filter and class the pages use.
        foreach (var classId in new[] { CurseForgeService.ClassIdMods, CurseForgeService.ClassIdResourcePacks,
                     CurseForgeService.ClassIdModpacks, CurseForgeService.ClassIdWorlds, CurseForgeService.ClassIdShaders })
        {
            await Call("search", () => cf.SearchAsync("jei", "1.21.1", "neoforge", limit: 20, offset: 40, classId: classId, sortField: 6, categoryIds: [5, 6]));
            await Call("search", () => cf.SearchAsync("", null, null, classId: classId, categoryIds: [5]));
            await Call("categories", () => cf.GetCategoriesAsync(classId));
        }
        await Call("mod", () => cf.GetModAsync(238222));
        await Call("description", () => cf.GetDescriptionAsync(238222));
        await Call("project detail", () => cf.GetProjectDetailAsync(238222));
        await Call("versions", () => cf.GetVersionsAsync(238222));
        await Call("versions (filtered)", () => cf.GetVersionsAsync(238222, "1.21.1", "neoforge", maxPages: 1, pacing: ProxyPacing.UpdateCheck));
        await Call("version", () => cf.GetVersionAsync(238222, 5000001));
        await Call("changelog", () => cf.GetChangelogAsync(238222, 5000001));
        await Call("mods", () => cf.GetModsAsync([238222, 238223]));
        await Call("project facts", () => cf.GetProjectFactsAsync([238222, 238223]));
        await Call("files", () => cf.GetFilesAsync([5000001, 5000002]));
        await Call("file indexes", () => cf.GetLatestFileIndexesAsync([238222, 238223]));
        await Call("fingerprints", () => cf.MatchFingerprintsAsync([123456789L, 987654321L]));
        await Call("slug search", () => cf.SearchBySlugAsync("jei"));
        await Call("counterpart", () => cf.FindCounterpartAsync("jei", "Just Enough Items"));
        await Call("download url", () => cf.GetDownloadUrlAsync(238222, 5000001));
        // Settings > Mod stores, "Test key".
        await Call("key test", async () =>
        {
            using var resp = await api.ProxyAsync("curseforge", HttpMethod.Get, "mods/search?gameId=432&classId=6&pageSize=1&searchFilter=cl-key-test-abc");
        });

        // Modrinth: every public method, with every project type and filter the pages use.
        foreach (var type in new[] { "mod", "modpack", "resourcepack", "shader" })
        {
            await Call("search", () => mr.SearchAsync("sodium", "1.21.1", "fabric", ["optimization", "utility"], limit: 30, offset: 30, projectType: type, index: "downloads"));
            await Call("search", () => mr.SearchAsync("", null, null, projectType: type));
        }
        await Call("categories", () => mr.GetCategoriesAsync("mod"));
        await Call("project", () => mr.GetProjectAsync("AANobbMI"));
        await Call("description", () => mr.GetDescriptionAsync("sodium"));
        await Call("project detail", () => mr.GetProjectDetailAsync("AANobbMI"));
        await Call("versions", () => mr.GetVersionsAsync("AANobbMI"));
        await Call("versions (filtered)", () => mr.GetVersionsAsync("AANobbMI", "1.21.1", "fabric"));
        await Call("versions (strict)", () => mr.GetVersionsStrictAsync("AANobbMI", "1.21.1", "fabric", pacing: ProxyPacing.UpdateCheck));
        await Call("update lookup", () => mr.GetLatestVersionsByHashAsync([new string('a', 128)], "1.21.1", "fabric", ["release", "beta"]));
        await Call("version changelog", () => mr.GetVersionChangelogAsync("AbCdEf12"));
        await Call("projects", () => mr.GetProjectsAsync(["AANobbMI", "P7dR8mSH"]));
        await Call("version with project", () => mr.GetVersionWithProjectAsync("AbCdEf12"));
        await Call("hash match", () => mr.MatchHashesAsync([new string('a', 128), new string('b', 128)]));
        await Call("counterpart", () => mr.FindCounterpartAsync("jei", "Just Enough Items"));
        // A CurseForge summary without a slug carries the mod's name in the slug slot; the name search
        // must be used instead of a path the server would refuse.
        await Call("counterpart by name", () => mr.FindCounterpartAsync("Just Enough Items (JEI)", "Just Enough Items (JEI)"));

        Check("every store call against the fake server succeeded", failed.Count == 0, string.Join("; ", failed));
        var seen = server.Seen.ToArray();
        Check("the launcher made store calls", seen.Length > 40, $"{seen.Length} calls");

        var refused = new List<string>();
        var tooLong = new List<string>();
        foreach (var s in seen)
        {
            var platform = ProxyAllowlist.Platform(s.Platform);
            if (platform is null || !ProxyAllowlist.Allows(platform, s.Method, s.Path))
                refused.Add($"{s.Method} {s.Platform}/{s.Path}");
            if (s.Query.Length > ProxyAllowlist.MaxQueryLength)
                tooLong.Add($"{s.Method} {s.Platform}/{s.Path}{s.Query}");
        }
        Check("every path the launcher built is allowed", refused.Count == 0, string.Join("; ", refused.Distinct()));
        Check("every query string is under the server's limit", tooLong.Count == 0, string.Join("; ", tooLong));
        Check("no path carries a percent-encoded or odd character",
            seen.All(s => ProxyAllowlist.IsCleanPath(s.Path)),
            string.Join("; ", seen.Where(s => !ProxyAllowlist.IsCleanPath(s.Path)).Select(s => s.Path).Distinct()));
        Check("no path was built from a mod name",
            !seen.Any(s => s.Path.Contains("Just", StringComparison.Ordinal)),
            string.Join("; ", seen.Where(s => s.Path.Contains("Just", StringComparison.Ordinal)).Select(s => s.Path)));

        var unused = AllowlistShapes
            .Where(shape => !seen.Any(s => s.Platform == shape.Platform && s.Method == shape.Method && Regex.IsMatch(s.Path, shape.Pattern)))
            .Select(shape => $"{shape.Method} {shape.Platform}/{shape.Pattern}")
            .ToList();
        Check("every allowlist entry is exercised by the launcher", unused.Count == 0, string.Join("; ", unused));

        var distinct = seen.Select(s => $"{s.Method} {s.Platform}/{s.Path}").Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"      {distinct.Count} distinct shapes: {string.Join(", ", distinct)}");
    }

    // ── 2. the per-account limits at their numbers ─────────────────────────

    private static async Task CheckLimitsAsync()
    {
        Console.WriteLine("-- per-account limits --");
        var limits = new ProxyUserLimits();
        var user = Guid.NewGuid();

        var burst = 0;
        while (limits.TryTake(user) is null && burst < 10_000) burst++;
        Check($"a quiet account may send {ProxyUserLimits.Burst:0} at once", burst == (int)ProxyUserLimits.Burst, $"{burst}");
        var wait = limits.TryTake(user);
        Check("the next one is told to wait under a second", wait is { } w && w > TimeSpan.Zero && w < TimeSpan.FromSeconds(1), $"{wait}");

        await Task.Delay(1050);
        var refilled = 0;
        while (limits.TryTake(user) is null && refilled < 10_000) refilled++;
        Check($"about {ProxyUserLimits.RequestsPerSecond:0} refill each second", refilled >= ProxyUserLimits.RequestsPerSecond - 2 && refilled <= ProxyUserLimits.RequestsPerSecond + 6, $"{refilled}");

        var places = 0;
        while (limits.TryEnterQueue(user, "curseforge") is null && places < 1000) places++;
        Check($"an account holds {ProxyUserLimits.MaxQueuedPerBucket} places in a store's queue", places == ProxyUserLimits.MaxQueuedPerBucket, $"{places}");
        Check("the other store's queue is separate", limits.TryEnterQueue(user, "modrinth") is null);
        limits.LeaveQueue(user, "curseforge");
        Check("a place given back can be taken again", limits.TryEnterQueue(user, "curseforge") is null);
        Check("the launcher's own in-flight ceiling fits under the queue places (3 pages + 8 update check)",
            3 + 8 <= ProxyUserLimits.MaxQueuedPerBucket);
        // Browsing is about 6/s to CurseForge and 4.5/s to Modrinth; the update-check rate is one
        // budget across both stores.
        Check("an update check at the top rate plus browsing stays under the sustained rate",
            AppSettings.MaxModUpdateChecksPerSecond + 11 <= ProxyUserLimits.RequestsPerSecond,
            $"{AppSettings.MaxModUpdateChecksPerSecond + 11} of {ProxyUserLimits.RequestsPerSecond}");
    }

    // ── 3. wait-and-retry ─────────────────────────────────────────────────

    private static async Task CheckRetriesAsync(FakeServer server, ApiClient api, CurseForgeService cf, ModrinthService mr,
        ConcurrentQueue<StoreWait> waits)
    {
        Console.WriteLine("-- wait and retry --");
        static Dictionary<string, string> RetryAfter(int seconds) => new() { ["Retry-After"] = seconds.ToString() };
        static string ServerError(string code, string message) => $"{{\"error\":\"{message}\",\"code\":\"{code}\"}}";
        int Calls(string method, string path) => server.Seen.Count(s => s.Method == method && s.Path == path);

        // 429 with Retry-After from the launcher server, twice, then the answer.
        waits.Clear();
        server.Script("GET", "mods/1001",
            new Answer(429, ServerError("too_many_requests", "Too many store requests from this account."), RetryAfter(1)),
            new Answer(429, ServerError("upstream_rate_limited", "CurseForge is rate-limiting the launcher server."), RetryAfter(1)));
        var clock = Stopwatch.StartNew();
        var mod = await cf.GetModAsync(1001);
        Check("429 x2 then 200: the answer arrives, no error", mod is not null && Calls("GET", "mods/1001") == 3, $"{Calls("GET", "mods/1001")} calls");
        Check("each wait honoured Retry-After (1 s), not the 2 s default", clock.Elapsed >= TimeSpan.FromSeconds(2) && clock.Elapsed < TimeSpan.FromSeconds(3.5), $"{clock.Elapsed.TotalSeconds:0.0}s");
        Check("the page was told twice that CurseForge is busy", waits.Count == 2 && waits.All(w => w.Store == "CurseForge" && w.Describe().Contains("busy")),
            string.Join(", ", waits.Select(w => w.Describe())));

        // The launcher server's database restarting: 503 db_unavailable with Retry-After, then fine.
        server.Script("POST", "version_files",
            new Answer(503, ServerError("db_unavailable", "The launcher server's database is restarting."), RetryAfter(1)));
        var before = Calls("POST", "version_files");
        var matches = await mr.MatchHashesAsync([new string('c', 128)]);
        Check("503 db_unavailable then 200: the hash match completes", matches.Count == 1 && Calls("POST", "version_files") - before == 2,
            $"{matches.Count} matches, {Calls("POST", "version_files") - before} calls");

        // nginx while the service restarts: 502 with an HTML page, then fine.
        server.Script("GET", "mods/1002", new Answer(502, "<html><body><h1>502 Bad Gateway</h1><hr>nginx</body></html>",
            new Dictionary<string, string> { ["Content-Type"] = "text/html" }));
        mod = await cf.GetModAsync(1002);
        Check("502 from nginx then 200: retried", mod is not null && Calls("GET", "mods/1002") == 2, $"{Calls("GET", "mods/1002")} calls");

        // 504, then fine.
        server.Script("GET", "project/gateway", new Answer(504, ""));
        var project = await mr.GetProjectAsync("gateway");
        Check("504 then 200: retried", project is not null && Calls("GET", "project/gateway") == 2, $"{Calls("GET", "project/gateway")} calls");

        // No key configured on the server: not something a wait fixes.
        server.Script("GET", "mods/1003/files", new Answer(503, ServerError("key_not_configured", "CurseForge API key not configured. An admin must set it in the dev menu.")));
        var ex = await Throws(() => cf.GetVersionsAsync(1003));
        Check("503 key_not_configured is not retried", Calls("GET", "mods/1003/files") == 1, $"{Calls("GET", "mods/1003/files")} calls");
        Check("and names the server's key setup, not a rejected key",
            ex is StoreRequestException { Failure: StoreFailure.KeyMissing } s1 && !s1.Plain.Contains("rejected", StringComparison.OrdinalIgnoreCase), Describe(ex));

        // CurseForge's throttling 403 on the shared key, passed through bare: retried, then fine.
        server.Script("GET", "mods/1004", new Answer(403, ""), new Answer(403, ""));
        mod = await cf.GetModAsync(1004);
        Check("bare 403 x2 then 200: retried", mod is not null && Calls("GET", "mods/1004") == 3, $"{Calls("GET", "mods/1004")} calls");

        // A 403 that never clears: a calm sentence after the retries, and never "API key".
        server.Script("GET", "mods/1005/files", Enumerable.Repeat(new Answer(403, ""), 6).ToArray());
        ex = await Throws(() => cf.GetVersionsAsync(1005));
        Check("persistent 403: gives up after three retries", Calls("GET", "mods/1005/files") == 4, $"{Calls("GET", "mods/1005/files")} calls");
        Check("persistent 403: calm sentence, no API key blamed",
            ex is StoreRequestException { Failure: StoreFailure.Refused } s2
            && s2.Plain.StartsWith("CurseForge is refusing", StringComparison.Ordinal)
            && !s2.Message.Contains("api key", StringComparison.OrdinalIgnoreCase), Describe(ex));

        // A 401 on the shared key, passed through: same wording rule.
        server.Script("GET", "mods/1006/files", new Answer(401, "{\"message\":\"Invalid API key\"}"));
        ex = await Throws(() => cf.GetVersionsAsync(1006));
        Check("shared-key 401: never says API key",
            ex is StoreRequestException { Failure: StoreFailure.Refused } s3 && !s3.Message.Contains("api key", StringComparison.OrdinalIgnoreCase)
            && Calls("GET", "mods/1006/files") == 1, Describe(ex));

        // The user's own key rejected: the only answer allowed to name an API key, and never retried.
        server.Script("GET", "mods/1007/files",
            new Answer(400, ServerError("own_key_rejected", "CurseForge rejected the API key you set in Settings > Mod stores. Check it, or clear the box to go back to the launcher's shared key.")));
        ex = await Throws(() => cf.GetVersionsAsync(1007));
        Check("own_key_rejected: not retried, names the key the user set",
            ex is StoreRequestException { Failure: StoreFailure.KeyRejected } s4 && s4.Plain.Contains("API key you set", StringComparison.Ordinal)
            && Calls("GET", "mods/1007/files") == 1, Describe(ex));

        // A 429 that never clears: the busy sentence after three retries.
        waits.Clear();
        server.Script("GET", "mods/1008/files", Enumerable.Repeat(new Answer(429, ServerError("too_many_requests", "Too many."), RetryAfter(1)), 6).ToArray());
        ex = await Throws(() => cf.GetVersionsAsync(1008));
        Check("persistent 429: gives up after three retries with the busy sentence",
            ex is StoreRequestException { Failure: StoreFailure.Busy } s5 && s5.Plain == StoreRequestException.BusySentence("CurseForge")
            && Calls("GET", "mods/1008/files") == 4, $"{Calls("GET", "mods/1008/files")} calls, {Describe(ex)}");
        Check("the page heard three waits, counted out of four attempts",
            waits.Count == 3 && waits.All(w => w.Attempts == 4) && waits.Select(w => w.Attempt).SequenceEqual([1, 2, 3]),
            string.Join(", ", waits.Select(w => $"{w.Attempt}/{w.Attempts}")));

        // Modrinth 429 straight from the store shape (no code), then fine.
        server.Script("GET", "search", new Answer(429, "{\"error\":\"ratelimit_error\",\"description\":\"You are being rate-limited.\"}", RetryAfter(1)));
        before = Calls("GET", "search");
        var hits = await mr.SearchAsync("sodium");
        Check("Modrinth 429 then 200: retried", hits.Count > 0 && Calls("GET", "search") - before == 2, $"{Calls("GET", "search") - before} calls");

        // upstream_blocked: the CDN block will not clear in seconds, so no retry.
        server.Script("GET", "mods/1009", new Answer(502, ServerError("upstream_blocked", "CurseForge is refusing requests from the launcher server right now. Try again later.")));
        var blocked = await cf.GetModAsync(1009);
        Check("502 upstream_blocked is not retried", blocked is null && Calls("GET", "mods/1009") == 1, $"{Calls("GET", "mods/1009")} calls");

        // A download URL refused because the author opted out: one call, the file's own sentence.
        server.Script("GET", "mods/1010/files/77/download-url", new Answer(403, ""));
        ex = await Throws(() => cf.GetDownloadUrlAsync(1010, 77));
        Check("download-url 403 with an opt-out: not retried, names the project, its page and the CurseForge app",
            ex is StoreRequestException { Failure: StoreFailure.NotDistributable } s6
            && s6.Plain.Contains("Opted Out Mod", StringComparison.Ordinal)
            && s6.Plain.Contains("CurseForge app", StringComparison.Ordinal)
            && s6.Plain.Contains("https://www.curseforge.com/minecraft/mc-mods/opted-out", StringComparison.Ordinal)
            && Calls("GET", "mods/1010/files/77/download-url") == 1, Describe(ex));

        // A download URL answered with no link at all: the same sentence.
        server.Script("GET", "mods/1010/files/78/download-url", new Answer(200, "{\"data\":null}"));
        ex = await Throws(() => cf.GetDownloadUrlAsync(1010, 78));
        Check("download-url with no link: the same sentence", ex is StoreRequestException { Failure: StoreFailure.NotDistributable } s7
            && s7.Plain.Contains("CurseForge app", StringComparison.Ordinal), Describe(ex));

        // A file that is fine: the CDN link, and nothing else asked.
        var url = await cf.GetDownloadUrlAsync(1011, 79);
        Check("download-url for a distributable file is the CDN link", url.StartsWith("https://edge.forgecdn.net/", StringComparison.Ordinal), url);

        // Modrinth and CurseForge bulk requests are chunked.
        before = Calls("POST", "version_files");
        await mr.MatchHashesAsync(Enumerable.Range(0, 1000).Select(i => new string((char)('a' + i % 26), 128)).Distinct().Concat(
            Enumerable.Range(0, 1000).Select(i => i.ToString("x128"))));
        var chunks = server.Seen.Where(s => s.Method == "POST" && s.Path == "version_files").Skip(before).ToList();
        Check($"a thousand hashes go in {ModrinthService.HashesPerRequest}-hash chunks",
            chunks.Count == 3 && chunks.All(c => Regex.Matches(c.Body, "\"[0-9a-f]{128}\"").Count <= ModrinthService.HashesPerRequest),
            $"{chunks.Count} requests");
        before = Calls("POST", "fingerprints");
        await cf.MatchFingerprintsAsync(Enumerable.Range(1, 2500).Select(i => (long)i));
        Check($"2,500 fingerprints go in {CurseForgeService.FingerprintsPerRequest}-fingerprint chunks", Calls("POST", "fingerprints") - before == 3,
            $"{Calls("POST", "fingerprints") - before} requests");
        before = Calls("POST", "mods");
        await cf.GetModsAsync(Enumerable.Range(1, 120));
        Check($"120 mod ids go in {CurseForgeService.BatchSize}-id chunks", Calls("POST", "mods") - before == 3, $"{Calls("POST", "mods") - before} requests");
    }

    private static string Describe(Exception? ex) => ex is null ? "no exception" : $"{ex.GetType().Name}: {ex.Message}";

    private static async Task<Exception?> Throws(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }
}

/// <summary>
/// A launcher server that answers the store proxy the way the real one does, from canned bodies, and
/// records every request. <see cref="Script"/> queues answers for one path, consumed in order before
/// the canned one.
/// </summary>
internal sealed class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Answer>> _scripts = new(StringComparer.Ordinal);
    private Task? _loop;
    private readonly CancellationTokenSource _stop = new();

    public ConcurrentQueue<Seen> Seen { get; } = new();
    public string BaseUrl { get; private set; } = "";

    public Task StartAsync()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        if (_loop is not null) { try { await _loop; } catch { } }
    }

    public void Dispose() => _listener.Close();

    public void Script(string method, string path, params Answer[] answers)
    {
        var queue = _scripts.GetOrAdd($"{method} {path}", _ => new ConcurrentQueue<Answer>());
        foreach (var a in answers) queue.Enqueue(a);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();

            // The proxy route is /proxy/{platform}/{**relativePath}; ASP.NET hands the controller the
            // decoded remainder, so the allowlist sees it decoded too.
            var segments = req.Url!.AbsolutePath.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
            var answer = new Answer(404, "{\"error\":\"Not found.\",\"code\":\"not_found\"}");
            if (segments.Length == 3 && segments[0] == "proxy")
            {
                var platform = segments[1];
                var path = Uri.UnescapeDataString(segments[2]);
                Seen.Enqueue(new Seen(req.HttpMethod, platform, path, req.Url.Query, body));
                if (!_scripts.TryGetValue($"{req.HttpMethod} {path}", out var queue) || !queue.TryDequeue(out answer!))
                    answer = Canned(req.HttpMethod, platform, path, body);
            }

            ctx.Response.StatusCode = answer.Status;
            ctx.Response.ContentType = "application/json";
            if (answer.Headers is not null)
                foreach (var (name, value) in answer.Headers)
                {
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) ctx.Response.ContentType = value;
                    else ctx.Response.Headers[name] = value;
                }
            var bytes = Encoding.UTF8.GetBytes(answer.Body);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("fake server: " + ex.Message);
            try { ctx.Response.Abort(); } catch { }
        }
    }

    // ── canned bodies in each store's shape ──────────────────────────────

    private const string CfFile =
        "{\"id\":5000001,\"modId\":238222,\"displayName\":\"jei-1.21.1-neoforge-19.0.0.jar\",\"fileName\":\"jei-1.21.1-neoforge-19.0.0.jar\"," +
        "\"fileDate\":\"2026-09-01T10:00:00Z\",\"fileLength\":1234,\"downloadCount\":5,\"downloadUrl\":\"https://edge.forgecdn.net/files/5000/1/jei.jar\"," +
        "\"gameVersions\":[\"1.21.1\",\"NeoForge\"],\"releaseType\":1,\"dependencies\":[{\"modId\":300000,\"relationType\":3}]}";

    private static string CfMod(int id, bool distributable = true, string name = "Just Enough Items", string slug = "jei") =>
        $"{{\"id\":{id},\"name\":\"{name}\",\"slug\":\"{slug}\",\"summary\":\"Items.\",\"downloadCount\":100,\"classId\":6," +
        $"\"allowModDistribution\":{(distributable ? "true" : "false")},\"logo\":{{\"url\":\"https://media.forgecdn.net/a.png\"}}," +
        "\"authors\":[{\"name\":\"mezz\"}],\"categories\":[{\"name\":\"Utility\"}]," +
        $"\"links\":{{\"websiteUrl\":\"https://www.curseforge.com/minecraft/mc-mods/{slug}\"}},\"screenshots\":[]," +
        "\"latestFilesIndexes\":[{\"gameVersion\":\"1.21.1\",\"fileId\":5000001,\"releaseType\":1,\"modLoader\":6}]}";

    private static readonly string MrVersion =
        "{\"id\":\"AbCdEf12\",\"project_id\":\"AANobbMI\",\"name\":\"Sodium 0.6.0\",\"version_number\":\"0.6.0\",\"game_versions\":[\"1.21.1\"]," +
        "\"loaders\":[\"fabric\"],\"version_type\":\"release\",\"date_published\":\"2026-09-01T10:00:00Z\",\"downloads\":10,\"changelog\":\"Faster.\"," +
        "\"files\":[{\"filename\":\"sodium-0.6.0.jar\",\"url\":\"https://cdn.modrinth.com/data/AANobbMI/versions/AbCdEf12/sodium-0.6.0.jar\",\"size\":10,\"primary\":true," +
        "\"hashes\":{\"sha512\":\"" + new string('a', 128) + "\"}}],\"dependencies\":[]}";

    private const string MrProject =
        "{\"id\":\"AANobbMI\",\"slug\":\"sodium\",\"title\":\"Sodium\",\"description\":\"Fast.\",\"body\":\"# Sodium\",\"downloads\":10," +
        "\"icon_url\":\"https://cdn.modrinth.com/i.png\",\"categories\":[\"optimization\"],\"gallery\":[]}";

    private static Answer Canned(string method, string platform, string path, string body)
    {
        if (platform == "curseforge")
        {
            if (method == "POST" && path == "mods")
                return new Answer(200, "{\"data\":[" + string.Join(",", IdsIn(body, "modIds").Select(id => CfMod(id))) + "]}");
            if (method == "POST" && path == "mods/files")
                return new Answer(200, "{\"data\":[" + CfFile + "]}");
            if (method == "POST" && path == "fingerprints")
                return new Answer(200, "{\"data\":{\"exactMatches\":[{\"id\":123456789,\"file\":" + CfFile + ",\"fingerprints\":[123456789]}],\"exactFingerprints\":[123456789]}}");
            if (path == "mods/search") return new Answer(200, "{\"data\":[" + CfMod(238222) + "]}");
            if (path == "categories") return new Answer(200, "{\"data\":[{\"id\":5,\"name\":\"Adventure\"},{\"id\":6,\"name\":\"Magic\"}]}");
            var m = Regex.Match(path, @"^mods/(\d+)(?:/(.*))?$");
            if (m.Success)
            {
                var id = int.Parse(m.Groups[1].Value);
                var rest = m.Groups[2].Value;
                if (rest == "") return new Answer(200, "{\"data\":" + (id == 1010 ? CfMod(id, false, "Opted Out Mod", "opted-out") : CfMod(id)) + "}");
                if (rest == "description") return new Answer(200, "{\"data\":\"<p>Items.</p>\"}");
                if (rest == "files") return new Answer(200, "{\"data\":[" + CfFile + "]}");
                if (Regex.IsMatch(rest, @"^files/\d+$")) return new Answer(200, "{\"data\":" + CfFile + "}");
                if (Regex.IsMatch(rest, @"^files/\d+/changelog$")) return new Answer(200, "{\"data\":\"<p>Changes.</p>\"}");
                if (Regex.IsMatch(rest, @"^files/\d+/download-url$")) return new Answer(200, "{\"data\":\"https://edge.forgecdn.net/files/5000/1/jei.jar\"}");
            }
            return new Answer(404, "{\"error\":\"Not found.\",\"code\":\"not_found\"}");
        }
        if (platform == "modrinth")
        {
            if (method == "POST" && path is "version_files" or "version_files/update")
                return new Answer(200, "{" + string.Join(",", HashesIn(body).Select(h => $"\"{h}\":{MrVersion}")) + "}");
            if (path == "search") return new Answer(200, "{\"hits\":[{\"project_id\":\"AANobbMI\",\"slug\":\"sodium\",\"title\":\"Sodium\",\"author\":\"jellysquid3\",\"downloads\":10,\"categories\":[\"optimization\"]}]}");
            if (path == "tag/category") return new Answer(200, "[{\"name\":\"optimization\",\"project_type\":\"mod\",\"header\":\"categories\"}]");
            if (path == "projects") return new Answer(200, "[" + MrProject + "]");
            if (Regex.IsMatch(path, @"^project/[^/]+$")) return new Answer(200, MrProject);
            if (Regex.IsMatch(path, @"^project/[^/]+/version$")) return new Answer(200, "[" + MrVersion + "]");
            if (Regex.IsMatch(path, @"^version/[^/]+$")) return new Answer(200, MrVersion);
            return new Answer(404, "{\"error\":\"not_found\",\"description\":\"the requested route does not exist\"}");
        }
        return new Answer(404, "{\"error\":\"Not found.\",\"code\":\"not_found\"}");
    }

    private static IEnumerable<int> IdsIn(string body, string field)
    {
        var m = Regex.Match(body, $"\"{field}\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!m.Success) yield break;
        foreach (var part in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var id)) yield return id;
    }

    private static IEnumerable<string> HashesIn(string body) =>
        Regex.Matches(body, "\"([0-9a-f]{128})\"").Select(m => m.Groups[1].Value).Distinct();
}
