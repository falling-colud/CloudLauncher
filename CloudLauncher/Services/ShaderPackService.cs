using System.IO;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Which shader loader, if any, an instance can actually run a shader pack with.</summary>
/// <remarks>Iris and its Forge fork Oculus read <c>config/iris.properties</c>; OptiFine reads
/// <c>optionsshaders.txt</c>. Writing the wrong one changes nothing in game.</remarks>
public enum ShaderLoader
{
    /// <summary>Nothing in <c>mods/</c> can load a shader pack. Installing one is a no-op in game.</summary>
    None = 0,
    Iris = 1,
    Oculus = 2,
    OptiFine = 3
}

/// <summary>Which of an instance's two shaderpacks/ folders a pack was found in.</summary>
/// <remarks>Same idea as <see cref="ResourcePackOrigin"/>: <c>local/</c> is the per-user, unsynced
/// side, hard-linked into <c>game/</c> at launch, and the content library puts packs there. Only the
/// <c>game/</c> copy is shared on a shared instance.</remarks>
public enum ShaderPackOrigin
{
    Game = 0,
    Local
}

/// <summary>The shader packs installed in each instance's <c>game/shaderpacks/</c> folder, and which
/// one Iris or OptiFine is set to use.</summary>
/// <remarks>The active pack lives in the loader's config: <c>shaderPack=</c> in
/// <c>config/iris.properties</c> for Iris and Oculus, <c>optionsshaders.txt</c> for OptiFine. Bulk
/// disk work (install, copy, delete) has an <c>Async</c> twin that runs on the thread pool, since
/// packs are 5-60 MB or thousands of files.</remarks>
public sealed class ShaderPackService(AppSettings settings, PackFolderService packs)
{
    private const string FolderName = "shaderpacks";

    /// <summary>Iris writes a pack's tuned options next to the pack as <c>&lt;file name&gt;.txt</c>.</summary>
    private const string SettingsSuffix = ".txt";

    public string FolderFor(Guid packId) => Path.Combine(packs.GameDir(packId), FolderName);

    /// <summary>The unsynced side: where the content library puts a shader, and where the launch
    /// overlay picks it up from.</summary>
    public string LocalFolderFor(Guid packId) => Path.Combine(packs.LocalDir(packId), FolderName);

    /// <summary>The settings key for a shader file inside an instance. Matches the scheme
    /// <c>ResourcePackService.Key</c> uses, so <see cref="AppSettings.ShaderPacks"/> can be shared.</summary>
    public static string Key(Guid packId, string fileName) => $"{packId:N}:{fileName}";

    /// <summary>The settings key qualified by which folder the file lives in.</summary>
    /// <remarks>The <c>game/</c> form is unchanged so names and provenance stored by earlier versions
    /// still resolve; the <c>local/</c> form is namespaced so the same file name in both folders gets
    /// two entries. Same scheme as <c>ResourcePackService.Key</c>.</remarks>
    public static string Key(Guid packId, string fileName, ShaderPackOrigin origin) =>
        origin == ShaderPackOrigin.Local ? $"{packId:N}:local/{fileName}" : Key(packId, fileName);

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

    public List<ShaderPackInfo> ScanPack(Guid packId, string packName) =>
        ScanPack(packId, packName, includeLocal: true);

    /// <summary>Every shader pack installed in an instance, with which one is active.</summary>
    /// <param name="includeLocal">Also scan the unsynced <c>local/shaderpacks/</c> folder. The content
    /// library puts packs there, so without it a just-applied pack is invisible until the next
    /// launch.</param>
    public List<ShaderPackInfo> ScanPack(Guid packId, string packName, bool includeLocal)
    {
        var gameDir = Path.Combine(packs.GameDir(packId, packName), FolderName);

        var active = ActiveShader(packId, packName);
        var result = new List<ShaderPackInfo>();

        Collect(gameDir, ShaderPackOrigin.Game);
        if (includeLocal)
        {
            try { Collect(LocalFolderFor(packId), ShaderPackOrigin.Local); }
            catch { /* no local/ folder: no unsynced packs */ }
        }
        return result;

        void Collect(string dir, ShaderPackOrigin origin)
        {
            if (!Directory.Exists(dir)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(entry);
                var isFolder = Directory.Exists(entry);
                // Zips and unpacked folders are both valid shader packs; anything else (a stray .txt, the
                // loader's cache) is not.
                if (!isFolder && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (isFolder && name.StartsWith('.')) continue;

                // After a launch the overlay has hard-linked this pack into game/, which is already listed, so
                // skip the local/ copy. A different pack with the same name still shows, so the clash is visible.
                if (origin == ShaderPackOrigin.Local
                    && PackFolderService.EntriesReferToSameContent(entry, Path.Combine(gameDir, name)))
                    continue;

                long size = 0;
                DateTime modified;
                try
                {
                    modified = isFolder ? Directory.GetLastWriteTime(entry) : File.GetLastWriteTime(entry);
                    size = isFolder ? FolderSize(entry) : new FileInfo(entry).Length;
                }
                catch { modified = DateTime.MinValue; }

                var key = Key(packId, name, origin);
                var stored = settings.GetShaderPack(key);
                // Iris writes a pack's tuning next to the copy it loaded, always in game/. That file stays per
                // instance; linking it would share one tuning across all of them.
                var settingsPath = Path.Combine(gameDir, name + SettingsSuffix);
                var hasSettings = File.Exists(settingsPath);

                result.Add(new ShaderPackInfo(
                    Key: key,
                    SourcePackId: packId,
                    SourcePackName: packName,
                    FileName: name,
                    FilePath: entry,
                    // A name the user typed wins over one derived from the file.
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
                    VersionNumber: stored?.VersionNumber,
                    Origin: origin));
            }
        }
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

    /// <summary>Which shader loader the instance has installed, by looking for its jar in
    /// <c>game/mods</c>.</summary>
    /// <remarks>Without a loader a shader pack does nothing and the game gives no hint, so the Shaders
    /// page uses this to warn up front. Matching is loose (jar names carry versions and renames) and
    /// cheap (file names only). OptiFine is reported only when no Iris-like jar is present, since an
    /// instance with both loads shaders through Iris.</remarks>
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

    /// <summary>Points the instance's shader loader at <paramref name="fileName"/> (null = turn
    /// shaders off).</summary>
    /// <remarks>Only writes the config the instance's loader reads. <c>config/iris.properties</c> is
    /// created when Iris or Oculus is present (a missing file means defaults); an OptiFine-only instance
    /// only gets <c>optionsshaders.txt</c>.</remarks>
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
    /// <param name="includeLocal">Also count one staged in <c>local/</c> (a library item that hasn't
    /// been through a launch yet).</param>
    public bool Exists(Guid packId, string fileName, bool includeLocal = false)
    {
        // Not a plain name, so nothing of that name can be in the folder.
        if (!PathSafety.IsSafeFileName(fileName)) return false;
        var path = Path.Combine(FolderFor(packId), fileName);
        if (File.Exists(path) || Directory.Exists(path)) return true;
        if (!includeLocal) return false;
        var local = Path.Combine(LocalFolderFor(packId), fileName);
        return File.Exists(local) || Directory.Exists(local);
    }

    /// <summary>A free path for <paramref name="fileName"/> inside <paramref name="dir"/>, appending
    /// -2, -3 and so on until nothing is in the way.</summary>
    /// <remarks>Like the resource pack screen's Bump: two downloads of one pack are two packs, and
    /// overwriting would lose what the user unpacked or tuned.</remarks>
    /// <exception cref="IOException"><paramref name="fileName"/> is not one plain file name. These
    /// names come from store listings and other people's packs, so every caller gets the check.</exception>
    public static string NextFreePath(string dir, string fileName)
    {
        var candidate = PathSafety.ResolveFileName(dir, fileName)
                        ?? throw new IOException($"'{fileName}' cannot be used as a file name.");
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
    /// alongside it as "name-2.zip".</param>
    /// <remarks>Installing a pack over itself is a no-op. That happens when a pack is dragged from the
    /// instance's own <c>shaderpacks/</c> back onto the list and "Replace" is chosen; without the
    /// <see cref="SamePath"/> check, replace would delete the source before copying it.</remarks>
    public string Install(Guid packId, string sourceFile, bool replace = false)
    {
        var dir = FolderFor(packId);
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(sourceFile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        // The replace branches delete what is at this path first, so it has to be a pack inside the
        // folder and never the folder itself or anything above it.
        var named = PathSafety.ResolveFileName(dir, name)
                    ?? throw new IOException($"'{name}' cannot be used as a shader pack name.");

        if (Directory.Exists(sourceFile))
        {
            // An unpacked shader folder dropped in from Explorer is as valid as a zip.
            var folderDest = replace ? named : NextFreePath(dir, name);
            if (SamePath(sourceFile, folderDest)) return folderDest;
            if (replace && Directory.Exists(folderDest)) Directory.Delete(folderDest, recursive: true);
            CopyFolder(sourceFile, folderDest);
            return folderDest;
        }

        var dest = replace ? named : NextFreePath(dir, name);
        if (SamePath(sourceFile, dest)) return dest;
        File.Copy(sourceFile, dest, overwrite: replace);
        return dest;
    }

    /// <inheritdoc cref="Install(Guid,string,bool)"/>
    public Task<string> InstallAsync(Guid packId, string sourceFile, bool replace = false,
        CancellationToken ct = default) =>
        Task.Run(() => Install(packId, sourceFile, replace), ct);

    /// <summary>Copies a shader into another instance.</summary>
    /// <remarks>The pack's Iris settings file goes with it, so the copy keeps its tuning. Copying into
    /// the instance the pack already lives in returns it untouched; with <paramref name="replace"/> the
    /// folder branch would otherwise delete the source first.</remarks>
    public string CopyTo(ShaderPackInfo shader, Guid targetPackId, bool replace = false)
    {
        var dir = Path.Combine(packs.GameDir(targetPackId), FolderName);
        Directory.CreateDirectory(dir);

        var dest = replace
            ? PathSafety.ResolveFileName(dir, shader.FileName)
              ?? throw new IOException($"'{shader.FileName}' cannot be used as a shader pack name.")
            : NextFreePath(dir, shader.FileName);
        if (SamePath(shader.FilePath, dest)) return shader.FilePath;
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

        // Same pack at the same version, so copy the provenance and keep it updatable.
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
    /// <exception cref="IOException">The pack is not an entry of this instance's shaderpacks folders;
    /// nothing was deleted.</exception>
    public void Delete(ShaderPackInfo shader)
    {
        var path = OwnEntry(shader.SourcePackId, shader.FilePath)
                   ?? throw new IOException("That shader pack is not inside this instance's shaderpacks folder.");

        // Find the twin on the other side before deleting anything: comparing content needs both paths.
        // A pack that came through local/ exists twice after a launch and the overlay only adds, so
        // deleting just one side would look like delete did nothing. A same-named pack with different
        // content belongs to the instance and is left alone.
        var otherSide = PathSafety.ResolveFileName(
            shader.Origin == ShaderPackOrigin.Local ? FolderFor(shader.SourcePackId) : LocalFolderFor(shader.SourcePackId),
            shader.FileName);
        string? twin = otherSide is not null && PackFolderService.EntriesReferToSameContent(path, otherSide)
            ? otherSide
            : null;

        if (shader.IsFolder) Directory.Delete(path, recursive: true);
        else File.Delete(path);

        if (twin is not null)
        {
            try
            {
                if (Directory.Exists(twin)) Directory.Delete(twin, recursive: true);
                else if (File.Exists(twin)) File.Delete(twin);
            }
            catch { /* the pack is gone from local/; a leftover twin is untidy, not a failure */ }
        }

        // Delete the pack's settings file too, or it is left orphaned.
        if (shader.SettingsPath is { Length: > 0 } sp && OwnEntry(shader.SourcePackId, sp) is { } settingsFile
            && File.Exists(settingsFile))
        {
            try { File.Delete(settingsFile); } catch { /* best effort */ }
        }

        // A deleted pack left in the config makes Iris fall back to "internal" with a warning.
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
        if (shader.SettingsPath is not { Length: > 0 } sp || OwnEntry(shader.SourcePackId, sp) is not { } settingsFile
            || !File.Exists(settingsFile)) return false;
        File.Delete(settingsFile);
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

    /// <summary>True when two paths name the same file or folder on disk.</summary>
    /// <remarks>Guards install/copy against source == destination (reachable by dragging a pack from the
    /// instance's own shaderpacks/ onto the list), where the replace branches would delete the source.
    /// Compares full paths case-insensitively, ignoring a trailing separator. A path that can't be
    /// resolved counts as different, so the copy fails normally.</remarks>
    private static bool SamePath(string a, string b)
    {
        try
        {
            static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The full path of <paramref name="path"/> when it names an entry directly inside one of
    /// the instance's two shaderpacks folders, else null. Deletes go through this so a path that
    /// points anywhere else is never removed.</summary>
    private string? OwnEntry(Guid packId, string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }
        var name = Path.GetFileName(full);
        foreach (var dir in new[] { FolderFor(packId), LocalFolderFor(packId) })
        {
            if (PathSafety.ResolveFileName(dir, name) is { } inside
                && string.Equals(inside, full, StringComparison.OrdinalIgnoreCase))
                return inside;
        }
        return null;
    }

    private static void CopyFolder(string source, string dest)
    {
        // Callers check this too; copying a folder onto itself would walk it while writing into it.
        if (SamePath(source, dest)) return;
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static long FolderSize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    /// <summary>"ComplementaryUnbound_r5.3.zip" -> "Complementary Unbound r5.3".</summary>
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
        catch { /* unreadable config: treat as unset */ }
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
    string? VersionNumber = null,
    ShaderPackOrigin Origin = ShaderPackOrigin.Game)
{
    /// <summary>True for a pack in the unsynced <c>local/</c> folder, usually one the content library
    /// applied. It loads like any other but isn't shared with others on a shared instance.</summary>
    public bool IsLocal => Origin == ShaderPackOrigin.Local;

    public string SizeLabel => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024:0.#} MB"
        : SizeBytes > 0 ? $"{SizeBytes / 1024} KB" : "-";

    /// <summary>The one-line subtitle under a shader pack row: where it came from, how big it is,
    /// and when it last changed.</summary>
    /// <remarks><see cref="LastModified"/> is already local time, so it goes through
    /// <see cref="TimeFormat.FromLocal"/>. The <c>default</c> check avoids <c>new DateTimeOffset</c> on a
    /// local <c>DateTime.MinValue</c>, which throws east of UTC.</remarks>
    public string MetaLabel => $"{SourcePackName}  ·  {SizeLabel}  ·  "
        + (LastModified == default ? "date unknown" : TimeFormat.Date(TimeFormat.FromLocal(LastModified)));

    /// <summary>True when the launcher knows which store listing this file came from, which is the
    /// precondition for offering an update.</summary>
    public bool HasProvenance => Source is not null && !string.IsNullOrWhiteSpace(ProjectId);

    /// <summary>Where this came from, for the row tooltip. Empty when it was added by hand.</summary>
    public string ProvenanceLabel => !HasProvenance
        ? ""
        : $"{(Source == ModSource.CurseForge ? "CurseForge" : "Modrinth")}" +
          $"{(VersionNumber is { Length: > 0 } v ? $"  ·  {v}" : "")}";
}
