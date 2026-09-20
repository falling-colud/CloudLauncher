using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>
/// Loads and saves per-pack mod flags from <c>game/.cloudlauncher/mods.json</c>. Because that
/// folder lives under <c>game/</c> and the default rules route <c>.cloudlauncher/</c> to Shared,
/// the flags sync to collaborators through the normal pack pipeline — "saved to the modpack".
///
/// Mods are keyed by a stable identity: the source project id when known (<c>modrinth:…</c> /
/// <c>curseforge:…</c>), which survives version updates, falling back to the file name for
/// unidentified jars. Reads probe every candidate key so a mod recognised under one key in a
/// later session still finds its flags; writes consolidate onto the primary key.
/// </summary>
public sealed class ModMetadataService
{
    private readonly PackFolderService _packs;
    private readonly AppSettings _settings;
    private readonly Dictionary<Guid, PackModMetadata> _cache = new();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ModMetadataService(PackFolderService packs, AppSettings settings) { _packs = packs; _settings = settings; }

    // ── key derivation ───────────────────────────────────────────────────────

    /// <summary>The primary (canonical) key for a mod — source id preferred, file name fallback.</summary>
    public static string KeyFor(ModSummary? modrinth, ModSummary? curseForge, string fileName)
    {
        if (modrinth is not null)   return $"modrinth:{modrinth.Id}";
        if (curseForge is not null) return $"curseforge:{curseForge.Id}";
        return $"file:{NormalizeFileName(fileName)}";
    }

    /// <summary>All keys a mod could be stored under, most-stable first.</summary>
    public static IReadOnlyList<string> CandidateKeys(ModSummary? modrinth, ModSummary? curseForge, string fileName)
    {
        var list = new List<string>(3);
        if (modrinth is not null)   list.Add($"modrinth:{modrinth.Id}");
        if (curseForge is not null) list.Add($"curseforge:{curseForge.Id}");
        list.Add($"file:{NormalizeFileName(fileName)}");
        return list;
    }

    private static string NormalizeFileName(string fileName)
    {
        var n = fileName.Trim();
        if (n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            n = n[..^".disabled".Length];
        return n.ToLowerInvariant();
    }

    // ── load / save ──────────────────────────────────────────────────────────

    private string? TryMetaPath(Guid packId)
    {
        try { return Path.Combine(_packs.GameDir(packId), ".cloudlauncher", "mods.json"); }
        catch { return null; } // pack folder not materialised yet
    }

    public PackModMetadata Load(Guid packId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(packId, out var cached)) return cached;

            var doc = new PackModMetadata();
            var path = TryMetaPath(packId);
            if (path is not null && File.Exists(path))
            {
                try
                {
                    doc = JsonSerializer.Deserialize<PackModMetadata>(File.ReadAllText(path), JsonOpts) ?? new();
                    Migrate(doc);
                }
                catch { doc = new PackModMetadata(); }
            }
            _cache[packId] = doc;
            return doc;
        }
    }

    public void Save(Guid packId)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(packId, out var doc)) return;
            var path = TryMetaPath(packId);
            if (path is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Temp + atomic rename: a torn File.WriteAllText (crash/power loss mid-write) would
                // leave an unparseable mods.json, which Load() silently resets to an empty doc —
                // wiping every priority/category/side flag, which then syncs to collaborators.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
                File.Move(tmp, path, overwrite: true);
            }
            catch { /* best-effort; flags are non-critical */ }
        }
    }

    /// <summary>Drops the in-memory copy so the next <see cref="Load"/> re-reads from disk
    /// (e.g. after a sync pulls a collaborator's updated flags).</summary>
    public void Invalidate(Guid packId)
    {
        lock (_lock) { _cache.Remove(packId); }
    }

    // ── per-mod flags ──────────────────────────────────────────────────────────

    /// <summary>Returns the stored flags for a mod, or a fresh default if none. The returned
    /// instance is the live cached object — mutate it then call <see cref="SetMeta"/> to persist.</summary>
    public ModMeta GetMeta(Guid packId, IReadOnlyList<string> candidateKeys)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            foreach (var key in candidateKeys)
                if (doc.Mods.TryGetValue(key, out var existing))
                    return existing;
        }
        return new ModMeta();
    }

    /// <summary>Stores flags under the primary (first) candidate key and consolidates any copies
    /// stored under the other candidate keys. A default/empty meta is removed entirely so the
    /// file stays small and only carries meaningful flags. Persists immediately.</summary>
    /// <summary>The auto-managed category that every library-marked mod belongs to.</summary>
    public const string LibraryCategory = "Library";

    /// <summary>Keeps a mod's membership in the managed <see cref="LibraryCategory"/> in sync with its
    /// <see cref="ModMeta.IsLibrary"/> flag. Returns true if the category list changed.</summary>
    private static bool ApplyLibraryCategory(ModMeta meta)
    {
        var has = meta.Categories.Any(c => string.Equals(c, LibraryCategory, StringComparison.OrdinalIgnoreCase));
        if (meta.IsLibrary && !has) { meta.Categories.Add(LibraryCategory); return true; }
        if (!meta.IsLibrary && has)
        {
            meta.Categories.RemoveAll(c => string.Equals(c, LibraryCategory, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        return false;
    }

    public void SetMeta(Guid packId, IReadOnlyList<string> candidateKeys, ModMeta meta)
    {
        StoreMeta(packId, candidateKeys, meta);
        Save(packId);
    }

    /// <summary>The <see cref="SetMeta"/> body without the write, so bulk edits can do one save at
    /// the end instead of one per mod.</summary>
    private void StoreMeta(Guid packId, IReadOnlyList<string> candidateKeys, ModMeta meta)
    {
        if (candidateKeys.Count == 0) return;
        if (ApplyLibraryCategory(meta) && meta.IsLibrary) AddCategory(packId, LibraryCategory);
        var doc = Load(packId);
        lock (_lock)
        {
            var primary = candidateKeys[0];
            // remove any stale copies under the secondary keys to keep one canonical entry
            for (var i = 1; i < candidateKeys.Count; i++)
                doc.Mods.Remove(candidateKeys[i]);

            if (meta.IsDefault) doc.Mods.Remove(primary);
            else doc.Mods[primary] = meta;
        }
    }

    // ── categories & advanced settings ─────────────────────────────────────────

    public IReadOnlyList<CustomCategory> Categories(Guid packId)
    {
        var doc = Load(packId);
        lock (_lock) { EnsureLibraryCategory(doc); } // the managed "Library" category is always present by default
        return doc.Categories;
    }

    /// <summary>Guarantees the built-in, non-deletable "Library" category exists. Returns true if it
    /// had to add or upgrade it (callers persist when that happens).</summary>
    private static bool EnsureLibraryCategory(PackModMetadata doc)
    {
        var existing = doc.Categories.FirstOrDefault(c => string.Equals(c.Name, LibraryCategory, StringComparison.OrdinalIgnoreCase));
        if (existing is null) { doc.Categories.Add(new CustomCategory { Name = LibraryCategory, Builtin = true }); return true; }
        if (!existing.Builtin) { existing.Builtin = true; return true; } // upgrade one created before this rule
        return false;
    }

    public CustomCategory AddCategory(Guid packId, string name, string? color = null)
    {
        var doc = Load(packId);
        var existing = doc.Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var cat = new CustomCategory
        {
            Name = name, Color = color,
            Builtin = string.Equals(name, LibraryCategory, StringComparison.OrdinalIgnoreCase)
        };
        doc.Categories.Add(cat);
        Save(packId);
        return cat;
    }

    /// <summary>
    /// Moves a category to a new slot. Position in <see cref="PackModMetadata.Categories"/> *is* the
    /// display order — every surface renders them in stored order rather than alphabetically — so
    /// this is the whole of "reorder".
    /// </summary>
    /// <param name="newIndex">Index in the list as it stands right now (before the move). Shifting
    /// for the removal is handled here so callers can just say "put it where that one is".</param>
    public void MoveCategory(Guid packId, string name, int newIndex)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            var from = doc.Categories.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (from < 0) return;

            var target = Math.Clamp(newIndex, 0, doc.Categories.Count);
            if (from < target) target--;              // pulling it out shifts everything after it left
            if (target == from) return;

            var cat = doc.Categories[from];
            doc.Categories.RemoveAt(from);
            doc.Categories.Insert(Math.Clamp(target, 0, doc.Categories.Count), cat);
        }
        Save(packId);
    }

    /// <summary>The saved <c>#RRGGBB</c> for a category, or null if it has none.</summary>
    public string? CategoryColor(Guid packId, string name)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            return doc.Categories
                .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Color;
        }
    }

    /// <summary>Sets (or clears, with null) a category's colour. Applies to the built-in Library
    /// category too — colour is presentation only, so there's nothing to protect there.</summary>
    public void SetCategoryColor(Guid packId, string name, string? color)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            var cat = doc.Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cat is null) return;
            cat.Color = color;
        }
        Save(packId);
    }

    /// <summary>Adds every mod in <paramref name="mods"/> to a category, or removes them all from it.
    /// One pass and one write, rather than a save per mod.</summary>
    public int SetMembership(Guid packId, IEnumerable<PackMod> mods, string category, bool member)
    {
        // The Library category and the IsLibrary flag are two views of the same thing. Adding a mod
        // to "Library" therefore drives the flag, and StoreMeta's ApplyLibraryCategory keeps the
        // category list in step — so managing Library by membership stays consistent with the flag
        // set from a mod's options menu.
        var isLibrary = string.Equals(category, LibraryCategory, StringComparison.OrdinalIgnoreCase);
        var changed = 0;
        foreach (var mod in mods)
        {
            var meta = GetMeta(packId, mod.CandidateKeys);
            var has = isLibrary
                ? meta.IsLibrary
                : meta.Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
            if (member == has) continue;

            if (isLibrary) meta.IsLibrary = member;
            else if (member) meta.Categories.Add(category);
            else meta.Categories.RemoveAll(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));

            StoreMeta(packId, mod.CandidateKeys, meta);
            mod.Meta = meta;   // re-point the view model at the stored flags and refresh its bindings
            changed++;
        }
        if (changed > 0) Save(packId);
        return changed;
    }

    // ── importing categories from another instance ───────────────────────────

    /// <summary>What an import would do, so it can be shown before it happens.</summary>
    /// <param name="NewCategories">Categories the source has and this pack does not.</param>
    /// <param name="SharedCategories">Categories both already have.</param>
    /// <param name="Assignments">Mod in this pack -> the categories the source puts it in.</param>
    /// <param name="UnmatchedSourceMods">Mods the source categorises that are not installed here.</param>
    /// <param name="FlagAssignments">Mods whose per-mod settings — priority, content size, side,
    /// library, extra, channel, lock and note — can be taken from the source pack. Only mods whose
    /// own metadata here is still untouched (<see cref="ModMeta.IsDefault"/>) qualify, so importing
    /// can never overwrite something the user set in this pack.</param>
    public sealed record CategoryImportPlan(
        IReadOnlyList<CustomCategory> NewCategories,
        IReadOnlyList<string> SharedCategories,
        IReadOnlyList<(PackMod Mod, IReadOnlyList<string> Categories)> Assignments,
        int UnmatchedSourceMods,
        IReadOnlyList<(PackMod Mod, ModMeta From)> FlagAssignments)
    {
        public int TaggedMods => Assignments.Count;
        public int FlaggedMods => FlagAssignments.Count;
        public bool IsEmpty => NewCategories.Count == 0 && Assignments.Count == 0 && FlagAssignments.Count == 0;
    }

    /// <summary>
    /// Works out what copying <paramref name="sourcePackId"/>'s categories onto
    /// <paramref name="targetMods"/> would produce, without changing anything.
    /// </summary>
    /// <remarks>
    /// Mods match across instances by their stored key, which is the store's project id
    /// (<c>modrinth:AANobbMI</c>) for anything identified and the file name otherwise — the same
    /// key the flags are saved under, so a mod tagged "Performance" in one pack is recognised as
    /// the same mod in another even at a different version. Mods the source does not know about
    /// are left exactly as they are: this adds, it never clears.
    /// </remarks>
    public CategoryImportPlan PlanCategoryImport(Guid sourcePackId, Guid targetPackId, IReadOnlyList<PackMod> targetMods)
    {
        var source = Load(sourcePackId);
        var target = Load(targetPackId);

        List<CustomCategory> sourceCats;
        Dictionary<string, ModMeta> sourceMods;
        HashSet<string> targetCatNames;
        lock (_lock)
        {
            sourceCats = source.Categories.ToList();
            sourceMods = new Dictionary<string, ModMeta>(source.Mods, StringComparer.OrdinalIgnoreCase);
            targetCatNames = new HashSet<string>(target.Categories.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        }

        // The built-in Library category is managed from the IsLibrary flag in each pack, so it is
        // never imported as a category of its own.
        var importable = sourceCats.Where(c => !c.Builtin).ToList();
        var newCats = importable.Where(c => !targetCatNames.Contains(c.Name)).ToList();
        var shared = importable.Where(c => targetCatNames.Contains(c.Name)).Select(c => c.Name).ToList();

        var assignments = new List<(PackMod, IReadOnlyList<string>)>();
        var flagAssignments = new List<(PackMod, ModMeta)>();
        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in targetMods)
        {
            var from = FirstMatch(sourceMods, mod.CandidateKeys, matchedKeys);
            if (from is null) continue;
            var wanted = from.Categories
                .Where(c => !string.Equals(c, LibraryCategory, StringComparison.OrdinalIgnoreCase))
                .Where(c => !mod.Meta.Categories.Contains(c, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (wanted.Count > 0) assignments.Add((mod, wanted));

            // Setting a pack up twice used to mean re-flagging every mod by hand: the import copied
            // the category names and dropped priority, size, side, library, notes and locks. It only
            // fills in mods this pack has said nothing about yet, so "adds, never clears" still holds.
            if (mod.Meta.IsDefault && HasImportableFlags(from)) flagAssignments.Add((mod, from));
        }

        var categorisedInSource = sourceMods.Count(kv =>
            kv.Value.Categories.Any(c => !string.Equals(c, LibraryCategory, StringComparison.OrdinalIgnoreCase)));
        var unmatched = Math.Max(0, categorisedInSource - matchedKeys.Count);

        return new CategoryImportPlan(newCats, shared, assignments, unmatched, flagAssignments);
    }

    /// <summary>Whether a source mod carries any per-mod setting worth copying. Categories are not
    /// counted: they travel through <see cref="CategoryImportPlan.Assignments"/>.</summary>
    private static bool HasImportableFlags(ModMeta from) =>
        from.Priority != 0 || from.ContentSize != 0 || from.Side != ModSide.Both
        || from.IsLibrary || from.IsExtra || from.UpdateLocked || from.UpdateChannel is not null
        || !string.IsNullOrWhiteSpace(from.Note);

    /// <summary>
    /// Copies the per-mod settings the import carries onto a target mod's (still untouched) metadata.
    /// </summary>
    /// <remarks>
    /// Deliberately not everything on <see cref="ModMeta"/>. <c>IsTesting</c> is left behind because
    /// a test set is about what you are doing right now in one instance, not a property of the mod;
    /// <c>PinnedVersionId</c> and <c>PreferredSource</c> are left behind because they name a
    /// version and a store this pack may not be on; and the incompatibility lists are left behind
    /// because they hold keys of the <em>source</em> pack's mods.
    /// </remarks>
    private static void CopyImportableFlags(ModMeta from, ModMeta to)
    {
        to.Priority = from.Priority;
        to.ContentSize = from.ContentSize;
        to.Side = from.Side;
        to.IsLibrary = from.IsLibrary;
        to.IsExtra = from.IsExtra;
        to.UpdateChannel = from.UpdateChannel;
        to.UpdateLocked = from.UpdateLocked;
        to.UpdateIncompatible = from.UpdateIncompatible;
        if (!string.IsNullOrWhiteSpace(from.Note)) to.Note = from.Note;
    }

    private static ModMeta? FirstMatch(Dictionary<string, ModMeta> sourceMods, IReadOnlyList<string> keys, HashSet<string> matched)
    {
        foreach (var key in keys)
        {
            if (!sourceMods.TryGetValue(key, out var meta)) continue;
            if (meta.Categories.Any(c => !string.Equals(c, LibraryCategory, StringComparison.OrdinalIgnoreCase)))
                matched.Add(key);
            return meta;
        }
        return null;
    }

    /// <summary>Applies a plan from <see cref="PlanCategoryImport"/>: adds the missing categories
    /// (keeping the source's colours and order) and tags the matching mods. One write at the end.</summary>
    public void ApplyCategoryImport(Guid targetPackId, CategoryImportPlan plan)
    {
        var doc = Load(targetPackId);
        lock (_lock)
        {
            foreach (var cat in plan.NewCategories)
                doc.Categories.Add(new CustomCategory { Name = cat.Name, Color = cat.Color });
        }

        // Flags first, so a mod that is in both lists ends up with the source's settings AND its
        // categories in one stored write rather than two.
        foreach (var (mod, from) in plan.FlagAssignments)
        {
            var meta = GetMeta(targetPackId, mod.CandidateKeys);
            CopyImportableFlags(from, meta);
            StoreMeta(targetPackId, mod.CandidateKeys, meta);
            mod.Meta = meta;
        }

        foreach (var (mod, categories) in plan.Assignments)
        {
            var meta = GetMeta(targetPackId, mod.CandidateKeys);
            foreach (var category in categories)
                if (!meta.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
                    meta.Categories.Add(category);
            StoreMeta(targetPackId, mod.CandidateKeys, meta);
            mod.Meta = meta;
        }
        Save(targetPackId);
    }

    public void RemoveCategory(Guid packId, string name)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            var cat = doc.Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cat is { Builtin: true }) return; // built-in categories (Library) can't be deleted
            doc.Categories.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            foreach (var meta in doc.Mods.Values)
                meta.Categories.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            RemoveCategoryLayout(doc, name);
            RemoveCollapsed(doc, name);
        }
        Save(packId);
    }

    /// <summary>Renames a category everywhere: the pack-wide list, every mod's membership, and its
    /// saved graph cluster position. Renaming onto an existing name merges into it.</summary>
    public void RenameCategory(Guid packId, string oldName, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;
        var doc = Load(packId);
        lock (_lock)
        {
            var cat = doc.Categories.FirstOrDefault(c => string.Equals(c.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (cat is { Builtin: true }) return; // built-in categories (Library) can't be renamed
            var target = doc.Categories.FirstOrDefault(c => string.Equals(c.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (cat is not null && target is null) cat.Name = newName;   // straight rename
            else if (cat is not null) doc.Categories.Remove(cat);        // merge into existing target

            // Folded-shut state follows the name, so renaming a collapsed category does not
            // silently pop it open.
            if (doc.CollapsedCategories.RemoveAll(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase)) > 0)
                Add(doc.CollapsedCategories, newName);

            foreach (var meta in doc.Mods.Values)
            {
                // Only mods that were actually IN the old category move to the new one. (This used to
                // add newName to every mod carrying any flag at all, which silently dumped unrelated
                // mods into a category every time one was renamed.)
                var wasMember = meta.Categories.RemoveAll(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase)) > 0;
                if (!wasMember) continue;
                if (!meta.Categories.Any(c => string.Equals(c, newName, StringComparison.OrdinalIgnoreCase)))
                    meta.Categories.Add(newName);
            }

            // carry the cluster's saved position to the new name (unless the target already has one)
            if (doc.CategoryLayout.TryGetValue(oldName, out var pos))
            {
                doc.CategoryLayout.Remove(oldName);
                doc.CategoryLayout.TryAdd(newName, pos);
            }
        }
        Save(packId);
    }

    /// <summary>Brings every library-marked mod into the managed <see cref="LibraryCategory"/> (and drops
    /// it from non-library mods), retroactively fixing mods flagged before this rule existed.</summary>
    public void SyncManagedCategories(Guid packId)
    {
        var doc = Load(packId);
        bool changed;
        lock (_lock)
        {
            changed = EnsureLibraryCategory(doc); // always present by default, even with no library mods yet
            foreach (var meta in doc.Mods.Values)
                changed |= ApplyLibraryCategory(meta);
        }
        if (changed) Save(packId);
    }

    public ModAdvancedSettings Advanced(Guid packId) => Load(packId).Advanced;

    public void SaveAdvanced(Guid packId) => Save(packId);

    /// <summary>The release channel this pack's downloads and update checks follow: its own setting,
    /// else the launcher-wide default. A mod's own <see cref="ModMeta.UpdateChannel"/> beats both —
    /// <see cref="PackMod.EffectiveUpdateChannel"/> applies that last step.</summary>
    public string EffectiveUpdateChannel(Guid packId) =>
        ModUpdateChannel.Normalize(Advanced(packId).UpdateChannel) ?? LauncherUpdateChannel;

    /// <summary>The launcher-wide channel every pack without one of its own follows.</summary>
    public string LauncherUpdateChannel =>
        ModUpdateChannel.Normalize(_settings.ModVersionChannel) ?? ModUpdateChannel.Alpha;

    /// <summary>Brings an older mods.json up to <see cref="PackModMetadata.CurrentVersion"/>.
    /// Version 1 had no way to say "follow the launcher default", so its pack channel was written as
    /// "release" whether or not anyone chose it; that is read as unset. A pack deliberately moved to
    /// beta or alpha said something the default never could, so it is kept.</summary>
    private static void Migrate(PackModMetadata doc)
    {
        if (doc.Version >= PackModMetadata.CurrentVersion) return;
        if (ModUpdateChannel.Normalize(doc.Advanced.UpdateChannel) == ModUpdateChannel.Release)
            doc.Advanced.UpdateChannel = null;
        doc.Version = PackModMetadata.CurrentVersion;
    }

    /// <summary>Applies one edit to several mods' flags and writes the file once. Used by the modpack
    /// importers to stamp every downloaded jar with the store it came from.</summary>
    public void EditMany(Guid packId, IEnumerable<IReadOnlyList<string>> candidateKeySets, Action<ModMeta> apply)
    {
        var any = false;
        foreach (var keys in candidateKeySets)
        {
            if (keys.Count == 0) continue;
            var meta = GetMeta(packId, keys);
            apply(meta);
            StoreMeta(packId, keys, meta);
            any = true;
        }
        if (any) Save(packId);
    }

    // ── custom graph layout ────────────────────────────────────────────────────

    public bool TryGetNodePosition(Guid packId, string key, out double x, out double y)
    {
        x = y = 0;
        var doc = Load(packId);
        lock (_lock)
        {
            if (doc.CustomLayout.TryGetValue(key, out var p) && p.Length >= 2) { x = p[0]; y = p[1]; return true; }
        }
        return false;
    }

    public void SetNodePosition(Guid packId, string key, double x, double y)
    {
        var doc = Load(packId);
        lock (_lock) { doc.CustomLayout[key] = new[] { x, y }; }
        Save(packId);
    }

    public bool TryGetCategoryPosition(Guid packId, string key, out double x, out double y)
    {
        x = y = 0;
        var doc = Load(packId);
        lock (_lock)
        {
            if (doc.CategoryLayout.TryGetValue(key, out var p) && p.Length >= 2) { x = p[0]; y = p[1]; return true; }
        }
        return false;
    }

    public void SetCategoryPosition(Guid packId, string key, double x, double y)
    {
        var doc = Load(packId);
        lock (_lock) { doc.CategoryLayout[key] = new[] { x, y }; }
        Save(packId);
    }

    // ── collapsed categories (Graph view, category mode) ─────────────────────

    /// <summary>True when this category is folded shut in the graph.</summary>
    public bool IsCategoryCollapsed(Guid packId, string name)
    {
        var doc = Load(packId);
        lock (_lock)
            return doc.CollapsedCategories.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
    }

    public void SetCategoryCollapsed(Guid packId, string name, bool collapsed)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            var changed = collapsed
                ? Add(doc.CollapsedCategories, name)
                : doc.CollapsedCategories.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!changed) return;
        }
        Save(packId);
    }

    /// <summary>Folds or unfolds several at once — one write for a "collapse all".</summary>
    public void SetCategoriesCollapsed(Guid packId, IEnumerable<string> names, bool collapsed)
    {
        var doc = Load(packId);
        var changed = false;
        lock (_lock)
        {
            foreach (var name in names)
            {
                changed |= collapsed
                    ? Add(doc.CollapsedCategories, name)
                    : doc.CollapsedCategories.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) > 0;
            }
        }
        if (changed) Save(packId);
    }

    private static bool Add(List<string> list, string name)
    {
        if (list.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) return false;
        list.Add(name);
        return true;
    }

    private static void RemoveCollapsed(PackModMetadata doc, string name) =>
        doc.CollapsedCategories.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

    private static void RemoveCategoryLayout(PackModMetadata doc, string name)
    {
        var hit = doc.CategoryLayout.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) doc.CategoryLayout.Remove(hit);
    }
}
