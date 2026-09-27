using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed partial class AppSettings
{
    /// <summary>Where the launcher talks to its server. <see cref="MigrateServerUrl"/> moves settings
    /// from older builds onto the HTTPS default.</summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>The address every new install uses, over TLS.</summary>
    public const string DefaultServerUrl = "https://api.cloudlauncher.co";

    /// <summary>The previous address, still served. Used while a resolver does not know the new name yet.</summary>
    public const string FallbackServerUrl = "https://launcher.crispythedev.duckdns.org";

    /// <summary>Addresses older builds shipped with. Settings still pointing at one are moved to
    /// <see cref="DefaultServerUrl"/> on load, so tokens stop going over plain HTTP.</summary>
    private static readonly string[] LegacyServerUrls =
    [
        "http://130.61.131.193:5000",
        "https://130.61.131.193:5000",
        "http://130.61.131.193:5000/",
        FallbackServerUrl,
        FallbackServerUrl + "/",
    ];

    // AccessToken and RefreshToken live in AppSettings.Secrets.cs, in the secret store.
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public string? Username { get; set; }
    public Guid? UserId { get; set; }
    public string PacksRoot { get; set; } = Path.Combine(DataRoot, "packs");

    /// <summary>Default max RAM (MB) used when launching a pack that has no override.</summary>
    public int DefaultMaxRamMb { get; set; } = 4096;

    /// <summary>Per-pack max RAM override (MB). Empty/missing means "use default".</summary>
    public Dictionary<Guid, int> PackMaxRamMb { get; set; } = new();

    /// <summary>Per-pack extra JVM arguments, appended after CmlLib's stock GC flags. Whitespace-separated,
    /// e.g. "-Dvoxy.geometryBufferSizeOverrideMB=1280". Missing entry = none.</summary>
    public Dictionary<Guid, string> PackJvmArgs { get; set; } = new();

    // ── dedicated server, per pack ───────────────────────────────────────────
    // A server usually wants a smaller heap and different GC flags than the client, so these are
    // separate keys, but each falls back to the client value. All are optional: older builds drop
    // keys they don't know when they save.

    /// <summary>Per-pack server max RAM override (MB). Missing = use the client's value for the pack.</summary>
    public Dictionary<Guid, int> PackServerMaxRamMb { get; set; } = new();

    /// <summary>Per-pack extra JVM arguments for the server, added after the stock GC flags (see
    /// <c>LaunchService.BuildServerJvmArguments</c>). Missing entry = none.</summary>
    public Dictionary<Guid, string> PackServerJvmArgs { get; set; } = new();

    /// <summary>Whether the user has accepted the Minecraft EULA for this pack's server.</summary>
    /// <remarks>Per pack and false by default, since the user has to accept it themselves. An
    /// <c>eula.txt</c> accepted on disk is also honoured (<c>LaunchService.EulaAcceptedOnDisk</c>).</remarks>
    public Dictionary<Guid, bool> PackServerEulaAccepted { get; set; } = new();

    /// <summary>Per-pack "bring the server back up after a crash". Missing entry = off.</summary>
    /// <remarks>Off by default so a crashed server stays down with its log intact.
    /// <c>ServerHostService</c> caps the restarts so a boot loop can't spin forever.</remarks>
    public Dictionary<Guid, bool> PackServerAutoRestart { get; set; } = new();

    /// <summary>Per-pack auto-update preference. True = always update before launch.</summary>
    public Dictionary<Guid, bool> PackAutoUpdate { get; set; } = new();

    /// <summary>Java executable for packs without their own override. Empty means automatic: the
    /// launcher picks (and if needed downloads) the Java version the pack's Minecraft needs.</summary>
    public string? DefaultJavaPath { get; set; }

    /// <summary>Per-pack Java executable override (full path to java.exe). Missing entry = use
    /// <see cref="DefaultJavaPath"/>, else automatic.</summary>
    public Dictionary<Guid, string> PackJavaPath { get; set; } = new();

    /// <summary>The Java executable a pack should launch with, or null for automatic selection.
    /// A stored path that no longer exists is ignored rather than failing the launch.</summary>
    public string? GetJavaPathFor(Guid packId)
    {
        if (PackJavaPath.TryGetValue(packId, out var own) && !string.IsNullOrWhiteSpace(own) && File.Exists(own))
            return own;
        return !string.IsNullOrWhiteSpace(DefaultJavaPath) && File.Exists(DefaultJavaPath) ? DefaultJavaPath : null;
    }

    public void SetJavaPathFor(Guid packId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) PackJavaPath.Remove(packId);
        else PackJavaPath[packId] = path.Trim();
    }

    /// <summary>Extra game/-relative paths that are never uploaded and never touched by a download,
    /// on top of <see cref="PrivateAssetPolicy.BuiltIn"/>. Rule syntax ("config/foo/" = whole folder);
    /// applies to every pack.</summary>
    /// <remarks>Edited in Settings. Stored verbatim: rewriting a pattern on load could stop it matching
    /// the path the user typed.</remarks>
    public List<string> PrivatePathPatterns { get; set; } = new();

    /// <summary>Whether "auto-update before launch" is on for a pack the user has never toggled.</summary>
    /// <remarks>On for shared packs owned by someone else, so people get the owner's fixes. Off for packs
    /// you own, since pulling the server copy would overwrite local changes not uploaded yet. A stored
    /// value always wins.</remarks>
    public bool GetAutoUpdateFor(Guid packId, bool isShared, Guid ownerId) =>
        PackAutoUpdate.TryGetValue(packId, out var stored) ? stored : (isShared && ownerId != UserId);

    /// <summary>Per-pack auto-apply-rules preference. Missing entry = on.</summary>
    public Dictionary<Guid, bool> PackAutoApplyRules { get; set; } = new();

    /// <summary>Per-pack "Low mode": turn the heavy visual settings down before launch so the pack
    /// runs on a modest machine. Missing entry = on, since new players are the most likely to need
    /// it.</summary>
    public Dictionary<Guid, bool> PackLowMode { get; set; } = new();

    /// <summary>When true, launcher-started games are embedded in the custom host window (play-time
    /// bar, instance side panel, collapse/fullscreen hotkeys); when false, Minecraft opens in its own
    /// window and the launcher just tracks the process. Off by default.</summary>
    public bool UseCustomGameWindow { get; set; } = false;

    /// <summary>Set once <see cref="ApplyCustomGameWindowDefault"/> has run for this profile.</summary>
    public bool CustomGameWindowDefaultApplied { get; set; }

    /// <summary>Turns the custom game window off once, for profiles written while it was on by
    /// default.</summary>
    /// <remarks>Settings files from before 1.1.10 store an explicit <c>true</c>, so the new default never
    /// reached them. The flag makes this run once, so turning it back on in Settings sticks.</remarks>
    public void ApplyCustomGameWindowDefault()
    {
        if (CustomGameWindowDefaultApplied) return;
        CustomGameWindowDefaultApplied = true;
        UseCustomGameWindow = false;
        try { Save(); } catch { /* read-only profile: the in-memory value is still right */ }
    }

    /// <summary>Set once <see cref="ApplyLookAccentDefault"/> has run for this profile.</summary>
    public bool LookAccentDefaultApplied { get; set; }

    /// <summary>The default Slate accent that 1.8.0 to 1.8.2 saved into every profile.</summary>
    private const string PreviousDefaultAccent = "#D9805E";

    /// <summary>Moves a profile still on the old default accent onto the new one, once.</summary>
    /// <remarks>1.8.0 writes the whole <see cref="LookSettings"/> on the first save, so a new
    /// <see cref="LookSettings.DefaultAccent"/> would otherwise only reach fresh installs. Terracotta is
    /// still a preset, and the flag means picking it again sticks.</remarks>
    public void ApplyLookAccentDefault()
    {
        if (LookAccentDefaultApplied) return;
        LookAccentDefaultApplied = true;
        if (Look is { } look && string.Equals(look.Accent?.Trim(), PreviousDefaultAccent, StringComparison.OrdinalIgnoreCase))
            look.Accent = LookSettings.DefaultAccent;
        try { Save(); } catch { /* read-only profile: the in-memory value is still right */ }
    }

    /// <summary>Default state for the pack page inside the custom Minecraft window.</summary>
    public bool MinecraftWindowPackPageCollapsedByDefault { get; set; } = false;

    /// <summary>Per-pack pack-page default override. Missing entry = use launcher default.</summary>
    public Dictionary<Guid, bool> PackMinecraftWindowPackPageCollapsedByDefault { get; set; } = new();

    /// <summary>Default fullscreen state for the custom Minecraft window.</summary>
    public bool MinecraftWindowBorderlessFullscreenByDefault { get; set; } = false;

    /// <summary>Per-pack borderless fullscreen default override. Missing entry = use launcher default.</summary>
    public Dictionary<Guid, bool> PackMinecraftWindowBorderlessFullscreenByDefault { get; set; } = new();

    /// <summary>Pack list sort preference. Pinned packs are always shown first.</summary>
    public PackSortMode PackSortMode { get; set; } = PackSortMode.LastPlayed;

    /// <summary>Instances page layout: cards (the default) or a compact list.</summary>
    public PackListLayout PackListLayout { get; set; } = PackListLayout.Cards;

    /// <summary>World list sort preference.</summary>
    public WorldSortMode WorldSortMode { get; set; } = WorldSortMode.Modified;

    public ResourcePackSortMode ResourcePackSortMode { get; set; } = ResourcePackSortMode.Modified;

    /// <summary>Shader list sort preference. Newest first by default, so a just-added shader is at the
    /// top. An older settings.json without the key gets this default too.</summary>
    public ShaderSortMode ShaderSortMode { get; set; } = ShaderSortMode.RecentlyAdded;

    // ── mod list view (Mods Management, List view) ──────────────────────────

    /// <summary>Which column the List view sorts by. Sticky across sessions.</summary>
    public ModListSortMode ModListSortMode { get; set; } = ModListSortMode.Priority;

    /// <summary>True when the List view sort runs against its natural direction.</summary>
    public bool ModListSortReversed { get; set; }

    /// <summary>List view "Hide disabled" filter. Sticky across sessions.</summary>
    public bool ModListHideDisabled { get; set; }

    /// <summary>List view store filter. Sticky across sessions. "Only updates" is not persisted, so
    /// the launcher never reopens to an apparently empty pack.</summary>
    public ModListSourceFilter ModListSourceFilter { get; set; } = ModListSourceFilter.All;

    /// <summary>Maximum width (DIPs) of a List view row's name/version block before its buttons start.
    /// Keeps the Options button near the mod name on a wide monitor.</summary>
    public double ModRowContentWidth { get; set; } = 620;

    /// <summary>Range a stored <see cref="ModRowContentWidth"/> is clamped to.</summary>
    public const double MinModRowContentWidth = 360, MaxModRowContentWidth = 2000;

    public double EffectiveModRowContentWidth =>
        double.IsFinite(ModRowContentWidth)
            ? Math.Clamp(ModRowContentWidth, MinModRowContentWidth, MaxModRowContentWidth)
            : 620;

    /// <summary>Whether a List view row's buttons sit against the right edge of the row (the default)
    /// or right after the name block.</summary>
    public bool ModRowActionsAtRight { get; set; } = true;

    // ── mod graph view (Mods Management, Graph view) ────────────────────────

    /// <summary>Whether the Graph view draws the dependency lines between nodes.</summary>
    /// <remarks>A per-launcher view preference. The pack's own
    /// <see cref="ModAdvancedSettings.ShowDependencyLines"/> lives in <c>mods.json</c> and syncs to
    /// collaborators, so toggling that would change the graph for everyone.</remarks>
    public bool GraphShowDependencyLines { get; set; } = true;

    /// <summary>How the Graph view groups nodes: the name of a <see cref="ModClusterMode"/> member, or
    /// null to follow the pack's <see cref="ModAdvancedSettings.DefaultClusterMode"/>.</summary>
    /// <remarks>Stored as the name, not the enum value, so a mode added by a newer launcher falls back
    /// to the pack default instead of mapping to whatever member shares its number.</remarks>
    public string? GraphClusterMode { get; set; }

    /// <summary>Whether the Planning board draws its grid and snaps cards to it. Applies to every board.</summary>
    public bool PlanGridEnabled { get; set; } = true;

    /// <summary>Release channel for mod downloads and updates when the pack has none of its own:
    /// "alpha" = newest whatever its channel, "beta" = releases and betas, "release" = stable only.
    /// See <see cref="ModUpdateChannel"/>.</summary>
    /// <remarks>Packs (Mods Management, Advanced) and single mods (right-click, Update channel) can
    /// still be pinned to release.</remarks>
    public string ModVersionChannel { get; set; } = ModUpdateChannel.Alpha;

    // ── downloads ────────────────────────────────────────────────────────────

    /// <summary>How many mod files download at once: "Update all", browse-page downloads with
    /// dependencies, and modpack installs.</summary>
    /// <remarks>Capped at 9 because the stores answer bigger bursts from one address with 403s and 429s
    /// (see <see cref="ApiClient"/>).</remarks>
    public int ModDownloadConcurrency { get; set; } = 3;

    public const int MinModDownloadConcurrency = 1, MaxModDownloadConcurrency = 9;

    /// <summary>The concurrency actually used, with the stored value clamped into range.</summary>
    public int EffectiveModDownloadConcurrency =>
        Math.Clamp(ModDownloadConcurrency, MinModDownloadConcurrency, MaxModDownloadConcurrency);

    /// <summary>How many mods are checked for updates at once.</summary>
    /// <remarks>Higher than the download concurrency: a check is a metadata read, usually answered from
    /// the shared version catalog's cache, so the connection is not the limit.</remarks>
    public int ModUpdateCheckConcurrency { get; set; } = 12;

    public const int MinModUpdateCheckConcurrency = 1, MaxModUpdateCheckConcurrency = 32;

    public int EffectiveModUpdateCheckConcurrency =>
        Math.Clamp(ModUpdateCheckConcurrency, MinModUpdateCheckConcurrency, MaxModUpdateCheckConcurrency);

    /// <summary>How many per-mod store requests an update check may make per second, across both
    /// stores.</summary>
    /// <remarks>Most mods are answered by a few bulk requests first, so this only paces the rest. The
    /// launcher server allows an account about 120 requests a second (see <c>ProxyUserLimits</c>), so
    /// the ceiling keeps a check plus browsing under that; set higher than the stores like, they ask
    /// to slow down, which the check then waits out.</remarks>
    public int ModUpdateChecksPerSecond { get; set; } = 10;

    public const int MinModUpdateChecksPerSecond = 1, MaxModUpdateChecksPerSecond = 100;

    public int EffectiveModUpdateChecksPerSecond =>
        Math.Clamp(ModUpdateChecksPerSecond, MinModUpdateChecksPerSecond, MaxModUpdateChecksPerSecond);

    /// <summary>The user's own CurseForge API key; empty means the server's shared key. A personal key
    /// has its own quota, so it avoids the 403s the shared key gets under load. Sent per request to the
    /// launcher's proxy, never stored server-side.</summary>
    /// <remarks>Kept in <see cref="SecretStore"/> (see <see cref="SetCurseForgeApiKey"/>); this is the
    /// in-memory copy.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CurseForgeApiKey { get; private set; }

    /// <summary>With a personal key set, talk to CurseForge directly instead of through the server's
    /// proxy, which queues everyone's traffic behind one address. On by default, since only people who
    /// want this set a key.</summary>
    public bool CurseForgeDirect { get; set; } = true;

    /// <summary>The plain-text settings.json slot the key was kept in before 1.8.0. Read so an
    /// existing key moves into <see cref="SecretStore"/>, then written back empty, unless the store
    /// can't be written on this PC; then the key stays here so it survives a restart.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("CurseForgeApiKey")]
    public string? LegacyCurseForgeApiKey
    {
        get => _keyLivesInSettings ? CurseForgeApiKey : null;
        set => _legacyCurseForgeApiKey = value;
    }

    private string? _legacyCurseForgeApiKey;

    /// <summary>True when the secret store failed and settings.json is carrying the key instead.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CurseForgeKeyInSettingsFile => _keyLivesInSettings;

    private bool _keyLivesInSettings;

    private const string CurseForgeKeySecret = "curseforge-key";

    /// <summary>Sets (or, with null/blank, clears) the user's own CurseForge key and stores it at once.
    /// Returns false when the secret store could not be written; the key is then kept in settings.json
    /// instead (see <see cref="SecretStore.LastError"/>).</summary>
    /// <remarks>The key has its own encrypted file because any running copy of the launcher (an older
    /// build, a second window) rewrites settings.json whole and would drop it. The settings.json
    /// fallback is there because an unencrypted key beats a lost one.</remarks>
    public bool SetCurseForgeApiKey(string? key)
    {
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        CurseForgeApiKey = key;
        var stored = SecretStore.Write(CurseForgeKeySecret, key);
        _keyLivesInSettings = !stored && key is not null;
        if (!stored) Save();
        return stored;
    }

    /// <summary>Loads the key from <see cref="SecretStore"/>, moving a key still sitting in
    /// settings.json (an older build, or the fallback above) into it first.</summary>
    private void LoadCurseForgeApiKey()
    {
        var stored = SecretStore.Read(CurseForgeKeySecret);
        if (stored is null && !string.IsNullOrWhiteSpace(_legacyCurseForgeApiKey))
        {
            stored = _legacyCurseForgeApiKey.Trim();
            _keyLivesInSettings = !SecretStore.Write(CurseForgeKeySecret, stored);
        }
        CurseForgeApiKey = stored;
        _legacyCurseForgeApiKey = null;
    }

    // ── servers ──────────────────────────────────────────────────────────────

    /// <summary>Admin details for servers the user runs, keyed by the server address as it appears in
    /// an instance's <c>servers.dat</c>, lower-cased (see <see cref="ServerKey"/>). Keyed on the address
    /// rather than an instance, since one server is usually reachable from several.</summary>
    public Dictionary<string, ServerAdminEntry> ServerAdmins { get; set; } = new();

    /// <summary>The key <see cref="ServerAdmins"/> uses for a server address.</summary>
    public static string ServerKey(string address) => (address ?? "").Trim().ToLowerInvariant();

    /// <summary>Admin details for one server, or null when the user has not filled any in.</summary>
    public ServerAdminEntry? GetServerAdmin(string address) =>
        ServerAdmins.TryGetValue(ServerKey(address), out var e) ? e : null;

    public ServerAdminEntry GetOrCreateServerAdmin(string address)
    {
        var key = ServerKey(address);
        if (!ServerAdmins.TryGetValue(key, out var e)) ServerAdmins[key] = e = new ServerAdminEntry();
        return e;
    }

    public void RemoveServerAdmin(string address) => ServerAdmins.Remove(ServerKey(address));

    // ── config & scripts hub ─────────────────────────────────────────────────

    /// <summary>Files pinned in the Config and scripts page, as "{packId:N}/{relative/path}". A pin
    /// whose file is missing is shown greyed rather than dropped, so it survives a re-sync or an
    /// unavailable pack.</summary>
    public List<string> ConfigHubPins { get; set; } = new();

    /// <summary>Last filter the Config and scripts page was left on, so it opens where it was left.</summary>
    public string? ConfigHubLastFilter { get; set; }

    /// <summary>Colours for the launcher chrome and log views in the Classic style. Left alone while
    /// Slate is active, so switching back to Classic restores them.</summary>
    public ThemeSettings Theme { get; set; } = new();

    /// <summary>The launcher's look: the Slate style (default) and its options, or Classic.</summary>
    public LookSettings Look { get; set; } = new();

    /// <summary>Text files the built-in editor has been told to keep open across sessions, newest first.</summary>
    public List<string> RecentEditedFiles { get; set; } = new();

    /// <summary>Local, per-device usage stats for ordering and display.</summary>
    // ConcurrentDictionary: play time is recorded on a background thread (process exit) while Save()
    // may be serializing on another. The JSON shape is the same as a plain dictionary.
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, PackUsageStats> PackUsage { get; set; } = new();

    public bool GetAutoApplyRulesFor(Guid packId) =>
        PackAutoApplyRules.TryGetValue(packId, out var v) ? v : true;

    public void SetAutoApplyRulesFor(Guid packId, bool value)
    {
        PackAutoApplyRules[packId] = value;
        Save();
    }

    public bool GetMinecraftWindowPackPageCollapsedByDefault(Guid packId) =>
        PackMinecraftWindowPackPageCollapsedByDefault.TryGetValue(packId, out var v)
            ? v
            : MinecraftWindowPackPageCollapsedByDefault;

    public bool? GetMinecraftWindowPackPageCollapsedOverride(Guid packId) =>
        PackMinecraftWindowPackPageCollapsedByDefault.TryGetValue(packId, out var v) ? v : null;

    public void SetMinecraftWindowPackPageCollapsedOverride(Guid packId, bool? value)
    {
        if (value is null) PackMinecraftWindowPackPageCollapsedByDefault.Remove(packId);
        else PackMinecraftWindowPackPageCollapsedByDefault[packId] = value.Value;
    }

    public bool GetMinecraftWindowBorderlessFullscreenByDefault(Guid packId) =>
        PackMinecraftWindowBorderlessFullscreenByDefault.TryGetValue(packId, out var v)
            ? v
            : MinecraftWindowBorderlessFullscreenByDefault;

    public bool? GetMinecraftWindowBorderlessFullscreenOverride(Guid packId) =>
        PackMinecraftWindowBorderlessFullscreenByDefault.TryGetValue(packId, out var v) ? v : null;

    public void SetMinecraftWindowBorderlessFullscreenOverride(Guid packId, bool? value)
    {
        if (value is null) PackMinecraftWindowBorderlessFullscreenByDefault.Remove(packId);
        else PackMinecraftWindowBorderlessFullscreenByDefault[packId] = value.Value;
    }

    public PackUsageStats GetPackUsage(Guid packId)
    {
        if (!PackUsage.TryGetValue(packId, out var stats))
        {
            stats = new PackUsageStats();
            PackUsage[packId] = stats;
        }
        return stats;
    }

    public void SetPackPinned(Guid packId, bool pinned)
    {
        GetPackUsage(packId).IsPinned = pinned;
    }

    public void RecordPackLaunchStarted(Guid packId)
    {
        var stats = GetPackUsage(packId);
        stats.PlayCount++;
        stats.LastPlayedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public void AddPackPlayTime(Guid packId, TimeSpan duration)
    {
        var seconds = (long)Math.Floor(duration.TotalSeconds);
        if (seconds <= 0) return;
        var stats = GetPackUsage(packId);
        stats.TotalPlayTimeSeconds += seconds;
        Save();
    }

    /// <summary>World metadata, keyed by "{sourcePackId:N}:{folderName}".</summary>
    public Dictionary<string, WorldEntry> Worlds { get; set; } = new();

    /// <summary>User-defined world folders. Key = folder name; value = list of world keys.</summary>
    public Dictionary<string, List<string>> WorldFolders { get; set; } = new();

    /// <summary>Per-folder rules for world folders. Key = the same folder name as
    /// <see cref="WorldFolders"/>; a folder with no entry has no rule.</summary>
    /// <remarks>A separate map because <see cref="WorldFolders"/> values are JSON arrays on disk, and
    /// changing their shape would lose every existing install's folders. Rename and delete go through
    /// <see cref="RenameWorldFolder"/> and <see cref="DeleteWorldFolder"/> so the two maps stay in step.</remarks>
    public Dictionary<string, ContentFolderRule> WorldFolderRules { get; set; } = new();

    /// <summary>Resource pack metadata, keyed by ResourcePackService.Key().</summary>
    public Dictionary<string, ResourcePackEntry> ResourcePacks { get; set; } = new();

    /// <summary>User-defined resource pack folders. Key = folder name; value = list of resource pack keys.</summary>
    public Dictionary<string, List<string>> ResourcePackFolders { get; set; } = new();

    /// <summary>Per-folder rules for resource pack folders, keyed the same way
    /// <see cref="ResourcePackFolders"/> is. A separate map for the reason given on
    /// <see cref="WorldFolderRules"/>.</summary>
    public Dictionary<string, ContentFolderRule> ResourcePackFolderRules { get; set; } = new();

    /// <summary>The drag order for the Resource packs page's "All" and "Defaults" chips, as item
    /// keys, first shown first.</summary>
    /// <remarks>User folders keep their order in <see cref="ResourcePackFolders"/>; "All" isn't a folder,
    /// and a reserved key in that map would show up as a chip. Only used by
    /// <see cref="ResourcePackSortMode.Manual"/>. Keys of deleted packs never match, so this needs no
    /// pruning.</remarks>
    public List<string> ResourcePackOrder { get; set; } = new();

    /// <summary>When true, dragging a row in the Resource packs page's "All" view also reorders every
    /// folder holding that pack, so the folders agree with the new order.</summary>
    /// <remarks>On by default. One drag can rewrite several folders, so it can be turned off to protect
    /// folders arranged by hand.</remarks>
    public bool PropagateResourcePackOrderToFolders { get; set; } = true;

    /// <summary>Shader pack metadata, keyed the way <see cref="ShaderPackService"/> keys a shader:
    /// <c>"{sourcePackId:N}:{fileNameOrFolderName}"</c>.</summary>
    /// <remarks>A shader has no manifest, id or version, so this is where the launcher remembers what a
    /// file is (see <see cref="ShaderPackEntry"/>). Entries with no file on disk are harmless.</remarks>
    public Dictionary<string, ShaderPackEntry> ShaderPacks { get; set; } = new();

    /// <summary>User-defined shader folders. Key = folder name; value = list of shader keys.</summary>
    public Dictionary<string, List<string>> ShaderFolders { get; set; } = new();

    /// <summary>Per-folder rules for shader folders, keyed the same way <see cref="ShaderFolders"/>
    /// is. A separate map for the reason given on <see cref="WorldFolderRules"/>.</summary>
    public Dictionary<string, ContentFolderRule> ShaderFolderRules { get; set; } = new();

    /// <summary>Per-pack last-known shared manifest version (for update detection).
    /// ConcurrentDictionary: written from background sync tasks while Save() may serialize.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, long> PackSyncedVersion { get; set; } = new();

    /// <summary>Default Minecraft options applied as a template for new packs.</summary>
    public McDefaults McDefaults { get; set; } = new();

    /// <summary>When true, secondary pages (settings, worlds, pack detail, etc.) open in a resizable
    /// panel next to the pack list; when false, they cover the main content area.</summary>
    public bool UseSidePanel { get; set; } = true;

    /// <summary>Global UI scale for the whole launcher (1.0 = 100%), applied as a layout zoom.</summary>
    public double LauncherScale { get; set; } = 1.0;

    /// <summary>Extra zoom applied to mod entries in the list/browse views, on top of <see cref="LauncherScale"/>.</summary>
    public double ModListScale { get; set; } = 1.0;

    /// <summary>Sizes the user has dragged an in-window dialog card to, keyed by the card's resize key
    /// (see <c>ResizableCard</c>). A key with no entry means "use the card's own default".</summary>
    /// <remarks>Not validated here: the window can be a different size next launch, so the restore path
    /// clamps against the live window.</remarks>
    public Dictionary<string, DialogSizeEntry> DialogSizes { get; set; } = new();

    /// <summary>Reads a remembered dialog size, or the supplied default when there is none.</summary>
    public (double Width, double Height) GetDialogSize(string key, double defaultWidth, double defaultHeight) =>
        DialogSizes.TryGetValue(key, out var saved) && saved.Width > 0 && saved.Height > 0
            ? (saved.Width, saved.Height)
            : (defaultWidth, defaultHeight);

    /// <summary>Remembers the size the user dragged a dialog card to.</summary>
    public void SetDialogSize(string key, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(key) || width <= 0 || height <= 0) return;
        if (DialogSizes.TryGetValue(key, out var existing)
            && Math.Abs(existing.Width - width) < 0.5 && Math.Abs(existing.Height - height) < 0.5)
            return;   // a click that moved the grip a sub-pixel is not a preference change

        DialogSizes[key] = new DialogSizeEntry { Width = width, Height = height };
        Save();
    }

    /// <summary>Keyboard shortcut used by the Minecraft wrapper window to hide the time bar.</summary>
    public string MinecraftWindowToggleKey { get; set; } = "key.keyboard.f8";

    /// <summary>Keyboard shortcut used by the Minecraft wrapper window to toggle borderless fullscreen.</summary>
    public string MinecraftWindowFullscreenKey { get; set; } = "key.keyboard.f11";

    /// <summary>User-defined pack folders. Key = folder name; value = list of pack IDs.
    /// Team folders aren't stored here; they are computed from team membership at runtime.</summary>
    public Dictionary<string, List<Guid>> PackFolders { get; set; } = new();

    /// <summary>Packs the user removed from their instance list (e.g. unsubscribed public packs).</summary>
    public List<Guid> HiddenPackIds { get; set; } = new();

    public bool IsPackHidden(Guid packId) => HiddenPackIds.Contains(packId);

    public void HidePack(Guid packId)
    {
        if (HiddenPackIds.Contains(packId)) return;
        HiddenPackIds.Add(packId);
        Save();
    }

    public void UnhidePack(Guid packId)
    {
        if (!HiddenPackIds.Remove(packId)) return;
        Save();
    }

    // ── folder helpers ──────────────────────────────────────────────────────

    public void CreatePackFolder(string name)
    {
        var key = (name ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return;
        if (!PackFolders.ContainsKey(key)) PackFolders[key] = new();
    }

    public void DeletePackFolder(string name)
    {
        PackFolders.Remove(name);
    }

    public void AddPackToFolder(string folder, Guid packId)
    {
        if (!PackFolders.TryGetValue(folder, out var list))
        {
            list = new();
            PackFolders[folder] = list;
        }
        if (!list.Contains(packId)) list.Add(packId);
    }

    public void RemovePackFromFolder(string folder, Guid packId)
    {
        if (PackFolders.TryGetValue(folder, out var list)) list.Remove(packId);
    }

    public IReadOnlyList<string> FoldersFor(Guid packId) =>
        PackFolders.Where(kvp => kvp.Value.Contains(packId)).Select(kvp => kvp.Key).ToList();

    public void CreateWorldFolder(string name)
    {
        var key = (name ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return;
        if (!WorldFolders.ContainsKey(key)) WorldFolders[key] = new();
    }

    public void DeleteWorldFolder(string name)
    {
        WorldFolders.Remove(name);
        // Drop the rule too, or it would come back on a new folder with the same name.
        WorldFolderRules.Remove(name);
    }

    /// <summary>Renames a world folder, moving its members and its rule and keeping its position.</summary>
    /// <remarks>Both maps are rebuilt in order, since position is the chip order. Doesn't save; the
    /// caller does.</remarks>
    /// <returns>False when there is no such folder or the new name is already taken, in which case
    /// nothing was changed.</returns>
    public bool RenameWorldFolder(string oldName, string newName) =>
        RenameFolderPair(WorldFolders, WorldFolderRules, oldName, newName);

    /// <summary>The rule on a world folder, or null when it has none.</summary>
    public ContentFolderRule? GetWorldFolderRule(string folder) =>
        WorldFolderRules.TryGetValue(folder, out var rule) ? rule : null;

    /// <summary>Writes a world folder's rule. Doesn't save; the caller does.</summary>
    public void SetWorldFolderRule(string folder, ContentFolderRule? rule) =>
        StoreFolderRule(WorldFolderRules, folder, rule);

    public void AddWorldToFolder(string folder, string worldKey)
    {
        if (!WorldFolders.TryGetValue(folder, out var list))
        {
            list = new();
            WorldFolders[folder] = list;
        }
        if (!list.Contains(worldKey)) list.Add(worldKey);
    }

    public void RemoveWorldFromFolder(string folder, string worldKey)
    {
        if (WorldFolders.TryGetValue(folder, out var list)) list.Remove(worldKey);
    }

    public void CreateResourcePackFolder(string name)
    {
        var key = (name ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return;
        if (!ResourcePackFolders.ContainsKey(key)) ResourcePackFolders[key] = new();
    }

    public void DeleteResourcePackFolder(string name)
    {
        ResourcePackFolders.Remove(name);
        ResourcePackFolderRules.Remove(name);
    }

    /// <inheritdoc cref="RenameWorldFolder"/>
    public bool RenameResourcePackFolder(string oldName, string newName) =>
        RenameFolderPair(ResourcePackFolders, ResourcePackFolderRules, oldName, newName);

    /// <summary>The rule on a resource pack folder, or null when it has none.</summary>
    public ContentFolderRule? GetResourcePackFolderRule(string folder) =>
        ResourcePackFolderRules.TryGetValue(folder, out var rule) ? rule : null;

    /// <inheritdoc cref="SetWorldFolderRule"/>
    public void SetResourcePackFolderRule(string folder, ContentFolderRule? rule) =>
        StoreFolderRule(ResourcePackFolderRules, folder, rule);

    public void AddResourcePackToFolder(string folder, string packKey)
    {
        if (!ResourcePackFolders.TryGetValue(folder, out var list))
        {
            list = new();
            ResourcePackFolders[folder] = list;
        }
        if (!list.Contains(packKey)) list.Add(packKey);
    }

    public void RemoveResourcePackFromFolder(string folder, string packKey)
    {
        if (ResourcePackFolders.TryGetValue(folder, out var list)) list.Remove(packKey);
    }

    /// <summary>Rewrites the order of one resource pack folder's members, which is the order its chip
    /// shows them in.</summary>
    /// <param name="keysInOrder">The members, first shown first. Keys that are not members are
    /// ignored, and members not listed are kept after the listed ones.</param>
    /// <returns>False when there is no such folder, in which case nothing was changed.</returns>
    /// <remarks>Doesn't save; the caller does.</remarks>
    public bool ReorderResourcePackFolder(string folder, IReadOnlyList<string> keysInOrder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (!ResourcePackFolders.TryGetValue(folder, out var members)) return false;
        ApplyKeyOrder(members, keysInOrder, membersOnly: true);
        return true;
    }

    /// <summary>Rewrites <see cref="ResourcePackOrder"/>, the page-wide order behind the "All" and
    /// "Defaults" chips.</summary>
    /// <param name="keysInOrder">The rows, first shown first. Any key is accepted, since "All" has no
    /// membership to check against.</param>
    /// <remarks>Doesn't save; the caller does.</remarks>
    public void ReorderResourcePacks(IReadOnlyList<string> keysInOrder) =>
        ApplyKeyOrder(ResourcePackOrder, keysInOrder, membersOnly: false);

    /// <summary>Puts <paramref name="wanted"/> at the front of <paramref name="stored"/>, keeping
    /// everything it did not mention.</summary>
    /// <remarks>Unmentioned keys are appended in their existing order, never dropped: the caller's list
    /// may be filtered, and a hidden member must not fall out of the folder.</remarks>
    private static void ApplyKeyOrder(
        List<string> stored, IReadOnlyList<string>? wanted, bool membersOnly)
    {
        if (wanted is null || wanted.Count == 0) return;

        var known = membersOnly ? new HashSet<string>(stored, StringComparer.Ordinal) : null;
        var placed = new List<string>(stored.Count + wanted.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in wanted)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (known is not null && !known.Contains(key)) continue;               if (seen.Add(key)) placed.Add(key);
        }
        foreach (var key in stored)
            if (seen.Add(key)) placed.Add(key);

        stored.Clear();
        stored.AddRange(placed);
    }

    public void CreateShaderFolder(string name)
    {
        var key = (name ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return;
        if (!ShaderFolders.ContainsKey(key)) ShaderFolders[key] = new();
    }

    public void DeleteShaderFolder(string name)
    {
        ShaderFolders.Remove(name);
        ShaderFolderRules.Remove(name);
    }

    /// <inheritdoc cref="RenameWorldFolder"/>
    public bool RenameShaderFolder(string oldName, string newName) =>
        RenameFolderPair(ShaderFolders, ShaderFolderRules, oldName, newName);

    /// <summary>The rule on a shader folder, or null when it has none.</summary>
    public ContentFolderRule? GetShaderFolderRule(string folder) =>
        ShaderFolderRules.TryGetValue(folder, out var rule) ? rule : null;

    /// <inheritdoc cref="SetWorldFolderRule"/>
    public void SetShaderFolderRule(string folder, ContentFolderRule? rule) =>
        StoreFolderRule(ShaderFolderRules, folder, rule);

    public void AddShaderToFolder(string folder, string shaderKey)
    {
        if (!ShaderFolders.TryGetValue(folder, out var list))
        {
            list = new();
            ShaderFolders[folder] = list;
        }
        if (!list.Contains(shaderKey)) list.Add(shaderKey);
    }

    public void RemoveShaderFromFolder(string folder, string shaderKey)
    {
        if (ShaderFolders.TryGetValue(folder, out var list)) list.Remove(shaderKey);
    }

    /// <summary>Stores a folder rule, or drops the entry when there is nothing worth storing.</summary>
    /// <remarks>An empty, disabled rule is removed so settings.json doesn't collect an entry for every
    /// folder whose dialog was opened. A disabled rule that still has text is kept, so re-enabling it
    /// doesn't mean retyping it.</remarks>
    private static void StoreFolderRule(
        Dictionary<string, ContentFolderRule> rules, string folder, ContentFolderRule? rule)
    {
        var key = (folder ?? "").Trim();
        if (key.Length == 0) return;
        if (rule is null || (!rule.Enabled && !rule.HasAnyRule)) { rules.Remove(key); return; }
        rules[key] = rule;
    }

    /// <summary>Renames one entry in a members map and its sibling rules map together, keeping both in
    /// order.</summary>
    /// <remarks>Both maps are assigned only after both rebuilds succeed, so a refused rename changes
    /// neither. A folder without a rule is normal.</remarks>
    private static bool RenameFolderPair(
        Dictionary<string, List<string>> members, Dictionary<string, ContentFolderRule> rules,
        string oldName, string newName)
    {
        var fresh = (newName ?? "").Trim();
        if (fresh.Length == 0 || !members.ContainsKey(oldName)) return false;
        if (string.Equals(oldName, fresh, StringComparison.Ordinal)) return true;
        if (members.ContainsKey(fresh)) return false;

        var rebuiltMembers = new Dictionary<string, List<string>>(members.Count);
        foreach (var (key, value) in members)
            rebuiltMembers[string.Equals(key, oldName, StringComparison.Ordinal) ? fresh : key] = value;

        var rebuiltRules = new Dictionary<string, ContentFolderRule>(rules.Count);
        foreach (var (key, value) in rules)
        {
            // A rule already under the new name can't belong to a real folder (the members map refused that
            // name), so the moved rule replaces it.
            if (string.Equals(key, fresh, StringComparison.Ordinal)) continue;
            rebuiltRules[string.Equals(key, oldName, StringComparison.Ordinal) ? fresh : key] = value;
        }

        members.Clear();
        foreach (var (key, value) in rebuiltMembers) members[key] = value;
        rules.Clear();
        foreach (var (key, value) in rebuiltRules) rules[key] = value;
        return true;
    }

    // ── shader entries ──────────────────────────────────────────────────────

    /// <summary>What is known about an installed shader, or null if nothing was recorded (normal for one
    /// dropped into the folder by hand).</summary>
    public ShaderPackEntry? GetShaderPack(string key) =>
        ShaderPacks.TryGetValue(key, out var e) ? e : null;

    /// <summary>The entry for a shader, created empty if this is the first thing written about it.
    /// Mirrors <c>ResourcePackService.GetOrCreate</c>; the caller saves.</summary>
    public ShaderPackEntry GetOrCreateShaderPack(string key)
    {
        if (!ShaderPacks.TryGetValue(key, out var e))
        {
            e = new ShaderPackEntry();
            ShaderPacks[key] = e;
        }
        return e;
    }

    /// <summary>Records where a shader came from, so a later store visit can recognise the file as this
    /// project at this version and offer the update.</summary>
    /// <remarks>Recorded at install time because nothing in a shader zip identifies it, and matching by
    /// name can pick a different author's shader. A null <paramref name="source"/> clears it, for a file
    /// replaced by hand.</remarks>
    public void SetShaderPackProvenance(
        string key, ModSource? source, string? projectId, string? versionId, string? versionNumber)
    {
        var e = GetOrCreateShaderPack(key);
        e.Source = source;
        e.ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        e.VersionId = string.IsNullOrWhiteSpace(versionId) ? null : versionId.Trim();
        e.VersionNumber = string.IsNullOrWhiteSpace(versionNumber) ? null : versionNumber.Trim();
        Save();
    }

    /// <summary>Forgets a shader: its entry and its membership of every shader folder. Called when the
    /// file is deleted, so a folder cannot keep listing a shader that is gone.</summary>
    public void RemoveShaderPack(string key)
    {
        ShaderPacks.Remove(key);
        foreach (var list in ShaderFolders.Values) list.Remove(key);
    }

    public int GetMaxRamFor(Guid packId) =>
        PackMaxRamMb.TryGetValue(packId, out var v) ? v : RecommendedRamMb();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    private static int _recommendedRamMb;

    /// <summary>How much RAM to give a pack with no value set, based on the machine's installed
    /// memory.</summary>
    /// <remarks>
    /// 8 GB gets 6 GB, 16 GB gets 10 GB, 32 GB or more gets 16 GB (Minecraft gains little past ~16 GB);
    /// under 8 GB it takes half. Uses GetPhysicallyInstalledSystemMemory because GlobalMemoryStatusEx
    /// leaves out hardware-reserved memory, so an 8 GB machine would read as ~7.9 GB. Falls back to
    /// 4 GB if the call fails.
    /// </remarks>
    public static int RecommendedRamMb()
    {
        if (_recommendedRamMb != 0) return _recommendedRamMb;
        int chosen;
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0)
            {
                long installedMb = kb / 1024;
                chosen = installedMb switch
                {
                    >= 32L * 1024 => 16384,
                    >= 16L * 1024 => 10240,
                    >= 8L * 1024 => 6144,
                    _ => (int)Math.Max(2048, Math.Min(4096, installedMb / 2)),
                };
            }
            else chosen = 4096;
        }
        catch { chosen = 4096; }
        _recommendedRamMb = chosen;
        return chosen;
    }

    public void SetMaxRamFor(Guid packId, int? mb)
    {
        if (mb is null) PackMaxRamMb.Remove(packId);
        else PackMaxRamMb[packId] = mb.Value;
    }

    public string GetJvmArgsFor(Guid packId) =>
        PackJvmArgs.TryGetValue(packId, out var v) ? v : "";

    public void SetJvmArgsFor(Guid packId, string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) PackJvmArgs.Remove(packId);
        else PackJvmArgs[packId] = args.Trim();
    }

    // ── dedicated server accessors ───────────────────────────────────────────

    /// <summary>How much RAM this pack's server gets: its own value, else the client's, else the
    /// machine-derived default.</summary>
    public int GetServerMaxRamFor(Guid packId) =>
        PackServerMaxRamMb.TryGetValue(packId, out var v) ? v : GetMaxRamFor(packId);

    /// <summary>True when this pack's server RAM is set explicitly rather than inherited from the
    /// client.</summary>
    public bool HasServerMaxRamOverride(Guid packId) => PackServerMaxRamMb.ContainsKey(packId);

    public void SetServerMaxRamFor(Guid packId, int? mb)
    {
        if (mb is null) PackServerMaxRamMb.Remove(packId);
        else PackServerMaxRamMb[packId] = mb.Value;
    }

    public string GetServerJvmArgsFor(Guid packId) =>
        PackServerJvmArgs.TryGetValue(packId, out var v) ? v : "";

    public void SetServerJvmArgsFor(Guid packId, string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) PackServerJvmArgs.Remove(packId);
        else PackServerJvmArgs[packId] = args.Trim();
    }

    public bool GetServerEulaAccepted(Guid packId) =>
        PackServerEulaAccepted.TryGetValue(packId, out var v) && v;

    public void SetServerEulaAccepted(Guid packId, bool accepted)
    {
        if (accepted) PackServerEulaAccepted[packId] = true;
        else PackServerEulaAccepted.Remove(packId);
    }

    public bool GetServerAutoRestart(Guid packId) =>
        PackServerAutoRestart.TryGetValue(packId, out var v) && v;

    public void SetServerAutoRestart(Guid packId, bool enabled)
    {
        if (enabled) PackServerAutoRestart[packId] = true;
        else PackServerAutoRestart.Remove(packId);
    }

    /// <summary>Shared Minecraft runtime (libraries, assets, version JARs), stored once globally and
    /// reused by all packs.</summary>
    public static string RuntimeRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudLauncher", "runtime");

    /// <summary>Managed Java runtimes downloaded by the launcher when the system Java is incompatible.</summary>
    public static string JavaRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudLauncher", "java");

    // CL_PROFILE lets a second instance run with a completely separate data folder.
    // e.g.  set CL_PROFILE=user2  then launch the exe
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudLauncher",
        Environment.GetEnvironmentVariable("CL_PROFILE") is { Length: > 0 } p ? p : "default");

    /// <summary>The per-profile data folder (settings, caches). Public so other small stores can sit
    /// beside settings.json rather than inventing their own location.</summary>
    public static string DataRootPath => DataRoot;

    private static readonly string SettingsPath = Path.Combine(DataRoot, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new();
                // Secrets first: each step below may save, and a save must not run before the
                // secret store has been read.
                var plainTextSecrets = loaded.LoadSecrets();
                loaded.MigrateServerUrl();
                loaded.ApplyCustomGameWindowDefault();
                loaded.ApplyLookAccentDefault();
                loaded.LoadCurseForgeApiKey();
                if (plainTextSecrets)
                {
                    try { loaded.Save(); }
                    catch { /* read-only profile: moved again on the next start */ }
                }
                return loaded;
            }
        }
        catch { /* corrupt: use defaults */ }
        // A fresh profile already has the current default accent. Without this, terracotta picked on
        // the first day would be moved on the next start.
        var fresh = new AppSettings { LookAccentDefaultApplied = true };
        // The secrets have their own files, so they outlive a settings.json that was missing or unreadable.
        fresh.LoadSecrets();
        fresh.LoadCurseForgeApiKey();
        return fresh;
    }

    private static readonly object SaveLock = new();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        // Save() runs on several threads (process-exit handlers, background sync). Write under a lock to
        // a temp file and rename it into place, so writers can't collide and a half-written file can't
        // make Load() fall back to defaults and lose the tokens and preferences.
        lock (SaveLock)
        {
            PersistRconPasswords();

            // Collections in this graph can be changed on other threads mid-serialize ("collection was
            // modified"). Retry a couple of times rather than crash a process-exit handler.
            string? json = null;
            for (var attempt = 0; attempt < 3 && json is null; attempt++)
            {
                try { json = JsonSerializer.Serialize(this, JsonOpts); }
                catch (InvalidOperationException) when (attempt < 2) { /* concurrent mutation, retry */ }
            }
            if (json is null) return; // gave up rather than crash; a later Save() will persist state

            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
    }

    /// <summary>Moves a settings file still using the old <c>http://IP:port</c> endpoint onto the HTTPS
    /// name, so the bearer token stops going out in clear text. A ServerUrl the user set themselves is
    /// left alone.</summary>
    public void MigrateServerUrl()
    {
        var current = (ServerUrl ?? "").Trim();
        if (current.Length == 0) current = DefaultServerUrl;
        var isDefault = string.Equals(current.TrimEnd('/'), DefaultServerUrl, StringComparison.OrdinalIgnoreCase);
        var isLegacy = LegacyServerUrls.Any(u => string.Equals(u.TrimEnd('/'), current.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (!isDefault && !isLegacy)
        {
            if (ServerUrl != current) ServerUrl = current;
            return;
        }

        // The new name needs the domain's nameserver change to have reached this PC's resolver. Until
        // it has, the old address keeps working, so a launcher stays (or lands) there and tries again
        // next start.
        var newHostKnown = HostResolves(new Uri(DefaultServerUrl).Host);
        var target = newHostKnown ? DefaultServerUrl : FallbackServerUrl;
        if (string.Equals(current.TrimEnd('/'), target, StringComparison.OrdinalIgnoreCase)) { ServerUrl = target; return; }
        ServerUrl = target;
        try { Save(); } catch { /* read-only profile: the in-memory value still points at the right place */ }
    }

    /// <summary>True when the name resolves within a short wait; a slow or offline resolver counts as no.</summary>
    private static bool HostResolves(string host)
    {
        try
        {
            var lookup = System.Net.Dns.GetHostAddressesAsync(host);
            return lookup.Wait(TimeSpan.FromSeconds(2)) && lookup.Result.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);

}

/// <summary>List view sort columns, in the order the Sort box lists them.</summary>
public enum ModListSortMode
{
    Priority = 0,
    Name = 1,
    AddDate = 2,
    UpdateDate = 3,
    Status = 4,
    ContentSize = 5,
    Category = 6
}

/// <summary>List view store filter, in the order the filter box lists them.</summary>
public enum ModListSourceFilter
{
    All = 0,
    CurseForge = 1,
    Modrinth = 2,
    External = 3
}

public enum PackSortMode
{
    LastPlayed = 0,
    Version = 1,
    PlayCount = 2,
    TimePlayed = 3
}

public enum PackListLayout
{
    Cards = 0,
    List = 1
}

public enum WorldSortMode
{
    Modified = 0,
    Name = 1,
    Size = 2,
    Pack = 3
}

/// <summary>Resource pack list sort columns, in the order the Sort menu lists them.</summary>
/// <remarks>Numbered explicitly: the numbers are stored in settings.json, so inserting a member
/// without one would change how existing installs sort.</remarks>
public enum ResourcePackSortMode
{
    Modified = 0,
    Name = 1,
    Size = 2,
    Pack = 3,

    /// <summary>The order the user dragged the rows into: <see cref="AppSettings.ResourcePackOrder"/>
    /// for the "All" and "Defaults" chips, and the folder's own member order inside a folder.</summary>
    /// <remarks>A drag switches to this mode and persists it; otherwise the next search keystroke would
    /// re-sort and undo the drag.</remarks>
    Manual = 4
}

/// <summary>Shader list sort columns, in the order the Sort box lists them.</summary>
/// <remarks>Numbered explicitly: the numbers are stored in settings.json, so inserting a member
/// without one would change how existing installs sort.</remarks>
public enum ShaderSortMode
{
    /// <summary>Alphabetical by display name.</summary>
    Name = 0,
    /// <summary>Grouped by the instance the shader is installed in.</summary>
    Instance = 1,
    /// <summary>Newest file first (the default; see <see cref="AppSettings.ShaderSortMode"/>).</summary>
    RecentlyAdded = 2,
    /// <summary>Largest first. A shader folder is measured whole, not just its zip.</summary>
    Size = 3
}

public sealed class PackUsageStats
{
    public bool IsPinned { get; set; }
    public int PlayCount { get; set; }
    public long TotalPlayTimeSeconds { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }
}

/// <summary>One in-window dialog card's remembered size, in device-independent pixels.</summary>
/// <remarks>A class rather than a record struct so an older settings.json without
/// <c>DialogSizes</c> deserialises to an empty dictionary instead of zero sizes.</remarks>
public sealed class DialogSizeEntry
{
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>The user's colours. A null field means "derive it", so an accent and a surface are
/// enough for a whole palette (see <see cref="ThemeService"/>). Stored as <c>#RRGGBB</c> strings so a
/// bad value falls back to the default instead of breaking the launcher.</summary>
public sealed class ThemeSettings
{
    /// <summary>Which preset the colours came from, for showing the right entry in the picker.
    /// Cleared as soon as a colour is changed by hand.</summary>
    public string? PresetName { get; set; }

    /// <summary>Primary action colour: Play, focus rings, the rail indicator, links.</summary>
    public string? Accent { get; set; }

    /// <summary>The deepest background. Its brightness decides whether the theme is light or dark.</summary>
    public string? Surface { get; set; }

    public string? Danger { get; set; }
    public string? Success { get; set; }
    public string? Warning { get; set; }

    // Log view. Null means "derive from the surface/accent above".
    public string? LogBackground { get; set; }
    public string? LogText { get; set; }
    public string? LogMuted { get; set; }
    public string? LogAccent { get; set; }
    public string? LogWarning { get; set; }
    public string? LogError { get; set; }

    /// <summary>True when nothing has been customised, so the picker can show "Default".</summary>
    public bool IsDefault =>
        Accent is null && Surface is null && Danger is null && Success is null && Warning is null
        && LogBackground is null && LogText is null && LogMuted is null && LogAccent is null
        && LogWarning is null && LogError is null;

    public ThemeSettings Clone() => (ThemeSettings)MemberwiseClone();
}

/// <summary>The launcher's style: <see cref="Style"/> picks Slate (the look of the Slate Minecraft
/// mods) or Classic. The other fields customise Slate the way the mod's Interface page does.</summary>
/// <remarks>A settings.json without <c>Look</c> gets these defaults and keeps
/// <see cref="AppSettings.Theme"/>, so choosing Classic restores the old colours. The two choices are
/// strings rather than enums, so an unknown value falls back to the default instead of failing the
/// whole file (which would sign the user out).</remarks>
public sealed class LookSettings
{
    public const string SlateStyle = "Slate", ClassicStyle = "Classic";
    public const string DarkSkin = "Dark", VanillaSkin = "Vanilla";
    public const string PixeloidFont = "Pixeloid Sans", MonocraftFont = "Monocraft", PixelifyFont = "Pixelify Sans";
    /// <summary>Rust, the launcher's own accent (the website uses it too). The mod's terracotta is still
    /// a preset.</summary>
    public const string DefaultAccent = "#601B00";
    public const int MaxRadius = 4;

    /// <summary><c>Slate</c> (default) or <c>Classic</c>.</summary>
    public string Style { get; set; } = SlateStyle;

    /// <summary><c>Dark</c> (default) or <c>Vanilla</c>, the mod's two skins.</summary>
    public string Skin { get; set; } = DarkSkin;

    /// <summary>The one accent colour, <c>#RRGGBB</c>. Rust by default (<see cref="DefaultAccent"/>).</summary>
    public string Accent { get; set; } = DefaultAccent;

    /// <summary>Animation speed: 0 turns motion off, 1 is normal, 2 is half speed.</summary>
    public double Motion { get; set; } = 1.0;

    /// <summary>Corner radius in pixel steps, 0 (square) to <see cref="MaxRadius"/>.</summary>
    public int Radius { get; set; } = 3;

    /// <summary>The pixel font for titles and headings.</summary>
    public bool PixelHeadings { get; set; } = true;

    /// <summary>The pixel font for all text, not just headings.</summary>
    public bool PixelText { get; set; }

    /// <summary>Which pixel font: <see cref="PixeloidFont"/> (default), <see cref="MonocraftFont"/> or
    /// <see cref="PixelifyFont"/>. Anything else falls back to the default.</summary>
    public string PixelFont { get; set; } = PixeloidFont;

    /// <summary>The Slate pixel icon set instead of the Windows icons.</summary>
    public bool PixelIcons { get; set; } = true;

    /// <summary>Hard two-pixel shadows under buttons, cards and menus.</summary>
    public bool Shadows { get; set; } = true;

    /// <summary>Pages and tabs slide in; off makes them appear at once.</summary>
    public bool Transitions { get; set; } = true;

    /// <summary>A soft click when a button is pressed.</summary>
    public bool UiSounds { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSlate => !string.Equals(Style, ClassicStyle, StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVanilla => string.Equals(Skin, VanillaSkin, StringComparison.OrdinalIgnoreCase);

    public LookSettings Clone() => (LookSettings)MemberwiseClone();
}

/// <summary>Per-world preferences stored locally. A world lives in one pack's game/saves folder,
/// but the user can choose which other packs may launch it.</summary>
public sealed class WorldEntry
{
    /// <summary>Human-friendly name. Defaults to the folder name; can be customised.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Short local/server summary for the world.</summary>
    public string? Summary { get; set; }

    /// <summary>Longer local/server description for the world.</summary>
    public string? Description { get; set; }

    /// <summary>Default visibility used when this local world is shared to the server.</summary>
    public PackVisibility Visibility { get; set; } = PackVisibility.Private;

    /// <summary>Server-hosted world id once this save has been shared.</summary>
    public Guid? SharedWorldId { get; set; }

    /// <summary>Whether hosted-world sharing controls are enabled for this local world. Null means legacy settings.</summary>
    public bool? SharingEnabled { get; set; }

    /// <summary>True = available to all packs. False = only the explicit list below.</summary>
    public bool CompatibleWithAll { get; set; } = false;

    /// <summary>Packs allowed to use this world when CompatibleWithAll is false.
    /// Always implicitly includes the source pack.</summary>
    public List<Guid> CompatiblePackIds { get; set; } = new();
}

/// <summary>
/// Per-resource-pack preferences stored locally.
/// </summary>
public sealed class ResourcePackEntry
{
    public string DisplayName { get; set; } = "";
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public PackVisibility Visibility { get; set; } = PackVisibility.Private;
    public Guid? HostedResourcePackId { get; set; }
    public bool? SharingEnabled { get; set; }
    public bool CompatibleWithAll { get; set; } = false;
    public List<Guid> CompatiblePackIds { get; set; } = new();

    // ── store provenance ────────────────────────────────────────────────────
    // Where this zip came from, recorded at install time. A resource pack has no id of its own, and
    // matching by name could offer a different author's pack as an update. All null for a pack added
    // by hand, which just means no update check.

    /// <summary>The store this pack was installed from, or null if it did not come from one.</summary>
    public ModSource? Source { get; set; }

    /// <summary>Project id on <see cref="Source"/> (Modrinth id/slug, CurseForge mod id).</summary>
    public string? ProjectId { get; set; }

    /// <summary>Id of the exact file that was installed, which is what an update compares against.</summary>
    public string? VersionId { get; set; }

    /// <summary>Human-readable version of the installed file ("v1.4.2"). Display only: authors can put
    /// anything here, so updates never compare it.</summary>
    public string? VersionNumber { get; set; }
}

/// <summary>Per-shader preferences stored locally, keyed by <see cref="AppSettings.ShaderPacks"/>'s
/// key.</summary>
/// <remarks>Smaller than <see cref="ResourcePackEntry"/>: shaders aren't shared through the
/// launcher, so there is no visibility, hosted id or compatibility list.</remarks>
public sealed class ShaderPackEntry
{
    /// <summary>Name to show instead of the file name. Empty means "use the file name".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The store this shader was installed from, or null if it did not come from one (added
    /// by hand, or installed before this was recorded).</summary>
    public ModSource? Source { get; set; }

    /// <summary>Project id on <see cref="Source"/> (Modrinth id/slug, CurseForge mod id).</summary>
    public string? ProjectId { get; set; }

    /// <summary>Id of the exact file that was installed, which is what an update compares against.</summary>
    public string? VersionId { get; set; }

    /// <summary>Human-readable version of the installed file ("v1.4.2"). Display only: authors can put
    /// anything here, so updates never compare it.</summary>
    public string? VersionNumber { get; set; }

    /// <summary>True once enough is known to look this shader up in the store it came from.</summary>
    [JsonIgnore]
    public bool HasProvenance => Source is not null && !string.IsNullOrWhiteSpace(ProjectId);
}

/// <summary>User-editable defaults that map onto Minecraft's <c>options.txt</c>, stored centrally
/// so the launcher can stamp them into new packs.</summary>
public sealed class McDefaults
{
    // Video
    public int    Fov               { get; set; } = 70;     // 30-110
    public int    RenderDistance    { get; set; } = 12;     // 2-32
    public int    SimulationDistance{ get; set; } = 10;     // 5-32
    public double Brightness        { get; set; } = 0.5;    // 0.0-1.0
    public int    GuiScale          { get; set; } = 0;      // 0 = Auto, 1-4
    public bool   Fullscreen        { get; set; } = false;
    public bool   VSync             { get; set; } = true;
    public bool   ViewBobbing       { get; set; } = true;
    public bool   AutoJump          { get; set; } = false;

    // Audio (0.0-1.0)
    public double MasterVolume      { get; set; } = 1.0;
    public double MusicVolume       { get; set; } = 1.0;
    public double SoundFxVolume     { get; set; } = 1.0;

    // Common keybinds, as Minecraft key.* identifiers
    public string KeyForward   { get; set; } = "key.keyboard.w";
    public string KeyBack      { get; set; } = "key.keyboard.s";
    public string KeyLeft      { get; set; } = "key.keyboard.a";
    public string KeyRight     { get; set; } = "key.keyboard.d";
    public string KeyJump      { get; set; } = "key.keyboard.space";
    public string KeySneak     { get; set; } = "key.keyboard.left.shift";
    public string KeySprint    { get; set; } = "key.keyboard.left.control";
    public string KeyInventory { get; set; } = "key.keyboard.e";
    public string KeyChat      { get; set; } = "key.keyboard.t";
    public string KeyDrop      { get; set; } = "key.keyboard.q";
    public string KeyAttack    { get; set; } = "key.mouse.left";
    public string KeyUse       { get; set; } = "key.mouse.right";
    public string KeyTogglePerspective { get; set; } = "key.keyboard.f5";
}

/// <summary>What the launcher needs to talk to a server's console, plus how the user labelled it.</summary>
/// <remarks>RCON authenticates with one shared password over an unencrypted socket. The password is
/// kept in <see cref="SecretStore"/>, not settings.json, and only ever sent to its own server.</remarks>
public sealed class ServerAdminEntry
{
    /// <summary>What to call this server in the launcher. Empty falls back to the servers.dat name.</summary>
    public string? Label { get; set; }

    /// <summary>RCON host. Empty means "the same host as the game address".</summary>
    public string? RconHost { get; set; }

    /// <summary>RCON port. Minecraft's default is 25575.</summary>
    public int RconPort { get; set; } = 25575;

    /// <summary>RCON password, as configured in the server's server.properties.</summary>
    /// <remarks>Kept in <see cref="SecretStore"/>: <see cref="AppSettings"/> reads it from there on load
    /// and writes it there on save.</remarks>
    [JsonIgnore]
    public string? RconPassword { get; set; }

    /// <summary>The settings.json slot the password was kept in, in plain text, by older builds. Read
    /// so an existing password moves into the secret store; written empty unless that store cannot be
    /// written on this PC.</summary>
    [JsonPropertyName("RconPassword")]
    public string? LegacyRconPassword
    {
        get => PasswordInSettingsFile ? RconPassword : null;
        set => RconPassword = value;
    }

    /// <summary>Set by <see cref="AppSettings"/> while the secret store cannot be written.</summary>
    [JsonIgnore]
    internal bool PasswordInSettingsFile { get; set; }

    /// <summary>Commands the user has run against this server, newest last, capped by the console UI.</summary>
    public List<string> CommandHistory { get; set; } = new();

    [JsonIgnore]
    public bool HasConsole => !string.IsNullOrWhiteSpace(RconPassword);
}
