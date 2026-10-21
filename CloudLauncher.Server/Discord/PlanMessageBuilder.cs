using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CloudLauncher.Server.Discord;

/// <summary>
/// The Discord messages for planning boards: a container with a board's name, its picture, how far
/// along each section is, and the ways to open it.
/// </summary>
/// <remarks>
/// <para>Two layouts. A webhook can't carry controls, so it posts one message per board
/// (<see cref="Board"/>). The app posts one message for the whole pack (<see cref="Switcher"/>): one
/// board at a time, with a board list and Previous/Next that change the message in place.</para>
/// <para>Built with Discord's layout components (message flag <see cref="ComponentsV2"/>), which can't
/// be mixed with plain content or embeds. A message holds at most 40 components and 4,000 characters
/// of text, so the section list is cut short on a very big board rather than split.</para>
/// </remarks>
public static class PlanMessageBuilder
{
    public const int ComponentsV2 = 1 << 15;
    public const string OpenActivityId = "plan:activity";

    /// <summary>The board list's custom id is this plus the link id; its value is a board id.</summary>
    public const string SwitchBoardPrefix = "plan:board:";
    public const string PreviousPrefix = "plan:prev:";
    public const string NextPrefix = "plan:next:";

    /// <summary>A select menu lists at most this many options (Discord's limit).</summary>
    private const int MaxChoices = 25;

    /// <summary>The bar down the container's side: the launcher's readable Rust.</summary>
    private const int AccentColour = 0xA07868;

    private const int MaxSummaryChars = 2400;

    /// <summary>A board as the board list offers it.</summary>
    public sealed record BoardChoice(string Id, string Name, int Cards, int Done, int Total)
    {
        public static BoardChoice Of(PlanBoardData board)
        {
            var (done, total) = Progress(board.Nodes);
            return new BoardChoice(board.Id, board.Name, board.Nodes.Count(n => n.Kind != PlanNodeData.KindSection),
                done, total);
        }

        /// <summary>The line under the board's name in the list.</summary>
        public string Detail => Cards == 0 ? "No cards yet"
            : Total > 0 ? $"{Plural(Cards, "card")} · {Done} of {Total} done" : Plural(Cards, "card");
    }

    public static string ImageName(PlanBoardData board, string hash) =>
        "board-" + new string(board.Name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(24).ToArray())
                 + "-" + hash[..8].ToLowerInvariant() + ".png";

    /// <summary>One board's own message (the webhook layout).</summary>
    /// <param name="changedAt">When this board last changed, for Discord's live "x minutes ago".</param>
    /// <param name="viewUrl">The live viewer, linked from a button; null leaves it out.</param>
    /// <param name="activityButton">Posted by the app with its Activity set up: add the button that
    /// opens the board inside Discord.</param>
    /// <param name="asWebhook">Posted through a webhook: name and avatar go on the message itself.</param>
    public static JsonObject Board(PlanBoardData board, string packName, DateTimeOffset changedAt, string imageName,
        string? viewUrl, bool activityButton, bool asWebhook)
    {
        var container = Body(board, Facts(board, packName, changedAt, null), packName, imageName);
        var buttons = OpenButtons(board, viewUrl, activityButton);
        if (buttons.Count > 0) container.Add(new JsonObject { ["type"] = 1, ["components"] = buttons });
        return Wrap(container, board, imageName, asWebhook);
    }

    /// <summary>The pack's one message (the app's layout): the board being shown, then the list of
    /// boards and Previous/Next to change it, then the ways to open the boards.</summary>
    /// <param name="boards">Every board the message can show, in the launcher's order.</param>
    /// <param name="linkId">Goes into the controls' ids, so a click finds its link.</param>
    public static JsonObject Switcher(PlanBoardData board, IReadOnlyList<BoardChoice> boards, string packName,
        DateTimeOffset changedAt, string imageName, string viewUrl, Guid linkId, bool activityButton)
    {
        var index = Math.Max(0, boards.ToList().FindIndex(b => b.Id == board.Id));
        var position = boards.Count > 1 ? $"board {index + 1} of {boards.Count}" : null;
        var container = Body(board, Facts(board, packName, changedAt, position), packName, imageName);
        var id = linkId.ToString("N");

        if (boards.Count > 1)
        {
            // A window of the list around the board shown, when there are more boards than a list holds.
            var start = Math.Clamp(index - MaxChoices / 2, 0, Math.Max(0, boards.Count - MaxChoices));
            var options = new JsonArray();
            foreach (var choice in boards.Skip(start).Take(MaxChoices))
                options.Add(new JsonObject
                {
                    ["label"] = Trim(choice.Name.Length > 0 ? choice.Name : "Board", 100),
                    ["value"] = choice.Id,
                    ["description"] = Trim(choice.Detail, 100),
                    ["default"] = choice.Id == board.Id
                });
            container.Add(new JsonObject
            {
                ["type"] = 1,
                ["components"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = 3, // string select
                        ["custom_id"] = SwitchBoardPrefix + id,
                        ["placeholder"] = "Show another board",
                        ["options"] = options
                    }
                }
            });
        }

        var buttons = new JsonArray();
        if (boards.Count > 1)
        {
            buttons.Add(new JsonObject
            {
                ["type"] = 2, ["style"] = 2, ["label"] = "Previous", ["custom_id"] = PreviousPrefix + id,
                ["emoji"] = new JsonObject { ["name"] = "⬅️" }
            });
            buttons.Add(new JsonObject
            {
                ["type"] = 2, ["style"] = 2, ["label"] = "Next", ["custom_id"] = NextPrefix + id,
                ["emoji"] = new JsonObject { ["name"] = "➡️" }
            });
        }
        foreach (var button in OpenButtons(board, viewUrl, activityButton)) buttons.Add(button!.DeepClone());
        container.Add(new JsonObject { ["type"] = 1, ["components"] = buttons });
        return Wrap(container, board, imageName, asWebhook: false);
    }

    /// <summary>For an edit: the same message, minus what only a new message may carry.</summary>
    public static JsonObject ForEdit(JsonObject message)
    {
        message.Remove("username");
        message.Remove("avatar_url");
        message.Remove("flags");
        return message;
    }

    private static string Facts(PlanBoardData board, string packName, DateTimeOffset changedAt, string? position)
    {
        var cards = board.Nodes.Count(n => n.Kind != PlanNodeData.KindSection);
        var sections = board.Nodes.Count - cards;
        var (done, total) = Progress(board.Nodes);
        var facts = new List<string> { Escape(packName) };
        if (position is not null) facts.Add(position);
        facts.Add(cards > 0 || sections == 0
            ? Plural(cards, "card")
            : $"{Plural(sections, "section")}, no cards yet");
        if (total > 0) facts.Add($"{done} of {total} done");
        facts.Add($"updated <t:{changedAt.ToUnixTimeSeconds()}:R>");
        return string.Join("  ·  ", facts);
    }

    /// <summary>The title, the picture and the section list.</summary>
    private static JsonArray Body(PlanBoardData board, string facts, string packName, string imageName)
    {
        var container = new JsonArray
        {
            Text($"### {Escape(board.Name)}\n-# {facts}"),
            new JsonObject
            {
                ["type"] = 12, // media gallery
                ["items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["media"] = new JsonObject { ["url"] = "attachment://" + imageName },
                        ["description"] = Trim($"The {board.Name} planning board of {packName}", 1000)
                    }
                }
            }
        };

        var summary = Summary(board);
        if (summary.Length > 0)
        {
            container.Add(new JsonObject { ["type"] = 14, ["divider"] = true, ["spacing"] = 1 }); // separator
            container.Add(Text(summary));
        }
        return container;
    }

    /// <summary>"Open board" (the Activity, posted by the app only) and the browser viewer, at this board.</summary>
    private static JsonArray OpenButtons(PlanBoardData board, string? viewUrl, bool activityButton)
    {
        var buttons = new JsonArray();
        if (activityButton)
            buttons.Add(new JsonObject
            {
                ["type"] = 2, ["style"] = 1, ["label"] = "Open board", ["custom_id"] = OpenActivityId
            });
        if (viewUrl is not null)
            buttons.Add(new JsonObject
            {
                ["type"] = 2, ["style"] = 5, ["label"] = activityButton ? "Open in browser" : "Open live board",
                ["url"] = viewUrl + "?board=" + Uri.EscapeDataString(board.Id)
            });
        return buttons;
    }

    private static JsonObject Wrap(JsonArray container, PlanBoardData board, string imageName, bool asWebhook)
    {
        var message = new JsonObject
        {
            ["flags"] = ComponentsV2,
            ["components"] = new JsonArray
            {
                new JsonObject { ["type"] = 17, ["accent_color"] = AccentColour, ["components"] = container }
            },
            ["attachments"] = new JsonArray
            {
                new JsonObject { ["id"] = 0, ["filename"] = imageName, ["description"] = Trim(board.Name, 1000) }
            },
            // Card text is whatever people typed: never let it ping anyone.
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() }
        };
        if (asWebhook)
        {
            message["username"] = "CloudLauncher";
            message["avatar_url"] = "https://cloudlauncher.co/img/apple-touch-icon.png";
        }
        return message;
    }

    /// <summary>One line per section, top to bottom: how much of it is done, or how many cards it
    /// holds when nothing on it is tracked. Then what to find, for placeholder cards.</summary>
    private static string Summary(PlanBoardData board)
    {
        var sb = new StringBuilder();
        var sections = board.Nodes.Where(n => n.Kind == PlanNodeData.KindSection)
            .OrderBy(n => Math.Round(n.Y / 48)).ThenBy(n => n.X).ToList();
        var shown = 0;
        foreach (var section in sections)
        {
            var inside = board.Nodes.Where(n => n.Kind != PlanNodeData.KindSection && n.SectionId == section.Id).ToList();
            var (done, total) = Progress(inside);
            var title = section.Title.Length > 0 ? section.Title : "Section";
            var line = total > 0
                ? $"- {(done == total ? "✅ " : "")}**{Escape(title)}**  ·  {done}/{total} done"
                : $"- **{Escape(title)}**  ·  {(inside.Count == 0 ? "empty" : Plural(inside.Count, "card"))}";
            if (sb.Length + line.Length > MaxSummaryChars)
            {
                sb.Append($"-# … and {sections.Count - shown} more section{(sections.Count - shown == 1 ? "" : "s")}");
                break;
            }
            sb.Append(line).Append('\n');
            shown++;
        }

        var placeholders = board.Nodes.Where(n => n.Kind == PlanNodeData.KindPlaceholder).ToList();
        var open = placeholders.Where(p => PlaceholderStatus.IsOpen(p.Placeholder?.Status)).ToList();
        if (open.Count > 0 && sb.Length < MaxSummaryChars)
        {
            var names = open.Take(8).Select(p => Escape(p.Title.Length > 0 ? p.Title : "Unnamed"));
            var more = open.Count > 8 ? $" and {open.Count - 8} more" : "";
            sb.Append($"\U0001F50E  Still to find: {string.Join(", ", names)}{more}\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>What's tracked across these cards: ticked checkboxes, finished progress bars (as
    /// fractions of one) and task-list items.</summary>
    private static (int Done, int Total) Progress(IEnumerable<PlanNodeData> nodes)
    {
        double done = 0;
        var total = 0;
        foreach (var n in nodes)
        {
            if (n.Kind != PlanNodeData.KindNote) continue;
            if (n.Tracker == PlanNodeData.TrackerCheckbox) { total++; if (n.Done) done++; }
            else if (n.Tracker == PlanNodeData.TrackerProgress) { total++; done += Math.Clamp(n.Progress, 0, 100) / 100.0; }
            if (n.Tasks is { Count: > 0 } tasks) { total += tasks.Count; done += tasks.Count(t => t.Done); }
        }
        return ((int)Math.Floor(done + 0.001), total);
    }

    private static JsonObject Text(string content) => new() { ["type"] = 10, ["content"] = Trim(content, 3900) };

    /// <summary>Keeps typed text from turning into Discord formatting.</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s.Replace('\n', ' ').Replace('\r', ' '))
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '[' or ']' or '(' or ')' or '<' or '@' or ':')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    public static string Plural(int n, string word) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");
}
