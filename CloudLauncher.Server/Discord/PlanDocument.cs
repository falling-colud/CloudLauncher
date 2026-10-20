using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Server.Discord;

/// <summary>
/// The server's read-only view of a launcher's plans.json: just what drawing a board needs.
/// </summary>
/// <remarks>
/// The launcher owns the format (<c>CloudLauncher/Services/ModPlan.cs</c>). It writes PascalCase
/// names and enums as numbers; anything this side doesn't know is ignored, so a newer launcher's
/// fields can't break the picture.
/// </remarks>
public sealed class PlanDocData
{
    public List<PlanBoardData> Boards { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Reads a stored plans.json, or null when it isn't one.</summary>
    public static PlanDocData? TryParse(string json)
    {
        try { return JsonSerializer.Deserialize<PlanDocData>(json, Options); }
        catch (JsonException) { return null; }
    }
}

public sealed class PlanBoardData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Plan";
    public string? Category { get; set; }
    public List<PlanNodeData> Nodes { get; set; } = new();
    public List<PlanEdgeData> Edges { get; set; } = new();

    [JsonIgnore] public bool IsEmpty => Nodes.Count == 0;

    public PlanNodeData? Node(string? id) => id is null ? null : Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>A hash of everything the board's message shows, so a board nobody touched is not
    /// redrawn when another board on the pack changes. The launcher's own view state (pan, zoom)
    /// isn't in it: that moves on every PC without changing the board.</summary>
    public string ContentHash(string salt)
    {
        var sb = new StringBuilder(salt).Append('|').Append(Name).Append('|');
        foreach (var n in Nodes)
        {
            sb.Append(n.Id).Append(n.Kind).Append(',').Append(n.X).Append(',').Append(n.Y).Append(',')
              .Append(n.W).Append(',').Append(n.H).Append(',').Append(n.ModKey).Append(',')
              .Append(n.Query?.Property).Append(n.Query?.Value).Append(',').Append(n.SectionId).Append(',')
              .Append(n.Title).Append('\u001f').Append(n.Body).Append('\u001f').Append(n.Color).Append(',')
              .Append(n.Collapsed).Append(n.Expanded).Append(n.Tracker).Append(n.Done).Append(n.Progress)
              .Append(n.TasksCollapsed).Append(',')
              .Append(n.Placeholder?.Status).Append(n.Placeholder?.Link).Append(n.Placeholder?.IconUrl).Append(';');
            if (n.Tasks is { } tasks)
                foreach (var t in tasks) sb.Append(t.Done ? '+' : '-').Append(t.Text).Append('\u001f');
        }
        foreach (var e in Edges)
            sb.Append(e.FromId).Append('>').Append(e.ToId).Append(e.Label).Append(e.Color).Append(e.Style)
              .Append(e.FromAnchor?.X).Append(e.FromAnchor?.Y).Append(e.ToAnchor?.X).Append(e.ToAnchor?.Y).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24];
    }
}

public sealed class PlanNodeData
{
    public string Id { get; set; } = "";
    /// <summary>0 mod, 1 group, 2 note, 3 section, 4 placeholder (<c>PlanNodeKind</c>).</summary>
    public int Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string? ModKey { get; set; }
    public PlanQueryData? Query { get; set; }
    public string? SectionId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Color { get; set; }
    public bool Collapsed { get; set; }
    public bool Expanded { get; set; }
    /// <summary>0 none, 1 checkbox, 2 progress bar (<c>PlanNoteTracker</c>).</summary>
    public int Tracker { get; set; }
    public bool Done { get; set; }
    public int Progress { get; set; }
    public List<PlanTaskData>? Tasks { get; set; }
    public bool TasksCollapsed { get; set; }
    public PlanPlaceholderData? Placeholder { get; set; }

    public const int KindMod = 0, KindGroup = 1, KindNote = 2, KindSection = 3, KindPlaceholder = 4;
    public const int TrackerNone = 0, TrackerCheckbox = 1, TrackerProgress = 2;
}

public sealed class PlanTaskData
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class PlanQueryData
{
    /// <summary><c>PlanGroupProperty</c>: 0 category, 1 priority, 2 side, 3 flag, 4 status, 5 source,
    /// 6 folder, 7 all, 8 content size.</summary>
    public int Property { get; set; }
    public string Value { get; set; } = "";

    public string Title => Property switch
    {
        7 => "All mods",
        1 => $"Priority {Value}",
        8 => Value switch { "1" => "Small", "2" => "Medium", "3" => "Large", _ => "Unsized" },
        0 => Value,
        _ => Value.Length > 0 ? char.ToUpperInvariant(Value[0]) + Value[1..] : Value
    };

    public string PropertyLabel => Property switch
    {
        0 => "Category",
        1 => "Priority",
        8 => "Content size",
        2 => "Side",
        3 => "Flag",
        4 => "Status",
        5 => "Source",
        6 => "Folder",
        _ => "Everything"
    };
}

public sealed class PlanPlaceholderData
{
    public string? Status { get; set; }
    public string? Link { get; set; }
    public string? IconUrl { get; set; }
}

public sealed class PlanEdgeData
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string? Label { get; set; }
    public string? Color { get; set; }
    /// <summary>0 arrow, 1 line, 2 dashed (<c>PlanEdgeStyle</c>).</summary>
    public int Style { get; set; }
    public PlanAnchorData? FromAnchor { get; set; }
    public PlanAnchorData? ToAnchor { get; set; }
}

public sealed class PlanAnchorData
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>The placeholder statuses, as the launcher names and colours them
/// (<c>PlanPlaceholderStatus</c>).</summary>
public static class PlaceholderStatus
{
    public static string Normalize(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "looking" => "looking",
        "porting" => "porting",
        "found" => "found",
        "dropped" => "dropped",
        _ => "needs-port"
    };

    public static string Label(string? s) => Normalize(s) switch
    {
        "looking" => "Looking for one",
        "porting" => "Porting it myself",
        "found" => "Found",
        "dropped" => "Leaving it out",
        _ => "Needs a port"
    };

    public static uint Colour(string? s) => Normalize(s) switch
    {
        "looking" => 0xFF5B9DF9,
        "porting" => 0xFFCFAEFF,
        "found" => 0xFF3FB950,
        "dropped" => 0xFF6B7688,
        _ => 0xFFE3B341
    };

    public static bool IsOpen(string? s) => Normalize(s) is "needs-port" or "looking" or "porting";
}
