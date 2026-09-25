using System.Reflection;

namespace CloudLauncher.Services;

/// <summary>Exposes this launcher build's version and compares it against remote releases.</summary>
public static class AppVersion
{
    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>Human-friendly version string, e.g. "1.0.0".</summary>
    public static string CurrentString =>
        $"{Current.Major}.{Current.Minor}.{Math.Max(Current.Build, 0)}";

    /// <summary>True when <paramref name="remoteVersion"/> is a valid, strictly newer version.</summary>
    public static bool IsNewer(string? remoteVersion)
        => Version.TryParse(remoteVersion, out var remote) && Rank(remote) > Rank(Current);

    /// <summary>Where a version sits in release order, for comparing and sorting.</summary>
    /// <remarks>Numbering restarted at 0.8.4 (the release after 1.8.3), so a 1.x version ranks as its
    /// 0.x twin. All of 1.x maps down, not only up to 1.8.3, because the server announces newer releases
    /// to old-line launchers as 1.x (LauncherController), and a 0.8.4 launcher told "1.8.4" must see its
    /// own version rather than an update. The next major version after 0.x is 2.0.</remarks>
    public static Version Rank(Version v)
    {
        var n = Normalize(v);
        return n.Major == 1 ? new Version(0, n.Minor, n.Build) : n;
    }

    // Ignore the (often unspecified) revision component so 1.0.0 and 1.0.0.0 compare equal.
    private static Version Normalize(Version v)
        => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
