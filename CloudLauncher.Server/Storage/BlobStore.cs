using System.Security.Cryptography;

namespace CloudLauncher.Server.Storage;

public class BlobStoreOptions
{
    public string RootPath { get; set; } = "";
}

/// <summary>What <see cref="BlobStore.PutAsync"/> did with one upload.</summary>
/// <param name="Hash">SHA-256 of the bytes as lowercase hex, which is also the blob's name.</param>
/// <param name="Size">How many bytes the upload held.</param>
/// <param name="Created">True when this upload put the file in the store, false when identical bytes
/// were already there.</param>
public readonly record struct StoredBlob(string Hash, long Size, bool Created);

public class BlobStore(BlobStoreOptions opts)
{
    /// <summary>Name prefix of the files uploads are written to before they are moved into place.</summary>
    public const string TempPrefix = "tmp_";

    private static readonly EnumerationOptions Walk = new() { IgnoreInaccessible = true };

    public string Root => opts.RootPath;

    /// <summary>True for the only kind of name this store gives a blob: a SHA-256 as 64 hex characters.</summary>
    public static bool IsValidHash(string? hash)
    {
        if (hash is not { Length: 64 }) return false;
        foreach (var c in hash)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

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

    public async Task<string> StoreAsync(Stream input, CancellationToken ct = default) =>
        (await PutAsync(input, ct)).Hash;

    /// <summary>Stores the bytes under their hash and says whether they were new to the store.</summary>
    /// <remarks>
    /// <para>Two uploads of the same new content can race to the final move; the loser finds the file in
    /// place, drops its temp copy and succeeds.</para>
    /// <para>Bytes already stored get their write time refreshed. That restarts the maintenance job's
    /// one-day grace period for unreferenced blobs, so a reused orphan survives until the commit that
    /// claims it.</para>
    /// </remarks>
    public async Task<StoredBlob> PutAsync(Stream input, CancellationToken ct = default)
    {
        Directory.CreateDirectory(opts.RootPath);
        var tmp = Path.Combine(opts.RootPath, TempPrefix + Guid.NewGuid().ToString("N"));

        try
        {
            string hash;
            long size;
            await using (var fs = File.Create(tmp))
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(fs, sha, CryptoStreamMode.Write))
            {
                await input.CopyToAsync(cs, ct);
                await cs.FlushAsync(ct);
                cs.FlushFinalBlock();
                hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                size = fs.Length;
            }

            var dest = PathForHash(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (TryAdopt(dest))
            {
                TryDeleteFile(tmp);
                return new StoredBlob(hash, size, Created: false);
            }

            try
            {
                File.Move(tmp, dest);
                return new StoredBlob(hash, size, Created: true);
            }
            catch (IOException) when (File.Exists(dest))
            {
                // Another upload of the same bytes moved its copy into place first.
                TryDeleteFile(tmp);
                TryAdopt(dest);
                return new StoredBlob(hash, size, Created: false);
            }
        }
        catch
        {
            TryDeleteFile(tmp);
            throw;
        }
    }

    /// <summary>Refreshes a stored blob's write time, restarting the maintenance job's grace period.</summary>
    /// <returns>False when there is no such blob.</returns>
    public bool Touch(string hash) => TryAdopt(PathForHash(hash));

    public Stream OpenRead(string hash) => File.OpenRead(PathForHash(hash));

    public long SizeOf(string hash) => new FileInfo(PathForHash(hash)).Length;

    /// <summary>Every blob file in the store, found lazily by walking the directory layout.</summary>
    /// <remarks>Only files at the path <see cref="PathForHash"/> gives a SHA-256 are returned, so temp
    /// files or other folders under the root are never offered for deletion.</remarks>
    public IEnumerable<FileInfo> EnumerateBlobFiles()
    {
        var root = new DirectoryInfo(opts.RootPath);
        if (!root.Exists) yield break;

        foreach (var first in root.EnumerateDirectories("*", Walk))
        {
            if (!IsLowerHex(first.Name, 2)) continue;
            foreach (var second in first.EnumerateDirectories("*", Walk))
            {
                if (!IsLowerHex(second.Name, 2)) continue;
                var prefix = first.Name + second.Name;
                foreach (var file in second.EnumerateFiles("*", Walk))
                    if (IsLowerHex(file.Name, 64) && file.Name.StartsWith(prefix, StringComparison.Ordinal))
                        yield return file;
            }
        }
    }

    /// <summary>Upload temp files at the top of the store, including ones a crashed upload left behind.</summary>
    public IEnumerable<FileInfo> EnumerateTempFiles()
    {
        var root = new DirectoryInfo(opts.RootPath);
        return root.Exists ? root.EnumerateFiles(TempPrefix + "*", Walk) : [];
    }

    /// <summary>Refreshes the write time of a file that exists.</summary>
    /// <returns>False only when the file is not there (any more).</returns>
    private static bool TryAdopt(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It is there; only its time could not be refreshed.
            return true;
        }
    }

    /// <summary>Best effort: a temp file that cannot be removed now is swept by the maintenance job.</summary>
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsLowerHex(string name, int length)
    {
        if (name.Length != length) return false;
        foreach (var c in name)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }
}
