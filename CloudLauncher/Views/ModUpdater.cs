using System.IO;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Shared mod-update helpers: fetching every published version (all channels) and
/// installing a chosen version into the pack, replacing the current jar.</summary>
public static class ModUpdater
{
    /// <summary>All published versions of a mod, every release channel, unfiltered by MC/loader
    /// (the picker filters client-side so it can also show "all"). Served from the session catalog.</summary>
    public static async Task<List<ModVersion>> FetchVersionsAsync(ModSummary mod, bool forceRefresh = false)
    {
        try { return (await App.State.ModVersions.GetVersionsAsync(mod, forceRefresh: forceRefresh)).ToList(); }
        catch { return new List<ModVersion>(); }
    }

    /// <summary>The newest version <paramref name="mod"/> should move to, or null when it is current.
    /// One rule for every surface: compatible with the pack's Minecraft version and loader, published
    /// after the installed file, and on a channel the mod's effective update channel admits (release
    /// only by default; beta adds betas; alpha adds everything). Lists come from the shared catalog, so
    /// the Mods tab, Modpack Management and the update dialog all see the same answer.</summary>
    public static async Task<ModVersion?> FindUpdateAsync(PackMod mod, string? mcVersion, string? loader,
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (mod.PrimaryMod is null || mod.PrimaryVersion is null) return null;
        try
        {
            var versions = await App.State.ModVersions.GetLatestVersionsAsync(mod.PrimaryMod, mcVersion, loader, forceRefresh, ct);
            var installed = mod.PrimaryVersion;
            var channel = mod.EffectiveUpdateChannel;
            return versions
                .Where(v => v.Id != installed.Id
                            && IsCompatible(v, mcVersion, loader)
                            && v.DatePublished > installed.DatePublished
                            && ModUpdateChannel.Admits(channel, v.ReleaseChannel))
                .OrderByDescending(v => v.DatePublished)
                .FirstOrDefault();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static bool IsCompatible(ModVersion v, string? mc, string? loader) => ModVersionCatalog.IsCompatible(v, mc, loader);

    /// <summary>The loader tag the stores use for a pack ("neoforge", "fabric", …) or null for vanilla.</summary>
    public static string? LoaderTag(CloudLauncher.Shared.PackDetail pack) =>
        pack.Loader == CloudLauncher.Shared.LoaderKind.None ? null : pack.Loader.ToString().ToLowerInvariant();

    /// <summary>Downloads <paramref name="version"/> into the mod's folder and removes the old jar.
    /// Returns false if there's nothing downloadable. <paramref name="progress"/> reports the
    /// download's bytes so a bulk update can show a bar per mod.</summary>
    public static async Task<bool> InstallVersionAsync(PackMod mod, ModVersion version,
        IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (file is null) return false;

        file = await EnsureDownloadableAsync(version, file, ct);
        if (string.IsNullOrWhiteSpace(file.DownloadUrl)) return false;

        // Preserve the mod's enabled/disabled state: a disabled mod lives as "<name>.jar.disabled".
        // Writing the update as a plain ".jar" (and deleting the old ".disabled") silently
        // re-enabled it — nasty during a bulk "Update all".
        // Path.GetFileName strips any directory components a hostile store response might smuggle
        // into the filename (defense-in-depth so it can't escape the mods folder).
        var safeName = Path.GetFileName(file.Filename);
        var fileName = mod.Enabled ? safeName : safeName + ".disabled";
        var dest = Path.Combine(Path.GetDirectoryName(mod.FilePath)!, fileName);
        await App.State.Modrinth.DownloadFileAsync(file.DownloadUrl, dest, progress, ct);
        if (!string.Equals(mod.FilePath, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(mod.FilePath))
            File.Delete(mod.FilePath);
        return true;
    }

    private static async Task<ModVersionFile> EnsureDownloadableAsync(ModVersion version, ModVersionFile file,
        CancellationToken ct = default)
    {
        if (version.Source != ModSource.CurseForge || !string.IsNullOrWhiteSpace(file.DownloadUrl)) return file;
        var ids = version.Id.Split(':');
        if (ids.Length != 2 || !int.TryParse(ids[0], out var modId) || !int.TryParse(ids[1], out var fileId)) return file;
        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId, ct);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }
}
