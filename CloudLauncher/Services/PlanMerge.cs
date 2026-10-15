using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudLauncher.Services;

/// <summary>
/// Merges a pack's planning boards when this PC and the server both changed them since they last
/// agreed: a three-way merge against that last agreed copy, board by board and then card by card, so
/// two people working on different cards (or different boards) both keep their work.
/// </summary>
/// <remarks>
/// <para>Rules, applied to each board, card and arrow by id: changed on one side only, that side wins;
/// changed on both, this PC wins (it is the one about to send); deleted on one side and untouched on
/// the other, it goes; deleted on one side but edited on the other, the edit stays.</para>
/// <para>Some fields are this PC's own view and never count as a change: the board that was open
/// last, each board's pan and zoom, and a section's height, which the board re-derives from the
/// cards on it (and from the mods installed here, which differ between PCs). Without that, two PCs
/// could keep sending each other their own layout forever.</para>
/// </remarks>
public static class PlanMerge
{
    private static readonly JsonSerializerOptions Opts = new();

    /// <summary>The document with every per-PC field blanked, for "did anything real change".</summary>
    public static string Comparable(PackPlans doc)
    {
        var root = JsonSerializer.SerializeToNode(doc, Opts)!.AsObject();
        root.Remove(nameof(PackPlans.LastBoardId));
        if (root[nameof(PackPlans.Boards)] is JsonArray boards)
            foreach (var b in boards.OfType<JsonObject>()) StripBoard(b);
        return root.ToJsonString();
    }

    public static bool SameContent(PackPlans a, PackPlans b) => Comparable(a) == Comparable(b);

    private static void StripBoard(JsonObject board)
    {
        board.Remove(nameof(PlanBoard.PanX));
        board.Remove(nameof(PlanBoard.PanY));
        board.Remove(nameof(PlanBoard.Zoom));
        if (board[nameof(PlanBoard.Nodes)] is JsonArray nodes)
            foreach (var n in nodes.OfType<JsonObject>()) StripNode(n);
    }

    private static void StripNode(JsonObject node)
    {
        if (node[nameof(PlanNode.Kind)]?.GetValue<int>() == (int)PlanNodeKind.Section)
            node.Remove(nameof(PlanNode.H));
    }

    private static string Key(PlanBoard b)
    {
        var o = JsonSerializer.SerializeToNode(b, Opts)!.AsObject();
        StripBoard(o);
        return o.ToJsonString();
    }

    private static string Key(PlanNode n)
    {
        var o = JsonSerializer.SerializeToNode(n, Opts)!.AsObject();
        StripNode(o);
        return o.ToJsonString();
    }

    private static string Key(PlanEdge e) => JsonSerializer.Serialize(e, Opts);

    /// <summary>Folds <paramref name="remote"/> into <paramref name="local"/>.</summary>
    /// <param name="baseDoc">The copy both last agreed on; null on a PC that has never synced this
    /// pack, and then the server's boards win and boards only this PC has are added (all but an empty
    /// one the board view made just to have something to show).</param>
    public static PackPlans Merge(PackPlans? baseDoc, PackPlans local, PackPlans remote)
    {
        var result = new PackPlans
        {
            Version = Math.Max(local.Version, remote.Version),
            LastBoardId = local.LastBoardId,
            Extra = remote.Extra ?? local.Extra
        };

        var b = baseDoc?.Boards.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var l = local.Boards.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var r = remote.Boards.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());

        var kept = new Dictionary<string, PlanBoard>();
        foreach (var id in l.Keys.Concat(r.Keys).Distinct())
        {
            l.TryGetValue(id, out var lb);
            r.TryGetValue(id, out var rb);
            PlanBoard? bb = null;
            b?.TryGetValue(id, out bb);

            PlanBoard? chosen;
            if (b is null)
                chosen = rb ?? (lb is { IsEmpty: true } && remote.Boards.Count > 0 ? null : lb);
            else
                chosen = Pick(bb, lb, rb, Key, (x, y, z) => MergeBoard(x, y, z));
            if (chosen is null) continue;

            // Pan and zoom are this PC's: a board that exists here keeps where it was left.
            if (lb is not null && !ReferenceEquals(chosen, lb))
            {
                chosen.PanX = lb.PanX;
                chosen.PanY = lb.PanY;
                chosen.Zoom = lb.Zoom;
            }
            kept[id] = chosen;
        }

        var order = MergeOrder(
            baseDoc?.Boards.Select(x => x.Id).ToList(),
            local.Boards.Select(x => x.Id).ToList(),
            remote.Boards.Select(x => x.Id).ToList(),
            kept.Keys.ToHashSet());
        result.Boards = order.Select(id => kept[id]).ToList();

        result.BoardCategories = MergeNames(baseDoc?.BoardCategories, local.BoardCategories, remote.BoardCategories);
        return result;
    }

    /// <summary>The three-way rule for one item. <paramref name="both"/> resolves an item changed on
    /// both sides; null there means this PC's copy wins.</summary>
    private static T? Pick<T>(T? bb, T? lb, T? rb, Func<T, string> key, Func<T, T, T, T>? both) where T : class
    {
        if (lb is null && rb is null) return null;
        if (lb is null)
            // Gone here: new on the server, or deleted here (it goes, unless the server edited it).
            return bb is null || key(bb) != key(rb!) ? rb : null;
        if (rb is null)
            return bb is null || key(bb) != key(lb) ? lb : null;

        var lk = key(lb);
        var rk = key(rb);
        if (lk == rk) return rb;
        if (bb is null) return lb;
        var bk = key(bb);
        if (bk == lk) return rb;
        if (bk == rk) return lb;
        return both is null ? lb : both(bb, lb, rb);
    }

    private static PlanBoard MergeBoard(PlanBoard bb, PlanBoard lb, PlanBoard rb)
    {
        var merged = new PlanBoard
        {
            Id = lb.Id,
            Name = Field(bb.Name, lb.Name, rb.Name) ?? lb.Name,
            Category = Field(bb.Category, lb.Category, rb.Category),
            SectionMembershipSet = lb.SectionMembershipSet || rb.SectionMembershipSet,
            PanX = lb.PanX, PanY = lb.PanY, Zoom = lb.Zoom,
            Extra = rb.Extra ?? lb.Extra
        };
        merged.Nodes = MergeItems(bb.Nodes, lb.Nodes, rb.Nodes, n => n.Id, Key);
        merged.Edges = MergeItems(bb.Edges, lb.Edges, rb.Edges, e => e.Id, Key);

        // What one side deleted can leave the other side's arrows and memberships pointing at nothing.
        var ids = merged.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        merged.Edges.RemoveAll(e => !ids.Contains(e.FromId) || !ids.Contains(e.ToId));
        var sections = merged.Nodes.Where(n => n.Kind == PlanNodeKind.Section).Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var n in merged.Nodes)
            if (n.SectionId is not null && !sections.Contains(n.SectionId)) n.SectionId = null;
        return merged;
    }

    private static List<T> MergeItems<T>(List<T> bs, List<T> ls, List<T> rs, Func<T, string> id,
        Func<T, string> key) where T : class
    {
        var b = bs.GroupBy(id).ToDictionary(g => g.Key, g => g.First());
        var l = ls.GroupBy(id).ToDictionary(g => g.Key, g => g.First());
        var r = rs.GroupBy(id).ToDictionary(g => g.Key, g => g.First());
        var kept = new Dictionary<string, T>();
        foreach (var k in l.Keys.Concat(r.Keys).Distinct())
        {
            b.TryGetValue(k, out var bi);
            l.TryGetValue(k, out var li);
            r.TryGetValue(k, out var ri);
            if (Pick(bi, li, ri, key, null) is { } chosen) kept[k] = chosen;
        }
        // Drawing order matters (later cards sit on top), so it merges like the board list does.
        var order = MergeOrder(bs.Select(id).ToList(), ls.Select(id).ToList(), rs.Select(id).ToList(),
            kept.Keys.ToHashSet());
        return order.Select(k => kept[k]).ToList();
    }

    private static string? Field(string? b, string? l, string? r) =>
        string.Equals(l, b, StringComparison.Ordinal) ? r : l;

    /// <summary>The order of the kept ids: this PC's order when only this PC reordered, else the
    /// server's; ids only the other side has go in after the id they followed there.</summary>
    private static List<string> MergeOrder(List<string>? b, List<string> l, List<string> r, HashSet<string> kept)
    {
        var useLocal = b is not null && !SameOrder(b, l) && SameOrder(b, r);
        var primary = useLocal ? l : r;
        var secondary = useLocal ? r : l;

        var result = primary.Where(kept.Contains).Distinct().ToList();
        var placed = result.ToHashSet(StringComparer.Ordinal);
        string? previous = null;
        foreach (var id in secondary)
        {
            if (kept.Contains(id) && placed.Add(id))
            {
                var at = previous is null ? 0 : result.IndexOf(previous) + 1;
                result.Insert(at, id);
            }
            if (placed.Contains(id)) previous = id;
        }
        foreach (var id in kept)
            if (placed.Add(id)) result.Add(id);
        return result;
    }

    /// <summary>Whether the ids both lists hold appear in the same order in each.</summary>
    private static bool SameOrder(List<string> a, List<string> b)
    {
        var inB = b.ToHashSet(StringComparer.Ordinal);
        var inA = a.ToHashSet(StringComparer.Ordinal);
        return a.Where(inB.Contains).SequenceEqual(b.Where(inA.Contains));
    }

    /// <summary>Board category names: the server's list, plus the names this PC added, minus the ones
    /// it removed, in this PC's order when only this PC reordered.</summary>
    private static List<string> MergeNames(List<string>? b, List<string> l, List<string> r)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var baseSet = (b ?? new List<string>()).ToHashSet(cmp);
        var removedHere = b is null ? new HashSet<string>(cmp) : baseSet.Where(n => !l.Contains(n, cmp)).ToHashSet(cmp);
        var removedThere = b is null ? new HashSet<string>(cmp) : baseSet.Where(n => !r.Contains(n, cmp)).ToHashSet(cmp);
        var kept = l.Concat(r).Where(n => !removedHere.Contains(n) && !removedThere.Contains(n))
            .Distinct(cmp).ToHashSet(cmp);
        // Case-insensitive names, matched back to one spelling each for the order merge.
        var spelling = new Dictionary<string, string>(cmp);
        foreach (var n in r.Concat(l)) spelling.TryAdd(n, n);
        var order = MergeOrder(
            b?.Select(n => spelling.GetValueOrDefault(n, n)).ToList(),
            l.Select(n => spelling[n]).ToList(),
            r.Select(n => spelling[n]).ToList(),
            kept.Select(n => spelling[n]).ToHashSet());
        return order;
    }
}
