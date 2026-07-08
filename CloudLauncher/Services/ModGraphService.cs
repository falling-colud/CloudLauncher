namespace CloudLauncher.Services;

/// <summary>
/// An installed-mod dependency graph built from the unified inventory. Edges come from each mod's
/// resolved version dependencies (matched to other installed mods by project id, across both
/// stores) plus any manually-declared dependency keys. Powers the disable/delete cascade, the
/// library-aware cleanup, the "Run as Test" resolution, and the Graph view's dependency lines.
/// </summary>
public sealed class ModGraph
{
    private readonly Dictionary<PackMod, HashSet<PackMod>> _deps = new();
    private readonly Dictionary<PackMod, HashSet<PackMod>> _dependents = new();

    private ModGraph() { }

    public static ModGraph Build(IReadOnlyList<PackMod> mods)
    {
        var g = new ModGraph();
        foreach (var m in mods) { g._deps[m] = new(); g._dependents[m] = new(); }

        // Index installed mods by every id/key they can be referenced through.
        var byModrinth = new Dictionary<string, PackMod>(StringComparer.OrdinalIgnoreCase);
        var byCurse = new Dictionary<string, PackMod>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, PackMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            if (m.Modrinth is not null) byModrinth[m.Modrinth.Id] = m;
            if (m.CurseForge is not null) byCurse[m.CurseForge.Id] = m;
            foreach (var k in m.CandidateKeys) byKey[k] = m;
        }

        foreach (var m in mods)
        {
            foreach (var dep in m.RequiredDependencies)
            {
                if (string.IsNullOrEmpty(dep.ProjectId)) continue;
                PackMod? target = dep.Source switch
                {
                    ModSource.CurseForge => byCurse.GetValueOrDefault(dep.ProjectId),
                    _ => byModrinth.GetValueOrDefault(dep.ProjectId)
                };
                // Cross-store fallback: a dep declared on one store may be installed from the other.
                target ??= byModrinth.GetValueOrDefault(dep.ProjectId) ?? byCurse.GetValueOrDefault(dep.ProjectId);
                if (target is not null && !ReferenceEquals(target, m)) g.Link(m, target);
            }

            foreach (var key in m.Meta.ManualDependencies)
                if (byKey.TryGetValue(key, out var target) && !ReferenceEquals(target, m))
                    g.Link(m, target);
        }

        return g;
    }

    private void Link(PackMod from, PackMod to)
    {
        _deps[from].Add(to);
        _dependents[to].Add(from);
    }

    public IReadOnlyCollection<PackMod> DependenciesOf(PackMod m) =>
        _deps.TryGetValue(m, out var s) ? s : Array.Empty<PackMod>();

    public IReadOnlyCollection<PackMod> DependentsOf(PackMod m) =>
        _dependents.TryGetValue(m, out var s) ? s : Array.Empty<PackMod>();

    /// <summary>All mods that (transitively) depend on <paramref name="m"/>.</summary>
    public List<PackMod> AllDependents(PackMod m)
    {
        var seen = new HashSet<PackMod>();
        var stack = new Stack<PackMod>(DependentsOf(m));
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            foreach (var d in DependentsOf(cur)) stack.Push(d);
        }
        return seen.ToList();
    }

    /// <summary>The transitive closure of <paramref name="roots"/> plus all their dependencies
    /// (used to launch a test set with everything it needs).</summary>
    public HashSet<PackMod> Closure(IEnumerable<PackMod> roots)
    {
        var seen = new HashSet<PackMod>();
        var stack = new Stack<PackMod>(roots);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            foreach (var d in DependenciesOf(cur)) stack.Push(d);
        }
        return seen;
    }
}

/// <summary>The set of mods a disable/delete should cascade to.</summary>
public sealed record DisablePlan(PackMod Target, IReadOnlyList<PackMod> AlsoDisable);

public static class ModGraphService
{
    /// <summary>
    /// Plans a disable/delete of <paramref name="target"/>: every currently-enabled mod that
    /// (transitively) depends on it must be disabled too. When <paramref name="includeLibraries"/>
    /// is set, library dependencies of the disabled set that no other enabled mod still needs are
    /// cleaned up as well. <paramref name="target"/> itself is returned separately so a delete can
    /// remove it while merely disabling the rest.
    /// </summary>
    public static DisablePlan PlanDisable(IReadOnlyList<PackMod> all, PackMod target, bool includeLibraries)
    {
        var graph = ModGraph.Build(all);
        var set = new HashSet<PackMod> { target };
        foreach (var dep in graph.AllDependents(target))
            if (dep.Enabled) set.Add(dep);

        if (includeLibraries)
        {
            foreach (var m in set.ToList())
            {
                foreach (var d in graph.DependenciesOf(m))
                {
                    if (!d.IsLibrary || !d.Enabled || set.Contains(d)) continue;
                    // Orphaned only if every remaining enabled dependent is also being disabled.
                    var stillNeeded = graph.DependentsOf(d).Any(x => x.Enabled && !set.Contains(x));
                    if (!stillNeeded) set.Add(d);
                }
            }
        }

        set.Remove(target);
        return new DisablePlan(target, set.ToList());
    }

    /// <summary>The disabled dependencies that enabling <paramref name="target"/> should also
    /// enable (so a mod is never enabled without the mods it requires).</summary>
    public static IReadOnlyList<PackMod> PlanEnable(IReadOnlyList<PackMod> all, PackMod target)
    {
        var graph = ModGraph.Build(all);
        return graph.Closure(new[] { target })
            .Where(m => !ReferenceEquals(m, target) && !m.Enabled)
            .ToList();
    }
}
