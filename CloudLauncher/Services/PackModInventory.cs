using System.ComponentModel;
using System.IO;

namespace CloudLauncher.Services;

/// <summary>
/// One installed mod, unified from its disk file, its resolved CurseForge/Modrinth identity,
/// and its saved flags. This is the single item type the Mod view, List view, Graph view and
/// Files tab all bind to — replacing the per-view re-scans so every surface agrees.
/// </summary>
public sealed class PackMod : INotifyPropertyChanged
{
    /// <summary>Absolute path on disk; may end <c>.jar</c> or <c>.jar.disabled</c>.</summary>
    public required string FilePath { get; set; }

    /// <summary>File name normalised to always end <c>.jar</c> (the <c>.disabled</c> suffix stripped).</summary>
    public required string FileName { get; init; }

    /// <summary>Which pack folder the jar lives in: <c>game</c> or <c>local</c>.</summary>
    public required string Folder { get; init; }

    public long Size { get; init; }

    /// <summary>When the jar landed in the pack folder (file creation time). Drives the "Last added" sort.</summary>
    public DateTime AddedAt { get; init; }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Refresh(); } }
    }

    public ModSummary? Modrinth { get; set; }
    public ModVersion? ModrinthVersion { get; set; }
    public ModSummary? CurseForge { get; set; }
    public ModVersion? CurseForgeVersion { get; set; }

    /// <summary>Fills in the resolved store identity after the fast initial scan and refreshes bindings.</summary>
    public void ApplyIdentity(ModSummary? modrinth, ModVersion? modrinthVer, ModSummary? curse, ModVersion? curseVer)
    {
        Modrinth = modrinth;
        ModrinthVersion = modrinthVer;
        CurseForge = curse;
        CurseForgeVersion = curseVer;
        Refresh();
    }

    private ModMeta _meta = new();
    public ModMeta Meta
    {
        get => _meta;
        set { _meta = value; Refresh(); }
    }

    /// <summary>List-view multi-select state (for bulk right-click actions).</summary>
    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    }

    /// <summary>Latest compatible version found by the background update check, if newer than installed.</summary>
    private ModVersion? _latest;
    public ModVersion? LatestVersion
    {
        get => _latest;
        set { _latest = value; Refresh(); }
    }

    public bool HasUpdate => LatestVersion is not null;

    // ── derived display / identity ─────────────────────────────────────────────

    // Honor the store the user installed from (Meta.PreferredSource) so a cross-listed jar keeps that
    // store's identity; otherwise default to Modrinth-first.
    public ModSummary? PrimaryMod => Meta.PreferredSource switch
    {
        ModSource.CurseForge when CurseForge is not null => CurseForge,
        ModSource.Modrinth when Modrinth is not null => Modrinth,
        _ => Modrinth ?? CurseForge
    };
    public ModVersion? PrimaryVersion => Meta.PreferredSource switch
    {
        ModSource.CurseForge when CurseForgeVersion is not null => CurseForgeVersion,
        ModSource.Modrinth when ModrinthVersion is not null => ModrinthVersion,
        _ => ModrinthVersion ?? CurseForgeVersion
    };
    public ModSource? PrimarySource => PrimaryMod?.Source;
    public bool IsExternal => PrimaryMod is null;

    public string DisplayName => PrimaryMod?.Name ?? Path.GetFileNameWithoutExtension(FileName);
    public string VersionLabel => PrimaryVersion?.VersionNumber ?? "—";
    public string? IconUrl => PrimaryMod?.IconUrl;
    public string? PageUrl => BuildPageUrl();

    public string Key => ModMetadataService.KeyFor(Modrinth, CurseForge, FileName);
    public IReadOnlyList<string> CandidateKeys => ModMetadataService.CandidateKeys(Modrinth, CurseForge, FileName);

    public int Priority => Meta.Priority;
    public bool IsLibrary => Meta.IsLibrary;
    public bool IsTesting => Meta.IsTesting;
    public bool IsExtra => Meta.IsExtra;
    public ModSide Side => Meta.Side;
    public bool IsSideRestricted => Meta.Side != ModSide.Both;
    public bool IsLocal => string.Equals(Folder, "local", StringComparison.OrdinalIgnoreCase);

    public string Initial =>
        DisplayName.Trim() is { Length: > 0 } n ? n[..1].ToUpperInvariant() : "?";

    public string SourceLabel => PrimarySource switch
    {
        ModSource.Modrinth   => "Modrinth",
        ModSource.CurseForge => "CurseForge",
        _                    => "External"
    };

    public string MetaLine
    {
        get
        {
            var parts = new List<string> { VersionLabel, SourceLabel };
            if (IsLocal) parts.Add("local");
            return string.Join("   ·   ", parts);
        }
    }

    public string CategoriesLabel => Meta.Categories.Count == 0 ? "" : string.Join(", ", Meta.Categories);

    public IReadOnlyList<ModDependency> Dependencies
    {
        get
        {
            // Union dependencies from BOTH stores' matched versions, so a dependency edge resolves
            // whether the dependent (or its target, e.g. Create) was recognised on Modrinth or
            // CurseForge — they reference each other by store-specific project ids.
            if (ModrinthVersion is null) return CurseForgeVersion?.Dependencies ?? Array.Empty<ModDependency>();
            if (CurseForgeVersion is null) return ModrinthVersion.Dependencies;
            var list = new List<ModDependency>(ModrinthVersion.Dependencies);
            list.AddRange(CurseForgeVersion.Dependencies);
            return list;
        }
    }

    public IReadOnlyList<ModDependency> RequiredDependencies =>
        Dependencies.Where(d => d.DependencyType is "required" or "required_dependency").ToList();

    public string TooltipText => BuildTooltip();

    private string BuildTooltip()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(DisplayName);
        sb.AppendLine($"Version: {VersionLabel}    Source: {SourceLabel}    Folder: {Folder}");
        if (PrimaryMod?.Author is { Length: > 0 } author) sb.AppendLine($"Author: {author}");

        var flags = new List<string>();
        if (!Enabled) flags.Add("disabled");
        if (IsLibrary) flags.Add("library");
        if (IsTesting) flags.Add("testing");
        if (IsExtra) flags.Add("extra");
        if (Side != ModSide.Both) flags.Add(Side == ModSide.Client ? "client-only" : "server-only");
        if (Priority != 0) flags.Add($"priority {Priority}");
        if (Meta.UpdateIncompatible) flags.Add("update-incompatible");
        if (flags.Count > 0) sb.AppendLine("Flags: " + string.Join(", ", flags));
        if (Meta.Categories.Count > 0) sb.AppendLine("Categories: " + string.Join(", ", Meta.Categories));
        if (RequiredDependencies.Count > 0) sb.AppendLine($"Dependencies: {RequiredDependencies.Count}");
        if (HasUpdate) sb.AppendLine($"Update available → {LatestVersion!.VersionNumber}");

        if (PrimaryMod?.Description is { Length: > 0 } desc)
        {
            sb.AppendLine();
            sb.Append(desc.Length > 400 ? desc[..400].TrimEnd() + "…" : desc);
        }
        return sb.ToString().TrimEnd();
    }

    private string? BuildPageUrl() => PrimaryMod switch
    {
        { Source: ModSource.Modrinth } m   => $"https://modrinth.com/mod/{m.Slug}",
        { Source: ModSource.CurseForge } m => $"https://www.curseforge.com/minecraft/mc-mods/{m.Slug}",
        _ => null
    };

    /// <summary>Raise change notification for every binding (call after a flag or state change).</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Loads a pack's installed mods as a single unified list (disk + identity + flags) and provides
/// the on-disk enable/disable primitive. Identity resolution and the fingerprint cache are reused
/// from <see cref="InstalledModResolver"/>, so this never re-implements hashing/matching.
/// </summary>
public sealed class PackModInventory
{
    private readonly PackFolderService _packs;
    private readonly ModFingerprintCache _cache;
    private readonly ModrinthService _modrinth;
    private readonly CurseForgeService _curseForge;
    private readonly ModMetadataService _metadata;

    public PackModInventory(
        PackFolderService packs,
        ModFingerprintCache cache,
        ModrinthService modrinth,
        CurseForgeService curseForge,
        ModMetadataService metadata)
    {
        _packs = packs;
        _cache = cache;
        _modrinth = modrinth;
        _curseForge = curseForge;
        _metadata = metadata;
    }

    public ModMetadataService Metadata => _metadata;

    /// <summary>Fast first pass: enumerate the jars, attach saved flags, and reuse each jar's
    /// locally-cached store identity (name / icon / version) — all without hashing or network, so
    /// the list and graph paint fully and instantly even for 400+ mod packs across relaunches.
    /// <see cref="ResolveIdentitiesAsync"/> runs afterwards to refine identities and catch changed
    /// jars; the update check follows that.</summary>
    public async Task<List<PackMod>> LoadAsync(Guid packId, bool includeLocal, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var found = ScanFolders(packId, includeLocal);

            var mods = new List<PackMod>(found.Count);
            foreach (var (path, folder, enabled) in found)
            {
                var fileName = StripDisabled(Path.GetFileName(path));
                long size = 0;
                var addedAt = DateTime.MinValue;
                try
                {
                    var info = new FileInfo(path);
                    size = info.Length;
                    // Creation time = when this jar was added to the pack (rename on enable/disable
                    // preserves it). Fall back to last-write if the creation time looks unset/bogus.
                    addedAt = info.CreationTimeUtc;
                    if (addedAt <= DateTime.FromFileTimeUtc(0) || addedAt > DateTime.UtcNow)
                        addedAt = info.LastWriteTimeUtc;
                }
                catch { /* locked */ }

                var mod = new PackMod
                {
                    FilePath = path,
                    FileName = fileName,
                    Folder = folder,
                    Enabled = enabled,
                    Size = size,
                    AddedAt = addedAt,
                    Meta = _metadata.GetMeta(packId, ModMetadataService.CandidateKeys(null, null, fileName))
                };

                // Reuse the saved identity right away (no hashing, no network — validated by the jar's
                // size + write time). The list/graph show real names, versions and icons immediately;
                // ResolveIdentitiesAsync still re-resolves both stores and re-checks for updates after.
                if (_cache.TryGetCachedMatch(path, out var cachedMod, out var cachedVer))
                {
                    if (cachedMod.Source == ModSource.Modrinth) mod.ApplyIdentity(cachedMod, cachedVer, null, null);
                    else mod.ApplyIdentity(null, null, cachedMod, cachedVer);
                    mod.Meta = _metadata.GetMeta(packId, mod.CandidateKeys); // re-key to the stable source id
                }

                mods.Add(mod);
            }

            return Sort(mods);
        }, ct);
    }

    /// <summary>Second pass: resolve each jar's CurseForge/Modrinth identity (hashing + cached
    /// network matching) and patch it into the already-displayed <see cref="PackMod"/> list in
    /// place. Safe to run in the background; mutations happen on the calling context.</summary>
    public async Task ResolveIdentitiesAsync(Guid packId, IReadOnlyList<PackMod> mods, CancellationToken ct = default)
    {
        if (mods.Count == 0) return;
        var paths = mods.Select(m => m.FilePath).ToList();
        var identities = await InstalledModResolver.ResolveAsync(paths, _cache, _modrinth, _curseForge, ct);

        var byPath = new Dictionary<string, InstalledModIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in identities) byPath[id.Path] = id;

        foreach (var m in mods)
        {
            if (ct.IsCancellationRequested) return;
            if (!byPath.TryGetValue(m.FilePath, out var id)) continue;
            if (id.Modrinth is null && id.CurseForge is null) continue;

            m.ApplyIdentity(id.Modrinth?.Mod, id.Modrinth?.Version, id.CurseForge?.Mod, id.CurseForge?.Version);
            // Re-read flags now that a stable source key (modrinth:/curseforge:) is available.
            m.Meta = _metadata.GetMeta(packId, m.CandidateKeys);
        }
    }

    private static List<PackMod> Sort(IEnumerable<PackMod> mods) =>
        mods.OrderByDescending(m => m.Priority)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<(string path, string folder, bool enabled)> ScanFolders(Guid packId, bool includeLocal)
    {
        var found = new List<(string, string, bool)>();

        void Add(string dir, string label)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) found.Add((f, label, false));
                else if (f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) found.Add((f, label, true));
            }
        }

        Add(Path.Combine(_packs.GameDir(packId), "mods"), "game");
        if (includeLocal) Add(Path.Combine(_packs.LocalDir(packId), "mods"), "local");

        // Dedupe by file name; a local jar shadows a game jar of the same name.
        return found
            .GroupBy(x => StripDisabled(Path.GetFileName(x.Item1)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Item2 == "local").First())
            .ToList();
    }

    private static string StripDisabled(string name) =>
        name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? name[..^".disabled".Length]
            : name;

    /// <summary>Flips a mod's effective enabled state by renaming <c>.jar</c> ⇄ <c>.jar.disabled</c>.
    /// Updates the mod's <see cref="PackMod.FilePath"/> and <see cref="PackMod.Enabled"/> in place.
    /// Returns false if the file could not be renamed (e.g. locked).</summary>
    public bool SetEnabled(PackMod mod, bool enabled)
    {
        try
        {
            var isDisabledOnDisk = mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

            if (enabled && isDisabledOnDisk)
            {
                var target = mod.FilePath[..^".disabled".Length];
                if (File.Exists(target)) File.Delete(target);
                File.Move(mod.FilePath, target);
                mod.FilePath = target;
            }
            else if (!enabled && !isDisabledOnDisk)
            {
                var target = mod.FilePath + ".disabled";
                if (File.Exists(target)) File.Delete(target);
                File.Move(mod.FilePath, target);
                mod.FilePath = target;
            }

            mod.Enabled = enabled;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Persists a mod's edited flags and refreshes its bindings.</summary>
    public void SaveMeta(Guid packId, PackMod mod)
    {
        _metadata.SetMeta(packId, mod.CandidateKeys, mod.Meta);
        mod.Refresh();
    }
}
