using System.IO;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>The last team list, and each team's roster, that the server answered with. Kept on disk
/// so an unreachable server doesn't read as "you are not on any teams".</summary>
/// <remarks>
/// <para>Built like <see cref="PackListCache"/>: same per-profile data root, atomic tmp+move, and write
/// failures are ignored. It also stores a <see cref="SchemaVersion"/>, because a file written before
/// 1.6.0 has no <c>MyRole</c> and would make every team look like plain membership, and the
/// per-team <see cref="TeamDetail"/>, so rosters can be read offline.</para>
/// <para>Best-effort: a missing, unreadable or older-schema file reads as no cache.</para>
/// </remarks>
public static class TeamListCache
{
    /// <summary>Bump when the meaning of anything stored here changes. Older files are discarded, not
    /// migrated; the server sends everything again on the next successful call.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly object Gate = new();

    private static string Path => System.IO.Path.Combine(AppSettings.DataRootPath, "teams-cache.json");

    /// <summary>What is on disk. Details are keyed by <c>Guid.ToString("N")</c> strings so the file
    /// stays readable by eye and by any serializer.</summary>
    private sealed record Snapshot(
        int SchemaVersion,
        DateTimeOffset CachedAt,
        List<TeamSummary> Teams,
        Dictionary<string, TeamDetail> Details);

    /// <summary>When the cached copy was written, or null when there is none.</summary>
    public static DateTimeOffset? CachedAt => Read()?.CachedAt;

    /// <summary>How old the cached copy is, in words ("just now", "3 hours ago", "on 14 Sep"), worded
    /// like the instance list. Null when there is no cache.</summary>
    public static string? AgeInWords() => PackListCache.Describe(CachedAt);

    /// <summary>Replaces the cached team list, keeping the rosters of teams that are still in it.</summary>
    /// <remarks>Pruned so a team you left doesn't keep its member list on this PC.</remarks>
    public static void SaveTeams(IReadOnlyList<TeamSummary> teams)
    {
        var keep = teams.Select(t => Key(t.Id)).ToHashSet(StringComparer.Ordinal);
        var details = Read()?.Details ?? new Dictionary<string, TeamDetail>();
        foreach (var gone in details.Keys.Where(k => !keep.Contains(k)).ToList())
            details.Remove(gone);

        Write(new Snapshot(SchemaVersion, DateTimeOffset.UtcNow, teams.ToList(), details));
    }

    /// <summary>Stores one team's roster alongside the list, without disturbing the list itself.</summary>
    public static void SaveDetail(TeamDetail detail)
    {
        var snapshot = Read();
        var teams = snapshot?.Teams ?? new List<TeamSummary>();
        var details = snapshot?.Details ?? new Dictionary<string, TeamDetail>();
        details[Key(detail.Id)] = detail;

        // Keep the list's own timestamp, so a detail write doesn't make an old list look fresh.
        Write(new Snapshot(SchemaVersion, snapshot?.CachedAt ?? DateTimeOffset.UtcNow, teams, details));
    }

    /// <summary>The last good team list, or null when there is none.</summary>
    public static List<TeamSummary>? LoadTeams() => Read()?.Teams;

    /// <summary>The last good roster for one team, or null when it was never fetched.</summary>
    public static TeamDetail? LoadDetail(Guid teamId)
    {
        var details = Read()?.Details;
        if (details is null) return null;
        return details.TryGetValue(Key(teamId), out var detail) ? detail : null;
    }

    public static void Clear()
    {
        try { lock (Gate) File.Delete(Path); } catch { /* nothing to do */ }
    }

    private static string Key(Guid id) => id.ToString("N");

    private static Snapshot? Read()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(Path)) return null;
                var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path), Json);
                if (snapshot is null) return null;
                // An older file is not worth migrating; it is a copy of something the server owns.
                return snapshot.SchemaVersion == SchemaVersion ? snapshot : null;
            }
        }
        catch { return null; }
    }

    private static void Write(Snapshot snapshot)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
                File.Move(tmp, Path, overwrite: true);
            }
        }
        catch { /* a cache that cannot be written is not worth failing a request over */ }
    }
}
