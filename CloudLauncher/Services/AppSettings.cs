using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed class AppSettings
{
    /// <summary>
    /// Where the launcher talks to its server. The default moved to the HTTPS name in 1.3.0;
    /// <see cref="MigrateServerUrl"/> carries settings written by older builds over to it.
    /// </summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>The address every new install uses: a real name over TLS, not an IP and a port.</summary>
    public const string DefaultServerUrl = "https://launcher.crispythedev.duckdns.org";

    /// <summary>Addresses earlier builds shipped with. A settings file still pointing at one of these
    /// is moved to <see cref="DefaultServerUrl"/> on load — the plain-HTTP endpoint keeps working, but
    /// nobody should still be sending their tokens over it.</summary>
    private static readonly string[] LegacyServerUrls =
    [
        "http://130.61.131.193:5000",
        "https://130.61.131.193:5000",
        "http://130.61.131.193:5000/",
    ];
    public string? RefreshToken { get; set; }
    public string? AccessToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public string? Username { get; set; }
    public Guid? UserId { get; set; }
    public string PacksRoot { get; set; } = Path.Combine(DataRoot, "packs");

    /// <summary>Default max RAM (MB) used when launching a pack that has no override.</summary>
    public int DefaultMaxRamMb { get; set; } = 4096;

    /// <summary>Per-pack max RAM override (MB). Empty/missing means "use default".</summary>
    public Dictionary<Guid, int> PackMaxRamMb { get; set; } = new();

    /// <summary>
    /// Per-pack extra JVM arguments, appended after CmlLib's stock GC flags. Whitespace-separated,
    /// e.g. "-Dvoxy.geometryBufferSizeOverrideMB=1280". Missing entry = none.
    /// </summary>
    public Dictionary<Guid, string> PackJvmArgs { get; set; } = new();

    /// <summary>Per-pack auto-update preference. True = always update before launch.</summary>
    public Dictionary<Guid, bool> PackAutoUpdate { get; set; } = new();

    /// <summary>
    /// Java executable used for every pack that has no override of its own. Empty/missing means
    /// automatic: the launcher picks (and if needed downloads) the Java major the pack's Minecraft
    /// version wants.
    /// </summary>
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

    /// <summary>
    /// Extra game/-relative paths that never leave this machine on upload and are never touched by a
    /// download, on top of <see cref="PrivateAssetPolicy.BuiltIn"/>. Rule syntax ("config/foo/" = whole
    /// folder). Applies to every pack: the point is that a private asset stays private no matter which
    /// pack it is copied into.
    /// </summary>
    /// <remarks>Edited by hand in settings.json until now; it is surfaced in Settings, so the list a
    /// user sees there is this one and nothing else filters it. Keep the stored strings verbatim —
    /// a pattern rewritten on load would silently stop matching the path someone typed.</remarks>
    public List<string> PrivatePathPatterns { get; set; } = new();

    /// <summary>
    /// Whether "auto-update before launch" is on for a pack the user has never toggled it for.
    /// </summary>
    /// <remarks>
    /// A shared pack you do NOT own is maintained by someone else, so tracking their updates is what
    /// you actually want: someone who downloads a modpack should get its fixes without having to know
    /// a setting exists. A pack you DO own defaults off - you are the source of truth for it, and
    /// pulling the server copy over your working directory before every launch would overwrite local
    /// changes you have not uploaded yet. Once the checkbox is touched the stored value always wins.
    /// </remarks>
    public bool GetAutoUpdateFor(Guid packId, bool isShared, Guid ownerId) =>
        PackAutoUpdate.TryGetValue(packId, out var stored) ? stored : (isShared && ownerId != UserId);

    /// <summary>Per-pack auto-apply-rules preference. Missing entry = ON by default.</summary>
    public Dictionary<Guid, bool> PackAutoApplyRules { get; set; } = new();

    /// <summary>
    /// Per-pack "Low mode": turn the heavy visual settings down before launch so the pack runs on a modest machine.
    /// Missing entry = ON by default, because a first-time player is exactly the one who cannot afford the full
    /// settings and has no idea which of four hundred mods is costing them the frames.
    /// </summary>
    public Dictionary<Guid, bool> PackLowMode { get; set; } = new();

    /// <summary>
    /// When true, launcher-started games are embedded in CloudLauncher's custom host
    /// window (play-time bar, instance side-panel, collapse/fullscreen hotkeys). When
    /// false, Minecraft opens in its own native window and the launcher just tracks the
    /// process.
    /// </summary>
    /// <remarks>Off by default since 1.1.10: the embedded host window is the launcher's own feature
    /// and a first-time player is better served by Minecraft behaving exactly as it does everywhere
    /// else. Anyone who wants the play-time bar and the side panel turns it back on in Settings; the
    /// stored value of anyone who already had it on is untouched, because this default only applies
    /// when the key is absent from settings.json.</remarks>
    public bool UseCustomGameWindow { get; set; } = false;

    /// <summary>Set once <see cref="ApplyCustomGameWindowDefault"/> has run for this profile.</summary>
    public bool CustomGameWindowDefaultApplied { get; set; }

    /// <summary>
    /// Turns the custom game window off once, for profiles written before it stopped being the
    /// default.
    /// </summary>
    /// <remarks>
    /// The default flipped to off in 1.1.10, but a default only applies when the key is absent — a
    /// settings.json from an earlier build carries <c>true</c> forever, so those installs kept
    /// hosting Minecraft in the launcher's window long after new ones stopped. This clears that
    /// once. The flag means it never runs again, so turning it back on in Settings sticks.
    /// </remarks>
    public void ApplyCustomGameWindowDefault()
    {
        if (CustomGameWindowDefaultApplied) return;
        CustomGameWindowDefaultApplied = true;
        UseCustomGameWindow = false;
        try { Save(); } catch { /* read-only profile: the in-memory value is still right */ }
    }

    /// <summary>
    /// Id of the pack every new install starts subscribed to, and whether that has been done.
    /// </summary>
    /// <remarks>Seeded once, not enforced: the flag is set even when the subscribe fails, and
    /// leaving the pack afterwards must stick rather than being undone on the next launch.</remarks>
    public static readonly Guid DefaultPackId = Guid.Parse("9dc74fcc-8baf-4a33-8eec-8e985ca309f3");

    public bool DefaultPackSeeded { get; set; }

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

    /// <summary>World list sort preference.</summary>
    public WorldSortMode WorldSortMode { get; set; } = WorldSortMode.Modified;

    public ResourcePackSortMode ResourcePackSortMode { get; set; } = ResourcePackSortMode.Modified;

    /// <summary>Shader list sort preference.</summary>
    /// <remarks>Newest first by default, which is what <see cref="ShaderPackService.ScanAll"/> already
    /// hands back: someone who has just dropped a shader in wants to see it without hunting, and the
    /// alphabetical order a shader folder happens to be in means nothing to anyone. The stored value
    /// wins once the Sort box is touched, and an absent key (an older settings.json) lands here rather
    /// than on the enum's zero member.</remarks>
    public ShaderSortMode ShaderSortMode { get; set; } = ShaderSortMode.RecentlyAdded;

    // ── mod list view (a pack's Mods Management → List view) ──────────────────

    /// <summary>Which column the List view sorts by. Sticky across sessions.</summary>
    public ModListSortMode ModListSortMode { get; set; } = ModListSortMode.Priority;

    /// <summary>True when the List view sort runs against its natural direction.</summary>
    public bool ModListSortReversed { get; set; }

    /// <summary>List view "Hide disabled" filter. Sticky across sessions.</summary>
    public bool ModListHideDisabled { get; set; }

    /// <summary>List view store filter. Sticky across sessions. ("Only updates" deliberately is not:
    /// it is a triage filter, and reopening the launcher to an apparently empty pack is alarming.)</summary>
    public ModListSourceFilter ModListSourceFilter { get; set; } = ModListSourceFilter.All;

    /// <summary>
    /// How wide a List view row's name/version block may get before the row's buttons start, in
    /// device-independent pixels. The row itself still spans the list; capping this is what keeps the
    /// Options button a short hop from the mod's name on a wide monitor instead of a screen away.
    /// </summary>
    public double ModRowContentWidth { get; set; } = 620;

    /// <summary>Bounds for <see cref="ModRowContentWidth"/>, and the value a stored nonsense clamps to.</summary>
    public const double MinModRowContentWidth = 360, MaxModRowContentWidth = 2000;

    public double EffectiveModRowContentWidth =>
        double.IsFinite(ModRowContentWidth)
            ? Math.Clamp(ModRowContentWidth, MinModRowContentWidth, MaxModRowContentWidth)
            : 620;

    /// <summary>
    /// Whether a List view row's buttons sit against the right edge of the row (the default) or
    /// immediately after the name block, where the width cap above puts them.
    /// </summary>
    /// <remarks>
    /// The cap alone used to decide both, which left the controls stranded mid-row on a wide window
    /// with no straight edge to aim down. They are two separate questions, so they are two settings.
    /// </remarks>
    public bool ModRowActionsAtRight { get; set; } = true;

    // ── mod graph view (a pack's Mods Management → Graph view) ────────────────

    /// <summary>Whether the Graph view draws the dependency lines between nodes.</summary>
    /// <remarks>
    /// The pack's own <see cref="ModAdvancedSettings.ShowDependencyLines"/> is a property OF THE PACK:
    /// it is written into <c>mods.json</c> and synced to collaborators, so flipping it to read one
    /// crowded graph changes what everyone else opens. This is the same switch as a per-launcher view
    /// preference, so the toolbar can remember how this person likes to look at a graph without
    /// touching a shared document.
    /// </remarks>
    public bool GraphShowDependencyLines { get; set; } = true;

    /// <summary>
    /// How the Graph view groups nodes — the name of a <see cref="ModClusterMode"/> member. Null means
    /// "follow the pack's <see cref="ModAdvancedSettings.DefaultClusterMode"/>", which is the state a
    /// launcher starts in.
    /// </summary>
    /// <remarks>Stored as the member NAME rather than the enum, because settings.json outlives the
    /// build that wrote it: a mode added by a newer launcher would deserialize into an out-of-range
    /// enum value here and be applied as whatever member happens to share its number. An unrecognised
    /// name parses as "not a mode I know" and falls back to the pack default instead.</remarks>
    public string? GraphClusterMode { get; set; }

    /// <summary>
    /// Which release channel a mod download or update follows when the pack has no channel of its own:
    /// "alpha" = the latest version whatever its channel, "beta" = releases and betas, "release" =
    /// stable releases only. See <see cref="ModUpdateChannel"/>.
    /// </summary>
    /// <remarks>Alpha by default: "give me the newest file" is what people mean by an update, and a
    /// pack (Mods Management → Advanced) or a single mod (right-click → Update channel) can still be
    /// pinned to release.</remarks>
    public string ModVersionChannel { get; set; } = ModUpdateChannel.Alpha;

    // ── downloads ────────────────────────────────────────────────────────────

    /// <summary>
    /// How many mod files the launcher downloads at the same time — during "Update all", a browse-page
    /// download with dependencies, and a modpack install.
    /// </summary>
    /// <remarks>Three by default. One at a time is slower than the connection allows; past a handful the
    /// bottleneck moves to the stores, which answer a burst from one address with 403s and 429s
    /// (see <see cref="ApiClient"/>), so the ceiling is deliberately 9 rather than "as many as you like".</remarks>
    public int ModDownloadConcurrency { get; set; } = 3;

    public const int MinModDownloadConcurrency = 1, MaxModDownloadConcurrency = 9;

    /// <summary>The concurrency actually used, with a stored nonsense clamped into range.</summary>
    public int EffectiveModDownloadConcurrency =>
        Math.Clamp(ModDownloadConcurrency, MinModDownloadConcurrency, MaxModDownloadConcurrency);

    /// <summary>
    /// The user's own CurseForge API key. Empty means "use the server's shared key", which is what
    /// almost everyone does; a personal key gives its owner their own quota, so a busy evening on the
    /// shared one (CurseForge answers a burst from the server's single address with 403s) stops being
    /// their problem. Sent per request to the launcher's proxy and never stored server-side.
    /// </summary>
    public string? CurseForgeApiKey { get; set; }

    // ── servers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Admin details for servers the user runs, keyed by the server address exactly as it appears in
    /// an instance's <c>servers.dat</c> (lower-cased, and with the default port left off when that is
    /// how it was typed — see <see cref="ServerKey"/>).
    /// </summary>
    /// <remarks>
    /// Deliberately keyed on the address rather than on an instance, because one server is usually
    /// reachable from several instances and the console is a property of the server, not of the copy
    /// of the server list that happens to name it.
    /// </remarks>
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

    /// <summary>
    /// Files the user has pinned in the Config and scripts page, as "{packId:N}/{relative/path}".
    /// A pinned file that no longer exists is shown greyed rather than dropped, so a pin survives an
    /// instance being re-synced or a pack being temporarily unavailable.
    /// </summary>
    public List<string> ConfigHubPins { get; set; } = new();

    /// <summary>Last filter the Config and scripts page was left on, so it opens where it was left.</summary>
    public string? ConfigHubLastFilter { get; set; }

    /// <summary>Colours for the launcher chrome and the log views.</summary>
    public ThemeSettings Theme { get; set; } = new();

    /// <summary>Text files the built-in editor has been told to keep open across sessions, newest first.</summary>
    public List<string> RecentEditedFiles { get; set; } = new();

    /// <summary>Local usage stats for ordering and display. These are intentionally per-device.</summary>
    // ConcurrentDictionary: mutated from background threads (play time recorded on the process-exit
    // callback) while Save() may be serializing on another thread. A plain Dictionary would throw
    // "collection was modified" mid-serialize; ConcurrentDictionary enumerates safely. JSON shape
    // is unchanged (still a { guid: stats } object).
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

    /// <summary>Resource pack metadata, keyed by ResourcePackService.Key().</summary>
    public Dictionary<string, ResourcePackEntry> ResourcePacks { get; set; } = new();

    /// <summary>User-defined resource pack folders. Key = folder name; value = list of resource pack keys.</summary>
    public Dictionary<string, List<string>> ResourcePackFolders { get; set; } = new();

    /// <summary>Shader pack metadata, keyed the way <see cref="ShaderPackService"/> keys a shader:
    /// <c>"{sourcePackId:N}:{fileNameOrFolderName}"</c>.</summary>
    /// <remarks>A shader is a file in a folder and nothing else — no manifest, no id, no version. This
    /// is where the launcher remembers what a given file actually IS (see
    /// <see cref="ShaderPackEntry"/>); an entry with no matching file on disk is harmless and is simply
    /// never looked up, so a shader deleted outside the launcher costs nothing.</remarks>
    public Dictionary<string, ShaderPackEntry> ShaderPacks { get; set; } = new();

    /// <summary>User-defined shader folders. Key = folder name; value = list of shader keys.</summary>
    public Dictionary<string, List<string>> ShaderFolders { get; set; } = new();

    /// <summary>Per-pack last-known shared manifest version (for update detection).
    /// ConcurrentDictionary: written from background sync tasks while Save() may serialize.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, long> PackSyncedVersion { get; set; } = new();

    /// <summary>Default Minecraft options applied as a template for new packs.</summary>
    public McDefaults McDefaults { get; set; } = new();

    /// <summary>
    /// When true, secondary pages (settings, worlds, pack detail, etc.) open in a
    /// resizable right-hand panel next to the master pack list. When false, they
    /// cover the main content area like a full-screen page.
    /// </summary>
    public bool UseSidePanel { get; set; } = true;

    /// <summary>Global UI scale for the whole launcher (1.0 = 100%), applied as a layout zoom.</summary>
    public double LauncherScale { get; set; } = 1.0;

    /// <summary>Extra zoom applied to mod entries in the list/browse views, on top of <see cref="LauncherScale"/>.</summary>
    public double ModListScale { get; set; } = 1.0;

    /// <summary>Keyboard shortcut used by the Minecraft wrapper window to hide the time bar.</summary>
    public string MinecraftWindowToggleKey { get; set; } = "key.keyboard.f8";

    /// <summary>Keyboard shortcut used by the Minecraft wrapper window to toggle borderless fullscreen.</summary>
    public string MinecraftWindowFullscreenKey { get; set; } = "key.keyboard.f11";

    /// <summary>User-defined pack folders. Key = folder name; value = list of pack IDs.
    /// Team folders are NOT stored here — they're computed from team membership at runtime.</summary>
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
    }

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
    }

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

    public void CreateShaderFolder(string name)
    {
        var key = (name ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return;
        if (!ShaderFolders.ContainsKey(key)) ShaderFolders[key] = new();
    }

    public void DeleteShaderFolder(string name)
    {
        ShaderFolders.Remove(name);
    }

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

    // ── shader entries ──────────────────────────────────────────────────────

    /// <summary>What is known about an installed shader, or null if it was never recorded — which is
    /// the normal state for one the user dropped into the folder by hand.</summary>
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

    /// <summary>
    /// Records where a shader came from, so a later store visit can recognise the file on disk as this
    /// project at this version and offer the update.
    /// </summary>
    /// <remarks>Written at install time: nothing in a shader zip identifies it afterwards, and matching
    /// by name is how you end up offering someone a different author's shader as an "update". A null
    /// <paramref name="source"/> clears the provenance, which is the right answer for a file the user
    /// replaced by hand — better no match than a wrong one.</remarks>
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

    /// <summary>
    /// How much RAM to give a pack the user has never set a value for, chosen from the machine's
    /// INSTALLED memory.
    /// </summary>
    /// <remarks>
    /// 8 GB machine gets 6 GB, 16 GB gets 10 GB, 32 GB or more gets 16 GB - the headroom deliberately
    /// grows with the total, because Windows, the GPU driver and a browser need a roughly fixed slice
    /// on a small machine but Minecraft itself stops benefiting past ~16 GB. Under 8 GB we take half
    /// and leave the rest, which is the most that can be spared without swapping.
    ///
    /// GetPhysicallyInstalledSystemMemory is used rather than GlobalMemoryStatusEx because the latter
    /// reports memory VISIBLE to the OS - hardware-reserved slices make an 8 GB machine read as ~7.9 GB
    /// and fall through a naive >= 8 GB test. This reports the installed total, so the thresholds mean
    /// what they say. If the call fails we fall back to the old fixed default rather than guess.
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

    /// <summary>
    /// Shared Minecraft runtime — libraries, assets, version JARs.
    /// Stored once globally (not per-profile) and reused across all packs.
    /// </summary>
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
                loaded.MigrateServerUrl();
                loaded.ApplyCustomGameWindowDefault();
                return loaded;
            }
        }
        catch { /* corrupt — use defaults */ }
        return new AppSettings();
    }

    private static readonly object SaveLock = new();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        // Save() is called from multiple threads (process-exit handlers, background sync).
        // Serialize and write atomically under a lock so two concurrent writers can't collide
        // (File.WriteAllText opens with no sharing → IOException) or leave a half-written,
        // unparseable settings.json that Load() would then discard, wiping the user's tokens
        // and preferences. Write to a temp file in the same directory, then atomically rename.
        lock (SaveLock)
        {
            // Other mutable collections in this graph may be structurally modified on the UI or a
            // background thread while we serialize, which makes JsonSerializer throw
            // "collection was modified". Retry a couple of times so a transient race doesn't
            // escape into a process-exit handler (crash) or skip persisting tokens/preferences.
            string? json = null;
            for (var attempt = 0; attempt < 3 && json is null; attempt++)
            {
                try { json = JsonSerializer.Serialize(this, JsonOpts); }
                catch (InvalidOperationException) when (attempt < 2) { /* concurrent mutation — retry */ }
            }
            if (json is null) return; // gave up rather than crash; a later Save() will persist state

            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
    }

    /// <summary>
    /// Moves a settings file that still names the old <c>http://IP:port</c> endpoint onto the HTTPS
    /// name. Both reach the same server, so nothing else has to change — but an existing install
    /// would otherwise keep sending its bearer token in clear text forever. A ServerUrl the user set
    /// themselves is left exactly as it is.
    /// </summary>
    public void MigrateServerUrl()
    {
        var current = (ServerUrl ?? "").Trim();
        if (current.Length == 0)
        {
            ServerUrl = DefaultServerUrl;
            return;
        }
        if (!LegacyServerUrls.Any(u => string.Equals(u.TrimEnd('/'), current.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            return;
        ServerUrl = DefaultServerUrl;
        try { Save(); } catch { /* read-only profile: the in-memory value still points at the right place */ }
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

public enum WorldSortMode
{
    Modified = 0,
    Name = 1,
    Size = 2,
    Pack = 3
}

public enum ResourcePackSortMode
{
    Modified = 0,
    Name = 1,
    Size = 2,
    Pack = 3
}

/// <summary>Shader list sort columns, in the order the Sort box lists them.</summary>
/// <remarks>Numbered explicitly, like every other stored enum here: the numbers are what sits in
/// settings.json, so inserting a member in the middle without one would quietly change what an
/// existing install is sorted by.</remarks>
public enum ShaderSortMode
{
    /// <summary>Alphabetical by display name.</summary>
    Name = 0,
    /// <summary>Grouped by the instance the shader is installed in.</summary>
    Instance = 1,
    /// <summary>Newest file first — the default; see <see cref="AppSettings.ShaderSortMode"/>.</summary>
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

/// <summary>
/// The user's colours. Everything here is optional: a null field means "derive it", which is how a
/// two-colour choice (accent + surface) produces a whole coherent palette — see
/// <see cref="ThemeService"/>. Stored as <c>#RRGGBB</c> strings so a settings.json stays readable
/// and a bad value degrades to the default instead of breaking the launcher.
/// </summary>
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

    // Log view — null means "derive from the surface/accent above".
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

/// <summary>
/// Per-world preferences stored locally. A world "lives" in a single pack's
/// game/saves folder but the user can declare which other packs may launch it.
/// </summary>
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
    // Where this zip came from, recorded at install time. A resource pack carries no id of its own, so
    // without this the launcher can only match it back to a store listing by name - which is how you
    // end up offering somebody a same-named pack by a different author as an "update". All four are
    // null for a pack that was dragged in by hand, and that is a normal, permanent state: no
    // provenance means no update check, not a broken entry.

    /// <summary>The store this pack was installed from, or null if it did not come from one.</summary>
    public ModSource? Source { get; set; }

    /// <summary>Project id on <see cref="Source"/> (Modrinth id/slug, CurseForge mod id).</summary>
    public string? ProjectId { get; set; }

    /// <summary>Id of the exact file that was installed, which is what an update compares against.</summary>
    public string? VersionId { get; set; }

    /// <summary>Human-readable version of the installed file ("v1.4.2"), for display only: stores let
    /// authors write anything here, so it is never the thing an update decision is made on.</summary>
    public string? VersionNumber { get; set; }
}

/// <summary>
/// Per-shader preferences stored locally, keyed by <see cref="AppSettings.ShaderPacks"/>'s key.
/// </summary>
/// <remarks>
/// Deliberately smaller than <see cref="ResourcePackEntry"/>: shaders are not shared through the
/// launcher, so there is no visibility, no hosted id and no per-instance compatibility list to keep —
/// a shader belongs to the instance whose folder it sits in. What is worth remembering is the name to
/// show and, above all, which store listing the file corresponds to.
/// </remarks>
public sealed class ShaderPackEntry
{
    /// <summary>Name to show instead of the file name. Empty means "use the file name".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The store this shader was installed from, or null if it did not come from one (dragged
    /// in by hand, or installed before the launcher recorded this).</summary>
    public ModSource? Source { get; set; }

    /// <summary>Project id on <see cref="Source"/> (Modrinth id/slug, CurseForge mod id).</summary>
    public string? ProjectId { get; set; }

    /// <summary>Id of the exact file that was installed, which is what an update compares against.</summary>
    public string? VersionId { get; set; }

    /// <summary>Human-readable version of the installed file ("v1.4.2"), for display only: stores let
    /// authors write anything here, so it is never the thing an update decision is made on.</summary>
    public string? VersionNumber { get; set; }

    /// <summary>True once enough is known to look this shader up in the store it came from.</summary>
    [JsonIgnore]
    public bool HasProvenance => Source is not null && !string.IsNullOrWhiteSpace(ProjectId);
}

/// <summary>
/// User-editable defaults that map onto Minecraft's <c>options.txt</c>.
/// Stored centrally so the launcher can stamp them into new packs.
/// </summary>
public sealed class McDefaults
{
    // Video
    public int    Fov               { get; set; } = 70;     // 30 – 110
    public int    RenderDistance    { get; set; } = 12;     // 2 – 32
    public int    SimulationDistance{ get; set; } = 10;     // 5 – 32
    public double Brightness        { get; set; } = 0.5;    // 0.0 – 1.0
    public int    GuiScale          { get; set; } = 0;      // 0 = Auto, 1 – 4
    public bool   Fullscreen        { get; set; } = false;
    public bool   VSync             { get; set; } = true;
    public bool   ViewBobbing       { get; set; } = true;
    public bool   AutoJump          { get; set; } = false;

    // Audio (0.0 – 1.0)
    public double MasterVolume      { get; set; } = 1.0;
    public double MusicVolume       { get; set; } = 1.0;
    public double SoundFxVolume     { get; set; } = 1.0;

    // Common keybinds — values are Minecraft's key.* identifiers
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

/// <summary>
/// What the launcher needs to talk to a server's console, plus how the user labelled it.
/// </summary>
/// <remarks>
/// <para>RCON is the only remote-console protocol vanilla Minecraft speaks, and it authenticates with
/// a single shared password sent over an unencrypted socket. The password is therefore stored here in
/// clear, exactly as <see cref="AppSettings.CurseForgeApiKey"/> and the account tokens beside it are —
/// settings.json is already the trust boundary for this application. The UI says so where the
/// password is entered, and the value is never logged or sent anywhere but the server it belongs to.</para>
/// </remarks>
public sealed class ServerAdminEntry
{
    /// <summary>What to call this server in the launcher. Empty falls back to the servers.dat name.</summary>
    public string? Label { get; set; }

    /// <summary>RCON host. Empty means "the same host as the game address".</summary>
    public string? RconHost { get; set; }

    /// <summary>RCON port. Minecraft's default is 25575.</summary>
    public int RconPort { get; set; } = 25575;

    /// <summary>RCON password, as configured in the server's server.properties.</summary>
    public string? RconPassword { get; set; }

    /// <summary>Commands the user has run against this server, newest last, capped by the console UI.</summary>
    public List<string> CommandHistory { get; set; } = new();

    [JsonIgnore]
    public bool HasConsole => !string.IsNullOrWhiteSpace(RconPassword);
}
