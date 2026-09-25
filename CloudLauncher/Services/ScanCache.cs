using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudLauncher.Services;

/// <summary>A cheap snapshot of one scanned root.</summary>
/// <param name="Exists">False when the folder doesn't exist, which is normal: an instance that has
/// never been launched has no <c>config/</c>.</param>
/// <param name="DirMtimeTicks">The root's own write time. Only changes when a direct child is
/// created, deleted or renamed.</param>
/// <param name="FileCount">Every file underneath, to the configured depth.</param>
/// <param name="DirCount">Every folder underneath.</param>
/// <param name="TotalBytes">Sum of every file's length.</param>
public readonly record struct RootStamp(
    bool Exists, long DirMtimeTicks, int FileCount, int DirCount, long TotalBytes)
{
    public static readonly RootStamp Missing = new(false, 0, 0, 0, 0);

    public override string ToString() =>
        Exists ? $"{DirMtimeTicks}:{FileCount}:{DirCount}:{TotalBytes}" : "-";
}

/// <summary>Builds the fingerprint string a scope is remembered under. Add the roots in the same
/// order every time or the cache never hits.</summary>
public sealed class Fingerprint
{
    private readonly List<string> _parts = ["v1"];

    public Fingerprint Add(string name, RootStamp stamp)
    {
        _parts.Add($"{name}:{stamp}");
        return this;
    }

    public Fingerprint Add(string name, string raw)
    {
        _parts.Add($"{name}:{raw}");
        return this;
    }

    public override string ToString() => string.Join('|', _parts);
}

/// <summary>Cheap directory fingerprinting shared by the scan caches.</summary>
/// <remarks>
/// <para>A file created, deleted or resized at any depth changes the count or byte total. An in-place
/// edit that keeps the file's length, or a same-length rename below the root, is missed; catching
/// those costs as much as the scan itself, so the Refresh button covers them.</para>
/// <para><see cref="Compute"/> walks the whole tree, so only run it when there is a cache to compare
/// with. <see cref="Shallow"/> is two syscalls and safe to run before serving a cache.</para>
/// <para>No FileSystemWatcher: a running Minecraft produces thousands of events a second.</para>
/// </remarks>
public static class RootFingerprint
{
    /// <summary>Recursive (exists, mtime, file count, dir count, total bytes) for one root.</summary>
    /// <param name="skipFolders">Folder names never descended into (KubeJS's <c>probe_dumps</c> alone
    /// holds tens of thousands of generated files). Must match what the scan skips, or the fingerprint
    /// describes a different tree.</param>
    /// <param name="maxDepth">Folders below this are counted but not descended into.</param>
    public static RootStamp Compute(string root, IReadOnlySet<string>? skipFolders, int maxDepth,
                                    CancellationToken ct)
    {
        if (!SafeExists(root)) return RootStamp.Missing;

        long mtime;
        try { mtime = Directory.GetLastWriteTimeUtc(root).Ticks; }
        catch { mtime = 0; }

        int files = 0, dirs = 0;
        long bytes = 0;

        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();

            FileSystemInfo[] entries;
            // GetFileSystemInfos throws up front rather than part-way through an enumeration, so
            // one unreadable folder skips itself instead of aborting the whole root.
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch { continue; }

            foreach (var info in entries)
            {
                try
                {
                    // Attributes and Length come with the enumeration, so reading them costs nothing extra.
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    if (info is DirectoryInfo sub)
                    {
                        dirs++;
                        if (skipFolders is not null && skipFolders.Contains(sub.Name)) continue;
                        if (depth + 1 <= maxDepth) stack.Push((sub.FullName, depth + 1));
                    }
                    else if (info is FileInfo file)
                    {
                        files++;
                        bytes += file.Length;
                    }
                }
                catch { /* file vanished mid-walk */ }
            }
        }

        return new RootStamp(true, mtime, files, dirs, bytes);
    }

    /// <summary>Exists plus the folder's own write time: two syscalls, safe to call synchronously
    /// before serving a remembered list.</summary>
    public static (bool Exists, long MtimeTicks) Shallow(string root)
    {
        if (!SafeExists(root)) return (false, 0);
        try { return (true, Directory.GetLastWriteTimeUtc(root).Ticks); }
        catch { return (true, 0); }
    }

    /// <summary>A stamp for one file, by (write time, length). Unlike a folder stamp this catches
    /// in-place edits. Meant for small single files like <c>config/iris.properties</c>,
    /// <c>options.txt</c> and <c>servers.dat</c>.</summary>
    public static string File(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "-";
        }
        catch { return "?"; }
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }
}

/// <summary>Whether a scope had anything remembered for it.</summary>
public enum ScanState
{
    /// <summary>Nothing remembered. The page should show that it is scanning rather than a number.</summary>
    Cold,
    /// <summary>Rows from the last walk, true as of <see cref="CachedScan{T}.ScannedUtc"/>.</summary>
    Cached
}

/// <summary>What a cache read produced. Never null, never throws, never touches the disk.</summary>
/// <param name="Rows">The remembered rows; empty when <paramref name="State"/> is Cold.</param>
/// <param name="ScannedUtc">When the walk that produced them ran.</param>
/// <param name="Fingerprint">What the disk looked like then, for the rescan to compare against.
/// Null means "walk everything", which is how an expired entry forces a full re-read.</param>
/// <param name="Overflow">True when the last walk found more rows than the cache will hold. The
/// rows are then left out rather than truncated.</param>
/// <param name="ItemCount">How many rows the last walk found, including an overflowed one.</param>
public sealed record CachedScan<T>(
    ScanState State,
    IReadOnlyList<T> Rows,
    DateTimeOffset? ScannedUtc,
    string? Fingerprint,
    bool Overflow = false,
    int ItemCount = 0)
{
    /// <summary>Nothing remembered.</summary>
    public static readonly CachedScan<T> Cold = new(ScanState.Cold, [], null, null);

    /// <summary>"just now", "3 hours ago", "on 14 Sep", worded the same as the instance-list cache.</summary>
    public string? AgeInWords => PackListCache.Describe(ScannedUtc);
}

/// <summary>Which kinds of remembered scan a mutation invalidates.</summary>
[Flags]
public enum ScanScope
{
    None = 0,
    /// <summary>config/, kubejs/, defaultconfigs/, logs/kubejs/, and the cross-instance compare.</summary>
    Config = 1,
    Worlds = 2,
    ResourcePacks = 4,
    Shaders = 8,
    Servers = 16,
    Mods = 32,
    /// <summary>The instance's file list / sync manifest.</summary>
    Files = 64,
    All = ~0
}

/// <summary>The cache names. Each is also its file name under the profile's <c>scan/</c> folder, so
/// keep them filename-safe and define new ones here.</summary>
public static class ScanKinds
{
    public const string ConfigHub = "config-hub";
    /// <summary>Cross-instance: which shared paths differ. Not keyed per instance (see
    /// <see cref="ScanCaches.For{T}"/>'s <c>crossInstance</c>).</summary>
    public const string ConfigGroups = "config-groups";
    public const string KubeJsErrors = "kubejs-errors";
    public const string Worlds = "worlds";
    public const string ResourcePacks = "resourcepacks";
    public const string Shaders = "shaderpacks";
    public const string Servers = "serverlists";
    public const string Mods = "mods";
    public const string PackFiles = "packfiles";
    /// <summary>Cross-instance: how much space everything takes. Nearly every scope invalidates it,
    /// since any content change moves the number.</summary>
    public const string StorageUsage = "storage-usage";
}

/// <summary>The non-generic half of <see cref="ScanCache{T}"/>, so the registry can drive every
/// cache without knowing any row type.</summary>
internal interface IScanCache
{
    string Kind { get; }
    void EnsureLoaded();
    void InvalidatePack(Guid packId);
    void Clear();
    void Flush();
}

/// <summary>A per-profile, on-disk memory of one kind of local-disk scan, so reopening a page paints
/// what it found last time in its first frame.</summary>
/// <remarks>
/// <para><see cref="Get"/> returns remembered rows at once and never blocks; pages still rescan in
/// the background and <see cref="Put"/> when the fingerprint moved, so a missed invalidation costs a
/// fingerprint comparison, not a wrong screen.</para>
/// <para>A file whose <see cref="SchemaVersion"/> or <typeparamref name="T"/> signature differs is
/// discarded, not migrated. Serializer options are fixed here
/// (<see cref="JsonSerializerDefaults.Web"/>).</para>
/// <para>Thread-safe. Reads and writes touch memory under one lock; serialisation runs on a
/// debounced timer or at process exit, never on the dispatcher.</para>
/// </remarks>
/// <typeparam name="T">The persisted row. Keep it slim and never hold a <c>BitmapImage</c>; cache
/// "has an icon" and decode lazily.</typeparam>
public sealed class ScanCache<T> : IScanCache
{
    /// <summary>Bump when the file's own shape changes. Row type changes are caught by the row
    /// signature.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Per-file ceiling. Past it the largest scope is dropped and the write retried.</summary>
    private const int MaxBytes = 4 * 1024 * 1024;

    private const int FlushDelayMs = 2000;

    /// <summary>A read only re-stamps "last used" when it has drifted this far, so ordinary page
    /// opens do not mark the cache dirty and rewrite it every few seconds.</summary>
    private static readonly TimeSpan TouchGranularity = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.Ordinal);
    private readonly int _rowVersion;
    private readonly int _maxRows;
    private readonly int _maxScopes;
    private readonly TimeSpan _maxAge;
    private readonly bool _crossInstance;
    private readonly bool _persist;
    private readonly string _rowShape;

    private bool _loaded;
    private bool _dirty;
    private Timer? _flushTimer;

    internal ScanCache(string kind, int rowVersion, int maxRowsPerScope, int maxScopes,
                       TimeSpan? maxAge, bool crossInstance, bool persist)
    {
        Kind = kind;
        _rowVersion = rowVersion;
        _maxRows = maxRowsPerScope;
        _maxScopes = maxScopes;
        // Hard age limit on top of the fingerprint, so a change the fingerprint can't see is stale for at
        // most two weeks.
        _maxAge = maxAge ?? TimeSpan.FromDays(14);
        _crossInstance = crossInstance;
        _persist = persist;
        _rowShape = RowShape.Of(typeof(T));
    }

    /// <summary>The cache's name, which is also its file: <c>scan/{Kind}.json</c>.</summary>
    public string Kind { get; }

    /// <summary>The scope key every cache uses. Keyed by instance id rather than name, so renaming an
    /// instance folder doesn't orphan the cache.</summary>
    public static string ScopeKey(Guid packId, string kind = "") =>
        kind.Length == 0 ? packId.ToString("N") : $"{packId:N}:{kind}";

    private string FilePath =>
        Path.Combine(AppSettings.DataRootPath, "scan", Kind + ".json");

    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>What was remembered for this scope. Never throws.</summary>
    /// <remarks>The first call reads the file, so warm the cache with <see cref="EnsureLoadedAsync"/>
    /// (or <see cref="ScanCaches.PreloadAsync"/>) before calling this on the UI thread.</remarks>
    public CachedScan<T> Get(string scopeKey)
    {
        try
        {
            EnsureLoaded();
            lock (_gate)
            {
                if (!_scopes.TryGetValue(scopeKey, out var scope)) return CachedScan<T>.Cold;

                var now = DateTimeOffset.UtcNow;
                if (now - scope.LastUsedUtc > TouchGranularity)
                {
                    scope.LastUsedUtc = now;
                    _dirty = true;
                }

                // Expired rows are still returned (better than a blank page), but without the fingerprint, so
                // the rescan walks everything.
                var expired = now - scope.ScannedUtc > _maxAge;

                if (scope.Overflow)
                    return new CachedScan<T>(ScanState.Cold, [], scope.ScannedUtc, null,
                                             Overflow: true, ItemCount: scope.ItemCount);

                return new CachedScan<T>(ScanState.Cached, scope.Rows, scope.ScannedUtc,
                                         expired ? null : scope.Fingerprint,
                                         Overflow: false, ItemCount: scope.ItemCount);
            }
        }
        catch { return CachedScan<T>.Cold; }
    }

    // ── writing ──────────────────────────────────────────────────────────────

    /// <summary>Records a completed walk.</summary>
    /// <param name="fingerprint">What the disk looked like when these rows were read, from
    /// <see cref="Fingerprint"/>. The next rescan compares against it.</param>
    /// <remarks>A scope with more rows than the cache holds is recorded as overflowed rather than
    /// truncated, so a partial list is never shown as the whole list.</remarks>
    public void Put(string scopeKey, IReadOnlyList<T> rows, string fingerprint)
    {
        try
        {
            EnsureLoaded();
            lock (_gate)
            {
                var over = rows.Count > _maxRows;
                _scopes[scopeKey] = new Scope
                {
                    // Snapshot: the caller keeps mutating its own list after this returns.
                    Rows = over ? Array.Empty<T>() : rows.ToArray(),
                    Fingerprint = fingerprint,
                    ScannedUtc = DateTimeOffset.UtcNow,
                    LastUsedUtc = DateTimeOffset.UtcNow,
                    ItemCount = rows.Count,
                    Overflow = over
                };
                EvictLocked();
                _dirty = true;
            }
            ArmFlush();
        }
        catch { /* a cache that cannot be written is not worth failing a scan over */ }
    }

    /// <summary>Forgets one scope, so the next rescan walks it instead of comparing fingerprints.
    /// Call it after the launcher's own writes: overwriting a file doesn't change any folder's
    /// timestamp.</summary>
    public void Invalidate(string scopeKey)
    {
        try
        {
            EnsureLoaded();
            lock (_gate)
            {
                if (!_scopes.Remove(scopeKey)) return;
                _dirty = true;
            }
            ArmFlush();
        }
        catch { /* nothing to do */ }
    }

    /// <summary>Forgets every scope belonging to one instance. A cross-instance cache (the config
    /// compare, whose every entry spans all of them) forgets everything instead.</summary>
    public void InvalidatePack(Guid packId)
    {
        try
        {
            EnsureLoaded();
            var prefix = packId.ToString("N");
            lock (_gate)
            {
                if (_crossInstance)
                {
                    if (_scopes.Count == 0) return;
                    _scopes.Clear();
                }
                else
                {
                    var hits = _scopes.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                    if (hits.Count == 0) return;
                    foreach (var key in hits) _scopes.Remove(key);
                }
                _dirty = true;
            }
            ArmFlush();
        }
        catch { /* nothing to do */ }
    }

    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                _scopes.Clear();
                _loaded = true;
                _dirty = true;
            }
            ArmFlush();
        }
        catch { /* nothing to do */ }
    }

    // ── persistence ──────────────────────────────────────────────────────────

    /// <summary>Reads the file on a thread-pool thread. Call this before the first
    /// <see cref="Get"/> if that first Get is on the UI thread.</summary>
    public Task EnsureLoadedAsync()
    {
        lock (_gate)
        {
            if (_loaded) return Task.CompletedTask;
        }
        return Task.Run(EnsureLoaded);
    }

    void IScanCache.EnsureLoaded() => EnsureLoaded();

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            if (!_persist) return;

            try
            {
                var path = FilePath;
                if (!System.IO.File.Exists(path)) return;

                var file = JsonSerializer.Deserialize<ScanCacheFile>(System.IO.File.ReadAllText(path), Json);

                // Treat a file that deserialises to all defaults the same as no file.
                if (file is null || file.Scopes is null || file.Scopes.Count == 0) return;

                if (file.SchemaVersion != SchemaVersion || file.RowVersion != _rowVersion
                    || !string.Equals(file.Kind, Kind, StringComparison.Ordinal)
                    || !string.Equals(file.RowShape, _rowShape, StringComparison.Ordinal))
                {
                    // Not migrated: a re-walk rebuilds all of it.
                    AppLog.Log("ScanCache", $"{Kind}: remembered scans were written by a different "
                                          + "version of this row and have been discarded.");
                    TryDelete(path);
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var (key, dto) in file.Scopes)
                {
                    if (dto is null) continue;
                    if (now - dto.ScannedUtc > _maxAge) continue;   // aged out; do not carry it forward
                    _scopes[key] = new Scope
                    {
                        Rows = dto.Rows ?? new List<T>(),
                        Fingerprint = dto.Fingerprint ?? "",
                        ScannedUtc = dto.ScannedUtc,
                        LastUsedUtc = dto.LastUsedUtc == default ? dto.ScannedUtc : dto.LastUsedUtc,
                        ItemCount = dto.ItemCount,
                        Overflow = dto.Overflow
                    };
                }
                EvictLocked();
            }
            catch
            {
                // Corrupt cache: start fresh rather than fail the page.
                _scopes.Clear();
            }
        }
    }

    /// <summary>Writes to disk if anything changed. Debounced internally, so call it directly only
    /// at process exit. Never runs on the dispatcher.</summary>
    public void Flush()
    {
        if (!_persist) return;
        try
        {
            ScanCacheFile file;
            lock (_gate)
            {
                if (!_dirty) return;
                _dirty = false;
                file = SnapshotLocked();
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json);

            // Up to three attempts, each dropping the largest scope, then give up.
            for (var attempt = 0; attempt < 3 && bytes.Length > MaxBytes; attempt++)
            {
                var biggest = file.Scopes.OrderByDescending(kv => kv.Value.Rows.Count)
                                         .Select(kv => kv.Key).FirstOrDefault();
                if (biggest is null) break;
                file.Scopes.Remove(biggest);
                lock (_gate) _scopes.Remove(biggest);   // or the next flush would just re-add it
                AppLog.Log("ScanCache", $"{Kind}: dropped the largest remembered scan to stay under "
                                      + $"{MaxBytes / (1024 * 1024)} MB.");
                bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json);
            }
            if (bytes.Length > MaxBytes) return;

            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            System.IO.File.WriteAllBytes(tmp, bytes);
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Don't fail the request, but mark it dirty again so the process-exit flush retries.
            lock (_gate) _dirty = true;
        }
    }

    private void ArmFlush()
    {
        if (!_persist) return;
        try
        {
            lock (_gate)
            {
                _flushTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                _flushTimer.Change(FlushDelayMs, Timeout.Infinite);
            }
        }
        catch { /* the process-exit flush is the backstop */ }
    }

    private ScanCacheFile SnapshotLocked()
    {
        var file = new ScanCacheFile
        {
            SchemaVersion = SchemaVersion,
            Kind = Kind,
            RowVersion = _rowVersion,
            RowShape = _rowShape
        };
        foreach (var (key, scope) in _scopes)
        {
            file.Scopes[key] = new ScanScopeFile
            {
                Fingerprint = scope.Fingerprint,
                ScannedUtc = scope.ScannedUtc,
                LastUsedUtc = scope.LastUsedUtc,
                ItemCount = scope.ItemCount,
                Overflow = scope.Overflow,
                Rows = [.. scope.Rows]
            };
        }
        return file;
    }

    /// <summary>Drops the least recently used scopes past the cap. Ordered by last read, not last
    /// write, so the instances people open are the ones kept.</summary>
    private void EvictLocked()
    {
        if (_scopes.Count <= _maxScopes) return;
        foreach (var key in _scopes.OrderBy(kv => kv.Value.LastUsedUtc)
                                   .Take(_scopes.Count - _maxScopes)
                                   .Select(kv => kv.Key).ToList())
            _scopes.Remove(key);
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch { /* nothing to do */ }
    }

    // ── shapes ───────────────────────────────────────────────────────────────

    private sealed class Scope
    {
        public IReadOnlyList<T> Rows { get; init; } = [];
        public string Fingerprint { get; init; } = "";
        public DateTimeOffset ScannedUtc { get; init; }
        public DateTimeOffset LastUsedUtc { get; set; }
        public int ItemCount { get; init; }
        public bool Overflow { get; init; }
    }

    private sealed class ScanCacheFile
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = "";
        public int RowVersion { get; set; }
        public string RowShape { get; set; } = "";
        public Dictionary<string, ScanScopeFile> Scopes { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ScanScopeFile
    {
        public string Fingerprint { get; set; } = "";
        public DateTimeOffset ScannedUtc { get; set; }
        public DateTimeOffset LastUsedUtc { get; set; }
        public int ItemCount { get; set; }
        public bool Overflow { get; set; }
        public List<T> Rows { get; set; } = [];
    }
}

/// <summary>A structural signature of a cached row type, so a launcher update that changes a row's
/// shape discards old entries instead of deserialising garbage into them.</summary>
/// <remarks>Catches added, removed, renamed or retyped public members. A change of meaning with the
/// same shape (seconds to milliseconds) needs the per-cache <c>rowVersion</c> instead.</remarks>
internal static class RowShape
{
    public static string Of(Type type)
    {
        var sb = new StringBuilder();
        Describe(type, sb, depth: 0, new HashSet<Type>());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static void Describe(Type type, StringBuilder sb, int depth, HashSet<Type> seen)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) { sb.Append('?'); type = underlying; }

        if (depth > 3 || type.IsPrimitive || type.IsEnum || type == typeof(string)
            || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
        {
            sb.Append(type.FullName ?? type.Name);
            // A generic collection's element type is part of the row's shape.
            if (type.IsGenericType)
                foreach (var arg in type.GetGenericArguments())
                {
                    sb.Append('<');
                    Describe(arg, sb, depth + 1, seen);
                    sb.Append('>');
                }
            return;
        }

        if (!seen.Add(type)) { sb.Append('@').Append(type.Name); return; }

        sb.Append(type.Name).Append('{');
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;
        foreach (var member in type.GetProperties(Flags)
                                   .Select(p => (p.Name, Type: p.PropertyType))
                                   .Concat(type.GetFields(Flags).Select(f => (f.Name, Type: f.FieldType)))
                                   .OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            sb.Append(member.Name).Append(':');
            Describe(member.Type, sb, depth + 1, seen);
            sb.Append(';');
        }
        sb.Append('}');
        seen.Remove(type);
    }
}

/// <summary>Every remembered scan in the launcher, and the one entry point a mutation calls.</summary>
/// <remarks>Most invalidation comes from three places: <c>PackJobs.Finish</c> (Download and Sync
/// invalidate <see cref="ScanScope.All"/>; Upload only reads), the game process exiting in
/// <c>LaunchService</c>, and the targeted invalidations in the content services, which forward to
/// <see cref="InvalidatePack"/>. External edits are caught by the fingerprint comparison or the
/// Refresh button.</remarks>
public static class ScanCaches
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IScanCache> Registry = new(StringComparer.Ordinal);

    /// <summary>Which caches a scope covers. Config carries three because the compare and the
    /// KubeJS log parse are derived from the same folders as the walk.</summary>
    private static readonly (ScanScope Scope, string[] Kinds)[] Coverage =
    [
        (ScanScope.Config, [ScanKinds.ConfigHub, ScanKinds.ConfigGroups, ScanKinds.KubeJsErrors,
                            ScanKinds.StorageUsage]),
        (ScanScope.Worlds, [ScanKinds.Worlds, ScanKinds.StorageUsage]),
        (ScanScope.ResourcePacks, [ScanKinds.ResourcePacks, ScanKinds.StorageUsage]),
        // DetectLoader reads mods/ to find an instance's shader loader, so the Mods scope below covers
        // Shaders too.
        (ScanScope.Shaders, [ScanKinds.Shaders, ScanKinds.StorageUsage]),
        // A server list is a few lines of text, so it alone does not move the storage number.
        (ScanScope.Servers, [ScanKinds.Servers]),
        (ScanScope.Mods, [ScanKinds.Mods, ScanKinds.Shaders, ScanKinds.StorageUsage]),
        (ScanScope.Files, [ScanKinds.PackFiles, ScanKinds.StorageUsage]),
    ];

    static ScanCaches()
    {
        // One handler for every cache. ModFingerprintCache isn't a ScanCache and installs its own.
        try { AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAll(); }
        catch { /* a host that will not let us hook exit still gets the debounced flush */ }
    }

    /// <summary>The cache for one content kind, created on first use. Keep the result in a
    /// <c>static readonly</c> field on the owning service so it registers before any page asks.</summary>
    /// <param name="kind">One of <see cref="ScanKinds"/>. It is the file name.</param>
    /// <param name="rowVersion">Bump only when a row's meaning changes without its shape changing;
    /// shape changes are detected automatically.</param>
    /// <param name="crossInstance">True for a cache whose entries span every instance (the config
    /// compare). Any instance's mutation clears those.</param>
    /// <remarks>Options only apply on the call that creates the cache. Asking for an existing kind with
    /// a different row type returns a non-persisting cache and logs it, so two types never share one
    /// file.</remarks>
    public static ScanCache<T> For<T>(string kind, int rowVersion = 1, int maxRowsPerScope = 20_000,
                                      int maxScopes = 64, TimeSpan? maxAge = null,
                                      bool crossInstance = false)
    {
        lock (Gate)
        {
            if (Registry.TryGetValue(kind, out var existing))
            {
                if (existing is ScanCache<T> match) return match;
                AppLog.Log("ScanCache", $"'{kind}' is already registered for a different row type; "
                                      + "this caller gets a cache that never persists.");
                return new ScanCache<T>(kind, rowVersion, maxRowsPerScope, maxScopes, maxAge,
                                        crossInstance, persist: false);
            }

            var created = new ScanCache<T>(kind, rowVersion, maxRowsPerScope, maxScopes, maxAge,
                                           crossInstance, persist: true);
            Registry[kind] = created;
            return created;
        }
    }

    /// <summary>Reads every registered cache's file on a thread-pool thread. Await it once before
    /// a page's first-frame paint so no <see cref="ScanCache{T}.Get"/> touches the disk on the UI
    /// thread.</summary>
    public static Task PreloadAsync() => Task.Run(() =>
    {
        foreach (var cache in Snapshot())
            try { cache.EnsureLoaded(); } catch { /* one bad file must not stop the rest */ }
    });

    /// <summary>The one call every mutation site makes.</summary>
    public static void InvalidatePack(Guid packId, ScanScope scopes)
    {
        if (scopes == ScanScope.None) return;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (scope, names) in Coverage)
            if ((scopes & scope) != 0) foreach (var name in names) kinds.Add(name);

        foreach (var cache in Snapshot())
            if (kinds.Contains(cache.Kind))
                try { cache.InvalidatePack(packId); } catch { /* best effort */ }
    }

    /// <summary>Forgets everything, for "the instance list changed underneath us" and for tests.</summary>
    public static void ClearAll()
    {
        foreach (var cache in Snapshot())
            try { cache.Clear(); } catch { /* best effort */ }
    }

    /// <summary>Writes every dirty cache. Registered on process exit; safe to call again.</summary>
    public static void FlushAll()
    {
        foreach (var cache in Snapshot())
            try { cache.Flush(); } catch { /* best effort */ }
    }

    private static List<IScanCache> Snapshot()
    {
        lock (Gate) return [.. Registry.Values];
    }
}
