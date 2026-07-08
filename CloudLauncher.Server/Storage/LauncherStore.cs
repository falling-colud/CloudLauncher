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
    private string PackagePath => Path.Combine(opts.RootPath, "package.bin");

    public LauncherReleaseInfo? GetLatest()
    {
        lock (_lock)
        {
            if (!File.Exists(MetaPath) || !File.Exists(PackagePath)) return null;
            try { return JsonSerializer.Deserialize<LauncherReleaseInfo>(File.ReadAllText(MetaPath), Json); }
            catch { return null; }
        }
    }

    public Stream? OpenPackage()
    {
        lock (_lock)
        {
            return File.Exists(PackagePath) ? File.OpenRead(PackagePath) : null;
        }
    }

    public async Task<LauncherReleaseInfo> StoreAsync(
        Stream input, string version, string fileName, string? notes, CancellationToken ct = default)
    {
        Directory.CreateDirectory(opts.RootPath);
        var tmp = Path.Combine(opts.RootPath, "tmp_" + Guid.NewGuid().ToString("N"));

        string hash;
        try
        {
            await using (var fs = File.Create(tmp))
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(fs, sha, CryptoStreamMode.Write))
            {
                await input.CopyToAsync(cs, ct);
                await cs.FlushAsync(ct);
                cs.FlushFinalBlock();
                hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            var size = new FileInfo(tmp).Length;
            var info = new LauncherReleaseInfo(version, fileName, size, hash, notes, DateTimeOffset.UtcNow);

            lock (_lock)
            {
                File.Move(tmp, PackagePath, overwrite: true);
                File.WriteAllText(MetaPath, JsonSerializer.Serialize(info, Json));
            }

            return info;
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }
}
