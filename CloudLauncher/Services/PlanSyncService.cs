using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Where a pack's live board sync stands, as the board's toolbar shows it.</summary>
public enum PlanSyncState
{
    /// <summary>Not synced at all: an instance only on this PC, or nobody signed in.</summary>
    Off,
    Syncing,
    Synced,
    /// <summary>The server can't be reached; changes are kept here and sent when it can.</summary>
    Offline,
    /// <summary>Changes from others arrive, but this account can't store any.</summary>
    ViewOnly,
    /// <summary>The server has no live boards (an older server), or the pack isn't on it.</summary>
    Unavailable
}

/// <summary>
/// Keeps a pack's planning boards in step with the server while they are open: every save is sent a
/// moment later, and a request held open on the server brings in what other PCs and collaborators
/// store the moment they store it.
/// </summary>
/// <remarks>
/// <para>Each PC remembers the copy it last agreed with the server on (its revision and content, in
/// <c>plans-sync.json</c> beside the pack's <c>game</c> folder, which never syncs). That is the base
/// of the three-way merge in <see cref="PlanMerge"/> when both sides changed, and it is how an edit
/// made offline is still sent later: the boards differ from that copy, so there is something to send.</para>
/// <para>Everything runs on the UI thread, where the boards are edited: the network waits are
/// awaited there and never block it.</para>
/// </remarks>
public sealed class PlanSyncService
{
    private const int PushDelayMs = 1200;
    private const int WaitSeconds = 25;

    private readonly ApiClient _api;
    private readonly PackFolderService _packs;
    private readonly ModPlanService _plans;
    private readonly Dictionary<Guid, PackSync> _state = new();

    /// <summary>A pack's sync state changed. Raised on the UI thread.</summary>
    public event Action<Guid>? StateChanged;

    public PlanSyncService(ApiClient api, PackFolderService packs, ModPlanService plans)
    {
        _api = api;
        _packs = packs;
        _plans = plans;
        _plans.Saved += id => OnUi(() => ScheduleSend(id));
        _plans.PackFilesReplaced += (id, plansReplaced) => OnUi(() => AfterPackDownload(id, plansReplaced));
    }

    private sealed class PackSync
    {
        public long Revision;
        public string? BaseJson;         // the copy last agreed with the server, null = never synced here
        public bool Loaded;              // Revision/BaseJson read from plans-sync.json
        public bool Agreed;              // talked to the server since the launcher started
        public bool Sending;
        public bool SendAgain;
        public TaskCompletionSource? SendDone;
        public DispatcherTimer? SendTimer;
        public int Watchers;
        public CancellationTokenSource? WatchCts;
        public bool ViewOnly;
        public bool Unavailable;
        public PlanSyncState State = PlanSyncState.Off;
        public DateTimeOffset? SyncedAt;
    }

    public PlanSyncState StateOf(Guid packId) => _state.TryGetValue(packId, out var s) ? s.State : PlanSyncState.Off;

    /// <summary>When the boards were last known to match the server.</summary>
    public DateTimeOffset? SyncedAt(Guid packId) => _state.TryGetValue(packId, out var s) ? s.SyncedAt : null;

    /// <summary>Keeps the pack's boards live while the returned handle is held: one request waiting on
    /// the server at a time, however many views hold a handle. Disposing the last one sends anything
    /// still waiting to go and stops listening.</summary>
    public IDisposable Watch(Guid packId)
    {
        var s = Get(packId);
        s.Watchers++;
        if (s.WatchCts is null)
        {
            s.WatchCts = new CancellationTokenSource();
            _ = WatchLoopAsync(packId, s, s.WatchCts.Token);
        }
        return new Handle(() =>
        {
            if (--s.Watchers > 0) return;
            s.WatchCts?.Cancel();
            s.WatchCts = null;
            if (s.SendTimer?.IsEnabled == true) { s.SendTimer.Stop(); _ = SendAsync(packId); }
        });
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }

    // ── when the sync applies ─────────────────────────────────────────────────

    private bool Applies(Guid packId, PackSync s) =>
        !s.Unavailable && _api.IsSignedIn && !_api.IsLocalPack(packId);

    private PackSync Get(Guid packId)
    {
        if (!_state.TryGetValue(packId, out var s)) _state[packId] = s = new PackSync();
        if (!s.Loaded) LoadBase(packId, s);
        return s;
    }

    private void SetState(Guid packId, PackSync s, PlanSyncState state)
    {
        if (state == PlanSyncState.Synced) s.SyncedAt = DateTimeOffset.Now;
        if (s.State == state) return;
        s.State = state;
        StateChanged?.Invoke(packId);
    }

    // ── sending ──────────────────────────────────────────────────────────────

    private void ScheduleSend(Guid packId)
    {
        var s = Get(packId);
        if (!Applies(packId, s)) return;
        if (s.SendTimer is null)
        {
            s.SendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PushDelayMs) };
            s.SendTimer.Tick += (_, _) => { s.SendTimer.Stop(); _ = SendAsync(packId); };
        }
        s.SendTimer.Stop();
        s.SendTimer.Start();
    }

    /// <summary>Sends the boards if they differ from the copy last agreed with the server, merging
    /// first whenever the server has moved on.</summary>
    private async Task SendAsync(Guid packId)
    {
        var s = Get(packId);
        if (!Applies(packId, s)) return;
        if (s.Sending) { s.SendAgain = true; return; }
        s.Sending = true;
        s.SendDone = new TaskCompletionSource();
        try
        {
            // Never send from a PC that hasn't heard what the server holds since it started: its copy
            // could be days behind, and would only come back as a conflict.
            if (!s.Agreed && !await PullAsync(packId, s)) return;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var json = _plans.CurrentJson(packId);
                var doc = ModPlanService.Parse(json);
                if (doc is null) return;
                if (s.BaseJson is { } baseJson && ModPlanService.Parse(baseJson) is { } agreed
                    && PlanMerge.SameContent(agreed, doc))
                {
                    SetState(packId, s, s.ViewOnly ? PlanSyncState.ViewOnly : PlanSyncState.Synced);
                    return;
                }
                if (s.ViewOnly) { SetState(packId, s, PlanSyncState.ViewOnly); return; }
                // Opening the tab makes an empty "Plan" board just to have something to show. That alone
                // isn't worth a row on the server; the first real edit is.
                if (s.Revision == 0 && s.BaseJson is null && IsUntouched(doc))
                {
                    SetState(packId, s, PlanSyncState.Synced);
                    return;
                }

                SetState(packId, s, PlanSyncState.Syncing);
                using var body = JsonDocument.Parse(json);
                var (stored, conflict) = await _api.PutPackPlansAsync(packId, s.Revision, body.RootElement);
                if (stored is not null)
                {
                    s.Revision = stored.Revision;
                    s.BaseJson = json;
                    SaveBase(packId, s);
                    SetState(packId, s, PlanSyncState.Synced);
                    return;
                }
                // Someone stored first: take their copy in, then send the merge on top of it.
                if (conflict is not null) Apply(packId, s, conflict);
            }
        }
        catch (Exception ex) { Failed(packId, s, ex); }
        finally
        {
            s.Sending = false;
            s.SendDone?.TrySetResult();
            if (s.SendAgain) { s.SendAgain = false; ScheduleSend(packId); }
        }
    }

    private static bool IsUntouched(PackPlans doc) =>
        doc.Boards.Count <= 1 && doc.BoardCategories.Count == 0
        && doc.Boards.All(b => b.IsEmpty && b.Name == "Plan" && b.Category is null);

    // ── receiving ────────────────────────────────────────────────────────────

    /// <summary>One plain read of the server's copy, merged in when it differs. False when the sync
    /// can't go on (offline, no access, no live boards on this server).</summary>
    private async Task<bool> PullAsync(Guid packId, PackSync s, CancellationToken ct = default)
    {
        try
        {
            var doc = await _api.GetPackPlansAsync(packId, s.BaseJson is null ? null : s.Revision, 0, ct);
            s.Agreed = true;
            if (doc is not null) Apply(packId, s, doc);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) { Failed(packId, s, ex, reading: true); return false; }
    }

    private async Task WatchLoopAsync(Guid packId, PackSync s, CancellationToken ct)
    {
        var backoff = TimeSpan.Zero;
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            if (!Applies(packId, s)) { SetState(packId, s, s.Unavailable ? PlanSyncState.Unavailable : PlanSyncState.Off); return; }
            try
            {
                if (first)
                {
                    // Catch up at once: what the server holds now, and anything this PC still has to send
                    // (made offline, or while the launcher was closed before it went).
                    if (s.State is PlanSyncState.Off) SetState(packId, s, PlanSyncState.Syncing);
                    if (!await PullAsync(packId, s, ct)) throw new RetryLaterException();
                    first = false;
                    await SendAsync(packId);
                    if (s.State == PlanSyncState.Syncing) SetState(packId, s, PlanSyncState.Synced);
                }

                var asked = s.Revision;
                var doc = await _api.GetPackPlansAsync(packId, asked, WaitSeconds, ct);
                backoff = TimeSpan.Zero;
                if (s.State is PlanSyncState.Offline) SetState(packId, s, s.ViewOnly ? PlanSyncState.ViewOnly : PlanSyncState.Synced);
                if (doc is null) continue;
                // The server only answers a wait early with a revision other than the one asked after.
                // Something in between that answers at once anyway mustn't turn this into a stream of
                // requests. (Our own store waking it is fine: that is a newer revision.)
                if (doc.Revision == asked) { await Task.Delay(TimeSpan.FromSeconds(5), ct); continue; }

                // Our own store wakes this request too, often before our PUT has had its answer. Let
                // the send finish, then only take what is still news.
                if (s.Sending && s.SendDone is { } done) await done.Task.WaitAsync(ct);
                if (doc.Revision != s.Revision) Apply(packId, s, doc);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (ex is not RetryLaterException) Failed(packId, s, ex, reading: true);
                if (!Applies(packId, s) || s.State is PlanSyncState.Off or PlanSyncState.Unavailable) return;
                backoff = backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
                first = true;
            }
        }
    }

    private sealed class RetryLaterException : Exception;

    /// <summary>Takes the server's document in: merged with this PC's boards against the last agreed
    /// copy, which it then becomes. Sends again when this PC has changes the server lacks.</summary>
    private void Apply(Guid packId, PackSync s, PackPlansDocument doc)
    {
        if (doc.Revision == 0 || doc.Plans is not { } plans)
        {
            // The server has no boards for this pack (yet, or any more): whatever this PC has seeds it.
            s.Revision = 0;
            s.BaseJson = null;
            SaveBase(packId, s);
            ScheduleSend(packId);
            return;
        }
        if (ModPlanService.Parse(plans.GetRawText()) is not { } remote) return;

        var local = _plans.Load(packId);
        var agreed = s.BaseJson is null ? null : ModPlanService.Parse(s.BaseJson);
        var merged = PlanMerge.Merge(agreed, local, remote);

        s.Revision = doc.Revision;
        s.BaseJson = ModPlanService.Serialize(remote);
        SaveBase(packId, s);

        if (!PlanMerge.SameContent(merged, local)) _plans.ReplaceFromSync(packId, merged);
        if (!PlanMerge.SameContent(merged, remote)) ScheduleSend(packId);
        else SetState(packId, s, s.ViewOnly ? PlanSyncState.ViewOnly : PlanSyncState.Synced);
    }

    /// <summary>A pack download rewrote plans.json with the copy stored in the pack's files, which is
    /// older than the live one whenever this PC has synced it. Put the live copy back before anything
    /// reads the old one as an edit, then catch up with the server.</summary>
    private void AfterPackDownload(Guid packId, bool plansReplaced)
    {
        var s = Get(packId);
        if (!plansReplaced || s.BaseJson is null || ModPlanService.Parse(s.BaseJson) is not { } agreed) return;
        var downloaded = _plans.Load(packId);
        agreed.LastBoardId = downloaded.LastBoardId;
        _plans.ReplaceFromSync(packId, agreed);
        if (s.Watchers > 0 && Applies(packId, s)) _ = PullAsync(packId, s);
    }

    /// <param name="reading">The failed call was a read, where a 403 means no access to the pack at
    /// all rather than access without uploading.</param>
    private void Failed(Guid packId, PackSync s, Exception ex, bool reading = false)
    {
        switch (ex)
        {
            case ApiException { Status: HttpStatusCode.Forbidden } when !reading:
                // Can see the pack but not store into it: keep receiving, keep changes here.
                s.ViewOnly = true;
                SetState(packId, s, PlanSyncState.ViewOnly);
                break;
            case ApiException { Status: HttpStatusCode.NotFound or HttpStatusCode.Forbidden }:
                s.Unavailable = true;
                SetState(packId, s, PlanSyncState.Unavailable);
                break;
            case ApiException { Status: HttpStatusCode.Unauthorized } or SessionExpiredException:
                SetState(packId, s, PlanSyncState.Off);
                break;
            case ApiException { Status: HttpStatusCode.Conflict } when _api.IsLocalPack(packId):
                SetState(packId, s, PlanSyncState.Off);
                break;
            default:
                AppLog.Log("plans", $"Board sync for {packId:N} failed: {ex.Message}");
                SetState(packId, s, PlanSyncState.Offline);
                break;
        }
    }

    // ── the last agreed copy, kept beside the pack ────────────────────────────

    private sealed record BaseFile(long Revision, string? Plans);

    private string? BasePath(Guid packId)
    {
        try { return Path.Combine(_packs.PackRoot(packId), "plans-sync.json"); }
        catch { return null; }
    }

    private void LoadBase(Guid packId, PackSync s)
    {
        s.Loaded = true;
        if (BasePath(packId) is not { } path || !File.Exists(path)) return;
        try
        {
            if (JsonSerializer.Deserialize<BaseFile>(File.ReadAllText(path)) is { } f)
                (s.Revision, s.BaseJson) = (f.Revision, f.Plans);
        }
        catch { /* unreadable: treated as never synced, so the server's copy wins */ }
    }

    private void SaveBase(Guid packId, PackSync s)
    {
        if (BasePath(packId) is not { } path) return;
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new BaseFile(s.Revision, s.BaseJson)));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort: worst case the next start merges without a base */ }
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
