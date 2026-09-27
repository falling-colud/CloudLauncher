using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>A submenu's worth of group cards to offer: one header with the values present in
/// this pack.</summary>
public sealed record PlanGroupOption(string Header, IReadOnlyList<PlanGroupQuery> Options);

/// <summary>
/// Loads and saves the pack's planning boards from <c>game/.cloudlauncher/plans.json</c>. The default
/// rules route that folder to Shared, so boards travel to collaborators like mod flags do.
///
/// Also resolves mod cards back to their <see cref="PackMod"/> (migrating the stored key when a
/// jar's identity firms up) and evaluates group card queries against the current inventory.
/// </summary>
public sealed class ModPlanService
{
    private readonly PackFolderService _packs;
    private readonly Dictionary<Guid, PackPlans> _cache = new();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ModPlanService(PackFolderService packs) { _packs = packs; }

    // ── load / save ──────────────────────────────────────────────────────────

    private string? TryPlanPath(Guid packId)
    {
        try { return Path.Combine(_packs.GameDir(packId), ".cloudlauncher", "plans.json"); }
        catch { return null; } // pack folder not materialised yet
    }

    public PackPlans Load(Guid packId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(packId, out var cached)) return cached;

            var doc = new PackPlans();
            var path = TryPlanPath(packId);
            if (path is not null && File.Exists(path))
            {
                try { doc = JsonSerializer.Deserialize<PackPlans>(File.ReadAllText(path), JsonOpts) ?? new(); }
                catch { doc = new PackPlans(); }
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
            var path = TryPlanPath(packId);
            if (path is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Temp file plus atomic rename, as for mods.json: a torn write would leave an unparseable
                // file, which Load() resets to empty, losing every board.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
                File.Move(tmp, path, overwrite: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Drops the in-memory copy so the next <see cref="Load"/> re-reads from disk
    /// (e.g. after a sync pulled a collaborator's boards).</summary>
    public void Invalidate(Guid packId)
    {
        lock (_lock) { _cache.Remove(packId); }
    }

    // ── boards ───────────────────────────────────────────────────────────────

    public IReadOnlyList<PlanBoard> Boards(Guid packId) => Load(packId).Boards;

    /// <summary>The board to show on open: the one last used, else the first, else a fresh one.</summary>
    public PlanBoard CurrentBoard(Guid packId)
    {
        var doc = Load(packId);
        var board = doc.Boards.FirstOrDefault(b => b.Id == doc.LastBoardId) ?? doc.Boards.FirstOrDefault();
        if (board is not null) return board;

        board = new PlanBoard { Name = "Plan" };
        doc.Boards.Add(board);
        doc.LastBoardId = board.Id;
        Save(packId);
        return board;
    }

    public void SetCurrentBoard(Guid packId, PlanBoard board)
    {
        Load(packId).LastBoardId = board.Id;
        Save(packId);
    }

    public PlanBoard AddBoard(Guid packId, string name)
    {
        var doc = Load(packId);
        var board = new PlanBoard { Name = UniqueName(doc, name) };
        doc.Boards.Add(board);
        doc.LastBoardId = board.Id;
        Save(packId);
        return board;
    }

    /// <summary>Copies a board (cards, links, viewport and category) under a new name. Cards get new
    /// ids and links are remapped, so the copy shares nothing with the original.</summary>
    public PlanBoard DuplicateBoard(Guid packId, PlanBoard source)
    {
        var doc = Load(packId);
        var copy = new PlanBoard
        {
            Name = UniqueName(doc, source.Name + " copy"),
            Category = source.Category,
            PanX = source.PanX, PanY = source.PanY, Zoom = source.Zoom
        };

        // Clone() re-ids every node, so remap the edges onto the new ids instead of dangling. Cards
        // keep their exact positions, so a section still contains the same (cloned) cards.
        var idMap = new Dictionary<string, string>();
        foreach (var n in source.Nodes)
        {
            var c = n.Clone();
            idMap[n.Id] = c.Id;
            copy.Nodes.Add(c);
        }
        // Re-point each cloned card's section membership at the cloned section (Clone copied the old id).
        foreach (var c in copy.Nodes)
            if (c.SectionId is not null && idMap.TryGetValue(c.SectionId, out var newSection)) c.SectionId = newSection;
        foreach (var e in source.Edges)
        {
            if (!idMap.TryGetValue(e.FromId, out var from) || !idMap.TryGetValue(e.ToId, out var to)) continue;
            copy.Edges.Add(e.CopyBetween(from, to));
        }
        copy.SectionMembershipSet = true; // cloned from a migrated board; membership is already explicit

        // Drop the copy right after its source, then re-group so it sits inside the same category.
        var at = doc.Boards.IndexOf(source);
        doc.Boards.Insert(at < 0 ? doc.Boards.Count : at + 1, copy);
        NormalizeBoards(doc);
        doc.LastBoardId = copy.Id;
        Save(packId);
        return copy;
    }

    public void RemoveBoard(Guid packId, PlanBoard board)
    {
        var doc = Load(packId);
        doc.Boards.Remove(board);
        if (doc.LastBoardId == board.Id) doc.LastBoardId = doc.Boards.FirstOrDefault()?.Id;
        Save(packId);
    }

    public void RenameBoard(Guid packId, PlanBoard board, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var doc = Load(packId);
        board.Name = string.Equals(board.Name, name, StringComparison.Ordinal) ? name : UniqueName(doc, name, board);
        Save(packId);
    }

    private static string UniqueName(PackPlans doc, string name, PlanBoard? skip = null)
    {
        name = name.Trim();
        if (name.Length == 0) name = "Plan";
        var taken = doc.Boards.Where(b => !ReferenceEquals(b, skip))
            .Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    // ── board ordering + categories ────────────────────────────────────────────

    /// <summary>Reorders a board within the flat list. <paramref name="newIndex"/> is in the list as
    /// it stands now; the shift for the removal is handled here. Followed by a re-group so boards stay
    /// contiguous within their category.</summary>
    public void MoveBoard(Guid packId, PlanBoard board, int newIndex)
    {
        var doc = Load(packId);
        var from = doc.Boards.IndexOf(board);
        if (from < 0) return;
        var target = Math.Clamp(newIndex, 0, doc.Boards.Count);
        if (from < target) target--;
        target = Math.Clamp(target, 0, doc.Boards.Count - 1);
        if (target == from) return;

        doc.Boards.RemoveAt(from);
        doc.Boards.Insert(target, board);
        NormalizeBoards(doc);
        Save(packId);
    }

    /// <summary>Assigns a board to a category (null / empty = uncategorized), registering the category
    /// if it's new, then re-groups.</summary>
    public void SetBoardCategory(Guid packId, PlanBoard board, string? category)
    {
        var doc = Load(packId);
        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        if (category is not null && !doc.BoardCategories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
            doc.BoardCategories.Add(category);
        board.Category = category;
        NormalizeBoards(doc);
        Save(packId);
    }

    public IReadOnlyList<string> BoardCategories(Guid packId) => Load(packId).BoardCategories;

    public void AddBoardCategory(Guid packId, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var doc = Load(packId);
        if (!doc.BoardCategories.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
        {
            doc.BoardCategories.Add(name);
            Save(packId);
        }
    }

    public void RenameBoardCategory(Guid packId, string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return;
        var doc = Load(packId);
        var i = doc.BoardCategories.FindIndex(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        doc.BoardCategories[i] = newName;
        foreach (var b in doc.Boards)
            if (string.Equals(b.Category, oldName, StringComparison.OrdinalIgnoreCase)) b.Category = newName;
        Save(packId);
    }

    /// <summary>Deletes a category; its boards stay and fall back to uncategorized.</summary>
    public void RemoveBoardCategory(Guid packId, string name)
    {
        var doc = Load(packId);
        doc.BoardCategories.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        foreach (var b in doc.Boards)
            if (string.Equals(b.Category, name, StringComparison.OrdinalIgnoreCase)) b.Category = null;
        NormalizeBoards(doc);
        Save(packId);
    }

    public void MoveBoardCategory(Guid packId, string name, int newIndex)
    {
        var doc = Load(packId);
        var from = doc.BoardCategories.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return;
        var target = Math.Clamp(newIndex, 0, doc.BoardCategories.Count);
        if (from < target) target--;
        target = Math.Clamp(target, 0, doc.BoardCategories.Count - 1);
        if (target == from) return;

        var cat = doc.BoardCategories[from];
        doc.BoardCategories.RemoveAt(from);
        doc.BoardCategories.Insert(target, cat);
        NormalizeBoards(doc);
        Save(packId);
    }

    /// <summary>Stable-sorts the flat board list so boards are grouped by their category in
    /// <see cref="PackPlans.BoardCategories"/> order, with uncategorized boards last. Relative order
    /// within a category is preserved, so intra-category reordering survives.</summary>
    private static void NormalizeBoards(PackPlans doc)
    {
        int Rank(PlanBoard b)
        {
            if (string.IsNullOrEmpty(b.Category)) return int.MaxValue;
            var i = doc.BoardCategories.FindIndex(c => string.Equals(c, b.Category, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue - 1 : i;
        }
        doc.Boards = doc.Boards
            .Select((b, i) => (b, i))
            .OrderBy(x => Rank(x.b)).ThenBy(x => x.i)
            .Select(x => x.b)
            .ToList();
    }

    // ── mod cards ────────────────────────────────────────────────────────────

    /// <summary>Resolves a mod card to its installed mod. A jar identified only by file name on one
    /// run and by store id on the next would otherwise orphan the card, so any candidate key
    /// matches and the node is migrated onto the mod's current primary key.</summary>
    public PackMod? ResolveMod(Guid packId, PlanNode node, IReadOnlyList<PackMod> all)
    {
        if (node.Kind != PlanNodeKind.Mod || node.ModKey is null) return null;

        var mod = all.FirstOrDefault(m => m.CandidateKeys.Contains(node.ModKey, StringComparer.OrdinalIgnoreCase));
        if (mod is null) return null;

        if (!string.Equals(node.ModKey, mod.Key, StringComparison.OrdinalIgnoreCase))
        {
            node.ModKey = mod.Key;
            Save(packId);
        }
        return mod;
    }

    /// <summary>Repoints every category group card at a renamed category, so those cards follow the
    /// rename instead of going empty. Call alongside
    /// <see cref="ModMetadataService.RenameCategory"/>.</summary>
    public void RenameCategoryReferences(Guid packId, string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;

        var doc = Load(packId);
        var changed = false;
        lock (_lock)
        {
            foreach (var query in doc.Boards.SelectMany(b => b.Nodes)
                         .Where(n => n.Kind == PlanNodeKind.Group)
                         .Select(n => n.Query)
                         .OfType<PlanGroupQuery>())
            {
                if (query.Property != PlanGroupProperty.Category) continue;
                if (!string.Equals(query.Value, oldName, StringComparison.OrdinalIgnoreCase)) continue;
                query.Value = newName;
                changed = true;
            }
        }
        if (changed) Save(packId);
    }

    // ── placeholder cards ────────────────────────────────────────────────────

    /// <summary>A name cut down to its letters and digits in lower case, so "Farmer's Delight",
    /// "farmers-delight" and "FarmersDelight" compare equal.</summary>
    public static string MatchName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var chars = new char[name.Length];
        var n = 0;
        foreach (var c in name)
            if (char.IsLetterOrDigit(c)) chars[n++] = char.ToLowerInvariant(c);
        return new string(chars, 0, n);
    }

    /// <summary>The store project a link points at, when it is a Modrinth or CurseForge project page:
    /// the store and the slug (or id) from its path. Null for any other link.</summary>
    public static (ModSource Source, string Slug)? StoreProjectOf(string? link)
    {
        if (!SafeLaunch.IsWebUrl(link, out var uri) || uri is null) return null;
        var host = uri.Host.ToLowerInvariant();
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // modrinth.com/mod/sodium (also /plugin, /datapack... and the generic /project)
        if ((host == "modrinth.com" || host.EndsWith(".modrinth.com")) && parts.Length >= 2)
            return (ModSource.Modrinth, Uri.UnescapeDataString(parts[1]));
        // www.curseforge.com/minecraft/mc-mods/jei
        if ((host == "curseforge.com" || host.EndsWith(".curseforge.com")) && parts.Length >= 3
            && string.Equals(parts[0], "minecraft", StringComparison.OrdinalIgnoreCase))
            return (ModSource.CurseForge, Uri.UnescapeDataString(parts[2]));
        return null;
    }

    // ── group cards ──────────────────────────────────────────────────────────

    /// <summary>Evaluates a group card's query against the current inventory.</summary>
    public static IReadOnlyList<PackMod> Resolve(PlanGroupQuery? query, IReadOnlyList<PackMod> all)
    {
        if (query is null) return Array.Empty<PackMod>();

        var v = query.Value;
        IEnumerable<PackMod> hits = query.Property switch
        {
            PlanGroupProperty.All => all,

            PlanGroupProperty.Category =>
                all.Where(m => m.Meta.Categories.Any(c => string.Equals(c, v, StringComparison.OrdinalIgnoreCase))),

            PlanGroupProperty.Priority =>
                all.Where(m => m.Priority == query.ParsedPriority),

            PlanGroupProperty.ContentSize =>
                all.Where(m => m.ContentSize == query.ParsedContentSize),

            PlanGroupProperty.Side =>
                all.Where(m => string.Equals(m.Side.ToString(), v, StringComparison.OrdinalIgnoreCase)),

            PlanGroupProperty.Flag => v.ToLowerInvariant() switch
            {
                "library" => all.Where(m => m.IsLibrary),
                "testing" => all.Where(m => m.IsTesting),
                "extra"   => all.Where(m => m.IsExtra),
                _         => Array.Empty<PackMod>()
            },

            PlanGroupProperty.Status => v.ToLowerInvariant() switch
            {
                "enabled"             => all.Where(m => m.Enabled),
                "disabled"            => all.Where(m => !m.Enabled),
                "update"              => all.Where(m => m.HasUpdate),
                "update-incompatible" => all.Where(m => m.Meta.UpdateIncompatible),
                "update-locked"       => all.Where(m => m.Meta.UpdateLocked),
                "note"                => all.Where(m => m.HasNote),
                "conflict"            => all.Where(m => m.Meta.IncompatibleWith.Count > 0 || m.Meta.IncompatibleWithUnknown),
                _                     => Array.Empty<PackMod>()
            },

            PlanGroupProperty.Source =>
                all.Where(m => string.Equals(m.SourceLabel, v, StringComparison.OrdinalIgnoreCase)),

            PlanGroupProperty.Folder =>
                all.Where(m => string.Equals(m.Folder, v, StringComparison.OrdinalIgnoreCase)),

            _ => Array.Empty<PackMod>()
        };

        return hits.OrderByDescending(m => m.Priority)
                   .ThenByDescending(m => m.ContentSize)
                   .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>The group cards worth offering for this pack: only values that match something, plus
    /// every declared category so a new empty category can still be planned for.</summary>
    public IReadOnlyList<PlanGroupOption> GroupOptions(Guid packId, IReadOnlyList<PackMod> all,
        ModMetadataService metadata)
    {
        var options = new List<PlanGroupOption>();

        // Declared categories keep their stored order (the user's arrangement); names found only on a
        // mod (e.g. from a collaborator) come after.
        var categories = metadata.Categories(packId).Select(c => c.Name)
            .Concat(all.SelectMany(m => m.Meta.Categories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => new PlanGroupQuery { Property = PlanGroupProperty.Category, Value = c })
            .ToList();
        if (categories.Count > 0) options.Add(new PlanGroupOption("Category", categories));

        var priorities = all.Select(m => m.Priority).Distinct().OrderByDescending(p => p)
            .Select(p => new PlanGroupQuery { Property = PlanGroupProperty.Priority, Value = p.ToString() })
            .ToList();
        if (priorities.Count > 0) options.Add(new PlanGroupOption("Priority", priorities));

        var sizes = all.Select(m => m.ContentSize).Where(s => s > 0).Distinct().OrderByDescending(s => s)
            .Select(s => new PlanGroupQuery { Property = PlanGroupProperty.ContentSize, Value = s.ToString() })
            .ToList();
        if (sizes.Count > 0) options.Add(new PlanGroupOption("Content size", sizes));

        var flags = new List<PlanGroupQuery>();
        if (all.Any(m => m.IsLibrary)) flags.Add(new() { Property = PlanGroupProperty.Flag, Value = "library" });
        if (all.Any(m => m.IsTesting)) flags.Add(new() { Property = PlanGroupProperty.Flag, Value = "testing" });
        if (all.Any(m => m.IsExtra))   flags.Add(new() { Property = PlanGroupProperty.Flag, Value = "extra" });
        if (flags.Count > 0) options.Add(new PlanGroupOption("Flag", flags));

        var sides = all.Select(m => m.Side).Distinct().OrderBy(s => (int)s)
            .Select(s => new PlanGroupQuery { Property = PlanGroupProperty.Side, Value = s.ToString() })
            .ToList();
        if (sides.Count > 1) options.Add(new PlanGroupOption("Side", sides));

        var status = new List<PlanGroupQuery>();
        void Status(string value, bool present) { if (present) status.Add(new() { Property = PlanGroupProperty.Status, Value = value }); }
        Status("enabled", all.Any(m => m.Enabled));
        Status("disabled", all.Any(m => !m.Enabled));
        Status("update", all.Any(m => m.HasUpdate));
        Status("update-incompatible", all.Any(m => m.Meta.UpdateIncompatible));
        Status("update-locked", all.Any(m => m.Meta.UpdateLocked));
        Status("conflict", all.Any(m => m.Meta.IncompatibleWith.Count > 0 || m.Meta.IncompatibleWithUnknown));
        Status("note", all.Any(m => m.HasNote));
        if (status.Count > 0) options.Add(new PlanGroupOption("Status", status));

        var sources = all.Select(m => m.SourceLabel).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(s => new PlanGroupQuery { Property = PlanGroupProperty.Source, Value = s })
            .ToList();
        if (sources.Count > 1) options.Add(new PlanGroupOption("Source", sources));

        var folders = all.Select(m => m.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(s => new PlanGroupQuery { Property = PlanGroupProperty.Folder, Value = s })
            .ToList();
        if (folders.Count > 1) options.Add(new PlanGroupOption("Folder", folders));

        options.Add(new PlanGroupOption("Everything",
            new[] { new PlanGroupQuery { Property = PlanGroupProperty.All, Value = "" } }));

        return options;
    }
}

/// <summary>Finds the installed mod a placeholder card stands for, so the board can offer to swap the
/// real card in once the mod is in the pack.</summary>
/// <remarks>
/// <para>Built once per board redraw from the pack's mods (a few dictionary fills), after which each
/// placeholder is a handful of lookups rather than a scan of every mod.</para>
/// <para>Strongest evidence first: the attached store project (<see cref="PlanNode.ModKey"/>), then a
/// Modrinth or CurseForge link's slug, then the name, ignoring case, spaces and punctuation, against
/// the mod's name on either store and its slugs. Names shorter than three letters are not matched
/// by name alone, since "EMI" and "AE2"-style stubs are too easy to hit by accident.</para>
/// </remarks>
public sealed class PlanPlaceholderMatcher
{
    private readonly Dictionary<string, PackMod> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackMod> _bySlug = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackMod> _byName = new(StringComparer.Ordinal);

    public PlanPlaceholderMatcher(IReadOnlyList<PackMod> mods)
    {
        foreach (var mod in mods)
        {
            foreach (var key in mod.CandidateKeys) _byKey.TryAdd(key, mod);
            if (mod.Modrinth is { } mr) Add(mr);
            if (mod.CurseForge is { } cf) Add(cf);
            Name(mod.DisplayName, mod);

            void Add(ModSummary s)
            {
                if (!string.IsNullOrWhiteSpace(s.Slug)) _bySlug.TryAdd($"{s.Source}:{s.Slug}", mod);
                Name(s.Name, mod);
                Name(s.Slug, mod);
            }
        }

        void Name(string? name, PackMod mod)
        {
            var key = ModPlanService.MatchName(name);
            if (key.Length >= 3) _byName.TryAdd(key, mod);
        }
    }

    public PackMod? Find(PlanNode placeholder)
    {
        if (placeholder.ModKey is { Length: > 0 } key && _byKey.TryGetValue(key, out var byKey)) return byKey;
        if (ModPlanService.StoreProjectOf(placeholder.Placeholder?.Link) is { } project
            && _bySlug.TryGetValue($"{project.Source}:{project.Slug}", out var bySlug)) return bySlug;
        var name = ModPlanService.MatchName(placeholder.Title);
        return name.Length >= 3 && _byName.TryGetValue(name, out var byName) ? byName : null;
    }
}
