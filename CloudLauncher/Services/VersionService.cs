using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed record MinecraftReleaseInfo(string Id, string Type, DateTimeOffset ReleaseTime);

public sealed class VersionService
{
    private static readonly HttpClient Http = ApiClient.WithUserAgent(new() { Timeout = TimeSpan.FromSeconds(30) });

    // These manifests change at most daily, but the create dialogs call them on every open.
    // Cache per-endpoint with a short TTL so routine UI actions don't re-download hundreds of
    // KB each time, and gate concurrent first-fetches so two dialogs opening at once fetch once.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private sealed record CacheEntry(DateTimeOffset Expires, object Value);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> Cache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private static async Task<T> CachedAsync<T>(string key, Func<Task<T>> fetch)
    {
        if (Cache.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
            return (T)hit.Value;

        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (Cache.TryGetValue(key, out var fresh) && fresh.Expires > DateTimeOffset.UtcNow)
                return (T)fresh.Value;
            var value = await fetch();
            Cache[key] = new CacheEntry(DateTimeOffset.UtcNow.Add(CacheTtl), value!);
            return value;
        }
        finally { gate.Release(); }
    }

    // ---------- Vanilla ----------

    public Task<List<MinecraftReleaseInfo>> ListMinecraftVersionsAsync(
        bool releasesOnly = true, CancellationToken ct = default) =>
        CachedAsync($"mc:{releasesOnly}", async () =>
        {
            var resp = await Http.GetFromJsonAsync<MojangVersionManifest>(
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
            if (resp?.Versions is null) return new();
            return resp.Versions
                .Where(v => !releasesOnly || v.Type == "release")
                .Select(v => new MinecraftReleaseInfo(v.Id, v.Type, v.ReleaseTime))
                .ToList();
        });

    // ---------- Fabric ----------

    public Task<List<string>> ListFabricLoaderVersionsAsync(string mcVersion, CancellationToken ct = default) =>
        CachedAsync($"fabric:loader:{mcVersion}", async () =>
        {
            var versions = await Http.GetFromJsonAsync<List<FabricLoaderEntry>>(
                $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}", ct);
            return versions?.Select(v => v.Loader.Version).Distinct().ToList() ?? new();
        });

    public Task<List<string>> ListFabricSupportedMinecraftAsync(CancellationToken ct = default) =>
        CachedAsync("fabric:game", async () =>
        {
            var resp = await Http.GetFromJsonAsync<List<FabricGameEntry>>(
                "https://meta.fabricmc.net/v2/versions/game", ct);
            return resp?.Where(g => g.Stable).Select(g => g.Version).ToList() ?? new();
        });

    // ---------- NeoForge ----------

    /// <summary>Every NeoForge build, newest first.</summary>
    /// <remarks>
    /// <para>NeoForge's own Maven index is asked first. On 2026-09-27 it (and its maven-metadata.xml)
    /// suddenly listed only the two newest betas, while every older installer was still downloadable,
    /// which left the create dialog with nothing for any Minecraft version. So a list shorter than
    /// <see cref="PlausibleNeoForgeCount"/> is topped up from Prism Launcher's public metadata index,
    /// and the last good list is kept on disk as the final fallback.</para>
    /// <para>Installing never depends on this list: an instance's NeoForge version is fetched straight
    /// from its installer URL.</para>
    /// </remarks>
    public Task<List<string>> ListNeoForgeVersionsAsync(CancellationToken ct = default) =>
        CachedAsync("neoforge", async () =>
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                // Versions look like "21.1.86" (Minecraft 1.21.1) or, since Minecraft moved to year
                // numbers, "26.1.2.111" (Minecraft 26.1.2); see NeoForgeVersionForMinecraft.
                var resp = await Http.GetFromJsonAsync<MavenVersionList>(
                    "https://maven.neoforged.net/api/maven/versions/releases/net%2Fneoforged%2Fneoforge", ct);
                foreach (var v in resp?.Versions ?? []) found.Add(v);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLog.Log("versions", "NeoForge's version list could not be read: " + ex.Message);
            }

            if (found.Count < PlausibleNeoForgeCount)
            {
                try
                {
                    var prism = await Http.GetFromJsonAsync<PrismIndex>(
                        "https://meta.prismlauncher.org/v1/net.neoforged/index.json", ct);
                    // Prism's index also carries NeoForge's first 1.20.1 builds ("47.1.106"), which were
                    // published as a different artifact; the launcher has never offered those.
                    foreach (var v in prism?.Versions ?? [])
                        if (IsNeoForgeArtifactVersion(v.Version)) found.Add(v.Version);
                    AppLog.Log("versions", $"NeoForge listed too few builds; topped up from Prism's index to {found.Count}.");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    AppLog.Log("versions", "Prism's NeoForge index could not be read either: " + ex.Message);
                }
            }

            var saved = LoadNeoForgeList();
            if (found.Count < PlausibleNeoForgeCount) found.UnionWith(saved);
            var list = found.OrderByDescending(v => v, NeoForgeVersionComparer.Instance).ToList();
            if (list.Count >= PlausibleNeoForgeCount && list.Count >= saved.Count) SaveNeoForgeList(list);
            return list;
        });

    /// <summary>Fewer builds than this means an index is broken, not that NeoForge is new: it has well
    /// over a thousand.</summary>
    private const int PlausibleNeoForgeCount = 200;

    private static string NeoForgeListPath =>
        System.IO.Path.Combine(AppSettings.DataRootPath, "neoforge-versions.json");

    private static List<string> LoadNeoForgeList()
    {
        try
        {
            return System.IO.File.Exists(NeoForgeListPath)
                ? JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(NeoForgeListPath)) ?? []
                : [];
        }
        catch { return []; }
    }

    private static void SaveNeoForgeList(List<string> list)
    {
        try
        {
            var tmp = NeoForgeListPath + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            System.IO.File.Move(tmp, NeoForgeListPath, overwrite: true);
        }
        catch { /* a fallback that can't be written just isn't there next time */ }
    }

    /// <summary>True for a build of the net.neoforged:neoforge artifact: three parts starting 20 or 21
    /// (1.x Minecraft), or four parts (year-numbered Minecraft).</summary>
    private static bool IsNeoForgeArtifactVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v) || NeoForgeVersionForMinecraft(v) is null) return false;
        var parts = v.Split('-')[0].Split('.');
        return parts.Length >= 4 || int.Parse(parts[0]) <= 24;
    }

    /// <summary>The Minecraft version a NeoForge build is for.</summary>
    /// <remarks>Two schemes: "21.1.86" is Minecraft 1.21.1 (and "21.0.143" is 1.21), from the years of
    /// 1.x versions; since Minecraft moved to year numbers, a build has four parts, "26.1.2.111" for
    /// Minecraft 26.1.2 and "26.3.0.25-beta" for 26.3.</remarks>
    public static string? NeoForgeVersionForMinecraft(string neoforgeVersion)
    {
        var parts = neoforgeVersion.Split('-')[0].Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        if (parts.Length >= 4)
        {
            if (!int.TryParse(parts[2], out var patch)) return null;
            return patch == 0 ? $"{major}.{minor}" : $"{major}.{minor}.{patch}";
        }
        // major.minor -> 1.major[.minor unless minor == 0]
        return minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";
    }

    /// <summary>Orders NeoForge builds by their numbers; a -beta sorts before the same numbers without
    /// it.</summary>
    private sealed class NeoForgeVersionComparer : IComparer<string>
    {
        public static readonly NeoForgeVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var (xn, xs) = Split(x);
            var (yn, ys) = Split(y);
            for (var i = 0; i < Math.Max(xn.Length, yn.Length); i++)
            {
                var a = i < xn.Length ? xn[i] : 0;
                var b = i < yn.Length ? yn[i] : 0;
                if (a != b) return a.CompareTo(b);
            }
            if (xs.Length == 0 || ys.Length == 0) return ys.Length.CompareTo(xs.Length);
            return string.CompareOrdinal(xs, ys);
        }

        private static (int[] Numbers, string Suffix) Split(string v)
        {
            var dash = v.IndexOf('-');
            var core = dash < 0 ? v : v[..dash];
            var suffix = dash < 0 ? "" : v[(dash + 1)..];
            return (core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray(), suffix);
        }
    }

    // ---------- Forge ----------

    public Task<Dictionary<string, List<string>>> ListForgeVersionsByMinecraftAsync(CancellationToken ct = default) =>
        CachedAsync("forge", async () =>
        {
            // Forge promotions_slim.json has recommended/latest per MC version, but we want full list.
            // Use the maven metadata XML which lists every installer; parse out the MC-version prefix.
            var xml = await Http.GetStringAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct);
            // Each <version> looks like "1.20.1-47.2.0"; split on '-' to get the MC version.
            var versions = new List<string>();
            var s = xml;
            int i = 0;
            while ((i = s.IndexOf("<version>", i)) >= 0)
            {
                i += "<version>".Length;
                var end = s.IndexOf("</version>", i);
                if (end < 0) break;
                versions.Add(s.Substring(i, end - i));
                i = end;
            }
            var grouped = new Dictionary<string, List<string>>();
            foreach (var v in versions)
            {
                var idx = v.IndexOf('-');
                if (idx <= 0) continue;
                var mc = v[..idx];
                var forge = v[(idx + 1)..];
                if (!grouped.TryGetValue(mc, out var list))
                    grouped[mc] = list = new();
                list.Add(forge);
            }
            foreach (var list in grouped.Values) list.Reverse(); // newest first
            return grouped;
        });

    // ---------- any loader ----------

    /// <summary>
    /// Builds of <paramref name="loader"/> published for <paramref name="mcVersion"/>, newest first.
    /// Empty when that loader publishes nothing for that Minecraft version (or for
    /// <see cref="LoaderKind.None"/>). Returns a fresh list, so callers may bind or sort it
    /// without disturbing the shared version cache.
    /// </summary>
    public async Task<List<string>> ListLoaderVersionsAsync(
        LoaderKind loader, string mcVersion, CancellationToken ct = default)
    {
        switch (loader)
        {
            case LoaderKind.Fabric:
                return [.. await ListFabricLoaderVersionsAsync(mcVersion, ct)];

            case LoaderKind.Forge:
            {
                var byMc = await ListForgeVersionsByMinecraftAsync(ct);
                return byMc.TryGetValue(mcVersion, out var forge) ? [.. forge] : [];
            }

            case LoaderKind.NeoForge:
            {
                var all = await ListNeoForgeVersionsAsync(ct);
                return all.Where(v => NeoForgeVersionForMinecraft(v) == mcVersion).ToList();
            }

            default:
                return [];
        }
    }

    // ---------- JSON DTOs (private) ----------

    private sealed class MojangVersionManifest
    {
        [JsonPropertyName("latest")] public MojangLatest? Latest { get; set; }
        [JsonPropertyName("versions")] public List<MojangVersionEntry>? Versions { get; set; }
    }
    private sealed class MojangLatest
    {
        [JsonPropertyName("release")] public string? Release { get; set; }
        [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
    }
    private sealed class MojangVersionEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("releaseTime")] public DateTimeOffset ReleaseTime { get; set; }
    }

    private sealed class FabricLoaderEntry
    {
        [JsonPropertyName("loader")] public FabricVersionInfo Loader { get; set; } = new();
    }
    private sealed class FabricVersionInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }
    private sealed class FabricGameEntry
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    /// <summary>Prism Launcher's metadata index for one component (only the fields used here).</summary>
    private sealed class PrismIndex
    {
        [JsonPropertyName("versions")] public List<PrismIndexVersion>? Versions { get; set; }
    }
    private sealed class PrismIndexVersion
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }

    private sealed class MavenVersionList
    {
        [JsonPropertyName("isSnapshot")] public bool IsSnapshot { get; set; }
        [JsonPropertyName("versions")] public List<string>? Versions { get; set; }
    }
}
