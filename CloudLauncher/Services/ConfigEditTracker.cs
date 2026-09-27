using System.IO;
using System.Text.Json;
using CloudLauncher.Shared;
using Entry = CloudLauncher.Services.ConfigHubService.Entry;
using Kind = CloudLauncher.Services.ConfigHubService.FileKind;

namespace CloudLauncher.Services;

/// <summary>
/// Remembers what each config file looked like the first time the launcher saw it, so the Config
/// pages can say which files have been edited since.
/// </summary>
/// <remarks>
/// <para>A file is "edited" when its bytes differ from that first copy, its baseline. The first copy
/// is normally what the mod wrote on its first launch. A file created in the launcher has no
/// original and counts as edited until marked otherwise. Anything that rewrites a file counts: the
/// in-game config screen, the launcher's editor, a text editor, and also FML adding the new settings
/// of an updated mod. "Mark as not edited" resets the baseline to the file as it is now.</para>
/// <para>Only a hash is kept, not the original text, so the launcher can tell that a file changed
/// but not show how. A copy of every config would double the size of the config folders.</para>
/// <para>Stored per instance at <c>&lt;pack root&gt;/config-baselines.json</c>, beside
/// <c>game/</c> like <see cref="InstanceContentOverrides"/>: sync and export only ever read
/// <c>game/</c>, so the file never reaches a collaborator or a modpack. Duplicating an instance copies
/// it along, which is right, since the copy starts with the same files.</para>
/// <para>Tracking can only start when the launcher first looks, so edits made before then are
/// invisible. <see cref="Baselines.TrackingSinceUtc"/> records when that was, and the pages say
/// so.</para>
/// <para>Each file also keeps the size, write time and hash it had when last seen, so a rescan
/// re-hashes only files that changed.</para>
/// </remarks>
public static class ConfigEditTracker
{
    /// <summary>The file name inside the pack root. Beside <c>game/</c>, never inside it.</summary>
    public const string FileName = "config-baselines.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Serialises every read-modify-write of a baselines file. The page's background scan
    /// and the editor's save can both write one, and a lost write would forget a baseline.</summary>
    private static readonly object Gate = new();

    /// <summary>What is stored for one instance.</summary>
    public sealed class Baselines
    {
        /// <summary>Stored shape version. Adding optional properties does not need a bump.</summary>
        public int Version { get; set; } = 1;

        /// <summary>When the launcher first recorded this instance's files. Edits made before this
        /// can't be known.</summary>
        public DateTimeOffset TrackingSinceUtc { get; set; }

        /// <summary>Game-relative path (<c>config/jei/jei-client.toml</c>) -> its baseline.</summary>
        public Dictionary<string, FileBaseline> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One file's baseline, plus what it looked like when last seen.</summary>
    public sealed class FileBaseline
    {
        /// <summary>SHA-256 of the file when first seen, or null for a file created in the
        /// launcher.</summary>
        public string? Hash { get; set; }

        /// <summary>Created in the launcher: edited by definition, since no mod wrote it.</summary>
        public bool Created { get; set; }

        public DateTimeOffset FirstSeenUtc { get; set; }

        public long SeenSize { get; set; }
        public long SeenTicks { get; set; }
        public string? SeenHash { get; set; }
    }

    /// <summary>How a config compares with its copy in <c>defaultconfigs/</c>.</summary>
    public enum DefaultCopyState
    {
        /// <summary>FML never reads this file from <c>defaultconfigs/</c> (not a <c>.toml</c> under
        /// <c>config/</c> or a world's <c>serverconfig/</c>).</summary>
        NotApplicable,
        /// <summary>No copy there yet.</summary>
        Missing,
        Same,
        Differs,
        /// <summary>There is a copy, but one side could not be read to compare.</summary>
        Unknown
    }

    /// <summary>One file's answer.</summary>
    /// <param name="DefaultPath">Game-relative path of its <c>defaultconfigs/</c> counterpart, or null
    /// when FML would never look for one.</param>
    public sealed record FileEdit(
        bool Edited, bool Created, DateTimeOffset? FirstSeenUtc, string? DefaultPath, DefaultCopyState DefaultState);

    /// <summary>Every tracked file's answer across the given instances.</summary>
    /// <param name="ByFullPath">Keyed by <see cref="Entry.FullPath"/>, which is unique across
    /// instances.</param>
    /// <param name="TrackingSince">Per instance, when tracking began; absent while it hasn't.</param>
    public sealed record Snapshot(
        Dictionary<string, FileEdit> ByFullPath, Dictionary<Guid, DateTimeOffset> TrackingSince)
    {
        public static Snapshot Empty => new(new(StringComparer.OrdinalIgnoreCase), new());

        public FileEdit? For(Entry entry) => ByFullPath.GetValueOrDefault(entry.FullPath);
    }

    /// <summary>True for the kinds whose edits are tracked: everything but the KubeJS logs, which the
    /// game rewrites on every launch.</summary>
    public static bool IsTracked(Kind kind) => kind != Kind.KubeJsLog;

    // ── evaluating ───────────────────────────────────────────────────────────

    /// <summary>
    /// Works out which of <paramref name="files"/> are edited, recording a baseline for every file
    /// seen for the first time. Call from a background thread.
    /// </summary>
    /// <param name="rehash">False for the page's first frame: answer from what is stored, read no
    /// config and write nothing. A file seen for the first time then simply isn't edited yet.</param>
    /// <param name="progress">Reports the instance name as each one starts.</param>
    public static Snapshot Evaluate(
        IReadOnlyList<PackSummary> packs, IReadOnlyList<Entry> files, PackFolderService folders,
        bool rehash, IProgress<string>? progress, CancellationToken ct)
    {
        var result = Snapshot.Empty;
        var byPack = files.GroupBy(f => f.PackId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var pack in packs)
        {
            ct.ThrowIfCancellationRequested();
            if (!byPack.TryGetValue(pack.Id, out var packFiles) || packFiles.Count == 0) continue;
            string packRoot;
            try { packRoot = folders.PackRoot(pack.Id); }
            catch { continue; }

            if (rehash) progress?.Report(pack.Name);
            var since = EvaluatePack(packRoot, packFiles, rehash, result.ByFullPath, ct);
            if (since is { } when) result.TrackingSince[pack.Id] = when;
        }
        return result;
    }

    /// <returns>When tracking began for this instance, or null when it hasn't.</returns>
    private static DateTimeOffset? EvaluatePack(
        string packRoot, List<Entry> files, bool rehash, Dictionary<string, FileEdit> into, CancellationToken ct)
    {
        var stored = Load(packRoot);
        if (stored is null && !rehash) return null;
        var now = DateTimeOffset.UtcNow;
        var doc = stored ?? new Baselines { TrackingSinceUtc = now };

        // What each file holds now, by relative path, for both the edit check and the defaultconfigs
        // compare. Null means unknown: too large, or locked by a running game.
        var current = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var updates = new Dictionary<string, FileBaseline>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsTracked(entry.Kind)) continue;
            if (entry.Size > ConfigHubService.MaxInspectBytes)
            {
                // Listed but never hashed; the null still tells the defaultconfigs compare there is a
                // copy.
                current[entry.RelativePath] = null;
                continue;
            }

            doc.Files.TryGetValue(entry.RelativePath, out var baseline);
            var unchanged = baseline is not null
                            && baseline.SeenSize == entry.Size
                            && baseline.SeenTicks == entry.ModifiedUtc.Ticks
                            && baseline.SeenHash is not null;

            string? hash;
            if (unchanged) hash = baseline!.SeenHash;
            else if (!rehash) hash = baseline?.SeenHash;
            else hash = ConfigHubService.HashOrNull(entry.FullPath);
            current[entry.RelativePath] = hash;

            if (!rehash || unchanged || hash is null) continue;

            // First sighting, or a file that has changed since it was last seen.
            var next = baseline is null
                ? new FileBaseline { Hash = hash, FirstSeenUtc = now }
                : new FileBaseline { Hash = baseline.Hash, Created = baseline.Created, FirstSeenUtc = baseline.FirstSeenUtc };
            next.SeenSize = entry.Size;
            next.SeenTicks = entry.ModifiedUtc.Ticks;
            next.SeenHash = hash;
            updates[entry.RelativePath] = next;
            doc.Files[entry.RelativePath] = next;
        }

        if (updates.Count > 0 || stored is null) doc = Merge(packRoot, doc.TrackingSinceUtc, updates);

        foreach (var entry in files)
        {
            if (!IsTracked(entry.Kind)) continue;
            if (!doc.Files.TryGetValue(entry.RelativePath, out var baseline)) continue;
            var hash = current.GetValueOrDefault(entry.RelativePath);

            var edited = baseline.Created || (baseline.Hash is not null && hash is not null && hash != baseline.Hash);
            var defaultPath = ConfigHubService.DefaultConfigsPath(entry);
            var defaultState = DefaultCopyState.NotApplicable;
            if (defaultPath is not null)
            {
                defaultState = !current.TryGetValue(defaultPath, out var defaultHash)
                    ? DefaultCopyState.Missing
                    : defaultHash is null || hash is null
                        ? DefaultCopyState.Unknown
                        : defaultHash == hash ? DefaultCopyState.Same : DefaultCopyState.Differs;
            }
            into[entry.FullPath] = new FileEdit(edited, baseline.Created, baseline.FirstSeenUtc, defaultPath, defaultState);
        }
        return doc.TrackingSinceUtc;
    }

    /// <summary>
    /// Writes <paramref name="updates"/> over the file as it is on disk now, rather than over the copy
    /// read at the start of the scan, so a baseline the editor recorded meanwhile is kept.
    /// </summary>
    /// <remarks>A baseline already on disk always wins over a first sighting: it is older, so it is
    /// the better guess at what the mod wrote.</remarks>
    private static Baselines Merge(string packRoot, DateTimeOffset trackingSince, Dictionary<string, FileBaseline> updates)
    {
        lock (Gate)
        {
            var latest = Load(packRoot) ?? new Baselines { TrackingSinceUtc = trackingSince };
            foreach (var (rel, update) in updates)
            {
                if (latest.Files.TryGetValue(rel, out var existing))
                {
                    existing.SeenSize = update.SeenSize;
                    existing.SeenTicks = update.SeenTicks;
                    existing.SeenHash = update.SeenHash;
                }
                else latest.Files[rel] = update;
            }
            Save(packRoot, latest);
            return latest;
        }
    }

    // ── changes made from the pages ──────────────────────────────────────────

    /// <summary>"Mark as not edited": makes each file's current content its new baseline.</summary>
    public static void MarkNotEdited(string packRoot, IEnumerable<Entry> files) =>
        Accept(packRoot, files.Select(e => (e.RelativePath, e.FullPath)));

    /// <summary>
    /// Makes each file's current content its baseline. Used by "Mark as not edited", and after the
    /// launcher itself writes a file into defaultconfigs.
    /// </summary>
    /// <remarks>A defaultconfigs copy the launcher just wrote is the user's chosen default, not an
    /// edit to it; without this, replacing an old default would list the new one as edited.</remarks>
    public static void Accept(string packRoot, IEnumerable<(string RelativePath, string FullPath)> files)
    {
        lock (Gate)
        {
            var doc = Load(packRoot) ?? new Baselines { TrackingSinceUtc = DateTimeOffset.UtcNow };
            foreach (var (relativePath, fullPath) in files)
            {
                var hash = ConfigHubService.HashOrNull(fullPath);
                if (hash is null) continue;
                var firstSeen = doc.Files.TryGetValue(relativePath, out var old)
                    ? old.FirstSeenUtc
                    : DateTimeOffset.UtcNow;
                FileInfo info;
                try { info = new FileInfo(fullPath); }
                catch { continue; }
                doc.Files[relativePath] = new FileBaseline
                {
                    Hash = hash, FirstSeenUtc = firstSeen,
                    SeenSize = info.Length, SeenTicks = info.LastWriteTimeUtc.Ticks, SeenHash = hash
                };
            }
            Save(packRoot, doc);
        }
    }

    /// <summary>Records a file the user has just created in the launcher, so it counts as edited: no
    /// mod wrote it.</summary>
    public static void MarkCreated(string packRoot, string relativePath)
    {
        lock (Gate)
        {
            var doc = Load(packRoot) ?? new Baselines { TrackingSinceUtc = DateTimeOffset.UtcNow };
            doc.Files[relativePath] = new FileBaseline { Created = true, FirstSeenUtc = DateTimeOffset.UtcNow };
            Save(packRoot, doc);
        }
    }

    /// <summary>
    /// Called by the editor just before it saves a file, so a config opened and edited before the
    /// Config pages ever scanned it still has its original recorded. Never throws.
    /// </summary>
    /// <remarks>Does nothing for a file that already has a baseline, or one outside the folders the
    /// Config pages list.</remarks>
    public static void RememberBeforeWrite(PackFolderService folders, Guid packId, string fullPath)
    {
        try
        {
            var gameDir = folders.GameDir(packId);
            var rel = Path.GetRelativePath(gameDir, fullPath).Replace('\\', '/');
            if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return;
            if (ConfigHubService.KindForPath(rel) is not { } kind || !IsTracked(kind)) return;

            var packRoot = folders.PackRoot(packId);
            lock (Gate)
            {
                var doc = Load(packRoot) ?? new Baselines { TrackingSinceUtc = DateTimeOffset.UtcNow };
                if (doc.Files.ContainsKey(rel)) return;
                doc.Files[rel] = File.Exists(fullPath)
                    ? new FileBaseline { Hash = ConfigHubService.HashOrNull(fullPath), FirstSeenUtc = DateTimeOffset.UtcNow }
                    : new FileBaseline { Created = true, FirstSeenUtc = DateTimeOffset.UtcNow };
                Save(packRoot, doc);
            }
        }
        catch (Exception ex) { AppLog.LogError("config-edits", ex); }
    }

    /// <summary>
    /// Forgets the baselines of files a sync has just replaced, so the next scan takes the synced
    /// copy as the new original. Never throws.
    /// </summary>
    /// <remarks>"Edited" means edited on this PC. Without this, every config a collaborator changed
    /// would show as edited here after the next download.</remarks>
    public static void ForgetSynced(string packRoot, IReadOnlyCollection<string> relativePaths)
    {
        if (relativePaths.Count == 0) return;
        try
        {
            lock (Gate)
            {
                var doc = Load(packRoot);
                if (doc is null) return;
                var removed = 0;
                foreach (var rel in relativePaths)
                    if (doc.Files.Remove(rel.Replace('\\', '/'))) removed++;
                if (removed > 0) Save(packRoot, doc);
            }
        }
        catch (Exception ex) { AppLog.LogError("config-edits", ex); }
    }

    // ── storage ──────────────────────────────────────────────────────────────

    /// <summary>Reads one instance's baselines, or null when tracking hasn't started. An unreadable
    /// file is logged and also reads as null, so tracking starts again rather than failing every
    /// scan.</summary>
    private static Baselines? Load(string packRoot)
    {
        try
        {
            var path = Path.Combine(packRoot, FileName);
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<Baselines>(File.ReadAllText(path), JsonOpts);
            if (doc is null) return null;
            // A hand-edited file may have lost the case-insensitive comparer along with its keys.
            doc.Files = new Dictionary<string, FileBaseline>(doc.Files ?? new(), StringComparer.OrdinalIgnoreCase);
            return doc;
        }
        catch (Exception ex)
        {
            AppLog.LogError("config-edits", ex);
            return null;
        }
    }

    /// <summary>Writes the file atomically. Failure is logged, not thrown: the worst case is one
    /// scan's first sightings being recorded again next time.</summary>
    private static void Save(string packRoot, Baselines doc)
    {
        try
        {
            if (!Directory.Exists(packRoot)) return;
            var path = Path.Combine(packRoot, FileName);
            var tmp = path + ".cl-tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { AppLog.LogError("config-edits", ex); }
    }
}
