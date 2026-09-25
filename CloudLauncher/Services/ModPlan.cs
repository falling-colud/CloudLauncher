using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

/// <summary>What a card on a planning board represents.</summary>
public enum PlanNodeKind
{
    /// <summary>One specific installed mod, resolved live by its metadata key.</summary>
    Mod = 0,
    /// <summary>A live query over a mod property ("everything tagged Magic"); members update as flags
    /// change.</summary>
    Group = 1,
    /// <summary>Free-text card: the actual planning prose.</summary>
    Note = 2,
    /// <summary>A titled backdrop region. Dragging it carries the cards sitting on it.</summary>
    Section = 3
}

/// <summary>Which mod property a <see cref="PlanNodeKind.Group"/> card selects on.</summary>
public enum PlanGroupProperty
{
    Category = 0,
    Priority = 1,
    Side     = 2,
    /// <summary>Library / Testing / Extra.</summary>
    Flag     = 3,
    /// <summary>Enabled / disabled / has update / update-incompatible / has note / has conflict.</summary>
    Status   = 4,
    Source   = 5,
    /// <summary>The pack folder the jar lives in (game / local).</summary>
    Folder   = 6,
    /// <summary>Every installed mod (a whole-pack card).</summary>
    All      = 7,
    /// <summary>How much content the mod adds (see <see cref="ModContentSize"/>).</summary>
    ContentSize = 8
}

/// <summary>How an edge between two cards is drawn.</summary>
public enum PlanEdgeStyle
{
    Arrow  = 0,
    Line   = 1,
    Dashed = 2
}

/// <summary>The one completion control a note card can carry, picked from its right-click menu.</summary>
public enum PlanNoteTracker
{
    None     = 0,
    /// <summary>A tick box in the card's corner: done or not.</summary>
    Checkbox = 1,
    /// <summary>A bar under the text, set to any percentage.</summary>
    Progress = 2
}

/// <summary>One item in a note card's task list.</summary>
public sealed class PlanTask
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }

    public PlanTask Clone() => new() { Text = Text, Done = Done };
}

/// <summary>The live selection behind a group card: a property plus the value to match.</summary>
public sealed class PlanGroupQuery
{
    public PlanGroupProperty Property { get; set; } = PlanGroupProperty.Category;

    /// <summary>The matched value: a category name, a priority number, "client", "library", "disabled",
    /// "modrinth", "local" and so on, interpreted per <see cref="Property"/>. Unused for
    /// <see cref="PlanGroupProperty.All"/>.</summary>
    public string Value { get; set; } = "";

    public PlanGroupQuery Clone() => new() { Property = Property, Value = Value };

    /// <summary>The heading shown on the card (the value itself reads best).</summary>
    public string Title => Property switch
    {
        PlanGroupProperty.All         => "All mods",
        PlanGroupProperty.Priority    => PriorityPalette.Label(ParsedPriority),
        PlanGroupProperty.ContentSize => ModContentSize.Label(ParsedContentSize),
        PlanGroupProperty.Category    => Value,
        _                             => Capitalize(Value)
    };

    /// <summary>The small line under the heading, naming which property this selects on.</summary>
    public string PropertyLabel => Property switch
    {
        PlanGroupProperty.Category => "Category",
        PlanGroupProperty.Priority => "Priority",
        PlanGroupProperty.ContentSize => "Content size",
        PlanGroupProperty.Side     => "Side",
        PlanGroupProperty.Flag     => "Flag",
        PlanGroupProperty.Status   => "Status",
        PlanGroupProperty.Source   => "Source",
        PlanGroupProperty.Folder   => "Folder",
        _                          => "Everything"
    };

    public int ParsedPriority => int.TryParse(Value, out var p) ? p : 0;

    public int ParsedContentSize => int.TryParse(Value, out var s) ? ModContentSize.Clamp(s) : 0;

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>One card on a planning board.</summary>
public sealed class PlanNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public PlanNodeKind Kind { get; set; }

    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Explicit size. 0 means "use the card's natural size for its kind".</summary>
    public double W { get; set; }
    public double H { get; set; }

    /// <summary>For <see cref="PlanNodeKind.Mod"/>: the mod's metadata key
    /// (<c>modrinth:...</c>, <c>curseforge:...</c> or <c>file:...</c>). Migrated forward automatically
    /// when a jar's identity resolves to a more stable key.</summary>
    public string? ModKey { get; set; }

    /// <summary>For <see cref="PlanNodeKind.Group"/>: the live selection.</summary>
    public PlanGroupQuery? Query { get; set; }

    /// <summary>The section this card belongs to (its <see cref="Id"/>), or null when loose. Set when a
    /// card is dropped onto a section, so overlapping sections never steal each other's cards. Not used
    /// by section nodes.</summary>
    public string? SectionId { get; set; }

    /// <summary>Note/section heading, or a user override for a group card's heading.</summary>
    public string Title { get; set; } = "";

    /// <summary>Note body text.</summary>
    public string Body { get; set; } = "";

    /// <summary>Accent colour as <c>#RRGGBB</c>. Null = the theme default for the kind.</summary>
    public string? Color { get; set; }

    /// <summary>Group cards: show only the heading + count, not the member chips. Note cards: show only
    /// the title, with the description folded away under the card's arrow.</summary>
    public bool Collapsed { get; set; }

    /// <summary>Group cards: list every member instead of capping the list and showing "+N more".
    /// Independent of <see cref="Collapsed"/>, which hides the list entirely.</summary>
    public bool Expanded { get; set; }

    // Note-card tracking. Omitted from the JSON while unused, so cards without it serialize the same
    // as in older versions.

    /// <summary>Note cards: the completion control shown on the card, if any.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PlanNoteTracker Tracker { get; set; }

    /// <summary>Note cards with a <see cref="PlanNoteTracker.Checkbox"/>: whether it's ticked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Done { get; set; }

    /// <summary>Note cards with a <see cref="PlanNoteTracker.Progress"/> bar: 0-100.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Progress { get; set; }

    /// <summary>Note cards: the task list under the card's collapsible "Tasks" heading. Null = no list.</summary>
    public List<PlanTask>? Tasks { get; set; }

    /// <summary>Note cards: the task list is folded away under its arrow.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TasksCollapsed { get; set; }

    public PlanNode Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = Kind, X = X, Y = Y, W = W, H = H,
        ModKey = ModKey, Query = Query?.Clone(),
        Title = Title, Body = Body, Color = Color, Collapsed = Collapsed, Expanded = Expanded,
        Tracker = Tracker, Done = Done, Progress = Progress,
        Tasks = Tasks?.Select(t => t.Clone()).ToList(), TasksCollapsed = TasksCollapsed,
        SectionId = SectionId // callers that re-id sections (duplicate/paste) remap this afterwards
    };
}

/// <summary>A fixed spot on a card's outline, as fractions of the card's width and height (one of
/// them 0 or 1), so it stays on the same part of the outline when the card is moved or resized.</summary>
public sealed class PlanAnchor
{
    public double X { get; set; }
    public double Y { get; set; }

    public PlanAnchor Clone() => new() { X = X, Y = Y };
}

/// <summary>A labelled connection between two cards.</summary>
public sealed class PlanEdge
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string? Label { get; set; }
    public string? Color { get; set; }
    public PlanEdgeStyle Style { get; set; } = PlanEdgeStyle.Arrow;

    /// <summary>Where the arrow leaves its start card, when it was drawn from a chosen point on the
    /// outline. Null = automatic: whichever point of the outline faces the other end.</summary>
    public PlanAnchor? FromAnchor { get; set; }

    /// <summary>Where the arrow meets its target, when it was dropped on a chosen point of the outline.
    /// Null = automatic.</summary>
    public PlanAnchor? ToAnchor { get; set; }

    /// <summary>A copy running between other cards (duplicating, pasting) that keeps everything else.</summary>
    public PlanEdge CopyBetween(string fromId, string toId) => new()
    {
        FromId = fromId, ToId = toId, Label = Label, Color = Color, Style = Style,
        FromAnchor = FromAnchor?.Clone(), ToAnchor = ToAnchor?.Clone()
    };
}

/// <summary>One planning board (one "page" in the planning tab).</summary>
public sealed class PlanBoard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Plan";

    /// <summary>Optional group this board belongs to in the board switcher / manage dialog. Null =
    /// uncategorized. Position in <see cref="PackPlans.Boards"/> is the display order.</summary>
    public string? Category { get; set; }

    public List<PlanNode> Nodes { get; set; } = new();
    public List<PlanEdge> Edges { get; set; } = new();

    /// <summary>One-time flag: set once section membership has been migrated from the old
    /// position-based model to explicit <see cref="PlanNode.SectionId"/>.</summary>
    public bool SectionMembershipSet { get; set; }

    /// <summary>Saved viewport so a board reopens where it was left.</summary>
    public double PanX { get; set; }
    public double PanY { get; set; }
    public double Zoom { get; set; } = 1;

    [JsonIgnore]
    public bool IsEmpty => Nodes.Count == 0 && Edges.Count == 0;

    public PlanNode? Node(string? id) => id is null ? null : Nodes.FirstOrDefault(n => n.Id == id);
}

/// <summary>Root document persisted to <c>game/.cloudlauncher/plans.json</c> and synced with the pack.</summary>
public sealed class PackPlans
{
    public int Version { get; set; } = 1;
    public List<PlanBoard> Boards { get; set; } = new();

    /// <summary>Ordered board-category names (defines the group order in the switcher; also holds
    /// categories that currently have no boards).</summary>
    public List<string> BoardCategories { get; set; } = new();

    /// <summary>The board that was open last, so the tab reopens on it.</summary>
    public string? LastBoardId { get; set; }
}
