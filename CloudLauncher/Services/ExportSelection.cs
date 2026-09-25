namespace CloudLauncher.Services;

/// <summary>
/// Which files under an instance's <c>game/</c> folder go into an export's <c>overrides/</c>: one
/// include-or-leave-out decision per path, inherited by everything below it.
/// </summary>
/// <remarks>
/// <para>Decisions per path rather than a file list, so a tree of clicks (tick <c>config</c>,
/// untick one folder in it, tick one file back) stays a few entries, and a ticked folder still
/// takes files that appear in it before the export runs.</para>
/// <para>Invariant: a decision is only stored where it differs from the inherited one, and setting
/// a path clears every decision below it. So "is anything below decided differently" is the same as
/// "is anything stored below", which lets <see cref="StateOf"/> report "partly" without a directory
/// listing and lets the exporter skip a whole folder at once.</para>
/// <para>Paths are relative to <c>game/</c>, use <c>/</c>, have no leading or trailing slash, and
/// compare case-insensitively. Nothing is included until something says so.</para>
/// </remarks>
public sealed class ExportSelection
{
    private readonly Dictionary<string, bool> _decisions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same path the way every method here spells it.</summary>
    public static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').Trim('/');

    /// <summary>A path's parent, or null for a top-level entry.</summary>
    public static string? ParentOf(string relativePath)
    {
        var cut = relativePath.LastIndexOf('/');
        return cut < 0 ? null : relativePath[..cut];
    }

    /// <summary>True when this path goes into the export: its own decision, else its nearest
    /// ancestor's.</summary>
    public bool IsIncluded(string relativePath)
    {
        for (string? p = Normalize(relativePath); p is not null; p = ParentOf(p))
            if (_decisions.TryGetValue(p, out var include)) return include;
        return false;
    }

    /// <summary>
    /// For a folder: true when it and everything in it go in, false when none of it does, null when
    /// only part of it does. For a file it is simply <see cref="IsIncluded"/>.
    /// </summary>
    public bool? StateOf(string relativePath)
    {
        var path = Normalize(relativePath);
        var own = IsIncluded(path);
        return HasDecisionsBelow(path) ? null : own;
    }

    /// <summary>True when anything inside this path was decided on its own.</summary>
    public bool HasDecisionsBelow(string relativePath)
    {
        var prefix = Normalize(relativePath) + "/";
        foreach (var key in _decisions.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Includes or leaves out a path and everything under it.</summary>
    public void Set(string relativePath, bool include)
    {
        var path = Normalize(relativePath);
        if (path.Length == 0) return;
        var prefix = path + "/";
        foreach (var key in _decisions.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _decisions.Remove(key);
        _decisions.Remove(path);
        var parent = ParentOf(path);
        var inherited = parent is not null && IsIncluded(parent);
        if (inherited != include) _decisions[path] = include;
    }

    /// <summary>Leaves everything out.</summary>
    public void Clear() => _decisions.Clear();

    /// <summary>The top-level entries with anything included, for the receipt.</summary>
    public IReadOnlyList<string> TopLevelWithAnything()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, include) in _decisions)
        {
            if (!include) continue;
            var cut = key.IndexOf('/');
            names.Add(cut < 0 ? key : key[..cut]);
        }
        return names.ToList();
    }

    /// <summary>A copy, so the export in flight is not changed by clicks made while it runs.</summary>
    public ExportSelection Clone()
    {
        var copy = new ExportSelection();
        foreach (var (key, include) in _decisions) copy._decisions[key] = include;
        return copy;
    }

    /// <summary>How many decisions are stored, for tests and the log.</summary>
    public int DecisionCount => _decisions.Count;
}
