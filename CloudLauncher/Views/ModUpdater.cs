using System.IO;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Shared mod-update helpers: fetching every published version (all channels) and
/// installing a chosen version into the pack, replacing the current jar.</summary>
public static class ModUpdater
{
    /// <summary>All published versions of a mod, every release channel, unfiltered by MC/loader
    /// (the picker filters client-side so it can also show "all").</summary>
    public static async Task<List<ModVersion>> FetchVersionsAsync(ModSummary mod)
    {
        try
        {
            if (mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var cfId))
                return await App.State.CurseForge.GetVersionsAsync(cfId);
            return await App.State.Modrinth.GetVersionsAsync(mod.Id);
        }
        catch { return new List<ModVersion>(); }
    }

    /// <summary>Downloads <paramref name="version"/> into the mod's folder and removes the old jar.
    /// Returns false if there's nothing downloadable.</summary>
    public static async Task<bool> InstallVersionAsync(PackMod mod, ModVersion version)
    {
        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (file is null) return false;

        file = await EnsureDownloadableAsync(version, file);
        if (string.IsNullOrWhiteSpace(file.DownloadUrl)) return false;

        // Preserve the mod's enabled/disabled state: a disabled mod lives as "<name>.jar.disabled".
        // Writing the update as a plain ".jar" (and deleting the old ".disabled") silently
        // re-enabled it — nasty during a bulk "Update all".
        // Path.GetFileName strips any directory components a hostile store response might smuggle
        // into the filename (defense-in-depth so it can't escape the mods folder).
        var safeName = Path.GetFileName(file.Filename);
        var fileName = mod.Enabled ? safeName : safeName + ".disabled";
        var dest = Path.Combine(Path.GetDirectoryName(mod.FilePath)!, fileName);
        await App.State.Modrinth.DownloadFileAsync(file.DownloadUrl, dest);
        if (!string.Equals(mod.FilePath, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(mod.FilePath))
            File.Delete(mod.FilePath);
        return true;
    }

    private static async Task<ModVersionFile> EnsureDownloadableAsync(ModVersion version, ModVersionFile file)
    {
        if (version.Source != ModSource.CurseForge || !string.IsNullOrWhiteSpace(file.DownloadUrl)) return file;
        var ids = version.Id.Split(':');
        if (ids.Length != 2 || !int.TryParse(ids[0], out var modId) || !int.TryParse(ids[1], out var fileId)) return file;
        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }
}
