using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>Which side(s) of the game a mod is needed on. Drives the Files-tab server/client split
/// and the "duplicate server+client" folder.</summary>
public enum ModSide
{
    Both   = 0,
    Client = 1,
    Server = 2
}

/// <summary>How the Graph view groups nodes.</summary>
public enum ModClusterMode
{
    Category     = 0,
    Dependencies = 1,
    CustomTree   = 2,
    /// <summary>Each dependency sits in the middle of a circle of the mods that need it.</summary>
    Circles      = 3
}

/// <summary>The release channels a mod update may come from. Stored as the lower-case store
/// wording ("release" / "beta" / "alpha") so a pack's mods.json reads the same on both stores.</summary>
public static class ModUpdateChannel
{
    public const string Release = "release";
    public const string Beta = "beta";
    public const string Alpha = "alpha";

    public static readonly IReadOnlyList<string> All = [Release, Beta, Alpha];

    /// <summary>Stability rank: release 2, beta 1, alpha (and anything unknown) 0. A channel
    /// admits every version whose rank is at least its own, so "beta" means release + beta.</summary>
    public static int Rank(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        Release => 2,
        Beta => 1,
        _ => 0
    };

    /// <summary>The canonical spelling, or null for anything that isn't a channel.</summary>
    public static string? Normalize(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        Release => Release,
        Beta => Beta,
        Alpha => Alpha,
        _ => null
    };

    public static string Label(string? channel) => Normalize(channel) switch
    {
        Beta => "Beta",
        Alpha => "Alpha",
        _ => "Release"
    };

    /// <summary>Label plus what it admits, for menus and tooltips.</summary>
    public static string Describe(string? channel) => Normalize(channel) switch
    {
        Beta => "Beta - releases and betas",
        Alpha => "Alpha - every published version, including alphas",
        _ => "Release - stable releases only"
    };

    /// <summary>True when a version of <paramref name="versionChannel"/> may be offered under
    /// <paramref name="channel"/>.</summary>
    public static bool Admits(string? channel, string? versionChannel) => Rank(versionChannel) >= Rank(channel);

    /// <summary>The version to install out of <paramref name="candidates"/> (already filtered to the
    /// ones that fit the pack): the newest one <paramref name="channel"/> admits, falling back to the
    /// newest of any channel so a mod that only ever publishes betas still installs. One rule for the
    /// browse page, the explorer window, the Mods view and dependency resolution.</summary>
    public static T? PickNewest<T>(
        IEnumerable<T> candidates,
        string? channel,
        Func<T, string?> channelOf,
        Func<T, DateTimeOffset> publishedAt) where T : class
    {
        var ordered = candidates.OrderByDescending(publishedAt).ToList();
        return ordered.FirstOrDefault(v => Admits(channel, channelOf(v))) ?? ordered.FirstOrDefault();
    }
}

/// <summary>
/// Per-mod managed flags, saved to the modpack (synced via <c>game/.cloudlauncher/mods.json</c>).
/// Enabled/disabled lives on disk as <c>.jar</c> vs <c>.jar.disabled</c>; this record holds only the
/// managed flags so the two can't conflict.
/// </summary>
public sealed class ModMeta
{
    /// <summary>Custom category names this mod belongs to (see
    /// <see cref="PackModMetadata.Categories"/>).</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>Priority band. 0 = normal/unset. Drives sort order and colored borders.</summary>
    public int Priority { get; set; }

    /// <summary>How much content this mod adds to the pack, 0-3 (unset / small / medium / large).
    /// Independent of <see cref="Priority"/>: priority is how much you care, content size is how much
    /// the mod brings. Sorts below priority everywhere.</summary>
    public int ContentSize { get; set; }

    public ModSide Side { get; set; } = ModSide.Both;

    /// <summary>A support/library mod, eligible for the library-aware disable cascade.</summary>
    public bool IsLibrary { get; set; }

    /// <summary>Marked for the test set (grey download, "Run as Test").</summary>
    public bool IsTesting { get; set; }

    /// <summary>Mod keys this mod is known to be incompatible with.</summary>
    public List<string> IncompatibleWith { get; set; } = new();

    /// <summary>Incompatible with mods not yet known/installed.</summary>
    public bool IncompatibleWithUnknown { get; set; }

    /// <summary>Updating this mod is known to break things; warn before updating.</summary>
    public bool UpdateIncompatible { get; set; }

    /// <summary>Hold this mod at the version it's on. A bulk "Update all" skips it entirely;
    /// updating it on its own still works, but asks first. Stronger than
    /// <see cref="UpdateIncompatible"/>, which only warns.</summary>
    public bool UpdateLocked { get; set; }

    public bool UpdateIncompatibleWithUnknown { get; set; }

    /// <summary>Version pinned by the user; updates won't move past it without intent.</summary>
    public string? PinnedVersionId { get; set; }

    /// <summary>Which release channel this mod's updates may come from ("release" / "beta" / "alpha");
    /// null follows the pack-wide <see cref="ModAdvancedSettings.UpdateChannel"/>.</summary>
    public string? UpdateChannel { get; set; }

    /// <summary>Manually-declared dependency mod keys (in addition to resolved ones).</summary>
    public List<string> ManualDependencies { get; set; } = new();

    /// <summary>Flagged as an optional "extra", togglable in bulk from the Files tab.</summary>
    public bool IsExtra { get; set; }

    /// <summary>The store this mod was installed from. Pins a cross-listed jar to that store's listing
    /// (source label, page link, version and update checks) instead of defaulting to Modrinth.
    /// Null = auto.</summary>
    public ModSource? PreferredSource { get; set; }

    /// <summary>Free-text planning note the user attached to this mod (why it's here, what to test,
    /// what it conflicts with...). Shown in the options menu, the card indicator and the planning
    /// board.</summary>
    public string? Note { get; set; }

    /// <summary>Fields this launcher doesn't know, from a newer one that saved the pack. Kept and
    /// written back unchanged, so an older launcher never strips what a newer one added.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <remarks>An entry holding only fields this launcher doesn't know is not default: dropping it
    /// (see <see cref="ModMetadataService.StoreMeta"/>) would drop them.</remarks>
    [JsonIgnore]
    public bool IsDefault =>
        Categories.Count == 0 && Priority == 0 && ContentSize == 0 && Side == ModSide.Both &&
        !IsLibrary && !IsTesting && IncompatibleWith.Count == 0 && !IncompatibleWithUnknown &&
        !UpdateIncompatible && !UpdateIncompatibleWithUnknown && !UpdateLocked && PinnedVersionId is null &&
        UpdateChannel is null &&
        ManualDependencies.Count == 0 && !IsExtra && PreferredSource is null &&
        string.IsNullOrWhiteSpace(Note) &&
        Extra is not { Count: > 0 };

    public ModMeta Clone() => new()
    {
        Categories = new List<string>(Categories),
        Priority = Priority,
        ContentSize = ContentSize,
        Side = Side,
        IsLibrary = IsLibrary,
        IsTesting = IsTesting,
        IncompatibleWith = new List<string>(IncompatibleWith),
        IncompatibleWithUnknown = IncompatibleWithUnknown,
        UpdateIncompatible = UpdateIncompatible,
        UpdateIncompatibleWithUnknown = UpdateIncompatibleWithUnknown,
        UpdateLocked = UpdateLocked,
        PinnedVersionId = PinnedVersionId,
        UpdateChannel = UpdateChannel,
        ManualDependencies = new List<string>(ManualDependencies),
        IsExtra = IsExtra,
        PreferredSource = PreferredSource,
        Note = Note,
        Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra)
    };
}

/// <summary>Labels for <see cref="ModMeta.ContentSize"/>, so the same wording is used everywhere.</summary>
public static class ModContentSize
{
    public const int Max = 3;

    public static string Label(int size) => size switch
    {
        1 => "Small",
        2 => "Medium",
        3 => "Large",
        _ => "Unset"
    };

    /// <summary>Label plus what the band means, for menus and tooltips.</summary>
    public static string Describe(int size) => size switch
    {
        1 => "Small - a tweak or a handful of additions",
        2 => "Medium - a decent chunk of new content",
        3 => "Large - a major, pack-defining mod",
        _ => "Unset - no content size recorded"
    };

    public static int Clamp(int size) => Math.Clamp(size, 0, Max);
}

/// <summary>A user-defined mod category. Color is an optional <c>#RRGGBB</c> hex.</summary>
public sealed class CustomCategory
{
    public string Name { get; set; } = "";
    public string? Color { get; set; }

    /// <summary>Built-in, auto-managed category (e.g. "Library"): always present, can't be renamed or
    /// deleted.</summary>
    public bool Builtin { get; set; }

    /// <summary>Fields this launcher doesn't know, kept and written back (see <see cref="ModMeta.Extra"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A tag a pack defines for its mods, as stored in <see cref="PackModMetadata.TagDefs"/>: its
/// name and the <c>#RRGGBB</c> its pill is drawn in.</summary>
/// <remarks>Not a <see cref="CustomCategory"/>: that one always writes <c>Builtin</c>, and the tools
/// that read tags from the file are written against exactly <c>Name</c> and <c>Color</c>.</remarks>
public sealed class ModTagDef
{
    public string Name { get; set; } = "";
    public string? Color { get; set; }

    /// <summary>Fields this launcher doesn't know, kept and written back (see <see cref="ModMeta.Extra"/>).</summary>
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A tag as the rest of the launcher sees it: a copy of its name and colour, so a list row or
/// a menu never holds on to the stored document's own objects.</summary>
public sealed record ModTag(string Name, string? Color);

/// <summary>Pack-wide toggles for the management system (the "Advanced Settings" tab).</summary>
public sealed class ModAdvancedSettings
{
    public bool AutoDownloadDependencies { get; set; } = true;
    public bool CascadeDisableDependents { get; set; } = true;
    public bool CascadeDisableLibraries { get; set; } = true;
    public bool WarnOnUpdateIncompatible { get; set; } = true;
    public bool ShowDependencyLines { get; set; } = true;
    public ModClusterMode DefaultClusterMode { get; set; } = ModClusterMode.Category;

    /// <summary>The store a mod listed on both CurseForge and Modrinth follows when it has no
    /// <see cref="ModMeta.PreferredSource"/> of its own (label, page link, version and update checks).
    /// Null = Modrinth first. Packs imported from a CurseForge modpack are set to CurseForge.</summary>
    public ModSource? PreferredSource { get; set; }

    /// <summary>Pack-wide release channel for downloads and update checks ("release" / "beta" /
    /// "alpha"), or null to follow the launcher-wide default
    /// (<see cref="AppSettings.ModVersionChannel"/>); a mod's own <see cref="ModMeta.UpdateChannel"/>
    /// overrides both. See <see cref="ModUpdateChannel"/>.</summary>
    public string? UpdateChannel { get; set; }

    /// <summary>Settings this launcher doesn't know, kept and written back (see <see cref="ModMeta.Extra"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Root document persisted to <c>game/.cloudlauncher/mods.json</c> and synced to
/// collaborators.</summary>
public sealed class PackModMetadata
{
    /// <summary>Document schema version. 2 added "follow the launcher default" as the pack channel:
    /// see <see cref="ModMetadataService.Migrate"/>.</summary>
    public int Version { get; set; } = PackModMetadata.CurrentVersion;

    public const int CurrentVersion = 2;

    /// <summary>Per-mod flags, keyed by a stable mod key (see
    /// <see cref="ModMetadataService.KeyFor"/>).</summary>
    public Dictionary<string, ModMeta> Mods { get; set; } = new();

    public List<CustomCategory> Categories { get; set; } = new();

    public ModAdvancedSettings Advanced { get; set; } = new();

    /// <summary>User-arranged node positions for the Graph view's custom layout
    /// (mod key -> [x, y]).</summary>
    public Dictionary<string, double[]> CustomLayout { get; set; } = new();

    /// <summary>User-arranged cluster positions for the Graph view's category layout
    /// (category -> [x, y]).</summary>
    public Dictionary<string, double[]> CategoryLayout { get; set; } = new();

    /// <summary>Categories folded shut in the Graph view's category mode. Kept with the pack (rather
    /// than per machine) because it is part of how the pack is laid out, like the positions above.</summary>
    public List<string> CollapsedCategories { get; set; } = new();

    // ── tags ──
    // TagDefs and Tags are read by tools outside the launcher (a pack's editions are built from them),
    // so their shape is a contract: PascalCase keys, mod ids in lower case, in this order ahead of
    // EarlyAccess. Both are null until the pack has something to say, and null is not written, so a
    // pack without tags keeps the file it always had. Once a key is in the file it stays, even empty:
    // a reader takes a present "Tags" for the truth and a missing one for a launcher too old to know
    // tags. They are read leniently (see the converters below), because one wrong value in a key a
    // tool or a person wrote by hand must never cost the whole file.
    // A mod's Side is no part of this: it is ModMeta.Side, under the mod's key in Mods, and nowhere
    // else. A "Sides" key that a file may hold is not the launcher's: like every key it doesn't know,
    // it is kept in Extra and written back as it was, and it gives no mod a side.

    /// <summary>The tags this pack defines for its mods, in the order menus and pills show them.
    /// A pack that defines none has no Tags menu.</summary>
    [JsonConverter(typeof(ModTagDefsConverter))]
    public List<ModTagDef>? TagDefs { get; set; }

    /// <summary>Which mods carry which tags: mod id -> the names of its tags, in
    /// <see cref="TagDefs"/> order.</summary>
    /// <remarks>Keyed by the mod id the jar itself declares (lower case), like
    /// <see cref="EarlyAccess"/> and for the same reason. A jar that declares several ids carries a tag
    /// when any of them does; the launcher writes it under every one.</remarks>
    [JsonConverter(typeof(ModTagMapConverter))]
    public SortedDictionary<string, List<string>>? Tags { get; set; }

    /// <summary>Mods marked early access in this pack: shared with the people who get early versions
    /// and kept out of released packs (an export leaves them out unless it is told to take them).</summary>
    /// <remarks>Held by the mod id the jar itself declares (lower case), not by the
    /// <see cref="Mods"/> key: an unreleased mod has no store id, so its key would be the file name,
    /// and every new version of the jar would lose the mark.</remarks>
    public List<string> EarlyAccess { get; set; } = new();

    /// <summary>Top-level keys this launcher doesn't know, from a newer one that saved the pack. Kept
    /// and written back unchanged (after the known keys), so an older launcher never strips what a
    /// newer one added.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Reads <see cref="PackModMetadata.TagDefs"/> without ever failing the file: a list of
/// <c>{"Name", "Color"}</c> objects, where a bare string is taken as a name and anything else is
/// skipped. Fields it doesn't know stay with their tag.</summary>
internal sealed class ModTagDefsConverter : JsonConverter<List<ModTagDef>>
{
    public override List<ModTagDef> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var defs = new List<ModTagDef>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return defs;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                defs.Add(new ModTagDef { Name = item.GetString() ?? "" });
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;

            var def = new ModTagDef();
            foreach (var field in item.EnumerateObject())
            {
                var text = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null;
                if (string.Equals(field.Name, nameof(ModTagDef.Name), StringComparison.OrdinalIgnoreCase))
                    def.Name = text ?? "";
                else if (string.Equals(field.Name, nameof(ModTagDef.Color), StringComparison.OrdinalIgnoreCase))
                    def.Color = text;
                else
                    (def.Extra ??= new())[field.Name] = field.Value.Clone();   // outlives the document
            }
            defs.Add(def);
        }
        return defs;
    }

    public override void Write(Utf8JsonWriter writer, List<ModTagDef> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var def in value)
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(ModTagDef.Name), def.Name);
            if (def.Color is not null) writer.WriteString(nameof(ModTagDef.Color), def.Color);
            if (def.Extra is not null)
                foreach (var (name, element) in def.Extra)
                {
                    writer.WritePropertyName(name);
                    element.WriteTo(writer);
                }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>Reads <see cref="PackModMetadata.Tags"/> without ever failing the file: mod id -> list of
/// tag names. Ids are lower-cased (two spellings of one id are merged), a single name written as a
/// string counts as a list of one, and anything else is skipped. Written sorted by mod id, so the file
/// doesn't reshuffle between saves.</summary>
internal sealed class ModTagMapConverter : JsonConverter<SortedDictionary<string, List<string>>>
{
    public override SortedDictionary<string, List<string>> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var map = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var id = entry.Name.Trim().ToLowerInvariant();
            if (id.Length == 0) continue;

            IEnumerable<JsonElement> values = entry.Value.ValueKind switch
            {
                JsonValueKind.Array => entry.Value.EnumerateArray(),
                JsonValueKind.String => [entry.Value],
                _ => []
            };
            foreach (var value in values)
            {
                if (value.ValueKind != JsonValueKind.String) continue;
                var name = value.GetString()?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (!map.TryGetValue(id, out var names)) map[id] = names = new List<string>();
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
        }
        return map;
    }

    public override void Write(Utf8JsonWriter writer, SortedDictionary<string, List<string>> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (id, names) in value)
        {
            writer.WriteStartArray(id);
            foreach (var name in names) writer.WriteStringValue(name);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
