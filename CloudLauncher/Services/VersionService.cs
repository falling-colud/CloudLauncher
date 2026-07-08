using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

public sealed record MinecraftReleaseInfo(string Id, string Type, DateTimeOffset ReleaseTime);

public sealed class VersionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

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

    public Task<List<string>> ListNeoForgeVersionsAsync(CancellationToken ct = default) =>
        CachedAsync("neoforge", async () =>
        {
            // NeoForge maven exposes a JSON list. Version strings are like "21.1.86" — the major
            // tracks the Minecraft version (21.1.x → MC 1.21.1, 20.4.x → MC 1.20.4, etc.).
            var resp = await Http.GetFromJsonAsync<MavenVersionList>(
                "https://maven.neoforged.net/api/maven/versions/releases/net%2Fneoforged%2Fneoforge", ct);
            var versions = resp?.Versions ?? new();
            versions.Reverse(); // newest first
            return versions;
        });

    public static string? NeoForgeVersionForMinecraft(string neoforgeVersion)
    {
        // 21.1.86 → 1.21.1, 20.4.237 → 1.20.4, 21.0.143 → 1.21
        var parts = neoforgeVersion.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        // major.minor → 1.major[.minor unless minor == 0]
        return minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";
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

    private sealed class MavenVersionList
    {
        [JsonPropertyName("isSnapshot")] public bool IsSnapshot { get; set; }
        [JsonPropertyName("versions")] public List<string>? Versions { get; set; }
    }
}
