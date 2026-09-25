using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Reads and writes the per-folder rules on the Worlds, Resource packs, Shader packs and Mods
/// pages, and works out which rule (or rules) one item inherits.
/// </summary>
/// <remarks>
/// <para>World, resource-pack and shader folders live in <see cref="AppSettings"/>; mod folders
/// live in <see cref="ModFolderStore"/>, keyed by mod identity rather than file name.</para>
/// <para>Membership is many-to-many, so rules intersect: <see cref="Effective"/> returns the first
/// rule with the rest chained in <see cref="ContentFolderRule.AndAlso"/>, and
/// <see cref="ContentCompatibility.FolderAdmits"/> walks the chain. The pages' <c>FolderOf</c>
/// helper is for display only.</para>
/// <para>Only library items (<c>saves/Skyblock.zip</c>) inherit rules. An instance's own file
/// (<c>{packId:N}:Skyblock</c>) is already where it is, so it gets null. Mods are keyed by
/// identity, so this check does not apply to them.</para>
/// </remarks>
public static class ContentFolderRules
{
    /// <summary>The one mod-folder store the engine reads. Its document is process-wide (see
    /// <see cref="ModFolderStore"/>), so this sees what the Mods page just wrote.</summary>
    private static readonly ModFolderStore ModFolders = new();

    /// <summary>True for the four kinds that have folders the user can put a rule on.</summary>
    /// <remarks>Config and KubeJS bundles have no folder strip, so there is nothing to hang a rule off
    /// and <see cref="Effective"/> answers null for them.</remarks>
    public static bool Supports(LibraryKind kind) =>
        kind is LibraryKind.World or LibraryKind.ResourcePack or LibraryKind.ShaderPack or LibraryKind.Mod;

    /// <summary>
    /// True when this folder is one the user may put a rule on.
    /// </summary>
    /// <remarks>The folder strips also have computed chips ("All", "Defaults", "New folder") that
    /// are views over the library, not real folders, so they take no rule. Same test as
    /// <c>WorldFolderChipVm.IsUserFolder</c>.</remarks>
    public static bool CanEdit(LibraryKind kind, string? folder) =>
        Supports(kind) && !ModFolderStore.IsReservedName(folder);

    /// <summary>Every folder on that page, in the order its chips are drawn.</summary>
    public static IReadOnlyList<string> FolderNames(LibraryKind kind, AppSettings? settings = null)
    {
        if (kind == LibraryKind.Mod) return ModFolders.Names();
        var map = MembersMap(kind, Settings(settings));
        return map is null ? [] : map.Keys.ToList();
    }

    /// <summary>The item keys filed under one folder, in the store's own spelling.</summary>
    public static IReadOnlyList<string> MemberKeys(LibraryKind kind, string folder, AppSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return [];
        if (kind == LibraryKind.Mod) return ModFolders.Members(folder);
        var map = MembersMap(kind, Settings(settings));
        return map is not null && map.TryGetValue(folder, out var list) ? list.ToList() : [];
    }

    /// <summary>
    /// The key one library item is filed under in its page's folder map.
    /// </summary>
    /// <remarks>Not always <see cref="LibraryItem.Key"/>: mods are filed under their identity
    /// (<c>modrinth:AANobbMI</c>) so the folder survives updates, since the file name carries the
    /// version. Without a recorded identity it falls back to the library key, as
    /// <see cref="ModMetadataService.KeyFor"/> does.</remarks>
    public static string KeyFor(LibraryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind == LibraryKind.Mod && item.ModKey is { Length: > 0 } modKey ? modKey : item.Key;
    }

    /// <summary>Every folder holding that item (many-to-many, see the class remarks).</summary>
    public static IReadOnlyList<string> FoldersOf(LibraryKind kind, string itemKey, AppSettings? settings = null)
    {
        if (!Supports(kind) || string.IsNullOrWhiteSpace(itemKey)) return [];
        if (kind == LibraryKind.Mod) return ModFolders.FoldersOf(itemKey);

        var map = MembersMap(kind, Settings(settings));
        if (map is null) return [];
        return map.Where(kv => kv.Value.Contains(itemKey, StringComparer.OrdinalIgnoreCase))
                  .Select(kv => kv.Key)
                  .ToList();
    }

    /// <summary>
    /// One folder's rule, as a separate object an editor can cancel out of.
    /// </summary>
    /// <remarks>Never null: a folder with no stored rule reads as an empty, switched-off one.
    /// <see cref="ContentFolderRule.FolderName"/> is filled in so a refusal can name the
    /// folder.</remarks>
    public static ContentFolderRule Get(LibraryKind kind, string folder, AppSettings? settings = null)
    {
        var rule = (Stored(kind, folder, Settings(settings))?.Clone()) ?? new ContentFolderRule();
        rule.FolderName = folder;
        rule.AndAlso.Clear();
        return rule;
    }

    /// <summary>
    /// Writes one folder's rule, and persists it.
    /// </summary>
    /// <remarks>Unlike the <see cref="AppSettings"/> mutators it calls, this saves to disk. An empty,
    /// switched-off rule removes the entry, as <c>AppSettings.SetWorldFolderRule</c> does.</remarks>
    public static void Set(LibraryKind kind, string folder, ContentFolderRule? rule, AppSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (kind == LibraryKind.Mod) { ModFolders.SetRule(folder, rule); return; }

        var store = Settings(settings);
        switch (kind)
        {
            case LibraryKind.World: store.SetWorldFolderRule(folder, rule); break;
            case LibraryKind.ResourcePack: store.SetResourcePackFolderRule(folder, rule); break;
            case LibraryKind.ShaderPack: store.SetShaderFolderRule(folder, rule); break;
            default: return;
        }
        store.Save();
    }

    /// <summary>
    /// The rule one item inherits from the folder(s) it is filed under, or null when no
    /// folder narrows it.
    /// </summary>
    /// <remarks>
    /// Folders with no active rule are skipped, so the common case costs one dictionary walk. With
    /// several rules the first is returned and the others are chained in
    /// <see cref="ContentFolderRule.AndAlso"/>; merging the csv lists would give their union and
    /// widen both rules.
    /// </remarks>
    public static ContentFolderRule? Effective(LibraryKind kind, string itemKey, AppSettings? settings = null)
    {
        if (!Supports(kind) || string.IsNullOrWhiteSpace(itemKey)) return null;
        // An instance's own file cannot inherit a placement rule (see the class remarks).
        if (kind != LibraryKind.Mod && !IsLibraryKey(kind, itemKey)) return null;

        var store = Settings(settings);
        List<ContentFolderRule>? active = null;
        foreach (var folder in FoldersOf(kind, itemKey, store))
        {
            var rule = Stored(kind, folder, store);
            if (rule is null || !rule.IsActive) continue;

            // Cloned, and the name written on the clone: the stored object is shared with whatever
            // else reads this map, and AndAlso must never be appended to it.
            var copy = rule.Clone();
            copy.FolderName = folder;
            copy.AndAlso.Clear();
            (active ??= []).Add(copy);
        }

        if (active is null || active.Count == 0) return null;
        var head = active[0];
        for (var i = 1; i < active.Count; i++) head.AndAlso.Add(active[i]);
        return head;
    }

    /// <inheritdoc cref="Effective"/>
    /// <remarks>Prefer this overload: it knows mods are filed under their identity and everything
    /// else under its library key.</remarks>
    public static ContentFolderRule? EffectiveFor(LibraryItem item, AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Worlds, resource packs and shader packs have no folder rules: their folders are
        // organisation only, with no UI to show or remove a rule. Rules stored for them are ignored
        // but left on disk.
        // Mods still have folder rules (ModLibraryService edits them), so they are not in this list.
        if (item.Kind is LibraryKind.World or LibraryKind.ResourcePack or LibraryKind.ShaderPack)
            return null;

        return Effective(item.Kind, KeyFor(item), settings);
    }

    /// <summary>
    /// True when this key names something in the library rather than one instance's own file.
    /// </summary>
    /// <remarks>Matched against the <see cref="ContentLibraryService.FolderNameFor"/> prefix rather
    /// than "contains a slash", so a world whose name contains a separator cannot fool it. The two
    /// key shapes are <c>saves/Skyblock.zip</c> and <c>{packId:N}:Skyblock</c>.</remarks>
    public static bool IsLibraryKey(LibraryKind kind, string? key) =>
        key is { Length: > 0 }
        && key.StartsWith(ContentLibraryService.FolderNameFor(kind) + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The library items filed under one folder, for the folder-rule dialog's consequences column.
    /// </summary>
    /// <remarks>Scans the library once and keeps the folder's order, which is how the page lists
    /// them. Member keys with no library item (an instance's own save, or something since deleted)
    /// are dropped.</remarks>
    public static IReadOnlyList<LibraryItem> MemberItems(
        LibraryKind kind, string folder, ContentLibraryService library, AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (!Supports(kind) || string.IsNullOrWhiteSpace(folder)) return [];

        var byKey = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in library.Scan(kind)) byKey[KeyFor(item)] = item;

        var items = new List<LibraryItem>();
        foreach (var key in MemberKeys(kind, folder, settings))
            if (byKey.TryGetValue(key, out var item)) items.Add(item);
        return items;
    }

    // ── the stores ───────────────────────────────────────────────────────────

    private static AppSettings Settings(AppSettings? supplied) => supplied ?? App.State.Settings;

    /// <summary>The stored rule object for one folder, or null. Not a copy: callers that pass it on
    /// must clone it, as <see cref="Get"/> and <see cref="Effective"/> do.</summary>
    private static ContentFolderRule? Stored(LibraryKind kind, string folder, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        if (kind == LibraryKind.Mod) return ModFolders.GetRule(folder);
        var map = RulesMap(kind, settings);
        return map is not null && map.TryGetValue(folder, out var rule) ? rule : null;
    }

    private static Dictionary<string, List<string>>? MembersMap(LibraryKind kind, AppSettings settings) => kind switch
    {
        LibraryKind.World => settings.WorldFolders,
        LibraryKind.ResourcePack => settings.ResourcePackFolders,
        LibraryKind.ShaderPack => settings.ShaderFolders,
        _ => null
    };

    private static Dictionary<string, ContentFolderRule>? RulesMap(LibraryKind kind, AppSettings settings) => kind switch
    {
        LibraryKind.World => settings.WorldFolderRules,
        LibraryKind.ResourcePack => settings.ResourcePackFolderRules,
        LibraryKind.ShaderPack => settings.ShaderFolderRules,
        _ => null
    };
}
