using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Instances that belong to this PC instead of a CloudLauncher account: everything a launcher that
/// is not signed in creates, and what stays behind after signing in until it is added to an account.
/// </summary>
/// <remarks>
/// <para>Each one is described by <see cref="MarkerFileName"/> in its own folder, next to the icon in
/// <c>.cloudlauncher/</c> and outside <c>game/</c>, so no sync ever uploads it. A folder holding that
/// file is a local instance. The list is whatever the instances folder holds, so moving the folder,
/// or restoring it from a backup, brings the instance with it.</para>
/// <para><see cref="ApiClient"/> answers its instance calls for these ids from here, so pages use the
/// same methods for both kinds.</para>
/// </remarks>
public sealed class LocalPackStore(AppSettings settings, PackFolderService folders)
{
    /// <summary>The file that makes a folder a local instance, under <see cref="AssetsDir"/>.</summary>
    public const string MarkerFileName = "local-instance.json";

    private const string AssetsDir = ".cloudlauncher";

    /// <summary>What a request that only the server can do is told about a local instance.</summary>
    public const string LocalOnlyMessage =
        "This instance is only on this PC. Add it to your CloudLauncher account to share it.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    /// <summary>Every local instance found by the last scan, with its folder. Null until the first
    /// scan.</summary>
    private Dictionary<Guid, (string Root, LocalPackRecord Record)>? _known;

    /// <summary>What is stored for one instance. The id is the folder's <c>.packid</c>.</summary>
    public sealed class LocalPackRecord
    {
        /// <summary>Bumped if the shape ever changes incompatibly.</summary>
        public int Format { get; set; } = 1;
        public string Name { get; set; } = "";
        public string? Summary { get; set; }
        public string? Description { get; set; }
        public bool IsEmpty { get; set; }
        public string? MinecraftVersion { get; set; }
        public LoaderKind Loader { get; set; }
        public string? LoaderVersion { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>True when <paramref name="packId"/> is an instance on this PC only.</summary>
    public bool Contains(Guid packId)
    {
        lock (_gate) return KnownLocked().ContainsKey(packId);
    }

    /// <summary>Every local instance, read fresh from the instances folder.</summary>
    public List<PackSummary> List()
    {
        lock (_gate)
        {
            Rescan();
            return _known!.Select(kv => ToSummary(kv.Key, kv.Value.Record)).ToList();
        }
    }

    /// <summary>One local instance in full, or null when the id is not one.</summary>
    /// <remarks>The file rules are the pack's own <c>.rules.json</c>: there is no server copy to
    /// prefer.</remarks>
    public PackDetail? Get(Guid packId, IReadOnlyList<PackFileRule> rules)
    {
        lock (_gate)
        {
            if (!KnownLocked().TryGetValue(packId, out var entry)) return null;
            return PackDetailCache.Synthesise(ToSummary(packId, entry.Record), rules);
        }
    }

    /// <summary>Forgets the last scan, for when the instances folder has moved.</summary>
    public void Invalidate()
    {
        lock (_gate) _known = null;
    }

    // ── writing ──────────────────────────────────────────────────────────────

    /// <summary>Creates a local instance with its folder, the way the server's <c>POST /packs</c>
    /// creates one on an account (same checks, same empty-instance rules).</summary>
    public PackSummary Create(CreatePackRequest req)
    {
        var name = CheckName(req.Name);
        CheckText(req.Summary, req.Description);

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var record = new LocalPackRecord
        {
            Name = name,
            Summary = req.Summary,
            Description = req.Description,
            IsEmpty = req.IsEmpty,
            MinecraftVersion = req.IsEmpty ? null : req.MinecraftVersion,
            Loader = req.IsEmpty ? LoaderKind.None : req.Loader,
            LoaderVersion = req.IsEmpty ? null : req.LoaderVersion,
            CreatedAt = now,
            UpdatedAt = now
        };

        lock (_gate)
        {
            var root = folders.CreateNamedFolder(id, name);
            Write(root, record);
            KnownLocked()[id] = (root, record);
            AppLog.Log("local-packs", $"Created local instance '{name}' ({id}) in {root}.");
            return ToSummary(id, record);
        }
    }

    /// <summary>Applies a change the way the server's <c>PATCH /packs/{id}</c> would.</summary>
    /// <remarks>Visibility and hosting belong to the account, so asking for either is refused with
    /// <see cref="LocalOnlyMessage"/>; asking for what the instance already is (private, not hosted)
    /// is not a change and passes.</remarks>
    public PackSummary Update(Guid packId, UpdatePackRequest req)
    {
        if (req.IsShared == true || req.Visibility is { } v && v != PackVisibility.Private)
            throw new ApiException(LocalOnlyMessage, HttpStatusCode.Conflict);
        CheckText(req.Summary, req.Description);
        var name = req.Name is null ? null : CheckName(req.Name);

        lock (_gate)
        {
            if (!KnownLocked().TryGetValue(packId, out var entry))
                throw new ApiException("That instance is no longer on this PC.", HttpStatusCode.NotFound);

            // A copy, so a failed write leaves the remembered record as it was.
            var r = Clone(entry.Record);
            if (name is not null) r.Name = name;
            if (req.Description is not null) r.Description = req.Description;
            if (req.Summary is not null) r.Summary = req.Summary;
            if (req.IsEmpty is { } empty)
            {
                r.IsEmpty = empty;
                if (empty)
                {
                    r.MinecraftVersion = null;
                    r.Loader = LoaderKind.None;
                    r.LoaderVersion = null;
                }
            }
            if (!r.IsEmpty)
            {
                if (req.MinecraftVersion is not null) r.MinecraftVersion = req.MinecraftVersion;
                if (req.Loader is { } loader) r.Loader = loader;
                if (req.LoaderVersion is not null) r.LoaderVersion = req.LoaderVersion;
            }
            r.UpdatedAt = DateTimeOffset.UtcNow;

            // The folder may have been renamed to follow the name since the last scan.
            var root = CurrentRoot(packId, entry.Root);
            Write(root, r);
            KnownLocked()[packId] = (root, r);
            return ToSummary(packId, r);
        }
    }

    /// <summary>Deletes a local instance: its folder goes to the Recycle Bin.</summary>
    /// <remarks>
    /// <para>Unlike deleting an instance on an account, the files are the instance here, so leaving
    /// them would leave a folder nothing lists. The Recycle Bin keeps it recoverable; Windows asks
    /// before deleting permanently when a folder is too big for it.</para>
    /// <para>Refuses while anything has a file inside open (Minecraft, an editor): a partial delete
    /// would leave a broken instance behind.</para>
    /// </remarks>
    /// <param name="toRecycleBin">False only for an instance the launcher itself just filled and is
    /// taking back (a duplicate that was stopped or failed): nothing in it is the user's.</param>
    public async Task DeleteAsync(Guid packId, bool toRecycleBin = true)
    {
        string root;
        lock (_gate)
        {
            if (!KnownLocked().TryGetValue(packId, out var entry)) return;
            root = CurrentRoot(packId, entry.Root);
        }

        if (Directory.Exists(root))
        {
            EnsureNothingOpenInside(root);
            if (toRecycleBin) await RunOnStaThreadAsync(() => SendToRecycleBin(root));
            else await Task.Run(() => Directory.Delete(root, recursive: true));
        }

        lock (_gate) KnownLocked().Remove(packId);
        folders.InvalidateRootCache();
        AppLog.Log("local-packs", toRecycleBin
            ? $"Deleted local instance {packId}; {root} went to the Recycle Bin."
            : $"Took back local instance {packId} and its folder {root}.");
    }

    /// <summary>Stops treating the instance as local once it has been added to an account: the
    /// folder stays, only its <see cref="MarkerFileName"/> goes.</summary>
    public void Release(Guid packId)
    {
        lock (_gate)
        {
            if (!KnownLocked().TryGetValue(packId, out var entry)) return;
            var marker = MarkerPath(CurrentRoot(packId, entry.Root));
            if (File.Exists(marker)) File.Delete(marker);
            KnownLocked().Remove(packId);
        }
    }

    // ── internals ────────────────────────────────────────────────────────────

    private Dictionary<Guid, (string Root, LocalPackRecord Record)> KnownLocked()
    {
        if (_known is null) Rescan();
        return _known!;
    }

    /// <summary>Reads every <see cref="MarkerFileName"/> under the instances folder.</summary>
    /// <remarks>A marker that cannot be parsed still makes its folder a local instance, named after
    /// the folder: dropping it from the list would hide the files with no way back to them.</remarks>
    private void Rescan()
    {
        var found = new Dictionary<Guid, (string, LocalPackRecord)>();
        try
        {
            if (Directory.Exists(settings.PacksRoot))
                foreach (var dir in Directory.EnumerateDirectories(settings.PacksRoot))
                {
                    var marker = MarkerPath(dir);
                    if (!File.Exists(marker) || !LocalPackScanner.TryReadPackId(dir, out var id)) continue;
                    found[id] = (dir, Read(marker, dir, id));
                }
        }
        catch (Exception ex)
        {
            AppLog.LogError("local-packs", ex); // what was found so far still counts
        }
        _known = found;
    }

    private static LocalPackRecord Read(string marker, string dir, Guid id)
    {
        try
        {
            var record = JsonSerializer.Deserialize<LocalPackRecord>(File.ReadAllText(marker), Json);
            if (record is not null && !string.IsNullOrWhiteSpace(record.Name)) return record;
        }
        catch (Exception ex)
        {
            AppLog.Log("local-packs", $"Could not read {marker} ({ex.Message}); naming the instance after its folder.");
        }

        var created = SafeTime(() => Directory.GetCreationTimeUtc(dir));
        return new LocalPackRecord
        {
            Name = LocalPackScanner.NameFromFolder(Path.GetFileName(dir), id),
            // The folder exists, so assume it has content: an "empty" instance can't be played.
            IsEmpty = false,
            CreatedAt = created,
            UpdatedAt = SafeTime(() => Directory.GetLastWriteTimeUtc(dir))
        };
    }

    private static void Write(string root, LocalPackRecord record)
    {
        var marker = MarkerPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        var tmp = marker + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
        File.Move(tmp, marker, overwrite: true);
    }

    private static string MarkerPath(string root) => Path.Combine(root, AssetsDir, MarkerFileName);

    /// <summary>The instance's folder now: the one remembered, unless it has been renamed since.</summary>
    private string CurrentRoot(Guid packId, string remembered) =>
        Directory.Exists(remembered) ? remembered : LocalPackScanner.FindPackRoot(settings, packId) ?? remembered;

    private PackSummary ToSummary(Guid id, LocalPackRecord r) => new(
        Id: id,
        Name: r.Name,
        Description: r.Description,
        // Whoever uses this PC owns what is on it. Signed in, that is the account, which keeps the
        // "is this mine" checks the pages already make working.
        OwnerId: settings.UserId ?? Guid.Empty,
        OwnerUsername: settings.Username is { Length: > 0 } user ? user : "you",
        Visibility: PackVisibility.Private,
        IsShared: false,
        IsEmpty: r.IsEmpty,
        MinecraftVersion: r.MinecraftVersion,
        Loader: r.Loader,
        LoaderVersion: r.LoaderVersion,
        CreatedAt: r.CreatedAt,
        UpdatedAt: r.UpdatedAt,
        EffectivePermissions: PackPermissions.Full,
        Summary: r.Summary);

    private static LocalPackRecord Clone(LocalPackRecord r) => new()
    {
        Format = r.Format,
        Name = r.Name,
        Summary = r.Summary,
        Description = r.Description,
        IsEmpty = r.IsEmpty,
        MinecraftVersion = r.MinecraftVersion,
        Loader = r.Loader,
        LoaderVersion = r.LoaderVersion,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt
    };

    /// <summary>The server's name rule (1-128 characters once trimmed), with its wording.</summary>
    private static string CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ApiException("Pack name must be 1-128 characters", HttpStatusCode.BadRequest);
        return name.Trim();
    }

    private static void CheckText(string? summary, string? description)
    {
        if (summary?.Length > PackText.SummaryMaxLength)
            throw new ApiException($"Pack summary must be {PackText.SummaryMaxLength} characters or fewer", HttpStatusCode.BadRequest);
        if (description?.Length > PackText.DescriptionMaxLength)
            throw new ApiException($"Pack description must be {PackText.DescriptionMaxLength} characters or fewer", HttpStatusCode.BadRequest);
    }

    private static DateTimeOffset SafeTime(Func<DateTime> read)
    {
        try { return new DateTimeOffset(read(), TimeSpan.Zero); }
        catch { return DateTimeOffset.UtcNow; }
    }

    // ── deleting to the Recycle Bin ───────────────────────────────────────────

    /// <summary>Throws when something holds a file inside <paramref name="root"/> open.</summary>
    /// <remarks>Windows refuses to rename a folder while a file in it is open, so renaming it away and
    /// straight back is the cheapest complete check.</remarks>
    private static void EnsureNothingOpenInside(string root)
    {
        var probe = root.TrimEnd(Path.DirectorySeparatorChar) + ".deleting-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.Move(root, probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "Something is still using this instance's files. Close Minecraft (and anything else that has " +
                "a file from it open) and try again.", ex);
        }
        Directory.Move(probe, root);
    }

    private static Task RunOnStaThreadAsync(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        })
        { IsBackground = true, Name = "Recycle instance folder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void SendToRecycleBin(string path)
    {
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            // Double-null-terminated list of one path.
            pFrom = Path.GetFullPath(path) + '\0' + '\0',
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning
        };
        var result = SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted)
            throw new OperationCanceledException("The instance was not deleted.");
        if (result != 0)
            throw new IOException($"Windows could not move the instance folder to the Recycle Bin (error {result}).");
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct lpFileOp);
}
