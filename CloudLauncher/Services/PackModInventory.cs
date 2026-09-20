using System.Collections.Concurrent;
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

    /// <summary>When this mod first landed in the pack. Seeded from the jar's creation time and then
    /// held steady across updates by <see cref="ModAddedCache"/>. Drives the "Add date" sort.</summary>
    public DateTime AddedAt { get; set; }

    /// <summary>"Added 12 Sep 2026, 21:04" in local time, for the List view row and the tooltip.</summary>
    public string AddedLabel =>
        AddedAt <= DateTime.MinValue ? "" : "Added " + AddedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm");

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

    /// <summary>Fills in the resolved store identity after the fast initial scan and refreshes bindings.
    /// A store that came back unresolved is left as it was rather than cleared: identity lookups fail
    /// per store (an API outage, a CDN block), and blanking one we had already identified drops the
    /// mod back to looking unrecognised until some later pass happens to succeed.</summary>
    public void ApplyIdentity(ModSummary? modrinth, ModVersion? modrinthVer, ModSummary? curse, ModVersion? curseVer)
    {
        if (modrinth is not null) { Modrinth = modrinth; ModrinthVersion = modrinthVer; }
        if (curse is not null) { CurseForge = curse; CurseForgeVersion = curseVer; }
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

    /// <summary>The pack-wide store preference (<see cref="ModAdvancedSettings.PreferredSource"/>),
    /// stamped on by the inventory at load so a mod without its own preference follows it.</summary>
    public ModSource? DefaultSource { get; set; }

    /// <summary>The pack-wide update channel (<see cref="ModAdvancedSettings.UpdateChannel"/>), stamped
    /// on by the inventory at load; <see cref="EffectiveUpdateChannel"/> applies the per-mod override.</summary>
    public string DefaultUpdateChannel { get; set; } = ModUpdateChannel.Release;

    /// <summary>The channel this mod's updates are drawn from: its own setting, else the pack's.</summary>
    public string EffectiveUpdateChannel =>
        ModUpdateChannel.Normalize(Meta.UpdateChannel) ?? ModUpdateChannel.Normalize(DefaultUpdateChannel) ?? ModUpdateChannel.Release;

    /// <summary>The store this mod follows: its own choice, else the pack default, else Modrinth first.</summary>
    public ModSource? EffectiveSourcePreference => Meta.PreferredSource ?? DefaultSource;

    // Honor the store the user installed from (Meta.PreferredSource) - or the pack-wide default a
    // CurseForge import sets - so a cross-listed jar keeps that store's identity; otherwise Modrinth-first.
    public ModSummary? PrimaryMod => EffectiveSourcePreference switch
    {
        ModSource.CurseForge when CurseForge is not null => CurseForge,
        ModSource.Modrinth when Modrinth is not null => Modrinth,
        _ => Modrinth ?? CurseForge
    };
    public ModVersion? PrimaryVersion => EffectiveSourcePreference switch
    {
        ModSource.CurseForge when CurseForgeVersion is not null => CurseForgeVersion,
        ModSource.Modrinth when ModrinthVersion is not null => ModrinthVersion,
        _ => ModrinthVersion ?? CurseForgeVersion
    };
    public ModSource? PrimarySource => PrimaryMod?.Source;
    public bool IsExternal => PrimaryMod is null;

    /// <summary>True when the jar is known on both stores, so a store preference actually changes something.</summary>
    public bool IsCrossListed => Modrinth is not null && CurseForge is not null;

    /// <summary>Numeric key so a grid sorts "1.10" after "1.9" instead of alphabetically.</summary>
    public long VersionSortKey
    {
        get
        {
            var version = PrimaryVersion?.VersionNumber;
            if (string.IsNullOrWhiteSpace(version)) return 0;
            var parts = version.Split('.', '-', '_', '+', ' ');
            long value = 0;
            for (var i = 0; i < 4; i++)
            {
                value *= 1000;
                if (i >= parts.Length) continue;
                var digits = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
                if (!int.TryParse(digits, out var part)) break;
                value += Math.Clamp(part, 0, 999);
            }
            return value;
        }
    }

    public string DisplayName => PrimaryMod?.Name ?? Path.GetFileNameWithoutExtension(FileName);
    public string VersionLabel => PrimaryVersion?.VersionNumber ?? "—";
    public string? IconUrl => PrimaryMod?.IconUrl;
    public string? PageUrl => BuildPageUrl();

    public string Key => ModMetadataService.KeyFor(Modrinth, CurseForge, FileName);
    public IReadOnlyList<string> CandidateKeys => ModMetadataService.CandidateKeys(Modrinth, CurseForge, FileName);

    public int Priority => Meta.Priority;
    public int ContentSize => Meta.ContentSize;
    public string ContentSizeLabel => ModContentSize.Label(Meta.ContentSize);
    public bool IsLibrary => Meta.IsLibrary;
    public bool IsTesting => Meta.IsTesting;
    public bool IsExtra => Meta.IsExtra;

    /// <summary>Held at its current version: bulk updates skip it. See <see cref="ModMeta.UpdateLocked"/>.</summary>
    public bool IsUpdateLocked => Meta.UpdateLocked;
    public ModSide Side => Meta.Side;
    public bool IsSideRestricted => Meta.Side != ModSide.Both;
    public bool IsLocal => string.Equals(Folder, "local", StringComparison.OrdinalIgnoreCase);

    public string? Note => string.IsNullOrWhiteSpace(Meta.Note) ? null : Meta.Note!.Trim();
    public bool HasNote => Note is not null;

    // ── recorded incompatibilities ─────────────────────────────────────────────

    private IReadOnlyList<string> _conflicts = Array.Empty<string>();

    /// <summary>Display names of the other installed mods this one is recorded as incompatible with,
    /// filled in by <see cref="PackModInventory.ResolveConflicts"/>.</summary>
    /// <remarks>Resolved for the whole list at once rather than computed per mod: the record is a
    /// list of mod <em>keys</em>, so answering it for one mod means indexing every other mod's keys,
    /// and a card-level property doing that would be O(n²) on every repaint.</remarks>
    public IReadOnlyList<string> ConflictsWith => _conflicts;

    /// <summary>True when this mod carries a conflict worth showing: a recorded clash with another
    /// installed mod, or the standing "incompatible with mods not installed here" flag.</summary>
    public bool HasConflict => _conflicts.Count > 0 || Meta.IncompatibleWithUnknown;

    /// <summary>What the CONFLICT pill's tooltip says — the mods by name, so the pill is actionable
    /// rather than just alarming.</summary>
    public string ConflictLabel
    {
        get
        {
            var lines = new List<string>();
            if (_conflicts.Count > 0)
                lines.Add("Marked incompatible with: " + string.Join(", ", _conflicts));
            if (Meta.IncompatibleWithUnknown)
                lines.Add("Marked incompatible with mods that are not installed here.");
            if (Meta.UpdateIncompatible)
                lines.Add("Updating this mod is marked as breaking compatibility.");
            return lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Sets the resolved conflict names, raising change notification only when they moved —
    /// the resolve runs over the whole pack after every metadata edit.</summary>
    internal void SetConflicts(IReadOnlyList<string> names)
    {
        if (_conflicts.Count == names.Count && _conflicts.SequenceEqual(names, StringComparer.Ordinal)) return;
        _conflicts = names;
        Refresh();
    }

    /// <summary>First line of the note, trimmed to fit a card row.</summary>
    public string NotePreview
    {
        get
        {
            if (Note is not { } n) return "";
            var line = n.Split('\n')[0].TrimEnd('\r', ' ');
            return line.Length > 90 ? line[..90].TrimEnd() + "…" : line;
        }
    }

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

    /// <summary>The category this mod is filed under when the views group by category: its first
    /// one, or "Uncategorized". A mod can be in several; the first is the one it was put in first,
    /// and grouping needs exactly one bucket per mod — the same rule the graph uses.</summary>
    public string PrimaryCategory => Meta.Categories.FirstOrDefault() ?? UncategorizedName;

    public const string UncategorizedName = "Uncategorized";

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
        if (AddedLabel.Length > 0) sb.AppendLine(AddedLabel);
        if (PrimaryMod?.Author is { Length: > 0 } author) sb.AppendLine($"Author: {author}");

        var flags = new List<string>();
        if (!Enabled) flags.Add("disabled");
        if (IsLibrary) flags.Add("library");
        if (IsTesting) flags.Add("testing");
        if (IsExtra) flags.Add("extra");
        if (Side != ModSide.Both) flags.Add(Side == ModSide.Client ? "client-only" : "server-only");
        if (Priority != 0) flags.Add($"priority {Priority}");
        if (ContentSize != 0) flags.Add($"{ContentSizeLabel.ToLowerInvariant()} content");
        if (Meta.UpdateIncompatible) flags.Add("update-incompatible");
        if (Meta.UpdateIncompatibleWithUnknown) flags.Add("update-incompatible with unknown mods");
        if (Meta.UpdateLocked)
            flags.Add(Meta.PinnedVersionId is null
                ? "updates locked"
                : $"kept at {VersionLabel}");
        if (flags.Count > 0) sb.AppendLine("Flags: " + string.Join(", ", flags));
        // The incompatibility record is otherwise invisible — it is written from the options menu and
        // then never mentioned again, which is exactly the trap it exists to prevent.
        if (ConflictLabel is { Length: > 0 } conflict) sb.AppendLine(conflict);
        if (Meta.Categories.Count > 0) sb.AppendLine("Categories: " + string.Join(", ", Meta.Categories));
        if (RequiredDependencies.Count > 0) sb.AppendLine($"Dependencies: {RequiredDependencies.Count}");
        if (HasUpdate)
            sb.AppendLine($"Update available → {LatestVersion!.VersionNumber}" +
                          (Meta.UpdateLocked ? "  (locked — “Update all” skips this)" : ""));
        if (Meta.UpdateChannel is not null)
            sb.AppendLine($"Update channel: {ModUpdateChannel.Label(Meta.UpdateChannel)}");

        if (Note is { } note)
        {
            sb.AppendLine();
            sb.AppendLine("Note:");
            sb.AppendLine(note.Length > 500 ? note[..500].TrimEnd() + "…" : note);
        }

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
    private readonly ModAddedCache _added;

    /// <summary>The store each pack's mods evidently came from (see <see cref="InferSource"/>), for
    /// packs that have no explicit default. Kept for the session so reloads don't flip labels.</summary>
    private readonly ConcurrentDictionary<Guid, ModSource?> _inferred = new();

    public PackModInventory(
        PackFolderService packs,
        ModFingerprintCache cache,
        ModrinthService modrinth,
        CurseForgeService curseForge,
        ModMetadataService metadata,
        ModAddedCache added)
    {
        _packs = packs;
        _cache = cache;
        _modrinth = modrinth;
        _curseForge = curseForge;
        _metadata = metadata;
        _added = added;
    }

    public ModMetadataService Metadata => _metadata;

    /// <summary>The store a pack's mods evidently came from, when the pack has no explicit default:
    /// whichever store more of its jars exist on <em>exclusively</em>. A jar only CurseForge knows
    /// can't have come from Modrinth, so a pack of CurseForge-only mods with one Modrinth-only mod is
    /// a CurseForge pack — and its cross-listed mods should link there too. Null when it's a tie.</summary>
    public ModSource? InferredSource(Guid packId) => _inferred.TryGetValue(packId, out var s) ? s : null;

    /// <summary>Exclusive-store counts behind <see cref="InferredSource"/>, for the Advanced tab's label.</summary>
    public (int CurseForgeOnly, int ModrinthOnly) ExclusiveCounts(IEnumerable<PackMod> mods)
    {
        int cf = 0, mr = 0;
        foreach (var m in mods)
        {
            if (m.CurseForge is not null && m.Modrinth is null) cf++;
            else if (m.Modrinth is not null && m.CurseForge is null) mr++;
        }
        return (cf, mr);
    }

    private static ModSource? InferSource(int curseForgeOnly, int modrinthOnly) =>
        curseForgeOnly > modrinthOnly ? ModSource.CurseForge
        : modrinthOnly > curseForgeOnly ? ModSource.Modrinth
        : null;

    /// <summary>The store a mod without its own preference follows: the pack's explicit default,
    /// else the inferred one.</summary>
    private ModSource? EffectiveDefault(Guid packId) =>
        _metadata.Advanced(packId).PreferredSource ?? InferredSource(packId);

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
            var advanced = _metadata.Advanced(packId);
            var channel = _metadata.EffectiveUpdateChannel(packId);

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
                    DefaultSource = advanced.PreferredSource ?? InferredSource(packId),
                    DefaultUpdateChannel = channel,
                    Meta = _metadata.GetMeta(packId, ModMetadataService.CandidateKeys(null, null, fileName))
                };

                // Reuse the saved identities right away (no hashing, no network — validated by the jar's
                // size + write time). Both stores' identities when known, so a cross-listed mod shows
                // the store it follows from the first frame instead of flipping once resolution lands.
                if (_cache.TryGetCachedMatches(path, out var cachedMr, out var cachedCf))
                {
                    mod.ApplyIdentity(cachedMr?.Mod, cachedMr?.Version, cachedCf?.Mod, cachedCf?.Version);
                    mod.Meta = _metadata.GetMeta(packId, mod.CandidateKeys); // re-key to the stable source id
                }

                // "Added" is the first time this mod was seen here, not this jar's creation time —
                // an update replaces the jar, and the user still means "when did I add this mod".
                mod.AddedAt = _added.Resolve(packId, mod.CandidateKeys, addedAt);

                mods.Add(mod);
            }

            // First load this session: infer the pack's home store from what the cache already knows,
            // so the very first paint links cross-listed mods to the right store.
            if (advanced.PreferredSource is null && !_inferred.ContainsKey(packId))
            {
                var (cfOnly, mrOnly) = ExclusiveCounts(mods);
                var guess = InferSource(cfOnly, mrOnly);
                if (guess is not null)
                {
                    _inferred[packId] = guess;
                    foreach (var m in mods) m.DefaultSource = guess;
                }
            }

            _added.Flush();
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
        var index = await InstalledModResolver.ResolveAsync(paths, _cache, _modrinth, _curseForge, ct);

        var byPath = new Dictionary<string, InstalledModIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in index.Identities) byPath[id.Path] = id;

        foreach (var m in mods)
        {
            if (ct.IsCancellationRequested) return;
            if (!byPath.TryGetValue(m.FilePath, out var id)) continue;
            if (id.Modrinth is null && id.CurseForge is null) continue;

            m.ApplyIdentity(id.Modrinth?.Mod, id.Modrinth?.Version, id.CurseForge?.Mod, id.CurseForge?.Version);
            // Re-read flags now that a stable source key (modrinth:/curseforge:) is available.
            m.Meta = _metadata.GetMeta(packId, m.CandidateKeys);
            m.AddedAt = _added.Resolve(packId, m.CandidateKeys, m.AddedAt);
        }

        // Re-infer from the full picture — but only from a pass where both stores answered, since a
        // store that failed would make every jar look exclusive to the other one.
        if (index.Complete)
        {
            var (cfOnly, mrOnly) = ExclusiveCounts(mods);
            _inferred[packId] = InferSource(cfOnly, mrOnly);
            // Every mod answered, so anything else on file is a mod that left the pack. Pruning off a
            // partial pass would throw away the added dates of the mods that store did not answer for.
            _added.Prune(packId, mods.SelectMany(m => m.CandidateKeys));
        }
        _added.Flush();
        var effective = EffectiveDefault(packId);
        foreach (var m in mods)
        {
            if (m.DefaultSource == effective) continue;
            m.DefaultSource = effective;
            m.Refresh();
        }
    }

    // Priority first, then content size (bigger mods float up within a band), then name.
    private static List<PackMod> Sort(IEnumerable<PackMod> mods) =>
        mods.OrderByDescending(m => m.Priority)
            .ThenByDescending(m => m.ContentSize)
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

    /// <summary>
    /// Resolves each mod's recorded mod-to-mod incompatibilities into the names of the installed mods
    /// it clashes with, so the views can show them (see <see cref="PackMod.ConflictsWith"/>).
    /// </summary>
    /// <remarks>
    /// A conflict is recorded on both mods by the options menu, but metadata written by an older
    /// build — or edited by hand in <c>mods.json</c> — may only carry one direction, so a link found
    /// either way is mirrored onto both here. Mods whose partner is not installed contribute nothing:
    /// a clash with a mod this pack does not have is not a conflict the user can act on, and
    /// <see cref="ModMeta.IncompatibleWithUnknown"/> is the flag for that case.
    /// </remarks>
    public static void ResolveConflicts(IReadOnlyList<PackMod> mods)
    {
        var byKey = new Dictionary<string, PackMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
            foreach (var key in m.CandidateKeys)
                byKey[key] = m;

        // PackMod does not override Equals, so the default comparer is reference identity — which is
        // what "this mod object" means here, since two jars can share a display name.
        var names = new Dictionary<PackMod, SortedSet<string>>();
        foreach (var m in mods) names[m] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in mods)
            foreach (var key in m.Meta.IncompatibleWith)
            {
                if (!byKey.TryGetValue(key, out var other) || ReferenceEquals(other, m)) continue;
                names[m].Add(other.DisplayName);
                if (names.TryGetValue(other, out var back)) back.Add(m.DisplayName);
            }

        foreach (var m in mods) m.SetConflicts(names[m].ToList());
    }

    /// <summary>Re-stamps the pack-wide defaults (store preference, update channel) onto already
    /// loaded mods after the Advanced settings changed, so labels and links follow without a re-scan.</summary>
    public void ApplyPackDefaults(Guid packId, IEnumerable<PackMod> mods)
    {
        var advanced = _metadata.Advanced(packId);
        var source = advanced.PreferredSource ?? InferredSource(packId);
        var channel = _metadata.EffectiveUpdateChannel(packId);
        foreach (var m in mods)
        {
            m.DefaultSource = source;
            m.DefaultUpdateChannel = channel;
            m.Refresh();
        }
    }
}
