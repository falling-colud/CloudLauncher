using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// Makes a new instance that is a copy of an existing one: same Minecraft version and loader, same
/// description and icon, the same mods, configs and settings, and optionally its worlds.
/// </summary>
/// <remarks>
/// <para>The copy is a new instance of the user's own, never hosted: on the account when signed in,
/// on this PC otherwise, whoever owns the original. Nothing about the original's sharing comes along
/// (its sync record, collaborators, visibility).</para>
/// <para>Files are copied, not linked, so editing or patching a jar or a config in one instance never
/// changes the other. The copy runs as a <see cref="PackJobKind.Copy"/> job on the new instance, so its
/// card shows the progress with Pause and Stop; a copy that is stopped or fails takes the new instance
/// back again rather than leaving half of one.</para>
/// </remarks>
public sealed class InstanceDuplicateService(
    ApiClient api, PackFolderService folders, LocalPackStore localPacks, AppSettings settings)
{
    /// <summary>What to bring along besides the instance itself.</summary>
    public sealed record Options(string Name, bool IncludeWorlds, bool IncludeScreenshots);

    /// <summary>Raised on the UI thread when a copy has finished, successfully or not.</summary>
    public event Action<Guid, bool>? Finished;

    // Top-level entries of the pack folder that belong to the original only: its identity, its sync
    // record with the server, its dedicated-server run folder (rebuilt from game/ on every start), its
    // world backups, and bookkeeping of the file browser and editor.
    private static readonly HashSet<string> SkippedAtRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        ".packid", ".sync-manifest.json", "server-run", "shared", "backups", ".trash",
        ".editor-session.json", ".test-scope.json"
    };

    // Inside game/: what Minecraft writes about one particular session.
    private static readonly HashSet<string> SkippedInGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "crash-reports", "debug"
    };

    /// <summary>Where a local instance keeps its marker, relative to its folder.</summary>
    private static readonly string LocalMarker = Path.Combine(".cloudlauncher", LocalPackStore.MarkerFileName);

    /// <summary>Creates the new instance and starts copying into it. Returns as soon as the instance
    /// exists; the copy carries on in the background under its job.</summary>
    /// <exception cref="InvalidOperationException">The original has no folder on this PC, or is busy.</exception>
    public async Task<PackSummary> StartAsync(PackDetail source, Options options, CancellationToken ct = default)
    {
        var sourceRoot = LocalPackScanner.FindPackRoot(settings, source.Id)
                         ?? throw new InvalidOperationException("That instance has no folder on this PC, so there is nothing to copy.");

        var created = await api.CreatePackAsync(new CreatePackRequest(
            options.Name, source.Description, source.IsEmpty, source.MinecraftVersion, source.Loader,
            source.LoaderVersion, source.Summary), ct);
        folders.EnsurePackFolder(created.Id, created.Name, created.IsShared);
        CopySettings(source.Id, created.Id);
        settings.Save();

        var job = PackJobs.Start(created.Id, PackJobKind.Copy, created.Name);
        ProgressHub.Indeterminate(created.Id, $"Copying {source.Name}...");
        var targetRoot = folders.PackRoot(created.Id);
        _ = Task.Run(async () =>
        {
            var ok = false;
            try
            {
                await CopyAsync(sourceRoot, targetRoot, created.Id, options, job);
                ok = true;
                AppLog.Log("duplicate", $"Copied '{source.Name}' ({source.Id}) into '{created.Name}' ({created.Id}).");
                ProgressHub.Report(created.Id, 1, "Copied.");
            }
            catch (OperationCanceledException)
            {
                AppLog.Log("duplicate", $"Copy of '{source.Name}' into {created.Id} was stopped; taking the new instance back.");
            }
            catch (Exception ex)
            {
                AppLog.LogError("duplicate", ex);
            }
            finally
            {
                if (!ok) await TakeBackAsync(created.Id, targetRoot, job);
                ProgressHub.Clear(created.Id);
                PackJobs.Finish(job);
                RaiseFinished(created.Id, ok);
            }
        });
        return created;
    }

    /// <summary>The per-instance settings kept in settings.json rather than in the folder. Not the
    /// ones that describe the original's history or hosting: play time, pin, sync version, update
    /// policy, the hidden flag.</summary>
    private void CopySettings(Guid from, Guid to) => ForEachSetting(from, to, forget: false);

    /// <summary>Drops what <see cref="CopySettings"/> gave an instance that is being taken back.</summary>
    private void ForgetSettings(Guid id) => ForEachSetting(Guid.Empty, id, forget: true);

    private void ForEachSetting(Guid from, Guid to, bool forget)
    {
        void One<T>(IDictionary<Guid, T> map)
        {
            if (forget) map.Remove(to);
            else if (map.TryGetValue(from, out var v)) map[to] = v;
        }

        One(settings.PackMaxRamMb);
        One(settings.PackJvmArgs);
        One(settings.PackJavaPath);
        One(settings.PackServerMaxRamMb);
        One(settings.PackServerJvmArgs);
        One(settings.PackServerEulaAccepted);
        One(settings.PackServerAutoRestart);
        One(settings.PackAutoApplyRules);
        One(settings.PackMinecraftWindowPackPageCollapsedByDefault);
        One(settings.PackMinecraftWindowBorderlessFullscreenByDefault);

        // Filed in the same folders as the original, so the copy turns up next to it.
        foreach (var members in settings.PackFolders.Values)
        {
            if (forget) members.Remove(to);
            else if (members.Contains(from) && !members.Contains(to)) members.Add(to);
        }
    }

    /// <summary>Copies the original's files into the new folder, reporting progress on the new
    /// instance. Every file written is tracked on the job, so a stop can remove it.</summary>
    private static async Task CopyAsync(string sourceRoot, string targetRoot, Guid targetId, Options options, PackJob job)
    {
        var ct = job.Token;
        var files = await Task.Run(() => ListFiles(sourceRoot, options), ct);
        long total = files.Sum(f => f.Size), done = 0;
        var buffer = new byte[1 << 20];

        foreach (var (relative, size) in files)
        {
            await job.Gate.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();

            var from = Path.Combine(sourceRoot, relative);
            var to = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            // The folder skeleton the new instance was created with may already hold an empty copy of a
            // default file; anything else in the way would be a bug, and is replaced like it.
            if (!File.Exists(to)) job.TrackCreatedFile(to);

            try
            {
                // Shared read: the launcher, an editor or a running game may have the file open.
                await using var src = new FileStream(from, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None,
                    buffer.Length, FileOptions.Asynchronous);
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    ProgressHub.Report(targetId, total > 0 ? (double)done / total : -1, "Copying files...",
                        size > 0 ? (double)src.Position / size : -1, relative.Replace('\\', '/'));
                }
            }
            catch (FileNotFoundException) { continue; }          // gone since the listing: nothing to copy
            catch (DirectoryNotFoundException) { continue; }

            try { File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from)); }
            catch { /* the time is cosmetic */ }
        }
    }

    /// <summary>Every file to copy, relative to the pack folder, with its size.</summary>
    private static List<(string Relative, long Size)> ListFiles(string sourceRoot, Options options)
    {
        var skippedInGame = new HashSet<string>(SkippedInGame, StringComparer.OrdinalIgnoreCase);
        if (!options.IncludeWorlds) skippedInGame.Add("saves");
        if (!options.IncludeScreenshots) skippedInGame.Add("screenshots");

        var result = new List<(string, long)>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            var relDir = Path.GetRelativePath(sourceRoot, dir);
            var atRoot = relDir == ".";
            var inGameRoot = string.Equals(relDir, "game", StringComparison.OrdinalIgnoreCase);

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                // A junction or symlink inside an instance points somewhere else on the disk; following
                // it would copy (or loop over) something that isn't the instance's.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (atRoot && SkippedAtRoot.Contains(entry.Name)) continue;
                if (inGameRoot && skippedInGame.Contains(entry.Name)) continue;

                if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub.FullName);
                    continue;
                }

                var file = (FileInfo)entry;
                var relative = Path.GetRelativePath(sourceRoot, file.FullName);
                // The local-instance marker is the new store's to write; a world's lock belongs to the
                // game that holds it.
                if (string.Equals(relative, LocalMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(file.Name, "session.lock", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add((relative, file.Length));
            }
        }
        return result;
    }

    /// <summary>Undoes a copy that did not finish: its files, then the new instance itself.</summary>
    private async Task TakeBackAsync(Guid packId, string root, PackJob job)
    {
        try
        {
            job.RollbackCreatedFiles();
            if (localPacks.Contains(packId))
            {
                await localPacks.DeleteAsync(packId, toRecycleBin: false);
            }
            else
            {
                await api.DeletePackAsync(packId);
                // Everything left is the skeleton made a moment ago.
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                folders.InvalidateRootCache();
            }
            ForgetSettings(packId);
            settings.Save();
        }
        catch (Exception ex)
        {
            // The instance stays, marked as a failed copy by its name in the log; it can be deleted by hand.
            AppLog.Log("duplicate", $"Could not take back the unfinished copy {packId}: {ex.Message}");
        }
    }

    private void RaiseFinished(Guid packId, bool ok)
    {
        var app = System.Windows.Application.Current;
        if (app is null) { Finished?.Invoke(packId, ok); return; }
        app.Dispatcher.BeginInvoke(() => Finished?.Invoke(packId, ok));
    }
}
