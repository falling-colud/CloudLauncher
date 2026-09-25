using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CloudLauncher.Views;

namespace CloudLauncher.Services;

/// <summary>Installs a batch of mod updates, several at a time, with per-mod progress.</summary>
/// <remarks>A mod download is mostly latency (resolve the CurseForge URL, then fetch a small file), so
/// running several at once saves a lot of time. The limit is a user setting (Settings > Downloads, 1-9)
/// because the useful ceiling depends on the connection, and the stores answer bursts from one address
/// with 403s and 429s, which hits everyone sharing the launcher's server.</remarks>
public static class ModUpdateRunner
{
    public enum JobState { Waiting, Running, Done, Failed, Skipped }

    /// <summary>One mod's update, and where it has got to. Bound directly by the progress card.</summary>
    public sealed class Job : INotifyPropertyChanged
    {
        public Job(PackMod mod, ModVersion target)
        {
            Mod = mod;
            Target = target;
        }

        public PackMod Mod { get; }
        public ModVersion Target { get; }

        public string Name => Mod.DisplayName;
        public string VersionsLabel => $"{Mod.VersionLabel}  >  {Target.VersionNumber}";

        private JobState _state = JobState.Waiting;
        public JobState State { get => _state; private set => Set(ref _state, value, nameof(State), nameof(IsRunning), nameof(IsFinished)); }

        public bool IsRunning => State == JobState.Running;
        public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Skipped;

        private double _percent;
        /// <summary>0-100. Stays at 0 while the download's size is unknown.</summary>
        public double Percent { get => _percent; private set => Set(ref _percent, value, nameof(Percent)); }

        // Starts false so waiting jobs show an empty bar instead of looking like active downloads.
        private bool _indeterminate;
        public bool IsIndeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value, nameof(IsIndeterminate)); }

        private string _status = "Waiting...";
        public string Status { get => _status; private set => Set(ref _status, value, nameof(Status)); }

        internal void Begin()
        {
            State = JobState.Running;
            IsIndeterminate = true;
            Status = "Starting...";
        }

        internal void Report(long done, long total)
        {
            if (total <= 0)
            {
                IsIndeterminate = true;
                Status = $"{Bytes(done)} downloaded";
                return;
            }
            var pct = Math.Clamp(done * 100.0 / total, 0, 100);
            // Only notify on a visible change, otherwise a big batch floods the dispatcher and the window
            // stops painting.
            if (IsIndeterminate || Math.Abs(pct - Percent) >= 1 || pct >= 100)
            {
                IsIndeterminate = false;
                Percent = pct;
                Status = $"{Bytes(done)} / {Bytes(total)}";
            }
        }

        internal void Finish(JobState state, string status)
        {
            IsIndeterminate = false;
            Percent = state == JobState.Done ? 100 : Percent;
            Status = status;
            State = state;
        }

        private static string Bytes(long n) =>
            n >= 1024L * 1024 ? $"{n / 1024.0 / 1024:0.#} MB" : $"{Math.Max(1, n / 1024)} KB";

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Assigns and notifies on the UI thread, since the runner writes these from worker
        /// tasks.</summary>
        private void Set<T>(ref T field, T value, params string[] names)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Raise(names);
            else dispatcher.BeginInvoke(() => Raise(names));
        }

        private void Raise(string[] names)
        {
            foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }

    public sealed record Summary(int Updated, IReadOnlyList<string> Failed, bool Cancelled)
    {
        public string Describe() =>
            Failed.Count == 0
                ? (Cancelled ? $"Stopped after updating {Updated} mod(s)." : $"Updated {Updated} mod(s).")
                : $"Updated {Updated} mod(s); failed: {string.Join(", ", Failed)}";
    }

    /// <summary>
    /// Runs every job, at most <paramref name="concurrency"/> at a time. Never throws for a single
    /// mod: a failure is recorded on its job and in the summary so the rest of the batch still lands.
    /// </summary>
    public static async Task<Summary> RunAsync(
        IReadOnlyList<Job> jobs, int concurrency, CancellationToken ct = default)
    {
        concurrency = Math.Clamp(concurrency, AppSettings.MinModDownloadConcurrency, AppSettings.MaxModDownloadConcurrency);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var failed = new List<string>();
        var updated = 0;

        await Task.WhenAll(jobs.Select(async job =>
        {
            try { await gate.WaitAsync(ct); }
            catch (OperationCanceledException) { job.Finish(JobState.Skipped, "Cancelled"); return; }

            try
            {
                if (ct.IsCancellationRequested) { job.Finish(JobState.Skipped, "Cancelled"); return; }
                job.Begin();
                var progress = new Progress<(long done, long total)>(p => job.Report(p.done, p.total));
                if (await ModUpdater.InstallVersionAsync(job.Mod, job.Target, progress, ct))
                {
                    job.Finish(JobState.Done, "Updated");
                    Interlocked.Increment(ref updated);
                }
                else
                {
                    job.Finish(JobState.Failed, "No downloadable file");
                    lock (failed) failed.Add(job.Name);
                }
            }
            catch (OperationCanceledException)
            {
                job.Finish(JobState.Skipped, "Cancelled");
            }
            catch (Exception ex)
            {
                job.Finish(JobState.Failed, ex.Message);
                lock (failed) failed.Add($"{job.Name} ({ex.Message})");
            }
            finally { gate.Release(); }
        }));

        return new Summary(updated, failed, ct.IsCancellationRequested);
    }
}
