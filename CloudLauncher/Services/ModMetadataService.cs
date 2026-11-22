using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>Loads and saves per-pack mod flags from <c>game/.cloudlauncher/mods.json</c>, which the
/// default sync rules share with collaborators.</summary>
/// <remarks>Mods are keyed by source project id when known (<c>modrinth:...</c> or
/// <c>curseforge:...</c>), which survives version updates, with the file name as a fallback. Reads
/// try every candidate key; writes consolidate onto the primary one.</remarks>
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

    // ──────────────────── the global scope ────────────────────

    /// <summary>The scope id the global Mods page edits: one document of flags, categories and layout
    /// that belongs to no instance and is never synced.</summary>
    /// <remarks>A scope id lets every caller that takes a pack id work on the global page unchanged.
    /// Not <see cref="Guid.Empty"/>: <c>PackFolderService.PackRoot</c> throws for it, which makes
    /// <see cref="Save"/> silently do nothing. Stored beside settings.json
    /// (<see cref="AppSettings.DataRootPath"/>) because a pack's <c>mods.json</c> syncs to
    /// collaborators.</remarks>
    public static readonly Guid GlobalScope = new("c10dc10d-0000-4000-8000-000000000001");

    /// <summary>True when <paramref name="scopeId"/> is <see cref="GlobalScope"/> rather than an
    /// instance.</summary>
    public static bool IsGlobalScope(Guid scopeId) => scopeId == GlobalScope;

    /// <summary>File name of the global document, beside <c>settings.json</c>.</summary>
    private const string GlobalFileName = "global-mods.json";

    // ── key derivation ───────────────────────────────────────────────────────

    /// <summary>The primary (canonical) key for a mod: the source id if known, else the file
    /// name.</summary>
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
        // Checked before anything that could throw; GameDir would fail for the sentinel id (see
        // GlobalScope).
        if (IsGlobalScope(packId)) return Path.Combine(AppSettings.DataRootPath, GlobalFileName);
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
                    TidyTags(doc);
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
                // Temp file + atomic rename. A torn write would leave an unparseable mods.json, which
                // Load() resets to empty, wiping every flag (and syncing that to collaborators).
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

    /// <summary>Returns the stored flags for a mod, or a fresh default if none. The result is the live
    /// cached object: mutate it, then call <see cref="SetMeta"/> to persist.</summary>
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

    /// <summary>The flags in force for a mod in one instance: the instance's own entry, else the
    /// global default, else nothing.</summary>
    /// <remarks>Inherited values are a clone, since <see cref="GetMeta"/> hands out live objects and an
    /// edit would otherwise change the global entry for every instance. An own entry equal to the
    /// default can't be told from no entry (<see cref="StoreMeta"/> deletes those), so an inherited
    /// flag can't yet be overridden back to its default.</remarks>
    /// <param name="packId">The instance asking, or <see cref="GlobalScope"/>, which never
    /// inherits.</param>
    /// <param name="candidateKeys">The mod's keys, most stable first, from
    /// <see cref="CandidateKeys"/>.</param>
    /// <param name="inherited">True when the values came from the global document, so a list can
    /// badge the row.</param>
    public ModMeta EffectiveMeta(Guid packId, IReadOnlyList<string> candidateKeys, out bool inherited)
    {
        inherited = false;
        var own = GetMeta(packId, candidateKeys);
        if (!own.IsDefault || IsGlobalScope(packId)) return own;

        var global = GetMeta(GlobalScope, candidateKeys);
        if (global.IsDefault) return own;

        inherited = true;
        return global.Clone();
    }

    // ── early access ───────────────────────────────────────────────────────────

    /// <summary>The mod ids marked early access in this pack (<see cref="PackModMetadata.EarlyAccess"/>),
    /// compared without regard to case.</summary>
    public IReadOnlySet<string> EarlyAccessIds(Guid packId)
    {
        var doc = Load(packId);
        lock (_lock) return new HashSet<string>(doc.EarlyAccess, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Marks or unmarks mods as early access by the mod ids their jars declare, and saves.</summary>
    public void SetEarlyAccess(Guid packId, IEnumerable<string> modIds, bool earlyAccess)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            foreach (var raw in modIds)
            {
                var id = raw.Trim().ToLowerInvariant();
                if (id.Length == 0) continue;
                doc.EarlyAccess.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
                if (earlyAccess) doc.EarlyAccess.Add(id);
            }
            doc.EarlyAccess.Sort(StringComparer.Ordinal);
        }
        Save(packId);
    }

    // ── tags ───────────────────────────────────────────────────────────────────
    //
    // A pack can define tags of its own for its mods (PackModMetadata.TagDefs) and put them on mods
    // (PackModMetadata.Tags). Like the early-access mark they go on the mod ids the jars declare, so a
    // new version of a jar keeps its tags and a tool can match them to files without asking a store.
    // What the tags mean is the pack's business: the launcher shows them, and tools read them from
    // mods.json (PackModMetadata says what that file must look like).

    /// <summary>What the Tags menu calls the early-access mark, which it lists as one more tag. A
    /// pack's own tag can't have this name: the menu would show two.</summary>
    public const string EarlyAccessTagName = "Early access";

    /// <summary>The tags this pack defines, in the order they are shown. Empty for a pack without
    /// tags, which is what keeps the Tags menu out of it.</summary>
    public IReadOnlyList<ModTag> TagDefs(Guid packId)
    {
        var doc = Load(packId);
        lock (_lock)
            return doc.TagDefs is { Count: > 0 } defs ? defs.Select(Snapshot).ToList() : [];
    }

    /// <summary>A tag name as it is stored: trimmed, runs of spaces made one. Null when it can't be a
    /// tag's name (empty, or <see cref="EarlyAccessTagName"/>).</summary>
    public static string? CleanTagName(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length == 0 || string.Equals(clean, EarlyAccessTagName, StringComparison.OrdinalIgnoreCase)
            ? null
            : clean;
    }

    /// <summary>Defines a tag, or returns the one of that name the pack already has (names differ by
    /// more than case). Null when the name can't be used or this is not a pack.</summary>
    /// <param name="color"><c>#RRGGBB</c>; null or anything else takes the first palette colour no
    /// tag of this pack uses yet, so every tag has one.</param>
    public ModTag? AddTag(Guid packId, string name, string? color = null)
    {
        if (IsGlobalScope(packId) || CleanTagName(name) is not { } clean) return null;
        var doc = Load(packId);
        ModTag added;
        lock (_lock)
        {
            if (FindTag(doc, clean) is { } existing) return Snapshot(existing);
            EnsureTagKeys(doc);
            var def = new ModTagDef { Name = clean, Color = CleanColor(color) ?? NextTagColor(doc.TagDefs!) };
            doc.TagDefs!.Add(def);
            added = Snapshot(def);
        }
        Save(packId);
        return added;
    }

    /// <summary>Renames a tag, on every mod that carries it too. False when the pack has no such tag,
    /// the new name can't be used, or another tag already has it (tags are never merged: what a tag
    /// means is the pack's, and two meanings must not become one by a typo).</summary>
    public bool RenameTag(Guid packId, string oldName, string newName)
    {
        if (CleanTagName(newName) is not { } clean) return false;
        var doc = Load(packId);
        lock (_lock)
        {
            if (FindTag(doc, oldName) is not { } def) return false;
            if (FindTag(doc, clean) is { } other && !ReferenceEquals(other, def)) return false;
            if (string.Equals(def.Name, clean, StringComparison.Ordinal)) return true;

            var previous = def.Name;
            def.Name = clean;
            if (doc.Tags is { } tags)
                foreach (var names in tags.Values)
                    for (var i = 0; i < names.Count; i++)
                        if (string.Equals(names[i], previous, StringComparison.OrdinalIgnoreCase)) names[i] = clean;
        }
        Save(packId);
        return true;
    }

    /// <summary>Removes a tag from the pack and takes it off every mod. False when the pack has no
    /// such tag.</summary>
    public bool RemoveTag(Guid packId, string name)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            if (FindTag(doc, name) is not { } def) return false;
            doc.TagDefs!.Remove(def);
            if (doc.Tags is { } tags)
                foreach (var id in tags.Keys.ToList())
                {
                    tags[id].RemoveAll(t => string.Equals(t, def.Name, StringComparison.OrdinalIgnoreCase));
                    if (tags[id].Count == 0) tags.Remove(id);
                }
        }
        Save(packId);
        return true;
    }

    /// <summary>Sets a tag's colour. False when the pack has no such tag or the colour is not
    /// <c>#RRGGBB</c>.</summary>
    public bool SetTagColor(Guid packId, string name, string? color)
    {
        if (CleanColor(color) is not { } clean) return false;
        var doc = Load(packId);
        lock (_lock)
        {
            if (FindTag(doc, name) is not { } def) return false;
            if (string.Equals(def.Color, clean, StringComparison.Ordinal)) return true;
            def.Color = clean;
        }
        Save(packId);
        return true;
    }

    /// <summary>The tags a jar carries: every tag on any of the mod ids it declares, in the order the
    /// pack defines them.</summary>
    /// <param name="modIds">The ids the jar declares, in any casing (<see cref="PackMod.JarModIds"/>).</param>
    public IReadOnlyList<ModTag> TagsFor(Guid packId, IEnumerable<string> modIds)
    {
        var doc = Load(packId);
        lock (_lock)
        {
            if (doc.Tags is not { Count: > 0 } tags) return [];

            HashSet<string>? carried = null;
            foreach (var raw in modIds)
                if (tags.TryGetValue(CleanModId(raw), out var names))
                    (carried ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).UnionWith(names);
            if (carried is null) return [];

            var found = new List<ModTag>(carried.Count);
            if (doc.TagDefs is { } defs)
                foreach (var def in defs)
                    if (carried.Remove(def.Name)) found.Add(Snapshot(def));
            // A name with no definition can't get here through the launcher (TidyTags defines it at
            // load); if one does, it is shown rather than hidden.
            foreach (var name in carried.Order(StringComparer.OrdinalIgnoreCase)) found.Add(new ModTag(name, null));
            return found;
        }
    }

    /// <summary>True when the pack has tags to show on its mods: the mod list then reads every jar's
    /// mod ids, which it otherwise leaves alone.</summary>
    public bool HasTags(Guid packId)
    {
        var doc = Load(packId);
        lock (_lock) return doc.TagDefs is { Count: > 0 } || doc.Tags is { Count: > 0 };
    }

    /// <summary>Puts a tag on mods or takes it off, by the mod ids their jars declare, with one write
    /// for the whole selection. Only a tag the pack defines can be set.</summary>
    /// <returns>True when anything changed.</returns>
    public bool SetTag(Guid packId, IEnumerable<string> modIds, string tag, bool on)
    {
        var doc = Load(packId);
        // The ids come lazily from the jars: read them before the lock is taken, not under it.
        var ids = modIds.Select(CleanModId).Where(id => id.Length > 0).Distinct().ToList();
        var changed = false;
        lock (_lock)
        {
            if (FindTag(doc, tag) is not { } def) return false;
            EnsureTagKeys(doc);
            var tags = doc.Tags!;
            foreach (var id in ids)
            {
                tags.TryGetValue(id, out var names);
                var has = names is not null && names.Contains(def.Name, StringComparer.OrdinalIgnoreCase);
                if (on == has) continue;

                if (on)
                {
                    names ??= tags[id] = new List<string>();
                    names.Add(def.Name);
                    SortByDefinition(doc.TagDefs!, names);
                }
                else
                {
                    names!.RemoveAll(n => string.Equals(n, def.Name, StringComparison.OrdinalIgnoreCase));
                    if (names.Count == 0) tags.Remove(id);
                }
                changed = true;
            }
        }
        if (changed) Save(packId);
        return changed;
    }

    private static ModTag Snapshot(ModTagDef def) => new(def.Name, def.Color);

    private static ModTagDef? FindTag(PackModMetadata doc, string? name) =>
        doc.TagDefs?.FirstOrDefault(d => string.Equals(d.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string CleanModId(string? id) => (id ?? "").Trim().ToLowerInvariant();

    /// <summary><c>#RRGGBB</c> in upper case, or null for anything else.</summary>
    private static string? CleanColor(string? color)
    {
        var hex = color?.Trim().TrimStart('#');
        return hex is { Length: 6 } && hex.All(Uri.IsHexDigit) ? "#" + hex.ToUpperInvariant() : null;
    }

    /// <summary>The first shared accent no tag uses yet, going round once they are all taken: the
    /// rule new categories get their colour by.</summary>
    private static string NextTagColor(List<ModTagDef> defs)
    {
        var palette = Views.AccentPalette.Colors;
        foreach (var (_, hex) in palette)
            if (!defs.Any(d => string.Equals(d.Color, hex, StringComparison.OrdinalIgnoreCase))) return hex;
        return palette[defs.Count % palette.Length].Hex;
    }

    /// <summary>Puts a mod's tag names in the order the pack defines its tags.</summary>
    private static void SortByDefinition(List<ModTagDef> defs, List<string> names)
    {
        if (names.Count < 2) return;
        int Rank(string name)
        {
            var i = defs.FindIndex(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
        var ordered = names.OrderBy(Rank).ToList();   // a stable sort: unknown names keep their order
        names.Clear();
        names.AddRange(ordered);
    }

    /// <summary>Makes both tag keys present, which is what gets them written from now on.</summary>
    /// <remarks>Called when a pack starts using tags, never just for reading: a pack without tags
    /// keeps a mods.json without these keys.</remarks>
    private static void EnsureTagKeys(PackModMetadata doc)
    {
        doc.TagDefs ??= new List<ModTagDef>();
        doc.Tags ??= new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
    }

    /// <summary>Tidies the tag keys of a document that was just read, so the rest of the service can
    /// rely on them: every tag has a name and names differ by more than case; every tag a mod carries
    /// is defined (a file written by hand may tag mods without saying what the tags are: each such
    /// name gets a definition and a colour, so it shows up and can be managed) and spelled as its
    /// definition spells it; and a pack with tags has both keys.</summary>
    private static void TidyTags(PackModMetadata doc)
    {
        if (doc.TagDefs is { } defs)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in defs) def.Name = (def.Name ?? "").Trim();
            defs.RemoveAll(d => d.Name.Length == 0 || !seen.Add(d.Name));   // the first of a name stays
        }

        if (doc.Tags is { Count: > 0 } tags)
        {
            var known = doc.TagDefs ??= new List<ModTagDef>();
            foreach (var names in tags.Values)
            {
                for (var i = 0; i < names.Count; i++)
                {
                    var def = known.FirstOrDefault(d => string.Equals(d.Name, names[i], StringComparison.OrdinalIgnoreCase));
                    if (def is null) known.Add(def = new ModTagDef { Name = names[i], Color = NextTagColor(known) });
                    names[i] = def.Name;
                }
                SortByDefinition(known, names);
            }
        }

        if (doc.TagDefs is { Count: > 0 }) EnsureTagKeys(doc);
    }

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

    /// <summary>Stores flags under the primary (first) candidate key, removing copies under the other
    /// keys, and drops default flags entirely so the file only carries meaningful ones. This is
    /// <see cref="SetMeta"/> without the write, so bulk edits can save once at the end.</summary>
    internal void StoreMeta(Guid packId, IReadOnlyList<string> candidateKeys, ModMeta meta)
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

    /// <summary>Moves a category to a new slot. List order in <see cref="PackModMetadata.Categories"/>
    /// is the display order everywhere, so this is all reordering needs.</summary>
    /// <param name="newIndex">Index in the list before the move. The shift from removing the item is
    /// handled here, so callers can just say "put it where that one is".</param>
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

    /// <summary>Sets (or clears, with null) a category's colour. Also allowed on the built-in Library
    /// category, since colour is presentation only.</summary>
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
        // The Library category mirrors the IsLibrary flag: membership sets the flag, and StoreMeta's
        // ApplyLibraryCategory keeps the category list in step.
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
    /// <param name="FlagAssignments">Mods whose per-mod settings (priority, content size, side,
    /// library, extra, channel, lock and note) can be taken from the source pack. Only mods with
    /// untouched metadata here (<see cref="ModMeta.IsDefault"/>) qualify, so an import never overwrites
    /// the user's own settings.</param>
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

    /// <summary>Works out what copying <paramref name="sourcePackId"/>'s categories onto
    /// <paramref name="targetMods"/> would produce, without changing anything.</summary>
    /// <remarks>Mods match by stored key (project id such as <c>modrinth:AANobbMI</c>, else file name),
    /// so the same mod matches across instances even at different versions. It only adds; mods the
    /// source doesn't know are left alone.</remarks>
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

            // Only mods this pack has no settings for yet take the source's flags, so the import still
            // only adds.
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

    /// <summary>Copies the per-mod settings the import carries onto a target mod's (still untouched)
    /// metadata.</summary>
    /// <remarks>Not everything on <see cref="ModMeta"/>: <c>IsTesting</c> is about one instance's
    /// current work, <c>PinnedVersionId</c> and <c>PreferredSource</c> name a version and store this
    /// pack may not use, and the incompatibility lists hold keys of the source pack's mods.</remarks>
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

        // Flags first, so a mod in both lists gets the source's settings and categories in one
        // stored write.
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

            // Collapsed state follows the name, so a renamed category stays collapsed.
            if (doc.CollapsedCategories.RemoveAll(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase)) > 0)
                Add(doc.CollapsedCategories, newName);

            foreach (var meta in doc.Mods.Values)
            {
                // Only mods that were in the old category move to the new one.
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

    /// <summary>Puts every library-marked mod into the managed <see cref="LibraryCategory"/> (and
    /// takes it off non-library mods), which also fixes mods flagged by older versions.</summary>
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
    /// else the launcher-wide default. A mod's own <see cref="ModMeta.UpdateChannel"/> beats both; that
    /// step is in <see cref="PackMod.EffectiveUpdateChannel"/>.</summary>
    public string EffectiveUpdateChannel(Guid packId) =>
        ModUpdateChannel.Normalize(Advanced(packId).UpdateChannel) ?? LauncherUpdateChannel;

    /// <summary>The launcher-wide channel every pack without one of its own follows.</summary>
    public string LauncherUpdateChannel =>
        ModUpdateChannel.Normalize(_settings.ModVersionChannel) ?? ModUpdateChannel.Alpha;

    /// <summary>Brings an older mods.json up to <see cref="PackModMetadata.CurrentVersion"/>.
    /// Version 1 always wrote the pack channel as "release", even when nobody chose it, so that is
    /// read as unset. Beta or alpha was an explicit choice and is kept.</summary>
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

    /// <summary>Folds or unfolds several at once, with one write for "collapse all".</summary>
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
