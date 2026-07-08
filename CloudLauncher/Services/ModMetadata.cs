using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>Which side(s) of the game a mod is needed on. Drives the Files-tab
/// server/client split and the §6 "duplicate server+client" folder.</summary>
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
    CustomTree   = 2
}

/// <summary>
/// Per-mod managed flags, saved to the modpack (synced via <c>game/.cloudlauncher/mods.json</c>).
/// Effective enable/disable lives on disk as <c>.jar</c> vs <c>.jar.disabled</c>; this record
/// holds only the *managed* flags so there's no dual-write conflict with the file state.
/// </summary>
public sealed class ModMeta
{
    /// <summary>Custom category names this mod belongs to (see <see cref="PackModMetadata.Categories"/>).</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>Priority band. 0 = normal/unset. Drives sort order and colored borders.</summary>
    public int Priority { get; set; }

    public ModSide Side { get; set; } = ModSide.Both;

    /// <summary>A support/library mod — eligible for the library-aware disable cascade.</summary>
    public bool IsLibrary { get; set; }

    /// <summary>Marked for the test set (grey download, "Run as Test").</summary>
    public bool IsTesting { get; set; }

    /// <summary>Mod keys this mod is known to be incompatible with.</summary>
    public List<string> IncompatibleWith { get; set; } = new();

    /// <summary>Incompatible with mods not yet known/installed.</summary>
    public bool IncompatibleWithUnknown { get; set; }

    /// <summary>Updating this mod is known to break things — warn before updating.</summary>
    public bool UpdateIncompatible { get; set; }

    public bool UpdateIncompatibleWithUnknown { get; set; }

    /// <summary>Version pinned by the user; updates won't move past it without intent.</summary>
    public string? PinnedVersionId { get; set; }

    /// <summary>Manually-declared dependency mod keys (in addition to resolved ones).</summary>
    public List<string> ManualDependencies { get; set; } = new();

    /// <summary>Flagged as an optional "extra" — togglable in bulk from the Files tab.</summary>
    public bool IsExtra { get; set; }

    /// <summary>The store this mod was installed from. Pins a cross-listed jar to that store's listing
    /// (source label, page link, version + update checks) instead of defaulting to Modrinth. Null = auto.</summary>
    public ModSource? PreferredSource { get; set; }

    [JsonIgnore]
    public bool IsDefault =>
        Categories.Count == 0 && Priority == 0 && Side == ModSide.Both &&
        !IsLibrary && !IsTesting && IncompatibleWith.Count == 0 && !IncompatibleWithUnknown &&
        !UpdateIncompatible && !UpdateIncompatibleWithUnknown && PinnedVersionId is null &&
        ManualDependencies.Count == 0 && !IsExtra && PreferredSource is null;

    public ModMeta Clone() => new()
    {
        Categories = new List<string>(Categories),
        Priority = Priority,
        Side = Side,
        IsLibrary = IsLibrary,
        IsTesting = IsTesting,
        IncompatibleWith = new List<string>(IncompatibleWith),
        IncompatibleWithUnknown = IncompatibleWithUnknown,
        UpdateIncompatible = UpdateIncompatible,
        UpdateIncompatibleWithUnknown = UpdateIncompatibleWithUnknown,
        PinnedVersionId = PinnedVersionId,
        ManualDependencies = new List<string>(ManualDependencies),
        IsExtra = IsExtra,
        PreferredSource = PreferredSource
    };
}

/// <summary>A user-defined mod category. Color is an optional <c>#RRGGBB</c> hex.</summary>
public sealed class CustomCategory
{
    public string Name { get; set; } = "";
    public string? Color { get; set; }

    /// <summary>Built-in, auto-managed category (e.g. "Library"): always present, can't be renamed or deleted.</summary>
    public bool Builtin { get; set; }
}

/// <summary>Pack-wide toggles for the management system (the §7 "Advanced Settings" tab).</summary>
public sealed class ModAdvancedSettings
{
    public bool AutoDownloadDependencies { get; set; } = true;
    public bool CascadeDisableDependents { get; set; } = true;
    public bool CascadeDisableLibraries { get; set; } = true;
    public bool WarnOnUpdateIncompatible { get; set; } = true;
    public bool ShowDependencyLines { get; set; } = true;
    public ModClusterMode DefaultClusterMode { get; set; } = ModClusterMode.Category;
}

/// <summary>Root document persisted to <c>game/.cloudlauncher/mods.json</c> and synced to collaborators.</summary>
public sealed class PackModMetadata
{
    public int Version { get; set; } = 1;

    /// <summary>Per-mod flags, keyed by a stable mod key (see <see cref="ModMetadataService.KeyFor"/>).</summary>
    public Dictionary<string, ModMeta> Mods { get; set; } = new();

    public List<CustomCategory> Categories { get; set; } = new();

    public ModAdvancedSettings Advanced { get; set; } = new();

    /// <summary>User-arranged node positions for the Graph view's custom layout (mod key → [x, y]).</summary>
    public Dictionary<string, double[]> CustomLayout { get; set; } = new();

    /// <summary>User-arranged cluster positions for the Graph view's category layout (category → [x, y]).</summary>
    public Dictionary<string, double[]> CategoryLayout { get; set; } = new();
}
