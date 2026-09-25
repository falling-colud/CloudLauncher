namespace CloudLauncher.Services;

public sealed record ModDownloadItem(
    ModSummary Mod,
    ModVersion Version,
    ModVersionFile File,
    bool IsDependency);

public static class ModDependencyResolver
{
    /// <summary>Everything one version needs downloaded: itself plus every required dependency,
    /// transitively, dependencies first.</summary>
    /// <param name="versionCatalog">The shared version catalog. With it, a dependency's version list is
    /// fetched filtered to <paramref name="minecraftVersion"/> and <paramref name="loader"/> and shared
    /// with other callers; without it a CurseForge dependency costs its whole file history (dozens of
    /// sequential proxy calls for a mod like JEI).</param>
    public static async Task<List<ModDownloadItem>> ResolveRequiredDownloadsAsync(
        ModSummary rootMod,
        ModVersion rootVersion,
        string minecraftVersion,
        string loader,
        ModrinthService modrinth,
        CurseForgeService curseForge,
        string? channel = null,
        ModVersionCatalog? versionCatalog = null,
        CancellationToken ct = default)
    {
        var result = new List<ModDownloadItem>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await VisitAsync(rootMod, rootVersion, isDependency: false, result, visited, minecraftVersion, loader, modrinth, curseForge, channel, versionCatalog, ct);
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
        string? channel,
        ModVersionCatalog? versionCatalog,
        CancellationToken ct)
    {
        if (!visited.Add($"{version.Source}:{version.Id}")) return;

        foreach (var dependency in version.Dependencies.Where(IsRequiredDependency))
        {
            var resolved = await ResolveDependencyAsync(dependency, minecraftVersion, loader, modrinth, curseForge, channel, versionCatalog, ct);
            if (resolved is not null)
                await VisitAsync(resolved.Value.Mod, resolved.Value.Version, isDependency: true, result, visited, minecraftVersion, loader, modrinth, curseForge, channel, versionCatalog, ct);
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
        string? channel,
        ModVersionCatalog? versionCatalog,
        CancellationToken ct)
    {
        return dependency.Source switch
        {
            ModSource.CurseForge => await ResolveCurseForgeDependencyAsync(dependency, minecraftVersion, loader, curseForge, channel, versionCatalog, ct),
            ModSource.Modrinth => await ResolveModrinthDependencyAsync(dependency, minecraftVersion, loader, modrinth, channel, ct),
            _ => null
        };
    }

    private static async Task<(ModSummary Mod, ModVersion Version)?> ResolveModrinthDependencyAsync(
        ModDependency dependency,
        string minecraftVersion,
        string loader,
        ModrinthService modrinth,
        string? channel,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dependency.VersionId))
        {
            var pinned = await modrinth.GetVersionWithProjectAsync(dependency.VersionId, ct);
            if (pinned is not null) return pinned;
            // The pinned version couldn't be fetched (deleted, region-locked, transient error): resolve it by
            // project below rather than drop a required dependency.
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

        var version = PickCompatibleVersion(versions, minecraftVersion, loader, channel);
        return version is null ? null : (mod, version);
    }

    private static async Task<(ModSummary Mod, ModVersion Version)?> ResolveCurseForgeDependencyAsync(
        ModDependency dependency,
        string minecraftVersion,
        string loader,
        CurseForgeService curseForge,
        string? channel,
        ModVersionCatalog? versionCatalog,
        CancellationToken ct)
    {
        if (!int.TryParse(dependency.ProjectId, out var modId)) return null;

        var mod = await curseForge.GetModAsync(modId, ct);
        if (mod is null) return null;

        // Ask filtered first and unfiltered only if nothing came back, like the Modrinth path above, so
        // a dependency listing a different Minecraft version still resolves. The unfiltered history of a
        // long-lived mod takes many sequential proxy calls.
        var mc = string.IsNullOrWhiteSpace(minecraftVersion) ? null : minecraftVersion;
        var loaderTag = string.IsNullOrWhiteSpace(loader) ? null : loader;
        var versions = versionCatalog is not null
            ? await versionCatalog.GetVersionsAsync(mod, mc, loaderTag, ct: ct)
            : await curseForge.GetVersionsAsync(modId, mc, loaderTag, maxPages: 0, ct);
        if (versions.Count == 0)
            versions = versionCatalog is not null
                ? await versionCatalog.GetVersionsAsync(mod, ct: ct)
                : await curseForge.GetVersionsAsync(modId, ct);

        var version = PickCompatibleVersion(versions, minecraftVersion, loader, channel);
        return version is null ? null : (mod, version);
    }

    private static ModVersion? PickCompatibleVersion(
        IReadOnlyList<ModVersion> versions, string minecraftVersion, string loader, string? channel)
    {
        var compatible = versions.Where(v => MatchesFilters(v, minecraftVersion, loader));
        var pick = ModUpdateChannel.PickNewest(compatible, channel, v => v.ReleaseChannel, v => v.DatePublished);
        if (pick is not null) return pick;

        // Nothing matched the MC version and loader (e.g. the dependency lists "1.20", the pack is
        // "1.20.1"). Fall back to the newest version rather than skip a required dependency.
        return ModUpdateChannel.PickNewest(versions, channel, v => v.ReleaseChannel, v => v.DatePublished);
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
