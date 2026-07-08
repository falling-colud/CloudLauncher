using System.IO;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "http://130.61.131.193:5000";
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

    /// <summary>Per-pack auto-update preference. True = always update before launch.</summary>
    public Dictionary<Guid, bool> PackAutoUpdate { get; set; } = new();

    /// <summary>Per-pack auto-apply-rules preference. Missing entry = ON by default.</summary>
    public Dictionary<Guid, bool> PackAutoApplyRules { get; set; } = new();

    /// <summary>
    /// When true, launcher-started games are embedded in CloudLauncher's custom host
    /// window (play-time bar, instance side-panel, collapse/fullscreen hotkeys). When
    /// false, Minecraft opens in its own native window and the launcher just tracks the
    /// process.
    /// </summary>
    public bool UseCustomGameWindow { get; set; } = true;

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

    public int GetMaxRamFor(Guid packId) =>
        PackMaxRamMb.TryGetValue(packId, out var v) ? v : DefaultMaxRamMb;

    public void SetMaxRamFor(Guid packId, int? mb)
    {
        if (mb is null) PackMaxRamMb.Remove(packId);
        else PackMaxRamMb[packId] = mb.Value;
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

    private static readonly string SettingsPath = Path.Combine(DataRoot, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new();
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

    public bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);

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

public sealed class PackUsageStats
{
    public bool IsPinned { get; set; }
    public int PlayCount { get; set; }
    public long TotalPlayTimeSeconds { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }
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
}
