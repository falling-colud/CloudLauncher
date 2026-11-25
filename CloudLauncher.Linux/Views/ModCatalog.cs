using System.Net;
using System.Text.RegularExpressions;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>How a store sorts a search.</summary>
public enum ModSort { Relevance, Latest, Downloads }

/// <summary>
/// Where the mod and modpack browsers read from: CurseForge and Modrinth through the shared services,
/// exactly as the Windows launcher's pages ask them. A page can be handed another catalog (the headless
/// shots do, since they cannot reach the stores); installing always goes through the real services.
/// </summary>
public class ModCatalog
{
    /// <summary>The stores.</summary>
    public static ModCatalog Stores { get; } = new();

    /// <summary>One page of results from one store. <paramref name="modpacks"/> searches modpacks
    /// instead of mods.</summary>
    public virtual Task<List<ModSummary>> SearchAsync(ModSource store, string query, string? mc, string? loader,
        ModSort sort, int offset, int limit, bool modpacks, CancellationToken ct) =>
        store == ModSource.CurseForge
            ? App.State.CurseForge.SearchAsync(query, mc, loader, limit, offset,
                modpacks ? CurseForgeService.ClassIdModpacks : CurseForgeService.ClassIdMods, CurseSortField(sort), ct: ct)
            : App.State.Modrinth.SearchAsync(query, mc, loader, limit: limit, offset: offset,
                projectType: modpacks ? "modpack" : "mod", index: ModrinthIndex(sort), ct: ct);

    /// <summary>A project's versions, newest first, from the shared catalog (cached for a while):
    /// those for <paramref name="mc"/> and <paramref name="loader"/> when given, otherwise all.</summary>
    public virtual async Task<IReadOnlyList<ModVersion>> VersionsAsync(ModSummary mod, string? mc, string? loader,
        CancellationToken ct) =>
        await App.State.ModVersions.GetVersionsAsync(mod, Clean(mc), Clean(loader), ct: ct);

    /// <summary>The project's page: its description and links.</summary>
    public virtual Task<ModProjectDetail> DetailAsync(ModSummary mod, CancellationToken ct) =>
        mod.Source == ModSource.CurseForge
            ? App.State.CurseForge.GetProjectDetailAsync(int.TryParse(mod.Id, out var id) ? id : 0, ct)
            : App.State.Modrinth.GetProjectDetailAsync(mod.Id, ct);

    /// <summary>The same project on the other store, or null.</summary>
    public virtual async Task<ModSummary?> CounterpartAsync(ModSummary mod, CancellationToken ct) => mod.Source switch
    {
        ModSource.CurseForge => await App.State.Modrinth.FindCounterpartAsync(mod.Slug, mod.Name, ct),
        ModSource.Modrinth => await App.State.CurseForge.FindCounterpartAsync(mod.Slug, mod.Name, ct),
        _ => null
    };

    public static string ModrinthIndex(ModSort sort) => sort switch
    {
        ModSort.Latest => "updated",
        ModSort.Downloads => "downloads",
        _ => "relevance"
    };

    /// <summary>CurseForge's sort fields: 2 popularity, 3 last updated, 6 total downloads.</summary>
    public static int CurseSortField(ModSort sort) => sort switch
    {
        ModSort.Latest => 3,
        ModSort.Downloads => 6,
        _ => 2
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Small wording helpers the mods pages share.</summary>
public static class ModText
{
    /// <summary>"950", "12.4K", "3.1M".</summary>
    public static string Number(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
        : n >= 1_000 ? $"{n / 1000.0:0.#}K"
        : n.ToString();

    public static string Initial(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();

    public static string Store(ModSource source) => source switch
    {
        ModSource.CurseForge => "CurseForge",
        ModSource.Modrinth => "Modrinth",
        _ => "CloudLauncher"
    };

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string NameList(IEnumerable<string> names)
    {
        var list = names.Distinct().ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1]
        };
    }

    /// <summary>"1 mod", "12 mods".</summary>
    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    /// <summary>A project page's description as plain text: CurseForge sends HTML, Modrinth Markdown
    /// (which may embed HTML too).</summary>
    public static string Plain(string? content, bool isMarkdown)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var text = content.Replace("\r\n", "\n").Replace('\r', '\n');
        if (isMarkdown)
        {
            text = Regex.Replace(text, @"```[^\n]*\n", "");
            text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
            text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s*", "");
            text = Regex.Replace(text, @"(?m)^\s{0,3}>\s?", "");
            text = Regex.Replace(text, @"(?m)^\s*[-*+]\s+", "- ");
            text = Regex.Replace(text, @"(?m)^\s*(-{3,}|\*{3,}|_{3,})\s*$", "");
            text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        }
        text = Regex.Replace(text, @"(?i)<\s*br\s*/?\s*>", "\n");
        text = Regex.Replace(text, @"(?i)</\s*(p|div|h[1-6]|li|ul|ol|blockquote|section|article|tr|table)\s*>", "\n\n");
        text = Regex.Replace(text, @"(?i)<\s*li[^>]*>", "- ");
        text = Regex.Replace(text, @"(?is)<(script|style)[^>]*>.*?</\1\s*>", "");
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text;
    }
}
