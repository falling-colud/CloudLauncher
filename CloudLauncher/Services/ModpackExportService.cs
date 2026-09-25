using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>The two modpack formats an instance can be written out as.</summary>
public enum ModpackExportFormat
{
    /// <summary>A CurseForge modpack: a zip whose <c>manifest.json</c> lists CurseForge project and
    /// file ids.</summary>
    CurseForge,

    /// <summary>A Modrinth modpack (<c>.mrpack</c>): <c>modrinth.index.json</c> lists CDN downloads
    /// with hashes.</summary>
    Modrinth
}

/// <summary>Everything one export needs to know.</summary>
/// <param name="Pack">The instance. Its Minecraft version and loader become the pack's
/// dependencies.</param>
/// <param name="Overrides">Which files under <c>game/</c> are copied into <c>overrides/</c>: whole
/// folders, single files, or a folder minus some of its contents (see
/// <see cref="ExportSelection"/>). Never includes <c>mods</c>; jars are decided by the two flags
/// below.</param>
/// <param name="IncludeDisabledMods">Take <c>.jar.disabled</c> files along, still switched off.</param>
/// <param name="IncludeLocalMods">Take this PC's own jars from <c>local/mods</c> along, as ordinary
/// mods.</param>
/// <param name="DestinationPath">The archive to write. Written to a temp file beside it and moved
/// into place at the end, so a failed or cancelled export never leaves a half-written file
/// here.</param>
public sealed record ExportOptions(
    PackDetail Pack,
    ModpackExportFormat Format,
    string Name,
    string Version,
    string? Author,
    string? Summary,
    ExportSelection Overrides,
    bool IncludeDisabledMods,
    bool IncludeLocalMods,
    string DestinationPath);

/// <summary>One step of an export, for a progress bar.</summary>
/// <param name="Fraction">0 to 1, or negative while the step has no measurable length (asking the
/// stores).</param>
/// <param name="Detail">The file being worked on, when there is one.</param>
public sealed record ExportProgress(double Fraction, string Step, string? Detail = null);

/// <summary>Mods the chosen store could not be asked about, reported to the caller before anything
/// is written.</summary>
/// <param name="Store">"Modrinth" or "CurseForge".</param>
/// <param name="Unchecked">How many jars will be packed in because it is unknown whether the store
/// has them.</param>
public sealed record ExportIdentityGap(string Store, int Unchecked);

/// <summary>What the stores said about one jar.</summary>
public sealed class ExportModEntry
{
    /// <summary>Absolute path on disk; may end <c>.jar.disabled</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The name on disk, including any <c>.disabled</c>. A packed-in copy keeps it.</summary>
    public required string FileName { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>This PC's own mod: from <c>local/mods</c>, or the copy of one the launch overlay made in
    /// <c>game/mods</c>.</summary>
    public required bool IsLocal { get; init; }

    public long Size { get; init; }

    /// <summary>Null when the jar could not be read at all.</summary>
    public string? Sha512 { get; set; }

    public long CurseForgeFingerprint { get; set; }

    public (ModSummary Mod, ModVersion Version)? Modrinth { get; set; }
    public (ModSummary Mod, ModVersion Version)? CurseForge { get; set; }

    /// <summary>The Modrinth file whose SHA-512 is this jar's, on a host a .mrpack may download from.
    /// Null means the jar is packed into a Modrinth export rather than listed.</summary>
    public ModVersionFile? ModrinthFile { get; set; }

    /// <summary>The CurseForge project and file this jar is listed as. Null means it is packed into a
    /// CurseForge export rather than listed.</summary>
    public (int ProjectId, int FileId)? CurseForgeIds { get; set; }

    /// <summary>True when <see cref="CurseForgeIds"/> points to CurseForge's file of the same name
    /// rather than these exact bytes (see <see cref="ModpackExportService"/>'s remarks).</summary>
    public bool CurseForgeByName { get; set; }

    /// <summary>The folder of <c>game/</c> it lives in: <c>mods</c>, or for a pack <c>resourcepacks</c> or
    /// <c>shaderpacks</c>.</summary>
    public string Folder { get; init; } = "mods";

    /// <summary>Where it sits under <c>game/</c>, with forward slashes, e.g.
    /// <c>resourcepacks/Fresh.zip</c>. Used by the file selection and as a .mrpack <c>path</c>.</summary>
    public string GamePath => Folder + "/" + FileName;

    /// <summary>A pack under a private path (<see cref="PrivateAssetPolicy"/>): neither listed nor copied,
    /// whatever the selection says.</summary>
    public bool IsPrivate { get; init; }

    /// <summary>Listed from a CurseForge project whose author turned off third-party distribution. The
    /// CurseForge app downloads it; other launchers ask the user to fetch it by hand.</summary>
    public bool CurseForgeAppOnly { get; set; }

    /// <summary>The CurseForge project's name, when it was asked for: what the dialog calls it.</summary>
    public string? CurseForgeName { get; set; }

    public ModSide Side { get; set; }

    /// <summary>The name a listed copy is installed under: always <c>.jar</c>.</summary>
    public string EnabledFileName =>
        FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? FileName[..^".disabled".Length] : FileName;
}

/// <summary>How the mods of one export split up.</summary>
/// <param name="Listed">Enabled jars the archive names, for the importing launcher to download.</param>
/// <param name="Bundled">Enabled jars packed into <c>overrides/mods</c> because the store does not
/// have them.</param>
/// <param name="DisabledListed">CurseForge only: disabled jars listed as optional
/// (<c>required: false</c>).</param>
/// <param name="DisabledBundled">Disabled jars packed in under their <c>.disabled</c> name.</param>
/// <param name="DisabledLeftOut">Disabled jars not exported because the option was off.</param>
/// <param name="LocalLeftOut">This PC's own jars not exported because the option was off.</param>
/// <param name="Unchecked">The part of <see cref="Bundled"/> and <see cref="DisabledBundled"/>
/// packed in only because the store could not be asked. Zero when the store answered.</param>
/// <param name="ListedByName">CurseForge only: the part of <see cref="Downloaded"/> listed as
/// CurseForge's file of the same name rather than these exact bytes.</param>
public readonly record struct ExportCounts(
    int Listed,
    int Bundled,
    int DisabledListed,
    int DisabledBundled,
    int DisabledLeftOut,
    int LocalLeftOut,
    int Unchecked,
    int ListedByName = 0)
{
    /// <summary>Everything that goes into the file as a jar rather than as a download.</summary>
    public int PackedIn => Bundled + DisabledBundled;

    /// <summary>Everything the archive names for the importing launcher to fetch.</summary>
    public int Downloaded => Listed + DisabledListed;
}

/// <summary>Every jar in an instance, with what each store said about it, resolved once so the options
/// can be flipped without asking again.</summary>
public sealed class ExportPlan(
    IReadOnlyList<ExportModEntry> mods, IReadOnlyList<ExportModEntry> packs, bool modrinthComplete, bool curseForgeComplete)
{
    public IReadOnlyList<ExportModEntry> Mods { get; } = mods;

    /// <summary>The zips at the top of <c>resourcepacks</c> and <c>shaderpacks</c>, with what each store
    /// said about them. Whether one goes into the pack at all is the file selection's decision; the plan
    /// only knows whether it can be listed instead of copied.</summary>
    public IReadOnlyList<ExportModEntry> Packs { get; } = packs;

    /// <summary>False when Modrinth could not be asked, so a jar with no Modrinth match may still be on
    /// Modrinth. See <see cref="InstalledModIndex.Complete"/>.</summary>
    public bool ModrinthComplete { get; } = modrinthComplete;

    /// <summary>The same, for CurseForge.</summary>
    public bool CurseForgeComplete { get; } = curseForgeComplete;

    public bool CompleteFor(ModpackExportFormat format) =>
        format == ModpackExportFormat.Modrinth ? ModrinthComplete : CurseForgeComplete;

    public int DisabledCount => Mods.Count(m => !m.Enabled);
    public int LocalCount => Mods.Count(m => m.IsLocal);

    /// <summary>Whether this jar goes into an export with these options at all.</summary>
    public static bool Included(ExportModEntry mod, bool includeDisabled, bool includeLocal) =>
        (includeLocal || !mod.IsLocal) && (includeDisabled || mod.Enabled);

    /// <summary>True when the archive names this jar instead of carrying it.</summary>
    /// <remarks>A disabled jar is never listed in a .mrpack, whose <c>files[]</c> entries install as
    /// working mods; it is packed in under its <c>.disabled</c> name. In a CurseForge manifest it is
    /// listed as <c>required: false</c> rather than also copied into <c>overrides/</c>, which would
    /// duplicate it (and CurseForge refuses packs carrying third-party jars). Packs from
    /// <see cref="ExportPlan.Packs"/> follow the same rules.</remarks>
    public static bool IsListed(ExportModEntry mod, ModpackExportFormat format) =>
        format == ModpackExportFormat.Modrinth
            ? mod.Enabled && mod.ModrinthFile is not null
            : mod.CurseForgeIds is not null;

    public ExportCounts Count(ModpackExportFormat format, bool includeDisabled, bool includeLocal)
    {
        int listed = 0, bundled = 0, disabledListed = 0, disabledBundled = 0, disabledOut = 0, localOut = 0, unchecked_ = 0;
        var byName = 0;
        var complete = CompleteFor(format);
        foreach (var mod in Mods)
        {
            if (mod.IsLocal && !includeLocal) { localOut++; continue; }
            if (!mod.Enabled && !includeDisabled) { disabledOut++; continue; }

            if (IsListed(mod, format))
            {
                if (mod.Enabled) listed++; else disabledListed++;
                if (format == ModpackExportFormat.CurseForge && mod.CurseForgeByName) byName++;
                continue;
            }

            if (mod.Enabled) bundled++; else disabledBundled++;
            // Only jars the store never answered about count as unchecked. Disabled jars in a .mrpack are
            // packed in regardless, and a match with no usable file was still checked.
            var identity = format == ModpackExportFormat.Modrinth ? mod.Modrinth : mod.CurseForge;
            var packedAnyway = format == ModpackExportFormat.Modrinth && !mod.Enabled;
            if (!complete && identity is null && !packedAnyway) unchecked_++;
        }
        return new ExportCounts(listed, bundled, disabledListed, disabledBundled, disabledOut, localOut, unchecked_, byName);
    }

    /// <summary>The packs the file selection takes, listed and copied, for the format.</summary>
    public (int Listed, int Copied) CountPacks(ModpackExportFormat format, ExportSelection selection)
    {
        int listed = 0, copied = 0;
        foreach (var pack in Packs)
        {
            if (pack.IsPrivate || !selection.IsIncluded(pack.GamePath)) continue;
            if (IsListed(pack, format)) listed++; else copied++;
        }
        return (listed, copied);
    }
}

/// <summary>What an export wrote.</summary>
/// <param name="OverrideFiles">Files copied from the ticked folders (packed-in jars not
/// included).</param>
/// <param name="PrivateHeldBack">Files under a private path (<see cref="PrivateAssetPolicy"/>) left
/// out although their folder was ticked.</param>
/// <param name="Unreadable">Override files that could not be opened and were skipped, relative to
/// <c>game/</c>.</param>
/// <param name="BundledMods">File names of every jar packed into <c>overrides/mods</c>.</param>
/// <param name="ListedPacks">Resource packs and shader packs listed for download instead of copied,
/// as <c>resourcepacks/...</c> paths.</param>
/// <param name="ListedByName">CurseForge only: mods listed as CurseForge's file of the same name
/// rather than these exact bytes.</param>
/// <param name="CurseForgeAppOnly">CurseForge only: listed projects whose authors turned off
/// third-party distribution. The CurseForge app installs them; other launchers ask for them by
/// hand.</param>
public sealed record ExportResult(
    string Path,
    long Bytes,
    ModpackExportFormat Format,
    ExportCounts Mods,
    int OverrideFiles,
    int PrivateHeldBack,
    IReadOnlyList<string> Unreadable,
    IReadOnlyList<string> BundledMods,
    IReadOnlyList<string>? ListedPacks = null,
    IReadOnlyList<string>? ListedByName = null,
    IReadOnlyList<string>? CurseForgeAppOnly = null);

/// <summary>A top-level entry of <c>game/</c> the export can take along.</summary>
/// <param name="Recommended">Ticked when the dialog opens (see
/// <see cref="ModpackExportService.RecommendedOverrides"/>).</param>
public sealed record ExportCandidate(string Name, string FullPath, bool IsFolder, bool Recommended);

/// <summary>Writes an instance out as a CurseForge <c>.zip</c> or a Modrinth <c>.mrpack</c>.</summary>
/// <remarks>
/// <para>Mods are identified at export time by hash (<see cref="InstalledModResolver"/>), not from
/// a page's possibly stale list. Jars the chosen store has are listed for download and the rest are
/// packed into <c>overrides/mods</c>; resource pack and shader zips are treated the same way. A
/// store that could not be reached is asked again (<see cref="RecheckModrinthAsync"/>), and any
/// remaining gap is reported before anything is written.</para>
/// <para>For CurseForge, a mod built separately per store is listed as CurseForge's file of the same
/// name when the match is certain; otherwise it is packed in.</para>
/// <para>Selected files go into <c>overrides/</c> minus <see cref="PrivateAssetPolicy"/> paths.
/// Files are opened with <c>FileShare.ReadWrite | Delete</c> so a running game is never blocked, and
/// the archive is written beside its destination and moved into place when complete.</para>
/// </remarks>
public sealed class ModpackExportService(
    PackFolderService packs,
    ModFingerprintCache fingerprints,
    ModrinthService modrinth,
    CurseForgeService curseForge,
    ModMetadataService metadata,
    AppSettings settings)
{
    /// <summary>Hosts a .mrpack may name in <c>downloads</c>. Modrinth and launchers following its
    /// rules refuse others, so a file hosted elsewhere is packed in.</summary>
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"
    };

    /// <summary>Folders ticked when the dialog opens: what a modpack is made of besides its
    /// mods.</summary>
    /// <remarks>An allow list: unknown folders are more often caches or map data than pack content, and
    /// an export goes to other people, so they are offered unticked with their size.</remarks>
    public static readonly IReadOnlySet<string> RecommendedOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks",
        "global_packs", "datapacks", "openloader", "paxi", "resources", "patchouli_books"
    };

    /// <summary>Indented, with <c>+</c> and accents left as they are instead of numeric escapes (the
    /// default encoder escapes the <c>+</c> in <c>Adorn-6.3.1+1.21.1.jar</c>), as Modrinth and
    /// CurseForge write it. Still strict JSON, and never embedded in HTML.</summary>
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Extensions already compressed, stored rather than deflated a second time.</summary>
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jar", ".zip", ".disabled", ".png", ".jpg", ".jpeg", ".ogg", ".mrpack", ".gz", ".7z", ".webp"
    };

    // ── the options the dialog offers ────────────────────────────────────────

    /// <summary>The instance's game folder, or null when this PC has none yet (e.g. a shared instance
    /// never downloaded), where <see cref="PackFolderService.GameDir(Guid)"/> would throw.</summary>
    public string? TryGameDir(Guid packId)
    {
        try
        {
            var dir = packs.GameDir(packId);
            return Directory.Exists(dir) ? dir : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The top-level entries of <c>game/</c> an export can take, folders first.</summary>
    /// <remarks><c>mods</c> is left out because jars are decided per jar, not copied as a folder.</remarks>
    public IReadOnlyList<ExportCandidate> ListCandidates(Guid packId)
    {
        var gameDir = packs.GameDir(packId);
        if (!Directory.Exists(gameDir)) return Array.Empty<ExportCandidate>();

        var list = new List<ExportCandidate>();
        foreach (var dir in Directory.EnumerateDirectories(gameDir))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "mods", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new ExportCandidate(name, dir, true, RecommendedOverrides.Contains(name)));
        }
        foreach (var file in Directory.EnumerateFiles(gameDir))
        {
            var name = Path.GetFileName(file);
            list.Add(new ExportCandidate(name, file, false, RecommendedOverrides.Contains(name)));
        }

        return list
            .OrderByDescending(c => c.IsFolder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The paths this PC never lets leave it, for the card to mark in its file tree. The export
    /// checks them again itself, file by file, whatever the card shows.</summary>
    public IReadOnlyList<string> PrivatePatterns() => PrivateAssetPolicy.Patterns(settings);

    /// <summary>Every jar the export would consider, before any store is asked. Cheap: one folder listing
    /// each for <c>game/mods</c> and <c>local/mods</c>.</summary>
    public IReadOnlyList<ExportModEntry> ScanMods(Guid packId)
    {
        var gameMods = Path.Combine(packs.GameDir(packId), "mods");
        var localMods = Path.Combine(packs.LocalDir(packId), "mods");

        var game = ListJars(gameMods);
        var local = ListJars(localMods);
        var localByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, name, _, _) in local) localByName[name] = path;
        var gameNames = new HashSet<string>(game.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);

        var result = new List<ExportModEntry>(game.Count + local.Count);
        foreach (var (path, name, enabled, size) in game)
        {
            // The launch overlay hard-links local/ jars into game/ under the same name, so a game/ jar
            // that is the same file as a local/ one is this PC's own mod and follows that option.
            var isOverlayCopy = localByName.TryGetValue(name, out var localPath)
                                && PackFolderService.PathsReferToSameFile(localPath, path);
            result.Add(new ExportModEntry
            {
                Path = path, FileName = name, Enabled = enabled, IsLocal = isOverlayCopy, Size = size
            });
        }

        foreach (var (path, name, enabled, size) in local)
        {
            // Same name in game/: either the overlay's copy (counted above) or an instance file the
            // overlay won't replace, in which case this one never loads.
            if (gameNames.Contains(name)) continue;
            result.Add(new ExportModEntry
            {
                Path = path, FileName = name, Enabled = enabled, IsLocal = true, Size = size
            });
        }

        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName));
        return result;
    }

    private static List<(string Path, string Name, bool Enabled, long Size)> ListJars(string dir)
    {
        var list = new List<(string, string, bool, long)>();
        if (!Directory.Exists(dir)) return list;
        // Top level only, like the Mods page. Subfolders of mods/ are per-player caches (MCEF's Chromium
        // natives, Connector's remapped jars) that no loader reads as mods and no pack should carry.
        foreach (var info in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            var name = info.Name;
            bool enabled;
            if (name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) enabled = false;
            else if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) enabled = true;
            else continue;

            long size = 0;
            try { size = info.Length; } catch { /* locked; the hash pass will say so */ }
            list.Add((info.FullName, name, enabled, size));
        }
        return list;
    }

    /// <summary>The folders whose zips can be listed, each with the CurseForge class a file listed from it
    /// must have: an importing launcher puts a listed file wherever its project's class says.</summary>
    private static readonly IReadOnlyDictionary<string, int> PackFolders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["resourcepacks"] = CurseForgeService.ClassIdResourcePacks,
        ["shaderpacks"] = CurseForgeService.ClassIdShaders
    };

    /// <summary>The zips at the top of <c>resourcepacks</c> and <c>shaderpacks</c>, which an export can
    /// list instead of copying. Unzipped packs and shader settings are never store files, so they are
    /// left to the selection.</summary>
    public IReadOnlyList<ExportModEntry> ScanPacks(Guid packId)
    {
        var gameDir = packs.GameDir(packId);
        var result = new List<ExportModEntry>();
        foreach (var folder in PackFolders.Keys)
        {
            var dir = Path.Combine(gameDir, folder);
            if (!Directory.Exists(dir)) continue;
            // "*" and an explicit check rather than "*.zip": Windows matches a three-letter extension
            // pattern against longer extensions too.
            foreach (var info in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (!info.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var gamePath = folder + "/" + info.Name;
                if (ExportTree.IsPartial(gamePath)) continue;
                long size = 0;
                try { size = info.Length; } catch { /* locked; the hash pass will say so */ }
                result.Add(new ExportModEntry
                {
                    Path = info.FullName, FileName = info.Name, Enabled = true, IsLocal = false, Size = size,
                    Folder = folder, IsPrivate = PrivateAssetPolicy.IsPrivate(gamePath, settings)
                });
            }
        }
        return result;
    }

    // ── resolving ────────────────────────────────────────────────────────────

    /// <summary>Resolves every jar and pack against both stores, for the dialog's running summary.</summary>
    public Task<ExportPlan> PlanAsync(PackDetail pack, CancellationToken ct = default) =>
        BuildPlanAsync(pack, null, ct);

    /// <param name="only">The store the export is for, so a failed lookup at the other one is not
    /// retried for nothing. Null asks both.</param>
    private async Task<ExportPlan> BuildPlanAsync(PackDetail pack, ModpackExportFormat? only, CancellationToken ct)
    {
        var (mods, packFiles) = await Task.Run(() => (ScanMods(pack.Id), ScanPacks(pack.Id)), ct);
        var all = mods.Concat(packFiles).ToList();
        if (all.Count == 0) return new ExportPlan(mods, packFiles, true, true);

        var index = await InstalledModResolver.ResolveAsync(all.Select(m => m.Path).ToList(),
            fingerprints, modrinth, curseForge, ct);
        var byPath = new Dictionary<string, InstalledModIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in index.Identities) byPath[id.Path] = id;

        foreach (var mod in all)
        {
            // The resolver has just hashed (or re-validated) every jar, so this is a dictionary read.
            if (fingerprints.TryGet(mod.Path, out var entry))
            {
                mod.Sha512 = string.IsNullOrEmpty(entry.Sha512) ? null : entry.Sha512;
                mod.CurseForgeFingerprint = entry.CurseForgeFingerprint;
            }
            if (byPath.TryGetValue(mod.Path, out var identity))
            {
                mod.Modrinth = identity.Modrinth;
                mod.CurseForge = identity.CurseForge;
            }
        }

        var modrinthComplete = index.Complete;
        var curseForgeComplete = index.Complete;
        if (!index.Complete)
        {
            // The resolver only reports that some store failed. Ask each relevant one again on its own,
            // so the answer is per store and a transient failure gets a retry.
            if (only is null or ModpackExportFormat.Modrinth)
                modrinthComplete = await RecheckModrinthAsync(all, ct);
            if (only is null or ModpackExportFormat.CurseForge)
                curseForgeComplete = await RecheckCurseForgeAsync(all, ct);
        }

        foreach (var mod in all)
        {
            mod.ModrinthFile = mod.Modrinth is { } mr && mod.Sha512 is { } sha
                ? mr.Version.Files.FirstOrDefault(f =>
                    string.Equals(f.Sha512, sha, StringComparison.OrdinalIgnoreCase) && IsAllowedDownload(f.DownloadUrl))
                : null;
            mod.CurseForgeIds = mod.CurseForge is { } cf
                                && CurseForgeService.TryParseFileIds(cf.Mod, cf.Version, out var projectId, out var fileId)
                ? (projectId, fileId)
                : null;
            if (mod.Folder == "mods")
                mod.Side = metadata.EffectiveMeta(pack.Id,
                    ModMetadataService.CandidateKeys(mod.Modrinth?.Mod, mod.CurseForge?.Mod, mod.FileName), out _).Side;
        }

        if (only is null or ModpackExportFormat.CurseForge)
        {
            await MatchSameNameOnCurseForgeAsync(mods, pack, ct);
            await CheckCurseForgeProjectsAsync(all, ct);
        }

        return new ExportPlan(mods, packFiles, modrinthComplete, curseForgeComplete);
    }

    /// <summary>For each mod CurseForge does not have byte for byte: CurseForge's file of the same name,
    /// in the project the jar's Modrinth identity leads to, for this Minecraft version.</summary>
    /// <remarks>Kept narrow: the project must be the one the Modrinth identity names (via
    /// <see cref="CurseForgeService.FindCounterpartAsync"/>) and the file name must match the jar's. A
    /// file marked for this loader is preferred, since some mods give Forge and NeoForge builds one
    /// name. A failed lookup leaves the jar packed in.</remarks>
    private async Task MatchSameNameOnCurseForgeAsync(IReadOnlyList<ExportModEntry> mods, PackDetail pack, CancellationToken ct)
    {
        var mcVersion = pack.MinecraftVersion?.Trim();
        if (string.IsNullOrEmpty(mcVersion)) return;
        var loader = pack.Loader switch
        {
            LoaderKind.NeoForge => "neoforge",
            LoaderKind.Forge => "forge",
            LoaderKind.Fabric => "fabric",
            _ => null
        };

        foreach (var mod in mods)
        {
            if (mod.CurseForgeIds is not null || mod.Modrinth is not { } mr) continue;
            var key = $"{mr.Mod.Id}|{mod.EnabledFileName}|{mcVersion}|{loader}";
            if (!_sameName.TryGetValue(key, out var found))
            {
                try
                {
                    found = await FindSameNameOnCurseForgeAsync(mr.Mod, mod.EnabledFileName, mcVersion, loader, ct);
                    _sameName[key] = found;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    AppLog.Log("export", $"CurseForge's copy of {mod.FileName} could not be looked up: {ex.Message}");
                    continue;
                }
            }
            if (found is not { } hit || !CurseForgeService.TryParseFileIds(hit.Project, hit.File, out var pid, out var fid)) continue;

            mod.CurseForge = (hit.Project, hit.File);
            mod.CurseForgeIds = (pid, fid);
            mod.CurseForgeByName = true;
        }
    }

    /// <summary>Same-name results for this service's lifetime (one export dialog, where the preview and
    /// the export both plan). A miss is stored as null; a failed lookup is not stored, so it is
    /// retried.</summary>
    private readonly ConcurrentDictionary<string, (ModSummary Project, ModVersion File)?> _sameName =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task<(ModSummary Project, ModVersion File)?> FindSameNameOnCurseForgeAsync(
        ModSummary modrinthProject, string fileName, string mcVersion, string? loader, CancellationToken ct)
    {
        var project = await curseForge.FindCounterpartAsync(modrinthProject.Slug, modrinthProject.Name, ct);
        if (project is null || !int.TryParse(project.Id, out var projectId)) return null;

        foreach (var byLoader in loader is null ? new string?[] { null } : new string?[] { loader, null })
        {
            var files = await curseForge.GetVersionsAsync(projectId, mcVersion, byLoader, maxPages: 4, ct);
            var same = files.FirstOrDefault(v => v.Files.Any(f =>
                string.Equals(f.Filename, fileName, StringComparison.OrdinalIgnoreCase)));
            if (same is not null) return (project, same);
        }
        return null;
    }

    /// <summary>Asks CurseForge, in one batch, about the project behind every file it would list:
    /// whether a pack's project has the right class for the pack's folder, and which projects only the
    /// CurseForge app may download.</summary>
    /// <remarks>A pack whose class can't be checked is copied instead of listed. If the request fails,
    /// mods stay listed without the "CurseForge app only" note.</remarks>
    private async Task CheckCurseForgeProjectsAsync(IReadOnlyList<ExportModEntry> entries, CancellationToken ct)
    {
        var matched = entries.Where(e => e.CurseForgeIds is not null).ToList();
        if (matched.Count == 0) return;

        Dictionary<int, CurseForgeProjectFacts> projects;
        try { projects = await curseForge.GetProjectFactsAsync(matched.Select(e => e.CurseForgeIds!.Value.ProjectId), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Log("export", $"CurseForge could not say what {matched.Count} project(s) are: {ex.Message}");
            projects = new();
        }

        foreach (var entry in matched)
        {
            var known = projects.TryGetValue(entry.CurseForgeIds!.Value.ProjectId, out var facts);
            if (entry.Folder != "mods" && (!known || facts.ClassId != PackFolders[entry.Folder]))
            {
                entry.CurseForgeIds = null;
                continue;
            }
            if (!known) continue;
            entry.CurseForgeAppOnly = !facts.AllowsDistribution;
            entry.CurseForgeName = facts.Name;
        }
    }

    /// <summary>Asks Modrinth about every jar that has no Modrinth identity yet. True when it answered.</summary>
    private async Task<bool> RecheckModrinthAsync(IReadOnlyList<ExportModEntry> mods, CancellationToken ct)
    {
        var missing = mods.Where(m => m.Modrinth is null && m.Sha512 is not null).ToList();
        if (missing.Count == 0) return true;
        try
        {
            var found = new Dictionary<string, (ModSummary mod, ModVersion version)>(
                await modrinth.MatchHashesAsync(missing.Select(m => m.Sha512!), ct), StringComparer.OrdinalIgnoreCase);
            foreach (var mod in missing)
            {
                if (!found.TryGetValue(mod.Sha512!, out var match) || match.mod.Source != ModSource.Modrinth) continue;
                mod.Modrinth = (match.mod, match.version);
                fingerprints.RememberModrinthMatch(mod.Sha512!, match.mod, match.version);
                fingerprints.StoreStoreMatches(mod.Path, (match.mod, match.version), null);
            }
            fingerprints.Flush();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Log("export", $"Modrinth could not be asked about {missing.Count} jar(s): {ex.Message}");
            return false;
        }
    }

    /// <summary>The same second opinion, from CurseForge by fingerprint.</summary>
    private async Task<bool> RecheckCurseForgeAsync(IReadOnlyList<ExportModEntry> mods, CancellationToken ct)
    {
        var missing = mods.Where(m => m.CurseForge is null && m.CurseForgeFingerprint != 0).ToList();
        if (missing.Count == 0) return true;
        try
        {
            var found = await curseForge.MatchFingerprintsAsync(missing.Select(m => m.CurseForgeFingerprint), ct);
            foreach (var mod in missing)
            {
                if (!found.TryGetValue(mod.CurseForgeFingerprint, out var match) || match.mod.Source != ModSource.CurseForge) continue;
                mod.CurseForge = (match.mod, match.version);
                fingerprints.RememberCurseForgeMatch(mod.CurseForgeFingerprint, match.mod, match.version);
                fingerprints.StoreStoreMatches(mod.Path, null, (match.mod, match.version));
            }
            fingerprints.Flush();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Log("export", $"CurseForge could not be asked about {missing.Count} jar(s): {ex.Message}");
            return false;
        }
    }

    private static bool IsAllowedDownload(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && AllowedDownloadHosts.Contains(uri.Host);

    public static string StoreName(ModpackExportFormat format) =>
        format == ModpackExportFormat.Modrinth ? "Modrinth" : "CurseForge";

    // ── exporting ────────────────────────────────────────────────────────────

    /// <summary>Resolves the instance again and writes the archive.</summary>
    /// <param name="confirmGap">Asked when the chosen store could not be reached, before anything is
    /// written: true packs the unchecked jars in, false cancels. Without it the export carries on, and
    /// the result's <see cref="ExportCounts.Unchecked"/> says how many that was.</param>
    /// <exception cref="OperationCanceledException">Cancelled, or the gap was declined. Nothing is left
    /// on disk either way.</exception>
    /// <exception cref="InvalidOperationException">The instance cannot be described by either format
    /// (no Minecraft version, an unknown loader version) or a field is empty. The message says
    /// which.</exception>
    public async Task<ExportResult> ExportAsync(
        ExportOptions options,
        IProgress<ExportProgress>? progress,
        CancellationToken ct,
        Func<ExportIdentityGap, CancellationToken, Task<bool>>? confirmGap = null)
    {
        Validate(options);
        var report = new Reporter(progress);
        var store = StoreName(options.Format);

        report.Now(-1, $"Checking which mods are on {store}...");
        var plan = await BuildPlanAsync(options.Pack, options.Format, ct);
        var counts = plan.Count(options.Format, options.IncludeDisabledMods, options.IncludeLocalMods);

        if (counts.Unchecked > 0 && confirmGap is not null
            && !await confirmGap(new ExportIdentityGap(store, counts.Unchecked), ct))
            throw new OperationCanceledException("The export was cancelled before anything was written.");

        var mods = plan.Mods
            .Where(m => ExportPlan.Included(m, options.IncludeDisabledMods, options.IncludeLocalMods))
            .ToList();
        // Packs the selection takes and the store has: listed instead of copied (the copy is skipped).
        var listedPacks = plan.Packs
            .Where(p => !p.IsPrivate && options.Overrides.IsIncluded(p.GamePath) && ExportPlan.IsListed(p, options.Format))
            .ToList();

        var result = await Task.Run(() => Write(options, mods, listedPacks, counts, report, ct), ct);
        AppLog.Log("export",
            $"Exported {options.Pack.Name} as {store} to {result.Path}: {result.Mods.Downloaded} listed "
            + $"({result.Mods.ListedByName} as {store}'s copy by name), {result.Mods.PackedIn} packed in "
            + $"({result.Mods.Unchecked} unchecked), {result.ListedPacks?.Count ?? 0} pack(s) listed, "
            + $"{result.OverrideFiles} override file(s), {result.PrivateHeldBack} private held back, "
            + $"{result.Unreadable.Count} unreadable.");
        return result;
    }

    private static void Validate(ExportOptions o)
    {
        if (o.Pack.IsEmpty || string.IsNullOrWhiteSpace(o.Pack.MinecraftVersion))
            throw new InvalidOperationException(
                "This instance has no Minecraft version yet, so there is nothing a launcher could install from it.");
        if (o.Pack.Loader != LoaderKind.None && string.IsNullOrWhiteSpace(o.Pack.LoaderVersion))
            throw new InvalidOperationException(
                $"This instance's {o.Pack.Loader} version is not known yet, and both formats have to name it.");
        if (string.IsNullOrWhiteSpace(o.Name)) throw new InvalidOperationException("Give the modpack a name.");
        if (string.IsNullOrWhiteSpace(o.Version)) throw new InvalidOperationException("Give the modpack a version.");
        if (string.IsNullOrWhiteSpace(o.DestinationPath)) throw new InvalidOperationException("Choose where to save it.");
    }

    /// <summary>One file that goes into <c>overrides/</c>.</summary>
    private sealed record OverrideFile(string FullPath, string EntryName, long Size, bool IsMod);

    private ExportResult Write(ExportOptions o, List<ExportModEntry> mods, List<ExportModEntry> listedPacks,
                               ExportCounts counts, Reporter report, CancellationToken ct)
    {
        var gameDir = packs.GameDir(o.Pack.Id);
        var dest = Path.GetFullPath(o.DestinationPath);
        var dir = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(dir);
        // Beside the destination rather than in %TEMP%, so the final move is a same-volume rename.
        // The leading dot marks it as temporary.
        var temp = Path.Combine(dir, $".{Path.GetFileName(dest)}.{Guid.NewGuid():N}.part");

        try
        {
            var listedMr = new List<MrFileOut>();
            var listedCf = new Dictionary<(int, int), CfFileOut>();
            // The mod behind each CurseForge entry, for modlist.html.
            var listedCfMods = new Dictionary<(int, int), ExportModEntry>();
            var overrides = new List<OverrideFile>();
            var bundled = new List<string>();

            // ── the jars ──
            var toHash = new List<ExportModEntry>();
            foreach (var mod in mods)
            {
                if (!ExportPlan.IsListed(mod, o.Format))
                {
                    overrides.Add(new OverrideFile(mod.Path, "overrides/mods/" + mod.FileName, mod.Size, true));
                    bundled.Add(mod.FileName);
                    continue;
                }

                if (o.Format == ModpackExportFormat.Modrinth) { toHash.Add(mod); continue; }

                var ids = mod.CurseForgeIds!.Value;
                // One entry per file: a jar kept twice (x.jar beside x.jar.disabled) is one download, and
                // it is required if either copy was switched on.
                if (listedCf.TryGetValue(ids, out var existing))
                    existing.Required |= mod.Enabled;
                else
                {
                    listedCf[ids] = new CfFileOut { ProjectId = ids.Item1, FileId = ids.Item2, Required = mod.Enabled };
                    listedCfMods[ids] = mod;
                }
            }

            // ── the packs listed instead of copied ──
            // Their copies are skipped when the selection is collected below. A .mrpack entry is only
            // settled once the pack has been hashed again, so it is added to the skip list there.
            var skipCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packsListed = new List<string>();
            foreach (var pack in listedPacks)
            {
                if (o.Format == ModpackExportFormat.Modrinth) { toHash.Add(pack); continue; }
                var ids = pack.CurseForgeIds!.Value;
                if (listedCf.TryAdd(ids, new CfFileOut { ProjectId = ids.Item1, FileId = ids.Item2, Required = true }))
                    listedCfMods[ids] = pack;
                skipCopies.Add(pack.GamePath);
                packsListed.Add(pack.GamePath);
            }

            if (toHash.Count > 0)
            {
                // The format needs SHA-1, which the cache doesn't keep. SHA-512 is recomputed in the
                // same read, so the listed hash matches the jar as it is now, and it is checked against
                // the Modrinth file.
                var hashed = HashJars(toHash, report, ct);
                foreach (var mod in toHash)
                {
                    var (sha1, sha512) = hashed[mod.Path];
                    var file = mod.ModrinthFile!;
                    var isPack = mod.Folder != "mods";
                    if (!string.Equals(sha512, file.Sha512, StringComparison.OrdinalIgnoreCase))
                    {
                        // Changed since it was matched. Its current bytes are what the user has, so they
                        // are what ship: a jar packed in, a pack left to the selection to copy.
                        AppLog.Log("export", $"{mod.FileName} changed while exporting; {(isPack ? "copied" : "packed in")} instead of listed.");
                        if (isPack) continue;
                        overrides.Add(new OverrideFile(mod.Path, "overrides/mods/" + mod.FileName, mod.Size, true));
                        bundled.Add(mod.FileName);
                        counts = counts with { Listed = counts.Listed - 1, Bundled = counts.Bundled + 1 };
                        continue;
                    }
                    listedMr.Add(new MrFileOut
                    {
                        Path = isPack ? mod.GamePath : "mods/" + mod.EnabledFileName,
                        Hashes = new MrHashesOut { Sha1 = sha1, Sha512 = sha512 },
                        // Resource packs and shaders are the player's; a server has no use for them.
                        Env = isPack ? new MrEnvOut { Client = "required", Server = "unsupported" } : EnvFor(mod.Side),
                        Downloads = [file.DownloadUrl],
                        FileSize = new FileInfo(mod.Path).Length
                    });
                    if (isPack)
                    {
                        skipCopies.Add(mod.GamePath);
                        packsListed.Add(mod.GamePath);
                    }
                }
            }

            // ── the chosen files and folders ──
            var extra = CollectOverrides(gameDir, o.Overrides, dest, temp, skipCopies, out var privateHeld, ct);
            overrides.AddRange(extra);
            overrides.Sort((a, b) => string.CompareOrdinal(a.EntryName, b.EntryName));

            // ── the manifest ──
            byte[] manifestBytes;
            string manifestName;
            if (o.Format == ModpackExportFormat.Modrinth)
            {
                manifestName = "modrinth.index.json";
                manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new MrIndexOut
                {
                    VersionId = o.Version.Trim(),
                    Name = o.Name.Trim(),
                    Summary = string.IsNullOrWhiteSpace(o.Summary) ? null : o.Summary.Trim(),
                    Files = listedMr.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
                    Dependencies = MrDependencies(o.Pack)
                }, ManifestJson);
            }
            else
            {
                manifestName = "manifest.json";
                manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new CfManifestOut
                {
                    Minecraft = new CfMinecraftOut
                    {
                        Version = o.Pack.MinecraftVersion!.Trim(),
                        ModLoaders = CfLoaders(o.Pack)
                    },
                    Name = o.Name.Trim(),
                    Version = o.Version.Trim(),
                    Author = o.Author?.Trim() ?? "",
                    Files = listedCf.Values.OrderBy(f => f.ProjectId).ThenBy(f => f.FileId).ToList()
                }, ManifestJson);
            }

            // ── the archive ──
            var unreadable = new List<string>();
            var totalBytes = Math.Max(1, overrides.Sum(f => f.Size));
            long doneBytes = 0;
            var hashShare = o.Format == ModpackExportFormat.Modrinth && toHash.Count > 0 ? 0.3 : 0.0;
            var buffer = new byte[1 << 20];
            var written = 0;

            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                               1 << 16, FileOptions.SequentialScan))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifestEntry = zip.CreateEntry(manifestName, CompressionLevel.Optimal);
                manifestEntry.LastWriteTime = DateTimeOffset.Now;
                using (var m = manifestEntry.Open()) m.Write(manifestBytes);

                if (o.Format == ModpackExportFormat.CurseForge)
                {
                    var listEntry = zip.CreateEntry("modlist.html", CompressionLevel.Optimal);
                    listEntry.LastWriteTime = DateTimeOffset.Now;
                    using var l = listEntry.Open();
                    l.Write(ModListHtml(listedCfMods));
                }

                foreach (var file in overrides)
                {
                    ct.ThrowIfCancellationRequested();
                    FileStream src;
                    try
                    {
                        src = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A mod that cannot be read is a broken pack, so that stops the export. A config
                        // the game is holding exclusively is one file short, and is reported instead.
                        if (file.IsMod)
                            throw new IOException($"{Path.GetFileName(file.FullPath)} could not be read ({ex.Message}).", ex);
                        unreadable.Add(Path.GetRelativePath(gameDir, file.FullPath).Replace('\\', '/'));
                        doneBytes += file.Size;
                        continue;
                    }

                    using (src)
                    {
                        var entry = zip.CreateEntry(file.EntryName, LevelFor(file.EntryName));
                        entry.LastWriteTime = ZipTime(file.FullPath);
                        using var dst = entry.Open();
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            dst.Write(buffer, 0, read);
                            doneBytes += read;
                            report.Maybe(hashShare + (1 - hashShare) * Math.Min(1.0, doneBytes / (double)totalBytes),
                                file.IsMod ? "Packing mods" : "Packing files", file.EntryName["overrides/".Length..]);
                        }
                    }
                    written++;
                }
            }

            ct.ThrowIfCancellationRequested();
            report.Now(1, "Finishing...");
            File.Move(temp, dest, overwrite: true);

            return new ExportResult(
                dest,
                new FileInfo(dest).Length,
                o.Format,
                counts,
                written - bundled.Count,
                privateHeld,
                unreadable,
                bundled.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                packsListed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                o.Format == ModpackExportFormat.CurseForge
                    ? mods.Where(m => m.CurseForgeByName && m.CurseForgeIds is not null)
                        .Select(m => m.FileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                    : [],
                listedCfMods.Values.Where(e => e.CurseForgeAppOnly)
                    .Select(e => e.CurseForgeName ?? e.EnabledFileName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort; the dot name marks it as ours */ }
            throw;
        }
    }

    /// <summary>SHA-1 and SHA-512 of each jar, four at a time, reading each file once.</summary>
    private static Dictionary<string, (string Sha1, string Sha512)> HashJars(
        IReadOnlyList<ExportModEntry> jars, Reporter report, CancellationToken ct)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        try
        {
            HashAll();
        }
        catch (AggregateException ae)
        {
            // Parallel.ForEach wraps what its bodies threw. The caller shows the message, and "One or
            // more errors occurred" names neither the jar nor the reason.
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }
        return result;

        void HashAll() => Parallel.ForEach(jars, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, jar =>
        {
            (string, string) hashes;
            try
            {
                using var input = new FileStream(jar.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha1.AppendData(buffer, 0, read);
                    sha512.AppendData(buffer, 0, read);
                }
                hashes = (Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
                          Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{jar.FileName} could not be read ({ex.Message}).", ex);
            }

            lock (result) result[jar.Path] = hashes;
            var n = Interlocked.Increment(ref done);
            report.Maybe(0.3 * n / jars.Count, "Hashing mods", $"{n} of {jars.Count}");
        });
    }

    /// <summary>The files the selection takes from <c>game/</c>, each with its archive name.</summary>
    /// <remarks>Walks the disk rather than trusting the card's names, since this decides what leaves
    /// the machine; mixed folders are checked entry by entry. <paramref name="skip"/> holds packs
    /// listed for download instead (relative to <c>game/</c>), so they are not in the archive
    /// twice.</remarks>
    private List<OverrideFile> CollectOverrides(string gameDir, ExportSelection selection,
        string dest, string temp, IReadOnlySet<string> skip, out int privateHeld, CancellationToken ct)
    {
        var list = new List<OverrideFile>();
        var held = 0;
        foreach (var name in selection.TopLevelWithAnything())
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
                || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0
                || string.Equals(name, "mods", StringComparison.OrdinalIgnoreCase))
                continue;

            var full = Path.Combine(gameDir, name);
            if (File.Exists(full))
            {
                if (selection.IsIncluded(name)) Take(new FileInfo(full));
            }
            else if (Directory.Exists(full))
                Walk(new DirectoryInfo(full), name);
        }
        privateHeld = held;
        return list;

        void Walk(DirectoryInfo dir, string rel)
        {
            ct.ThrowIfCancellationRequested();
            switch (selection.StateOf(rel))
            {
                case false:
                    return;
                case true:
                    foreach (var info in dir.EnumerateFiles("*", WalkOptions)) Take(info);
                    return;
            }
            foreach (var info in dir.EnumerateFiles("*", OneLevelOptions))
                if (selection.IsIncluded(rel + "/" + info.Name)) Take(info);
            foreach (var sub in dir.EnumerateDirectories("*", OneLevelOptions))
                Walk(sub, rel + "/" + sub.Name);
        }

        void Take(FileInfo info)
        {
            ct.ThrowIfCancellationRequested();
            var path = info.FullName;
            if (string.Equals(path, dest, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, temp, StringComparison.OrdinalIgnoreCase))
                return;   // exporting into the instance's own folder must not swallow itself

            var rel = Path.GetRelativePath(gameDir, path).Replace('\\', '/');
            if (PrivateAssetPolicy.IsPrivate(rel, settings)) { held++; return; }
            // Skip half-downloaded files.
            if (ExportTree.IsPartial(rel)) return;
            if (skip.Contains(rel)) return;   // listed for download instead

            long size = 0;
            try { size = info.Length; } catch { /* vanished; the copy will say so */ }
            list.Add(new OverrideFile(path, "overrides/" + rel, size, false));
        }
    }

    /// <summary>The <c>modlist.html</c> a CurseForge zip carries beside its manifest: one
    /// "Name (by Author)" link per file the manifest names, as the CurseForge app and Prism write
    /// it.</summary>
    /// <remarks>Packed-in jars are not listed. Links use the project's slug when there is one, and the
    /// project id otherwise; CurseForge accepts both.</remarks>
    private static byte[] ModListHtml(IReadOnlyDictionary<(int ProjectId, int FileId), ExportModEntry> listed)
    {
        var rows = listed
            .Select(pair =>
            {
                var mod = pair.Value.CurseForge?.Mod;
                var name = string.IsNullOrWhiteSpace(mod?.Name) ? pair.Value.EnabledFileName : mod!.Name.Trim();
                var section = pair.Value.Folder switch
                {
                    "resourcepacks" => "texture-packs",
                    "shaderpacks" => "shaders",
                    _ => "mc-mods"
                };
                var url = mod?.Slug is { Length: > 0 } slug && IsSlug(slug)
                    ? $"https://www.curseforge.com/minecraft/{section}/{slug}"
                    : $"https://www.curseforge.com/projects/{pair.Key.ProjectId}";
                var by = string.IsNullOrWhiteSpace(mod?.Author) ? "" : $" (by {mod!.Author!.Trim()})";
                return (name, url, by);
            })
            .OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase);

        var html = new System.Text.StringBuilder("<ul>\n");
        foreach (var (name, url, by) in rows)
            html.Append("<li><a href=\"").Append(System.Net.WebUtility.HtmlEncode(url)).Append("\">")
                .Append(System.Net.WebUtility.HtmlEncode(name + by)).Append("</a></li>\n");
        html.Append("</ul>\n");
        return System.Text.Encoding.UTF8.GetBytes(html.ToString());

        // The CurseForge mapping falls back to the mod's name when a project has no slug, and a name
        // is not a valid path segment.
        static bool IsSlug(string s) => s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');
    }

    /// <summary>Recursive, but never through a junction or symlink, which could point anywhere on the
    /// machine. Includes hidden files, which the default options skip.</summary>
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary><see cref="WalkOptions"/>, one level at a time, for a folder only partly ticked.</summary>
    private static readonly EnumerationOptions OneLevelOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static CompressionLevel LevelFor(string entryName) =>
        StoredExtensions.Contains(Path.GetExtension(entryName)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;

    /// <summary>The file's own time, clamped to what a zip can record (1980 to 2107). Jars unpacked
    /// from other zips often carry 1980-01-01, and a few the Unix epoch, which the setter would
    /// refuse.</summary>
    private static DateTimeOffset ZipTime(string path)
    {
        DateTimeOffset at;
        try { at = File.GetLastWriteTime(path); }
        catch { at = DateTimeOffset.Now; }
        var min = new DateTimeOffset(1980, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var max = new DateTimeOffset(2107, 12, 30, 0, 0, 0, TimeSpan.Zero);
        return at < min ? min : at > max ? max : at;
    }

    /// <summary>The side flag on the Mods page, in the words a .mrpack uses for it.</summary>
    private static MrEnvOut EnvFor(ModSide side) => side switch
    {
        ModSide.Client => new MrEnvOut { Client = "required", Server = "unsupported" },
        ModSide.Server => new MrEnvOut { Client = "unsupported", Server = "required" },
        _ => new MrEnvOut { Client = "required", Server = "required" }
    };

    /// <summary>The loader's own version number, which is what both formats want.</summary>
    /// <remarks>Stored bare already (<c>21.1.247</c>), but prefixes like <c>1.21.1-</c> or
    /// <c>neoforge-</c> are stripped anyway, since they would make the pack unresolvable.</remarks>
    private static string BareLoaderVersion(PackDetail pack)
    {
        var version = (pack.LoaderVersion ?? "").Trim();
        foreach (var prefix in new[] { pack.MinecraftVersion + "-", "neoforge-", "forge-", "fabric-", "fabric-loader-" })
            if (prefix.Length > 1 && version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                version = version[prefix.Length..];
        return version;
    }

    private static Dictionary<string, string> MrDependencies(PackDetail pack)
    {
        // Minecraft first, then the loader: insertion order is the written order, and that is the order
        // Modrinth writes them in.
        var deps = new Dictionary<string, string>(StringComparer.Ordinal) { ["minecraft"] = pack.MinecraftVersion!.Trim() };
        var key = pack.Loader switch
        {
            LoaderKind.Fabric => "fabric-loader",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            _ => null
        };
        if (key is not null) deps[key] = BareLoaderVersion(pack);
        return deps;
    }

    private static List<CfLoaderOut> CfLoaders(PackDetail pack)
    {
        var prefix = pack.Loader switch
        {
            LoaderKind.Fabric => "fabric-",
            LoaderKind.Forge => "forge-",
            LoaderKind.NeoForge => "neoforge-",
            _ => null
        };
        return prefix is null ? [] : [new CfLoaderOut { Id = prefix + BareLoaderVersion(pack), Primary = true }];
    }

    /// <summary>Throttled progress, so thousands of small config files don't queue thousands of UI
    /// updates.</summary>
    private sealed class Reporter(IProgress<ExportProgress>? inner)
    {
        private readonly object _gate = new();
        private long _last;

        public void Now(double fraction, string step, string? detail = null)
        {
            if (inner is null) return;
            lock (_gate) _last = Stopwatch.GetTimestamp();
            inner.Report(new ExportProgress(fraction, step, detail));
        }

        public void Maybe(double fraction, string step, string? detail = null)
        {
            if (inner is null) return;
            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (Stopwatch.GetElapsedTime(_last, now) < TimeSpan.FromMilliseconds(80)) return;
                _last = now;
            }
            inner.Report(new ExportProgress(fraction, step, detail));
        }
    }

    // ── JSON shapes, as the formats spell them ───────────────────────────────

    private sealed class MrIndexOut
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; init; } = 1;
        [JsonPropertyName("game")] public string Game { get; init; } = "minecraft";
        [JsonPropertyName("versionId")] public required string VersionId { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("summary")] public string? Summary { get; init; }
        [JsonPropertyName("files")] public required List<MrFileOut> Files { get; init; }
        [JsonPropertyName("dependencies")] public required Dictionary<string, string> Dependencies { get; init; }
    }

    private sealed class MrFileOut
    {
        [JsonPropertyName("path")] public required string Path { get; init; }
        [JsonPropertyName("hashes")] public required MrHashesOut Hashes { get; init; }
        [JsonPropertyName("env")] public MrEnvOut? Env { get; init; }
        [JsonPropertyName("downloads")] public required List<string> Downloads { get; init; }
        [JsonPropertyName("fileSize")] public long FileSize { get; init; }
    }

    private sealed class MrHashesOut
    {
        [JsonPropertyName("sha1")] public required string Sha1 { get; init; }
        [JsonPropertyName("sha512")] public required string Sha512 { get; init; }
    }

    private sealed class MrEnvOut
    {
        [JsonPropertyName("client")] public required string Client { get; init; }
        [JsonPropertyName("server")] public required string Server { get; init; }
    }

    private sealed class CfManifestOut
    {
        [JsonPropertyName("minecraft")] public required CfMinecraftOut Minecraft { get; init; }
        [JsonPropertyName("manifestType")] public string ManifestType { get; init; } = "minecraftModpack";
        [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; init; } = 1;
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("version")] public required string Version { get; init; }
        [JsonPropertyName("author")] public required string Author { get; init; }
        [JsonPropertyName("files")] public required List<CfFileOut> Files { get; init; }
        [JsonPropertyName("overrides")] public string Overrides { get; init; } = "overrides";
    }

    private sealed class CfMinecraftOut
    {
        [JsonPropertyName("version")] public required string Version { get; init; }
        [JsonPropertyName("modLoaders")] public required List<CfLoaderOut> ModLoaders { get; init; }
    }

    private sealed class CfLoaderOut
    {
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("primary")] public bool Primary { get; init; }
    }

    private sealed class CfFileOut
    {
        [JsonPropertyName("projectID")] public int ProjectId { get; init; }
        [JsonPropertyName("fileID")] public int FileId { get; init; }
        [JsonPropertyName("required")] public bool Required { get; set; }
    }
}
