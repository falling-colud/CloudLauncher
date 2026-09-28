using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudLauncher.Services;

/// <summary>Which of the game's two pack systems a folder feeds.</summary>
public enum GlobalPackKind { Resource, Data }

/// <summary>One folder a mod in the instance loads packs from, always on and in every world.</summary>
/// <param name="ModKey">Stable id of the mod family (<c>paxi</c>, <c>globalpacks</c>, ...).</param>
/// <param name="ModName">What the user calls it, for "Always on - loaded by Paxi".</param>
/// <param name="RelativeDir">The folder, relative to the instance's <c>game/</c> folder, with forward
/// slashes.</param>
/// <param name="OrderFile">The mod's load-order file, relative to <c>game/</c>, when it has one the
/// launcher can edit. Null means the mod decides the order itself.</param>
/// <param name="Mixed">True for a folder that holds both kinds and tells them apart by what is inside
/// each pack (Open Loader on 1.21).</param>
/// <param name="NewWorldsOnly">True when the mod only adds these packs as a new world is created, so
/// an existing world is not affected (Data Loader).</param>
/// <param name="LoadedBy">Every detected mod that reads this same folder. Several mods read the
/// instance's <c>datapacks/</c> folder.</param>
public sealed record GlobalPackFolder(
    string ModKey,
    string ModName,
    GlobalPackKind Kind,
    string RelativeDir,
    string? OrderFile,
    bool Mixed,
    bool NewWorldsOnly,
    IReadOnlyList<string> LoadedBy)
{
    /// <summary>"Paxi", or "Paxi and Open Loader" for a shared folder.</summary>
    public string LoadedByLabel => LoadedBy.Count switch
    {
        0 => ModName,
        1 => LoadedBy[0],
        _ => string.Join(", ", LoadedBy.Take(LoadedBy.Count - 1)) + " and " + LoadedBy[^1]
    };

    /// <summary>True when the launcher can put these packs in order by editing the mod's file.</summary>
    public bool CanOrder => OrderFile is not null;

    /// <summary>One sentence on who decides the order, for tooltips.</summary>
    public string OrderNote => CanOrder
        ? $"{ModName} loads them in the order shown here (top wins), from {OrderFile}."
        : $"{LoadedByLabel} decides their order itself (by name, the first time) - the launcher can't change it.";
}

/// <summary>A pack found in one of the always-on folders.</summary>
/// <param name="Order">1 for the pack that wins, 2 for the next and so on, when the folder has a
/// load-order file that lists it. 0 when it doesn't: the mod still loads it, below the listed ones.</param>
public sealed record GlobalPackEntry(
    GlobalPackFolder Folder,
    string FileName,
    string FilePath,
    bool IsFolder,
    long SizeBytes,
    DateTimeOffset Modified,
    int Order)
{
    public string DisplayName => IsFolder ? FileName : Path.GetFileNameWithoutExtension(FileName);

    /// <summary>"Always on - loaded by Paxi", the label every list uses.</summary>
    public string AlwaysOnLabel => Folder.NewWorldsOnly
        ? $"New worlds - loaded by {Folder.LoadedByLabel}"
        : $"Always on - loaded by {Folder.LoadedByLabel}";
}

/// <summary>
/// Mods that load resource packs and data packs from a folder for every world, always on: Paxi,
/// Global Packs, Open Loader and a few smaller ones. Finds which of them an instance has, lists what
/// is in their folders, and puts packs in and out of them.
/// </summary>
/// <remarks>
/// <para>Every folder and file name here was read out of the mods' own jars (decompiled, per
/// version line) rather than taken from their pages, because they differ between versions and the
/// pages lag: Open Loader moved from <c>openloader/</c> to <c>config/openloader/{data,resources}</c>
/// at 1.17 and to one mixed <c>config/openloader/packs</c> at 1.21; Global Packs reads its folder list
/// from a config file whose name changed; Paxi added a resource pack order file at 3.0.1 and the
/// base <c>datapacks/</c> folder at 5.1.3.</para>
/// <para>Detection reads the instance's enabled jars in <c>mods/</c> (and <c>local/mods/</c>, which the
/// launch overlay adds): a jar is recognised by the store identity the launcher already cached for
/// it, or, when its file name hints at one of these mods, by the mod id in its own metadata. Only
/// hinted jars are opened, so a scan of a 400-mod instance costs a directory listing.</para>
/// <para>Only Paxi has a load-order file (<c>{"loadOrder": [...]}</c>, the last entry wins, packs
/// not listed load below the listed ones). The others decide the order themselves, so the lists say
/// so instead of offering arrows that would do nothing.</para>
/// <para>These packs never go through options.txt or level.dat, so the resource pack stack and the
/// defaults engine never see them; moving a pack in here takes it out of <c>resourcepacks/</c> (and
/// out of the defaults for that instance) so the game doesn't load it twice.</para>
/// </remarks>
public sealed class GlobalPackService(
    PackFolderService packs,
    MinecraftInstanceService instances,
    ModFingerprintCache fingerprints,
    ResourcePackService resourcePacks,
    ContentLibraryService library,
    ContentDefaultsService defaults)
{
    // ── the catalogue ────────────────────────────────────────────────────────

    private sealed record ModFamily(string Key, string Name, string[] ModIds, string[] NameHints,
                                    string[] ModrinthIds, string[] CurseForgeIds, int Rank);

    /// <summary>The mods this knows, best install target first. Ids: Modrinth project ids and
    /// CurseForge project ids as the stores give them (CurseForge checked through its API), mod ids
    /// from each jar's fabric.mod.json / mods.toml.</summary>
    private static readonly ModFamily[] Families =
    [
        new("paxi", "Paxi", ["paxi"], ["paxi"], ["CU0PAyzb"], ["515708", "418881", "1015157"], 0),
        new("globalpacks", "Global Packs", ["globalpacks", "globaldataandresourcepacks"],
            ["globalpack", "globaldataandresource", "drpglobal"], ["NRLPy2mk"], ["317134"], 1),
        new("openloader", "Open Loader", ["openloader"], ["openloader"], ["KwWsINvD"], ["354339"], 2),
        new("simpleresourceloader", "Simple Resource Loader", ["simpleresourceloader"],
            ["simpleresourceloader"], ["5e65FGXQ"], [], 3),
        new("global-datapack", "Global Datapacks", ["global-datapack"], ["globaldatapack"], ["bRa9yz7E"], [], 4),
        new("dataloader", "Data Loader", ["dataloader"], ["dataloader"], ["gEUERjxK"], ["318894"], 5),
    ];

    /// <summary>One detected mod, with the version its own metadata gives.</summary>
    public sealed record DetectedMod(string Key, string Name, string? Version);

    // ── detection ────────────────────────────────────────────────────────────

    /// <summary>The global-pack mods switched on in this instance, best install target first.</summary>
    /// <remarks>Never throws; an unreadable mods folder means none.</remarks>
    public IReadOnlyList<DetectedMod> Detect(Guid packId, string? packName = null)
    {
        var found = new Dictionary<string, DetectedMod>(StringComparer.Ordinal);
        try
        {
            if (ReadGameDir(packId) is not { } gameDir) return [];
            foreach (var dir in new[] { Path.Combine(gameDir, "mods"), Path.Combine(packs.LocalDir(packId), "mods") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var jar in Directory.EnumerateFiles(dir, "*.jar"))
                {
                    if (FamilyOf(jar) is not { } family || found.ContainsKey(family.Key)) continue;
                    found[family.Key] = new DetectedMod(family.Key, family.Name, JarMeta(jar).Version);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.LogError(nameof(GlobalPackService), ex);
        }
        return Families.Where(f => found.ContainsKey(f.Key)).Select(f => found[f.Key]).ToList();
    }

    /// <summary>Which family a jar belongs to, or null.</summary>
    private ModFamily? FamilyOf(string jar)
    {
        // The identity the store lookups already cached: no zip opened, and a renamed jar still counts.
        if (fingerprints.TryGetCachedMatches(jar, out var modrinth, out var curseForge))
        {
            var byStore = Families.FirstOrDefault(f =>
                (modrinth is { } mr && f.ModrinthIds.Contains(mr.Mod.Id, StringComparer.Ordinal))
                || (curseForge is { } cf && f.CurseForgeIds.Contains(cf.Mod.Id, StringComparer.Ordinal)));
            if (byStore is not null) return byStore;
        }

        // Otherwise only jars whose name hints at one of these are opened, and the mod id inside
        // decides: "paxi" in a file name is a hint, not proof.
        var collapsed = Collapse(Path.GetFileName(jar));
        var hinted = Families.Where(f => f.NameHints.Any(collapsed.Contains)).ToList();
        if (hinted.Count == 0) return null;
        var ids = JarMeta(jar).ModIds;
        return hinted.FirstOrDefault(f => f.ModIds.Any(ids.Contains));
    }

    private static string Collapse(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record JarInfo(long Length, long Ticks, HashSet<string> ModIds, string? Version);

    /// <summary>Mod ids and version per jar, keyed by path and stamped by size and write time.</summary>
    private static readonly ConcurrentDictionary<string, JarInfo> JarCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mod ids a jar declares (Fabric, Quilt, Forge and NeoForge metadata) and its
    /// version. Never throws.</summary>
    private static JarInfo JarMeta(string jar)
    {
        long length = 0, ticks = 0;
        try
        {
            var info = new FileInfo(jar);
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
            if (JarCache.TryGetValue(jar, out var hit) && hit.Length == length && hit.Ticks == ticks) return hit;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            string? version = null;
            using var zip = ZipFile.OpenRead(jar);
            foreach (var name in new[] { "fabric.mod.json", "quilt.mod.json" })
            {
                if (zip.GetEntry(name) is not { } entry) continue;
                using var doc = JsonDocument.Parse(ReadText(entry), new JsonDocumentOptions
                    { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = doc.RootElement;
                if (root.TryGetProperty("quilt_loader", out var quilt)) root = quilt;
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) ids.Add(id.GetString()!);
                if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) version ??= v.GetString();
            }
            foreach (var name in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
            {
                if (zip.GetEntry(name) is not { } entry) continue;
                var text = ReadText(entry);
                // The first [[mods]] block is the jar's own mod; later modId lines are dependencies.
                if (Regex.Match(text, @"^\s*modId\s*=\s*""([^""]+)""", RegexOptions.Multiline) is { Success: true } m)
                    ids.Add(m.Groups[1].Value);
                if (version is null
                    && Regex.Match(text, @"^\s*version\s*=\s*""([^""$]+)""", RegexOptions.Multiline) is { Success: true } vm)
                    version = vm.Groups[1].Value;
            }
            if (version is null && zip.GetEntry("META-INF/MANIFEST.MF") is { } manifest
                && Regex.Match(ReadText(manifest), @"Implementation-Version:\s*(\S+)") is { Success: true } mv)
                version = mv.Groups[1].Value;

            var result = new JarInfo(length, ticks, ids, version);
            JarCache[jar] = result;
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            var empty = new JarInfo(length, ticks, [], null);
            JarCache[jar] = empty;
            return empty;
        }
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        // Metadata files are a few KB; the cap keeps a hostile jar from handing us a gigabyte.
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var buffer = new char[256 * 1024];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    // ── folders ──────────────────────────────────────────────────────────────

    /// <summary>The always-on folders for one kind in this instance, best install target first.</summary>
    /// <param name="minecraftVersion">The instance's Minecraft version. Open Loader's folders depend on
    /// it.</param>
    public IReadOnlyList<GlobalPackFolder> FoldersFor(Guid packId, string? packName, string? minecraftVersion,
                                                      GlobalPackKind kind) =>
        ReadGameDir(packId) is { } gameDir
            ? FoldersFor(Detect(packId, packName), gameDir, minecraftVersion, kind)
            : [];

    /// <inheritdoc cref="FoldersFor(Guid, string?, string?, GlobalPackKind)"/>
    public static IReadOnlyList<GlobalPackFolder> FoldersFor(IReadOnlyList<DetectedMod> mods, string gameDir,
                                                             string? minecraftVersion, GlobalPackKind kind)
    {
        var raw = new List<(DetectedMod Mod, string Dir, string? Order, bool Mixed, bool NewOnly)>();
        foreach (var mod in mods)
        {
            foreach (var (dir, order, mixed, newOnly) in LayoutOf(mod, gameDir, minecraftVersion, kind))
                raw.Add((mod, Normalise(dir), order, mixed, newOnly));
        }

        // One row per folder, naming every mod that reads it. The first mod (best target) owns it.
        return raw
            .GroupBy(r => r.Dir, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var owner = g.First();
                return new GlobalPackFolder(owner.Mod.Key, owner.Mod.Name, kind, owner.Dir,
                    g.Select(r => r.Order).FirstOrDefault(o => o is not null),
                    g.Any(r => r.Mixed),
                    g.All(r => r.NewOnly),
                    g.Select(r => r.Mod.Name).Distinct().ToList());
            })
            .ToList();
    }

    /// <summary>The folder a new pack of this kind goes into: the best mod's own folder, not the shared
    /// <c>datapacks/</c> one, when there is a choice.</summary>
    public static GlobalPackFolder? DefaultTarget(IReadOnlyList<GlobalPackFolder> folders) =>
        folders.FirstOrDefault(f => !f.NewWorldsOnly && !string.Equals(f.RelativeDir, "datapacks", StringComparison.OrdinalIgnoreCase))
        ?? folders.FirstOrDefault(f => !f.NewWorldsOnly)
        ?? folders.FirstOrDefault();

    private static string Normalise(string dir) => dir.Replace('\\', '/').Trim('/');

    /// <summary>Where one mod reads one kind from, as its jar does for this Minecraft version.</summary>
    private static IEnumerable<(string Dir, string? Order, bool Mixed, bool NewOnly)> LayoutOf(
        DetectedMod mod, string gameDir, string? mc, GlobalPackKind kind)
    {
        var data = kind == GlobalPackKind.Data;
        switch (mod.Key)
        {
            case "paxi":
                // config/paxi/ on every version line (the loader's config dir + "paxi").
                yield return data
                    ? ("config/paxi/datapacks", "config/paxi/datapack_load_order.json", false, false)
                    : ("config/paxi/resourcepacks", "config/paxi/resourcepack_load_order.json", false, false);
                // 5.1.3 also force-loads the instance's own datapacks/ folder, unless its config says no.
                if (data && VersionAtLeast(mod.Version, 5, 1, 3) && !PaxiSkipsBaseDatapacks(gameDir))
                    yield return ("datapacks", null, false, false);
                break;

            case "globalpacks":
                foreach (var dir in GlobalPacksFolders(gameDir, data))
                    yield return (dir, null, false, false);
                break;

            case "openloader":
                if (!McAtLeast(mc, 17))
                {
                    yield return (data ? "openloader/data" : "openloader/resources", null, false, false);
                }
                else if (!McAtLeast(mc, 21))
                {
                    yield return (data ? "config/openloader/data" : "config/openloader/resources", null, false, false);
                }
                else
                {
                    var options = OpenLoaderOptions(gameDir);
                    if (data ? options.LoadData : options.LoadResources)
                        yield return ("config/openloader/packs", null, true, false);
                    // 21.1.3 added the instance's datapacks/ folder, on by default.
                    if (data && options.LoadData && options.LoadDatapacksDir && VersionAtLeast(mod.Version, 21, 1, 3))
                        yield return ("datapacks", null, false, false);
                }
                break;

            case "simpleresourceloader":
                // "common" feeds both kinds. The optional/ folders are offered but off, so not listed.
                yield return (data ? "resources/datapack/required" : "resources/resourcepack/required", null, false, false);
                yield return ("resources/common/required", null, false, false);
                break;

            case "global-datapack":
                if (data) yield return ("datapacks", null, false, false);
                break;

            case "dataloader":
                if (data) yield return ("datapacks", null, false, true);
                break;
        }
    }

    /// <summary>Global Packs' required folders from its config, or its defaults when there is none
    /// yet (it writes the file on first start).</summary>
    /// <remarks>Only folder entries are listed, not single-file entries, and for data packs the
    /// <c>resourcepacks/</c> entry is left out: it lets a resource pack that also carries data load as
    /// a data pack, and listing every resource pack as a data pack would be noise. Optional folders
    /// are off until picked in the game, so they aren't "always on".</remarks>
    private static IEnumerable<string> GlobalPacksFolders(string gameDir, bool data)
    {
        List<string>? configured = null;
        foreach (var name in new[] { "global_packs.toml", "global_data_and_resourcepacks.toml" })
        {
            var path = Path.Combine(gameDir, "config", name);
            if (!File.Exists(path)) continue;
            try { configured = TomlStringArray(File.ReadAllText(path), data ? "datapacks" : "resourcepacks", "required"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            break;
        }
        configured ??= data
            ? ["datapacks/", "resourcepacks/", "global_packs/required_data/"]
            : ["global_packs/required_resources/"];

        foreach (var entry in configured)
        {
            var clean = Normalise(entry);
            if (clean.Length == 0 || !PathSafety.IsSafeRelativePath(clean)) continue;
            if (data && string.Equals(clean, "resourcepacks", StringComparison.OrdinalIgnoreCase)) continue;
            // A path to one pack file rather than a folder.
            if (clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(gameDir, clean))
                || File.Exists(Path.Combine(gameDir, clean, "pack.mcmeta"))) continue;
            yield return clean;
        }
    }

    /// <summary>The strings of <c>key = [ ... ]</c> under <c>[section]</c> in a TOML file, comments
    /// dropped, or null when the key isn't there.</summary>
    /// <remarks>Just enough TOML for Global Packs' config: a table header and one array of strings,
    /// possibly over several lines.</remarks>
    public static List<string>? TomlStringArray(string text, string section, string key)
    {
        var lines = text.Split('\n').Select(StripTomlComment).ToList();
        var inSection = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && !line.StartsWith("[["))
            {
                inSection = string.Equals(line.Trim('[', ']', ' '), section, StringComparison.Ordinal);
                continue;
            }
            if (!inSection) continue;
            var m = Regex.Match(line, $@"^{Regex.Escape(key)}\s*=\s*(.*)$");
            if (!m.Success) continue;

            var body = m.Groups[1].Value;
            while (!body.Contains(']') && i + 1 < lines.Count) body += "\n" + lines[++i];
            return Regex.Matches(body, "\"((?:[^\"\\\\]|\\\\.)*)\"|'([^']*)'")
                .Select(s => s.Groups[1].Success ? s.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"") : s.Groups[2].Value)
                .ToList();
        }
        return null;
    }

    private static string StripTomlComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '#') return line[..i];
        }
        return line;
    }

    /// <summary>Paxi 5.1.3's "Load from base 'datapacks' directory" set to false in its NeoForge or
    /// Forge config. Fabric's AutoConfig file is not read; its default is on.</summary>
    private static bool PaxiSkipsBaseDatapacks(string gameDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(gameDir, "config"), "paxi*.toml"))
                if (Regex.IsMatch(File.ReadAllText(file), @"""Load from base 'datapacks' directory""\s*=\s*false"))
                    return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private sealed record OpenLoaderSwitches(bool LoadResources, bool LoadData, bool LoadDatapacksDir);

    /// <summary>Open Loader 1.21's <c>config/openloader/options.json</c> switches; all on when the file
    /// is missing or unreadable, as the mod's defaults are.</summary>
    private static OpenLoaderSwitches OpenLoaderOptions(string gameDir)
    {
        var path = Path.Combine(gameDir, "config", "openloader", "options.json");
        try
        {
            if (!File.Exists(path)) return new(true, true, true);
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            bool Flag(string name) => !doc.RootElement.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.False;
            return new(Flag("load_resource_packs"), Flag("load_data_packs"), Flag("load_datapacks_dir"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(true, true, true);
        }
    }

    // ── versions ─────────────────────────────────────────────────────────────

    /// <summary>True when a <c>1.x</c> Minecraft version is at least <c>1.minor</c>. Unknown and
    /// year-numbered versions (26.1) count as new.</summary>
    public static bool McAtLeast(string? minecraftVersion, int minor)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return true;
        var parts = minecraftVersion.Trim().Split('.', '-', ' ');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major)) return true;
        if (major != 1) return major > 1;
        var digits = new string(parts[1].TakeWhile(char.IsDigit).ToArray());
        return !int.TryParse(digits, out var m) || m >= minor;
    }

    /// <summary>True when the last <c>a.b.c</c> in a jar's version string is at least the one given.
    /// Paxi writes <c>1.21.1-NeoForge-5.1.3</c>, so the Minecraft part in front is skipped. An
    /// unreadable version counts as new.</summary>
    public static bool VersionAtLeast(string? version, int major, int minor, int patch)
    {
        if (string.IsNullOrWhiteSpace(version)) return true;
        var matches = Regex.Matches(version, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (matches.Count == 0) return true;
        var last = matches[^1];
        var v = (int.Parse(last.Groups[1].Value), int.Parse(last.Groups[2].Value),
                 last.Groups[3].Success ? int.Parse(last.Groups[3].Value) : 0);
        return v.CompareTo((major, minor, patch)) >= 0;
    }

    // ── listing ──────────────────────────────────────────────────────────────

    private string GameDirOf(Guid packId, string? packName) =>
        packName is null ? packs.GameDir(packId) : packs.GameDir(packId, packName);

    /// <summary>The instance's game folder for reading, or null when it has no folder on this PC.</summary>
    /// <remarks>The overloads that take a name create a missing instance folder, which a scan over
    /// every instance must never do.</remarks>
    private string? ReadGameDir(Guid packId)
    {
        try { return packs.GameDir(packId); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Every pack in the always-on folders for one kind, each folder's packs in load order
    /// (the winner first), then the ones its order file doesn't list.</summary>
    /// <remarks>Never throws. Reads folder listings and, for a mixed folder, each pack's top
    /// entries.</remarks>
    public List<GlobalPackEntry> Scan(Guid packId, string? packName, string? minecraftVersion, GlobalPackKind kind) =>
        ReadGameDir(packId) is { } gameDir ? Scan(gameDir, FoldersFor(packId, packName, minecraftVersion, kind)) : [];

    /// <inheritdoc cref="Scan(Guid, string?, string?, GlobalPackKind)"/>
    public static List<GlobalPackEntry> Scan(string gameDir, IReadOnlyList<GlobalPackFolder> folders)
    {
        var result = new List<GlobalPackEntry>();
        foreach (var folder in folders)
        {
            try
            {
                var dir = PathSafety.ResolveInside(gameDir, folder.RelativeDir);
                if (dir is null || !Directory.Exists(dir)) continue;

                var order = folder.OrderFile is { } of ? ReadOrder(Path.Combine(gameDir, of)) ?? [] : [];
                var entries = new List<GlobalPackEntry>();
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith('.')) continue;
                    var isFolder = Directory.Exists(entry);
                    if (!isFolder && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (isFolder && !File.Exists(Path.Combine(entry, "pack.mcmeta"))) continue;
                    if (folder.Mixed && !HoldsKind(entry, isFolder, folder.Kind)) continue;

                    long size = 0;
                    var modified = DateTimeOffset.MinValue;
                    try
                    {
                        if (isFolder)
                        {
                            modified = Directory.GetLastWriteTime(entry);
                            size = new DirectoryInfo(entry).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                        }
                        else
                        {
                            var info = new FileInfo(entry);
                            modified = info.LastWriteTime;
                            size = info.Length;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

                    // Last in the file wins, so the last entry is #1.
                    var at = order.FindLastIndex(o => string.Equals(o, name, StringComparison.Ordinal));
                    entries.Add(new GlobalPackEntry(folder, name, entry, isFolder, size, modified,
                        at < 0 ? 0 : order.Count - at));
                }
                result.AddRange(entries
                    .OrderBy(e => e.Order == 0 ? int.MaxValue : e.Order)
                    .ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.LogError(nameof(GlobalPackService), ex);
            }
        }
        return result;
    }

    /// <summary>True when a pack in Open Loader's mixed folder is of <paramref name="kind"/>: it has
    /// <c>data/</c> for data, <c>assets/</c> for resources, and one with neither loads as both.</summary>
    private static bool HoldsKind(string path, bool isFolder, GlobalPackKind kind)
    {
        try
        {
            bool hasData, hasAssets;
            if (isFolder)
            {
                hasData = Directory.Exists(Path.Combine(path, "data"));
                hasAssets = Directory.Exists(Path.Combine(path, "assets"));
            }
            else
            {
                using var zip = ZipFile.OpenRead(path);
                hasData = zip.Entries.Any(e => e.FullName.StartsWith("data/", StringComparison.Ordinal));
                hasAssets = zip.Entries.Any(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal));
            }
            if (!hasData && !hasAssets) return true;
            return kind == GlobalPackKind.Data ? hasData : hasAssets;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // ── Paxi's order file ────────────────────────────────────────────────────

    /// <summary>The names in a Paxi order file, first entry first. Empty for a missing file, null
    /// for one that can't be read as <c>{"loadOrder": [...]}</c> (which must then not be
    /// overwritten).</summary>
    public static List<string>? ReadOrder(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("loadOrder", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a Paxi order file in the shape Paxi writes it.</summary>
    private static void WriteOrder(string path, IReadOnlyList<string> names)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<string>> { ["loadOrder"] = names },
            new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Changes the order file with <paramref name="edit"/>, refusing a file that couldn't be
    /// read rather than replacing someone's hand-written order.</summary>
    private static void EditOrder(string gameDir, GlobalPackFolder folder, Action<List<string>> edit)
    {
        if (folder.OrderFile is not { } rel) return;
        var path = PathSafety.ResolveInside(gameDir, rel)
                   ?? throw new GlobalPackRefusedException($"{rel} is not a file inside the instance.");
        var order = ReadOrder(path)
                    ?? throw new GlobalPackRefusedException(
                        $"{folder.ModName}'s {Path.GetFileName(path)} could not be read, so the order was left alone. "
                        + "Fix or delete that file and try again.");
        edit(order);
        WriteOrder(path, order);
    }

    // ── changing things ──────────────────────────────────────────────────────

    private void EnsureWritable(Guid packId)
    {
        if (!ContentWriteGate.Allows(instances, packId, duringLaunch: false, out var why))
            throw new GlobalPackRefusedException(why);
    }

    /// <summary>The folder on disk, created if needed.</summary>
    private static string FolderOnDisk(string gameDir, GlobalPackFolder folder)
    {
        var dir = PathSafety.ResolveInside(gameDir, folder.RelativeDir)
                  ?? throw new GlobalPackRefusedException($"{folder.RelativeDir} is not a folder inside the instance.");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Copies a pack (zip or folder) into an always-on folder, at the top of the order where
    /// the mod has one.</summary>
    /// <returns>The path it landed at; a second copy of one name becomes <c>name-2.zip</c>.</returns>
    /// <exception cref="GlobalPackRefusedException">Minecraft runs in the instance, or the order file
    /// can't be read. The copy is not made in either case.</exception>
    public string Install(Guid packId, string? packName, GlobalPackFolder folder, string sourcePath)
    {
        EnsureWritable(packId);
        var gameDir = GameDirOf(packId, packName);
        // Read the order first, so an unreadable file stops us before anything is copied.
        EditOrder(gameDir, folder, _ => { });

        var dir = FolderOnDisk(gameDir, folder);
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dest = ShaderPackService.NextFreePath(dir, name);
        if (Directory.Exists(sourcePath)) CopyDirectory(sourcePath, dest);
        else File.Copy(sourcePath, dest);

        var placed = Path.GetFileName(dest);
        EditOrder(gameDir, folder, order =>
        {
            order.RemoveAll(n => string.Equals(n, placed, StringComparison.Ordinal));
            order.Add(placed);   // last wins
        });
        return dest;
    }

    /// <summary>Deletes a pack from its always-on folder and from the order file.</summary>
    public void Remove(Guid packId, string? packName, GlobalPackEntry entry)
    {
        EnsureWritable(packId);
        var gameDir = GameDirOf(packId, packName);
        DeleteEntry(gameDir, entry);
        EditOrder(gameDir, entry.Folder, order =>
            order.RemoveAll(n => string.Equals(n, entry.FileName, StringComparison.Ordinal)));
    }

    /// <summary>Puts one folder's packs in this order, the winner first, by rewriting the mod's order
    /// file.</summary>
    /// <remarks>Every pack shown gets listed, so "what you see is the order" holds afterwards; entries
    /// the file named that aren't packs in the folder (Paxi 5.1.3 also takes paths such as
    /// <c>resourcepacks/X.zip</c>) are kept, below the rest, in the order they had.</remarks>
    public void SetOrder(Guid packId, string? packName, GlobalPackFolder folder, IReadOnlyList<string> fileNamesHighestFirst)
    {
        EnsureWritable(packId);
        if (!folder.CanOrder)
            throw new GlobalPackRefusedException($"{folder.LoadedByLabel} decides this order itself.");
        var gameDir = GameDirOf(packId, packName);
        EditOrder(gameDir, folder, order =>
        {
            var shown = new HashSet<string>(fileNamesHighestFirst, StringComparer.Ordinal);
            var others = order.Where(n => !shown.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
            order.Clear();
            order.AddRange(others);
            order.AddRange(fileNamesHighestFirst.Reverse());
        });
    }

    /// <summary>Moves one pack <paramref name="delta"/> places (negative = up, toward winning) within
    /// its folder's order.</summary>
    public void Move(Guid packId, string? packName, GlobalPackEntry entry, IReadOnlyList<GlobalPackEntry> sameFolder, int delta)
    {
        var names = sameFolder.Select(e => e.FileName).ToList();
        var index = names.IndexOf(entry.FileName);
        if (index < 0) return;
        var target = Math.Clamp(index + delta, 0, names.Count - 1);
        if (target == index) return;
        names.RemoveAt(index);
        names.Insert(target, entry.FileName);
        SetOrder(packId, packName, entry.Folder, names);
    }

    /// <summary>Moves a resource pack out of <c>resourcepacks/</c> into an always-on folder.</summary>
    /// <remarks>
    /// <para>Moved, not copied: with the pack in both places the game would load it twice, once from
    /// options.txt and once from the mod. Both folders' copies go (the launch overlay puts a
    /// <c>local/</c> pack into <c>game/</c> too), and it leaves the options.txt stack.</para>
    /// <para>A pack the launcher hands out as a default would come straight back at the next launch,
    /// so that default is switched off for this instance (the same "not here" as unticking it on the
    /// pack's page).</para>
    /// </remarks>
    /// <returns>The pack's new path.</returns>
    public string MoveFromResourcePacks(Guid packId, string packName, ResourcePackInfo pack, GlobalPackFolder target)
    {
        EnsureWritable(packId);
        var dest = Install(packId, packName, target, pack.FilePath);

        // Scanned here rather than taken from the page: the lists hide a local/ copy behind its game/
        // twin, and a local/ copy left behind would be linked back into game/ at the next launch.
        var twins = resourcePacks.ScanPack(packId, packName, includeLocal: true)
            .Where(p => p.Key != pack.Key
                        && string.Equals(p.FileName, pack.FileName, StringComparison.OrdinalIgnoreCase)
                        && PackFolderService.EntriesReferToSameContent(p.FilePath, pack.FilePath))
            .ToList();

        // Scanned once, not per twin: Scan re-reads the whole library.
        var libraryItem = library.Scan(LibraryKind.ResourcePack)
            .FirstOrDefault(i => PackFolderService.EntriesReferToSameContent(i.Path, pack.FilePath));

        foreach (var copy in twins.Prepend(pack)) resourcePacks.DeleteFromDisk(copy);
        resourcePacks.Save();

        if (libraryItem is not null)
            defaults.SetChoice(packId, libraryItem.Key, ContentDefaultChoice.Excluded);
        return dest;
    }

    /// <summary>Moves an always-on resource pack back into the instance's <c>resourcepacks/</c>
    /// folder and switches it on there, at the top, so it keeps loading.</summary>
    /// <returns>The pack's file name in <c>resourcepacks/</c>.</returns>
    public string MoveToResourcePacks(Guid packId, string packName, string? minecraftVersion, GlobalPackEntry entry)
    {
        EnsureWritable(packId);
        var gameDir = GameDirOf(packId, packName);
        var dir = Path.Combine(gameDir, "resourcepacks");
        Directory.CreateDirectory(dir);
        var dest = ShaderPackService.NextFreePath(dir, entry.FileName);
        if (entry.IsFolder) CopyDirectory(entry.FilePath, dest);
        else File.Copy(entry.FilePath, dest);

        Remove(packId, packName, entry);
        var name = Path.GetFileName(dest);
        resourcePacks.SetEnabled(packId, packName, name, enabled: true, minecraftVersion);
        return name;
    }

    private static void DeleteEntry(string gameDir, GlobalPackEntry entry)
    {
        var dir = PathSafety.ResolveInside(gameDir, entry.Folder.RelativeDir)
                  ?? throw new GlobalPackRefusedException("That pack is not inside the instance.");
        var path = PathSafety.ResolveFileName(dir, entry.FileName)
                   ?? throw new GlobalPackRefusedException($"'{entry.FileName}' is not a plain file name.");
        if (Directory.Exists(path))
        {
            if (PathSafety.CrossesLink(dir, path))
                throw new GlobalPackRefusedException($"Refused to delete {path}: it is a link to somewhere else.");
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var sub in Directory.EnumerateDirectories(source))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }
}

/// <summary>An always-on pack change the launcher refused, with the reason written for the user.</summary>
public sealed class GlobalPackRefusedException(string message) : InvalidOperationException(message);
