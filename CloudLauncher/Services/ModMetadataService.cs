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
    private readonly Dictionary<Guid, PackModMetadata> _cache = new();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ModMetadataService(PackFolderService packs) { _packs = packs; }

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
                File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts));
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
        Save(packId);
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

            foreach (var meta in doc.Mods.Values)
            {
                var hadNew = meta.Categories.Any(c => string.Equals(c, newName, StringComparison.OrdinalIgnoreCase));
                meta.Categories.RemoveAll(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase));
                if (!hadNew && !meta.Categories.Contains(newName)) meta.Categories.Add(newName);
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

    private static void RemoveCategoryLayout(PackModMetadata doc, string name)
    {
        var hit = doc.CategoryLayout.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) doc.CategoryLayout.Remove(hit);
    }
}
