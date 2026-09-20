using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// The shader packs installed in each instance's <c>game/shaderpacks/</c> folder, and which one Iris
/// or OptiFine is set to use.
/// </summary>
/// <remarks>
/// Shaders are not mods and not resource packs: they are zips (or folders) dropped in their own
/// directory, and which one is active lives in the shader loader's config rather than in
/// <c>options.txt</c>. Iris and its forks (Oculus on Forge) keep it in
/// <c>config/iris.properties</c> as <c>shaderPack=</c>; OptiFine uses <c>optionsshaders.txt</c>.
/// Reading and writing those two files is all it takes to switch packs from the launcher, and it is
/// worth doing here because the alternative is launching the game to find out what is enabled.
/// </remarks>
public sealed class ShaderPackService(AppSettings settings, PackFolderService packs)
{
    private const string FolderName = "shaderpacks";

    public string FolderFor(Guid packId) => Path.Combine(packs.GameDir(packId), FolderName);

    public List<ShaderPackInfo> ScanAll(IReadOnlyList<PackSummary> knownPacks)
    {
        var result = new List<ShaderPackInfo>();
        foreach (var p in knownPacks)
        {
            try { result.AddRange(ScanPack(p.Id, p.Name)); }
            catch { /* a pack that has never been launched has no folder yet */ }
        }
        return result.OrderByDescending(r => r.LastModified).ToList();
    }

    public List<ShaderPackInfo> ScanPack(Guid packId, string packName)
    {
        var dir = Path.Combine(packs.GameDir(packId, packName), FolderName);
        if (!Directory.Exists(dir)) return new();

        var active = ActiveShader(packId, packName);
        var result = new List<ShaderPackInfo>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(entry);
            var isFolder = Directory.Exists(entry);
            // A zip, or an unpacked shader folder — both are valid to Iris. Anything else (a stray
            // .txt, the loader's own cache) is not a shader pack.
            if (!isFolder && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (isFolder && name.StartsWith('.')) continue;

            long size = 0;
            DateTime modified;
            try
            {
                modified = isFolder ? Directory.GetLastWriteTime(entry) : File.GetLastWriteTime(entry);
                size = isFolder ? FolderSize(entry) : new FileInfo(entry).Length;
            }
            catch { modified = DateTime.MinValue; }

            result.Add(new ShaderPackInfo(
                Key: $"{packId:N}:{name}",
                SourcePackId: packId,
                SourcePackName: packName,
                FileName: name,
                FilePath: entry,
                DisplayName: PrettyName(name),
                IsFolder: isFolder,
                IsActive: string.Equals(active, name, StringComparison.OrdinalIgnoreCase),
                LastModified: modified,
                SizeBytes: size));
        }
        return result;
    }

    /// <summary>The shader pack the instance is currently set to load, or null for "none".</summary>
    public string? ActiveShader(Guid packId, string? packName = null)
    {
        var gameDir = packName is null ? packs.GameDir(packId) : packs.GameDir(packId, packName);
        var iris = Path.Combine(gameDir, "config", "iris.properties");
        if (File.Exists(iris))
        {
            var value = ReadProperty(iris, "shaderPack");
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        var optifine = Path.Combine(gameDir, "optionsshaders.txt");
        if (File.Exists(optifine))
        {
            var value = ReadProperty(optifine, "shaderPack");
            if (!string.IsNullOrWhiteSpace(value) && !value.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    /// <summary>
    /// Points the instance's shader loader at <paramref name="fileName"/> (null = turn shaders off).
    /// Writes whichever config files exist, and creates Iris's when neither does — Iris reads it at
    /// startup and a missing file simply means "defaults".
    /// </summary>
    public void SetActiveShader(Guid packId, string? fileName)
    {
        var gameDir = packs.GameDir(packId);
        var configDir = Path.Combine(gameDir, "config");
        Directory.CreateDirectory(configDir);

        var iris = Path.Combine(configDir, "iris.properties");
        WriteProperty(iris, "shaderPack", fileName ?? "");
        WriteProperty(iris, "enableShaders", fileName is null ? "false" : "true");

        var optifine = Path.Combine(gameDir, "optionsshaders.txt");
        if (File.Exists(optifine)) WriteProperty(optifine, "shaderPack", fileName ?? "OFF");
    }

    /// <summary>Copies a shader zip from anywhere on disk into the instance. Returns the new path.</summary>
    public string Install(Guid packId, string sourceFile)
    {
        var dir = FolderFor(packId);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(sourceFile));
        File.Copy(sourceFile, dest, overwrite: true);
        return dest;
    }

    public void Delete(ShaderPackInfo shader)
    {
        if (shader.IsFolder) Directory.Delete(shader.FilePath, recursive: true);
        else File.Delete(shader.FilePath);
        // Leaving a deleted pack named in the config makes Iris fall back to "internal" with a
        // warning; clearing it is the honest state.
        if (shader.IsActive) SetActiveShader(shader.SourcePackId, null);
    }

    /// <summary>Copies a shader into another instance, so a pack you like is one click away in all of them.</summary>
    public string CopyTo(ShaderPackInfo shader, Guid targetPackId)
    {
        var dir = Path.Combine(packs.GameDir(targetPackId), FolderName);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, shader.FileName);
        if (shader.IsFolder) CopyFolder(shader.FilePath, dest);
        else File.Copy(shader.FilePath, dest, overwrite: true);
        return dest;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void CopyFolder(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }

    private static long FolderSize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    /// <summary>"ComplementaryUnbound_r5.3.zip" → "Complementary Unbound r5.3".</summary>
    public static string PrettyName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('-', ' ');
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? ReadProperty(string path, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                if (!trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return trimmed[(eq + 1)..].Trim();
            }
        }
        catch { /* unreadable config — treat as unset */ }
        return null;
    }

    /// <summary>Sets one key in a java .properties-style file, leaving every other line untouched
    /// (these files carry the loader's own settings, which are not ours to rewrite).</summary>
    private static void WriteProperty(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var written = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0 || !trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = $"{key}={value}";
            written = true;
            break;
        }
        if (!written) lines.Add($"{key}={value}");

        var tmp = path + ".cl-tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
    }

    public void Save() => settings.Save();
}

public sealed record ShaderPackInfo(
    string Key,
    Guid SourcePackId,
    string SourcePackName,
    string FileName,
    string FilePath,
    string DisplayName,
    bool IsFolder,
    bool IsActive,
    DateTime LastModified,
    long SizeBytes)
{
    public string SizeLabel => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024:0.#} MB"
        : SizeBytes > 0 ? $"{SizeBytes / 1024} KB" : "—";

    public string MetaLabel => $"{SourcePackName}  ·  {SizeLabel}  ·  {LastModified:d MMM yyyy}";
}
