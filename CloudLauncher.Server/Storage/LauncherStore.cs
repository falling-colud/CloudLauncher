using System.Security.Cryptography;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Server.Storage;

public class LauncherStoreOptions
{
    public string RootPath { get; set; } = "";
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

    /// <summary>
    /// Every release this server has published, newest first — the launcher's changelog.
    /// </summary>
    /// <remarks>Falls back to just the current release when there is no history yet, so a server that
    /// has never published through this code path still answers with something true.</remarks>
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
    /// version (a re-publish of the same number is a correction, not a second release).</summary>
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

    /// <summary>Atomically replaces the "latest" release. <paramref name="installer"/> is optional:
    /// when supplied it becomes the setup .exe served at <c>/download</c>; when null the previous
    /// installer file is removed so a release never serves a stale mismatched installer.</summary>
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
