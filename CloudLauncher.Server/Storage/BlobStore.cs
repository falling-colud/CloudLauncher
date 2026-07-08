using System.Security.Cryptography;

namespace CloudLauncher.Server.Storage;

public class BlobStoreOptions
{
    public string RootPath { get; set; } = "";
}

public class BlobStore(BlobStoreOptions opts)
{
    public string Root => opts.RootPath;

    public string PathForHash(string hash)
    {
        if (hash.Length < 4) throw new ArgumentException("Hash too short", nameof(hash));
        var safe = hash.ToLowerInvariant();
        foreach (var c in safe)
            if (!Uri.IsHexDigit(c))
                throw new ArgumentException("Hash must be hex", nameof(hash));
        var dir = Path.Combine(opts.RootPath, safe[..2], safe[2..4]);
        return Path.Combine(dir, safe);
    }

    public bool Exists(string hash) => File.Exists(PathForHash(hash));

    public async Task<string> StoreAsync(Stream input, CancellationToken ct = default)
    {
        Directory.CreateDirectory(opts.RootPath);
        var tmp = Path.Combine(opts.RootPath, "tmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);

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

            var dest = PathForHash(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest))
                File.Delete(tmp);
            else
                File.Move(tmp, dest);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }

        return hash;
    }

    public Stream OpenRead(string hash) => File.OpenRead(PathForHash(hash));

    public long SizeOf(string hash) => new FileInfo(PathForHash(hash)).Length;
}
