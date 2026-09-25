using System.IO;
using System.IO.Compression;

namespace CloudLauncher.Services;

/// <summary>Which of an instance's switched-on resource packs override each other, and which one
/// the player actually sees.</summary>
/// <remarks>Packs conflict only where they share an asset path; the stack comes in priority order, so
/// the first pack holding a path wins it. Only entry names are read, never contents. This runs on
/// every Resource packs page scan, so it is capped (<see cref="MaxPacks"/>,
/// <see cref="MaxEntriesPerPack"/>, <see cref="MaxNames"/>) and says when a cap was hit.</remarks>
public static class ResourcePackConflicts
{
    /// <summary>How many of the stack's packs are opened. More would make the message unreadable
    /// anyway.</summary>
    public const int MaxPacks = 12;

    /// <summary>Entries read from one pack. Most packs have a few thousand files; big overhauls can have
    /// a hundred thousand, and the first few thousand are enough.</summary>
    public const int MaxEntriesPerPack = 20_000;

    /// <summary>Distinct overridden paths counted before the answer starts saying "at least".</summary>
    public const int MaxNames = 5_000;

    /// <summary>One pack that is losing files to the packs above it.</summary>
    /// <param name="FileName">The losing pack.</param>
    /// <param name="LostTo">The pack sitting above it that wins the most of them.</param>
    /// <param name="Count">How many of its paths something above it wins.</param>
    public sealed record Overridden(string FileName, string LostTo, int Count);

    /// <summary>
    /// The stack's conflicts, or an empty list when nothing overlaps.
    /// </summary>
    /// <param name="stack">The instance's switched-on packs, #1 first, with the path to each.</param>
    /// <param name="truncated">True when a cap was hit, so the caller can say the count is a floor.</param>
    public static IReadOnlyList<Overridden> Find(
        IReadOnlyList<(string FileName, string Path)> stack, out bool truncated, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stack);
        truncated = stack.Count > MaxPacks;

        var considered = stack.Take(MaxPacks).ToList();
        if (considered.Count < 2) return [];

        // Path -> the pack that wins it, i.e. the first one seen (the stack is in priority order).
        var winner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Losing pack -> (winning pack -> paths lost to it).
        var losses = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fileName, path) in considered)
        {
            ct.ThrowIfCancellationRequested();

            var entries = ReadNames(path, ct, out var cappedHere);
            if (entries is null) continue;              // unreadable: the row for it already says so
            truncated |= cappedHere;

            foreach (var entry in entries)
            {
                if (winner.TryGetValue(entry, out var above))
                {
                    if (!losses.TryGetValue(fileName, out var byWinner))
                        losses[fileName] = byWinner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    byWinner[above] = byWinner.GetValueOrDefault(above) + 1;
                }
                else if (winner.Count < MaxNames)
                {
                    winner[entry] = fileName;
                }
                else
                {
                    truncated = true;
                }
            }
        }

        return losses
            .Select(loser =>
            {
                var worst = loser.Value.OrderByDescending(kv => kv.Value).First();
                return new Overridden(loser.Key, worst.Key, loser.Value.Values.Sum());
            })
            .OrderByDescending(o => o.Count)
            .ToList();
    }

    /// <summary>
    /// <see cref="Find"/> as the one line the page's status bar has room for, or null when nothing
    /// overlaps.
    /// </summary>
    /// <remarks>Null rather than "no conflicts", which isn't worth a status line.</remarks>
    public static string? Describe(
        IReadOnlyList<(string FileName, string Path)> stack, CancellationToken ct = default)
    {
        var found = Find(stack, out var truncated, ct);
        if (found.Count == 0) return null;

        var worst = found[0];
        var about = $"{worst.LostTo} overrides {worst.Count}{(truncated ? "+" : "")} file(s) in {worst.FileName}";
        var rest = found.Count > 1 ? $", and {found.Count - 1} other pack(s) lose files too" : "";
        return $"{about}{rest} - #1 in the stack is the one you see.";
    }

    /// <summary>
    /// Every asset path in one pack, or null when it could not be opened.
    /// </summary>
    /// <remarks>Skips directory entries (trailing slash in a zip) and <c>pack.mcmeta</c> and
    /// <c>pack.png</c>, which every pack has.</remarks>
    private static List<string>? ReadNames(string path, CancellationToken ct, out bool capped)
    {
        capped = false;
        try
        {
            if (Directory.Exists(path))
            {
                var names = new List<string>();
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (names.Count >= MaxEntriesPerPack) { capped = true; break; }
                    var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    if (!IsPackFurniture(relative)) names.Add(relative);
                }
                return names;
            }

            if (!File.Exists(path)) return null;

            using var zip = ZipFile.OpenRead(path);
            var entries = new List<string>();
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntriesPerPack) { capped = true; break; }
                if (entry.FullName.EndsWith('/')) continue;
                if (!IsPackFurniture(entry.FullName)) entries.Add(entry.FullName);
            }
            return entries;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.LogError("resourcepack-conflicts", ex);
            return null;
        }
    }

    private static bool IsPackFurniture(string name) =>
        name.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pack.png", StringComparison.OrdinalIgnoreCase);
}
