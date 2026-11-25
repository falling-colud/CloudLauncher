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

    /// <summary>The file in the same folder that this one takes over from, for an update: removed
    /// once this one is in place, so two versions of a mod never load together.</summary>
    public string? Replaces { get; set; }

    public ManualDownloadState State { get; set; }

    /// <summary>The file belongs in the instance's <c>local</c> folder (this PC's own mods) rather than
    /// in <c>game</c>. Not stored: such entries are kept in the local folder's own list, which never
    /// syncs, so a friend's launcher is never asked for a file that is only this PC's.</summary>
    [JsonIgnore]
    public bool Local { get; set; }

    /// <summary>The folders a modpack can route a file to.</summary>
    public static readonly string[] Folders = ["mods", "resourcepacks", "shaderpacks"];

    /// <summary>The name the file has inside the instance.</summary>
    [JsonIgnore]
    public string TargetFileName => Disabled && Folder == "mods" ? FileName + ".disabled" : FileName;

    /// <summary>A record that names a known folder and a plain file name. Anything else is left out of
    /// every list rather than acted on.</summary>
    [JsonIgnore]
    public bool IsUsable =>
        FileId > 0 && Folders.Contains(Folder) && PathSafety.IsSafeFileName(FileName)
        && (Replaces is null || PathSafety.IsSafeFileName(Replaces));

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

/// <summary>One file a page wants fetched by hand, as the store listed it, and where it goes.</summary>
/// <param name="Folder"><c>mods</c>, <c>resourcepacks</c> or <c>shaderpacks</c>.</param>
/// <param name="Local">Into the instance's <c>local</c> folder (this PC's own mods).</param>
/// <param name="Disabled">Goes in switched off (<c>.jar.disabled</c>).</param>
/// <param name="Replaces">The file it takes over from, for an update.</param>
public sealed record ManualDownloadWant(
    ModSummary Mod,
    ModVersion Version,
    ModVersionFile File,
    string Folder = "mods",
    bool Local = false,
    bool Disabled = false,
    string? Replaces = null);

/// <summary>The store would not hand over a file, so it went to the download-by-hand card instead,
/// which is up now. The message says so in a sentence a status line can show as it is.</summary>
public sealed class ManualDownloadQueuedException(ManualDownloadEntry entry, string message)
    : InvalidOperationException(message)
{
    public ManualDownloadEntry Entry { get; } = entry;
}

/// <summary>
/// Files the launcher may not download (CurseForge authors can turn off downloads outside CurseForge's
/// own site and app), kept per instance until they are in place: what a modpack import could not fetch,
/// and what the mod browser, the Mods page and mod updates hand over.
/// </summary>
/// <remarks>
/// <para>The import records them (<see cref="Record"/>), a page adds to them (<see cref="QueueAsync"/>),
/// and both raise <see cref="Needed"/>; the card that answers it sends the user to each file's download
/// page and puts the downloaded files in place (<see cref="Place"/>), recognising them by size and
/// fingerprint so a browser's renamed copy still counts.</para>
/// <para>A file counts as added once it is in its folder under its own name, however it got there
/// (<see cref="Reconcile"/>), so the list never nags about a file the user dropped in themselves.</para>
/// <para>Two lists per instance: <c>game/.cloudlauncher</c> (synced with a shared instance, so a friend
/// is asked for the same files) and <c>local/.cloudlauncher</c> for this PC's own mods.</para>
/// </remarks>
public sealed class ManualDownloadService(PackFolderService packs, AppSettings settings)
{
    /// <summary>The list's file, in the instance's <c>game/.cloudlauncher</c> folder.</summary>
    public const string ListFileName = "manual-downloads.json";

    /// <summary>How long the launcher keeps looking for a file after its card closed, once the user
    /// opened its download page: closing the card right after clicking Download is natural, and the
    /// file should still land in the instance.</summary>
    public static readonly TimeSpan WatchAfterCardFor = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    /// <summary>Files are waiting to be downloaded by hand: the instance, its name, and the files
    /// the card should show (null: every file the instance is still missing, as after an import).
    /// Raised on the UI thread.</summary>
    public event Action<Guid, string, IReadOnlyList<int>?>? Needed;

    /// <summary>An instance's list changed (a file added or skipped). Raised on the UI thread.</summary>
    public event Action<Guid>? Changed;

    /// <summary>The launcher found and placed a file after its card had closed: the instance, the
    /// entry and where the file now is. Raised on the UI thread.</summary>
    public event Action<Guid, ManualDownloadEntry, string>? PlacedInBackground;

    private string ListPath(Guid packId) => Path.Combine(packs.GameDir(packId), ".cloudlauncher", ListFileName);

    private string LocalListPath(Guid packId) => Path.Combine(packs.LocalDir(packId), ".cloudlauncher", ListFileName);

    /// <summary>The instance folder an entry's <see cref="ManualDownloadEntry.Folder"/> sits in.</summary>
    private string RootOf(Guid packId, ManualDownloadEntry file) =>
        file.Local ? packs.LocalDir(packId) : packs.GameDir(packId);

    /// <summary>The folder a file goes into: <c>game/mods</c>, <c>local/mods</c>, ...</summary>
    public string FolderOf(Guid packId, ManualDownloadEntry file) => Path.Combine(RootOf(packId, file), file.Folder);

    /// <summary>The instance's list as stored (both of its files), or null when it has none.</summary>
    public ManualDownloadList? Load(Guid packId)
    {
        lock (_gate)
        {
            var game = ReadList(packId, ListPath(packId));
            var local = ReadList(packId, LocalListPath(packId));
            if (game is null && local is null) return null;
            var list = game ?? new ManualDownloadList();
            if (local is not null)
            {
                foreach (var file in local.Files) file.Local = true;
                list.Files.AddRange(local.Files);
            }
            return list;
        }
    }

    private static ManualDownloadList? ReadList(Guid packId, string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var list = JsonSerializer.Deserialize<ManualDownloadList>(File.ReadAllText(path), Json);
            if (list is null) return null;
            list.Files ??= new();
            list.Files.RemoveAll(f => f is null || !f.IsUsable);
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Log("manual-downloads", $"Could not read the list for {packId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes the list back, each entry into the file it belongs in. A part left empty has
    /// its file removed, except the synced list of an import, which keeps where it came from.</summary>
    private void Save(Guid packId, ManualDownloadList list)
    {
        lock (_gate)
        {
            var game = list.Files.Where(f => !f.Local).ToList();
            var local = list.Files.Where(f => f.Local).ToList();
            WriteList(ListPath(packId), game.Count == 0 && list.Source is null ? null
                : new ManualDownloadList { Version = list.Version, Source = list.Source, CreatedAt = list.CreatedAt, Files = game });
            WriteList(LocalListPath(packId), local.Count == 0 ? null
                : new ManualDownloadList { Version = list.Version, CreatedAt = list.CreatedAt, Files = local });
        }
    }

    private static void WriteList(string path, ManualDownloadList? list)
    {
        if (list is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Starts the instance's synced list over with what an import could not download. An
    /// import with nothing left over removes it, so a re-import that got everything clears it. This
    /// PC's own list is left as it is.</summary>
    public void Record(Guid packId, string? source, IReadOnlyCollection<ManualDownloadEntry> files)
    {
        try
        {
            lock (_gate)
            {
                WriteList(ListPath(packId), files.Count == 0 ? null : new ManualDownloadList
                {
                    Source = source,
                    Files = files.Where(f => f.IsUsable && !f.Local)
                        .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log("manual-downloads", $"Could not save the list for {packId}: {ex.Message}");
        }
        RaiseChanged(packId);
    }

    /// <summary>
    /// Adds files to the instance's list, keeping what is already on it (an import's leftovers stay).
    /// A file already listed is listed again as new; a file of a project that still has another file
    /// waiting in the same folder takes that one's place, since the user picked this one now.
    /// </summary>
    /// <returns>The entries as stored, with their state: <see cref="ManualDownloadState.Added"/> for a
    /// file that is already in place (which then also removes the file it replaces).</returns>
    public IReadOnlyList<ManualDownloadEntry> Add(Guid packId, IReadOnlyCollection<ManualDownloadEntry> files)
    {
        var usable = files.Where(f => f.IsUsable).ToList();
        if (usable.Count == 0) return [];
        try
        {
            lock (_gate)
            {
                var list = Load(packId) ?? new ManualDownloadList();
                foreach (var file in usable)
                {
                    list.Files.RemoveAll(f => f.Local == file.Local
                        && (f.FileId == file.FileId
                            || f.ProjectId == file.ProjectId && f.Folder == file.Folder && f.State == ManualDownloadState.Waiting));
                    file.State = IsInPlace(packId, file) ? ManualDownloadState.Added : ManualDownloadState.Waiting;
                    // Already there (an update to a file the folder holds): it takes over at once.
                    if (file.State == ManualDownloadState.Added) RemoveReplaced(packId, file);
                    list.Files.Add(file);
                }
                list.Files.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
                Save(packId, list);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log("manual-downloads", $"Could not save the list for {packId}: {ex.Message}");
        }
        RaiseChanged(packId);
        return usable;
    }

    /// <summary>
    /// What a page does with files the store would not hand over: describes them (one CurseForge lookup
    /// for their size, hashes and pages), adds them to the instance's list and raises
    /// <see cref="Needed"/> for exactly those files, so the card pops up with them. A file that is
    /// already in its folder is listed as added and brings up no card.
    /// </summary>
    /// <returns>The entries added; empty when none could be listed (the reason is in the log).</returns>
    public async Task<IReadOnlyList<ManualDownloadEntry>> QueueAsync(Guid packId, string packName,
        CurseForgeService curseForge, IReadOnlyList<ManualDownloadWant> wants, CancellationToken ct = default)
    {
        var entries = await DescribeAsync(curseForge, wants, ct);
        var stored = Add(packId, entries);
        var waiting = stored.Where(e => e.State == ManualDownloadState.Waiting).ToList();
        if (waiting.Count > 0)
        {
            AppLog.Log("manual-downloads",
                $"{waiting.Count} file(s) for {packName} to download by hand: {string.Join(", ", waiting.Select(e => e.FileName))}");
            RaiseNeeded(packId, packName, waiting.Select(e => e.FileId).ToList());
        }
        return stored;
    }

    /// <summary>
    /// Turns store files into entries the card can act on: the project's name and download page, and the
    /// size and hashes that recognise the file once it is downloaded. One lookup for all of them; what it
    /// cannot answer comes from the store's own listing (then only the name and size recognise it).
    /// </summary>
    public static async Task<List<ManualDownloadEntry>> DescribeAsync(CurseForgeService curseForge,
        IReadOnlyList<ManualDownloadWant> wants, CancellationToken ct = default)
    {
        var ids = new List<(ManualDownloadWant Want, int ModId, int FileId)>();
        foreach (var want in wants)
        {
            if (want.Version.Source != ModSource.CurseForge) continue;
            if (CurseForgeService.TryParseFileIds(want.Mod, want.Version, out var modId, out var fileId))
                ids.Add((want, modId, fileId));
            else
                AppLog.Log("manual-downloads", $"{want.Mod.Name}: its CurseForge file could not be identified, so it cannot be listed.");
        }
        if (ids.Count == 0) return [];

        Dictionary<int, CurseForgeFileDetails> details;
        try { details = await curseForge.GetFileDetailsAsync(ids.Select(i => i.FileId), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Log("manual-downloads", $"Could not look up the files to download by hand ({ex.Message}); listing what the store showed.");
            details = new();
        }

        var entries = new List<ManualDownloadEntry>();
        foreach (var (want, modId, fileId) in ids)
        {
            details.TryGetValue(fileId, out var d);
            var entry = new ManualDownloadEntry
            {
                ProjectId = modId,
                FileId = fileId,
                Name = d?.ProjectName ?? want.Mod.Name,
                FileName = d?.FileName ?? want.File.Filename,
                Size = d?.Length is > 0 ? d.Length : want.File.Size,
                Fingerprint = d?.Fingerprint ?? 0,
                Sha1 = d?.Sha1,
                Folder = ManualDownloadEntry.Folders.Contains(want.Folder) ? want.Folder : "mods",
                Disabled = want.Disabled,
                ProjectUrl = d?.WebsiteUrl,
                IconUrl = d?.IconUrl ?? want.Mod.IconUrl,
                OptedOut = d is null || !d.AllowsDistribution,
                Replaces = want.Replaces,
                Local = want.Local
            };
            if (!entry.IsUsable)
            {
                AppLog.Log("manual-downloads", $"{entry.Name} ({modId}:{fileId}) can't be listed for a manual download: CurseForge gave no usable file name.");
                continue;
            }
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>Marks every waiting file that is already in place as added, saves when that changed
    /// anything, and returns the list (null when the instance has none).</summary>
    /// <remarks>A file that takes over from another (an update) also removes that one here, when the
    /// user put the new file in by hand: both in the folder would load two versions of the mod.</remarks>
    public ManualDownloadList? Reconcile(Guid packId)
    {
        ManualDownloadList? list;
        var changed = false;
        lock (_gate)
        {
            list = Load(packId);
            if (list is null) return null;
            foreach (var file in list.Files.Where(f => f.State == ManualDownloadState.Waiting))
            {
                if (!IsInPlace(packId, file)) continue;
                file.State = ManualDownloadState.Added;
                RemoveReplaced(packId, file);
                changed = true;
            }
            if (changed)
            {
                try { Save(packId, list); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Log("manual-downloads", $"Could not save the list for {packId}: {ex.Message}");
                }
            }
        }
        if (changed) RaiseChanged(packId);
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
            var dir = FolderOf(packId, file);
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
        lock (_gate)
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
        var dir = FolderOf(packId, file);
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
        RemoveReplaced(packId, file);
        SetState(packId, file.FileId, ManualDownloadState.Added);
        return dest;
    }

    /// <summary>Removes the file an update takes over from, once the new one is in place.</summary>
    private void RemoveReplaced(Guid packId, ManualDownloadEntry file)
    {
        if (string.IsNullOrEmpty(file.Replaces)
            || string.Equals(file.Replaces, file.TargetFileName, PathComparison)
            || string.Equals(file.Replaces, file.FileName, PathComparison)) return;
        try
        {
            if (PathSafety.ResolveFileName(FolderOf(packId, file), file.Replaces) is { } old && File.Exists(old))
            {
                File.Delete(old);
                AppLog.Log("manual-downloads", $"Removed {file.Replaces}: {file.FileName} takes over from it.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log("manual-downloads", $"Added {file.FileName} but could not remove the old {file.Replaces}: {ex.Message}");
        }
    }

    /// <summary>File names compare without case on Windows and with it elsewhere.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);

    /// <summary>Tells whoever shows the card that there are files to download by hand: an import's
    /// (<paramref name="fileIds"/> null, every waiting file) or a page's (those files).</summary>
    public void RaiseNeeded(Guid packId, string packName, IReadOnlyList<int>? fileIds = null) =>
        OnUi(() => Needed?.Invoke(packId, packName, fileIds));

    private void RaiseChanged(Guid packId) => OnUi(() => Changed?.Invoke(packId));

    // ── watching after the card closed ────────────────────────────────────────

    private readonly Dictionary<Guid, BackgroundWatch> _background = new();

    private sealed class BackgroundWatch(HashSet<int> fileIds) : IDisposable
    {
        public HashSet<int> FileIds { get; } = fileIds;
        public ManualDownloadWatcher? Watcher { get; set; }
        public System.Threading.Timer? Expiry { get; set; }

        public void Dispose()
        {
            Watcher?.Dispose();
            Expiry?.Dispose();
        }
    }

    /// <summary>
    /// Keeps looking for these files in the Downloads folder for <see cref="WatchAfterCardFor"/> after
    /// their card closed, and puts each in place as it arrives (<see cref="PlacedInBackground"/>).
    /// Replaces any earlier watch for the instance.
    /// </summary>
    public void WatchInBackground(Guid packId, IReadOnlyCollection<int> fileIds)
    {
        StopBackgroundWatch(packId);
        if (fileIds.Count == 0) return;
        var folder = WatchFolder;
        if (!Directory.Exists(folder)) return;

        var watch = new BackgroundWatch(fileIds.ToHashSet());
        lock (_background) _background[packId] = watch;
        watch.Watcher = new ManualDownloadWatcher(folder,
            () =>
            {
                int[] ids;
                lock (_background) ids = watch.FileIds.ToArray();
                return ids.Length == 0 ? [] : Waiting(packId).Where(f => ids.Contains(f.FileId)).ToList();
            },
            (entry, path) =>
            {
                try
                {
                    var dest = Place(packId, entry, path, RemoveAfterAdding);
                    bool done;
                    lock (_background) { watch.FileIds.Remove(entry.FileId); done = watch.FileIds.Count == 0; }
                    OnUi(() => PlacedInBackground?.Invoke(packId, entry, dest));
                    if (done) StopBackgroundWatch(packId, watch);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    AppLog.Log("manual-downloads", $"Found {path} for {entry.FileName} but could not add it: {ex.Message}");
                }
            });
        watch.Expiry = new System.Threading.Timer(_ => StopBackgroundWatch(packId, watch), null, WatchAfterCardFor, Timeout.InfiniteTimeSpan);
        AppLog.Log("manual-downloads", $"Still looking in {folder} for {fileIds.Count} file(s) of {packId}.");
        _ = watch.Watcher.ScanAsync();
    }

    /// <summary>Stops looking after a closed card (its card is open again, say) and hands back the
    /// files it was still looking for, so the new card can show them.</summary>
    public IReadOnlyList<int> StopBackgroundWatch(Guid packId) => StopBackgroundWatch(packId, null);

    private IReadOnlyList<int> StopBackgroundWatch(Guid packId, BackgroundWatch? only)
    {
        BackgroundWatch? watch;
        lock (_background)
        {
            if (!_background.TryGetValue(packId, out watch) || only is not null && !ReferenceEquals(watch, only))
                return [];
            _background.Remove(packId);
        }
        int[] left;
        lock (_background) left = watch.FileIds.ToArray();
        watch.Dispose();
        return left;
    }

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

    /// <summary>The folder the card watches: the user's choice, else the system's Downloads folder.</summary>
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

    /// <summary>The Downloads folder as the system has it, which a user may have moved off the home
    /// folder: Windows' known folder, or <c>XDG_DOWNLOAD_DIR</c> on Linux; <c>~/Downloads</c> when the
    /// system will not say.</summary>
    public static string DownloadsFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
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
                /* fall through */
            }
        }
        else if (XdgDownloadDir(home) is { } xdg)
        {
            return xdg;
        }
        return Path.Combine(home, "Downloads");
    }

    /// <summary><c>XDG_DOWNLOAD_DIR</c> from the environment or <c>~/.config/user-dirs.dirs</c>
    /// (<c>XDG_DOWNLOAD_DIR="$HOME/Downloads"</c>), when it names a folder that exists.</summary>
    private static string? XdgDownloadDir(string home)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
            if (string.IsNullOrWhiteSpace(value))
            {
                var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrWhiteSpace(config)) config = Path.Combine(home, ".config");
                var dirs = Path.Combine(config, "user-dirs.dirs");
                if (!File.Exists(dirs)) return null;
                foreach (var raw in File.ReadLines(dirs))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal)) continue;
                    value = line["XDG_DOWNLOAD_DIR=".Length..].Trim().Trim('"');
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Replace("$HOME", home, StringComparison.Ordinal);
            if (value.StartsWith("~/", StringComparison.Ordinal)) value = Path.Combine(home, value[2..]);
            return Path.IsPathRooted(value) && Directory.Exists(value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void OnUi(Action raise)
    {
#if WINDOWS
        var app = System.Windows.Application.Current;
        if (app is null) { raise(); return; }
        if (app.Dispatcher.CheckAccess()) raise();
        else app.Dispatcher.BeginInvoke(raise);
#else
        UiThread.Run(raise);
#endif
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
