using System.Security.Cryptography;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Server.Storage;

public class LauncherStoreOptions
{
    public string RootPath { get; set; } = "";

    /// <summary>Whether POST /launcher/upload exists (config <c>Launcher:AllowUpload</c>). Off by
    /// default: releases are published by copying files into <see cref="RootPath"/>, and a route
    /// that can replace every launcher's update should not exist unless it is used.</summary>
    public bool AllowUpload { get; set; }
}

/// <summary>Stores a single "latest" launcher release: the package file plus its metadata.
/// Uploading a new release atomically replaces the previous one.</summary>
public class LauncherStore(LauncherStoreOptions opts)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _lock = new();

    private string MetaPath => Path.Combine(opts.RootPath, "latest.json");
    private string HistoryPath => Path.Combine(opts.RootPath, "releases.json");
    private string PackagePath => Path.Combine(opts.RootPath, "package.bin");
    private string InstallerPath => Path.Combine(opts.RootPath, "installer.bin");

    /// <summary>The web installer: a small setup .exe that downloads the current installer.bin and
    /// runs it. Copied here by hand (installer/README-web-installer.md); a publish never writes it,
    /// because it must keep the same bytes across releases for SmartScreen's sake.</summary>
    private string WebInstallerPath => Path.Combine(opts.RootPath, "web-installer.bin");

    /// <summary>Packages stored by content as packages/{sha256}.bin (lowercase hex), so a launcher
    /// can fetch the build it verified even after package.bin has changed.</summary>
    private string PackagesDir => Path.Combine(opts.RootPath, "packages");

    public bool AllowUpload => opts.AllowUpload;

    /// <summary>True for 64 hex digits, the only form a package hash may take before it is used in
    /// a path.</summary>
    public static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (var c in value)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
                return false;
        return true;
    }

    /// <summary>
    /// Opens the package whose SHA-256 is <paramref name="sha256"/>: packages/{sha256}.bin when the
    /// publish left one, otherwise package.bin but only while latest.json names that hash.
    /// Null when neither applies.
    /// </summary>
    public Stream? OpenPackageBySha256(string sha256)
    {
        if (!IsSha256Hex(sha256)) return null;
        var hash = sha256.ToLowerInvariant();

        var kept = Path.Combine(PackagesDir, hash + ".bin");
        lock (_lock)
        {
            if (File.Exists(kept))
            {
                try { return File.OpenRead(kept); }
                catch (FileNotFoundException) { /* removed just now: fall through */ }
                catch (DirectoryNotFoundException) { }
            }
        }

        var latest = GetLatest();
        if (latest is null || !string.Equals(latest.Sha256, hash, StringComparison.OrdinalIgnoreCase)) return null;
        return OpenPackage();
    }

    /// <summary>The published release whose package has this SHA-256, newest first, or null.</summary>
    public LauncherReleaseInfo? FindRelease(string sha256) =>
        GetHistory().FirstOrDefault(r => string.Equals(r.Sha256, sha256, StringComparison.OrdinalIgnoreCase));

    public LauncherReleaseInfo? GetLatest()
    {
        lock (_lock)
        {
            if (!File.Exists(MetaPath) || !File.Exists(PackagePath)) return null;
            try { return JsonSerializer.Deserialize<LauncherReleaseInfo>(File.ReadAllText(MetaPath), Json); }
            catch { return null; }
        }
    }

    /// <summary>How many past releases to keep. Long enough to be a changelog, short enough that the
    /// file stays a file.</summary>
    private const int MaxHistory = 60;

    /// <summary>All published releases, newest first (the launcher's changelog).</summary>
    /// <remarks>Falls back to the current release alone when there is no history yet.</remarks>
    public IReadOnlyList<LauncherReleaseInfo> GetHistory()
    {
        lock (_lock)
        {
            if (File.Exists(HistoryPath))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<List<LauncherReleaseInfo>>(File.ReadAllText(HistoryPath), Json);
                    if (stored is { Count: > 0 }) return stored;
                }
                catch { /* unreadable history: fall through to the latest */ }
            }
        }
        var latest = GetLatest();
        return latest is null ? Array.Empty<LauncherReleaseInfo>() : new[] { latest };
    }

    /// <summary>Records a release at the head of the history, replacing any entry with the same
    /// version (re-publishing a version number is a correction, not a new release).</summary>
    private void AppendHistory(LauncherReleaseInfo info)
    {
        try
        {
            var history = new List<LauncherReleaseInfo>();
            if (File.Exists(HistoryPath))
            {
                try { history = JsonSerializer.Deserialize<List<LauncherReleaseInfo>>(File.ReadAllText(HistoryPath), Json) ?? new(); }
                catch { history = new(); }
            }
            history.RemoveAll(r => string.Equals(r.Version, info.Version, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, info);
            if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);

            var tmp = HistoryPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(history, Json));
            File.Move(tmp, HistoryPath, overwrite: true);
        }
        catch { /* the changelog is not worth failing a publish over */ }
    }

    /// <summary>Opens the self-update package (zip) fetched by the in-app updater.</summary>
    public Stream? OpenPackage()
    {
        lock (_lock)
        {
            return File.Exists(PackagePath) ? File.OpenRead(PackagePath) : null;
        }
    }

    /// <summary>Opens the Windows setup .exe served to human downloaders, or null if the latest
    /// release was published without one.</summary>
    public Stream? OpenInstaller()
    {
        lock (_lock)
        {
            return File.Exists(InstallerPath) ? File.OpenRead(InstallerPath) : null;
        }
    }

    /// <summary>Opens the web installer served to human downloaders in place of the full setup .exe,
    /// or null when none has been put in place.</summary>
    public Stream? OpenWebInstaller()
    {
        lock (_lock)
        {
            return File.Exists(WebInstallerPath) ? File.OpenRead(WebInstallerPath) : null;
        }
    }

    /// <summary>Atomically replaces the "latest" release. When <paramref name="installer"/> is
    /// given it becomes the setup .exe served at <c>/download</c>; when null the previous installer
    /// is removed so it never serves a mismatched one.</summary>
    public async Task<LauncherReleaseInfo> StoreAsync(
        Stream input, string version, string fileName, string? notes,
        Stream? installer = null, string? installerFileName = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(opts.RootPath);
        var tmpPkg = Path.Combine(opts.RootPath, "tmp_" + Guid.NewGuid().ToString("N"));
        var tmpInstaller = installer is null ? null : Path.Combine(opts.RootPath, "tmp_" + Guid.NewGuid().ToString("N"));

        try
        {
            var hash = await WriteHashedAsync(input, tmpPkg, ct);
            var size = new FileInfo(tmpPkg).Length;

            string? installerHash = null;
            long installerSize = 0;
            if (installer is not null && tmpInstaller is not null)
            {
                installerHash = await WriteHashedAsync(installer, tmpInstaller, ct);
                installerSize = new FileInfo(tmpInstaller).Length;
            }

            var info = new LauncherReleaseInfo(
                version, fileName, size, hash, notes, DateTimeOffset.UtcNow,
                installer is null ? null : installerFileName, installerSize, installerHash);

            lock (_lock)
            {
                File.Move(tmpPkg, PackagePath, overwrite: true);
                if (tmpInstaller is not null)
                    File.Move(tmpInstaller, InstallerPath, overwrite: true);
                else if (File.Exists(InstallerPath))
                    File.Delete(InstallerPath); // don't leave a mismatched installer for the new version
                File.WriteAllText(MetaPath, JsonSerializer.Serialize(info, Json));
                AppendHistory(info);
            }

            return info;
        }
        catch
        {
            if (File.Exists(tmpPkg)) File.Delete(tmpPkg);
            if (tmpInstaller is not null && File.Exists(tmpInstaller)) File.Delete(tmpInstaller);
            throw;
        }
    }

    private static async Task<string> WriteHashedAsync(Stream input, string path, CancellationToken ct)
    {
        await using var fs = File.Create(path);
        using var sha = SHA256.Create();
        await using var cs = new CryptoStream(fs, sha, CryptoStreamMode.Write);
        await input.CopyToAsync(cs, ct);
        await cs.FlushAsync(ct);
        cs.FlushFinalBlock();
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
