using System.IO;
using System.IO.Compression;

namespace CloudLauncher.Services;

/// <summary>Checks for relative paths that come from outside the launcher: sync manifests, modpack
/// archives, bundles, world zips and store metadata.</summary>
public static class PathSafety
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>Joins <paramref name="relative"/> onto <paramref name="root"/> and returns the full path,
    /// or null when it is not a plain relative path or would end up outside the root.</summary>
    /// <remarks>Both slash styles are accepted. Rejected: empty paths, rooted or drive-relative paths,
    /// UNC paths, "." and ".." segments, colons (drive letters and alternate data streams), device
    /// names, names ending in a dot or space, and characters Windows does not allow in names.</remarks>
    public static string? ResolveInside(string root, string? relative)
    {
        if (!IsSafeRelativePath(relative)) return null;
        string fullRoot;
        try { fullRoot = Path.GetFullPath(root); }
        catch { return null; }
        var rootWithSlash = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(rootWithSlash, relative!.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return null; }
        return full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>True when <paramref name="relative"/> is a plain relative path that names something
    /// below its root. Does not touch the disk.</summary>
    public static bool IsSafeRelativePath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024) return false;
        if (relative.Contains('\0') || relative.Contains(':')) return false;
        var normal = relative.Replace('\\', '/');
        if (normal.StartsWith('/')) return false;
        var segments = normal.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
            if (segment.IndexOfAny(InvalidNameChars) >= 0) return false;
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (DeviceNames.Contains(stem)) return false;
        }
        return true;
    }

    /// <summary>True when <paramref name="name"/> is one plain file or folder name: no separators
    /// and nothing <see cref="IsSafeRelativePath"/> would refuse. Use it for names from store
    /// metadata.</summary>
    public static bool IsSafeFileName(string? name) =>
        IsSafeRelativePath(name) && name!.IndexOf('/') < 0 && name.IndexOf('\\') < 0;

    /// <summary>Joins a plain file name onto <paramref name="folder"/>, or returns null when the name
    /// is not a plain name (see <see cref="IsSafeFileName"/>).</summary>
    public static string? ResolveFileName(string folder, string? name) =>
        IsSafeFileName(name) ? ResolveInside(folder, name) : null;

    /// <summary>True when a folder between <paramref name="root"/> (not included) and
    /// <paramref name="fullPath"/> (not included) is a junction or symbolic link, or when that cannot
    /// be told. Deleting through such a folder could reach somewhere outside the root.</summary>
    public static bool CrossesLink(string root, string fullPath)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var dir = Path.GetDirectoryName(Path.GetFullPath(fullPath));
            while (!string.IsNullOrEmpty(dir) && dir.Length > fullRoot.Length)
            {
                if (!dir.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
                var info = new DirectoryInfo(dir);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> itself or somewhere
    /// below it.</summary>
    public static bool IsInside(string root, string path)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Unpacks zip archives other people made (modpacks, bundles, world and pack zips) without
/// letting an entry land outside its folder or expand without limit.</summary>
public static class SafeZip
{
    /// <summary>Most an archive may unpack to in total.</summary>
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Most entries an archive may hold.</summary>
    public const int MaxEntries = 200_000;

    // Deflate tops out a little above 1000:1, so a sizeable entry claiming more is crafted.
    private const long RatioCheckFromBytes = 1024 * 1024;
    private const long MaxRatio = 1000;

    /// <param name="Written">Files written.</param>
    /// <param name="Skipped">One line per entry left out, with the reason.</param>
    public sealed record ExtractResult(int Written, List<string> Skipped);

    /// <summary>Null when <paramref name="entries"/> fit the entry-count and total-size limits, else
    /// the reason they do not. Sizes are the declared ones; <see cref="ExtractToFile"/> holds each
    /// entry to its declared size.</summary>
    public static string? CheckLimits(IEnumerable<ZipArchiveEntry> entries)
    {
        long total = 0;
        var count = 0;
        foreach (var entry in entries)
        {
            if (++count > MaxEntries)
                return $"the archive holds more than {MaxEntries:N0} entries";
            total += Math.Max(0, entry.Length);
            if (total > MaxTotalBytes)
                return $"the archive unpacks to more than {MaxTotalBytes / (1024 * 1024 * 1024)} GB";
        }
        return null;
    }

    /// <summary>Null when the entry's declared sizes look like real content, else the reason not.</summary>
    public static string? CheckEntry(ZipArchiveEntry entry)
    {
        if (entry.Length < 0 || entry.Length > MaxTotalBytes)
            return "its declared size is not believable";
        if (entry.Length > RatioCheckFromBytes && entry.Length / Math.Max(1, entry.CompressedLength) > MaxRatio)
            return "its compression ratio is not believable";
        return null;
    }

    /// <summary>Writes one entry to <paramref name="destination"/>, never more bytes than the entry
    /// declares. With <paramref name="overwrite"/> the file is written beside the destination and
    /// swapped in, so a failed copy leaves the old file alone and a hard-linked file is replaced
    /// rather than written through.</summary>
    /// <exception cref="InvalidDataException">The entry inflated past its declared size, or is
    /// corrupt.</exception>
    public static void ExtractToFile(ZipArchiveEntry entry, string destination, bool overwrite)
    {
        var target = overwrite ? destination + "." + Guid.NewGuid().ToString("N")[..8] + ".part" : destination;
        // CreateNew throws when something is already there, so only a file this call made is removed.
        var created = false;
        try
        {
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var input = entry.Open();
                CopyCapped(input, output, entry.Length);
            }
            try { File.SetLastWriteTime(target, entry.LastWriteTime.DateTime); }
            catch { /* a timestamp the file system refuses is not worth failing over */ }
            if (overwrite) File.Move(target, destination, overwrite: true);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(target); } catch { /* best effort */ }
            }
            throw;
        }
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/> and throws once
    /// more than <paramref name="maxBytes"/> bytes have come out.</summary>
    public static long CopyCapped(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException("the entry is larger than the archive says it is");
            destination.Write(buffer, 0, read);
        }
        return total;
    }

    /// <summary>Unpacks the entries of <paramref name="archive"/> under
    /// <paramref name="destRoot"/>. An entry whose path would leave the folder, or that looks like
    /// a zip bomb, is skipped and listed in the result instead of failing the whole archive.</summary>
    /// <param name="relativeName">Maps an entry to its path under the root, or null to skip it
    /// without listing it. Defaults to the entry's own name.</param>
    /// <exception cref="InvalidDataException">The archive as a whole is past the limits. Nothing
    /// was written.</exception>
    public static ExtractResult ExtractToDirectory(
        ZipArchive archive, string destRoot, bool overwrite,
        Func<ZipArchiveEntry, string?>? relativeName = null, CancellationToken ct = default)
    {
        var plan = new List<(ZipArchiveEntry Entry, string Relative)>();
        foreach (var entry in archive.Entries)
        {
            var rel = relativeName is null ? entry.FullName : relativeName(entry);
            if (rel is not null) plan.Add((entry, rel));
            if (plan.Count > MaxEntries) break;
        }
        if (CheckLimits(plan.Select(p => p.Entry)) is { } why)
            throw new InvalidDataException($"Refused to unpack the archive: {why}.");

        var skipped = new List<string>();
        var written = 0;
        Directory.CreateDirectory(destRoot);
        foreach (var (entry, raw) in plan)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Normalize(raw);
            var isFolder = rel.EndsWith('/');
            var target = PathSafety.ResolveInside(destRoot, isFolder ? rel.TrimEnd('/') : rel);
            if (target is null)
            {
                if (rel.Trim('/').Length > 0) skipped.Add($"{entry.FullName} (unsafe path)");
                continue;
            }
            if (isFolder) { Directory.CreateDirectory(target); continue; }
            if (CheckEntry(entry) is { } bad) { skipped.Add($"{entry.FullName} ({bad})"); continue; }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                ExtractToFile(entry, target, overwrite);
                written++;
            }
            catch (InvalidDataException ex) { skipped.Add($"{entry.FullName} ({ex.Message})"); }
        }
        return new ExtractResult(written, skipped);
    }

    /// <summary>Forward slashes, and no leading "./" that some zip tools write.</summary>
    private static string Normalize(string name)
    {
        var s = name.Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        return s;
    }
}
