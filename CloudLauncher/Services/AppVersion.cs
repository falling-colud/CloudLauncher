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
        => Version.TryParse(remoteVersion, out var remote) && Normalize(remote) > Normalize(Current);

    // Ignore the (often unspecified) revision component so 1.0.0 and 1.0.0.0 compare equal.
    private static Version Normalize(Version v)
        => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
