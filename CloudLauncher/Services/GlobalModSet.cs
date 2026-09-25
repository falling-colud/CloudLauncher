using System.ComponentModel;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>One instance's own copy of one mod, and the instance it is in.</summary>
/// <param name="Instance">The instance the jar was read from.</param>
/// <param name="Mod">That instance's row for it, as <see cref="PackModInventory.LoadAsync"/> produced
/// it: its <see cref="PackMod.Meta"/> holds that instance's effective flags and its
/// <see cref="PackMod.FilePath"/> is the file per-instance actions act on.</param>
public sealed record GlobalModCopy(PackSummary Instance, PackMod Mod);

/// <summary>
/// One row of the global mod set: a mod, wherever it lives.
/// </summary>
/// <remarks>
/// <para>A row object rather than a bare <see cref="PackMod"/> because <see cref="Views.ListDiff"/>
/// keeps row objects on screen across refreshes and folds new values in through
/// <see cref="CopyFrom"/>. Anything the page shows that isn't on a <see cref="PackMod"/> (which
/// instances hold it, library state, its rule) must live on the row.</para>
/// <para><see cref="Mod"/> is a representative: it carries the mod's identity and the global flags,
/// so an options menu opened on it edits the default. Per-instance work (enable, delete, reveal)
/// goes through <see cref="Copies"/>.</para>
/// </remarks>
public sealed class GlobalModRow : INotifyPropertyChanged
{
    /// <summary>The identity this row was grouped under: <c>modrinth:...</c>, <c>curseforge:...</c>
    /// or <c>file:...</c>. Stable enough to diff on; the global flags are filed under it.</summary>
    public required string Key { get; init; }

    /// <summary>The mod itself, with the global flags on it. See the class remarks.</summary>
    public required PackMod Mod { get; set; }

    /// <summary>Every instance that has this mod. Empty for a default that is in the library and in no
    /// instance yet.</summary>
    public required IReadOnlyList<GlobalModCopy> Copies { get; set; }

    /// <summary>The library's copy, when this mod is one of the user's defaults. Null for a mod that
    /// is merely installed somewhere.</summary>
    public LibraryItem? Library { get; set; }

    /// <summary>True when this mod is in the library at all (the launcher keeps its own copy).</summary>
    public bool InLibrary => Library is not null;

    /// <summary>True when the library copy also carries a switched-on rule, so every compatible
    /// instance gets it. In the library without this is "kept, but placed by hand".</summary>
    public bool IsDefault => Library?.AutoApply == true;

    /// <summary>One line naming what the rule narrows down to, or what state the row is in when there
    /// is no rule. Written for a column, so it never reads as an error.</summary>
    public string LibraryLabel =>
        IsDefault ? Library!.RuleSummary
        : InLibrary ? "kept by the launcher, placed by hand"
        : Copies.Count == 0 ? ""
        : "not a default";

    /// <summary>How many instances hold it.</summary>
    public int InstanceCount => Copies.Count;

    /// <summary>True when every instance that has it has it switched on. A row with no copies reads as
    /// on, because there is nothing switched off.</summary>
    public bool EnabledEverywhere => Copies.All(c => c.Mod.Enabled);

    /// <summary>True when some instances have it on and others off, which a single toggle can't show,
    /// so the row says so.</summary>
    public bool MixedEnabled => Copies.Count > 1 && Copies.Any(c => c.Mod.Enabled) && Copies.Any(c => !c.Mod.Enabled);

    /// <summary>True when at least one instance has its own copy shadowing the library's.</summary>
    public bool AnyShadowed => Copies.Any(c => c.Mod.ShadowsLibrary);

    /// <summary>The instances it is in, named, for a tooltip. Capped so a mod in forty instances does
    /// not produce a tooltip the height of the screen.</summary>
    public string WhereTooltip
    {
        get
        {
            if (Copies.Count == 0) return "Kept by the launcher, and in no instance yet.";
            var names = Copies.Select(c => c.Instance.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            var shown = string.Join(", ", names.Take(8));
            return names.Count <= 8 ? "In " + shown : $"In {shown} and {names.Count - 8} more";
        }
    }

    /// <summary>The category this row is filed under when the list groups by category.</summary>
    /// <remarks>A passthrough rather than a dotted path: <c>PropertyGroupDescription</c> takes a property
    /// name, and passing it a path into a child object isn't reliable.</remarks>
    public string PrimaryCategory => Mod.PrimaryCategory;

    /// <summary>List-view multi-select state, mirrored onto the representative so the row template and
    /// the options menu agree about what is selected.</summary>
    public bool IsSelected
    {
        get => Mod.IsSelected;
        set
        {
            if (Mod.IsSelected == value) return;
            Mod.IsSelected = value;
            Refresh();
        }
    }

    /// <summary>
    /// Folds a rescanned row into this one, which is the row still on screen.
    /// </summary>
    /// <remarks>Without this, <see cref="Views.ListDiff"/> keeps the same object with no notification and
    /// the page keeps showing the last scan's count and version. The representative is replaced whole
    /// and then notifies, since all its display properties are derived.</remarks>
    public void CopyFrom(GlobalModRow fresh)
    {
        var wasSelected = Mod.IsSelected;
        Mod = fresh.Mod;
        Mod.IsSelected = wasSelected;   // selection is the user's, not the scan's
        Copies = fresh.Copies;
        Library = fresh.Library;
        Mod.Refresh();
        Refresh();
    }

    /// <summary>
    /// Raises change notification for every binding on this row.
    /// </summary>
    /// <remarks>
    /// Separate from <c>Mod.Refresh()</c>: about half of the row binds this object (the DEFAULT,
    /// SHADOWED and MIXED pills, the library column, <see cref="EnabledEverywhere"/>, the select box),
    /// all derived from <see cref="Copies"/> and <see cref="Library"/>. Everything is derived, so one
    /// blanket notification is used.
    /// </remarks>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>What one build of the global mod set found, and what it couldn't read.</summary>
/// <param name="Rows">The mods, one row per identity, ordered by name.</param>
/// <param name="Instances">The instances the page may scope to, in the order the server listed them.</param>
/// <param name="ScannedUtc">When the walk ran, for the provenance line on a cached paint.</param>
/// <param name="InstancesRead">How many instances were actually read.</param>
/// <param name="Unreadable">How many could not be read at all.</param>
/// <param name="Skipped">How many were not attempted because the cap was reached.</param>
public sealed record GlobalModSnapshot(
    IReadOnlyList<GlobalModRow> Rows,
    IReadOnlyList<PackSummary> Instances,
    DateTimeOffset ScannedUtc,
    int InstancesRead,
    int Unreadable,
    int Skipped)
{
    /// <summary>The status-line sentence: what was read, and what was not.</summary>
    /// <remarks>Never null, and always mentions failures. One unreadable instance doesn't fail the page,
    /// but the page mustn't claim it read everything.</remarks>
    public string Note
    {
        get
        {
            var parts = new List<string> { $"Read from {InstancesRead} instance{(InstancesRead == 1 ? "" : "s")}." };
            if (Unreadable > 0)
                parts.Add($"{Unreadable} could not be read - the launcher log says why.");
            if (Skipped > 0)
                parts.Add($"{Skipped} more were not scanned (the page stops at {GlobalModSet.InstanceCap}).");
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// The mods one instance actually has, as that instance's own rows.
    /// </summary>
    /// <remarks>
    /// <para>A <see cref="GlobalModRow"/>'s <see cref="GlobalModRow.Mod"/> is a representative with the
    /// global flags. Per-instance reasoning (what is installed there, what depends on what) needs that
    /// instance's own <see cref="PackMod"/> objects, from <see cref="GlobalModCopy.Mod"/>.</para>
    /// <para>Dependency graphs must use this: resolved over the union, they would pair a mod in one
    /// instance with a library in another and draw edges that exist in no launchable instance.</para>
    /// <para>Order is the folder's scan order, same as the per-pack hub's list.</para>
    /// </remarks>
    public IReadOnlyList<PackMod> ModsIn(Guid instanceId) =>
        Rows.SelectMany(r => r.Copies)
            .Where(c => c.Instance.Id == instanceId)
            .Select(c => c.Mod)
            .ToList();

    /// <summary>An empty set, for a launcher with no instances at all.</summary>
    public static GlobalModSnapshot Empty(IReadOnlyList<PackSummary> instances) =>
        new([], instances, DateTimeOffset.Now, 0, 0, 0);
}

/// <summary>
/// The union of every instance's mods, joined by mod identity, with the library's defaults folded in.
/// </summary>
/// <remarks>
/// <para>Backs the global Mods page, where the same jar in five instances is one mod. Kept here so
/// anything else asking the same question uses one implementation.</para>
/// <para>Grouped by identity, not file name: <see cref="ModMetadataService.CandidateKeys"/> are the
/// keys the flags are filed under, so <c>modrinth:AANobbMI</c> lines up Sodium 0.5 in one instance
/// with 0.6 in another. The first matching key wins, most stable first, so a jar recognised on
/// Modrinth in one instance and only by file name in another still lands in one row.</para>
/// <para><see cref="Build"/> is the fast pass: <see cref="PackModInventory.LoadAsync"/> lists jars
/// and reuses cached store identities, with no hashing or network. <see cref="EnrichAsync"/> resolves
/// identities afterwards (cancellable) and re-groups, since resolving can change a jar's key.</para>
/// <para>Conflicts are resolved per instance with <see cref="PackModInventory.ResolveConflicts"/>;
/// over the union it would report clashes between mods in different instances.</para>
/// <para>Cached in memory only: rows are live view models the page mutates (selection, flags,
/// enabled state), so a serialised cache would return stale objects. <see cref="Cached"/> makes
/// re-entering the page a refresh rather than a reload; <see cref="Invalidate"/> drops it after any
/// write.</para>
/// </remarks>
public sealed class GlobalModSet(PackModInventory inventory, ModMetadataService metadata, TestLaunchScope? testScope = null)
{
    /// <summary>How many instances a build will walk before it stops and says so.</summary>
    /// <remarks>Each is a directory read plus a cache lookup per jar; forty is already about a second of
    /// disk on a cold cache. The note names the cap so the list doesn't just end.</remarks>
    public const int InstanceCap = 40;

    private readonly object _lock = new();
    private GlobalModSnapshot? _cached;

    /// <summary>The last build, or null. Painted first so reopening the page does not blank it.</summary>
    public GlobalModSnapshot? Cached
    {
        get { lock (_lock) return _cached; }
    }

    /// <summary>Drops the remembered build, so the next one re-reads the disk. Called after anything
    /// that changes what is in an instance or in the library.</summary>
    public void Invalidate()
    {
        lock (_lock) _cached = null;
    }

    /// <summary>
    /// Builds the union. Blocking disk work; call it from a worker thread.
    /// </summary>
    /// <param name="instances">The instances to walk, as already fetched by the caller, so the result
    /// matches the list on screen.</param>
    /// <param name="libraryMods">The library's mod items, from
    /// <c>ContentLibraryService.Scan(LibraryKind.Mod)</c>. Empty means "no defaults yet".</param>
    /// <param name="progress">Names the instance being read, for the busy line.</param>
    public GlobalModSnapshot Build(
        IReadOnlyList<PackSummary> instances,
        IReadOnlyList<LibraryItem> libraryMods,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var scanned = instances.Take(InstanceCap).ToList();
        var skipped = instances.Count - scanned.Count;
        var unreadable = 0;
        var copies = new List<GlobalModCopy>();

        foreach (var instance in scanned)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Reading {instance.Name}...");

            // A launcher killed during "Run as Test" leaves that instance's mods folder truncated, and
            // a scan over it would record every held-back mod as removed. The per-pack hub runs the
            // same guard before its reload, and only while nothing is running there.
            try { testScope?.RestoreIfPending(instance.Id); }
            catch (Exception ex) { AppLog.LogError(nameof(GlobalModSet), ex); }

            List<PackMod> mods;
            try
            {
                // Always include local/: library defaults land there, and it's the per-user side of a
                // shared instance.
                mods = inventory.LoadAsync(instance.Id, includeLocal: true, ct, instance.Name)
                                .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One unreadable instance is not a failed page.
                unreadable++;
                AppLog.LogError(nameof(GlobalModSet), ex);
                continue;
            }

            // Per instance, before the union: see the class remarks.
            PackModInventory.ResolveConflicts(mods);
            foreach (var mod in mods) copies.Add(new GlobalModCopy(instance, mod));
        }

        ct.ThrowIfCancellationRequested();

        var libraryIndex = libraryMods
            .GroupBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        PackModInventory.MarkLibraryCopies(
            copies.Select(c => c.Mod).ToList(),
            libraryMods.Select(i => (i.FileName, i.Path)).ToList());

        var rows = Group(copies, libraryMods, libraryIndex);
        var snapshot = new GlobalModSnapshot(rows, instances, DateTimeOffset.Now,
                                             scanned.Count, unreadable, skipped);
        lock (_lock) _cached = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Resolves store identities for everything in <paramref name="snapshot"/> and re-groups it.
    /// </summary>
    /// <remarks>
    /// <para>This pass hashes files and hits the network, so the page runs it after the list is on
    /// screen. Instances go one at a time: they usually share a disk, and
    /// <see cref="PackModInventory.ResolveIdentitiesAsync"/> works per pack.</para>
    /// <para>Returns a new snapshot because resolving can change a mod's key and so its row. The page
    /// diffs the result; a row whose key moved loses its selection.</para>
    /// <para>The same jar in five instances is hashed five times on a cold cache, since
    /// <c>ModFingerprintCache</c> keys by absolute path. That cost is why this isn't part of
    /// <see cref="Build"/>.</para>
    /// </remarks>
    public async Task<GlobalModSnapshot> EnrichAsync(
        GlobalModSnapshot snapshot,
        IReadOnlyList<LibraryItem> libraryMods,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var byInstance = snapshot.Rows
            .SelectMany(r => r.Copies)
            .GroupBy(c => c.Instance.Id)
            .ToList();

        foreach (var group in byInstance)
        {
            ct.ThrowIfCancellationRequested();
            var instance = group.First().Instance;
            progress?.Report($"Identifying {instance.Name}...");
            try
            {
                await inventory.ResolveIdentitiesAsync(instance.Id, group.Select(c => c.Mod).ToList(), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Offline, or one store refusing: the file-name view is still true.
                AppLog.LogError(nameof(GlobalModSet), ex);
            }
        }

        ct.ThrowIfCancellationRequested();

        var copies = snapshot.Rows.SelectMany(r => r.Copies).ToList();
        var libraryIndex = libraryMods
            .GroupBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        PackModInventory.MarkLibraryCopies(
            copies.Select(c => c.Mod).ToList(),
            libraryMods.Select(i => (i.FileName, i.Path)).ToList());

        var enriched = snapshot with
        {
            Rows = Group(copies, libraryMods, libraryIndex),
            ScannedUtc = DateTimeOffset.Now
        };
        lock (_lock) _cached = enriched;
        return enriched;
    }

    // ── grouping ─────────────────────────────────────────────────────────────

    /// <summary>Groups every instance's copies into one row per identity and folds in the library
    /// items, including defaults no instance has yet.</summary>
    private List<GlobalModRow> Group(
        IReadOnlyList<GlobalModCopy> copies,
        IReadOnlyList<LibraryItem> libraryMods,
        Dictionary<string, LibraryItem> libraryByFileName)
    {
        var grouped = new Dictionary<string, List<GlobalModCopy>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var copy in copies)
        {
            // Reuse the first candidate key an earlier copy used, so a jar recognised on Modrinth
            // here and only by file name there still shares a row. Otherwise its own key starts a row.
            var key = copy.Mod.CandidateKeys.FirstOrDefault(grouped.ContainsKey) ?? copy.Mod.Key;
            if (!grouped.TryGetValue(key, out var list))
            {
                grouped[key] = list = [];
                order.Add(key);
            }
            list.Add(copy);
        }

        var rows = new List<GlobalModRow>(order.Count + libraryMods.Count);
        var claimedLibrary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The identity each library item recorded when it was added, so a default whose file name no
        // longer matches any instance's (it was updated in one, say) still finds its row.
        var libraryByModKey = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in libraryMods)
            if (item.ModKey is { Length: > 0 } modKey) libraryByModKey.TryAdd(modKey, item);

        foreach (var key in order)
        {
            var list = grouped[key];
            // Match by file name first (what an instance's folder holds; the library's own key is a
            // path, not a mod identity), then by the recorded identity. Either alone can list the same
            // mod twice: once as the instances' row and once as an unclaimed library item.
            LibraryItem? library = null;
            foreach (var copy in list)
                if (libraryByFileName.TryGetValue(copy.Mod.FileName, out var hit)) { library = hit; break; }
            if (library is null && libraryByModKey.TryGetValue(key, out var byKey)) library = byKey;
            if (library is null)
                foreach (var copy in list)
                {
                    var hit = copy.Mod.CandidateKeys
                        .Select(k => libraryByModKey.TryGetValue(k, out var m) ? m : null)
                        .FirstOrDefault(m => m is not null);
                    if (hit is not null) { library = hit; break; }
                }
            if (library is not null) claimedLibrary.Add(library.Key);

            rows.Add(new GlobalModRow
            {
                Key = key,
                Mod = Representative(list, library),
                Copies = list,
                Library = library
            });
        }

        // Defaults in the library but in no instance yet, so a rule the user wrote but hasn't applied
        // still shows.
        foreach (var item in libraryMods)
        {
            if (claimedLibrary.Contains(item.Key)) continue;
            var key = item.ModKey is { Length: > 0 } stored
                ? stored
                : ModMetadataService.KeyFor(null, null, item.FileName);
            rows.Add(new GlobalModRow
            {
                Key = key,
                Mod = LibraryRepresentative(item),
                Copies = [],
                Library = item
            });
        }

        return rows
            .OrderBy(r => r.Mod.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The one <see cref="PackMod"/> that stands for a whole row.
    /// </summary>
    /// <remarks>
    /// <para>Built fresh rather than taken from <paramref name="copies"/>: its <see cref="PackMod.Meta"/>
    /// must be the global entry (so the options menu edits the default, while each instance's row keeps
    /// its own flags), and its enabled state is "on in every instance", which no single copy has.</para>
    /// <para>Identity comes from whichever copy has one, so a jar recognised in any instance shows the
    /// known name, icon and version.</para>
    /// </remarks>
    private PackMod Representative(IReadOnlyList<GlobalModCopy> copies, LibraryItem? library)
    {
        var donor = copies.FirstOrDefault(c => c.Mod.PrimaryMod is not null)?.Mod ?? copies[0].Mod;
        var rep = new PackMod
        {
            FilePath = library?.Path ?? donor.FilePath,
            FileName = donor.FileName,
            Folder = library is not null ? "library" : donor.Folder,
            Enabled = copies.All(c => c.Mod.Enabled),
            Size = donor.Size,
            AddedAt = copies.Min(c => c.Mod.AddedAt),
            OwnerPackId = library is not null ? Guid.Empty : donor.OwnerPackId,
            OwnerPackName = copies.Count == 1 ? donor.OwnerPackName : null,
            CopyCount = copies.Count,
            IsLibraryCopy = library is not null,
            ShadowsLibrary = false,
            DefaultUpdateChannel = metadata.EffectiveUpdateChannel(ModMetadataService.GlobalScope),
            DefaultSource = donor.DefaultSource
        };
        rep.ApplyIdentity(donor.Modrinth, donor.ModrinthVersion, donor.CurseForge, donor.CurseForgeVersion);
        // The global entry, read through the representative's candidate keys like everywhere else, so a
        // mod now recognised on a store finds an entry written when it was only a file name, and writes
        // from the options menu (PackModInventory.SaveMeta, same keys) consolidate onto the stable one.
        rep.Meta = metadata.GetMeta(ModMetadataService.GlobalScope, rep.CandidateKeys);
        return rep;
    }

    /// <summary>The representative for a default that is in the library and in no instance.</summary>
    private PackMod LibraryRepresentative(LibraryItem item)
    {
        var rep = new PackMod
        {
            FilePath = item.Path,
            FileName = item.FileName,
            Folder = "library",
            Enabled = true,
            Size = item.SizeBytes,
            AddedAt = item.AddedAt.UtcDateTime,
            OwnerPackId = Guid.Empty,
            CopyCount = 0,
            IsLibraryCopy = true,
            DefaultUpdateChannel = metadata.EffectiveUpdateChannel(ModMetadataService.GlobalScope)
        };
        rep.Meta = metadata.GetMeta(ModMetadataService.GlobalScope, rep.CandidateKeys);
        return rep;
    }
}
