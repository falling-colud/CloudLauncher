using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  The bundle and activity routes aren't in ApiClient yet, so they live here and share one
//  authenticated HttpClient and error reader. If ApiClient gains BrowseBundlesAsync/ActivityAsync,
//  delete SharingHttp and point these classes at it. Token refresh stays in ApiClient; see
//  SharingTransport.FreshTokenAsync.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The one authenticated <see cref="HttpClient"/> the sharing hub's own endpoints go through.
/// </summary>
/// <remarks>
/// <para>Static and shared, like <see cref="ApiClient"/>'s: the hub's panels are rebuilt on every
/// refresh, and a client per panel means a connection pool per panel, which exhausts sockets.</para>
/// <para>Rebuilt when the configured server URL changes.</para>
/// </remarks>
internal static class SharingHttp
{
    private static readonly object Gate = new();
    private static HttpClient? _client;
    private static string _builtFor = "";

    public static HttpClient Client(AppSettings settings)
    {
        var baseUrl = settings.ServerUrl.TrimEnd('/') + "/";
        lock (Gate)
        {
            if (_client is not null && _builtFor == baseUrl) return _client;
            _client?.Dispose();
            // Same two timeouts ApiClient uses: a short connect deadline so a blackholed route is
            // noticed in seconds, and a long overall one because a bundle upload is 64 MB.
            var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) };
            _client = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromMinutes(5)
            };
            ApiClient.ApplyUserAgent(_client);
            _builtFor = baseUrl;
            return _client;
        }
    }
}

/// <summary>
/// Browse, publish, download and install for content bundles: shader packs, config bundles,
/// KubeJS bundles and data packs.
/// </summary>
/// <remarks>
/// <para>A bundle is a zip plus the instance folder it unpacks into
/// (<see cref="ContentBundleSummary.TargetPathRoot"/>). Versions, collaborators, teams and
/// visibility work as for hosted mods and resource packs.</para>
/// <para>Installing re-validates every path even though the server checks them on upload, since this
/// client is what writes the files. See <see cref="BundleSafePath"/> and <see cref="InstallAsync"/>.</para>
/// </remarks>
public sealed class ContentBundleService(ApiClient api, AppSettings settings, PackFolderService folders)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What the server accepts per bundle zip. Mirrored here so an over-sized compose is
    /// refused before 64 MB is uploaded and rejected.</summary>
    public const long MaxBundleBytes = 64L * 1024 * 1024;

    // ── labels ──

    public static string KindLabel(BundleKind kind) => kind switch
    {
        BundleKind.ShaderPack => "Shader pack",
        BundleKind.ConfigBundle => "Config bundle",
        BundleKind.KubeJsBundle => "KubeJS bundle",
        BundleKind.DataPack => "Data pack",
        _ => "Bundle"
    };

    /// <summary>The kinds the create form offers, in the order it offers them.</summary>
    public static IReadOnlyList<BundleKind> Kinds { get; } =
    [
        BundleKind.ShaderPack, BundleKind.ConfigBundle, BundleKind.KubeJsBundle, BundleKind.DataPack
    ];

    public string IconUrl(Guid bundleId) => $"{settings.ServerUrl.TrimEnd('/')}/bundles/{bundleId}/icon";

    // ── browse / detail ──

    /// <param name="kind">Null lists every kind (the Sharing overview shows them together).</param>
    public async Task<BundleBrowsePage> BrowseAsync(
        BundleKind? kind, BundleBrowseSource source, Guid? teamId = null,
        string? query = null, string? sort = null, int offset = 0, int limit = 50,
        CancellationToken ct = default)
    {
        var qs = $"bundles?source={source}&offset={offset}&limit={limit}";
        if (kind.HasValue) qs += $"&kind={kind.Value}";
        if (teamId.HasValue) qs += $"&teamId={teamId.Value}";
        if (!string.IsNullOrWhiteSpace(query)) qs += $"&q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(sort)) qs += $"&sort={Uri.EscapeDataString(sort)}";
        return await ReadAsync<BundleBrowsePage>(
            new HttpRequestMessage(HttpMethod.Get, qs), "The bundle list could not be loaded", ct);
    }

    public async Task<ContentBundleDetail> GetAsync(Guid id, CancellationToken ct = default) =>
        await ReadAsync<ContentBundleDetail>(
            new HttpRequestMessage(HttpMethod.Get, $"bundles/{id}"), "That bundle could not be opened", ct);

    public async Task<ContentBundleSummary> CreateAsync(CreateBundleRequest req, CancellationToken ct = default) =>
        await ReadAsync<ContentBundleSummary>(
            JsonRequest(HttpMethod.Post, "bundles", req), "The bundle could not be created", ct);

    public async Task UpdateAsync(Guid id, UpdateBundleRequest req, CancellationToken ct = default) =>
        await SendNoContentAsync(
            JsonRequest(HttpMethod.Patch, $"bundles/{id}", req), "The changes could not be saved", ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        await SendNoContentAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"bundles/{id}"), "The bundle could not be deleted", ct);

    // ── collaborators and teams ──

    public async Task<PackCollaboratorEntry> AddCollaboratorAsync(Guid id, AddCollaboratorRequest req, CancellationToken ct = default) =>
        await ReadAsync<PackCollaboratorEntry>(
            JsonRequest(HttpMethod.Post, $"bundles/{id}/collaborators", req), "That person could not be added", ct);

    public async Task UpdateCollaboratorAsync(Guid id, Guid userId, UpdateCollaboratorRequest req, CancellationToken ct = default) =>
        await SendNoContentAsync(
            JsonRequest(HttpMethod.Patch, $"bundles/{id}/collaborators/{userId}", req),
            "Their access could not be changed", ct);

    public async Task RemoveCollaboratorAsync(Guid id, Guid userId, CancellationToken ct = default) =>
        await SendNoContentAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"bundles/{id}/collaborators/{userId}"),
            "They could not be removed", ct);

    /// <summary>Gives back the caller's own access to a bundle.</summary>
    /// <remarks>The same route an owner removes someone with (anyone may remove themselves). A method of
    /// its own so a refusal reads "you could not leave".</remarks>
    public async Task LeaveAsync(Guid id, Guid me, CancellationToken ct = default) =>
        await SendNoContentAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"bundles/{id}/collaborators/{me}"),
            "You could not leave this bundle", ct);

    public async Task<PackTeamEntry> AddTeamAsync(Guid id, AddPackTeamRequest req, CancellationToken ct = default) =>
        await ReadAsync<PackTeamEntry>(
            JsonRequest(HttpMethod.Post, $"bundles/{id}/teams", req), "That team could not be added", ct);

    public async Task UpdateTeamAsync(Guid id, Guid teamId, UpdatePackTeamRequest req, CancellationToken ct = default) =>
        await SendNoContentAsync(
            JsonRequest(HttpMethod.Patch, $"bundles/{id}/teams/{teamId}", req),
            "The team's access could not be changed", ct);

    public async Task RemoveTeamAsync(Guid id, Guid teamId, CancellationToken ct = default) =>
        await SendNoContentAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"bundles/{id}/teams/{teamId}"),
            "The team could not be removed", ct);

    // ── invitations and share links ──

    /// <summary>Offers somebody access instead of imposing it.</summary>
    /// <remarks>
    /// Unlike <see cref="AddCollaboratorAsync"/>, the invitation waits in the other person's Sharing hub
    /// to be accepted or declined.
    /// </remarks>
    public async Task<BundleInvitationEntry> InviteAsync(
        Guid id, string username, PackPermissions permissions,
        int? expiresInDays = null, string? message = null, CancellationToken ct = default) =>
        await ReadAsync<BundleInvitationEntry>(
            JsonRequest(HttpMethod.Post, $"bundles/{id}/invitations",
                new CreatePackInvitationRequest(username, permissions, message, expiresInDays)),
            "The invitation could not be sent", ct);

    /// <summary>Every invitation ever minted for this bundle, tokens included.</summary>
    /// <remarks>Needs manage-sharing on the server, since a token grants access.</remarks>
    public async Task<IReadOnlyList<BundleInvitationEntry>> ListInvitationsAsync(
        Guid id, CancellationToken ct = default) =>
        await ReadAsync<List<BundleInvitationEntry>>(
            new HttpRequestMessage(HttpMethod.Get, $"bundles/{id}/invitations"),
            "The invitations could not be read", ct);

    /// <summary>Withdraws one invitation. Idempotent.</summary>
    public async Task RevokeInvitationAsync(Guid id, Guid invitationId, CancellationToken ct = default) =>
        await SendNoContentAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"bundles/{id}/invitations/{invitationId}"),
            "The invitation could not be withdrawn", ct);

    /// <summary>Mints (or re-mints) the bundle's share link. Re-minting kills the previous one.</summary>
    public async Task<ShareLinkInfo> CreateShareLinkAsync(
        Guid id, PackPermissions permissions, int? expiresInDays = null, CancellationToken ct = default) =>
        await ReadAsync<ShareLinkInfo>(
            JsonRequest(HttpMethod.Post, $"bundles/{id}/share-link",
                new CreateShareLinkRequest(permissions, expiresInDays)),
            "The share link could not be created", ct);

    /// <summary>Kills the share link. What people redeemed while it was live stays granted.</summary>
    public async Task RevokeShareLinkAsync(Guid id, CancellationToken ct = default) =>
        await SendNoContentAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"bundles/{id}/share-link"),
            "The share link could not be removed", ct);

    /// <summary>
    /// Usernames starting with <paramref name="prefix"/>, for "did you mean" after a name that did
    /// not exist.
    /// </summary>
    /// <remarks>Prefix match, small page, rate-limited server-side. The server needs at least two
    /// characters.</remarks>
    public async Task<UserSearchPage> SearchUsersAsync(string prefix, CancellationToken ct = default)
    {
        if (prefix.Trim().Length < 2) return new UserSearchPage([], 0);
        return await ReadAsync<UserSearchPage>(
            new HttpRequestMessage(HttpMethod.Get, $"users?q={Uri.EscapeDataString(prefix.Trim())}"),
            "That name could not be looked up", ct);
    }

    // ── versions ──

    /// <param name="progress">Bytes handed to the socket so far, for an upload bar.</param>
    public async Task<ContentBundleVersionInfo> PublishVersionAsync(
        Guid id, string zipPath, CreateBundleVersionRequest meta,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var size = new FileInfo(zipPath).Length;
        if (size == 0) throw new InvalidOperationException("That file is empty, so there is nothing to publish.");
        if (size > MaxBundleBytes)
            throw new InvalidOperationException(
                $"That file is {FormatSize(size)}. The server accepts bundles up to {FormatSize(MaxBundleBytes)}.");

        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(zipPath);
        var fileContent = new StreamContent(progress is null ? fs : new CountingStream(fs, progress));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "file", Path.GetFileName(zipPath));
        form.Add(new StringContent(JsonSerializer.Serialize(meta, Json)), "metadata");

        var request = new HttpRequestMessage(HttpMethod.Post, $"bundles/{id}/versions") { Content = form };
        return await ReadAsync<ContentBundleVersionInfo>(request, "The version could not be published", ct);
    }

    public async Task UpdateVersionAsync(Guid id, Guid versionId, UpdateBundleVersionRequest req, CancellationToken ct = default) =>
        await SendNoContentAsync(
            JsonRequest(HttpMethod.Patch, $"bundles/{id}/versions/{versionId}", req),
            "The version could not be updated", ct);

    public async Task DeleteVersionAsync(Guid id, Guid versionId, CancellationToken ct = default) =>
        await SendNoContentAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"bundles/{id}/versions/{versionId}"),
            "The version could not be deleted", ct);

    /// <summary>Downloads one version to a temporary file and returns its path. The caller deletes it.</summary>
    public async Task<string> DownloadVersionAsync(
        Guid id, Guid versionId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"bundles/{id}/versions/{versionId}/download");
        var resp = await SendAsync(request, ct, streaming: true);
        try
        {
            await ThrowIfFailedAsync(resp, "The bundle could not be downloaded", ct);
            var total = resp.Content.Headers.ContentLength ?? -1;
            var dir = Path.Combine(Path.GetTempPath(), "CloudLauncher", "bundles");
            Directory.CreateDirectory(dir);
            // A fresh name each time, created new, so two downloads never share a file and nothing already
            // sitting at a predictable name gets written through.
            var path = Path.Combine(dir, $"{versionId:N}-{Guid.NewGuid():N}.zip");

            await using var source = await resp.Content.ReadAsStreamAsync(ct);
            var created = false;
            try
            {
                await using var dest = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                var buffer = new byte[128 * 1024];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) progress?.Report(Math.Clamp((double)read / total, 0, 1));
                }
            }
            catch
            {
                if (created)
                {
                    try { File.Delete(path); } catch { /* a temp file that outlives us is not worth an error */ }
                }
                throw;
            }
            return path;
        }
        finally { resp.Dispose(); }
    }

    // ── installing ──

    /// <param name="Failures">One line per instance or file that could not be written, with a reason.</param>
    /// <param name="Rejected">Zip entries refused by <see cref="BundleSafePath"/>. Non-empty means
    /// nothing was written (see <see cref="InstallAsync"/>).</param>
    public sealed record BundleInstallResult(
        int Instances, int FilesWritten, int BackedUp, List<string> Failures, List<string> Rejected)
    {
        public bool Refused => Rejected.Count > 0;

        /// <summary>One status-bar line, worded the way the config hub's copy already reports.</summary>
        public string Summary()
        {
            if (Refused)
                return $"Refused - {Rejected.Count} unsafe path(s) in the archive. Nothing was written.";
            var parts = new List<string>();
            if (FilesWritten > 0) parts.Add($"{FilesWritten} file(s) into {Instances} instance(s)");
            if (BackedUp > 0) parts.Add($"{BackedUp} replaced (backed up)");
            if (Failures.Count > 0) parts.Add($"{Failures.Count} failed - {Failures[0]}");
            return parts.Count == 0 ? "Nothing to install." : string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// Downloads a version and unpacks it into every chosen instance, backing up anything it
    /// replaces as <c>&lt;name&gt;.bak-yyyyMMdd-HHmmss</c>.
    /// </summary>
    /// <remarks>
    /// <para>Every entry is checked against <see cref="BundleSafePath"/> and the target instance root
    /// before the first byte is written. One bad entry refuses the whole install, so a hostile archive
    /// is never half-applied.</para>
    /// <para>Call from a background thread.</para>
    /// </remarks>
    /// <param name="bundleId">The bundle to install.</param>
    /// <param name="bundleName">Only used in progress lines and the launcher log.</param>
    /// <param name="targetPathRoot">The bundle's stored root. Re-validated here against
    /// <paramref name="kind"/> before anything is written.</param>
    public async Task<BundleInstallResult> InstallAsync(
        Guid bundleId, string bundleName, BundleKind kind, string targetPathRoot,
        Guid versionId, IReadOnlyList<PackSummary> targets,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (targets.Count == 0) return new BundleInstallResult(0, 0, 0, [], []);

        log?.Report($"Downloading {bundleName}...");
        var zipPath = await DownloadVersionAsync(bundleId, versionId, null, ct);
        try
        {
            return InstallFromZip(zipPath, bundleName, kind, targetPathRoot, targets, log, ct);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* a temp file that outlives us is not worth an error */ }
        }
    }

    /// <summary>
    /// The unpacking half of <see cref="InstallAsync"/>, against a zip already on disk.
    /// </summary>
    /// <remarks>Split out so it can be tested against hostile archives without a server. Call from a
    /// background thread.</remarks>
    public BundleInstallResult InstallFromZip(
        string zipPath, string bundleName, BundleKind kind, string targetPathRoot,
        IReadOnlyList<PackSummary> targets, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var failures = new List<string>();
        var rejected = new List<string>();
        if (targets.Count == 0) return new BundleInstallResult(0, 0, 0, failures, rejected);

        var root = BundleSafePath.NormalizeRoot(kind, targetPathRoot, out var rootError);
        if (rootError is not null)
        {
            rejected.Add($"target folder \"{targetPathRoot}\" - {rootError}");
            AppLog.Log("bundles", $"Refused to install {bundleName}: {rootError}");
            return new BundleInstallResult(0, 0, 0, failures, rejected);
        }

        {
            using var archive = ZipFile.OpenRead(zipPath);

            // ── the gate ──
            var entries = new List<ZipArchiveEntry>();
            long totalUnpacked = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // a folder
                if ((BundleSafePath.ValidateEntryUnder(root, entry.FullName) ?? SafeZip.CheckEntry(entry)) is { } why)
                {
                    rejected.Add($"{entry.FullName} - {why}");
                    continue;
                }
                if (entry.Length > BundleSafePath.MaxEntryBytes)
                {
                    rejected.Add($"{entry.FullName} - one file of {FormatSize(entry.Length)} is beyond the {FormatSize(BundleSafePath.MaxEntryBytes)} limit");
                    continue;
                }
                totalUnpacked += entry.Length;
                entries.Add(entry);
            }
            if (entries.Count > BundleSafePath.MaxEntries)
                rejected.Add($"the archive holds {entries.Count:N0} files, beyond the {BundleSafePath.MaxEntries:N0} limit");
            if (totalUnpacked > BundleSafePath.MaxTotalBytes)
                rejected.Add($"the archive unpacks to {FormatSize(totalUnpacked)}, beyond the {FormatSize(BundleSafePath.MaxTotalBytes)} limit");
            if (entries.Count == 0 && rejected.Count == 0)
                rejected.Add("the archive is empty");

            if (rejected.Count > 0)
            {
                AppLog.Log("bundles",
                    $"Refused to install {bundleName}: {rejected.Count} unsafe or over-size entr(ies); first is {rejected[0]}.");
                return new BundleInstallResult(0, 0, 0, failures, rejected);
            }

            // ── writing ──
            // Invariant: this becomes a ".bak-" suffix that ConfigHubService.BackupTakenUtc
            // parses back with InvariantCulture, so the write has to agree with the read.
            var stamp = TimeFormat.StampNow();
            var written = 0;
            var backedUp = 0;
            var instances = 0;

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                string gameDir;
                try { gameDir = folders.GameDir(target.Id); }
                catch (Exception ex) { failures.Add($"{target.Name}: {ex.Message}"); continue; }

                var destRoot = root.Length == 0 ? gameDir : Path.Combine(gameDir, root.Replace('/', Path.DirectorySeparatorChar));
                var fullGameDir = Path.GetFullPath(gameDir);
                instances++;

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = PathSafety.ResolveInside(destRoot, entry.FullName);

                    // Second, independent check: the resolved path must still be under this instance.
                    if (dest is null || !BundleSafePath.StaysInside(fullGameDir, dest))
                    {
                        failures.Add($"{target.Name} · {entry.FullName}: would write outside the instance folder");
                        continue;
                    }

                    log?.Report($"{target.Name} · {entry.FullName}");
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if (File.Exists(dest))
                        {
                            File.Copy(dest, $"{dest}.bak-{stamp}", overwrite: true);
                            backedUp++;
                        }
                        SafeZip.ExtractToFile(entry, dest, overwrite: true);
                        written++;
                    }
                    catch (Exception ex) { failures.Add($"{target.Name} · {entry.FullName}: {ex.Message}"); }
                }
                ConfigHubService.Invalidate(target.Id);
            }

            AppLog.Log("bundles",
                $"Installed {bundleName} into {instances} instance(s): {written} file(s), {backedUp} backed up, {failures.Count} failure(s).");
            return new BundleInstallResult(instances, written, backedUp, failures, rejected);
        }
    }

    // ── composing a bundle out of an instance ──

    /// <param name="Skipped">Paths that were not under the bundle's target folder, so could not be
    /// carried by a bundle that unpacks into it.</param>
    public sealed record ComposeResult(string ZipPath, int Files, long Bytes, List<string> Skipped);

    /// <summary>
    /// Zips a set of an instance's files into a bundle payload, with each entry stored relative to
    /// <paramref name="targetRoot"/>.
    /// </summary>
    /// <remarks>
    /// A bundle rooted at <c>config</c> stores <c>jei/jei-client.ini</c>, so installing it writes back to
    /// <c>config/jei/</c> in the receiving instance. Paths outside the root are skipped, since they
    /// couldn't be put back in the same place.
    /// </remarks>
    public static ComposeResult Compose(
        string gameDir, string targetRoot, IReadOnlyList<string> gameRelativePaths,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var prefix = targetRoot.Trim('/').Length == 0 ? "" : targetRoot.Trim('/') + "/";
        var dir = Path.Combine(Path.GetTempPath(), "CloudLauncher", "bundles");
        Directory.CreateDirectory(dir);
        // Invariant so the name sorts, and a non-Gregorian calendar can't change the year in it.
        var zipPath = Path.Combine(dir, $"compose-{TimeFormat.StampNow()}.zip");

        var skipped = new List<string>();
        var files = 0;
        long bytes = 0;

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var rel in gameRelativePaths)
            {
                ct.ThrowIfCancellationRequested();
                var normalised = rel.Replace('\\', '/').TrimStart('/');
                if (prefix.Length > 0 && !normalised.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(rel);
                    continue;
                }
                var entryName = prefix.Length == 0 ? normalised : normalised[prefix.Length..];
                if (BundleSafePath.ValidateEntryUnder(prefix.TrimEnd('/'), entryName) is { } why)
                {
                    skipped.Add($"{rel} ({why})");
                    continue;
                }

                // The prefix is the bundle's stored root, so the file is only read from inside the game folder.
                var source = PathSafety.ResolveInside(gameDir, normalised);
                if (source is null || !File.Exists(source)) { skipped.Add(rel); continue; }

                log?.Report(entryName);
                zip.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
                files++;
                try { bytes += new FileInfo(source).Length; } catch { /* size is a label, not the work */ }
            }
        }

        AppLog.Log("bundles", $"Composed {files} file(s) ({FormatSize(bytes)}) into {Path.GetFileName(zipPath)}.");
        return new ComposeResult(zipPath, files, bytes, skipped);
    }

    // ── plumbing ──

    private readonly SharingTransport _transport = new(api, settings);

    private static HttpRequestMessage JsonRequest<T>(HttpMethod method, string url, T body) =>
        new(method, url) { Content = JsonContent.Create(body, options: Json) };

    private Task<T> ReadAsync<T>(HttpRequestMessage request, string what, CancellationToken ct) =>
        _transport.ReadAsync<T>(request, what, ct);

    private Task SendNoContentAsync(HttpRequestMessage request, string what, CancellationToken ct) =>
        _transport.SendNoContentAsync(request, what, ct);

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, bool streaming = false) =>
        _transport.SendAsync(request, ct, streaming);

    private static Task ThrowIfFailedAsync(HttpResponseMessage resp, string what, CancellationToken ct) =>
        SharingTransport.ThrowIfFailedAsync(resp, what, ct);

    /// <summary>
    /// One sentence a person can act on, for any exception a sharing call can throw.
    /// </summary>
    /// <remarks>
    /// <see cref="ApiException"/> reads <c>"403 Forbidden: {body}"</c> and the body can be empty. This
    /// strips the prefix, keeps the server's sentence if there is one, and otherwise words the status
    /// code. The exception itself goes to <see cref="AppLog"/>.
    /// </remarks>
    public static string Explain(Exception ex, string fallback = "Something went wrong.") =>
        SharingTransport.Explain(ex, fallback);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };

    /// <summary>A pass-through stream that reports how many bytes have been pulled out of it.</summary>
    private sealed class CountingStream(Stream inner, IProgress<long> progress) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            if (n > 0) { _read += n; progress.Report(_read); }
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            if (n > 0) { _read += n; progress.Report(_read); }
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The authenticated request/response half of the sharing hub's own endpoints, shared by the
/// bundle and activity services.
/// </summary>
/// <remarks>Separate so the activity feed doesn't need a bundle service for one GET. Token refresh is
/// left to <see cref="ApiClient"/>; see <see cref="FreshTokenAsync"/>.</remarks>
internal sealed class SharingTransport(ApiClient api, AppSettings settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T> ReadAsync<T>(HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var resp = await SendAsync(request, ct);
        await ThrowIfFailedAsync(resp, what, ct);
        var value = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new ApiException($"{what}: the server's reply was empty.", resp.StatusCode);
    }

    public async Task SendNoContentAsync(HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var resp = await SendAsync(request, ct);
        await ThrowIfFailedAsync(resp, what, ct);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, bool streaming = false)
    {
        var token = await FreshTokenAsync(ct);
        if (token is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            return await SharingHttp.Client(settings).SendAsync(
                request,
                streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new OfflineException(Connectivity.DescribeTransportFailure(ex, ct) ?? "the request could not be sent");
        }
    }

    /// <summary>
    /// The access token to send, refreshed through <see cref="ApiClient"/> if it is near expiry.
    /// </summary>
    /// <remarks>
    /// The refresh token rotates on use, so only ApiClient refreshes (behind its own semaphore); a second
    /// implementation would race it and invalidate the token for both. <c>auth/me</c> is the cheapest
    /// call that makes ApiClient refresh, and that only happens within two minutes of expiry.
    /// </remarks>
    private async Task<string?> FreshTokenAsync(CancellationToken ct)
    {
        var token = settings.AccessToken;
        if (string.IsNullOrEmpty(token)) return null;
        var expires = settings.AccessTokenExpiresAt;
        if (expires is not null && expires.Value > DateTimeOffset.UtcNow.AddMinutes(2)) return token;

        try { await api.MeAsync(ct); }
        catch (SessionExpiredException) { throw; }
        catch (OfflineException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // auth/me failed for some other reason. The token may still be good, so try the real call
            // anyway.
            AppLog.Log("bundles", $"Token check before a sharing call did not answer cleanly: {ex.GetType().Name}.");
        }
        return settings.AccessToken;
    }

    public static async Task ThrowIfFailedAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var sentence = await DescribeFailureAsync(resp, ct);
        AppLog.Log("bundles", $"{resp.RequestMessage?.Method} {resp.RequestMessage?.RequestUri} > {(int)resp.StatusCode}: {sentence}");
        throw new ApiException($"{what}: {sentence}", resp.StatusCode);
    }

    /// <summary>
    /// One plain sentence for a non-2xx answer.
    /// </summary>
    /// <remarks>
    /// The server answers with a problem body on most routes, <c>{ "error": "..." }</c> on the bundle
    /// routes, and no body at all for a bare <c>Forbid()</c>. All three become a sentence; the status
    /// code is only the fallback.
    /// </remarks>
    private static async Task<string> DescribeFailureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string body;
        try { body = await resp.Content.ReadAsStringAsync(ct); }
        catch { body = ""; }
        if (body.Length > 8192) body = body[..8192];

        var fromBody = ExtractSentence(body);
        return fromBody ?? StatusSentence(resp.StatusCode);
    }

    private static string? ExtractSentence(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                foreach (var name in (string[])["detail", "error", "message", "title"])
                    if (doc.RootElement.TryGetProperty(name, out var prop)
                        && prop.ValueKind == JsonValueKind.String
                        && prop.GetString() is { Length: > 0 } text)
                        return Sentence(text);
            }
            catch { /* not the JSON we expected; fall through to the status sentence */ }
            return null;
        }

        // A captive portal or a proxy answering with HTML is not a message for a person.
        if (trimmed.StartsWith('<') || trimmed.Length > 300) return null;
        return Sentence(trimmed);
    }

    private static string StatusSentence(HttpStatusCode status) => (int)status switch
    {
        401 => "you are not signed in any more.",
        403 => "you do not have permission to do that.",
        404 => "the server does not have it any more.",
        409 => "it changed on the server while you were working on it.",
        413 => "the file is larger than the server accepts.",
        429 => "the server is rate-limiting this - try again in a moment.",
        >= 500 => $"the server had a problem ({(int)status}).",
        _ => $"the server refused it ({(int)status})."
    };

    private static string Sentence(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return s;
        if (!char.IsUpper(s[0]) && !char.IsDigit(s[0])) s = char.ToUpperInvariant(s[0]) + s[1..];
        if (!s.EndsWith('.') && !s.EndsWith('!') && !s.EndsWith('?')) s += ".";
        return s;
    }

    // ── shared helpers for the sharing panels ──

    /// <summary>Behind <see cref="ContentBundleService.Explain"/>; see its remarks.</summary>
    public static string Explain(Exception ex, string fallback = "Something went wrong.") => ex switch
    {
        OfflineException off => off.Message,
        SessionExpiredException => "Your session has expired - sign in again.",
        ApiException api => ExplainApi(api, fallback),
        InvalidOperationException inv when inv.Message.Length is > 0 and < 300 => Sentence(inv.Message),
        _ => fallback
    };

    private static string ExplainApi(ApiException ex, string fallback)
    {
        var message = ex.Message;
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && colon < 40)
        {
            var head = message[..colon];
            // "403 Forbidden" / "404 Not Found": a status line, not a sentence.
            if (head.Length > 0 && char.IsDigit(head[0]))
            {
                var tail = message[(colon + 2)..].Trim();
                var sentence = ExtractSentence(tail);
                return sentence ?? StatusSentence(ex.Status);
            }
        }
        return message.Length is > 0 and < 400 ? message : fallback;
    }
}

/// <summary>
/// The path rules the client enforces before it unpacks anything a bundle carries.
/// </summary>
/// <remarks>
/// <para>The server checks the same things on upload (<c>BundleContentPolicy</c>). This is an
/// independent second copy because the client is what writes the files, and an entry like
/// <c>..\..\..\Start Menu\Programs\Startup\x.bat</c> must never land.</para>
/// <para>Allowed: relative, forward-slash paths of ordinary name segments. Refused: absolute or
/// rooted paths, drive letters, UNC prefixes, backslashes, <c>.</c> and <c>..</c>, empty segments,
/// a leading <c>~</c>, control characters, Win32 wildcard and redirection characters, segments Win32
/// trims (trailing dot or space, leading space) and reserved device names. <see cref="StaysInside"/>
/// then asks the OS where the resolved path really points.</para>
/// </remarks>
public static class BundleSafePath
{
    public const int MaxEntries = 20_000;
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxTotalBytes = 1024L * 1024 * 1024;

    /// <summary>Longest a target root may be (the server's column width).</summary>
    public const int MaxRootLength = 128;

    private static readonly char[] Forbidden = ['*', '?', '"', '<', '>', '|', ':'];

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    /// <summary>The folders each kind is allowed to unpack into, first segment only. Mirrors the
    /// server's table, so a kind the server would refuse is refused here too.</summary>
    public static IReadOnlyList<string> AllowedRoots(BundleKind kind) => kind switch
    {
        BundleKind.ShaderPack => ["shaderpacks"],
        BundleKind.ConfigBundle => ["config", "defaultconfigs"],
        BundleKind.KubeJsBundle => ["kubejs"],
        BundleKind.DataPack => ["datapacks"],
        BundleKind.Other =>
            ["", "shaderpacks", "config", "defaultconfigs", "kubejs", "datapacks", "resourcepacks", "scripts", "schematics", "saves"],
        _ => []
    };

    /// <summary>
    /// The stored, lower-cased form of a bundle's target folder, or "" with
    /// <paramref name="error"/> set.
    /// </summary>
    /// <remarks><c>mods</c> is allowed for no kind, including Other: a jar dropped there is code
    /// execution, which only the mod family handles.</remarks>
    public static string NormalizeRoot(BundleKind kind, string? raw, out string? error)
    {
        error = null;
        var allowed = AllowedRoots(kind);
        if (allowed.Count == 0)
        {
            error = "that is not a bundle kind this launcher knows how to unpack";
            return "";
        }

        var value = (raw ?? "").Replace('\\', '/').Trim();
        value = value.Trim('/');
        if (value.Length == 0)
        {
            if (allowed.Contains("")) return "";
            var fallback = BundleTargets.DefaultFor(kind);
            return fallback;
        }

        if (value.Length > MaxRootLength)
        {
            error = $"the folder path is longer than {MaxRootLength} characters";
            return "";
        }

        if (ValidateEntry(value) is { } why) { error = why; return ""; }

        var segments = value.Split('/');
        if (segments.Length > 4)
        {
            error = "the folder is more than four levels deep";
            return "";
        }

        var first = segments[0].ToLowerInvariant();
        if (!allowed.Contains(first))
        {
            error = $"a {ContentBundleService.KindLabel(kind).ToLowerInvariant()} may only unpack into "
                  + string.Join(" or ", allowed.Where(a => a.Length > 0).Select(a => $"\"{a}\""));
            return "";
        }

        return string.Join('/', segments.Select(s => s.ToLowerInvariant()));
    }

    /// <summary>Null when the relative path is safe to write, else the reason it is not.</summary>
    public static string? ValidateEntry(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "the path is empty";
        if (path.Length > 1024) return "the path is too long";
        if (path.Contains('\\')) return "it contains a backslash";
        if (path.StartsWith('/')) return "it starts at the drive root";
        if (path.StartsWith('~')) return "it starts with ~";

        foreach (var c in path)
        {
            if (char.IsControl(c)) return "it contains a control character";
            if (Array.IndexOf(Forbidden, c) >= 0) return $"it contains '{c}'";
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) return "it has an empty folder name";
            if (segment is "." or "..") return "it walks up out of the folder";
            if (segment.Length > 255) return "one folder name is too long";
            if (segment[^1] is '.' or ' ' || segment[0] == ' ') return $"\"{segment}\" ends or starts with a space or dot";
            var stem = segment.Split('.')[0];
            if (DeviceNames.Contains(stem)) return $"\"{stem}\" is a reserved Windows device name";
        }

        // The launcher-wide rules too, which also know the device names Win32 still recognises with
        // a trailing space or a superscript digit.
        if (!PathSafety.IsSafeRelativePath(path)) return "it is not a plain relative path";

        return null;
    }

    /// <summary>Null when an archive entry may be unpacked under <paramref name="root"/> (a root
    /// <see cref="NormalizeRoot"/> returned), else the reason it may not.</summary>
    /// <remarks><see cref="ValidateEntry"/>, plus the one rule the empty root adds: under the instance
    /// folder itself, which only Other bundles unpack into, nothing may land in <c>mods</c>, for the
    /// same reason no root may be <c>mods</c>.</remarks>
    public static string? ValidateEntryUnder(string root, string? path)
    {
        if (ValidateEntry(path) is { } why) return why;
        if (root.Length == 0 && string.Equals(path!.Split('/')[0], "mods", StringComparison.OrdinalIgnoreCase))
            return "nothing may be unpacked into mods";
        return null;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> resolves to somewhere inside
    /// <paramref name="rootFullPath"/>.
    /// </summary>
    /// <remarks>The independent check: it asks the OS where the path really points, so it catches
    /// anything the segment rules miss.</remarks>
    public static bool StaysInside(string rootFullPath, string candidate)
    {
        try
        {
            var root = Path.GetFullPath(rootFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(candidate);
            return full.Length > root.Length
                && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (full[root.Length] == Path.DirectorySeparatorChar || full[root.Length] == Path.AltDirectorySeparatorChar);
        }
        catch { return false; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  ACTIVITY
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Reads <c>/activity</c>: who shared what with whom, newest first.</summary>
public sealed class ActivityFeedService(ApiClient api, AppSettings settings)
{
    private readonly SharingTransport _transport = new(api, settings);

    /// <summary>Everything that touched me: what I did, what was done to me, and what happened to
    /// the things I own.</summary>
    public Task<ActivityFeedPage> MineAsync(int offset = 0, int limit = 50, CancellationToken ct = default) =>
        Get($"activity/me?offset={offset}&limit={limit}", ct);

    /// <summary>One subject's history. The server refuses subjects the caller may not view, deleted
    /// ones included (entries outlive what they describe).</summary>
    public Task<ActivityFeedPage> ForSubjectAsync(
        ActivitySubjectType subjectType, Guid subjectId, int offset = 0, int limit = 50, CancellationToken ct = default) =>
        Get($"activity?subjectType={subjectType}&subjectId={subjectId}&offset={offset}&limit={limit}", ct);

    private Task<ActivityFeedPage> Get(string url, CancellationToken ct) =>
        _transport.ReadAsync<ActivityFeedPage>(
            new HttpRequestMessage(HttpMethod.Get, url), "The activity feed could not be loaded", ct);

    // ── rendering one entry as a sentence ──

    /// <summary>
    /// One entry as the plain sentence the feed shows: "alex shared My Pack with Survival Crew".
    /// </summary>
    /// <remarks>
    /// The server stores the subject's name so a deleted pack still reads sensibly, and an actor whose
    /// account is gone reads as "Someone". The time is left out; the feed shows it separately.
    /// </remarks>
    public static string Describe(ActivityFeedEntry entry)
    {
        var actor = entry.ActorUsername is { Length: > 0 } a ? a : "Someone";
        var subject = entry.SubjectName is { Length: > 0 } s ? s : "something";
        var target = entry.TargetTeamName is { Length: > 0 } t ? t
                   : entry.TargetUsername is { Length: > 0 } u ? u
                   : null;
        var detail = entry.Detail is { Length: > 0 } d ? d : null;

        return entry.Kind switch
        {
            ActivityKind.Shared => target is null
                ? $"{actor} shared {subject}"
                : $"{actor} shared {subject} with {target}",
            ActivityKind.Unshared => target is null
                ? $"{actor} stopped sharing {subject}"
                : $"{actor} stopped sharing {subject} with {target}",
            ActivityKind.PermissionsChanged => target is null
                ? $"{actor} changed who can do what on {subject}"
                : $"{actor} changed what {target} can do on {subject}",
            ActivityKind.VisibilityChanged => detail is null
                ? $"{actor} changed who can see {subject}"
                : $"{actor} set {subject} to {detail.ToLowerInvariant()}",
            ActivityKind.Uploaded => $"{actor} uploaded changes to {subject}",
            ActivityKind.VersionPublished => detail is null
                ? $"{actor} published a new version of {subject}"
                : $"{actor} published {subject} {detail}",
            ActivityKind.MemberAdded => target is null
                ? $"{actor} added somebody to {subject}"
                : $"{actor} added {target} to {subject}",
            ActivityKind.MemberRemoved => target is null
                ? $"{actor} removed somebody from {subject}"
                : $"{actor} removed {target} from {subject}",
            ActivityKind.RoleChanged => target is null
                ? $"{actor} changed a role on {subject}"
                : detail is null
                    ? $"{actor} changed {target}'s role on {subject}"
                    : $"{actor} made {target} {detail.ToLowerInvariant()} of {subject}",
            ActivityKind.InviteSent => target is null
                ? $"{actor} created an invite link for {subject}"
                : $"{actor} invited {target} to {subject}",
            ActivityKind.InviteAccepted => $"{actor} accepted an invitation to {subject}",
            ActivityKind.OwnershipTransferred => target is null
                ? $"{actor} handed {subject} to somebody else"
                : $"{actor} handed {subject} to {target}",
            _ => $"{actor} changed {subject}"
        };
    }

    /// <summary>"3 days ago", "yesterday", "just now": the same wording the pack cache uses.</summary>
    public static string When(DateTimeOffset at) => PackListCache.Describe(at) ?? "";

    /// <summary>The day heading a row sits under: "Today", "Yesterday", then the weekday, then
    /// the date.</summary>
    /// <remarks><see cref="TimeFormat.DayHeading"/> localises it and switches to the date after a
    /// week.</remarks>
    public static string DayHeading(DateTimeOffset at) => TimeFormat.DayHeading(at);

    public static string SubjectLabel(ActivitySubjectType type) => type switch
    {
        ActivitySubjectType.Pack => "Instance",
        ActivitySubjectType.Mod => "Mod",
        ActivitySubjectType.World => "World",
        ActivitySubjectType.ResourcePack => "Resource pack",
        ActivitySubjectType.Bundle => "Bundle",
        ActivitySubjectType.Team => "Team",
        _ => "Item"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
//  CACHE: works like Services/PackListCache.cs (per profile under DataRootPath, written
//  tmp-then-move, failures swallowed, CachedAt for the UI). SchemaVersion makes a cache from an
//  older record shape read as "no cache" instead of rows with empty columns.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The last page of <c>/activity/me</c>, so the Activity tab is readable offline.</summary>
public static class ActivityFeedCache
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();

    private sealed record Payload(int SchemaVersion, DateTimeOffset CachedAt, int Total, List<ActivityFeedEntry> Items);

    private static string FilePath => Path.Combine(AppSettings.DataRootPath, "sharing-activity-cache.json");

    public static DateTimeOffset? CachedAt
    {
        get
        {
            try { return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : null; }
            catch { return null; }
        }
    }

    public static string? AgeInWords() => PackListCache.Describe(CachedAt);

    /// <summary>Only ever the first page: this is what the tab opens on, not a local archive.</summary>
    public static void Save(ActivityFeedPage page)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var payload = new Payload(SchemaVersion, DateTimeOffset.UtcNow, page.Total, page.Items.ToList());
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch { /* a failed cache write must never fail the caller, as in PackListCache */ }
    }

    public static ActivityFeedPage? Load()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return null;
                var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(FilePath), Json);
                if (payload is null || payload.SchemaVersion != SchemaVersion) return null;
                return new ActivityFeedPage(payload.Items, 0, payload.Items.Count, payload.Total);
            }
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { lock (Gate) File.Delete(FilePath); } catch { /* nothing to do */ }
    }
}
