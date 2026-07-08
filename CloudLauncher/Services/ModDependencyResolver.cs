namespace CloudLauncher.Services;

public sealed record ModDownloadItem(
    ModSummary Mod,
    ModVersion Version,
    ModVersionFile File,
    bool IsDependency);

public static class ModDependencyResolver
{
    public static async Task<List<ModDownloadItem>> ResolveRequiredDownloadsAsync(
        ModSummary rootMod,
        ModVersion rootVersion,
        string minecraftVersion,
        string loader,
        ModrinthService modrinth,
        CurseForgeService curseForge,
        CancellationToken ct = default)
    {
        var result = new List<ModDownloadItem>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await VisitAsync(rootMod, rootVersion, isDependency: false, result, visited, minecraftVersion, loader, modrinth, curseForge, ct);
        return result;
    }

    private static async Task VisitAsync(
        ModSummary mod,
        ModVersion version,
        bool isDependency,
        List<ModDownloadItem> result,
        HashSet<string> visited,
        string minecraftVersion,
        string loader,
        ModrinthService modrinth,
        CurseForgeService curseForge,
        CancellationToken ct)
    {
        if (!visited.Add($"{version.Source}:{version.Id}")) return;

        foreach (var dependency in version.Dependencies.Where(IsRequiredDependency))
        {
            var resolved = await ResolveDependencyAsync(dependency, minecraftVersion, loader, modrinth, curseForge, ct);
            if (resolved is not null)
                await VisitAsync(resolved.Value.Mod, resolved.Value.Version, isDependency: true, result, visited, minecraftVersion, loader, modrinth, curseForge, ct);
        }

        var file = await ResolveDownloadFileAsync(mod, version, curseForge, ct);
        if (file is not null && !string.IsNullOrWhiteSpace(file.DownloadUrl))
            result.Add(new ModDownloadItem(mod, version, file, isDependency));
    }

    private static bool IsRequiredDependency(ModDependency dependency) =>
        string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dependency.DependencyType, "required_dependency", StringComparison.OrdinalIgnoreCase);

    private static async Task<(ModSummary Mod, ModVersion Version)?> ResolveDependencyAsync(
        ModDependency dependency,
        string minecraftVersion,
        string loader,
        ModrinthService modrinth,
        CurseForgeService curseForge,
        CancellationToken ct)
    {
        return dependency.Source switch
        {
            ModSource.CurseForge => await ResolveCurseForgeDependencyAsync(dependency, minecraftVersion, loader, curseForge, ct),
            ModSource.Modrinth => await ResolveModrinthDependencyAsync(dependency, minecraftVersion, loader, modrinth, ct),
            _ => null
        };
    }

    private static async Task<(ModSummary Mod, ModVersion Version)?> ResolveModrinthDependencyAsync(
        ModDependency dependency,
        string minecraftVersion,
        string loader,
        ModrinthService modrinth,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dependency.VersionId))
        {
            var pinned = await modrinth.GetVersionWithProjectAsync(dependency.VersionId, ct);
            if (pinned is not null) return pinned;
            // The pinned version couldn't be fetched (deleted/region-locked/transient) — rather than
            // silently dropping a required dependency, fall back to resolving it by project below.
        }

        if (string.IsNullOrWhiteSpace(dependency.ProjectId)) return null;

        var mod = await modrinth.GetProjectAsync(dependency.ProjectId, ct);
        if (mod is null) return null;

        var versions = await modrinth.GetVersionsAsync(
            dependency.ProjectId,
            string.IsNullOrWhiteSpace(minecraftVersion) ? null : minecraftVersion,
            string.IsNullOrWhiteSpace(loader) ? null : loader,
            ct);
        if (versions.Count == 0)
            versions = await modrinth.GetVersionsAsync(dependency.ProjectId, ct: ct);

        var version = PickCompatibleVersion(versions, minecraftVersion, loader);
        return version is null ? null : (mod, version);
    }

    private static async Task<(ModSummary Mod, ModVersion Version)?> ResolveCurseForgeDependencyAsync(
        ModDependency dependency,
        string minecraftVersion,
        string loader,
        CurseForgeService curseForge,
        CancellationToken ct)
    {
        if (!int.TryParse(dependency.ProjectId, out var modId)) return null;

        var mod = await curseForge.GetModAsync(modId, ct);
        if (mod is null) return null;

        var versions = await curseForge.GetVersionsAsync(modId, ct);
        var version = PickCompatibleVersion(versions, minecraftVersion, loader);
        return version is null ? null : (mod, version);
    }

    private static ModVersion? PickCompatibleVersion(IReadOnlyList<ModVersion> versions, string minecraftVersion, string loader)
    {
        var compatible = versions.Where(v => MatchesFilters(v, minecraftVersion, loader)).ToList();
        var pick = compatible.FirstOrDefault(v => string.Equals(v.ReleaseChannel, "release", StringComparison.OrdinalIgnoreCase))
            ?? compatible.FirstOrDefault();
        if (pick is not null) return pick;

        // Nothing matched the exact MC version + loader (e.g. the dependency lists "1.20" while the
        // pack is "1.20.1"). Rather than silently skip a *required* dependency, fall back to the
        // newest available version so it still installs.
        return versions.FirstOrDefault(v => string.Equals(v.ReleaseChannel, "release", StringComparison.OrdinalIgnoreCase))
            ?? versions.FirstOrDefault();
    }

    private static bool MatchesFilters(ModVersion version, string minecraftVersion, string loader) =>
        (string.IsNullOrWhiteSpace(minecraftVersion) || version.GameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(loader) || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    private static async Task<ModVersionFile?> ResolveDownloadFileAsync(
        ModSummary mod,
        ModVersion version,
        CurseForgeService curseForge,
        CancellationToken ct)
    {
        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (file is null || !string.IsNullOrWhiteSpace(file.DownloadUrl)) return file;

        if (version.Source != ModSource.CurseForge) return file;

        var ids = version.Id.Split(':', 2);
        var modId = ids.Length == 2 && int.TryParse(ids[0], out var parsedModId)
            ? parsedModId
            : int.TryParse(mod.Id, out var fallbackModId) ? fallbackModId : 0;
        var fileId = ids.Length == 2 && int.TryParse(ids[1], out var parsedFileId) ? parsedFileId : 0;
        if (modId == 0 || fileId == 0) return file;

        var url = await curseForge.GetDownloadUrlAsync(modId, fileId, ct);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }
}
