using System.IO;
using System.Windows;

namespace CloudLauncher.Services;

/// <summary>An async gate a long-running transfer awaits at its safe points, so it can be paused
/// mid-flight and resumed.</summary>
/// <remarks>The wait takes the job's cancellation token, so a paused transfer can be stopped without
/// resuming it first.</remarks>
public sealed class PauseGate
{
    private readonly object _gate = new();

    /// <summary>Non-null while paused; completing it releases the waiters.</summary>
    private TaskCompletionSource? _resume;

    public bool IsPaused
    {
        get { lock (_gate) return _resume is not null; }
    }

    public void Pause()
    {
        lock (_gate)
            _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? resume;
        lock (_gate) { resume = _resume; _resume = null; }
        resume?.TrySetResult();
    }

    /// <summary>Completes immediately unless paused. When paused, completes on resume, or throws when
    /// the job is stopped.</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        Task? resume;
        lock (_gate) resume = _resume?.Task;
        return resume is null ? Task.CompletedTask : resume.WaitAsync(ct);
    }
}

public enum PackJobKind
{
    /// <summary>Downloading a modpack from a store and importing it into a new instance.</summary>
    Download,
    /// <summary>Pulling a shared instance's files down from the CloudLauncher server.</summary>
    Sync,
    /// <summary>Pushing a shared instance's files up to the CloudLauncher server.</summary>
    Upload,
    /// <summary>Writing the instance out as a CurseForge or Modrinth modpack (see
    /// <see cref="ModpackExportService"/>).</summary>
    /// <remarks>Registered so nothing that checks <see cref="PackJobs.IsRunning"/> rewrites
    /// the instance mid-export. It only reads, so it doesn't invalidate scans or replace Play
    /// on the instance page.</remarks>
    Export
}

/// <summary>A pausable, stoppable operation running against one instance. The worker holds
/// <see cref="Token"/> and awaits <see cref="Gate"/>; every view that shows the operation drives it
/// through this object.</summary>
public sealed class PackJob
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Files this job created (not there before it started). A stop deletes them.</summary>
    private readonly List<string> _created = new();

    internal PackJob(Guid packId, PackJobKind kind, string? what)
    {
        PackId = packId;
        Kind = kind;
        What = what;
        // A copy of the struct, so reading it stays valid for the job's whole life.
        Token = _cts.Token;
    }

    public Guid PackId { get; }
    public PackJobKind Kind { get; }

    /// <summary>What is being transferred (a pack or file name), for tooltips.</summary>
    public string? What { get; }

    public PauseGate Gate { get; } = new();
    public CancellationToken Token { get; }
    public bool IsPaused => Gate.IsPaused;

    /// <summary>Set the moment Stop is pressed, so the UI stops offering Pause on a dying job.</summary>
    public bool IsStopping { get; private set; }

    public string KindLabel => Kind switch
    {
        PackJobKind.Download => "download",
        PackJobKind.Sync => "update",
        PackJobKind.Export => "export",
        _ => "upload"
    };

    public void Pause()
    {
        if (IsStopping) return;
        Gate.Pause();
        PackJobs.NotifyChanged(PackId);
    }

    public void Resume()
    {
        Gate.Resume();
        PackJobs.NotifyChanged(PackId);
    }

    public void Stop()
    {
        if (IsStopping) return;
        IsStopping = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        // Cancel before releasing, so a paused job wakes into the cancellation, not one more file.
        Gate.Resume();
        PackJobs.NotifyChanged(PackId);
    }

    /// <summary>Records a file this job created. Only for paths that didn't exist before:
    /// <see cref="RollbackCreatedFiles"/> deletes them, and a file that was already there is the
    /// user's.</summary>
    public void TrackCreatedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_created) _created.Add(path);
    }

    /// <summary>Deletes the files this job created and prunes directories left empty, so a stopped
    /// transfer leaves the instance as it was. Returns how many files were deleted.</summary>
    /// <remarks>Files the job replaced are left alone: restoring them would need a backup, and the new
    /// copy is a complete, hash-verified file anyway.</remarks>
    public int RollbackCreatedFiles()
    {
        string[] paths;
        lock (_created)
        {
            paths = _created.ToArray();
            _created.Clear();
        }

        var removed = 0;
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                // A .part / .cldownload sibling can outlive a hard stop mid-write.
                foreach (var candidate in new[] { path, path + ".part", path + ".cldownload" })
                    if (File.Exists(candidate)) { File.Delete(candidate); removed++; }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
            }
            catch { /* a file the game already has open is not worth failing the rollback over */ }
        }

        // Deepest first, so mods/foo/bar empties before mods/foo.
        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { /* best effort */ }
        }

        return removed;
    }
}

/// <summary>The registry of running transfers, keyed by instance. Like <see cref="ProgressHub"/>, it
/// is shared by every view that shows a transfer, none of which owns it.</summary>
public static class PackJobs
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, PackJob> Running = new();

    /// <summary>Raised on the UI thread when a pack's job starts, pauses, resumes or ends.</summary>
    public static event Action<Guid>? Changed;

    public static PackJob? For(Guid packId)
    {
        lock (Gate) return Running.TryGetValue(packId, out var job) ? job : null;
    }

    public static bool IsRunning(Guid packId) => For(packId) is not null;

    /// <summary>Registers a job for the pack. A second job for the same pack replaces the entry instead
    /// of throwing, so the buttons drive the latest transfer.</summary>
    public static PackJob Start(Guid packId, PackJobKind kind, string? what = null)
    {
        var job = new PackJob(packId, kind, what);
        lock (Gate) Running[packId] = job;
        NotifyChanged(packId);
        return job;
    }

    /// <summary>Removes the job once its worker has actually finished. Idempotent.</summary>
    /// <remarks>After a download or sync it also drops the pack's cached folder scans, since content
    /// pages paint from <see cref="ScanCaches"/> first and would show stale files. Upload and Export
    /// only read, so they are left alone.</remarks>
    public static void Finish(PackJob job)
    {
        lock (Gate)
        {
            // Only clear the entry if it is still this job's: a newer transfer may own it now.
            if (Running.TryGetValue(job.PackId, out var current) && ReferenceEquals(current, job))
                Running.Remove(job.PackId);
        }

        if (job.Kind is PackJobKind.Download or PackJobKind.Sync)
            ScanCaches.InvalidatePack(job.PackId, ScanScope.All);

        NotifyChanged(job.PackId);
    }

    public static void Pause(Guid packId) => For(packId)?.Pause();
    public static void Resume(Guid packId) => For(packId)?.Resume();
    public static void Stop(Guid packId) => For(packId)?.Stop();

    /// <summary>Pauses when running, resumes when paused.</summary>
    public static void TogglePause(Guid packId)
    {
        var job = For(packId);
        if (job is null) return;
        if (job.IsPaused) job.Resume(); else job.Pause();
    }

    internal static void NotifyChanged(Guid packId)
    {
        var app = Application.Current;
        if (app is null) { Changed?.Invoke(packId); return; }
        if (app.Dispatcher.CheckAccess()) Changed?.Invoke(packId);
        else app.Dispatcher.BeginInvoke(() => Changed?.Invoke(packId));
    }
}
