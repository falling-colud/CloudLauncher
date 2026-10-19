using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>Where one file a modpack import could not download stands.</summary>
public enum ManualDownloadState
{
    /// <summary>Not in the instance yet.</summary>
    Waiting = 0,
    /// <summary>Put in place, by the launcher or by hand.</summary>
    Added,
    /// <summary>The user said the pack can do without it.</summary>
    Skipped
}

/// <summary>
/// One file of a CurseForge modpack that the launcher could not download, with what it takes to get it
/// by hand: the project's download page, and the size and hashes that recognise the file once it is in
/// the Downloads folder.
/// </summary>
/// <remarks>Read back from the instance folder, which a shared instance syncs, so every field is checked
/// again before it names a folder, a file or a link (see <see cref="IsUsable"/>).</remarks>
public sealed class ManualDownloadEntry
{
    public int ProjectId { get; set; }
    public int FileId { get; set; }

    /// <summary>The project's name, e.g. "Fresh Animations".</summary>
    public string Name { get; set; } = "";

    /// <summary>The file's name on CurseForge, which is also the name it is saved under.</summary>
    public string FileName { get; set; } = "";

    public long Size { get; set; }

    /// <summary>CurseForge's murmur2 fingerprint; 0 when unknown.</summary>
    public long Fingerprint { get; set; }

    /// <summary>Lower-case hex SHA-1; null when unknown.</summary>
    public string? Sha1 { get; set; }

    /// <summary><c>mods</c>, <c>resourcepacks</c> or <c>shaderpacks</c>, from the project's class.</summary>
    public string Folder { get; set; } = "mods";

    /// <summary>The pack ships this mod switched off, so it goes in as <c>.jar.disabled</c>.</summary>
    public bool Disabled { get; set; }

    /// <summary>The project's page on curseforge.com.</summary>
    public string? ProjectUrl { get; set; }

    public string? IconUrl { get; set; }

    /// <summary>True when the author turned off downloads outside CurseForge's own site and app;
    /// false when the download just failed.</summary>
    public bool OptedOut { get; set; }

    /// <summary>Why the launcher could not fetch it, for a failure other than the opt-out.</summary>
    public string? Reason { get; set; }

    public ManualDownloadState State { get; set; }

    /// <summary>The folders a modpack can route a file to.</summary>
    public static readonly string[] Folders = ["mods", "resourcepacks", "shaderpacks"];

    /// <summary>The name the file has inside the instance.</summary>
    [JsonIgnore]
    public string TargetFileName => Disabled && Folder == "mods" ? FileName + ".disabled" : FileName;

    /// <summary>A record that names a known folder and a plain file name. Anything else is left out of
    /// every list rather than acted on.</summary>
    [JsonIgnore]
    public bool IsUsable =>
        FileId > 0 && Folders.Contains(Folder) && PathSafety.IsSafeFileName(FileName);

    /// <summary>The project page, when it is a curseforge.com address; CurseForge's id redirect
    /// otherwise.</summary>
    [JsonIgnore]
    public string PageUrl =>
        IsCurseForgePage(ProjectUrl) ? ProjectUrl!.TrimEnd('/') : $"https://www.curseforge.com/projects/{ProjectId}";

    /// <summary>The website's download page for exactly this file. It starts the download by itself
    /// after a few seconds, the same way the site's own Download button does.</summary>
    [JsonIgnore]
    public string DownloadPageUrl =>
        IsCurseForgePage(ProjectUrl)
            ? $"{ProjectUrl!.TrimEnd('/')}/download/{FileId}"
            : $"https://www.curseforge.com/projects/{ProjectId}";

    private static bool IsCurseForgePage(string? url) =>
        SafeLaunch.IsWebUrl(url, out var uri)
        && uri!.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("www.curseforge.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

/// <summary>Everything an import left to download by hand, kept in the instance folder.</summary>
public sealed class ManualDownloadList
{
    public int Version { get; set; } = 1;

    /// <summary>The modpack file the list came from.</summary>
    public string? Source { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ManualDownloadEntry> Files { get; set; } = new();
}

/// <summary>
/// Files a CurseForge modpack lists that the launcher may not download (authors can turn off downloads
/// outside CurseForge's own site and app), kept per instance until they are in place.
/// </summary>
/// <remarks>
/// <para>The import records them (<see cref="Record"/>) and raises <see cref="Needed"/>; the card that
/// answers it sends the user to each file's download page and puts the downloaded files in place
/// (<see cref="Place"/>), recognising them by size and fingerprint so a browser's renamed copy still
/// counts.</para>
/// <para>A file counts as added once it is in its folder under its own name, however it got there
/// (<see cref="Reconcile"/>), so the list never nags about a file the user dropped in themselves.</para>
/// </remarks>
public sealed class ManualDownloadService(PackFolderService packs, AppSettings settings)
{
    /// <summary>The list's file, in the instance's <c>game/.cloudlauncher</c> folder.</summary>
    public const string ListFileName = "manual-downloads.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    /// <summary>An import finished with files left to download by hand: the instance and its name.
    /// Raised on the UI thread.</summary>
    public event Action<Guid, string>? Needed;

    /// <summary>An instance's list changed (a file added or skipped). Raised on the UI thread.</summary>
    public event Action<Guid>? Changed;

    private string ListPath(Guid packId) => Path.Combine(packs.GameDir(packId), ".cloudlauncher", ListFileName);

    /// <summary>The instance's list as stored, or null when it has none.</summary>
    public ManualDownloadList? Load(Guid packId)
    {
        try
        {
            var path = ListPath(packId);
            if (!File.Exists(path)) return null;
            lock (_gate)
            {
                var list = JsonSerializer.Deserialize<ManualDownloadList>(File.ReadAllText(path), Json);
                if (list is null) return null;
                list.Files.RemoveAll(f => f is null || !f.IsUsable);
                return list;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Log("manual-downloads", $"Could not read the list for {packId}: {ex.Message}");
            return null;
        }
    }

    private void Save(Guid packId, ManualDownloadList list)
    {
        var path = ListPath(packId);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Starts the instance's list over with what an import could not download. An import
    /// with nothing left over removes the list, so a re-import that got everything clears it.</summary>
    public void Record(Guid packId, string? source, IReadOnlyCollection<ManualDownloadEntry> files)
    {
        try
        {
            if (files.Count == 0)
            {
                var path = ListPath(packId);
                lock (_gate) { if (File.Exists(path)) File.Delete(path); }
            }
            else
            {
                Save(packId, new ManualDownloadList
                {
                    Source = source,
                    Files = files.Where(f => f.IsUsable).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log("manual-downloads", $"Could not save the list for {packId}: {ex.Message}");
        }
        RaiseChanged(packId);
    }

    /// <summary>Marks every waiting file that is already in place as added, saves when that changed
    /// anything, and returns the list (null when the instance has none).</summary>
    public ManualDownloadList? Reconcile(Guid packId)
    {
        var list = Load(packId);
        if (list is null) return null;
        var changed = false;
        foreach (var file in list.Files.Where(f => f.State == ManualDownloadState.Waiting))
        {
            if (!IsInPlace(packId, file)) continue;
            file.State = ManualDownloadState.Added;
            changed = true;
        }
        if (changed)
        {
            try { Save(packId, list); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Log("manual-downloads", $"Could not save the list for {packId}: {ex.Message}");
            }
            RaiseChanged(packId);
        }
        return list;
    }

    /// <summary>The files still to get, after <see cref="Reconcile"/>.</summary>
    public IReadOnlyList<ManualDownloadEntry> Waiting(Guid packId) =>
        Reconcile(packId)?.Files.Where(f => f.State == ManualDownloadState.Waiting).ToList()
        ?? (IReadOnlyList<ManualDownloadEntry>)[];

    /// <summary>True when the file is in its folder, switched on or off.</summary>
    public bool IsInPlace(Guid packId, ManualDownloadEntry file)
    {
        if (!file.IsUsable) return false;
        try
        {
            var dir = Path.Combine(packs.GameDir(packId), file.Folder);
            return File.Exists(Path.Combine(dir, file.FileName))
                   || File.Exists(Path.Combine(dir, file.FileName + ".disabled"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Sets one file's state (skip it, or want it again) and saves.</summary>
    public void SetState(Guid packId, int fileId, ManualDownloadState state)
    {
        var list = Load(packId);
        var file = list?.Files.FirstOrDefault(f => f.FileId == fileId);
        if (list is null || file is null || file.State == state) return;
        file.State = state;
        try { Save(packId, list); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log("manual-downloads", $"Could not save the list for {packId}: {ex.Message}");
        }
        RaiseChanged(packId);
    }

    /// <summary>
    /// Copies (or moves) a downloaded file into the instance under the name the pack expects, and marks
    /// it added. The caller has already checked it with <see cref="Matches"/>.
    /// </summary>
    /// <returns>Where the file now is in the instance.</returns>
    public string Place(Guid packId, ManualDownloadEntry file, string source, bool removeSource)
    {
        if (!file.IsUsable) throw new InvalidOperationException("That entry does not name a usable file.");
        var dir = Path.Combine(packs.GameDir(packId), file.Folder);
        var dest = PathSafety.ResolveFileName(dir, file.TargetFileName)
                   ?? throw new InvalidOperationException($"{file.FileName} is not a usable file name.");
        Directory.CreateDirectory(dir);

        if (!SamePath(source, dest))
        {
            // Copied beside the target first, so a half-written jar never sits in mods/.
            var temp = dest + ".cl-part";
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, dest, overwrite: true);
            if (removeSource)
            {
                try { File.Delete(source); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Log("manual-downloads", $"Added {file.FileName} but could not remove {source}: {ex.Message}");
                }
            }
        }

        AppLog.Log("manual-downloads", $"Added {file.FileName} to {packId} from {source}.");
        SetState(packId, file.FileId, ManualDownloadState.Added);
        return dest;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Tells whoever shows the card that an import left files to download by hand.</summary>
    public void RaiseNeeded(Guid packId, string packName) => OnUi(() => Needed?.Invoke(packId, packName));

    private void RaiseChanged(Guid packId) => OnUi(() => Changed?.Invoke(packId));

    // ── recognising a downloaded file ─────────────────────────────────────────

    /// <summary>The hashes <see cref="Matches"/> compares: SHA-1 and CurseForge's fingerprint, from
    /// one read of the file.</summary>
    public static (string Sha1, long Fingerprint) Hash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var normalized = new MemoryStream();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            sha.AppendData(buffer.AsSpan(0, read));
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b is 9 or 10 or 13 or 32) continue;
                normalized.WriteByte(b);
            }
        }
        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(),
            CurseForgeService.MurmurHash2Normalized(normalized));
    }

    /// <summary>
    /// True when a file with this name, size and hashes is the one <paramref name="file"/> wants. The
    /// fingerprint decides when CurseForge gave one, then the SHA-1; only a file CurseForge gave neither
    /// for falls back to the name. The size has to match whenever it is known.
    /// </summary>
    public static bool Matches(ManualDownloadEntry file, string name, long length, string sha1, long fingerprint)
    {
        if (file.Size > 0 && length != file.Size) return false;
        if (file.Fingerprint != 0) return fingerprint == file.Fingerprint;
        if (!string.IsNullOrEmpty(file.Sha1)) return string.Equals(sha1, file.Sha1, StringComparison.OrdinalIgnoreCase);
        return string.Equals(name, file.FileName, StringComparison.OrdinalIgnoreCase);
    }

    // ── where downloads land ──────────────────────────────────────────────────

    /// <summary>The folder the card watches: the user's choice, else the Windows Downloads folder.</summary>
    public string WatchFolder =>
        settings.ManualDownloadFolder is { Length: > 0 } chosen && Directory.Exists(chosen)
            ? chosen
            : DownloadsFolder();

    /// <summary>Whether a file the card takes in is removed from the watched folder.</summary>
    public bool RemoveAfterAdding
    {
        get => settings.ManualDownloadRemoveAfterAdding;
        set
        {
            if (settings.ManualDownloadRemoveAfterAdding == value) return;
            settings.ManualDownloadRemoveAfterAdding = value;
            settings.Save();
        }
    }

    /// <summary>Remembers a folder to watch instead of Downloads; null goes back to Downloads.</summary>
    public void SetWatchFolder(string? folder)
    {
        settings.ManualDownloadFolder = string.IsNullOrWhiteSpace(folder)
            || SamePath(folder, DownloadsFolder()) ? null : folder;
        settings.Save();
    }

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>The Downloads folder as Windows has it, which a user may have moved off the profile
    /// folder; <c>%USERPROFILE%\Downloads</c> when Windows will not say.</summary>
    public static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            /* not Windows: fall through */
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private static void OnUi(Action raise)
    {
        var app = System.Windows.Application.Current;
        if (app is null) { raise(); return; }
        if (app.Dispatcher.CheckAccess()) raise();
        else app.Dispatcher.BeginInvoke(raise);
    }
}

/// <summary>
/// Watches one folder for the files a <see cref="ManualDownloadService"/> list is waiting for, and
/// reports each one it recognises once.
/// </summary>
/// <remarks>
/// <para>Browsers write a download under a temporary name and rename it when it is done, so the watcher
/// reacts to renames as well as new files, and only looks at files whose size is one it is waiting for,
/// which also passes over a download still being written. Hashing happens only for those.</para>
/// <para>File-system events can be dropped (a full buffer, a network folder), so a slow poll runs
/// alongside them.</para>
/// </remarks>
public sealed class ManualDownloadWatcher : IDisposable
{
    private static readonly string[] PartialSuffixes = [".crdownload", ".part", ".partial", ".download", ".tmp", ".opdownload"];

    private readonly string _folder;
    private readonly Func<IReadOnlyList<ManualDownloadEntry>> _waiting;
    private readonly Action<ManualDownloadEntry, string> _found;
    private readonly FileSystemWatcher? _fsw;
    private readonly System.Threading.Timer _poll;
    private readonly System.Threading.Timer _debounce;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    // Hashes already worked out, by path, size and write time, so the poll does not re-read a file that
    // turned out to be something else.
    private readonly Dictionary<string, (long Length, long Ticks, string Sha1, long Fingerprint)> _hashes =
        new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    /// <param name="waiting">The files still wanted, asked for on every pass.</param>
    /// <param name="found">Called on a worker thread with a recognised file and the entry it fills.</param>
    public ManualDownloadWatcher(string folder, Func<IReadOnlyList<ManualDownloadEntry>> waiting,
        Action<ManualDownloadEntry, string> found)
    {
        _folder = folder;
        _waiting = waiting;
        _found = found;
        _debounce = new System.Threading.Timer(_ => _ = ScanAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _poll = new System.Threading.Timer(_ => _ = ScanAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        try
        {
            _fsw = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024
            };
            _fsw.Created += OnEvent;
            _fsw.Changed += OnEvent;
            _fsw.Renamed += OnEvent;
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The poll still covers it.
            AppLog.Log("manual-downloads", $"Could not watch {folder}: {ex.Message}");
        }
    }

    public string Folder => _folder;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        // Several events arrive per download; one pass after they settle is enough.
        try { _debounce.Change(400, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    /// <summary>Looks through the folder once. Safe to call at any time; overlapping calls are
    /// skipped.</summary>
    public async Task ScanAsync()
    {
        if (_disposed || !await _scanGate.WaitAsync(0)) return;
        try
        {
            await Task.Run(ScanOnce);
        }
        catch (Exception ex)
        {
            AppLog.Log("manual-downloads", $"Scanning {_folder} failed: {ex.Message}");
        }
        finally { _scanGate.Release(); }
    }

    private void ScanOnce()
    {
        var wanted = _waiting().Where(f => f.State == ManualDownloadState.Waiting && f.IsUsable).ToList();
        if (wanted.Count == 0 || !Directory.Exists(_folder)) return;
        var sizes = wanted.Where(f => f.Size > 0).Select(f => f.Size).ToHashSet();
        var names = wanted.Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(_folder))
        {
            if (_disposed || wanted.Count == 0) return;
            var name = Path.GetFileName(path);
            if (PartialSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) continue;

            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (!sizes.Contains(info.Length) && !names.Contains(name)) continue;

            var hashes = HashesOf(path, info);
            if (hashes is null) continue;

            var match = wanted.FirstOrDefault(f =>
                ManualDownloadService.Matches(f, name, info.Length, hashes.Value.Sha1, hashes.Value.Fingerprint));
            if (match is null) continue;
            wanted.Remove(match);
            _found(match, path);
        }
    }

    private (string Sha1, long Fingerprint)? HashesOf(string path, FileInfo info)
    {
        var ticks = info.LastWriteTimeUtc.Ticks;
        if (_hashes.TryGetValue(path, out var known) && known.Length == info.Length && known.Ticks == ticks)
            return (known.Sha1, known.Fingerprint);
        try
        {
            var (sha1, fingerprint) = ManualDownloadService.Hash(path);
            _hashes[path] = (info.Length, ticks, sha1, fingerprint);
            return (sha1, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still being written, or held by a virus scan: the next pass tries again.
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Dispose();
        _debounce.Dispose();
        if (_fsw is not null)
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        }
    }
}
