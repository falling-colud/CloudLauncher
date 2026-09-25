using System.IO;
using System.Text.Json;

namespace CloudLauncher.Services;

/// <summary>
/// Narrows a pack's enabled mods to a test set for a Run-as-Test launch, then restores the prior
/// state. CmlLib always launches from <c>game/mods</c>, so other enabled jars get a <c>.cltest</c>
/// suffix and disabled test mods are enabled. Each move is journaled to <c>.test-scope.json</c> so
/// the layout can be restored even if the launcher is killed mid-test.
/// </summary>
public sealed class TestLaunchScope
{
    private const string Marker = ".cltest";
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    private readonly PackFolderService _packs;

    public TestLaunchScope(PackFolderService packs) { _packs = packs; }

    private string SidecarPath(Guid packId) => Path.Combine(_packs.PackRoot(packId), ".test-scope.json");

    public bool IsActive(Guid packId) => File.Exists(SidecarPath(packId));

    /// <summary>Parks every enabled jar not in <paramref name="testFileNames"/> and enables any
    /// disabled jar that is, journaling each move. <paramref name="testFileNames"/> holds the
    /// normalised <c>name.jar</c> of each test mod and its dependencies.</summary>
    public void Apply(Guid packId, ISet<string> testFileNames)
    {
        var moves = new List<TestScopeMove>();
        foreach (var folder in ModFolders(packId))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                var enabled = name.EndsWith(".jar", OIC);
                var disabled = name.EndsWith(".jar.disabled", OIC);
                if (!enabled && !disabled) continue;

                var baseName = disabled ? name[..^".disabled".Length] : name; // -> "name.jar"
                var inTest = testFileNames.Contains(baseName);

                if (enabled && !inTest)
                    SafeMove(file, file + Marker, moves);            // name.jar -> name.jar.cltest
                else if (disabled && inTest)
                    SafeMove(file, file[..^".disabled".Length], moves); // name.jar.disabled -> name.jar
            }
        }
        Write(packId, moves);
    }

    /// <summary>Reverse every journaled move and drop the sidecar.</summary>
    public void Restore(Guid packId)
    {
        var moves = Read(packId);
        if (moves is not null)
        {
            for (var i = moves.Count - 1; i >= 0; i--)
            {
                var mv = moves[i];
                try
                {
                    if (!File.Exists(mv.To)) continue;
                    if (File.Exists(mv.From)) File.Delete(mv.From);
                    File.Move(mv.To, mv.From);
                }
                catch { /* best-effort restore */ }
            }
        }
        try { File.Delete(SidecarPath(packId)); } catch { }
    }

    public void RestoreIfPending(Guid packId)
    {
        if (IsActive(packId)) Restore(packId);
    }

    private IEnumerable<string> ModFolders(Guid packId)
    {
        yield return Path.Combine(_packs.GameDir(packId), "mods");
        yield return Path.Combine(_packs.LocalDir(packId), "mods");
    }

    private static void SafeMove(string from, string to, List<TestScopeMove> journal)
    {
        try
        {
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
            journal.Add(new TestScopeMove { From = from, To = to });
        }
        catch { /* locked file: leave it, the test run can still go ahead */ }
    }

    private void Write(Guid packId, List<TestScopeMove> moves)
    {
        try { File.WriteAllText(SidecarPath(packId), JsonSerializer.Serialize(new TestScopeFile { Moves = moves })); }
        catch { /* best-effort */ }
    }

    private List<TestScopeMove>? Read(Guid packId)
    {
        var path = SidecarPath(packId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TestScopeFile>(File.ReadAllText(path))?.Moves ?? new(); }
        catch { return new(); }
    }

    private sealed class TestScopeFile { public List<TestScopeMove> Moves { get; set; } = new(); }
    private sealed class TestScopeMove { public string From { get; set; } = ""; public string To { get; set; } = ""; }
}
