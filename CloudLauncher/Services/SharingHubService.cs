using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>How somebody ended up with access to a thing.</summary>
public enum SharingGrant
{
    /// <summary>It is mine.</summary>
    Owner = 0,
    /// <summary>A collaborator row names me personally.</summary>
    Collaborator = 1,
    /// <summary>A team I am on was granted it.</summary>
    Team = 2,
    /// <summary>Anybody signed in can see it.</summary>
    Public = 3,
    /// <summary>It is in my library and the server now grants me nothing.</summary>
    Revoked = 4
}

/// <summary>What the launcher knows about one instance's sync state.</summary>
/// <remarks><see cref="Unknown"/> renders as nothing, never as "in sync": a failed manifest call
/// must not look like a safe upload.</remarks>
public enum SharingSyncState
{
    Unknown = 0,
    NotPublished = 1,
    NotOnThisPc = 2,
    InSync = 3,
    ChangesToUpload = 4,
    UpdateAvailable = 5,
    Conflict = 6,
    AccessRevoked = 7
}

/// <summary>Which family a shared thing belongs to. Instances carry sync state and live in the
/// library; the hosted families carry versions on the server and nothing on disk.</summary>
public enum SharedFamily
{
    Instance = 0,
    Mod = 1,
    World = 2,
    ResourcePack = 3,
    /// <summary>A content bundle: a hosted shader pack, config set, KubeJS scripts or data pack.</summary>
    Bundle = 4
}

/// <summary>Which family an invitation belongs to; they redeem through different routes.</summary>
public enum InviteKind
{
    Pack = 0,
    Team = 1,
    Bundle = 2
}

/// <summary>Whether a section of the page got its answer, and what to say when it did not.</summary>
/// <param name="Ok">True when the call returned. False degrades only this section.</param>
/// <param name="Problem">One plain clause for the status line, never an exception's own words.</param>
/// <param name="Unsupported">The server answered 404 for the whole route: it is older than this
/// launcher, so the feature isn't deployed rather than broken.</param>
public readonly record struct SectionOutcome(bool Ok, string? Problem = null, bool Unsupported = false)
{
    public static readonly SectionOutcome Good = new(true);
    public static SectionOutcome Failed(string problem) => new(false, problem);
    public static SectionOutcome NotDeployed(string what) => new(false, what, true);
}

/// <summary>One person or team that can see a shared thing.</summary>
public sealed class AccessEntry
{
    public string Name { get; set; } = "";
    public bool IsTeam { get; set; }
    public PackPermissions Permissions { get; set; }
    public int MemberCount { get; set; }
    public List<string> Members { get; set; } = [];

    /// <summary>One or two letters for the face chip. Teams get their own glyph instead.</summary>
    public string Initials
    {
        get
        {
            var n = Name.Trim();
            if (n.Length == 0) return "?";
            var parts = n.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
            return n.Length >= 2 ? n[..2].ToUpperInvariant() : n.ToUpperInvariant();
        }
    }

    public string Tip => IsTeam
        ? $"{Name} - {MemberCount} member(s): {(Members.Count == 0 ? "roster not shown" : string.Join(", ", Members))}. "
          + SharingHubService.DescribePermissions(Permissions)
        : $"{Name} - {SharingHubService.DescribePermissions(Permissions)}";
}

/// <summary>An invitation waiting for an answer, in either direction.</summary>
public sealed class PendingInvite
{
    public InviteKind Kind { get; set; }
    public Guid Id { get; set; }
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = "";
    public string? Token { get; set; }
    public string InvitedByUsername { get; set; } = "";
    public string? InvitedUsername { get; set; }
    public string PermissionLabel { get; set; } = "";
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsLink { get; set; }

    /// <summary>True for one I sent, false for one waiting on me.</summary>
    public bool Outgoing { get; set; }

    /// <summary>Which kind of bundle a <see cref="InviteKind.Bundle"/> invitation is for.</summary>
    public BundleKind? BundleKind { get; set; }

    public string KindLabel => Kind switch
    {
        InviteKind.Pack => "Instance",
        InviteKind.Team => "Team",
        _ => SharingHubService.FamilyLabel(SharedFamily.Bundle, BundleKind)
    };

    public string Headline => Outgoing
        ? (IsLink ? $"Share link for {SubjectName}" : $"{InvitedUsername} - {SubjectName}")
        : $"{InvitedByUsername} invited you to {SubjectName}";

    public string SubLine
    {
        get
        {
            var bits = new List<string> { KindLabel, PermissionLabel };
            if (ExpiresAt is { } exp)
                bits.Add(exp <= DateTimeOffset.UtcNow ? "expired" : "expires " + TimeFormat.MonthDay(exp));
            if (PackListCache.Describe(CreatedAt) is { } age) bits.Add("sent " + age);
            if (Message is { Length: > 0 } m) bits.Add("'" + m + "'");
            return string.Join(" · ", bits);
        }
    }

    public bool IsExpired => ExpiresAt is { } e && e <= DateTimeOffset.UtcNow;
}

/// <summary>One row of "everything of mine that is shared, and everything shared with me".</summary>
/// <remarks>The sync properties arrive later (manifest read and folder walk) and raise
/// <see cref="PropertyChanged"/>; everything else is set once at build time.</remarks>
public sealed class SharingRow : INotifyPropertyChanged
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>The family's display name: "Instance", "Mod", "Shader pack" and so on.</summary>
    public string Kind { get; set; } = "Instance";

    public SharedFamily Family { get; set; } = SharedFamily.Instance;

    /// <summary>Which kind of bundle, on a <see cref="SharedFamily.Bundle"/> row.</summary>
    public BundleKind? BundleKind { get; set; }

    /// <summary>How many versions a hosted thing has. -1 on an instance, which has none.</summary>
    public int VersionCount { get; set; } = -1;

    /// <summary>A share link is live, so anybody holding it can redeem it. Reported for bundles, to
    /// whoever may manage them.</summary>
    public bool HasShareLink { get; set; }

    public bool IsMine { get; set; }
    public string OwnerUsername { get; set; } = "";
    public PackVisibility Visibility { get; set; }
    public bool IsPublished { get; set; }
    public PackPermissions Permissions { get; set; }
    public SharingGrant Grant { get; set; }

    /// <summary>Where the grant came from, in words, e.g. "Through Friends".</summary>
    public string GrantLabel { get; set; } = "";

    public List<AccessEntry> Access { get; set; } = [];

    /// <summary>False when the access list could not be read; the row then says so instead of
    /// showing "only you".</summary>
    public bool AccessKnown { get; set; }

    public int PendingInviteCount { get; set; }
    public DateTimeOffset? LastUploadedAt { get; set; }
    public string? LastUploadedByUsername { get; set; }
    public bool InLibrary { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Visibility in words. An instance adds that no files are uploaded yet; a hosted thing
    /// has versions instead, and says when it has none.</summary>
    public string VisibilityLabel => Family == SharedFamily.Instance
        ? SharingHubService.DescribeVisibility(Visibility, IsPublished)
        : SharingHubService.DescribeVisibility(Visibility, isShared: true)
          + (VersionCount == 0 ? ", nothing uploaded yet" : "");

    /// <summary>The sidebar's own glyph for the family, so a row reads as the page it belongs to.</summary>
    public string TypeGlyph => Family switch
    {
        SharedFamily.Mod => "\uEC4A",
        SharedFamily.World => "\uE909",
        SharedFamily.ResourcePack => "\uE7C9",
        SharedFamily.Bundle => BundleKind == CloudLauncher.Shared.BundleKind.ShaderPack ? "\uE706" : "\uE943",
        _ => "\uE7C3"
    };

    /// <summary>The second line of a row: where it came from and who can reach it. The kind is its
    /// own chip, so it is not repeated here.</summary>
    public string SubLine
    {
        get
        {
            var bits = new List<string>();
            if (IsMine) bits.Add("Shared by you with " + AccessSummary);
            else bits.Add(GrantLabel.Length > 0 ? GrantLabel : "Shared with you by " + OwnerUsername);
            bits.Add(VisibilityLabel);
            if (!IsMine) bits.Add(SharingHubService.DescribePermissions(Permissions, Family));
            if (PendingInviteCount > 0)
                bits.Add(SharingHubService.Plural(PendingInviteCount, "invitation") + " pending");
            if (HasShareLink) bits.Add("share link live");
            return string.Join(" · ", bits);
        }
    }

    /// <summary>"3 people and 1 team", "everyone on the server", or "people the server did not list"
    /// when the access list could not be read.</summary>
    public string AccessSummary
    {
        get
        {
            var people = Access.Count(a => !a.IsTeam);
            var teams = Access.Count(a => a.IsTeam);
            if (people == 0 && teams == 0)
            {
                if (Visibility == PackVisibility.Public) return "everyone on the server";
                if (!AccessKnown) return "people the server did not list";
                if (Visibility == PackVisibility.Team) return "your teams";
                if (HasShareLink || PendingInviteCount > 0) return "whoever you invite";
                return "nobody yet";
            }
            var bits = new List<string>();
            if (people > 0) bits.Add(SharingHubService.Plural(people, "person", "people"));
            if (teams > 0) bits.Add(SharingHubService.Plural(teams, "team"));
            return string.Join(" and ", bits);
        }
    }

    /// <summary>May I change who can reach this: it is mine, or I was given manage-sharing on it.</summary>
    public bool CanManageAccess => IsMine || Permissions.HasFlag(PackPermissions.ManageCollaborators);

    /// <summary>Only public gets a pill. Private is the default, and the second line already says who
    /// it is shared with.</summary>
    public bool IsPublic => Visibility == PackVisibility.Public;
    public bool IsTeamVisible => Visibility == PackVisibility.Team;

    /// <summary>At most four chips; the rest are counted in <see cref="MoreAccessLabel"/>.</summary>
    public IReadOnlyList<AccessEntry> Faces => Access.Take(4).ToList();

    public string MoreAccessLabel => Access.Count > 4 ? $"+{Access.Count - 4}" : "";

    private SharingSyncState _sync = SharingSyncState.Unknown;
    public SharingSyncState Sync
    {
        get => _sync;
        set { _sync = value; Raise(); Raise(nameof(SyncLabel)); Raise(nameof(HasSync)); Raise(nameof(ActionLabel)); Raise(nameof(HasAction)); }
    }

    private string _syncLabel = "";
    public string SyncLabel
    {
        get => _syncLabel;
        set { _syncLabel = value; Raise(); Raise(nameof(HasSync)); }
    }

    public bool HasSync => _syncLabel.Length > 0;

    private string _actionLabel = "";
    /// <summary>The row's one-click verb, or empty when there is nothing to offer.</summary>
    public string ActionLabel
    {
        get => _actionLabel;
        set { _actionLabel = value; Raise(); Raise(nameof(HasAction)); }
    }

    public bool HasAction => _actionLabel.Length > 0;

    /// <summary>
    /// Copies a fresh load's values into this row, so the object on screen stays the same.
    /// </summary>
    /// <remarks>The sync half is left alone until the new pass fills it in, so the pill doesn't flash
    /// blank on refresh.</remarks>
    public void Rebind(SharingRow fresh)
    {
        Name = fresh.Name;
        Kind = fresh.Kind;
        Family = fresh.Family;
        BundleKind = fresh.BundleKind;
        VersionCount = fresh.VersionCount;
        HasShareLink = fresh.HasShareLink;
        IsMine = fresh.IsMine;
        OwnerUsername = fresh.OwnerUsername;
        Visibility = fresh.Visibility;
        IsPublished = fresh.IsPublished;
        Permissions = fresh.Permissions;
        Grant = fresh.Grant;
        GrantLabel = fresh.GrantLabel;
        Access = fresh.Access;
        AccessKnown = fresh.AccessKnown;
        PendingInviteCount = fresh.PendingInviteCount;
        LastUploadedAt = fresh.LastUploadedAt;
        LastUploadedByUsername = fresh.LastUploadedByUsername;
        InLibrary = fresh.InLibrary;
        UpdatedAt = fresh.UpdatedAt;
        // Empty name refreshes every binding on the row.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>What one instance's folder and manifest say about its sync state.</summary>
public sealed class InstanceSyncFacts
{
    public Guid PackId { get; set; }
    public SharingSyncState State { get; set; } = SharingSyncState.Unknown;
    public string Label { get; set; } = "";

    /// <summary>Files the next upload would send.</summary>
    public int WillSync { get; set; } = -1;

    /// <summary>Files the rules would have shared that the private-path policy holds back.</summary>
    public int PrivateHeldBack { get; set; } = -1;

    /// <summary>Files that differ from the last manifest this PC synced. -1 = we did not look.</summary>
    public int LocallyChanged { get; set; } = -1;

    public long LocalVersion { get; set; } = -1;
    public long ServerVersion { get; set; } = -1;
    public bool OnDisk { get; set; }

    /// <summary>Why a fact is missing, for a tooltip. Never an exception's own words.</summary>
    public string? Problem { get; set; }

    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Everything the sharing hub draws, with an outcome per section.</summary>
public sealed class SharingSnapshot
{
    public List<SharingRow> SharedByMe { get; set; } = [];
    public List<SharingRow> SharedWithMe { get; set; } = [];
    public List<PendingInvite> Incoming { get; set; } = [];
    public List<PendingInvite> Outgoing { get; set; } = [];

    public SectionOutcome Library { get; set; } = SectionOutcome.Good;
    public SectionOutcome Access { get; set; } = SectionOutcome.Good;
    public SectionOutcome Invites { get; set; } = SectionOutcome.Good;
    public SectionOutcome Teams { get; set; } = SectionOutcome.Good;

    /// <summary>Outcome for the hosted mods, worlds, resource packs and bundles, which come from their
    /// own routes.</summary>
    public SectionOutcome Hosted { get; set; } = SectionOutcome.Good;

    /// <summary>True when nothing in this snapshot came from the server on this pass.</summary>
    public bool FromCache { get; set; }

    public DateTimeOffset? CachedAt { get; set; }

    /// <summary>The transport phrase when the server was not answering. Null while online.</summary>
    public string? OfflineReason { get; set; }

    // Not cached: the raw material for instance rows, rebuilt on every load.
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<PackSummary> Packs { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<Guid, PackDetail> Details { get; set; } = new Dictionary<Guid, PackDetail>();

    /// <summary>Every summary this pass saw, in the library or not, so a row for something shared with
    /// me can still subscribe to it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<Guid, PackSummary> Summaries { get; set; } = new Dictionary<Guid, PackSummary>();

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TeamSummary> TeamList { get; set; } = [];

    /// <summary>Everything hosted that I own, shared or not, for "Share something you host". Only the
    /// first page of each family, like the rows.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<SharingRow> HostedMine { get; set; } = [];

    /// <summary>Every problem worth putting on the status line, already worded.</summary>
    public IReadOnlyList<string> Problems =>
        new[] { Library, Access, Invites, Teams, Hosted }
            .Where(o => !o.Ok && o.Problem is { Length: > 0 })
            .Select(o => o.Problem!)
            .Distinct()
            .ToList();
}

/// <summary>
/// The client side of sharing: one fan-out that merges the library, the browse sources, the access
/// lists and the invitations into the lists the hub draws, plus every mutation those lists offer.
/// </summary>
/// <remarks>
/// <para>Each section carries its own <see cref="SectionOutcome"/>, so one failed call degrades that
/// section instead of blanking the page.</para>
/// <para>Offline is read-only: mutations are live writes with no queue, and <see cref="LoadAsync"/>
/// serves the last snapshot from disk.</para>
/// <para><see cref="SharingApi"/> covers routes <see cref="ApiClient"/> has no methods for yet.</para>
/// </remarks>
public sealed class SharingHubService
{
    private readonly ApiClient _api = App.State.Api;
    private readonly AppSettings _settings = App.State.Settings;

    /// <summary>How long a computed sync fact is reused before the folder is walked again.</summary>
    private static readonly TimeSpan SyncFactLife = TimeSpan.FromSeconds(90);

    /// <summary>Max instances whose details are fetched per load; the rest render from their
    /// summary.</summary>
    private const int DetailBudget = 60;

    /// <summary>Concurrent detail fetches: enough to avoid a long stall on a slow server without
    /// hammering it.</summary>
    private const int DetailParallelism = 4;

    private readonly ConcurrentDictionary<Guid, InstanceSyncFacts> _syncFacts = new();

    /// <summary>How long a snapshot is reused when another tab asks for one, so switching tabs right
    /// after opening the hub doesn't fan out twice.</summary>
    private static readonly TimeSpan SnapshotLife = TimeSpan.FromSeconds(30);

    private DateTimeOffset _lastLoadedAt = DateTimeOffset.MinValue;

    /// <summary>The last snapshot, for a tab that opens after another one loaded.</summary>
    public SharingSnapshot? Last { get; private set; }

    /// <summary>Raised on the thread that finished a load, after <see cref="Last"/> is set.</summary>
    public event Action<SharingSnapshot>? Loaded;

    public Guid MyUserId => _settings.UserId ?? Guid.Empty;

    // ── the fan-out ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a whole snapshot. Never throws for a failed section; check the outcomes.
    /// </summary>
    /// <param name="force">Ignore the cached snapshot even when offline.</param>
    public async Task<SharingSnapshot> LoadAsync(bool force, CancellationToken ct)
    {
        if (!force && Last is { } recent && DateTimeOffset.UtcNow - _lastLoadedAt < SnapshotLife)
            return recent;

        // Offline, the fan-out is a dozen calls that will all fail slowly, so use the cache. Retry
        // passes force.
        if (!force && App.State.IsOffline && FromCacheOnly() is { } cached)
        {
            Last = cached;
            _lastLoadedAt = DateTimeOffset.UtcNow;
            Loaded?.Invoke(cached);
            return cached;
        }

        var me = MyUserId;
        var snap = new SharingSnapshot();

        // ── library ──────────────────────────────────────────────────────────
        List<PackSummary> packs;
        try
        {
            packs = await _api.ListPacksAsync(ct);
            // A cached list isn't a failed section. FromCache already says so on the status line, and
            // repeating it would crowd out the parts that did fail.
            if (_api.PackListStale is { Length: > 0 }) snap.FromCache = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            packs = PackListCache.Load() ?? [];
            snap.FromCache = true;
            snap.CachedAt = PackListCache.CachedAt;
            snap.OfflineReason = OfflineReasonOf(ex);
            if (packs.Count == 0)
                snap.Library = SectionOutcome.Failed("your instances could not be read - " + Explain(ex));

            // Nothing else will answer either. Serve the cached snapshot rather than an empty page.
            if (packs.Count == 0 && !force && SharingHubCache.Load() is { } cachedOnly)
            {
                cachedOnly.OfflineReason = snap.OfflineReason;
                cachedOnly.Library = snap.Library;
                cachedOnly.FromCache = true;
                Last = cachedOnly;
                _lastLoadedAt = DateTimeOffset.UtcNow;
                Loaded?.Invoke(cachedOnly);
                return cachedOnly;
            }
        }
        snap.Packs = packs;

        // ── teams ────────────────────────────────────────────────────────────
        var teams = new List<TeamSummary>();
        try { teams = await _api.ListTeamsAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { snap.Teams = SectionOutcome.Failed("your teams could not be read - " + Explain(ex)); }
        snap.TeamList = teams;

        // Hosted families use their own routes and don't depend on the instance half, so run them in
        // parallel. Only faults on cancel.
        var hosted = LoadHostedAsync(me, teams, ct);

        // ── where each grant came from ───────────────────────────────────────
        // The server splits these: source=Shared is collaborator rows only and team packs are under
        // source=Team, so "shared with me" is merged here.
        var byCollaborator = new HashSet<Guid>();
        var byTeam = new Dictionary<Guid, List<string>>();
        var extra = new Dictionary<Guid, PackSummary>();

        try
        {
            var shared = await _api.BrowsePacksAsync(PackBrowseSource.Shared, limit: 100, ct: ct);
            foreach (var item in shared.Items)
            {
                byCollaborator.Add(item.Id);
                extra[item.Id] = item;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            snap.Library = snap.Library.Ok
                ? SectionOutcome.Failed("what is shared with you directly could not be read - " + Explain(ex))
                : snap.Library;
        }

        foreach (var team in teams.Take(12))
        {
            try
            {
                var page = await _api.BrowsePacksAsync(PackBrowseSource.Team, team.Id, limit: 100, ct: ct);
                foreach (var item in page.Items)
                {
                    if (!byTeam.TryGetValue(item.Id, out var names)) byTeam[item.Id] = names = [];
                    if (!names.Contains(team.Name)) names.Add(team.Name);
                    extra.TryAdd(item.Id, item);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                snap.Teams = SectionOutcome.Failed($"what team {team.Name} shares could not be read - " + Explain(ex));
            }
        }

        // ── access lists ─────────────────────────────────────────────────────
        var details = new ConcurrentDictionary<Guid, PackDetail>();
        var manageable = packs
            .Where(p => p.OwnerId == me || p.EffectivePermissions.HasFlag(PackPermissions.ManageCollaborators))
            .Take(DetailBudget)
            .ToList();

        var detailFailures = 0;
        using (var gate = new SemaphoreSlim(DetailParallelism))
        {
            var jobs = manageable.Select(async p =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    details[p.Id] = await _api.GetPackAsync(p.Id, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref detailFailures);
                    AppLog.Log("sharing", $"Who has access to {p.Name} could not be read: {Explain(ex)}");
                    if (PackDetailCache.Load(p.Id) is { } cachedDetail) details[p.Id] = cachedDetail;
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(jobs);
        }
        if (detailFailures > 0)
            snap.Access = SectionOutcome.Failed(
                $"who has access could not be read for {detailFailures} instance(s)");
        snap.Details = details;

        // ── invitations ──────────────────────────────────────────────────────
        await LoadInvitationsAsync(snap, details, ct);

        // ── rows ─────────────────────────────────────────────────────────────
        foreach (var p in packs.Where(p => p.OwnerId == me))
        {
            details.TryGetValue(p.Id, out var detail);
            var access = BuildAccess(detail);
            var live = detail?.PendingInvitations?.Where(IsLive).ToList() ?? [];
            var pending = live.Count(i => !i.IsLink);
            var link = live.Any(i => i.IsLink) || detail?.ShareToken is { Length: > 0 };
            // Shared means somebody other than me can reach it; published alone just syncs between my own
            // PCs. If the detail couldn't be read, fall back to the summary's IsShared.
            var isShared = p.Visibility != PackVisibility.Private || access.Count > 0 || pending > 0 || link
                           || (detail is null && p.IsShared);
            if (!isShared) continue;

            snap.SharedByMe.Add(new SharingRow
            {
                Id = p.Id,
                Name = p.Name,
                IsMine = true,
                OwnerUsername = p.OwnerUsername,
                Visibility = p.Visibility,
                IsPublished = p.IsShared,
                Permissions = p.EffectivePermissions,
                Grant = SharingGrant.Owner,
                GrantLabel = "Yours",
                Access = access,
                AccessKnown = detail is not null,
                PendingInviteCount = pending,
                HasShareLink = link,
                LastUploadedAt = p.LastUploadedAt ?? detail?.LastUploadedAt,
                LastUploadedByUsername = p.LastUploadedByUsername ?? detail?.LastUploadedByUsername,
                InLibrary = true,
                UpdatedAt = p.UpdatedAt
            });
        }

        var mineIds = packs.Where(p => p.OwnerId == me).Select(p => p.Id).ToHashSet();
        var withMe = new Dictionary<Guid, PackSummary>();
        foreach (var p in packs.Where(p => p.OwnerId != me)) withMe[p.Id] = p;
        foreach (var (id, p) in extra)
            if (!mineIds.Contains(id)) withMe.TryAdd(id, p);

        foreach (var p in withMe.Values)
        {
            var sources = new List<string>();
            var grant = SharingGrant.Public;
            if (byCollaborator.Contains(p.Id)) { sources.Add("shared with you by " + p.OwnerUsername); grant = SharingGrant.Collaborator; }
            if (byTeam.TryGetValue(p.Id, out var teamNames) && teamNames.Count > 0)
            {
                sources.Add("through " + string.Join(", ", teamNames));
                if (grant != SharingGrant.Collaborator) grant = SharingGrant.Team;
            }
            if (sources.Count == 0 && p.Visibility == PackVisibility.Public) sources.Add("public on this server");
            if (p.EffectivePermissions == PackPermissions.None)
            {
                grant = SharingGrant.Revoked;
                sources.Clear();
                sources.Add("access was withdrawn");
            }
            if (sources.Count == 0) sources.Add("shared with you by " + p.OwnerUsername);

            details.TryGetValue(p.Id, out var detail);
            snap.SharedWithMe.Add(new SharingRow
            {
                Id = p.Id,
                Name = p.Name,
                IsMine = false,
                OwnerUsername = p.OwnerUsername,
                Visibility = p.Visibility,
                IsPublished = p.IsShared,
                Permissions = p.EffectivePermissions,
                Grant = grant,
                GrantLabel = Capitalise(string.Join(" · ", sources)),
                Access = BuildAccess(detail),
                AccessKnown = detail is not null,
                InLibrary = packs.Any(x => x.Id == p.Id),
                LastUploadedAt = p.LastUploadedAt,
                LastUploadedByUsername = p.LastUploadedByUsername,
                UpdatedAt = p.UpdatedAt
            });
        }

        var hostedResult = await hosted;
        snap.SharedByMe.AddRange(hostedResult.Mine);
        snap.SharedWithMe.AddRange(hostedResult.Theirs);
        snap.Outgoing.AddRange(hostedResult.Outgoing);
        snap.Outgoing.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        snap.HostedMine = hostedResult.AllMine;
        snap.Hosted = hostedResult.Outcome;

        snap.SharedByMe.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        snap.SharedWithMe.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

        var summaries = new Dictionary<Guid, PackSummary>();
        foreach (var p in packs) summaries[p.Id] = p;
        foreach (var (id, p) in extra) summaries.TryAdd(id, p);
        snap.Summaries = summaries;

        if (!snap.FromCache) SharingHubCache.Save(snap);
        snap.CachedAt ??= SharingHubCache.CachedAt;

        Last = snap;
        _lastLoadedAt = DateTimeOffset.UtcNow;
        Loaded?.Invoke(snap);
        return snap;
    }

    /// <summary>
    /// The whole snapshot from disk, for when the launcher is offline.
    /// </summary>
    /// <remarks>Rows come from this page's cache; the instance and access lists come from the launcher's
    /// other caches, so instance rows have real <see cref="PackSummary"/> records. Null when nothing is
    /// cached.</remarks>
    private SharingSnapshot? FromCacheOnly()
    {
        if (SharingHubCache.Load() is not { } cached) return null;

        var packs = PackListCache.Load() ?? [];
        cached.Packs = packs;
        cached.Details = PackDetailCache.LoadAll();
        cached.Summaries = packs.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        cached.OfflineReason = App.State.OfflineReason;
        cached.FromCache = true;
        return cached;
    }

    private async Task LoadInvitationsAsync(
        SharingSnapshot snap, IDictionary<Guid, PackDetail> details, CancellationToken ct)
    {
        var problems = new List<string>();
        var unsupported = 0;

        try
        {
            var mine = await SharingApi.GetAsync<MyInvitations>("invitations", ct);
            foreach (var inv in mine.Packs.Where(IsLive))
                snap.Incoming.Add(FromPack(inv, outgoing: false));
            foreach (var inv in mine.Teams.Where(IsLive))
                snap.Incoming.Add(FromTeam(inv, outgoing: false));
            foreach (var inv in mine.Bundles.Where(IsLive))
                snap.Incoming.Add(FromBundle(inv));
        }
        catch (OperationCanceledException) { throw; }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound) { unsupported++; }
        catch (Exception ex) { problems.Add("invitations could not be read - " + Explain(ex)); }

        // /invitations only covers pack tokens; team invites come from the teams route. Ask both.
        try
        {
            var teamInvites = await SharingApi.GetAsync<List<TeamInvitationEntry>>("teams/invitations", ct);
            foreach (var inv in teamInvites.Where(IsLive))
                if (!snap.Incoming.Any(i => i.Kind == InviteKind.Team && i.Id == inv.Id))
                    snap.Incoming.Add(FromTeam(inv, outgoing: false));
        }
        catch (OperationCanceledException) { throw; }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound) { unsupported++; }
        catch (Exception ex) { problems.Add("team invitations could not be read - " + Explain(ex)); }

        // Outgoing ones come with the pack details already fetched.
        foreach (var detail in details.Values)
            foreach (var inv in (detail.PendingInvitations ?? []).Where(IsLive))
                snap.Outgoing.Add(FromPack(inv, outgoing: true));

        snap.Incoming.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        snap.Outgoing.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));

        if (unsupported >= 2)
            snap.Invites = SectionOutcome.NotDeployed(
                "this server does not have invitations yet - it is older than this launcher");
        else if (problems.Count > 0)
            // When both fail it's usually the same cause (server down), so report only the first.
            snap.Invites = SectionOutcome.Failed(problems[0]);
    }

    private static bool IsLive(PackInvitationEntry i) => i.AcceptedAt is null && i.RevokedAt is null;
    private static bool IsLive(TeamInvitationEntry i) => i.AcceptedAt is null && i.RevokedAt is null;
    private static bool IsLive(BundleInvitationEntry i) => i.AcceptedAt is null && i.RevokedAt is null;

    private static PendingInvite FromPack(PackInvitationEntry i, bool outgoing) => new()
    {
        Kind = InviteKind.Pack,
        Id = i.Id,
        SubjectId = i.PackId,
        SubjectName = i.PackName,
        Token = i.Token,
        InvitedByUsername = i.InvitedByUsername,
        InvitedUsername = i.InvitedUsername ?? (i.IsLink ? "anyone with the link" : "someone"),
        PermissionLabel = DescribePermissions(i.Permissions),
        Message = i.Message,
        CreatedAt = i.CreatedAt,
        ExpiresAt = i.ExpiresAt,
        IsLink = i.IsLink,
        Outgoing = outgoing
    };

    private static PendingInvite FromTeam(TeamInvitationEntry i, bool outgoing) => new()
    {
        Kind = InviteKind.Team,
        Id = i.Id,
        SubjectId = i.TeamId,
        SubjectName = i.TeamName,
        Token = i.Token,
        InvitedByUsername = i.InvitedByUsername,
        InvitedUsername = i.InvitedUsername ?? (i.IsLink ? "anyone with the link" : "someone"),
        PermissionLabel = i.Role switch
        {
            TeamRole.Owner => "as the owner",
            TeamRole.Admin => "as an admin",
            _ => "as a member"
        },
        Message = i.Message,
        CreatedAt = i.CreatedAt,
        ExpiresAt = i.ExpiresAt,
        IsLink = i.IsLink,
        Outgoing = outgoing
    };

    private static PendingInvite FromBundle(BundleInvitationEntry i, bool outgoing = false) => new()
    {
        Kind = InviteKind.Bundle,
        BundleKind = i.Kind,
        Id = i.Id,
        SubjectId = i.BundleId,
        SubjectName = i.BundleName,
        Token = i.Token,
        InvitedByUsername = i.InvitedByUsername,
        InvitedUsername = i.InvitedUsername ?? (i.IsLink ? "anyone with the link" : "someone"),
        PermissionLabel = DescribePermissions(i.Permissions, SharedFamily.Bundle),
        Message = i.Message,
        CreatedAt = i.CreatedAt,
        ExpiresAt = i.ExpiresAt,
        IsLink = i.IsLink,
        Outgoing = outgoing
    };

    private static List<AccessEntry> BuildAccess(PackDetail? detail) =>
        detail is null ? [] : BuildAccess(detail.Collaborators, detail.Teams);

    private static List<AccessEntry> BuildAccess(
        IEnumerable<PackCollaboratorEntry> collaborators, IEnumerable<PackTeamEntry> teams)
    {
        var list = new List<AccessEntry>();
        foreach (var c in collaborators)
            list.Add(new AccessEntry { Name = c.Username, Permissions = c.Permissions });
        foreach (var t in teams)
            list.Add(new AccessEntry
            {
                Name = t.TeamName,
                IsTeam = true,
                Permissions = t.Permissions,
                MemberCount = t.MemberCount,
                Members = t.MemberUsernames?.ToList() ?? []
            });
        return list;
    }

    // ── hosted families ──────────────────────────────────────────────────────

    /// <summary>Max hosted things of mine whose details are read per load. Summaries don't list
    /// collaborators, so only the detail shows whether a private item is shared.</summary>
    private const int HostedDetailBudget = 40;

    /// <summary>First page of each family and source. The browse routes page at 100 at most.</summary>
    private const int HostedPageSize = 100;

    private enum HostedSource { Personal, Shared, Team }

    /// <summary>One hosted thing, whichever family's summary it came from.</summary>
    private sealed record HostedItem(
        SharedFamily Family, Guid Id, string Name, Guid OwnerId, string OwnerUsername,
        PackVisibility Visibility, PackPermissions Permissions, DateTimeOffset UpdatedAt,
        int VersionCount, BundleKind? BundleKind);

    private sealed record HostedAccess(
        IReadOnlyList<PackCollaboratorEntry> Collaborators, IReadOnlyList<PackTeamEntry> Teams, bool ShareLink,
        IReadOnlyList<BundleInvitationEntry> Invitations);

    private sealed record HostedResult(
        List<SharingRow> Mine, List<SharingRow> Theirs, List<SharingRow> AllMine, List<PendingInvite> Outgoing,
        SectionOutcome Outcome);

    private static readonly SharedFamily[] HostedFamilies =
        [SharedFamily.Mod, SharedFamily.World, SharedFamily.ResourcePack, SharedFamily.Bundle];

    /// <summary>
    /// Mods, worlds, resource packs and bundles: mine that somebody else can reach, and other
    /// people's that I have been let into.
    /// </summary>
    /// <remarks>
    /// <para>Three sources per family (mine, shared with me, through a team), four calls at a time,
    /// then one detail per item of mine up to <see cref="HostedDetailBudget"/>. A failed family costs
    /// its rows and one status clause. Never throws: it runs beside the instance half, where a fault
    /// would go unobserved.</para>
    /// <para>A private item whose detail couldn't be read is left out rather than guessed at.</para>
    /// </remarks>
    private async Task<HostedResult> LoadHostedAsync(Guid me, IReadOnlyList<TeamSummary> teams, CancellationToken ct)
    {
        var bundles = new ContentBundleService(_api, _settings, App.State.Packs);
        var problems = new ConcurrentQueue<string>();
        using var gate = new SemaphoreSlim(DetailParallelism);

        async Task<List<HostedItem>> BrowseOne(SharedFamily family, HostedSource source)
        {
            await gate.WaitAsync(ct);
            try
            {
                return await BrowseHostedAsync(bundles, family, source, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One clause per family, not one per source.
                var what = FamilyLabel(family).ToLowerInvariant() + (family == SharedFamily.Bundle ? "" : "s");
                var why = family == SharedFamily.Bundle ? ContentBundleService.Explain(ex, "the server refused") : Explain(ex);
                if (!problems.Any(p => p.StartsWith("shared " + what, StringComparison.Ordinal)))
                    problems.Enqueue($"shared {(family == SharedFamily.Bundle ? "shader packs and configs" : what)} could not be read - {why}");
                AppLog.Log("sharing", $"Browsing {family} ({source}) failed: {why}");
                return [];
            }
            finally { gate.Release(); }
        }

        async Task<HostedAccess?> AccessOf(HostedItem item)
        {
            await gate.WaitAsync(ct);
            try
            {
                return await HostedAccessAsync(bundles, item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLog.Log("sharing", $"Who can reach {item.Name} could not be read: {Explain(ex)}");
                return null;
            }
            finally { gate.Release(); }
        }

        try
        {
            var jobs = HostedFamilies
                .SelectMany(f => new[] { HostedSource.Personal, HostedSource.Shared, HostedSource.Team }
                    .Select(s => (Family: f, Source: s, Task: BrowseOne(f, s))))
                .ToList();
            await Task.WhenAll(jobs.Select(j => j.Task));

            bool IsMine(HostedItem i) => me == Guid.Empty || i.OwnerId == me;

            var mineItems = jobs.Where(j => j.Source == HostedSource.Personal)
                                .SelectMany(j => j.Task.Result)
                                .Where(IsMine)
                                .GroupBy(i => i.Id).Select(g => g.First())
                                .ToList();

            var accessJobs = mineItems.OrderByDescending(i => i.UpdatedAt)
                                      .Take(HostedDetailBudget)
                                      .Select(i => (Item: i, Task: AccessOf(i)))
                                      .ToList();
            await Task.WhenAll(accessJobs.Select(a => a.Task));
            var access = accessJobs.Where(a => a.Task.Result is not null)
                                   .ToDictionary(a => a.Item.Id, a => a.Task.Result!);

            var allMine = new List<SharingRow>();
            var mine = new List<SharingRow>();
            var outgoing = new List<PendingInvite>();
            foreach (var item in mineItems)
            {
                access.TryGetValue(item.Id, out var a);
                var pending = (a?.Invitations ?? []).Where(IsLive).ToList();
                var row = HostedRow(item, isMine: true);
                row.Grant = SharingGrant.Owner;
                row.GrantLabel = "Yours";
                row.Access = a is null ? [] : BuildAccess(a.Collaborators, a.Teams);
                row.AccessKnown = a is not null;
                row.HasShareLink = a?.ShareLink == true;
                row.PendingInviteCount = pending.Count(i => !i.IsLink);
                allMine.Add(row);
                outgoing.AddRange(pending.Select(i => FromBundle(i, outgoing: true)));

                if (item.Visibility != PackVisibility.Private || row.Access.Count > 0
                    || row.HasShareLink || row.PendingInviteCount > 0)
                    mine.Add(row);
            }

            // An item can come back from both "shared with me" and "through my teams"; make it one row.
            var theirs = new Dictionary<Guid, (HostedItem Item, bool Direct, bool Team)>();
            foreach (var job in jobs.Where(j => j.Source != HostedSource.Personal))
                foreach (var item in job.Task.Result)
                {
                    if (me != Guid.Empty && item.OwnerId == me) continue;
                    theirs.TryGetValue(item.Id, out var seen);
                    theirs[item.Id] = (item, seen.Direct || job.Source == HostedSource.Shared,
                                             seen.Team || job.Source == HostedSource.Team);
                }

            // Which team is not in the summary; with one team there is only one it can be.
            var teamPhrase = teams.Count == 1 ? "through " + teams[0].Name : "through one of your teams";
            var theirRows = new List<SharingRow>();
            foreach (var (item, direct, team) in theirs.Values)
            {
                var row = HostedRow(item, isMine: false);
                var sources = new List<string>();
                if (direct) sources.Add("shared with you by " + item.OwnerUsername);
                if (team) sources.Add(teamPhrase);
                row.Grant = direct ? SharingGrant.Collaborator : SharingGrant.Team;
                row.GrantLabel = Capitalise(string.Join(" · ", sources));
                theirRows.Add(row);
            }

            var outcome = problems.IsEmpty
                ? SectionOutcome.Good
                : SectionOutcome.Failed(problems.First());
            AppLog.Log("sharing", $"Hosted: {mine.Count} of {mineItems.Count} of mine shared, {theirRows.Count} shared with me"
                                + (problems.IsEmpty ? "." : $", {problems.Count} family failure(s)."));
            return new HostedResult(mine, theirRows, allMine, outgoing, outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.hosted", ex);
            return new HostedResult([], [], [], [], SectionOutcome.Failed("hosted mods and packs could not be read - " + Explain(ex)));
        }
    }

    private async Task<List<HostedItem>> BrowseHostedAsync(
        ContentBundleService bundles, SharedFamily family, HostedSource source, CancellationToken ct)
    {
        switch (family)
        {
            case SharedFamily.Mod:
            {
                var page = await _api.BrowseModsAsync(source switch
                {
                    HostedSource.Personal => ModBrowseSource.Personal,
                    HostedSource.Shared => ModBrowseSource.Shared,
                    _ => ModBrowseSource.Team
                }, limit: HostedPageSize, ct: ct);
                return page.Items.Select(m => new HostedItem(family, m.Id, m.Name, m.OwnerId, m.OwnerUsername,
                    m.Visibility, m.EffectivePermissions, m.UpdatedAt, m.VersionCount, null)).ToList();
            }
            case SharedFamily.World:
            {
                var page = await _api.BrowseWorldsAsync(source switch
                {
                    HostedSource.Personal => WorldBrowseSource.Personal,
                    HostedSource.Shared => WorldBrowseSource.Shared,
                    _ => WorldBrowseSource.Team
                }, limit: HostedPageSize, ct: ct);
                return page.Items.Select(w => new HostedItem(family, w.Id, w.Name, w.OwnerId, w.OwnerUsername,
                    w.Visibility, w.EffectivePermissions, w.UpdatedAt, w.VersionCount, null)).ToList();
            }
            case SharedFamily.ResourcePack:
            {
                var page = await _api.BrowseResourcePacksAsync(source switch
                {
                    HostedSource.Personal => ResourcePackBrowseSource.Personal,
                    HostedSource.Shared => ResourcePackBrowseSource.Shared,
                    _ => ResourcePackBrowseSource.Team
                }, limit: HostedPageSize, ct: ct);
                return page.Items.Select(p => new HostedItem(family, p.Id, p.Name, p.OwnerId, p.OwnerUsername,
                    p.Visibility, p.EffectivePermissions, p.UpdatedAt, p.VersionCount, null)).ToList();
            }
            default:
            {
                var page = await bundles.BrowseAsync(null, source switch
                {
                    HostedSource.Personal => BundleBrowseSource.Personal,
                    HostedSource.Shared => BundleBrowseSource.Shared,
                    _ => BundleBrowseSource.Team
                }, limit: HostedPageSize, ct: ct);
                return page.Items.Select(b => new HostedItem(SharedFamily.Bundle, b.Id, b.Name, b.OwnerId, b.OwnerUsername,
                    b.Visibility, b.EffectivePermissions, b.UpdatedAt, b.VersionCount, b.Kind)).ToList();
            }
        }
    }

    private async Task<HostedAccess> HostedAccessAsync(ContentBundleService bundles, HostedItem item, CancellationToken ct)
    {
        switch (item.Family)
        {
            case SharedFamily.Mod:
            {
                var d = await _api.GetModAsync(item.Id, ct);
                return new HostedAccess(d.Collaborators, d.Teams, false, []);
            }
            case SharedFamily.World:
            {
                var d = await _api.GetSharedWorldAsync(item.Id, ct);
                return new HostedAccess(d.Collaborators, d.Teams, false, []);
            }
            case SharedFamily.ResourcePack:
            {
                var d = await _api.GetResourcePackAsync(item.Id, ct);
                return new HostedAccess(d.Collaborators, d.Teams, false, []);
            }
            default:
            {
                var d = await bundles.GetAsync(item.Id, ct);
                // Bundles are the one hosted family with invitations. The list is manage-only; a
                // refusal means "none I can see", not a failed row.
                IReadOnlyList<BundleInvitationEntry> invitations = [];
                try { invitations = await bundles.ListInvitationsAsync(item.Id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { AppLog.Log("sharing", $"Invitations for {item.Name} could not be read: {Explain(ex)}"); }
                return new HostedAccess(d.Collaborators, d.Teams, d.ShareToken is { Length: > 0 }, invitations);
            }
        }
    }

    private static SharingRow HostedRow(HostedItem item, bool isMine) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Kind = FamilyLabel(item.Family, item.BundleKind),
        Family = item.Family,
        BundleKind = item.BundleKind,
        VersionCount = item.VersionCount,
        IsMine = isMine,
        OwnerUsername = item.OwnerUsername,
        Visibility = item.Visibility,
        IsPublished = item.VersionCount > 0,
        Permissions = item.Permissions,
        // A hosted thing has no library to be in; true keeps the instance-only "Add" verb away.
        InLibrary = true,
        UpdatedAt = item.UpdatedAt
    };

    // ── sync facts ───────────────────────────────────────────────────────────

    /// <summary>
    /// What this PC and the server each hold for one instance. Cheap enough to call per row, and
    /// cached for <see cref="SyncFactLife"/>.
    /// </summary>
    /// <remarks>If the manifest call fails, the answer falls back to what the disk knows ("could not
    /// check for updates") instead of claiming "in sync".</remarks>
    public async Task<InstanceSyncFacts> SyncFactsAsync(PackSummary pack, CancellationToken ct)
    {
        if (_syncFacts.TryGetValue(pack.Id, out var cached)
            && DateTimeOffset.UtcNow - cached.ComputedAt < SyncFactLife)
            return cached;

        var facts = new InstanceSyncFacts { PackId = pack.Id };

        string? root = null;
        try { root = App.State.Packs.PackRoot(pack.Id); } catch { /* never downloaded */ }
        facts.OnDisk = root is not null && Directory.Exists(Path.Combine(root, "game"));

        PackManifest? manifest = null;
        if (pack.IsShared && pack.EffectivePermissions.HasFlag(PackPermissions.View) && !App.State.IsOffline)
        {
            try { manifest = await _api.GetManifestAsync(pack.Id, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { facts.Problem = "the server's file list could not be read - " + Explain(ex); }
        }

        await Task.Run(() => FillLocalFacts(facts, pack, root, manifest), ct);

        Describe(facts, pack);
        _syncFacts[pack.Id] = facts;
        return facts;
    }

    /// <summary>Forgets one instance's facts. Call after an upload, download or rule change.</summary>
    public void InvalidateSync(Guid packId) => _syncFacts.TryRemove(packId, out _);

    public void InvalidateAllSync() => _syncFacts.Clear();

    private void FillLocalFacts(InstanceSyncFacts facts, PackSummary pack, string? root, PackManifest? manifest)
    {
        facts.LocalVersion = _settings.PackSyncedVersion.TryGetValue(pack.Id, out var v) ? v : 0;
        facts.ServerVersion = manifest?.Version ?? -1;

        if (root is null) return;
        var gameDir = Path.Combine(root, "game");
        if (!Directory.Exists(gameDir)) return;

        try
        {
            var rules = App.State.Rules.Load(root);
            var willSync = new List<string>();
            var held = 0;

            foreach (var rel in App.State.Packs.ListRelativeFiles(gameDir))
            {
                var isShared = rel.StartsWith(".cloudlauncher/", StringComparison.OrdinalIgnoreCase)
                               || App.State.Rules.Match(rel, rules).IsAutoShared;
                if (!isShared) continue;
                if (PrivateAssetPolicy.IsPrivate(rel, _settings)) { held++; continue; }
                willSync.Add(rel);
            }

            facts.WillSync = willSync.Count;
            facts.PrivateHeldBack = held;

            // What the last sync left behind, so "changed since" is measured against the server's copy.
            var lockPaths = ReadSyncLock(root);
            var changed = 0;

            if (manifest is not null)
            {
                var server = manifest.Entries
                    .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
                foreach (var rel in willSync)
                {
                    if (!server.ContainsKey(rel)) { changed++; continue; }
                    var abs = Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (File.GetLastWriteTimeUtc(abs) > manifest.UpdatedAt.UtcDateTime.AddSeconds(2))
                            changed++;
                    }
                    catch { /* a file that vanished mid-walk doesn't count as a change */ }
                }
                changed += server.Keys.Count(k => !willSync.Contains(k, StringComparer.OrdinalIgnoreCase));
            }
            else if (lockPaths.Count > 0)
            {
                changed += willSync.Count(rel => !lockPaths.Contains(rel));
                changed += lockPaths.Count(rel => !willSync.Contains(rel, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                changed = -1; // nothing to compare against, so don't guess
            }

            facts.LocallyChanged = changed;
        }
        catch (Exception ex)
        {
            facts.Problem = "this instance's folder could not be read";
            AppLog.LogError("sharing.syncfacts", ex);
        }
    }

    /// <summary>The <c>.sync-manifest.json</c> the folder service writes after every sync: the only
    /// record of which files came from the server. Read directly since the service keeps it
    /// private.</summary>
    private static HashSet<string> ReadSyncLock(string packRoot)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(packRoot, ".sync-manifest.json");
            if (!File.Exists(path)) return set;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            if (list is not null) foreach (var p in list) set.Add(p);
        }
        catch { /* corrupt or missing: an empty set means "don't know" */ }
        return set;
    }

    private void Describe(InstanceSyncFacts facts, PackSummary pack)
    {
        if (pack.EffectivePermissions == PackPermissions.None)
        {
            facts.State = SharingSyncState.AccessRevoked;
            facts.Label = "Access revoked";
            return;
        }
        if (!pack.IsShared)
        {
            facts.State = SharingSyncState.NotPublished;
            facts.Label = pack.OwnerId == MyUserId ? "Not published" : "No files published";
            return;
        }
        if (!facts.OnDisk)
        {
            facts.State = SharingSyncState.NotOnThisPc;
            facts.Label = "Not on this PC";
            return;
        }

        var behind = facts.ServerVersion >= 0 && facts.ServerVersion > facts.LocalVersion;
        var ahead = facts.LocallyChanged > 0;

        if (behind && ahead) { facts.State = SharingSyncState.Conflict; facts.Label = "Conflict"; return; }
        if (behind) { facts.State = SharingSyncState.UpdateAvailable; facts.Label = "Update available"; return; }
        if (ahead) { facts.State = SharingSyncState.ChangesToUpload; facts.Label = $"{facts.LocallyChanged} change(s) to upload"; return; }
        if (facts.ServerVersion < 0 && facts.LocallyChanged < 0)
        {
            facts.State = SharingSyncState.Unknown;
            facts.Label = "";
            return;
        }
        facts.State = SharingSyncState.InSync;
        facts.Label = "In sync";
    }

    // ── mutations (all of them online-only) ──────────────────────────────────

    public async Task SetVisibilityAsync(Guid packId, PackVisibility visibility, CancellationToken ct)
    {
        await _api.UpdatePackAsync(packId, new UpdatePackRequest(
            null, null, visibility, null, null, null, null, null), ct);
        AppLog.Log("sharing", $"Visibility for {packId} set to {visibility}.");
    }

    public async Task SetPublishedAsync(Guid packId, bool published, CancellationToken ct)
    {
        await _api.UpdatePackAsync(packId, new UpdatePackRequest(
            null, null, null, published, null, null, null, null), ct);
        if (published) App.State.PackAssets.MirrorToSharedFolder(packId);
        InvalidateSync(packId);
        AppLog.Log("sharing", $"Cloud sync for {packId} turned {(published ? "on" : "off")}.");
    }

    public Task<PackInvitationEntry> InviteAsync(
        Guid packId, string username, PackPermissions permissions, int? expiresInDays,
        string? message, CancellationToken ct) =>
        SharingApi.PostAsync<PackInvitationEntry>(
            $"packs/{packId}/invitations",
            new CreatePackInvitationRequest(username, permissions, message, expiresInDays), ct);

    public Task<PackInvitationEntry> MintInviteLinkAsync(
        Guid packId, PackPermissions permissions, int? expiresInDays, CancellationToken ct) =>
        SharingApi.PostAsync<PackInvitationEntry>(
            $"packs/{packId}/invitations",
            new CreatePackInvitationRequest(null, permissions, null, expiresInDays), ct);

    public Task<ShareLinkInfo> CreateShareLinkAsync(
        Guid packId, PackPermissions permissions, int? expiresInDays, CancellationToken ct) =>
        SharingApi.PostAsync<ShareLinkInfo>(
            $"packs/{packId}/share-link", new CreateShareLinkRequest(permissions, expiresInDays), ct);

    public Task RevokeShareLinkAsync(Guid packId, CancellationToken ct) =>
        SharingApi.DeleteAsync($"packs/{packId}/share-link", ct);

    public Task RevokeInvitationAsync(PendingInvite invite, CancellationToken ct) => invite.Kind switch
    {
        InviteKind.Team => SharingApi.DeleteAsync($"teams/{invite.SubjectId}/invitations/{invite.Id}", ct),
        InviteKind.Bundle => SharingApi.DeleteAsync($"bundles/{invite.SubjectId}/invitations/{invite.Id}", ct),
        _ => SharingApi.DeleteAsync($"packs/{invite.SubjectId}/invitations/{invite.Id}", ct)
    };

    public async Task AcceptInvitationAsync(PendingInvite invite, CancellationToken ct)
    {
        if (invite.Token is not { Length: > 0 } token)
            throw new InvalidOperationException("That invitation did not come with a token.");

        if (invite.Kind == InviteKind.Team)
            await SharingApi.PostAsync($"teams/invitations/{Uri.EscapeDataString(token)}/accept", null, ct);
        else
            await SharingApi.PostAsync($"invitations/{Uri.EscapeDataString(token)}/accept", null, ct);

        AppLog.Log("sharing", $"Accepted the invitation to {invite.SubjectName}.");
    }

    public async Task DeclineInvitationAsync(PendingInvite invite, CancellationToken ct)
    {
        if (invite.Token is not { Length: > 0 } token)
            throw new InvalidOperationException("That invitation did not come with a token.");

        if (invite.Kind == InviteKind.Team)
            await SharingApi.PostAsync($"teams/invitations/{Uri.EscapeDataString(token)}/decline", null, ct);
        else
            await SharingApi.PostAsync($"invitations/{Uri.EscapeDataString(token)}/decline", null, ct);

        AppLog.Log("sharing", $"Declined the invitation to {invite.SubjectName}.");
    }

    // ── redeeming a pasted link ──────────────────────────────────────────────

    /// <summary>What a redeemed link turned out to be: an instance, a bundle, or a team.</summary>
    public sealed record RedeemedInvite(SharedFamily Family, Guid Id, string Name, BundleKind? BundleKind, bool IsTeam);

    /// <summary>
    /// The token inside whatever was pasted: a whole share link (with or without a trailing slash,
    /// query or fragment), a team invite code, or the bare token. Null when nothing token-shaped is
    /// there.
    /// </summary>
    /// <remarks>Tokens are URL-safe and at most 32 characters (the server refuses anything longer
    /// before touching the database), so anything with a space or a slash left in it is not one.</remarks>
    public static string? ParseInviteToken(string? pasted)
    {
        var s = (pasted ?? "").Trim().Trim('<', '>', '"', '\'');
        if (s.Length == 0) return null;

        var marker = s.IndexOf("/invitations/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) s = s[(marker + "/invitations/".Length)..];
        var cut = s.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) s = s[..cut];
        s = Uri.UnescapeDataString(s).Trim();

        return s.Length is > 0 and <= 32 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=')
            ? s
            : null;
    }

    /// <summary>Who is asking and what a token would grant, before anybody decides. 404 when the
    /// token is not an instance's or a bundle's (a team code, or a dead link).</summary>
    public Task<InvitationPreview> PreviewInvitationAsync(string token, CancellationToken ct) =>
        SharingApi.GetAsync<InvitationPreview>($"invitations/{Uri.EscapeDataString(token)}", ct);

    /// <summary>
    /// Accepts an instance or bundle token and says which of the two it was.
    /// </summary>
    /// <remarks>One route for both, since the redeemer only has a token. It returns a
    /// <see cref="PackSummary"/> or a <see cref="ContentBundleSummary"/>; only the bundle has a target
    /// folder.</remarks>
    public async Task<RedeemedInvite> RedeemAsync(string token, CancellationToken ct)
    {
        var body = await SharingApi.PostAsync<JsonElement>($"invitations/{Uri.EscapeDataString(token)}/accept", null, ct);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("targetPathRoot", out _))
        {
            var bundle = body.Deserialize<ContentBundleSummary>(web)!;
            AppLog.Log("sharing", $"Redeemed a link to the bundle {bundle.Name}.");
            return new RedeemedInvite(SharedFamily.Bundle, bundle.Id, bundle.Name, bundle.Kind, false);
        }
        var pack = body.Deserialize<PackSummary>(web)!;
        AppLog.Log("sharing", $"Redeemed a link to the instance {pack.Name}.");
        return new RedeemedInvite(SharedFamily.Instance, pack.Id, pack.Name, null, false);
    }

    /// <summary>Joins the team a code belongs to. Idempotent on the server.</summary>
    public async Task<RedeemedInvite> RedeemTeamCodeAsync(string token, CancellationToken ct)
    {
        var joined = await SharingApi.PostAsync<TeamInvitationEntry>(
            $"teams/invitations/{Uri.EscapeDataString(token)}/accept", null, ct);
        AppLog.Log("sharing", $"Joined the team {joined.TeamName} with a code.");
        return new RedeemedInvite(SharedFamily.Instance, joined.TeamId, joined.TeamName, null, true);
    }

    /// <summary>Prefix search over usernames for the invite box. The server wants two characters
    /// minimum.</summary>
    public async Task<IReadOnlyList<UserSummary>> SearchUsersAsync(string query, CancellationToken ct)
    {
        var q = (query ?? "").Trim();
        if (q.Length < 2) return [];
        var page = await SharingApi.GetAsync<UserSearchPage>($"users?q={Uri.EscapeDataString(q)}", ct);
        return page.Items;
    }

    /// <summary>Removes a pack from this account's library. The only fix for a listed pack whose access
    /// was withdrawn, since the server never cleans those up.</summary>
    public async Task RemoveFromLibraryAsync(Guid packId, CancellationToken ct)
    {
        try { await _api.UnsubscribePackAsync(packId, ct); }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // Already gone server-side; hiding it locally is still the right outcome.
        }
        _settings.HidePack(packId);   // saves itself
        AppLog.Log("sharing", $"Removed {packId} from this library.");
    }

    // ── wording ──────────────────────────────────────────────────────────────

    /// <summary>The share link as the user would paste it.</summary>
    public string ShareLinkUrl(string token) => LinkFor(token);

    /// <summary>
    /// The link for an invitation or share token, which is what every Copy puts on the clipboard.
    /// </summary>
    /// <remarks>A link rather than the bare token: in a browser it opens a page explaining what it is,
    /// and Redeem a link on the Sharing page accepts it whole.</remarks>
    public static string LinkFor(string token) =>
        $"{App.State.Settings.ServerUrl.TrimEnd('/')}/invitations/{token}";

    /// <summary>
    /// "1 person", "3 people": a count with the right noun, for use inside sentences.
    /// </summary>
    /// <remarks>Count slots beside a list use the "world(s)" form instead.</remarks>
    public static string Plural(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count:N0} {many ?? one + "s"}";

    public static string DescribePermissions(PackPermissions p)
    {
        if (p == PackPermissions.None) return "no access";
        if (p.HasFlag(PackPermissions.ManageCollaborators)) return "full access, can manage sharing";
        if (p.HasFlag(PackPermissions.UploadShared)) return "can view, download and upload";
        if (p.HasFlag(PackPermissions.Download)) return "can view and download";
        return "can view only";
    }

    /// <summary>The same, in the family's terms: on a hosted mod or world, Upload means adding
    /// versions rather than uploading files.</summary>
    public static string DescribePermissions(PackPermissions p, SharedFamily family)
    {
        if (family == SharedFamily.Instance || p == PackPermissions.None) return DescribePermissions(p);
        if (p.HasFlag(PackPermissions.ManageCollaborators)) return "full access, can manage sharing";
        if (p.HasFlag(PackPermissions.UploadShared)) return "can download and add versions";
        if (p.HasFlag(PackPermissions.Download)) return "can download";
        return "can view only";
    }

    /// <summary>What a family is called on a row's chip. Bundles show their kind, since "bundle" is
    /// only the server's term.</summary>
    public static string FamilyLabel(SharedFamily family, BundleKind? bundleKind = null) => family switch
    {
        SharedFamily.Mod => "Mod",
        SharedFamily.World => "World",
        SharedFamily.ResourcePack => "Resource pack",
        SharedFamily.Bundle => bundleKind switch
        {
            BundleKind.ShaderPack => "Shader pack",
            BundleKind.ConfigBundle => "Configs",
            BundleKind.KubeJsBundle => "KubeJS scripts",
            BundleKind.DataPack => "Data pack",
            _ => "Files"
        },
        _ => "Instance"
    };

    /// <summary>
    /// Visibility in words. <see cref="PackVisibility.Team"/> only reaches the teams the pack has
    /// actually been shared with.
    /// </summary>
    public static string DescribeVisibility(PackVisibility v, bool isShared)
    {
        var who = v switch
        {
            PackVisibility.Public => "Public",
            PackVisibility.Team => "Visible to your teams",
            _ => "Private"
        };
        return isShared ? who : who + ", no files uploaded";
    }

    /// <summary>
    /// One plain sentence for a failure, preferring the server's own problem text over the status code.
    /// </summary>
    public static string Explain(Exception ex)
    {
        switch (ex)
        {
            case OfflineException off:
                return off.Reason is { Length: > 0 } r
                    ? "the server is not answering (" + r + ")"
                    : "the server is not answering";
            case SessionExpiredException:
                return "your session expired - sign in again";
            case ApiException api:
            {
                if (ExtractProblem(api.Message) is { Length: > 0 } sentence) return sentence;
                return api.Status switch
                {
                    HttpStatusCode.Forbidden => "you are not allowed to do that",
                    HttpStatusCode.NotFound => "that is no longer there",
                    HttpStatusCode.Conflict => "somebody else changed it first",
                    HttpStatusCode.TooManyRequests => "that has been asked too often - wait a minute",
                    _ => $"the server refused it ({(int)api.Status})"
                };
            }
            case OperationCanceledException:
                return "it was cancelled";
            default:
                return Connectivity.DescribeTransportFailure(ex, CancellationToken.None)
                       ?? "something went wrong";
        }
    }

    /// <summary>
    /// Digs the server's <c>{"error":"..."}</c> sentence out of an <see cref="ApiException"/> whose
    /// message is <c>"403 Forbidden: {body}"</c>.
    /// </summary>
    private static string? ExtractProblem(string message)
    {
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return null;
        var body = message[(colon + 2)..].Trim();
        if (body.Length == 0) return null;

        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var name in new[] { "error", "detail", "title", "message" })
                    if (doc.RootElement.TryGetProperty(name, out var el)
                        && el.ValueKind == JsonValueKind.String
                        && el.GetString() is { Length: > 0 } s)
                        return s;
            }
            catch { /* not JSON after all */ }
            return null;
        }

        // A bare sentence, or HTML from something that isn't our API; only pass on the sentence.
        return body.StartsWith('<') ? null : body;
    }

    private static string OfflineReasonOf(Exception ex) =>
        ex is OfflineException off && off.Reason is { Length: > 0 } r
            ? r
            : Connectivity.DescribeTransportFailure(ex, CancellationToken.None) ?? "the server is unreachable";

    private static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// Sharing routes that <see cref="ApiClient"/> has no methods for yet: invitations, share links and
/// user search.
/// </summary>
/// <remarks>A thin wrapper: one send with the bearer token from settings, and the server's problem
/// text as the <see cref="ApiException"/> message. On a 401 it makes one real <see cref="ApiClient"/>
/// call (which owns token refresh) and retries once. Replace with <c>ApiClient</c> methods when
/// they exist.</remarks>
internal static class SharingApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();
    private static HttpClient? _http;
    private static string _base = "";

    private static HttpClient Client()
    {
        var url = App.State.Settings.ServerUrl.TrimEnd('/') + "/";
        lock (Gate)
        {
            if (_http is not null && _base == url) return _http;
            _http?.Dispose();
            _base = url;
            _http = ApiClient.WithUserAgent(new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) });
            return _http;
        }
    }

    public static Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Get, path, null, ct);

    public static Task<T> PostAsync<T>(string path, object? body, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Post, path, body, ct);

    public static async Task PostAsync(string path, object? body, CancellationToken ct) =>
        await SendAsync<object?>(HttpMethod.Post, path, body, ct, wantBody: false);

    public static async Task DeleteAsync(string path, CancellationToken ct) =>
        await SendAsync<object?>(HttpMethod.Delete, path, null, ct, wantBody: false);

    private static async Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken ct, bool wantBody = true)
    {
        var resp = await SendOnceAsync(method, path, body, ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            // One refresh through the client that owns the refresh lock, then one retry. A second 401
            // means the session really is gone.
            try { await App.State.Api.MeAsync(ct); }
            catch (Exception ex) { AppLog.Log("sharing", "Re-authenticating before a sharing call failed: " + SharingHubService.Explain(ex)); }
            resp = await SendOnceAsync(method, path, body, ct);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {text}", resp.StatusCode);
            }
            if (!wantBody) return default!;
            var result = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
            return result ?? throw new ApiException("Empty response body", resp.StatusCode);
        }
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (App.State.Settings.AccessToken is { Length: > 0 } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body, body.GetType(), options: Json);

        try
        {
            return await Client().SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new OfflineException(Connectivity.DescribeTransportFailure(ex, ct) ?? "the server is unreachable");
        }
    }
}

/// <summary>
/// The last sharing snapshot, on disk, so the hub renders read-only when the server is not
/// answering instead of showing an empty page.
/// </summary>
/// <remarks>Works like <see cref="PackListCache"/> (per-profile folder, write to temp then move,
/// write failures ignored), plus a <see cref="SchemaVersion"/> because these rows are computed
/// rather than a server DTO.</remarks>
public static class SharingHubCache
{
    /// <summary>Bump whenever a cached type's shape changes; older files are then ignored.</summary>
    /// <remarks>2: rows carry their family (hosted mods, worlds, resource packs and bundles).</remarks>
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly object Gate = new();

    private static string Path => System.IO.Path.Combine(AppSettings.DataRootPath, "sharing-hub-cache.json");

    public static DateTimeOffset? CachedAt
    {
        get
        {
            try { return File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null; }
            catch { return null; }
        }
    }

    /// <summary>How old the cached copy is, in the same words every other cache uses.</summary>
    public static string? AgeInWords() => PackListCache.Describe(CachedAt);

    private sealed class Doc
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset CachedAt { get; set; }
        public List<SharingRow> SharedByMe { get; set; } = [];
        public List<SharingRow> SharedWithMe { get; set; } = [];
        public List<PendingInvite> Incoming { get; set; } = [];
        public List<PendingInvite> Outgoing { get; set; } = [];
    }

    public static void Save(SharingSnapshot snap)
    {
        try
        {
            var doc = new Doc
            {
                SchemaVersion = SchemaVersion,
                CachedAt = DateTimeOffset.UtcNow,
                SharedByMe = snap.SharedByMe,
                SharedWithMe = snap.SharedWithMe,
                Incoming = snap.Incoming,
                Outgoing = snap.Outgoing
            };
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
                File.Move(tmp, Path, overwrite: true);
            }
        }
        catch { /* a cache write failure shouldn't fail the page */ }
    }

    /// <summary>The last snapshot, or null when there is none, it is unreadable, or it was written
    /// by a build whose row shape was different.</summary>
    public static SharingSnapshot? Load()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(Path)) return null;
                var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(Path), Json);
                if (doc is null || doc.SchemaVersion != SchemaVersion) return null;
                return new SharingSnapshot
                {
                    SharedByMe = doc.SharedByMe,
                    SharedWithMe = doc.SharedWithMe,
                    Incoming = doc.Incoming,
                    Outgoing = doc.Outgoing,
                    FromCache = true,
                    CachedAt = doc.CachedAt
                };
            }
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { lock (Gate) File.Delete(Path); } catch { /* nothing to do */ }
    }
}
