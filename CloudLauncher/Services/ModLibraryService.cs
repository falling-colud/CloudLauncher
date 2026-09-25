using System.IO;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Mod semantics on top of <see cref="ContentLibraryService"/>: the user's default mod set, the rules
/// that decide which instances get it, and the guard a mod needs that a texture pack does not.
/// </summary>
/// <remarks>
/// <para>Owns no storage: content and rules live in <see cref="ContentLibraryService"/>, placement in
/// <see cref="ContentDefaultsService"/>.</para>
/// <para>Two versions of one mod under different file names crash the game, and names can't catch
/// that; <see cref="ConflictsWithInstalled"/> compares mod identity instead.</para>
/// <para>Disabling a mod renames it to <c>.jar.disabled</c>, which file-name lookups miss;
/// <see cref="HoldsCopy"/> checks both names. <c>Inspect</c> and <c>UnapplyAsync</c> in
/// <see cref="ContentLibraryService"/> don't yet.</para>
/// <para>No global update yet: <c>ModUpdater</c> replaces the instance's jar and leaves the library
/// copy stale.</para>
/// </remarks>
public sealed class ModLibraryService(
    ContentLibraryService library,
    ContentDefaultsService defaults,
    PackFolderService packs)
{
    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>Every mod in the library, newest first.</summary>
    public IReadOnlyList<LibraryItem> Items() => library.Scan(LibraryKind.Mod);

    /// <summary>One library mod by its key, or null when it is no longer there.</summary>
    public LibraryItem? Find(string key)
    {
        var item = library.Find(key);
        return item?.Kind == LibraryKind.Mod ? item : null;
    }

    /// <summary>The item's rule, or a fresh switched-off one so an editor never has to cope with
    /// null.</summary>
    public ContentDefaultPolicy Rules(string key) => library.GetDefaults(key);

    /// <summary>
    /// Writes an item's rule.
    /// </summary>
    /// <remarks>Storage only; nothing is placed until <see cref="ReconcileAsync"/> runs.
    /// <paramref name="policy"/>'s <see cref="ContentDefaultPolicy.KeepLocal"/> is mirrored onto the
    /// item so a default mod set isn't uploaded to every collaborator on shared instances.</remarks>
    public void SetRules(string key, ContentDefaultPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        library.SetDefaults(key, policy);
        library.SetKeepLocal(key, policy.KeepLocal);
    }

    /// <summary>Records the mod-inventory identity key on a library item, so the global list can line
    /// a default that is in no instance up with the same mod once it is installed somewhere.</summary>
    public void SetModKey(string key, string? modKey) => library.SetModKey(key, modKey);

    // ── admission ────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether one default belongs in one instance, and the sentence saying why either way.
    /// </summary>
    /// <remarks>Uses <see cref="ContentCompatibility.Matches"/>, the launcher's only matcher, plus the
    /// folder the mod is filed under, same as the planner, so this never disagrees with the
    /// reconciler.</remarks>
    public bool Admits(LibraryItem item, PackSummary instance, out string why)
    {
        ArgumentNullException.ThrowIfNull(item);
        var policy = library.GetDefaults(item.Key);
        if (!policy.Enabled)
        {
            why = "this one is not a default, so it is only ever placed by hand.";
            return false;
        }
        // Iris rather than None: the argument is unused for a mod, and None is the value that blocks.
        return ContentCompatibility.Matches(policy, instance, item, ShaderLoader.Iris, out why,
                                            ContentFolderRules.EffectiveFor(item));
    }

    /// <summary>Which of the user's default mods belong in one instance.</summary>
    public IReadOnlyList<LibraryItem> AdmittedFor(PackSummary instance) =>
        Items().Where(i => Admits(i, instance, out _)).ToList();

    /// <summary>
    /// Whether placing <paramref name="item"/> in an instance would put a second copy of the same mod
    /// there under a different file name.
    /// </summary>
    /// <remarks>The crash guard (see the class remarks). Uses the instance's already-scanned rows, so a
    /// batch costs one scan. Jars with no known identity only match by file name, which the library's
    /// shadow handling already covers.</remarks>
    /// <param name="item">The library mod about to be placed.</param>
    /// <param name="installed">That instance's rows, from <see cref="PackModInventory.LoadAsync"/>.</param>
    /// <param name="why">Names the file already there, when the answer is yes.</param>
    public static bool ConflictsWithInstalled(
        LibraryItem item, IReadOnlyList<PackMod> installed, out string why)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(installed);
        why = "";
        if (item.ModKey is not { Length: > 0 } key || key.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var mod in installed)
        {
            // Its own copy of this item is not a conflict with itself.
            if (string.Equals(mod.FileName, item.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!mod.CandidateKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            why = $"this instance already has {mod.FileName}, which is the same mod under another "
                + "file name - loading both would stop the game starting.";
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when an instance holds this library mod's file, as <c>.jar</c> or <c>.jar.disabled</c>.
    /// </summary>
    /// <remarks>Checks both <c>local/</c> and <c>game/</c>, since the launch overlay moves files between
    /// them.</remarks>
    public bool HoldsCopy(Guid packId, LibraryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            foreach (var folder in new[]
                     {
                         Path.Combine(packs.LocalDir(packId), "mods"),
                         Path.Combine(packs.GameDir(packId), "mods")
                     })
            foreach (var name in new[] { item.FileName, item.FileName + ".disabled" })
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)
                    && PackFolderService.EntriesReferToSameContent(item.Path, candidate)) return true;
            }
        }
        catch (Exception ex)
        {
            // An unreadable folder counts as "not holding it"; the apply will then fail visibly.
            AppLog.LogError("mod-library", ex);
        }
        return false;
    }

    // ── adding ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies a jar from anywhere on disk into the library, optionally as a default.
    /// </summary>
    /// <remarks>The source file is never touched. If a mod with that file name or identity key is
    /// already in the library, the existing item is returned and the rule applied to it: a mod's file
    /// name carries its version, and "add every mod from this instance" run twice must not create
    /// "name-2.jar" copies of everything.</remarks>
    /// <param name="sourcePath">The jar to copy in.</param>
    /// <param name="policy">A rule to attach, or null for an ordinary library mod placed by hand.</param>
    /// <param name="modKey">The mod's inventory identity (<c>modrinth:...</c>, <c>curseforge:...</c> or
    /// <c>file:...</c>), so a default that is in no instance still lines up with the same mod once it
    /// is installed. Null derives one from the file name.</param>
    public async Task<LibraryItem> AddFromFileAsync(
        string sourcePath, ContentDefaultPolicy? policy = null,
        ModSource? source = null, string? projectId = null,
        string? versionId = null, string? versionNumber = null,
        string? modKey = null, CancellationToken ct = default)
    {
        var name = Path.GetFileName(sourcePath);
        var existing = Items().FirstOrDefault(i =>
            string.Equals(i.FileName, name, StringComparison.OrdinalIgnoreCase)
            || (modKey is { Length: > 0 } && string.Equals(i.ModKey, modKey, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            if (policy is not null) SetRules(existing.Key, policy);
            if (modKey is { Length: > 0 }) library.SetModKey(existing.Key, modKey);
            return existing;
        }

        var item = await library.AddAsync(sourcePath, LibraryKind.Mod, null,
                                          source, projectId, versionId, versionNumber, ct);
        library.SetModKey(item.Key, modKey is { Length: > 0 }
            ? modKey
            : ModMetadataService.KeyFor(null, null, item.FileName));
        if (policy is not null) SetRules(item.Key, policy);
        return item;
    }

    /// <summary>
    /// Takes mods that are already inside one instance into the library, and leaves that instance
    /// sharing the library's bytes instead of keeping its own private copy.
    /// </summary>
    /// <remarks>
    /// <para>Goes through <see cref="ContentDefaultsService.AddFromInstanceAsync"/> (add, apply, relink,
    /// with dedupe) and also records each mod's inventory key, which
    /// <see cref="ConflictsWithInstalled"/> relies on.</para>
    /// <para><paramref name="makeDefault"/> seeds the rule from the instance's Minecraft version and
    /// loader, so the mods aren't offered to incompatible instances.</para>
    /// </remarks>
    public async Task<ContentAddResult> AddFromInstanceAsync(
        PackSummary instance, IReadOnlyList<PackMod> mods, bool makeDefault,
        ContentDefaultPolicy? policyTemplate = null,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mods);

        var requests = mods.Select(m => new ContentAddRequest(
            SourcePath: m.FilePath,
            Kind: LibraryKind.Mod,
            DisplayName: null,
            Source: m.PrimarySource,
            ProjectId: m.PrimaryMod?.Id,
            VersionId: m.PrimaryVersion?.Id,
            VersionNumber: m.PrimaryVersion?.VersionNumber)).ToList();

        var result = await defaults.AddFromInstanceAsync(
            instance, requests, makeDefault, policyTemplate, log, ct);

        // Match outcomes back to their mods by source path, so each item records the mod's identity key
        // rather than a name-derived one.
        var byPath = mods.ToDictionary(m => m.FilePath, m => m, StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in result.Items)
        {
            if (outcome.Item is null) continue;
            if (!byPath.TryGetValue(outcome.SourcePath, out var mod)) continue;
            library.SetModKey(outcome.Item.Key, mod.Key);
        }
        return result;
    }

    // ── placing and removing ─────────────────────────────────────────────────

    /// <summary>Brings one instance in line with the user's defaults, for every kind of content: the
    /// engine plans them together, and per-kind runs would rescan the library each time.</summary>
    /// <remarks>While the game is running in that instance it refuses via the result instead of
    /// throwing.</remarks>
    public Task<ContentReconcileResult> ReconcileAsync(PackSummary instance, CancellationToken ct = default) =>
        defaults.ReconcileAsync(instance, activate: true, ct: ct);

    /// <summary>Puts one library mod into one instance by hand, outside the rules.</summary>
    public Task<LibraryApplyResult> ApplyAsync(
        IReadOnlyList<LibraryItem> items, IReadOnlyList<PackSummary> targets,
        IProgress<string>? log = null, CancellationToken ct = default) =>
        library.ApplyAsync(items, targets, replaceOwnCopy: false, log, ct);

    /// <summary>Takes library mods back out of instances. The bytes survive in the library.</summary>
    public Task<LibraryApplyResult> UnapplyAsync(
        IReadOnlyList<LibraryItem> items, IReadOnlyList<PackSummary> targets,
        IProgress<string>? log = null, CancellationToken ct = default) =>
        library.UnapplyAsync(items, targets, log, ct);

    /// <summary>
    /// Deletes the library's copy of a mod.
    /// </summary>
    /// <remarks>Instances holding a hard link keep working, since the content lives until the last link
    /// goes. Only the ability to hand it to more instances is lost.</remarks>
    public Task RemoveAsync(string key, CancellationToken ct = default) => library.RemoveAsync(key, ct);

    /// <summary>Whether the library and the instances are on one volume, so a placed mod really is one
    /// copy of the bytes rather than a copy per instance.</summary>
    public bool SupportsHardLinks => library.SupportsHardLinks;

}

/// <summary>
/// The global Mods page's folders: a name, and the mods filed under it.
/// </summary>
/// <remarks>
/// <para>Per-machine, like the other folder maps. Those live in <c>AppSettings</c>, and this could
/// move into an <c>AppSettings.ModFolders</c> dictionary with the same shape.</para>
/// <para>Keyed by mod identity (e.g. <c>modrinth:AANobbMI</c>) rather than file name, so folders
/// survive mod updates.</para>
/// </remarks>
public sealed class ModFolderStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// The one in-memory document, shared by every instance of this store.
    /// </summary>
    /// <remarks>Static because the Mods page and <see cref="ContentFolderRules"/> each create their own
    /// store, and a rule saved on the page must be visible to the planner right away. Nothing outside
    /// this process writes the file.</remarks>
    private static readonly object _lock = new();
    private static Document? _doc;

    private static string Path_ => Path.Combine(AppSettings.DataRootPath, "global-mod-folders.json");

    /// <summary>
    /// Names a folder may not have.
    /// </summary>
    /// <remarks>Checked on create and on rename. A leading colon is reserved for the launcher's own
    /// pseudo-folders and <c>team:</c> for shared-instance folders.</remarks>
    public static bool IsReservedName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length == 0
            || trimmed.StartsWith(':')
            || trimmed.StartsWith("team:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why a name was refused, for the status line. Empty when it is fine.</summary>
    public static string ReservedReason(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "A folder needs a name.";
        if (trimmed.StartsWith(':')) return "A folder name cannot start with ':' - that prefix is the launcher's.";
        if (trimmed.StartsWith("team:", StringComparison.OrdinalIgnoreCase))
            return "A folder name cannot start with 'team:' - that prefix is for a shared instance's own folders.";
        return "";
    }

    /// <summary>Every folder, in the order they were made.</summary>
    public IReadOnlyList<string> Names()
    {
        lock (_lock) return Load().Folders.Keys.ToList();
    }

    /// <summary>The mod keys filed under one folder.</summary>
    public IReadOnlyList<string> Members(string folder)
    {
        lock (_lock)
            return Load().Folders.TryGetValue(folder, out var list) ? list.ToList() : [];
    }

    /// <summary>How many mods are in a folder.</summary>
    public int Count(string folder)
    {
        lock (_lock)
            return Load().Folders.TryGetValue(folder, out var list) ? list.Count : 0;
    }

    /// <summary>True when that folder holds that mod.</summary>
    public bool Contains(string folder, string modKey)
    {
        lock (_lock)
            return Load().Folders.TryGetValue(folder, out var list)
                && list.Contains(modKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The folders one mod is in.</summary>
    public IReadOnlyList<string> FoldersOf(string modKey)
    {
        lock (_lock)
            return Load().Folders
                .Where(kv => kv.Value.Contains(modKey, StringComparer.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
    }

    /// <summary>The rule on one mod folder, or null when it has none.</summary>
    /// <remarks>Read fresh each time: the dialog edits a <see cref="ContentFolderRule.Clone"/> and must
    /// not write through to the store before Save.</remarks>
    public ContentFolderRule? GetRule(string folder)
    {
        lock (_lock)
            return Load().FolderRules.TryGetValue(folder, out var rule) ? rule : null;
    }

    /// <summary>
    /// Writes a mod folder's rule, or drops it when there is nothing worth storing.
    /// </summary>
    /// <remarks>Saves immediately, unlike the <c>AppSettings</c> mutators. An empty, switched-off rule
    /// is dropped; a switched-off rule with text is kept so turning it back on doesn't lose it.</remarks>
    public void SetRule(string folder, ContentFolderRule? rule)
    {
        var key = (folder ?? "").Trim();
        if (key.Length == 0) return;
        lock (_lock)
        {
            var doc = Load();
            if (!doc.Folders.ContainsKey(key)) return;
            if (rule is null || (!rule.Enabled && !rule.HasAnyRule))
            {
                if (!doc.FolderRules.Remove(key)) return;
            }
            else
            {
                doc.FolderRules[key] = rule;
            }
            Save(doc);
        }
    }

    /// <summary>Creates a folder. Returns false when the name is reserved or already taken.</summary>
    public bool Create(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (IsReservedName(trimmed)) return false;
        lock (_lock)
        {
            var doc = Load();
            if (doc.Folders.ContainsKey(trimmed)) return false;
            doc.Folders[trimmed] = [];
            Save(doc);
            return true;
        }
    }

    /// <summary>Renames a folder, keeping its members and its place in the order. Returns false when
    /// the new name is reserved or already taken, same as <see cref="Create"/>.</summary>
    public bool Rename(string oldName, string newName)
    {
        var trimmed = (newName ?? "").Trim();
        if (IsReservedName(trimmed)) return false;
        lock (_lock)
        {
            var doc = Load();
            if (!doc.Folders.TryGetValue(oldName, out var members)) return false;
            if (string.Equals(oldName, trimmed, StringComparison.Ordinal)) return true;
            if (doc.Folders.ContainsKey(trimmed)) return false;

            // Rebuilt in order rather than removed and re-added, since position is the display order.
            var rebuilt = new Dictionary<string, List<string>>(doc.Folders.Count, StringComparer.Ordinal);
            foreach (var (key, value) in doc.Folders)
                rebuilt[string.Equals(key, oldName, StringComparison.Ordinal) ? trimmed : key] = value;
            doc.Folders = rebuilt;

            // Move the rule in the same rebuild. A rule left under the old name would reattach to the next
            // folder given that name.
            var rebuiltRules = new Dictionary<string, ContentFolderRule>(doc.FolderRules.Count, StringComparer.Ordinal);
            foreach (var (key, value) in doc.FolderRules)
            {
                if (string.Equals(key, trimmed, StringComparison.Ordinal)) continue;
                rebuiltRules[string.Equals(key, oldName, StringComparison.Ordinal) ? trimmed : key] = value;
            }
            doc.FolderRules = rebuiltRules;
            if (string.Equals(doc.Active, oldName, StringComparison.Ordinal)) doc.Active = trimmed;
            Save(doc);
            return true;
        }
    }

    /// <summary>Deletes a folder. The mods in it are untouched.</summary>
    public void Delete(string name)
    {
        lock (_lock)
        {
            var doc = Load();
            if (!doc.Folders.Remove(name)) return;
            doc.FolderRules.Remove(name);
            if (string.Equals(doc.Active, name, StringComparison.Ordinal)) doc.Active = null;
            Save(doc);
        }
    }

    /// <summary>Puts a mod in a folder, or takes it out.</summary>
    public void SetMembership(string folder, string modKey, bool member)
    {
        lock (_lock)
        {
            var doc = Load();
            if (!doc.Folders.TryGetValue(folder, out var list)) return;
            var has = list.Contains(modKey, StringComparer.OrdinalIgnoreCase);
            if (has == member) return;
            if (member) list.Add(modKey);
            else list.RemoveAll(k => string.Equals(k, modKey, StringComparison.OrdinalIgnoreCase));
            Save(doc);
        }
    }

    /// <summary>
    /// Drops a mod out of every folder.
    /// </summary>
    /// <remarks>Called when a mod leaves the set entirely, so folder counts don't include mods that are
    /// gone.</remarks>
    public void Forget(string modKey)
    {
        lock (_lock)
        {
            var doc = Load();
            var changed = false;
            foreach (var list in doc.Folders.Values)
                changed |= list.RemoveAll(k => string.Equals(k, modKey, StringComparison.OrdinalIgnoreCase)) > 0;
            if (changed) Save(doc);
        }
    }

    /// <summary>
    /// The folder the page was last looking at, or null for "everything".
    /// </summary>
    /// <remarks>A name that is no longer a folder reads as null, and that is saved, so the page never
    /// stays filtered to a missing folder.</remarks>
    public string? Active
    {
        get
        {
            lock (_lock)
            {
                var doc = Load();
                if (doc.Active is not { Length: > 0 } name) return null;
                if (doc.Folders.ContainsKey(name)) return name;
                doc.Active = null;
                Save(doc);
                return null;
            }
        }
        set
        {
            lock (_lock)
            {
                var doc = Load();
                var wanted = value is { Length: > 0 } name && doc.Folders.ContainsKey(name) ? name : null;
                if (string.Equals(doc.Active, wanted, StringComparison.Ordinal)) return;
                doc.Active = wanted;
                Save(doc);
            }
        }
    }

    private Document Load()
    {
        if (_doc is not null) return _doc;
        try
        {
            var path = Path_;
            _doc = File.Exists(path)
                ? JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOpts) ?? new Document()
                : new Document();
        }
        catch (Exception ex)
        {
            // A corrupt folder map only loses the arrangement, so start empty rather than fail the page.
            AppLog.LogError("mod-folders", ex);
            _doc = new Document();
        }
        return _doc;
    }

    private static void Save(Document doc)
    {
        try
        {
            var path = Path_;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Write to temp and rename: a torn write would leave an unparseable file, which Load() resets
            // to empty.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("mod-folders", ex);
        }
    }

    /// <summary>The stored shape. Public only so the serializer can see it.</summary>
    public sealed class Document
    {
        public int Version { get; set; } = 1;

        /// <summary>Folder name to the mod keys in it. Ordinal, and ordered: position is the order the
        /// chips are drawn in.</summary>
        public Dictionary<string, List<string>> Folders { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Folder name to that folder's rule ("only use the mods in here where I say").</summary>
        /// <remarks>A separate map rather than a richer value in <see cref="Folders"/>: the stored value
        /// is a JSON array, and changing its shape would empty every existing folder list on load. Most
        /// folders have no entry here.</remarks>
        public Dictionary<string, ContentFolderRule> FolderRules { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The folder the page was last filtered to, or null.</summary>
        public string? Active { get; set; }
    }
}
