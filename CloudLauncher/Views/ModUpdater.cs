using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>The outcome of checking one mod: the version to move to (null when it is current), or
/// <see cref="Failed"/> when its store couldn't be asked, which is not the same as "current".
/// <see cref="Error"/> says why, for the log.</summary>
public readonly record struct ModUpdateCheckResult(ModVersion? Update, bool Failed, string? Error = null);

/// <summary>What one update check over a list of mods found, for the status line and the log.</summary>
public sealed record ModUpdateCheckSummary(
    int Total, int Failed, int Updates, int OneByOne, TimeSpan Elapsed, BulkCheckReport Bulk)
{
    /// <summary>"12 mods could not be checked", or null when every mod was.</summary>
    public string? CouldNotCheck => Failed == 0 ? null : $"{ModUpdater.Mods(Failed)} could not be checked";
}

/// <summary>Shared mod-update helpers: fetching every published version (all channels), checking
/// mods for updates, and installing a chosen version into the pack, replacing the current jar.</summary>
public static class ModUpdater
{
    /// <summary>All published versions of a mod, every release channel, unfiltered by MC/loader
    /// (the picker filters client-side so it can also show "all"). Served from the session catalog.</summary>
    public static async Task<List<ModVersion>> FetchVersionsAsync(ModSummary mod, bool forceRefresh = false)
    {
        try { return (await App.State.ModVersions.GetVersionsAsync(mod, forceRefresh: forceRefresh)).ToList(); }
        catch { return new List<ModVersion>(); }
    }

    /// <summary>The newest version <paramref name="mod"/> should move to, or null when it is current
    /// or its store couldn't be asked. <see cref="CheckUpdateAsync"/> tells those two apart.</summary>
    public static async Task<ModVersion?> FindUpdateAsync(PackMod mod, string? mcVersion, string? loader,
        bool forceRefresh = false, CancellationToken ct = default) =>
        (await CheckUpdateAsync(mod, mcVersion, loader, forceRefresh, ct)).Update;

    /// <summary>Checks one mod against the stores. Versions come from the shared catalog (the bulk
    /// pass's answer when fresh, otherwise fetched at the update-check pace), so every page sees the
    /// same answer.</summary>
    /// <remarks>A mod whose store couldn't be asked comes back <see cref="ModUpdateCheckResult.Failed"/>,
    /// not as "no update", so a rate-limited pass doesn't read as "everything is up to date".</remarks>
    public static async Task<ModUpdateCheckResult> CheckUpdateAsync(PackMod mod, string? mcVersion, string? loader,
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (mod.PrimaryMod is null || mod.PrimaryVersion is null) return default;
        try
        {
            var versions = await App.State.ModVersions.GetUpdateCandidatesAsync(mod.PrimaryMod, mcVersion, loader, forceRefresh, ct,
                mod.EffectiveUpdateChannel);
            return new ModUpdateCheckResult(PickUpdate(mod, versions, mcVersion, loader), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new ModUpdateCheckResult(null, true, ex.Message); }
    }

    /// <summary>The update rule shared by every page: the newest version compatible with the pack's
    /// Minecraft version and loader, published after the installed file, on a channel the mod's effective
    /// update channel admits (release only by default; beta adds betas; alpha adds everything).</summary>
    public static ModVersion? PickUpdate(PackMod mod, IEnumerable<ModVersion> versions, string? mcVersion, string? loader)
    {
        var installed = mod.PrimaryVersion;
        if (installed is null) return null;
        var channel = mod.EffectiveUpdateChannel;
        return versions
            .Where(v => v.Id != installed.Id
                        && IsCompatible(v, mcVersion, loader)
                        && v.DatePublished > installed.DatePublished
                        && ModUpdateChannel.Admits(channel, v.ReleaseChannel))
            .OrderByDescending(v => v.DatePublished)
            .FirstOrDefault();
    }

    /// <summary>
    /// Checks a list of mods: one bulk pass for whatever the stores can answer that way, then every mod
    /// through <see cref="CheckUpdateAsync"/>'s rule, most straight from the bulk answers and the rest
    /// one at a time at the update-check pace.
    /// </summary>
    /// <remarks>
    /// <para>Runs off the calling thread. <paramref name="onAnswer"/> is called from worker threads for
    /// each mod that got an answer (update or not), never for a failed one, so a caller keeps an update
    /// it was already showing.</para>
    /// <para>With <paramref name="forceRefresh"/>, mods the bulk pass answered aren't asked again; only
    /// the ones it couldn't answer are re-fetched.</para>
    /// <para>Answers already in hand skip the concurrency gate so they don't queue behind slow
    /// requests.</para>
    /// </remarks>
    public static Task<ModUpdateCheckSummary> CheckManyAsync(IReadOnlyList<PackMod> mods, string? mcVersion,
        string? loader, bool forceRefresh, ModUpdateCheckProgress progress, Action<PackMod, ModVersion?> onAnswer,
        CancellationToken ct = default)
    {
        progress.Begin(mods.Count);
        return Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            var catalog = App.State.ModVersions;
            var identified = mods.Where(m => m.PrimaryMod is not null && m.PrimaryVersion is not null).ToList();
            progress.Skip(mods.Count - identified.Count); // external jars: no store to ask

            var items = identified
                .Select(m => new UpdateCheckItem(m.PrimaryMod!, m.PrimaryVersion!,
                    m.PrimarySource == ModSource.Modrinth ? ModrinthHash(m) : null, m.EffectiveUpdateChannel))
                .ToList();
            BulkCheckReport bulk;
            try { bulk = await catalog.PrefetchLatestAsync(items, mcVersion, loader, forceRefresh, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLog.LogError("update check (bulk pass)", ex);
                bulk = BulkCheckReport.None;
            }
            progress.EndBulk();

            var width = App.State.Settings.EffectiveModUpdateCheckConcurrency;
            using var gate = new SemaphoreSlim(width, width);
            var updates = 0;
            var oneByOne = 0;
            string? firstError = null;
            await Task.WhenAll(identified.Select(async mod =>
            {
                try
                {
                    var force = forceRefresh && !bulk.IsAnswered(mod.PrimaryMod!);
                    if (!force && catalog.TryGetCachedCandidates(mod.PrimaryMod!, mcVersion, loader, out var known, mod.EffectiveUpdateChannel))
                    {
                        Answer(mod, PickUpdate(mod, known, mcVersion, loader));
                        return;
                    }

                    Interlocked.Increment(ref oneByOne);
                    await gate.WaitAsync(ct);
                    try
                    {
                        var result = await CheckUpdateAsync(mod, mcVersion, loader, force, ct);
                        if (result.Failed)
                        {
                            progress.NoteFailed();
                            Interlocked.CompareExchange(ref firstError, $"{mod.DisplayName}: {result.Error}", null);
                        }
                        else Answer(mod, result.Update);
                    }
                    finally { gate.Release(); }
                }
                finally { progress.NoteDone(); }
            }));

            var summary = new ModUpdateCheckSummary(mods.Count, progress.Failed, updates, oneByOne, clock.Elapsed, bulk);
            var line = $"Checked {mods.Count} mod(s) for {mcVersion ?? "any version"}/{loader ?? "no loader"} in {Duration(summary.Elapsed)}" +
                       (forceRefresh ? " (refresh)" : "") +
                       $": {bulk.Answered} answered in bulk ({bulk.AnsweredByModrinth} Modrinth, {bulk.AnsweredByCurseForge} CurseForge)" +
                       $" with {bulk.Requests} request(s) in {Duration(bulk.Elapsed)}, {bulk.AlreadyKnown} already known," +
                       $" {oneByOne} asked one at a time, {summary.Failed} failed, {updates} update(s).";
            if (bulk.ModrinthProblem is { } mr) line += $" Modrinth bulk pass: {mr.TrimEnd('.')}.";
            if (bulk.CurseForgeProblem is { } cf) line += $" CurseForge bulk pass: {cf.TrimEnd('.')}.";
            if (firstError is { } first) line += $" First failure: {first.TrimEnd('.')}.";
            AppLog.Log("updates", line);
            return summary;

            void Answer(PackMod mod, ModVersion? update)
            {
                if (update is not null) Interlocked.Increment(ref updates);
                onAnswer(mod, update);
            }
        }, CancellationToken.None);
    }

    /// <summary>The SHA-512 Modrinth's bulk lookup is keyed by: the jar's own (from the fingerprint
    /// cache the identity pass just filled), else any file of the installed version. The store answers
    /// per project, so another file of the same version works too.</summary>
    private static string? ModrinthHash(PackMod mod)
    {
        if (App.State.ModFingerprints.TryGet(mod.FilePath, out var entry) && entry.Sha512 is { Length: > 0 } sha)
            return sha;
        var files = mod.PrimaryVersion?.Files;
        return files?.FirstOrDefault(f => f.IsPrimary && !string.IsNullOrEmpty(f.Sha512))?.Sha512
               ?? files?.FirstOrDefault(f => !string.IsNullOrEmpty(f.Sha512))?.Sha512;
    }

    /// <summary>"1 mod" / "1,900 mods".</summary>
    public static string Mods(int count) => count == 1 ? "1 mod" : $"{count:N0} mods";

    /// <summary>"0.4 s", "12 s", "2 min 5 s".</summary>
    public static string Duration(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 10 ? $"{elapsed.TotalSeconds:0.0} s"
        : elapsed.TotalSeconds < 90 ? $"{elapsed.TotalSeconds:0} s"
        : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";

    public static bool IsCompatible(ModVersion v, string? mc, string? loader) => ModVersionCatalog.IsCompatible(v, mc, loader);

    /// <summary>The loader tag the stores use for a pack ("neoforge", "fabric", ...), or null for
    /// vanilla.</summary>
    public static string? LoaderTag(CloudLauncher.Shared.PackDetail pack) =>
        pack.Loader == CloudLauncher.Shared.LoaderKind.None ? null : pack.Loader.ToString().ToLowerInvariant();

    /// <summary>Downloads <paramref name="version"/> into the mod's folder and removes the old jar.
    /// Returns false if there's nothing downloadable. <paramref name="progress"/> reports the
    /// download's bytes so a bulk update can show a bar per mod.</summary>
    public static async Task<bool> InstallVersionAsync(PackMod mod, ModVersion version,
        IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (file is null) return false;

        file = await EnsureDownloadableAsync(version, file, ct);
        if (string.IsNullOrWhiteSpace(file.DownloadUrl)) return false;

        // Keep the mod's enabled state: a disabled mod lives as "<name>.jar.disabled", and writing the
        // update as a plain .jar would re-enable it.
        // The file name comes from the store, so it must be one plain name: a path, drive, stream or
        // device name in it could write outside the mods folder.
        var fileName = mod.Enabled ? file.Filename : file.Filename + ".disabled";
        if (!PathSafety.IsSafeFileName(file.Filename)
            || PathSafety.ResolveFileName(Path.GetDirectoryName(mod.FilePath)!, fileName) is not { } dest)
        {
            AppLog.Log("updates", $"Did not update {mod.DisplayName}: the store's file name is not a plain file name: {file.Filename}");
            throw new InvalidOperationException("The store gave a file name that cannot be used.");
        }
        await App.State.Modrinth.DownloadFileAsync(file.DownloadUrl, dest, progress, ct);
        if (!string.Equals(mod.FilePath, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(mod.FilePath))
            File.Delete(mod.FilePath);
        return true;
    }

    private static async Task<ModVersionFile> EnsureDownloadableAsync(ModVersion version, ModVersionFile file,
        CancellationToken ct = default)
    {
        if (version.Source != ModSource.CurseForge || !string.IsNullOrWhiteSpace(file.DownloadUrl)) return file;
        var ids = version.Id.Split(':');
        if (ids.Length != 2 || !int.TryParse(ids[0], out var modId) || !int.TryParse(ids[1], out var fileId)) return file;
        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId, ct);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }
}

/// <summary>Live counters of a running update check, safe to read from the UI thread while worker
/// threads move them.</summary>
public sealed class ModUpdateCheckProgress
{
    private int _total, _done, _failed, _asking;

    public int Total => Volatile.Read(ref _total);
    public int Done => Volatile.Read(ref _done);
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>True while the bulk pass is out, before any mod has its answer.</summary>
    public bool Asking => Volatile.Read(ref _asking) != 0;

    /// <summary>"Checking 1,900 mods for updates..." while the bulk pass is out, then
    /// "Checking for updates... 1,234 of 1,900".</summary>
    public string Describe() => Asking
        ? $"Checking {ModUpdater.Mods(Total)} for updates..."
        : $"Checking for updates... {Done:N0} of {Total:N0}";

    internal void Begin(int total)
    {
        Volatile.Write(ref _total, total);
        Volatile.Write(ref _done, 0);
        Volatile.Write(ref _failed, 0);
        Volatile.Write(ref _asking, 1);
    }

    internal void EndBulk() => Volatile.Write(ref _asking, 0);
    internal void NoteDone() => Interlocked.Increment(ref _done);
    internal void Skip(int count) => Interlocked.Add(ref _done, count);
    internal void NoteFailed() => Interlocked.Increment(ref _failed);
}

/// <summary>
/// Carries a running update check's answers onto the UI thread in batches, ten times a second, and
/// repaints its progress on the same beat.
/// </summary>
/// <remarks>
/// Bulk answers arrive within milliseconds of each other. One dispatcher call per answer would queue
/// thousands of callbacks on a big pack; ten batches a second still looks live.
/// </remarks>
public sealed class ModUpdateCheckPump
{
    private readonly ConcurrentQueue<(PackMod Mod, ModVersion? Latest)> _answers = new();
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _stale;
    private readonly Action<ModUpdateCheckProgress>? _onTick;

    public ModUpdateCheckProgress Progress { get; } = new();

    /// <param name="stale">True once the answers are no longer wanted (the list was re-scanned); they
    /// are then dropped rather than applied.</param>
    /// <param name="onTick">Repaints the progress; called on the UI thread on every beat.</param>
    public ModUpdateCheckPump(Dispatcher dispatcher, Func<bool> stale, Action<ModUpdateCheckProgress>? onTick = null)
    {
        _stale = stale;
        _onTick = onTick;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Drain(tick: true);
        _timer.Start();
    }

    /// <summary>Queues one answer. Any thread.</summary>
    public void Post(PackMod mod, ModVersion? latest) => _answers.Enqueue((mod, latest));

    /// <summary>Stops the beat and applies whatever is still queued. UI thread.</summary>
    public void Stop()
    {
        _timer.Stop();
        Drain(tick: false);
    }

    private void Drain(bool tick)
    {
        var stale = _stale();
        while (_answers.TryDequeue(out var answer))
            if (!stale) answer.Mod.LatestVersion = answer.Latest;
        if (tick && !stale) _onTick?.Invoke(Progress);
    }
}
