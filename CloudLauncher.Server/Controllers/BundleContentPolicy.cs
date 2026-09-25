using System.IO.Compression;
using CloudLauncher.Shared;

namespace CloudLauncher.Server.Controllers;

/// <summary>The rules for a content bundle's paths: the folder it unpacks into and the names of
/// the entries in its zip.</summary>
/// <remarks>
/// <para>The client unpacks bundles into folders of a live instance, so these rules are what stops
/// a hostile upload writing arbitrary files on subscribers' machines. Kept free of ASP.NET, EF and
/// storage so they are easy to test.</para>
/// <para>Written for a Windows client and a Linux server, so nothing relies on the OS's idea of
/// "rooted": <c>Path.IsPathRooted("C:\\x")</c> and <c>Path.IsPathRooted("\\\\host\\share")</c>
/// are both false on Linux. Backslashes, colons, leading slashes, device names and trailing dots
/// are checked explicitly. <see cref="SyncController"/> does the same for manifest paths.</para>
/// </remarks>
internal static class BundleContentPolicy
{
    /// <summary>Longest a <c>TargetPathRoot</c> may be: the column's width.</summary>
    public const int MaxTargetPathLength = 128;

    /// <summary>How deep a <c>TargetPathRoot</c> may go, counting the allow-listed first segment.</summary>
    /// <remarks>A config bundle that only wants <c>config/jei</c> is reasonable; one that wants to
    /// describe a whole tree in the root is describing the zip's own layout instead.</remarks>
    public const int MaxTargetPathSegments = 4;

    /// <summary>Longest single path segment. Windows' own limit is 255.</summary>
    public const int MaxSegmentLength = 255;

    /// <summary>Longest a zip entry's path may be.</summary>
    public const int MaxEntryPathLength = 1024;

    /// <summary>How many entries a bundle zip may contain.</summary>
    public const int MaxZipEntries = 20_000;

    /// <summary>Largest a single entry may be once unpacked.</summary>
    public const long MaxEntryUncompressedBytes = 256L * 1024 * 1024;

    /// <summary>Largest a whole bundle may be once unpacked.</summary>
    /// <remarks>This is the zip-bomb defence rather than a compression-ratio rule. Uploads are capped
    /// at 64 MB, so the risk is expanding past the subscriber's disk; a ratio rule would reject KubeJS
    /// bundles, which are thousands of small text files that compress very well.</remarks>
    public const long MaxTotalUncompressedBytes = 1024L * 1024 * 1024;

    /// <summary>The instance-relative folders each kind is allowed to unpack into.</summary>
    /// <remarks>
    /// <para>An allow-list per kind, because the client joins <c>TargetPathRoot</c> onto the instance
    /// directory and extracts there as is. A shader pack only ever belongs in one place.</para>
    /// <para><see cref="BundleKind.Other"/> is the only kind allowed the empty root (the instance
    /// directory itself). It still can't reach <c>mods</c>, since a jar there means code execution and
    /// belongs to the mod flow.</para>
    /// </remarks>
    public static IReadOnlyList<string> AllowedRootsFor(BundleKind kind) => kind switch
    {
        BundleKind.ShaderPack => ["shaderpacks"],
        BundleKind.ConfigBundle => ["config", "defaultconfigs"],
        BundleKind.KubeJsBundle => ["kubejs"],
        BundleKind.DataPack => ["datapacks"],
        BundleKind.Other =>
        [
            "",              // the instance directory itself
            "shaderpacks", "config", "defaultconfigs", "kubejs", "datapacks",
            "resourcepacks", "scripts", "schematics", "saves"
        ],
        _ => []              // an enum value from a newer client: allow nothing rather than guess
    };

    /// <summary>The root a bundle of this kind gets when the caller does not name one.</summary>
    public static string DefaultRootFor(BundleKind kind) => BundleTargets.DefaultFor(kind);

    /// <summary>Whether <paramref name="kind"/> is a value this server knows.</summary>
    /// <remarks>Guards against a client posting <c>kind=7</c>: the enum would bind happily, the row
    /// would store 7, and <see cref="AllowedRootsFor"/> would then allow nothing forever.</remarks>
    public static bool IsKnownKind(BundleKind kind) => kind is
        BundleKind.ShaderPack or BundleKind.ConfigBundle or BundleKind.KubeJsBundle
        or BundleKind.DataPack or BundleKind.Other;

    /// <summary>
    /// Checks and canonicalises the folder a bundle claims to unpack into.
    /// </summary>
    /// <param name="kind">The bundle's kind, which chooses the allow-list.</param>
    /// <param name="raw">What the caller sent. Null or blank asks for the kind's default.</param>
    /// <param name="normalized">The value to store: lowercase, '/'-separated, no trailing slash.</param>
    /// <param name="error">The sentence to show the caller, when this returns false.</param>
    /// <returns>True when <paramref name="normalized"/> is safe to store and hand to a client.</returns>
    public static bool TryNormalizeTargetPathRoot(
        BundleKind kind, string? raw, out string normalized, out string? error)
    {
        normalized = "";
        error = null;

        if (!IsKnownKind(kind))
        {
            error = $"Unknown bundle kind '{(int)kind}'.";
            return false;
        }

        var allowed = AllowedRootsFor(kind);

        // Blank means the kind's default, never the instance root. Only an Other bundle can ask for
        // the instance root, by sending "".
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = DefaultRootFor(kind);
            return true;
        }

        var value = raw.Trim();
        if (value.Length > MaxTargetPathLength)
        {
            error = $"Target folder must be {MaxTargetPathLength} characters or fewer.";
            return false;
        }

        // Accept one trailing slash ("config/"). "config//" has an empty segment and is rejected
        // below.
        if (value.EndsWith('/')) value = value[..^1];
        if (value.Length == 0)
        {
            // The caller sent "/": rooted, not empty.
            error = "Target folder cannot start with '/'.";
            return false;
        }

        var lowered = value.ToLowerInvariant();
        if (!IsSafeRelativePath(lowered, MaxTargetPathLength, out var pathError))
        {
            error = $"'{raw}' is not a usable target folder - {pathError}.";
            return false;
        }

        var segments = lowered.Split('/');
        if (segments.Length > MaxTargetPathSegments)
        {
            error = $"Target folder may be at most {MaxTargetPathSegments} folders deep.";
            return false;
        }

        if (!allowed.Contains(segments[0], StringComparer.Ordinal))
        {
            var list = string.Join(", ", allowed.Where(a => a.Length > 0));
            error = allowed.Contains("", StringComparer.Ordinal)
                ? $"'{segments[0]}' is not a folder a bundle may unpack into. Allowed: the instance root, {list}."
                : $"A {kind} bundle unpacks into {list}, not '{segments[0]}'.";
            return false;
        }

        normalized = lowered;
        return true;
    }

    /// <summary>Checks one path from inside a bundle zip.</summary>
    /// <returns>The sentence to show the caller, or null when the entry is safe to extract.</returns>
    public static string? ValidateZipEntryPath(string? entryFullName)
    {
        if (string.IsNullOrWhiteSpace(entryFullName))
            return "The archive contains an entry with no name.";

        // A directory entry looks like "config/"; the trailing slash is not a path segment.
        var name = entryFullName.EndsWith('/') ? entryFullName[..^1] : entryFullName;
        if (name.Length == 0)
            return $"The archive contains an entry at the archive root: '{entryFullName}'.";

        return IsSafeRelativePath(name, MaxEntryPathLength, out var error)
            ? null
            : $"The archive contains an unsafe path: '{entryFullName}' - {error}";
    }

    /// <summary>Whether <paramref name="path"/> is a relative path that cannot escape the folder it is
    /// joined to, on any operating system.</summary>
    /// <remarks>Each rule blocks a real escape on a Windows client that a Linux server wouldn't notice:
    /// <c>\</c> is a separator and <c>\\host\share</c> a UNC root; <c>:</c> is a drive letter or an
    /// NTFS alternate data stream; Win32 trims a trailing <c>.</c> or space, so <c>config.</c> is
    /// <c>config</c>; <c>CON</c>, <c>NUL</c> and the <c>COM</c>/<c>LPT</c> devices are not files. Plus
    /// the usual <c>..</c> check.</remarks>
    public static bool IsSafeRelativePath(string? path, int maxLength, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) { error = "it is empty"; return false; }
        if (path.Length > maxLength) { error = $"it is longer than {maxLength} characters"; return false; }

        foreach (var c in path)
        {
            if (char.IsControl(c)) { error = "it contains a control character"; return false; }
            if (c is '\\') { error = "it contains a backslash"; return false; }
            if (c is ':') { error = "it contains a colon (drive letter or data stream)"; return false; }
            if (c is '*' or '?' or '"' or '<' or '>' or '|') { error = $"it contains '{c}'"; return false; }
        }

        if (path[0] == '/') { error = "it starts with '/'"; return false; }
        if (path[0] == '~') { error = "it starts with '~'"; return false; }
        // Belt and braces: on Windows this also catches "C:x" and "\\?\" forms, and on Linux a
        // leading '/'. Never the only check.
        if (Path.IsPathRooted(path)) { error = "it is an absolute path"; return false; }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) { error = "it has an empty folder name"; return false; }
            if (segment.Length > MaxSegmentLength) { error = "a folder name is too long"; return false; }
            if (segment is "." or "..") { error = "it walks up out of the folder"; return false; }
            if (segment[^1] is '.' or ' ') { error = $"'{segment}' ends with a dot or a space"; return false; }
            if (segment[0] == ' ') { error = $"'{segment}' starts with a space"; return false; }

            // Device names are reserved with or without an extension: "nul.txt" is still NUL.
            var stem = segment.Split('.')[0];
            if (ReservedDeviceNames.Contains(stem)) { error = $"'{segment}' is a reserved device name"; return false; }
        }

        // Final independent check: resolve against a fake root and confirm the result is still under
        // it, so removing one of the rules above can't open an escape.
        const string probeRoot = "/__bundle_root__";
        string resolved;
        try { resolved = Path.GetFullPath(Path.Combine(probeRoot, path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "it is not a usable path";
            return false;
        }
        // The separator has to be part of the prefix, or "/root" would happily "contain" "/rootless".
        var expectedPrefix = Path.GetFullPath(probeRoot).TrimEnd(Path.DirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || resolved.Length <= expectedPrefix.Length)
        {
            error = "it resolves outside the target folder";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a bundle zip's central directory and rejects it if anything about its shape or its
    /// entry names would be unsafe to extract.
    /// </summary>
    /// <param name="seekableZip">The stored blob, opened for reading. Left open.</param>
    /// <returns>The sentence to show the caller, or null when the archive is safe.</returns>
    public static string? ValidateZip(Stream seekableZip)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(seekableZip, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return "That file is not a readable .zip archive.";
        }

        using (archive)
        {
            var count = 0;
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (++count > MaxZipEntries)
                    return $"That archive has more than {MaxZipEntries:N0} entries.";

                if (ValidateZipEntryPath(entry.FullName) is { } pathError)
                    return pathError;

                if (entry.Length > MaxEntryUncompressedBytes)
                    return $"'{entry.FullName}' unpacks to {entry.Length / (1024 * 1024)} MB, "
                         + $"more than the {MaxEntryUncompressedBytes / (1024 * 1024)} MB limit for one file.";

                total += entry.Length;
                if (total > MaxTotalUncompressedBytes)
                    return $"That archive unpacks to more than {MaxTotalUncompressedBytes / (1024 * 1024)} MB.";
            }

            if (count == 0)
                return "That archive is empty.";
        }

        return null;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };
}
