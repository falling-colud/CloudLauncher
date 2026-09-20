using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Which shader loader, if any, an instance can actually run a shader pack with.</summary>
/// <remarks>
/// Ordered by what the launcher should believe when more than one is present: Iris and its Forge fork
/// Oculus own <c>config/iris.properties</c>, while OptiFine owns <c>optionsshaders.txt</c>. The
/// distinction is not cosmetic — writing Iris's config into an OptiFine-only instance creates a file
/// nothing reads and reports success for a change that will never happen.
/// </remarks>
public enum ShaderLoader
{
    /// <summary>Nothing in <c>mods/</c> can load a shader pack. Installing one is a no-op in game.</summary>
    None = 0,
    Iris = 1,
    Oculus = 2,
    OptiFine = 3
}

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
///
/// Everything that touches disk in bulk (installing, copying, deleting an unpacked folder) has an
/// <c>…Async</c> twin that runs the same work on a thread-pool thread: a shader pack is routinely
/// 5-60 MB and an unpacked one is thousands of small files, so doing it inline froze the window.
/// </remarks>
public sealed class ShaderPackService(AppSettings settings, PackFolderService packs)
{
    private const string FolderName = "shaderpacks";

    /// <summary>Iris writes a pack's tuned options next to the pack as <c>&lt;file name&gt;.txt</c>.</summary>
    private const string SettingsSuffix = ".txt";

    public string FolderFor(Guid packId) => Path.Combine(packs.GameDir(packId), FolderName);

    /// <summary>The settings key for a shader file inside an instance. Matches the scheme
    /// <c>ResourcePackService.Key</c> uses, so <see cref="AppSettings.ShaderPacks"/> can be shared.</summary>
    public static string Key(Guid packId, string fileName) => $"{packId:N}:{fileName}";

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

            var key = Key(packId, name);
            var stored = settings.GetShaderPack(key);
            var settingsPath = Path.Combine(dir, name + SettingsSuffix);
            var hasSettings = File.Exists(settingsPath);

            result.Add(new ShaderPackInfo(
                Key: key,
                SourcePackId: packId,
                SourcePackName: packName,
                FileName: name,
                FilePath: entry,
                // A name the user typed wins over one derived from the file: they renamed it precisely
                // because the derived one was not good enough.
                DisplayName: stored is { DisplayName.Length: > 0 } ? stored.DisplayName : PrettyName(name),
                IsFolder: isFolder,
                IsActive: string.Equals(active, name, StringComparison.OrdinalIgnoreCase),
                LastModified: modified,
                SizeBytes: size,
                HasSettings: hasSettings,
                SettingsPath: settingsPath,
                Source: stored?.Source,
                ProjectId: stored?.ProjectId,
                VersionId: stored?.VersionId,
                VersionNumber: stored?.VersionNumber));
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

    // ── loader detection ─────────────────────────────────────────────────────

    /// <summary>
    /// Which shader loader the instance has installed, by looking for its jar in <c>game/mods</c>.
    /// </summary>
    /// <remarks>
    /// A shader pack in an instance with no loader does nothing at all, and the game gives no hint:
    /// it just renders vanilla. Nothing else in the launcher can answer "why did my shader not
    /// apply?", so the Shaders page asks this and says so up front. The match is deliberately loose —
    /// jar names carry version suffixes, build numbers and mod-pack renames — and deliberately cheap:
    /// it reads file names, never opens a jar. A wrong answer costs a banner, not a failed launch.
    /// OptiFine is reported only when nothing Iris-shaped is present, because an instance carrying
    /// both loads shaders through Iris.
    /// </remarks>
    public ShaderLoader DetectLoader(Guid packId, string? packName = null)
    {
        var gameDir = packName is null ? packs.GameDir(packId) : packs.GameDir(packId, packName);
        var modsDir = Path.Combine(gameDir, "mods");

        var optifine = false;
        var oculus = false;
        if (Directory.Exists(modsDir))
        {
            foreach (var file in Directory.EnumerateFiles(modsDir))
            {
                var name = Collapse(Path.GetFileName(file));
                if (!name.Contains("jar")) continue;   // .jar / .jar.disabled only
                if (name.Contains("iris")) return ShaderLoader.Iris;
                if (name.Contains("oculus")) oculus = true;
                if (name.Contains("optifine")) optifine = true;
            }
        }
        if (oculus) return ShaderLoader.Oculus;
        if (optifine) return ShaderLoader.OptiFine;

        // OptiFine can also be installed as a launcher profile rather than a jar in mods/, in which
        // case the only trace inside the instance is the settings file it writes.
        return File.Exists(Path.Combine(gameDir, "optionsshaders.txt"))
            ? ShaderLoader.OptiFine
            : ShaderLoader.None;
    }

    /// <summary>Strips everything but letters and digits, so "Iris-1.7.5+mc1.21.1.jar" and
    /// "irisshaders.jar" collapse to comparable keys.</summary>
    private static string Collapse(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Points the instance's shader loader at <paramref name="fileName"/> (null = turn shaders off).
    /// </summary>
    /// <remarks>
    /// Writes only the config the instance's loader actually reads. Iris reads
    /// <c>config/iris.properties</c> at startup and a missing file simply means "defaults", so it is
    /// created when Iris or Oculus is present. An OptiFine-only instance gets <c>optionsshaders.txt</c>
    /// and nothing else — creating an iris.properties there used to leave a file no one reads while
    /// the launcher reported the change as done.
    /// </remarks>
    public void SetActiveShader(Guid packId, string? fileName)
    {
        var gameDir = packs.GameDir(packId);
        var optifinePath = Path.Combine(gameDir, "optionsshaders.txt");
        var loader = DetectLoader(packId);

        if (loader != ShaderLoader.OptiFine)
        {
            var configDir = Path.Combine(gameDir, "config");
            Directory.CreateDirectory(configDir);
            var iris = Path.Combine(configDir, "iris.properties");
            WriteProperty(iris, "shaderPack", fileName ?? "");
            WriteProperty(iris, "enableShaders", fileName is null ? "false" : "true");
        }

        if (loader == ShaderLoader.OptiFine || File.Exists(optifinePath))
            WriteProperty(optifinePath, "shaderPack", fileName ?? "OFF");
    }

    // ── install / copy / delete ──────────────────────────────────────────────

    /// <summary>True when the instance already holds a shader file of this name.</summary>
    public bool Exists(Guid packId, string fileName)
    {
        var path = Path.Combine(FolderFor(packId), fileName);
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>
    /// A free path for <paramref name="fileName"/> inside <paramref name="dir"/>, appending -2, -3 …
    /// until nothing is in the way.
    /// </summary>
    /// <remarks>Mirrors the resource pack screen's Bump: two downloads of the same pack are two
    /// packs, and quietly overwriting the first destroys whatever the user had unpacked or tuned.</remarks>
    public static string NextFreePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; n < 1000; n++)
        {
            candidate = Path.Combine(dir, $"{stem}-{n}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    /// <summary>Copies a shader zip from anywhere on disk into the instance. Returns the new path.</summary>
    /// <param name="replace">True overwrites a same-named pack; false (the default) installs
    /// alongside it as "name-2.zip". Only the caller knows which the user asked for.</param>
    public string Install(Guid packId, string sourceFile, bool replace = false)
    {
        var dir = FolderFor(packId);
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(sourceFile);

        if (Directory.Exists(sourceFile))
        {
            // An unpacked shader folder dropped in from Explorer is as valid as a zip.
            var folderDest = replace ? Path.Combine(dir, name) : NextFreePath(dir, name);
            if (replace && Directory.Exists(folderDest)) Directory.Delete(folderDest, recursive: true);
            CopyFolder(sourceFile, folderDest);
            return folderDest;
        }

        var dest = replace ? Path.Combine(dir, name) : NextFreePath(dir, name);
        File.Copy(sourceFile, dest, overwrite: replace);
        return dest;
    }

    /// <inheritdoc cref="Install(Guid,string,bool)"/>
    public Task<string> InstallAsync(Guid packId, string sourceFile, bool replace = false,
        CancellationToken ct = default) =>
        Task.Run(() => Install(packId, sourceFile, replace), ct);

    /// <summary>Copies a shader into another instance, so a pack you like is one click away in all of them.</summary>
    /// <remarks>The pack's Iris settings file travels with it. Leaving it behind means the copy opens
    /// at defaults, which is exactly the thing the user was trying to avoid by copying rather than
    /// re-downloading.</remarks>
    public string CopyTo(ShaderPackInfo shader, Guid targetPackId, bool replace = false)
    {
        var dir = Path.Combine(packs.GameDir(targetPackId), FolderName);
        Directory.CreateDirectory(dir);

        var dest = replace ? Path.Combine(dir, shader.FileName) : NextFreePath(dir, shader.FileName);
        if (shader.IsFolder)
        {
            if (replace && Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            CopyFolder(shader.FilePath, dest);
        }
        else File.Copy(shader.FilePath, dest, overwrite: replace);

        if (shader.SettingsPath is { Length: > 0 } src && File.Exists(src))
        {
            try { File.Copy(src, dest + SettingsSuffix, overwrite: true); }
            catch { /* the pack copied; its tuning is a bonus, not a failure */ }
        }

        // Carry the provenance across too: the copy is the same pack at the same version, so it stays
        // updatable in its new instance.
        if (shader.Source is { } source)
            settings.SetShaderPackProvenance(Key(targetPackId, Path.GetFileName(dest)),
                source, shader.ProjectId, shader.VersionId, shader.VersionNumber);

        return dest;
    }

    /// <inheritdoc cref="CopyTo(ShaderPackInfo,Guid,bool)"/>
    public Task<string> CopyToAsync(ShaderPackInfo shader, Guid targetPackId, bool replace = false,
        CancellationToken ct = default) =>
        Task.Run(() => CopyTo(shader, targetPackId, replace), ct);

    /// <summary>Removes the pack, the Iris settings file that belongs to it, and what the launcher
    /// remembered about it.</summary>
    public void Delete(ShaderPackInfo shader)
    {
        if (shader.IsFolder) Directory.Delete(shader.FilePath, recursive: true);
        else File.Delete(shader.FilePath);

        // The settings file is part of the pack, not of the folder: leaving it orphans a .txt nothing
        // will ever claim again.
        if (shader.SettingsPath is { Length: > 0 } sp && File.Exists(sp))
        {
            try { File.Delete(sp); } catch { /* best effort */ }
        }

        // Leaving a deleted pack named in the config makes Iris fall back to "internal" with a
        // warning; clearing it is the honest state.
        if (shader.IsActive) SetActiveShader(shader.SourcePackId, null);

        settings.RemoveShaderPack(shader.Key);
        settings.Save();
    }

    /// <inheritdoc cref="Delete(ShaderPackInfo)"/>
    public Task DeleteAsync(ShaderPackInfo shader, CancellationToken ct = default) =>
        Task.Run(() => Delete(shader), ct);

    /// <summary>Deletes just the pack's tuned Iris options, putting it back to the author's defaults.
    /// Returns false when there was nothing to reset.</summary>
    public bool ResetSettings(ShaderPackInfo shader)
    {
        if (shader.SettingsPath is not { Length: > 0 } sp || !File.Exists(sp)) return false;
        File.Delete(sp);
        return true;
    }

    // ── stored metadata ──────────────────────────────────────────────────────

    /// <summary>Gives a shader a display name of the user's choosing. An empty name restores the one
    /// derived from the file.</summary>
    public void Rename(string key, string? displayName)
    {
        var entry = settings.GetOrCreateShaderPack(key);
        entry.DisplayName = (displayName ?? "").Trim();
        settings.Save();
    }

    /// <summary>Records which store listing and version a freshly installed file corresponds to.</summary>
    public void RecordProvenance(Guid packId, string fileName, ModSource source,
        string? projectId, string? versionId, string? versionNumber) =>
        settings.SetShaderPackProvenance(Key(packId, fileName), source, projectId, versionId, versionNumber);

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
    long SizeBytes,
    bool HasSettings = false,
    string? SettingsPath = null,
    ModSource? Source = null,
    string? ProjectId = null,
    string? VersionId = null,
    string? VersionNumber = null)
{
    public string SizeLabel => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024:0.#} MB"
        : SizeBytes > 0 ? $"{SizeBytes / 1024} KB" : "—";

    public string MetaLabel => $"{SourcePackName}  ·  {SizeLabel}  ·  {LastModified:d MMM yyyy}";

    /// <summary>True when the launcher knows which store listing this file came from, which is the
    /// precondition for offering an update.</summary>
    public bool HasProvenance => Source is not null && !string.IsNullOrWhiteSpace(ProjectId);

    /// <summary>Where this came from, for the row tooltip. Empty when it was added by hand.</summary>
    public string ProvenanceLabel => !HasProvenance
        ? ""
        : $"{(Source == ModSource.CurseForge ? "CurseForge" : "Modrinth")}" +
          $"{(VersionNumber is { Length: > 0 } v ? $"  ·  {v}" : "")}";
}
