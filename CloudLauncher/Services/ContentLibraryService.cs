using System.IO;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>The kinds of content the launcher can keep once and hand to every instance.</summary>
/// <remarks>All but <see cref="World"/> are shared by hard link. A world is a template, copied in
/// once, since a running game writes its save continuously. The numbers are stored in index.json
/// keys: append new kinds, never reorder.</remarks>
public enum LibraryKind
{
    ResourcePack = 0,
    ShaderPack = 1,
    /// <summary>A save folder, or a world zip. A template: copied into an instance, never linked.</summary>
    World = 2,
    /// <summary>A <c>.jar</c> (or <c>.jar.disabled</c>) for the global Mods page.</summary>
    Mod = 3,
    /// <summary>A zip that unpacks into an instance's <c>config/</c>.</summary>
    ConfigBundle = 4,
    /// <summary>A zip that unpacks into an instance's <c>kubejs/</c>.</summary>
    KubeJsBundle = 5
}

/// <summary>What applying a library item to one instance actually did.</summary>
public enum LibraryApplyOutcome
{
    /// <summary>A hard link was created: one copy of the bytes, shared with the library.</summary>
    Linked = 0,
    /// <summary>Hard-linking was refused (other volume, non-NTFS) so the bytes were copied.</summary>
    Copied,
    /// <summary>The instance already held this exact file. Nothing was written.</summary>
    AlreadyThere,
    /// <summary>Applied, but the instance has its own same-named pack in <c>game/</c>, which wins at
    /// launch, so the library copy is present but unused until that one is removed.</summary>
    Shadowed,
    /// <summary>Removed from this instance.</summary>
    Removed,
    /// <summary>Nothing to do: the item was not in this instance.</summary>
    NotPresent,
    Failed
}

/// <summary>How one instance currently stands in relation to one library item.</summary>
/// <param name="HasOwnCopy">The instance has its own same-named pack in <c>game/</c> that is not the
/// library file. The launch overlay never overwrites, so that copy wins and the library one is inert.</param>
/// <param name="Diverged">The launcher recorded this instance as linked, but the file there is no
/// longer the library file: an editor that writes a temp file and renames it over the top replaces the
/// directory entry and silently breaks the link, leaving the instance with a private copy.</param>
/// <param name="LoaderMissing">Shaders only: nothing in <c>mods/</c> can load a shader pack here, so
/// applying one is a no-op in game.</param>
public sealed record LibraryTargetState(
    Guid PackId,
    string PackName,
    bool IsShared,
    bool Applied,
    bool LinkedToLibrary,
    bool HasOwnCopy,
    bool Diverged,
    bool LoaderMissing)
{
    /// <summary>One clause for the row under the instance name. Kept plain, since all of these states
    /// are normal rather than user errors.</summary>
    public string StateLabel =>
        Diverged        ? "has its own edited copy - no longer linked"
        : HasOwnCopy    ? "has its own copy of this file; the shared one is not in use"
        : LinkedToLibrary ? "already using the shared copy"
        : Applied       ? "already has a copy of this file"
        : "will be added";
}

/// <summary>What one instance got out of an apply / unapply, for the per-instance report.</summary>
public sealed record LibraryApplyTarget(
    Guid PackId,
    string PackName,
    LibraryApplyOutcome Outcome,
    string Detail);

/// <summary>The whole result of applying one or more items to one or more instances.</summary>
public sealed record LibraryApplyResult(IReadOnlyList<LibraryApplyTarget> Targets)
{
    public int Linked       => Targets.Count(t => t.Outcome == LibraryApplyOutcome.Linked);
    public int Copied       => Targets.Count(t => t.Outcome == LibraryApplyOutcome.Copied);
    public int AlreadyThere => Targets.Count(t => t.Outcome == LibraryApplyOutcome.AlreadyThere);
    public int Shadowed     => Targets.Count(t => t.Outcome == LibraryApplyOutcome.Shadowed);
    public int Removed      => Targets.Count(t => t.Outcome == LibraryApplyOutcome.Removed);
    public int Failed       => Targets.Count(t => t.Outcome == LibraryApplyOutcome.Failed);

    /// <summary>One status-bar line, in the shape the config-copy card already reports in:
    /// counts first, then the one failure worth naming.</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Linked > 0)       parts.Add($"{Linked} linked");
        if (Copied > 0)       parts.Add($"{Copied} copied");
        if (Removed > 0)      parts.Add($"{Removed} removed");
        if (AlreadyThere > 0) parts.Add($"{AlreadyThere} already had it");
        if (Shadowed > 0)     parts.Add($"{Shadowed} not in use - the instance has its own copy");
        if (Failed > 0)
        {
            var first = Targets.First(t => t.Outcome == LibraryApplyOutcome.Failed);
            parts.Add($"{Failed} failed - {first.PackName}: {first.Detail}");
        }
        return parts.Count == 0 ? "Nothing to do." : string.Join("  ·  ", parts);
    }
}

/// <summary>Everything the launcher remembers about one file in the library.</summary>
public sealed record LibraryItem(
    string Key,
    LibraryKind Kind,
    string FileName,
    string Path,
    string DisplayName,
    bool IsFolder,
    long SizeBytes,
    DateTimeOffset AddedAt,
    DateTimeOffset LastModified,
    bool KeepLocal,
    IReadOnlyList<Guid> AppliedTo,
    string? OriginPath,
    ModSource? Source,
    string? ProjectId,
    string? VersionId,
    string? VersionNumber,
    // Appended last, with defaults, so existing construction sites keep compiling unchanged.
    ContentDefaultPolicy? Defaults = null,
    string? ModKey = null)
{
    public string SizeLabel => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024:0.#} MB"
        : SizeBytes > 0 ? $"{SizeBytes / 1024} KB" : "-";

    public bool HasProvenance => Source is not null && !string.IsNullOrWhiteSpace(ProjectId);

    // ── the default rule, read through Defaults ──
    // Conveniences for the pages; nothing is stored twice. Changes go through
    // ContentLibraryService.SetDefaults with a whole policy.

    /// <summary>True when this item is one of the user's defaults (every compatible instance gets it).
    /// Same bit as <see cref="ContentDefaultPolicy.Enabled"/>.</summary>
    public bool AutoApply => Defaults?.Enabled == true;

    /// <summary>True when no include list narrows this item down. What the old per-item "compatible
    /// with all packs" checkbox meant.</summary>
    public bool CompatibleWithAll => Defaults?.AppliesToAllInstances ?? true;

    /// <summary>The instances this item is limited to, or empty for "every instance".</summary>
    public IReadOnlyList<Guid> CompatiblePackIds => Defaults?.IncludePackIds ?? [];

    /// <summary>The instances that never get this item, whatever the other rules say.</summary>
    public IReadOnlyList<Guid> ExcludedPackIds => Defaults?.ExcludePackIds ?? [];

    /// <summary>Minecraft versions this item is for, comma separated. Empty means any.</summary>
    public string? McVersionsCsv => Defaults?.McVersionsCsv;

    /// <summary>Loaders this item is for, comma separated. Empty means any.</summary>
    public string? LoadersCsv => Defaults?.LoadersCsv;

    /// <summary>One line naming what the rule narrows down to, for a row subtitle.</summary>
    public string RuleSummary => Defaults is null ? "not a default" : Defaults.RuleSummary;

    /// <summary>True for the one kind that is copied into an instance rather than shared with it.</summary>
    public bool IsTemplate => Kind == LibraryKind.World;

    /// <summary>
    /// A stand-in for content that is no longer in the library.
    /// </summary>
    /// <remarks>For when the defaults engine switched a pack on in an instance and the item has since
    /// been removed from the library: the launcher still has to switch it back off and name it. It has
    /// no path on disk, so nothing else should construct one.</remarks>
    public static LibraryItem Missing(LibraryKind kind, string fileName) => new(
        Key: ContentLibraryService.KeyFor(kind, fileName), Kind: kind, FileName: fileName, Path: "",
        DisplayName: kind == LibraryKind.ShaderPack ? ShaderPackService.PrettyName(fileName) : fileName,
        IsFolder: false, SizeBytes: 0, AddedAt: default, LastModified: default, KeepLocal: false,
        AppliedTo: [], OriginPath: null, Source: null, ProjectId: null, VersionId: null,
        VersionNumber: null);
}

/// <summary>One copy of a resource pack, shader pack, mod, world template or config/KubeJS bundle,
/// kept outside every instance and hard-linked into as many of them as the user likes.</summary>
/// <remarks>
/// <para>Lives in <c>&lt;PacksRoot&gt;/.library/</c>: hard links need one volume, and every instance
/// is under PacksRoot. It has no <c>.packid</c>, so instance scans skip it. Items are linked into an
/// instance's unsynced <c>local/</c> side, which the launch overlay links into <c>game/</c>; when a
/// link is refused (other volume, non-NTFS) the bytes are copied instead.</para>
/// <para>Applying is not enabling, and <see cref="ContentDefaultsService"/> decides which instances
/// get what. Worlds are copied, never linked, since a running game writes its save continuously;
/// <see cref="Materialise"/> throws for one.</para>
/// </remarks>
public sealed class ContentLibraryService(AppSettings settings, PackFolderService packs, ShaderPackService shaders)
{
    /// <summary>Name of the library folder inside PacksRoot. Dot-prefixed so it sorts out of the way
    /// and reads as launcher bookkeeping rather than as somebody's instance.</summary>
    public const string FolderName = ".library";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private IndexFile? _index;
    private DateTime _indexStamp = DateTime.MinValue;
    // Built from the index on first use (a sync asks it once per path) and dropped whenever the index
    // changes.
    private Dictionary<string, string>? _keepLocal;

    // ── layout ───────────────────────────────────────────────────────────────

    /// <summary>Absolute path of the library root. Read from settings every time rather than cached:
    /// the instances folder is user-settable and the library follows it.</summary>
    public string Root => Path.Combine(settings.PacksRoot, FolderName);

    /// <summary>The folder one kind lives in, under <c>.library/</c>.</summary>
    /// <remarks><c>resourcepacks</c>, <c>shaderpacks</c> and <c>mods</c> match the in-instance folder
    /// names, which keeps <see cref="InstancePathFor"/> and <see cref="GamePathFor"/> to one line each.
    /// <c>saves</c> matches too, but those paths are never used for it: a world is copied into
    /// <c>game/saves/</c>, not linked. The bundle folders have no in-instance counterpart.</remarks>
    public static string FolderNameFor(LibraryKind kind) => kind switch
    {
        LibraryKind.ShaderPack => "shaderpacks",
        LibraryKind.World => "saves",
        LibraryKind.Mod => "mods",
        LibraryKind.ConfigBundle => "configbundles",
        LibraryKind.KubeJsBundle => "kubejsbundles",
        _ => "resourcepacks"
    };

    /// <summary>Every kind, in the order the library scans and the pages list them.</summary>
    public static readonly IReadOnlyList<LibraryKind> AllKinds =
    [
        LibraryKind.ResourcePack, LibraryKind.ShaderPack, LibraryKind.World,
        LibraryKind.Mod, LibraryKind.ConfigBundle, LibraryKind.KubeJsBundle
    ];

    public string FolderFor(LibraryKind kind) => Path.Combine(Root, FolderNameFor(kind));

    private string IndexPath => Path.Combine(Root, "index.json");

    /// <summary>Key for an item: its folder plus its file name, e.g.
    /// <c>resourcepacks/Faithful64x.zip</c>. Stable across display-name changes, since the file name is
    /// what every instance sees.</summary>
    public static string KeyFor(LibraryKind kind, string fileName) => $"{FolderNameFor(kind)}/{fileName}";

    /// <summary>The inverse of <see cref="FolderNameFor"/>.</summary>
    /// <remarks>Also used by <see cref="IsKeepLocalPath"/> on the first segment of a
    /// <c>game/</c>-relative path, so <c>mods</c> has to answer here: a library mod in <c>mods/</c> is a
    /// real hard link that can be held back at sync. A path under <c>saves/</c> always fails the identity
    /// test, which is correct since worlds are copied.</remarks>
    private static LibraryKind? KindForFolder(string folder) => folder.ToLowerInvariant() switch
    {
        "resourcepacks" => LibraryKind.ResourcePack,
        "shaderpacks" => LibraryKind.ShaderPack,
        "saves" => LibraryKind.World,
        "mods" => LibraryKind.Mod,
        "configbundles" => LibraryKind.ConfigBundle,
        "kubejsbundles" => LibraryKind.KubeJsBundle,
        _ => null
    };

    /// <summary>Where an item lands inside an instance: the unsynced <c>local/</c> side.</summary>
    public string InstancePathFor(Guid packId, LibraryKind kind, string fileName) =>
        Path.Combine(packs.LocalDir(packId), FolderNameFor(kind), fileName);

    /// <summary>Where the launch overlay will materialise it, and where an instance's own same-named
    /// pack would already be sitting.</summary>
    public string GamePathFor(Guid packId, LibraryKind kind, string fileName) =>
        Path.Combine(packs.GameDir(packId), FolderNameFor(kind), fileName);

    // ── health ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the library and the instances are on the same volume, so hard links are possible.
    /// </summary>
    /// <remarks>They always are, unless PacksRoot resolves across a junction. Check this before
    /// promising "one copy of the bytes": when it is false every apply is a real copy and the page should
    /// say so.</remarks>
    public bool SupportsHardLinks
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                var a = Path.GetPathRoot(Path.GetFullPath(Root));
                var b = Path.GetPathRoot(Path.GetFullPath(settings.PacksRoot));
                return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    // ── reading the library ──────────────────────────────────────────────────

    /// <summary>
    /// Everything in the library, newest first.
    /// </summary>
    /// <remarks>The disk is the truth and index.json only adds metadata: a file dropped into
    /// <c>.library/resourcepacks/</c> by hand is an item with a derived name, and an index entry whose
    /// file is gone is not shown.</remarks>
    public List<LibraryItem> Scan(LibraryKind? only = null)
    {
        var index = LoadIndex();
        var items = new List<LibraryItem>();

        foreach (var kind in AllKinds)
        {
            if (only is { } k && k != kind) continue;
            var dir = FolderFor(kind);
            if (!Directory.Exists(dir)) continue;

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var fileName = Path.GetFileName(entry);
                var isFolder = Directory.Exists(entry);
                if (fileName.StartsWith('.')) continue;
                if (!Accepts(kind, entry, fileName, isFolder)) continue;

                var key = KeyFor(kind, fileName);
                index.Items.TryGetValue(key, out var stored);

                long size;
                DateTimeOffset modified;
                try
                {
                    modified = isFolder ? Directory.GetLastWriteTime(entry) : File.GetLastWriteTime(entry);
                    // Folder sizes come from the index (refreshed on add and apply) instead of a
                    // walk. Saves are never walked: a world can be several GB in tens of thousands
                    // of files, and this scan runs on every launch.
                    size = isFolder
                        ? stored?.SizeBytes ?? (kind == LibraryKind.World ? 0 : FolderSize(entry))
                        : new FileInfo(entry).Length;
                }
                catch { modified = DateTimeOffset.MinValue; size = stored?.SizeBytes ?? 0; }

                items.Add(new LibraryItem(
                    Key: key,
                    Kind: kind,
                    FileName: fileName,
                    Path: entry,
                    // Each kind keeps the name its own page would have derived, so an item reads the
                    // same in the library as it does once it is in an instance.
                    DisplayName: stored is { DisplayName.Length: > 0 }
                        ? stored.DisplayName
                        : DerivedName(kind, entry, fileName, isFolder),
                    IsFolder: isFolder,
                    SizeBytes: size,
                    AddedAt: stored?.AddedAt ?? modified,
                    LastModified: modified,
                    KeepLocal: stored?.KeepLocal ?? false,
                    AppliedTo: stored?.AppliedTo.Keys
                        .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                        .Where(g => g != Guid.Empty).ToList() ?? [],
                    OriginPath: stored?.OriginPath,
                    Source: stored?.Source,
                    ProjectId: stored?.ProjectId,
                    VersionId: stored?.VersionId,
                    VersionNumber: stored?.VersionNumber,
                    // Cloned because the index entry is live in-memory state; editing it through
                    // the item would change it without saving. Rules change through SetDefaults.
                    Defaults: stored?.Defaults?.Clone(),
                    ModKey: stored?.ModKey));
            }
        }

        return items.OrderByDescending(i => i.AddedAt).ToList();
    }

    public LibraryItem? Find(string key) =>
        Scan().FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether one directory entry is content of that kind, using the same test as that kind's
    /// page, so a stray <c>readme.txt</c> never becomes an item.</summary>
    /// <remarks>A world zip costs a zip open (<see cref="WorldService.ZipContainsWorld"/> reads only the
    /// central directory), so a mod zip is never listed as a world.</remarks>
    private static bool Accepts(LibraryKind kind, string path, string fileName, bool isFolder) => kind switch
    {
        // A save folder has a level.dat directly inside it; a world zip has one at the root or one
        // folder down. Everything else in saves/ is not a world.
        LibraryKind.World => isFolder
            ? WorldService.IsWorldFolder(path)
            : fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && WorldService.ZipContainsWorld(path),
        // A mod is always a file; a folder in mods/ is a loader's cache.
        LibraryKind.Mod => !isFolder
            && (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)),
        // A bundle is the zip itself. It is unpacked into the instance, so an unpacked one here would be
        // indistinguishable from the files it contains.
        LibraryKind.ConfigBundle or LibraryKind.KubeJsBundle =>
            !isFolder && fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
        // Resource and shader packs: a zip or an unpacked pack folder, the same rule both pack
        // services apply.
        _ => isFolder || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
    };

    /// <summary>The name to show when the user has not chosen one.</summary>
    /// <remarks>Each kind borrows its own page's derivation so an item reads the same in the library
    /// as it does in an instance. A save folder is the one case where the content knows better than
    /// the file name does: level.dat carries the name Minecraft itself shows.</remarks>
    private static string DerivedName(LibraryKind kind, string path, string fileName, bool isFolder) => kind switch
    {
        LibraryKind.ShaderPack => ShaderPackService.PrettyName(fileName),
        LibraryKind.World when isFolder => WorldService.ReadMeta(path).LevelName is { Length: > 0 } level
            ? level
            : fileName,
        LibraryKind.World => Path.GetFileNameWithoutExtension(fileName),
        LibraryKind.Mod => fileName,
        _ => isFolder ? fileName : Path.GetFileNameWithoutExtension(fileName)
    };

    // ── adding ───────────────────────────────────────────────────────────────

    /// <summary>Copies a pack into the library from anywhere on disk: a download, or a file already
    /// installed in an instance.</summary>
    /// <remarks>The source is never touched; <see cref="RelinkAsync"/> makes the source instance share
    /// the library copy afterwards. A same-named item already in the library is kept and the new one is
    /// saved as "name-2.zip", like other install paths, so nothing the user tuned is overwritten.</remarks>
    public Task<LibraryItem> AddAsync(
        string sourcePath, LibraryKind kind, string? displayName = null,
        ModSource? source = null, string? projectId = null, string? versionId = null, string? versionNumber = null,
        CancellationToken ct = default) =>
        Task.Run(() => Add(sourcePath, kind, displayName, source, projectId, versionId, versionNumber), ct);

    /// <inheritdoc cref="AddAsync"/>
    public LibraryItem Add(
        string sourcePath, LibraryKind kind, string? displayName = null,
        ModSource? source = null, string? projectId = null, string? versionId = null, string? versionNumber = null)
    {
        var dir = FolderFor(kind);
        Directory.CreateDirectory(dir);

        var isFolder = Directory.Exists(sourcePath);
        if (!isFolder && !File.Exists(sourcePath))
            throw new FileNotFoundException("That pack is no longer on disk.", sourcePath);

        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar,
                                                                 Path.AltDirectorySeparatorChar));

        // Already in the library (someone browsed to .library and picked a file out of it): nothing to
        // copy, and copying would make a second entry for one pack.
        if (IsUnderRoot(sourcePath))
            return Find(KeyFor(kind, name)) ?? throw new InvalidOperationException("The launcher already has that file.");

        var dest = ShaderPackService.NextFreePath(dir, name);
        long size;
        if (isFolder)
        {
            CopyFolder(sourcePath, dest);
            size = FolderSize(dest);
        }
        else
        {
            File.Copy(sourcePath, dest);
            size = new FileInfo(dest).Length;
        }

        var key = KeyFor(kind, Path.GetFileName(dest));
        MutateIndex(index =>
        {
            var entry = index.GetOrCreate(key);
            entry.DisplayName = (displayName ?? "").Trim();
            entry.AddedAt = DateTimeOffset.Now;
            entry.SizeBytes = size;
            entry.OriginPath = sourcePath;
            entry.Source = source;
            entry.ProjectId = Clean(projectId);
            entry.VersionId = Clean(versionId);
            entry.VersionNumber = Clean(versionNumber);
        });

        AppLog.Log("library", $"Added {key} ({size / 1024} KB) from {sourcePath}");
        return Find(key)!;
    }

    /// <summary>Gives a library item a name of the user's choosing; an empty name restores the derived
    /// one. The name travels with the item to every instance it is applied to afterwards.</summary>
    public void Rename(string key, string? displayName) =>
        MutateIndex(index => index.GetOrCreate(key).DisplayName = (displayName ?? "").Trim());

    // ── default rules ────────────────────────────────────────────────────────

    /// <summary>Makes an item one of the user's defaults, or takes it back out (<c>null</c>, or a
    /// policy with <see cref="ContentDefaultPolicy.Enabled"/> false).</summary>
    /// <remarks>Storage only: nothing is placed or switched on until
    /// <see cref="ContentDefaultsService.ReconcileAsync"/> runs.</remarks>
    public void SetDefaults(string key, ContentDefaultPolicy? policy) =>
        MutateIndex(index => index.GetOrCreate(key).Defaults = policy);

    /// <summary>The item's rule, or a fresh switched-off one so an editor never has to cope with
    /// null.</summary>
    public ContentDefaultPolicy GetDefaults(string key) =>
        (LoadIndex().Items.TryGetValue(key, out var entry) ? entry.Defaults : null)?.Clone()
        ?? ContentDefaultPolicy.Off();

    /// <summary>Records the Mods page's own identity string for a <see cref="LibraryKind.Mod"/>.</summary>
    public void SetModKey(string key, string? modKey) =>
        MutateIndex(index => index.GetOrCreate(key).ModKey = string.IsNullOrWhiteSpace(modKey) ? null : modKey.Trim());

    /// <summary>Which one-off migration of the old per-item compatibility lists has already run.</summary>
    public int DefaultsMigrationVersion => LoadIndex().DefaultsMigration;

    /// <inheritdoc cref="DefaultsMigrationVersion"/>
    public void SetDefaultsMigrationVersion(int version) =>
        MutateIndex(index => index.DefaultsMigration = version);

    /// <summary>
    /// Which shader loader an instance has, as this service answers it.
    /// </summary>
    /// <remarks>Public so the compatibility rules use this same answer. An instance whose mods folder
    /// can't be read reports <see cref="ShaderLoader.Iris"/>, not <see cref="ShaderLoader.None"/>:
    /// unknown is not none, and a guess should never be why the user is refused something.</remarks>
    public ShaderLoader LoaderFor(PackSummary target) => SafeLoader(target);

    /// <summary>"Keep on this machine": never uploaded when a shared instance syncs, and never replaced
    /// by a collaborator's same-named file.</summary>
    /// <remarks>Library files end up in game/, which is shared by default. This is enforced at the sync
    /// boundary (<see cref="IsKeepLocalPath"/>) rather than in rules, which the server's copy can
    /// overwrite.</remarks>
    public void SetKeepLocal(string key, bool keepLocal) =>
        MutateIndex(index => index.GetOrCreate(key).KeepLocal = keepLocal);

    /// <summary>Deletes the library's copy of an item.</summary>
    /// <remarks>Instances holding a link keep working, since the content lives until the last link is
    /// gone. The UI should say so rather than warn about breakage, and mention how many instances hold
    /// it.</remarks>
    public Task RemoveAsync(string key, CancellationToken ct = default) => Task.Run(() => Remove(key), ct);

    /// <inheritdoc cref="RemoveAsync"/>
    public void Remove(string key)
    {
        var item = Find(key);
        if (item is not null)
        {
            if (item.IsFolder) Directory.Delete(item.Path, recursive: true);
            else File.Delete(item.Path);
        }
        MutateIndex(index => index.Items.Remove(key));
        AppLog.Log("library", $"Removed {key} from the library");
    }

    // ── looking before applying ──────────────────────────────────────────────

    /// <summary>
    /// How each instance stands in relation to one item, resolved off the UI thread so the apply card
    /// can open already knowing what it is about to do.
    /// </summary>
    public Task<List<LibraryTargetState>> InspectAsync(
        LibraryItem item, IReadOnlyList<PackSummary> targets, CancellationToken ct = default) =>
        Task.Run(() => targets.Select(t => Inspect(item, t)).ToList(), ct);

    /// <inheritdoc cref="InspectAsync"/>
    public LibraryTargetState Inspect(LibraryItem item, PackSummary target)
    {
        var applied = false;
        var linked = false;
        var ownCopy = false;
        var diverged = false;

        try
        {
            var localPath = InstancePathFor(target.Id, item.Kind, item.FileName);
            var gamePath = GamePathFor(target.Id, item.Kind, item.FileName);

            var localExists = File.Exists(localPath) || Directory.Exists(localPath);
            var gameExists = File.Exists(gamePath) || Directory.Exists(gamePath);

            var localIsLibrary = localExists && PackFolderService.EntriesReferToSameContent(item.Path, localPath);
            var gameIsLibrary = gameExists && PackFolderService.EntriesReferToSameContent(item.Path, gamePath);

            applied = localExists || gameExists;
            linked = localIsLibrary || gameIsLibrary;
            // The overlay never overwrites, so an instance's own game/ file wins at launch and the
            // library link in local/ is inert. Only say so when it really is a different file.
            ownCopy = gameExists && !gameIsLibrary;

            // Recorded as linked but no longer a link: something wrote a temp file and renamed it over the
            // top, replacing the directory entry. The instance now has a private copy.
            var record = LoadIndex().Items.TryGetValue(item.Key, out var e) && e.AppliedTo.TryGetValue(target.Id.ToString("N"), out var a) ? a : null;
            if (record is { Linked: true } && localExists && !localIsLibrary) diverged = true;
        }
        catch (Exception ex)
        {
            // An unreadable instance shows as "will be added" and fails at apply time, where the error can
            // be explained.
            AppLog.LogError("library", ex);
        }

        var loaderMissing = item.Kind == LibraryKind.ShaderPack && SafeLoader(target) == ShaderLoader.None;

        return new LibraryTargetState(target.Id, target.Name, target.IsShared,
            applied, linked, ownCopy, diverged, loaderMissing);
    }

    private ShaderLoader SafeLoader(PackSummary target)
    {
        try { return shaders.DetectLoader(target.Id, target.Name); }
        catch { return ShaderLoader.Iris; }   // unknown is not "none": never warn on a guess
    }

    // ── applying ─────────────────────────────────────────────────────────────

    /// <summary>Puts every item into every instance, in one action.</summary>
    /// <param name="replaceOwnCopy">Remove an instance's own same-named pack in <c>game/</c> so the
    /// library copy is the one that loads. Off by default: that file is the user's, and on a shared
    /// instance deleting it also removes it for everyone else.</param>
    /// <remarks>Applying is not enabling: the file lands in <c>local/</c> and the load order is
    /// untouched, so applying the same packs twice changes nothing.</remarks>
    public Task<LibraryApplyResult> ApplyAsync(
        IReadOnlyList<LibraryItem> items, IReadOnlyList<PackSummary> targets,
        bool replaceOwnCopy = false, IProgress<string>? log = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var results = new List<LibraryApplyTarget>();
            foreach (var item in items)
            {
                foreach (var target in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    results.Add(ApplyOne(item, target, replaceOwnCopy, log));
                }
            }
            settings.Save();
            return new LibraryApplyResult(results);
        }, ct);

    private LibraryApplyTarget ApplyOne(LibraryItem item, PackSummary target, bool replaceOwnCopy, IProgress<string>? log)
    {
        void Report(string m) { log?.Report(m); AppLog.Log("library", m); }

        try
        {
            var localPath = InstancePathFor(target.Id, item.Kind, item.FileName);
            var gamePath = GamePathFor(target.Id, item.Kind, item.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            // The instance's own same-named pack. The launch overlay never overwrites, so unless it is
            // replaced the library file would be applied but never used.
            var gameExists = File.Exists(gamePath) || Directory.Exists(gamePath);
            var gameIsLibrary = gameExists && PackFolderService.EntriesReferToSameContent(item.Path, gamePath);
            if (gameExists && !gameIsLibrary && replaceOwnCopy)
            {
                if (Directory.Exists(gamePath)) Directory.Delete(gamePath, recursive: true);
                else File.Delete(gamePath);
                Report($"{target.Name}: removed its own copy of {item.FileName} so the shared one loads");
                gameExists = false;
            }

            // A different file of that name is already in local/: the user's own copy, or one
            // edited after it was linked. Overwriting it needs replaceOwnCopy, like the game/ case.
            // The applied record is left as is so the divergence stays visible on the page.
            var localExists = File.Exists(localPath) || Directory.Exists(localPath);
            if (localExists && !replaceOwnCopy
                && !PackFolderService.EntriesReferToSameContent(item.Path, localPath))
            {
                Report($"{target.Name}: already has its own {item.FileName}; left it alone");
                return new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.AlreadyThere,
                    "this instance has its own copy here - nothing was overwritten");
            }

            var outcome = Materialise(item, localPath);
            // Recorded from what is on disk afterwards, not from the outcome: an already-linked instance
            // reports AlreadyThere, and recording that as unlinked would hide a later divergence.
            RecordApplied(item.Key, target.Id,
                PackFolderService.EntriesReferToSameContent(item.Path, localPath));
            CarryMetadata(item, target.Id);

            if (outcome == LibraryApplyOutcome.AlreadyThere && !gameExists)
                Report($"{target.Name}: already using the shared copy of {item.FileName}");

            if (gameExists && !gameIsLibrary)
            {
                Report($"{target.Name}: has its own {item.FileName}; the shared one is here but not in use");
                return new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.Shadowed,
                    $"this instance has its own copy of {item.FileName}");
            }

            return new LibraryApplyTarget(target.Id, target.Name, outcome,
                outcome switch
                {
                    LibraryApplyOutcome.Linked => "linked - one copy of the bytes",
                    LibraryApplyOutcome.Copied => "copied (this instance is on another drive, so it cannot share one file)",
                    LibraryApplyOutcome.AlreadyThere => "already using the shared copy",
                    _ => ""
                });
        }
        catch (Exception ex)
        {
            AppLog.LogError("library", ex);
            return new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.Failed, Friendly(ex));
        }
    }

    /// <summary>Puts the library item at <paramref name="destPath"/>, linked if the filesystem allows
    /// it and copied if it does not.</summary>
    private LibraryApplyOutcome Materialise(LibraryItem item, string destPath)
    {
        // Worlds must never be shared: a running game writes its save continuously, so a hard link
        // between instances would corrupt it. Checked here because every apply and re-link goes through
        // this method, and Friendly() passes the message on to the user as is.
        if (item.Kind == LibraryKind.World)
            throw new InvalidOperationException(
                "a world is copied into an instance, never shared with it - use \"copy into instance\" "
                + "(ContentDefaultsService does this for a world default)");

        if (item.IsFolder)
        {
            // A directory has no inode to link; an unpacked pack is a tree of hard-linked files, which
            // is the same one-copy-of-the-bytes deal a file gets.
            if (PackFolderService.EntriesReferToSameContent(item.Path, destPath))
                return LibraryApplyOutcome.AlreadyThere;
            var (linked, copied, _) = PackFolderService.LinkOrCopyTree(item.Path, destPath,
                overwriteExisting: true, preferHardLink: SupportsHardLinks);
            return linked >= copied ? LibraryApplyOutcome.Linked : LibraryApplyOutcome.Copied;
        }

        return PackFolderService.LinkOrCopyFile(item.Path, destPath,
                   overwriteExisting: true, preferHardLink: SupportsHardLinks) switch
        {
            FileLinkOutcome.AlreadySameFile => LibraryApplyOutcome.AlreadyThere,
            FileLinkOutcome.Linked => LibraryApplyOutcome.Linked,
            _ => LibraryApplyOutcome.Copied
        };
    }

    /// <summary>Takes an item back out of an instance.</summary>
    /// <remarks>Deletes the <c>local/</c> link and, if the launch overlay already placed it, the
    /// <c>game/</c> twin (the overlay only adds). The twin is deleted only when it is the same content,
    /// never the instance's own same-named pack.</remarks>
    public Task<LibraryApplyResult> UnapplyAsync(
        IReadOnlyList<LibraryItem> items, IReadOnlyList<PackSummary> targets,
        IProgress<string>? log = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var results = new List<LibraryApplyTarget>();
            foreach (var item in items)
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(UnapplyOne(item, target, log));
            }
            return new LibraryApplyResult(results);
        }, ct);

    private LibraryApplyTarget UnapplyOne(LibraryItem item, PackSummary target, IProgress<string>? log)
    {
        try
        {
            var localPath = InstancePathFor(target.Id, item.Kind, item.FileName);
            var gamePath = GamePathFor(target.Id, item.Kind, item.FileName);

            var removed = false;
            // Both sides are compared against the library file, never against each other, so an
            // instance's own same-named pack is never mistaken for one of ours and never deleted.
            if (PackFolderService.EntriesReferToSameContent(item.Path, gamePath)) removed |= DeleteEntry(gamePath);
            if (PackFolderService.EntriesReferToSameContent(item.Path, localPath)) removed |= DeleteEntry(localPath);

            ForgetApplied(item.Key, target.Id);

            if (!removed)
                return new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.NotPresent,
                    "no shared copy here");

            log?.Report($"{target.Name}: removed {item.FileName}");
            AppLog.Log("library", $"{target.Name}: removed the library copy of {item.FileName}");
            return new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.Removed,
                "removed - the pack itself stays, and other instances are unaffected");
        }
        catch (Exception ex)
        {
            AppLog.LogError("library", ex);
            return new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.Failed, Friendly(ex));
        }
    }

    /// <summary>Puts a diverged instance back on the library copy.</summary>
    /// <remarks>Only while both are still the same bytes (compared in full), since an edited file is the
    /// user's work. Zips only: an unpacked folder reports <see cref="LibraryApplyOutcome.NotPresent"/>,
    /// so the UI shouldn't offer it for one.</remarks>
    public Task<LibraryApplyResult> RelinkAsync(
        LibraryItem item, IReadOnlyList<PackSummary> targets, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var results = new List<LibraryApplyTarget>();
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var localPath = InstancePathFor(target.Id, item.Kind, item.FileName);
                    if (item.IsFolder || !File.Exists(localPath) || !File.Exists(item.Path))
                    {
                        results.Add(new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.NotPresent,
                            "nothing to re-link"));
                        continue;
                    }
                    if (!SameBytes(item.Path, localPath))
                    {
                        results.Add(new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.Failed,
                            "this instance's copy has been edited - re-linking would discard those changes"));
                        continue;
                    }
                    var outcome = Materialise(item, localPath);
                    RecordApplied(item.Key, target.Id,
                        PackFolderService.EntriesReferToSameContent(item.Path, localPath));
                    results.Add(new LibraryApplyTarget(target.Id, target.Name, outcome, "re-linked"));
                }
                catch (Exception ex)
                {
                    AppLog.LogError("library", ex);
                    results.Add(new LibraryApplyTarget(target.Id, target.Name, LibraryApplyOutcome.Failed, Friendly(ex)));
                }
            }
            return new LibraryApplyResult(results);
        }, ct);

    // ── the sync boundary ────────────────────────────────────────────────────

    /// <summary>True when a game/-relative path is a library item the user marked "keep on this
    /// machine".</summary>
    /// <remarks>Checked by file identity (volume serial and file index), not by name, since a library
    /// pack and the instance's own pack look the same in <c>resourcepacks/</c>. The keep-local map is
    /// checked first, so an instance without library items never opens a handle.</remarks>
    public bool IsKeepLocalPath(string gameDir, string relativePath)
    {
        try
        {
            var norm = relativePath.Replace('\\', '/');
            var slash = norm.IndexOf('/');
            if (slash <= 0) return false;
            if (KindForFolder(norm[..slash]) is not { } kind) return false;

            var rest = norm[(slash + 1)..];
            if (rest.Length == 0) return false;
            var nested = rest.IndexOf('/');
            var topName = nested < 0 ? rest : rest[..nested];

            var map = KeepLocalMap();
            if (!map.TryGetValue(KeyFor(kind, topName), out var libraryPath)) return false;

            // For an unpacked pack the shared file is the one inside the folder, not the folder itself.
            // The path can come from a server manifest, so both sides are resolved inside their own
            // folder and nothing outside them is ever opened to answer.
            var libraryFile = nested < 0
                ? libraryPath
                : PathSafety.ResolveInside(libraryPath, rest[(nested + 1)..]);
            var abs = PathSafety.ResolveInside(gameDir, norm);
            if (libraryFile is null || abs is null) return false;

            return PackFolderService.PathsReferToSameFile(abs, libraryFile);
        }
        catch { return false; }   // an unanswerable path is not a library item; sync proceeds as before
    }

    /// <summary>True when an absolute path is one of this library's files, whatever its keep-local
    /// setting. For "where did this pack come from?" on a page, not for the sync boundary.</summary>
    public bool IsLibraryLink(string absolutePath)
    {
        try
        {
            foreach (var item in Scan())
                if (PackFolderService.EntriesReferToSameContent(item.Path, absolutePath)) return true;
        }
        catch { /* unreadable library: not one of ours */ }
        return false;
    }

    /// <summary>One line for a sync log: which library items were held back and how many files.</summary>
    public static string Describe(IReadOnlyCollection<string> relativePaths)
    {
        if (relativePaths.Count == 0) return "";
        var names = relativePaths
            .Select(p => p.Replace('\\', '/'))
            .Select(p => { var parts = p.Split('/'); return parts.Length > 1 ? $"{parts[0]}/{parts[1]}" : p; })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count <= 4
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(4))} and {names.Count - 4} more";
    }

    // ── index.json ───────────────────────────────────────────────────────────

    private IndexFile LoadIndex()
    {
        lock (_lock)
        {
            var path = IndexPath;
            DateTime stamp;
            try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { stamp = DateTime.MinValue; }

            // Re-read only when the file changed: the sync boundary asks this for every path in a manifest.
            if (_index is not null && stamp == _indexStamp) return _index;

            try
            {
                _index = File.Exists(path)
                    ? JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(path)) ?? new IndexFile()
                    : new IndexFile();
            }
            catch (Exception ex)
            {
                // A corrupt index costs display names and keep-local flags, not content: the files are
                // still there and still linked. Starting empty beats refusing to open the page.
                AppLog.LogError("library", ex);
                _index = new IndexFile();
            }
            _indexStamp = stamp;
            _keepLocal = null;
            return _index;
        }
    }

    private void MutateIndex(Action<IndexFile> mutate)
    {
        lock (_lock)
        {
            var index = LoadIndex();
            mutate(index);
            _keepLocal = null;
            try
            {
                Directory.CreateDirectory(Root);
                var tmp = IndexPath + ".cl-tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(index, JsonOpts));
                File.Move(tmp, IndexPath, overwrite: true);
                _indexStamp = File.GetLastWriteTimeUtc(IndexPath);
            }
            catch (Exception ex)
            {
                AppLog.LogError("library", ex);
            }
        }
    }

    private void RecordApplied(string key, Guid packId, bool linked) =>
        MutateIndex(index => index.GetOrCreate(key).AppliedTo[packId.ToString("N")] =
            new AppliedRecord { Linked = linked, AppliedAt = DateTimeOffset.Now });

    private void ForgetApplied(string key, Guid packId) =>
        MutateIndex(index =>
        {
            if (index.Items.TryGetValue(key, out var e)) e.AppliedTo.Remove(packId.ToString("N"));
        });

    /// <summary>The instance-side display name and provenance, copied from the library entry.</summary>
    /// <remarks>So the same pack in several instances has one display name and knows which store
    /// listing it came from.
    /// <para>Written under both the <c>local/</c> and the <c>game/</c> key: the pack starts in
    /// <c>local/</c> and the overlay puts it in <c>game/</c> on first launch, and the name must not
    /// change when the page switches keys. The caller saves.</para></remarks>
    private void CarryMetadata(LibraryItem item, Guid packId)
    {
        if (item.Kind == LibraryKind.ShaderPack)
        {
            foreach (var origin in new[] { ShaderPackOrigin.Local, ShaderPackOrigin.Game })
            {
                var entry = settings.GetOrCreateShaderPack(ShaderPackService.Key(packId, item.FileName, origin));
                if (item.DisplayName.Length > 0) entry.DisplayName = item.DisplayName;
                entry.Source = item.Source;
                entry.ProjectId = item.ProjectId;
                entry.VersionId = item.VersionId;
                entry.VersionNumber = item.VersionNumber;
            }
        }
        else
        {
            foreach (var origin in new[] { ResourcePackOrigin.Local, ResourcePackOrigin.Game })
            {
                var key = ResourcePackService.Key(packId, item.FileName, origin);
                if (!settings.ResourcePacks.TryGetValue(key, out var entry))
                {
                    entry = new ResourcePackEntry();
                    settings.ResourcePacks[key] = entry;
                }
                if (item.DisplayName.Length > 0) entry.DisplayName = item.DisplayName;
                entry.Source = item.Source;
                entry.ProjectId = item.ProjectId;
                entry.VersionId = item.VersionId;
                entry.VersionNumber = item.VersionNumber;
            }
        }
    }

    private Dictionary<string, string> KeepLocalMap()
    {
        lock (_lock)
        {
            var index = LoadIndex();          // drops the cache below when the file has moved on
            if (_keepLocal is not null) return _keepLocal;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, entry) in index.Items)
            {
                if (!entry.KeepLocal) continue;
                var slash = key.IndexOf('/');
                if (slash <= 0) continue;
                if (KindForFolder(key[..slash]) is not { } kind) continue;
                map[key] = Path.Combine(FolderFor(kind), key[(slash + 1)..]);
            }
            return _keepLocal = map;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private bool IsUnderRoot(string path)
    {
        try
        {
            var root = Path.GetFullPath(Root);
            var full = Path.GetFullPath(path);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool DeleteEntry(string path)
    {
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); return true; }
        if (File.Exists(path)) { File.Delete(path); return true; }
        return false;
    }

    private static bool SameBytes(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            var bufA = new byte[81920];
            var bufB = new byte[81920];
            int read;
            while ((read = sa.ReadAtLeast(bufA, bufA.Length, throwOnEndOfStream: false)) > 0)
            {
                var readB = sb.ReadAtLeast(bufB, read, throwOnEndOfStream: false);
                if (readB != read) return false;
                if (!bufA.AsSpan(0, read).SequenceEqual(bufB.AsSpan(0, read))) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static void CopyFolder(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            // The only file a folder copy may skip. A world's session.lock is a zero-byte marker the game
            // holds open and rewrites on load; WorldService.CopyWorldAsync skips it for the same reason.
            if (Path.GetFileName(relative).Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dest, relative), overwrite: true);
        }
    }

    private static long FolderSize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>What to put in front of the user. The exception itself goes to the log.</summary>
    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        // Refusals raised here already read as a sentence for the user (mainly the world guard in
        // Materialise), and "could not be applied" would hide them.
        InvalidOperationException refusal => refusal.Message,
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) => "not enough disk space",
        IOException => "the file is in use",
        _ => "could not be applied"
    };

    // ── stored shape ─────────────────────────────────────────────────────────

    private sealed class IndexFile
    {
        public int Version { get; set; } = 1;

        /// <summary>Which one-off migration of the old per-item "compatible with" lists has run. See
        /// <see cref="ContentDefaultsService.MigrateManualCompatibility"/>.</summary>
        /// <remarks>Kept here rather than in AppSettings so the record sits beside the thing it
        /// migrated into, and so a user who moves their instances folder takes it with them.</remarks>
        public int DefaultsMigration { get; set; }

        public Dictionary<string, LibraryIndexEntry> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public LibraryIndexEntry GetOrCreate(string key)
        {
            if (!Items.TryGetValue(key, out var e)) Items[key] = e = new LibraryIndexEntry();
            return e;
        }
    }

    /// <summary>What index.json remembers per item. Public for the JSON serializer; nothing outside
    /// this service should read it. <see cref="LibraryItem"/> is the view of it joined against what is
    /// on disk.</summary>
    public sealed class LibraryIndexEntry
    {
        public string DisplayName { get; set; } = "";
        public DateTimeOffset AddedAt { get; set; }
        public long SizeBytes { get; set; }
        public bool KeepLocal { get; set; }
        /// <summary>Where it was added from, for "this came out of my Downloads folder" on the page.</summary>
        public string? OriginPath { get; set; }
        public ModSource? Source { get; set; }
        public string? ProjectId { get; set; }
        public string? VersionId { get; set; }
        public string? VersionNumber { get; set; }

        /// <summary>The rule that makes this item one of the user's defaults, or null for an ordinary
        /// manual item. Everything the defaults engine obeys is in here.</summary>
        /// <remarks>Optional, so an index.json from an older build (no such property) deserialises to
        /// null, meaning "not a default". No migration is needed.</remarks>
        public ContentDefaultPolicy? Defaults { get; set; }

        /// <summary>Opaque identity for a <see cref="LibraryKind.Mod"/>, written and read by the
        /// global Mods page (the mod-inventory candidate key). Nothing else interprets it.</summary>
        public string? ModKey { get; set; }

        /// <summary>Instance id ("N" form) to how it was applied. The Linked flag is what makes
        /// divergence detectable: "we linked this and it is no longer a link" is a different fact from
        /// "we had to copy it because the volume would not take a link".</summary>
        public Dictionary<string, AppliedRecord> AppliedTo { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class AppliedRecord
    {
        public bool Linked { get; set; }
        public DateTimeOffset AppliedAt { get; set; }
    }
}
