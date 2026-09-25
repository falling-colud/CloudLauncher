using System.IO;
using System.Text.Json;

namespace CloudLauncher.Services;

/// <summary>What one instance has decided about one of the user's default items.</summary>
public enum ContentDefaultChoice
{
    /// <summary>Do whatever the item's rules say. The default, and the only value that needs no
    /// storage.</summary>
    Follow = 0,
    /// <summary>Never in this instance, whatever the rules say.</summary>
    Excluded = 1,
    /// <summary>Always in this instance, even when the rules don't cover it. The only state that lets a
    /// default replace a shader the user chose.</summary>
    Forced = 2
}

/// <summary>
/// One instance's answers to the user's shared defaults, plus a record of what the reconciler itself
/// changed there.
/// </summary>
/// <remarks>
/// <para>Stored at <c>&lt;pack root&gt;/content-defaults.json</c>, beside <c>game/</c> rather than
/// in it: files under <c>game/</c> can be synced, and a personal opt-out must not reach
/// collaborators.</para>
/// <para>The records (<see cref="ActivatedByDefaults"/>, <see cref="ActivatedShader"/>,
/// <see cref="UnpackedBundleFiles"/>) list only what this engine switched on or wrote, so undoing a
/// default never touches a pack the user enabled or a config they edited. Same approach as
/// <see cref="LowModeService"/>. They are stored rather than recomputed because a wrong guess would
/// delete the user's work.</para>
/// <para><see cref="InstantiatedWorlds"/> records which save folder each world template became, so a
/// later reconcile never copies the template over a save in use.</para>
/// <para>A missing or unreadable file means "no overrides, nothing recorded". The engine then stops
/// claiming those entries instead of switching off things it can't prove it set.</para>
/// </remarks>
public sealed class InstanceContentOverrides
{
    /// <summary>The file name inside the pack root. Beside <c>game/</c>, never inside it.</summary>
    public const string FileName = "content-defaults.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Stored shape version. Adding optional properties does not need a bump; a load that
    /// cannot be parsed starts empty.</summary>
    public int Version { get; set; } = 1;

    /// <summary>"Leave this instance alone": no default is placed or switched on here.</summary>
    /// <remarks>Items explicitly <see cref="ContentDefaultChoice.Forced"/> still apply, since the user
    /// forced them into this instance.</remarks>
    public bool OptOutOfAllDefaults { get; set; }

    /// <summary>Library key -> this instance's answer. Only non-<see cref="ContentDefaultChoice.Follow"/>
    /// entries are stored; <see cref="SetChoice"/> removes the rest.</summary>
    public Dictionary<string, ContentDefaultChoice> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resource pack file names this engine added to <c>options.txt</c>. Never anything the
    /// user enabled themselves.</summary>
    public List<string> ActivatedByDefaults { get; set; } = new();

    /// <summary>The shader file name this engine pointed the instance at, or null. One entry because
    /// only one shader can be active at a time.</summary>
    public string? ActivatedShader { get; set; }

    /// <summary>Library key -> the <c>saves/&lt;folder&gt;</c> a world template became here.</summary>
    public Dictionary<string, string> InstantiatedWorlds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Library key -> the files a config or KubeJS bundle wrote here, as they were when it
    /// wrote them. Un-applying a bundle may only delete these, and only while unchanged.</summary>
    public Dictionary<string, List<UnpackedFile>> UnpackedBundleFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── storage ──────────────────────────────────────────────────────────────

    /// <summary>Where the file sits for one instance.</summary>
    public static string PathFor(PackFolderService packs, Guid packId) =>
        Path.Combine(packs.PackRoot(packId), FileName);

    /// <summary>Reads one instance's overrides; never throws.</summary>
    public static InstanceContentOverrides Load(PackFolderService packs, Guid packId)
    {
        try
        {
            var path = PathFor(packs, packId);
            if (!File.Exists(path)) return new InstanceContentOverrides();
            return JsonSerializer.Deserialize<InstanceContentOverrides>(File.ReadAllText(path))
                   ?? new InstanceContentOverrides();
        }
        catch (Exception ex)
        {
            // An instance with no folder yet, or a file somebody hand-edited into invalid JSON. Both
            // mean "no overrides"; refusing to plan would leave the user with no way to fix it.
            AppLog.LogError("content-defaults", ex);
            return new InstanceContentOverrides();
        }
    }

    /// <summary>Writes the file atomically. Failure is logged, not thrown: the reconcile that just
    /// happened is still valid, and the worst case is one repeated apply.</summary>
    public void Save(PackFolderService packs, Guid packId)
    {
        try
        {
            var path = PathFor(packs, packId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".cl-tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("content-defaults", ex);
        }
    }

    // ── choices ──────────────────────────────────────────────────────────────

    public ContentDefaultChoice ChoiceFor(string key) =>
        Items.TryGetValue(key, out var choice) ? choice : ContentDefaultChoice.Follow;

    /// <summary>Records this instance's answer for one item. <see cref="ContentDefaultChoice.Follow"/>
    /// removes the entry rather than storing it, so the file only ever lists the exceptions.</summary>
    public void SetChoice(string key, ContentDefaultChoice choice)
    {
        if (choice == ContentDefaultChoice.Follow) Items.Remove(key);
        else Items[key] = choice;
    }

    public bool IsExcluded(string key) => ChoiceFor(key) == ContentDefaultChoice.Excluded;
    public bool IsForced(string key) => ChoiceFor(key) == ContentDefaultChoice.Forced;

    /// <summary>The save folder a world template became in this instance, or null when it has never
    /// been copied in.</summary>
    public string? WorldFolderFor(string key) =>
        InstantiatedWorlds.TryGetValue(key, out var folder) && folder.Length > 0 ? folder : null;

    /// <summary>The files one bundle wrote here, or an empty list when it has not been unpacked.</summary>
    public IReadOnlyList<UnpackedFile> BundleFilesFor(string key) =>
        UnpackedBundleFiles.TryGetValue(key, out var files) ? files : [];

    /// <summary>True when this engine is the one that switched <paramref name="fileName"/> on.</summary>
    public bool DidActivate(string fileName) =>
        ActivatedByDefaults.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One file a config or KubeJS bundle unpacked, and what it looked like at the time.</summary>
/// <remarks>
/// Un-applying a bundle deletes files, and the user may have edited them since. Length plus last-write
/// time tells "as we left it" from "edited" with one stat call per file, without hashing whole script
/// folders. An edit that keeps both length and timestamp goes unnoticed, but that needs a tool that
/// restores the mtime.
/// </remarks>
public sealed class UnpackedFile
{
    /// <summary><c>local/</c>-relative path, forward slashes, as the unpack recorded it.</summary>
    public string Path { get; set; } = "";

    /// <summary>Length in bytes when the bundle wrote it.</summary>
    public long Size { get; set; }

    /// <summary>Last-write time in ticks when the bundle wrote it.</summary>
    public long MTicks { get; set; }

    /// <summary>True when the file on disk still has the length and timestamp the bundle left, so it is
    /// safe to remove on the bundle's behalf.</summary>
    public bool StillUntouched(string fullPath)
    {
        try
        {
            var info = new System.IO.FileInfo(fullPath);
            // A file that is gone is not "touched": there is nothing to protect and nothing to delete.
            if (!info.Exists) return true;
            return info.Length == Size && info.LastWriteTimeUtc.Ticks == MTicks;
        }
        catch
        {
            // Unreadable means we cannot prove it is ours, so we do not touch it.
            return false;
        }
    }
}
