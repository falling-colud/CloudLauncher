using SkiaSharp;

namespace CloudLauncher.Server.Discord;

/// <summary>What a mod card shows for its key: the store's name and icon, looked up by the server.</summary>
public sealed record ModLabel(string Name, string? Meta, string? IconUrl);

/// <summary>
/// Lays a board out the way the launcher's planning view does (<c>ModPlanView</c>, grid on) and turns
/// it into drawing steps, which <see cref="PlanPainters"/> paint as a picture or as SVG.
/// </summary>
/// <remarks>
/// Sizes and spacing are the launcher's own constants. What only the launcher knows is left out or
/// simplified: a group card's members (worked out from the PC's mod list) and the editing controls
/// (fold arrows, resize grips, "Add task"). Section titles without a colour of their own are drawn a
/// little brighter than in the launcher, where they sit almost on the background, because here they
/// are the only way to tell sections apart at a glance.
/// </remarks>
internal sealed class PlanBoardLayout
{
    private const double GridCell = 24;
    private const double ModW = 216, ModH = 72;
    private const double GroupW = 240, GroupHeaderH = 50, GroupRowH = 20;
    private const double NoteW = 240, NoteH = 144;
    private const double NoteProgressH = 26, NoteTasksHeaderH = 28, NoteTaskRowH = 22, NoteAddTaskRowH = 24, NoteTasksPad = 6;
    private const double NoteCornerW = 26, NoteGripRoom = 18, NoteBodyLineH = 16, MinCardW = 144;
    private const double SectionW = 552, SectionH = 216, MinSectionH = 144;
    private const double SectionHeaderH = 30, SectionPad = 24;
    private const double SlotColW = 240, SlotGap = 24, SlotPitch = SlotColW + SlotGap;
    private const double PlaceholderW = 216, PlaceholderH = 72, PlaceholderInfoH = 96;
    private const float SectionRestOpacity = 0.93f;
    private const float NoteBorder = 1.4f;

    private readonly PlanBoardData _board;
    private readonly PlanText _text;
    private readonly IReadOnlyDictionary<string, ModLabel> _mods;
    private readonly Dictionary<string, SKRect> _rect = new();
    private readonly List<PlanOp> _ops = new();
    private SKRect _bounds = SKRect.Empty;

    private PlanBoardLayout(PlanBoardData board, PlanText text, IReadOnlyDictionary<string, ModLabel> mods)
    {
        _board = board;
        _text = text;
        _mods = mods;
    }

    /// <summary>The board's drawing steps and the area they cover, in board coordinates.</summary>
    public static (IReadOnlyList<PlanOp> Ops, SKRect Bounds) Build(PlanBoardData board, PlanText text,
        IReadOnlyDictionary<string, ModLabel> mods)
    {
        var layout = new PlanBoardLayout(board, text, mods);
        layout.Run();
        return (layout._ops, layout._bounds);
    }

    private void Run()
    {
        foreach (var n in _board.Nodes)
        {
            var (w, h) = SizeOf(n);
            _rect[n.Id] = Rect(n.X, n.Y, w, h);
        }
        ApplySectionColumnWidths();
        AutoSizeSections();

        foreach (var n in _board.Nodes.Where(n => n.Kind == PlanNodeData.KindSection)) DrawSection(n);
        foreach (var e in _board.Edges) DrawEdge(e);
        foreach (var n in _board.Nodes.Where(n => n.Kind != PlanNodeData.KindSection))
        {
            switch (n.Kind)
            {
                case PlanNodeData.KindMod: DrawModCard(n); break;
                case PlanNodeData.KindGroup: DrawGroupCard(n); break;
                case PlanNodeData.KindPlaceholder: DrawPlaceholderCard(n); break;
                default: DrawNoteCard(n); break;
            }
        }
        foreach (var r in _rect.Values) Grow(r);
    }

    // ── sizes ───────────────────────────────────────────────────────────────────

    private (double w, double h) SizeOf(PlanNodeData n)
    {
        switch (n.Kind)
        {
            case PlanNodeData.KindMod:
                return (CardW(n, ModW), ModH);
            case PlanNodeData.KindGroup:
            {
                // The members come from a PC's mod list, which the server doesn't have: one row says so.
                var rows = n.Collapsed ? 0 : 1;
                var h = GroupHeaderH + rows * GroupRowH + (rows > 0 ? 10 : 4);
                return (CardW(n, GroupW), CeilToGrid(h));
            }
            case PlanNodeData.KindPlaceholder:
                return (CardW(n, PlaceholderW), n.Body.Length > 0 ? PlaceholderInfoH : PlaceholderH);
            case PlanNodeData.KindSection:
            {
                var w = n.W > 0 ? n.W : SectionW;
                return (SnapToColumns(w), n.H > 0 ? n.H : SectionH);
            }
            default:
            {
                var w = CardW(n, NoteW);
                return (w, NoteCardH(n, w));
            }
        }
    }

    private static double CardW(PlanNodeData n, double natural) =>
        n.W <= 0 ? natural : Math.Max(MinCardW, SnapToGrid(n.W));

    private double NoteCardH(PlanNodeData n, double w)
    {
        var h = NoteTextH(n, w) + NoteExtraH(n);
        return NoteShowsTitleOnly(n, w) ? h : CeilToGrid(h);
    }

    private double NoteTextH(PlanNodeData n, double w)
    {
        if (NoteShowsTitleOnly(n, w)) return NoteTitleOnlyH(n, w);
        if (n.H > 0) return n.H;
        var tracked = n.Tracker != PlanNodeData.TrackerNone || n.Tasks is { Count: > 0 };
        return tracked && n.Body.Length == 0 ? NoteTitleOnlyH(n, w) : NoteH;
    }

    private bool NoteShowsTitleOnly(PlanNodeData n, double w) =>
        (n.Body.Length > 0 && n.Collapsed) || (n.H > 0 && n.H < NoteTitleOnlyH(n, w) + NoteBodyLineH);

    private static int NoteCornerControls(PlanNodeData n) =>
        (n.Body.Length > 0 ? 1 : 0) + (n.Tracker == PlanNodeData.TrackerCheckbox ? 1 : 0);

    private static string NoteHeadline(PlanNodeData n) =>
        n.Title.Length > 0 ? n.Title : n.Body.Replace("\r\n", "\n").Split('\n', 2)[0].Trim();

    private static PlanStyle HeadlineStyle(PlanNodeData n) =>
        n.Title.Length > 0 ? PlanStyle.Semi(13.5f) : PlanStyle.Regular(12);

    private double NoteTitleOnlyH(PlanNodeData n, double w)
    {
        var corner = NoteCornerControls(n);
        var minH = corner > 0 ? 38.0 : 12;
        var headline = NoteHeadline(n);
        if (headline.Length == 0) return minH;
        var style = HeadlineStyle(n);
        var width = (float)Math.Max(20, w - 3 - 24 - Math.Max(corner * NoteCornerW, NoteGripRoom));
        var lines = n.Title.Length > 0 ? _text.Wrap(headline, style, width).Count : 1;
        return Math.Max(minH, Math.Ceiling(lines * style.LineHeight + 20));
    }

    private static double NoteExtraH(PlanNodeData n)
    {
        var h = n.Tracker == PlanNodeData.TrackerProgress ? NoteProgressH + 2 : 0;
        if (n.Tasks is { Count: > 0 } tasks)
            h += NoteTasksHeaderH + NoteTasksPad + (n.TasksCollapsed ? 0 : tasks.Count * NoteTaskRowH + NoteAddTaskRowH);
        return h;
    }

    private void ApplySectionColumnWidths()
    {
        foreach (var card in _board.Nodes.Where(n => n.Kind != PlanNodeData.KindSection && n.SectionId is not null))
        {
            if (_board.Node(card.SectionId) is not { Kind: PlanNodeData.KindSection } sec) continue;
            if (!_rect.TryGetValue(sec.Id, out var sr) || !_rect.TryGetValue(card.Id, out var cr)) continue;
            var cols = ColumnCount(sr);
            var span = SpanOf(cr.Width, cols);
            var w = span * SlotPitch - SlotGap;
            var h = card.Kind == PlanNodeData.KindNote ? NoteCardH(card, w) : cr.Height;
            var col = Math.Clamp((int)Math.Round((cr.Left - (sr.Left + SectionPad)) / SlotPitch), 0, cols - span);
            _rect[card.Id] = Rect(sr.Left + SectionPad + col * SlotPitch, cr.Top, w, h);
        }
    }

    private void AutoSizeSections()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var section in _board.Nodes.Where(n => n.Kind == PlanNodeData.KindSection))
            {
                var sr = _rect[section.Id];
                var cards = Contained(section).Select(n => _rect[n.Id]).ToList();
                var wanted = cards.Count == 0
                    ? Math.Max(sr.Height, SectionH)
                    : Math.Max(MinSectionH, cards.Max(r => r.Bottom) + SectionPad - sr.Top);
                _rect[section.Id] = Rect(sr.Left, sr.Top, sr.Width, CeilToGrid(wanted));
            }
        }
    }

    private List<PlanNodeData> Contained(PlanNodeData section) =>
        _board.Nodes.Where(n => n.Kind != PlanNodeData.KindSection
                                && string.Equals(n.SectionId, section.Id, StringComparison.Ordinal)).ToList();

    private static int ColumnCount(SKRect section) =>
        Math.Max(1, (int)Math.Floor((section.Width - SectionPad * 2 + SlotGap) / SlotPitch));

    private static int SpanOf(double width, int cols) =>
        Math.Clamp((int)Math.Round((width + SlotGap) / SlotPitch), 1, cols);

    private static double SnapToGrid(double v) => Math.Floor(v / GridCell + 0.5) * GridCell;
    private static double CeilToGrid(double v) => Math.Ceiling((v - 0.01) / GridCell) * GridCell;

    private static double SnapToColumns(double width) =>
        Math.Max(1, Math.Floor((width - SectionPad) / SlotPitch + 0.5)) * SlotPitch + SectionPad;

    private static SKRect Rect(double x, double y, double w, double h) =>
        SKRect.Create((float)x, (float)y, (float)w, (float)h);

    // ── sections ────────────────────────────────────────────────────────────────

    private void DrawSection(PlanNodeData n)
    {
        var r = _rect[n.Id];
        var colour = PlanColours.Parse(n.Color);
        _ops.Add(new RectOp(Inset(r, 0.7f), 12, PlanColours.Surface0, colour ?? PlanColours.BorderStrong, 1.4f,
            SectionRestOpacity));

        var title = n.Title.Length > 0 ? n.Title : "Section";
        var count = Contained(n).Count;
        var countText = count == 0 ? "· empty" : $"· {count} card{(count == 1 ? "" : "s")}";
        var titleStyle = PlanStyle.Semi(12.5f);
        var countStyle = PlanStyle.Regular(10.5f);
        var countW = _text.Measure(countText, countStyle);
        var titleMax = Math.Max(20, r.Width - SectionPad - 14 - 9 - countW);
        var shown = _text.Ellipsize(title, titleStyle, (float)titleMax);
        var x = r.Left + (float)SectionPad;
        Line(x, r.Top, (float)SectionHeaderH, shown, titleStyle, colour ?? PlanColours.Text2, out var titleW);
        Line(x + titleW + 9, r.Top + 1, (float)SectionHeaderH, countText, countStyle, PlanColours.Text3, out _);
    }

    // ── note cards ──────────────────────────────────────────────────────────────

    private void DrawNoteCard(PlanNodeData n)
    {
        var r = _rect[n.Id];
        var accent = PlanColours.Parse(n.Color);
        _ops.Add(new RectOp(Inset(r, NoteBorder / 2), 9, PlanColours.Surface2, accent ?? PlanColours.BorderStrong, NoteBorder));

        var w = r.Width;
        var titleOnly = NoteShowsTitleOnly(n, w);
        var hasBody = n.Body.Length > 0;
        var checkbox = n.Tracker == PlanNodeData.TrackerCheckbox;
        var ticked = checkbox && n.Done;
        var hasTasks = n.Tasks is { Count: > 0 };
        var cornerRoom = (float)(NoteCornerControls(n) * NoteCornerW);

        var inner = Inset(r, NoteBorder);
        var textH = (float)NoteTextH(n, w);
        var row0 = SKRect.Create(inner.Left, inner.Top, inner.Width, Math.Max(0, inner.Height - (float)NoteExtraH(n)));
        _ops.Add(new ClipOp(row0, 8));

        var x = inner.Left + 12;
        var y = inner.Top + 10;
        var headline = titleOnly ? NoteHeadline(n) : n.Title;
        if (headline.Length > 0)
        {
            var titled = n.Title.Length > 0;
            var style = titled ? PlanStyle.Semi(13.5f) : PlanStyle.Regular(12);
            var colour = ticked || !titled ? PlanColours.Text2 : PlanColours.Text;
            var right = titleOnly ? Math.Max(cornerRoom, (float)NoteGripRoom) : cornerRoom;
            var width = inner.Width - 24 - right;
            var lines = titled ? _text.Wrap(headline, style, width) : [_text.Ellipsize(headline, style, width)];
            foreach (var line in lines)
            {
                Line(x, y, style.LineHeight, line, style, colour, out _, strike: ticked);
                y += style.LineHeight;
            }
            if (!titleOnly) y += 5;
        }

        if (!titleOnly && hasBody)
        {
            var bodyRoom = n.Title.Length > 0
                ? textH - NoteTitleOnlyH(n, w) - 5
                : textH - 20;
            var maxLines = Math.Max(1, (int)Math.Floor(bodyRoom / NoteBodyLineH));
            var style = PlanStyle.Regular(12);
            var width = inner.Width - 24 - (n.Title.Length > 0 ? 0 : cornerRoom);
            foreach (var line in _text.Wrap(n.Body, style, width, maxLines))
            {
                Line(x, y, (float)NoteBodyLineH, line, style, PlanColours.Text2, out _);
                y += (float)NoteBodyLineH;
            }
        }
        _ops.Add(new UnclipOp());

        var rowTop = row0.Bottom;
        if (n.Tracker == PlanNodeData.TrackerProgress)
        {
            DrawProgress(n, inner, rowTop, accent);
            rowTop += (float)(NoteProgressH + 2);
        }
        if (hasTasks) DrawTasks(n, inner, rowTop);

        if (checkbox)
        {
            // The corner's right edge, less the box's own right margin.
            var boxRight = inner.Right - 8 - 1;
            TickBox(SKRect.Create(boxRight - 16, inner.Top + 7 + 3, 16, 16), n.Done);
        }
    }

    private void DrawProgress(PlanNodeData n, SKRect inner, float top, uint? accent)
    {
        var pct = Math.Clamp(n.Progress, 0, 100);
        var left = inner.Left + 12;
        var right = inner.Right - 26;
        var label = PlanStyle.Semi(11);
        Line(left, top, (float)NoteProgressH, $"{pct}%", label, PlanColours.Text2, out _);

        var trackLeft = left + 38;
        var track = SKRect.Create(trackLeft, top + (float)NoteProgressH / 2 - 3.5f, Math.Max(10, right - trackLeft), 7);
        _ops.Add(new RectOp(Inset(track, 0.5f), 3.5f, PlanColours.Surface3, PlanColours.Border, 1));
        if (pct > 0)
            _ops.Add(new RectOp(SKRect.Create(track.Left, track.Top, track.Width * pct / 100f, track.Height), 3.5f,
                accent ?? PlanColours.Accent, null));
    }

    private void DrawTasks(PlanNodeData n, SKRect inner, float top)
    {
        var tasks = n.Tasks!;
        var done = tasks.Count(t => t.Done);
        var left = inner.Left + 8;
        var right = inner.Right - 8;

        _ops.Add(new StrokeOp(new PlanPath().MoveTo(left, top + 0.5f).LineTo(right, top + 0.5f), PlanColours.Border, 1));
        var hx = left + 4;
        var mid = top + (float)NoteTasksHeaderH / 2;
        // Chevron: down when the list shows, right when it's folded.
        var chevron = n.TasksCollapsed
            ? new PlanPath().MoveTo(hx + 2, mid - 3.5f).LineTo(hx + 5.5f, mid).LineTo(hx + 2, mid + 3.5f)
            : new PlanPath().MoveTo(hx, mid - 2).LineTo(hx + 3.5f, mid + 1.5f).LineTo(hx + 7, mid - 2);
        _ops.Add(new StrokeOp(chevron, PlanColours.Text2, 1.3f));
        Line(hx + 16, top, (float)NoteTasksHeaderH, "Tasks", PlanStyle.Semi(11.5f), PlanColours.Text, out var tw);
        Line(hx + 16 + tw + 8, top, (float)NoteTasksHeaderH, $"{done}/{tasks.Count}", PlanStyle.Regular(11),
            done == tasks.Count ? PlanColours.AccentText : PlanColours.Text3, out _);
        if (n.TasksCollapsed) return;

        var y = top + (float)NoteTasksHeaderH;
        var style = PlanStyle.Regular(12);
        foreach (var task in tasks)
        {
            var boxLeft = left + 4;
            TickBox(SKRect.Create(boxLeft, y + ((float)NoteTaskRowH - 14) / 2, 14, 14), task.Done);
            var textLeft = boxLeft + 14 + 8;
            var text = _text.Ellipsize(task.Text, style, right - textLeft);
            Line(textLeft, y, (float)NoteTaskRowH, text, style, task.Done ? PlanColours.Text3 : PlanColours.Text2, out _,
                strike: task.Done);
            y += (float)NoteTaskRowH;
        }
    }

    private void TickBox(SKRect box, bool done)
    {
        var radius = box.Width / 4;
        if (done)
        {
            _ops.Add(new RectOp(Inset(box, 0.6f), radius, PlanColours.Accent, PlanColours.Accent, 1.2f));
            var s = box.Width;
            var check = new PlanPath()
                .MoveTo(box.Left + s * 0.25f, box.Top + s * 0.52f)
                .LineTo(box.Left + s * 0.43f, box.Top + s * 0.70f)
                .LineTo(box.Left + s * 0.76f, box.Top + s * 0.33f);
            _ops.Add(new StrokeOp(check, PlanColours.OnAccent, Math.Max(1.4f, s / 9)));
        }
        else
        {
            _ops.Add(new RectOp(Inset(box, 0.6f), radius, PlanColours.Surface3, PlanColours.BorderStrong, 1.2f));
        }
    }

    // ── mod, group and placeholder cards ────────────────────────────────────────

    private void DrawModCard(PlanNodeData n)
    {
        var r = _rect[n.Id];
        var label = n.ModKey is { } key && _mods.TryGetValue(key, out var found) ? found : null;
        var name = label?.Name ?? ShortKey(n.ModKey);
        _ops.Add(new RectOp(Inset(r, 0.5f), 9, label is null ? PlanColours.Surface2 : PlanColours.Surface3,
            label is null ? PlanColours.Border : PlanColours.BorderStrong, 1));
        var textLeft = IconSlot(r, name, label?.IconUrl, PlanColours.Text2);
        TwoLines(r, textLeft, name, label?.Meta ?? "Mod", label is null ? PlanColours.Text2 : PlanColours.Text);
    }

    private void DrawPlaceholderCard(PlanNodeData n)
    {
        var r = _rect[n.Id];
        var status = n.Placeholder?.Status;
        var dropped = PlaceholderStatus.Normalize(status) == "dropped";
        var statusColour = PlaceholderStatus.Colour(status);
        _ops.Add(new RectOp(Inset(r, 0.7f), 9, PlanColours.Canvas, PlanColours.WithAlpha(statusColour, 0.75), 1.4f,
            Dash: [5, 4]));

        var name = n.Title.Length > 0 ? n.Title : ShortKey(n.ModKey);
        var textLeft = IconSlot(r, name, n.Placeholder?.IconUrl, PlanColours.Text3);
        var width = r.Right - 1 - 10 - textLeft;

        var nameStyle = PlanStyle.Semi(13);
        var pillStyle = PlanStyle.Semi(10);
        var hasNote = n.Body.Length > 0;
        var blockH = nameStyle.LineHeight + 3 + 18 + (hasNote ? 4 + 15 : 0);
        var y = r.Top + (r.Height - blockH) / 2;

        Line(textLeft, y, nameStyle.LineHeight, _text.Ellipsize(name, nameStyle, width), nameStyle,
            dropped ? PlanColours.Text3 : PlanColours.Text, out _, strike: dropped);
        y += nameStyle.LineHeight + 3;

        // The status pill: a dot in the status colour and its name.
        var labelText = PlaceholderStatus.Label(status);
        var labelW = _text.Measure(labelText, pillStyle);
        var pill = SKRect.Create(textLeft, y, Math.Min(width, 7 + 6 + 5 + labelW + 8), 18);
        _ops.Add(new RectOp(Inset(pill, 0.5f), 9, PlanColours.Surface3, PlanColours.Border, 1));
        _ops.Add(new CircleOp(pill.Left + 7 + 3, pill.MidY, 3, statusColour));
        Line(pill.Left + 7 + 6 + 5, y, 18, labelText, pillStyle, PlanColours.Text2, out _);
        var meta = PlaceholderMeta(n);
        if (meta.Length > 0 && pill.Right + 7 < textLeft + width)
        {
            var metaStyle = PlanStyle.Regular(11);
            Line(pill.Right + 7, y, 18, _text.Ellipsize(meta, metaStyle, textLeft + width - pill.Right - 7), metaStyle,
                PlanColours.Text3, out _);
        }
        y += 18;
        if (hasNote)
        {
            var noteStyle = PlanStyle.Italic(11);
            var first = n.Body.Replace("\r\n", "\n").Split('\n', 2)[0].Trim();
            Line(textLeft, y + 4, 15, _text.Ellipsize(first, noteStyle, width), noteStyle, PlanColours.Text2, out _);
        }
    }

    private static string PlaceholderMeta(PlanNodeData n)
    {
        if (n.Placeholder?.Link is { Length: > 0 } link && Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return "";
    }

    private void DrawGroupCard(PlanNodeData n)
    {
        var r = _rect[n.Id];
        var accent = PlanColours.Parse(n.Color) ?? PlanColours.AccentText;
        _ops.Add(new RectOp(Inset(r, 0.7f), 9, PlanColours.Surface2, accent, 1.4f));

        var left = r.Left + 1.4f + 11;
        var width = r.Width - 2.8f - 11 - 8;
        var title = n.Title.Length > 0 ? n.Title : n.Query?.Title ?? "Group";
        var titleStyle = PlanStyle.Semi(13);
        var y = r.Top + 1.4f + 9;
        Line(left, y, titleStyle.LineHeight, _text.Ellipsize(title, titleStyle, width), titleStyle, PlanColours.Text, out _);
        y += titleStyle.LineHeight;
        var sub = $"{n.Query?.PropertyLabel ?? "Group"}  ·  live group";
        var subStyle = PlanStyle.Regular(10);
        Line(left, y, subStyle.LineHeight, _text.Ellipsize(sub, subStyle, width), subStyle, PlanColours.Text3, out _);
        y += subStyle.LineHeight + 7;
        _ops.Add(new RectOp(SKRect.Create(left, y, r.Width - 2.8f - 22, 2), 1, PlanColours.WithAlpha(accent, 0.7), null));
        if (n.Collapsed) return;
        var rowStyle = PlanStyle.Italic(11);
        Line(left + 2, y + 2 + 5, (float)GroupRowH, _text.Ellipsize("Members show in CloudLauncher", rowStyle, width),
            rowStyle, PlanColours.Text3, out _);
    }

    /// <summary>The 30-pixel icon square at a card's left (the icon, else the name's initial) and
    /// where the text beside it starts.</summary>
    private float IconSlot(SKRect r, string name, string? iconUrl, uint initialColour)
    {
        var icon = SKRect.Create(r.Left + 1 + 9, r.MidY - 15, 30, 30);
        _ops.Add(new RectOp(Inset(icon, 0.5f), 7, PlanColours.Surface2, PlanColours.Border, 1));
        var initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        var style = PlanStyle.Semi(12);
        var w = _text.Measure(initial, style);
        Line(icon.MidX - w / 2, icon.Top, 30, initial, style, initialColour, out _);
        if (!string.IsNullOrWhiteSpace(iconUrl)) _ops.Add(new ImageOp(Inset(icon, 1), iconUrl, 6));
        return icon.Right + 9;
    }

    private void TwoLines(SKRect r, float left, string name, string meta, uint nameColour)
    {
        var width = r.Right - 1 - 10 - left;
        var nameStyle = PlanStyle.Semi(13);
        var metaStyle = PlanStyle.Regular(11);
        var blockH = nameStyle.LineHeight + metaStyle.LineHeight;
        var y = r.MidY - blockH / 2;
        Line(left, y, nameStyle.LineHeight, _text.Ellipsize(name, nameStyle, width), nameStyle, nameColour, out _);
        Line(left, y + nameStyle.LineHeight, metaStyle.LineHeight, _text.Ellipsize(meta, metaStyle, width), metaStyle,
            PlanColours.Text2, out _);
    }

    private static string ShortKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "Unknown mod";
        var i = key.IndexOf(':');
        var tail = i >= 0 && i < key.Length - 1 ? key[(i + 1)..] : key;
        return tail.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? tail[..^4] : tail;
    }

    // ── links ───────────────────────────────────────────────────────────────────

    private void DrawEdge(PlanEdgeData edge)
    {
        if (!_rect.TryGetValue(edge.FromId, out var from) || !_rect.TryGetValue(edge.ToId, out var to)) return;

        (SKPoint P, SKPoint N)? pa = edge.FromAnchor is { } fa ? AnchorOnCard(from, fa) : null;
        (SKPoint P, SKPoint N)? pb = edge.ToAnchor is { } ta ? AnchorOnCard(to, ta) : null;
        var start = pa?.P ?? BorderPoint(from, pb?.P ?? Centre(to));
        var end = pb?.P ?? BorderPoint(to, pa?.P ?? Centre(from));
        var len = Distance(start, end);
        if (len < 2) return;

        var colour = PlanColours.Parse(edge.Color);
        var stroke = colour ?? PlanColours.Edge;
        var (path, tangent, mid) = Curve(start, pa?.N, end, pb?.N);
        float[]? dash = edge.Style == 2 ? [5 * 1.8f, 4 * 1.8f] : null;
        _ops.Add(new StrokeOp(path, stroke, 1.8f, dash));
        Grow(path.Bounds());

        if (edge.Style == 0)
        {
            var arrowLen = Math.Clamp(len * 0.3f, 5, 10);
            var arrowW = arrowLen * 0.52f;
            var back = new SKPoint(end.X - tangent.X * arrowLen, end.Y - tangent.Y * arrowLen);
            var ap = new SKPoint(-tangent.Y, tangent.X);
            var head = new PlanPath()
                .MoveTo(end.X, end.Y)
                .LineTo(back.X + ap.X * arrowW, back.Y + ap.Y * arrowW)
                .LineTo(back.X - ap.X * arrowW, back.Y - ap.Y * arrowW)
                .Close();
            _ops.Add(new FillOp(head, colour ?? PlanColours.Arrow));
        }

        if (!string.IsNullOrWhiteSpace(edge.Label))
        {
            var style = PlanStyle.Regular(10.5f);
            var text = _text.Ellipsize(edge.Label.Trim(), style, 150);
            var tw = _text.Measure(text, style);
            var chip = SKRect.Create(mid.X - (tw + 12 + 2) / 2, mid.Y - (style.LineHeight + 4 + 2) / 2,
                tw + 12 + 2, style.LineHeight + 4 + 2);
            _ops.Add(new RectOp(Inset(chip, 0.5f), 5, PlanColours.Surface3, stroke, 1));
            Line(chip.Left + 7, chip.Top + 3, style.LineHeight, text, style, PlanColours.Text2, out _);
            Grow(chip);
        }
    }

    private static (SKPoint P, SKPoint N) AnchorOnCard(SKRect r, PlanAnchorData a)
    {
        var q = new SKPoint(r.Left + (float)Math.Clamp(a.X, 0, 1) * r.Width, r.Top + (float)Math.Clamp(a.Y, 0, 1) * r.Height);
        float left = q.X - r.Left, right = r.Right - q.X, top = q.Y - r.Top, bottom = r.Bottom - q.Y;
        var side = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        var normal = side == left ? new SKPoint(-1, 0) : side == right ? new SKPoint(1, 0)
            : side == top ? new SKPoint(0, -1) : new SKPoint(0, 1);
        if (normal.X != 0) q.X = normal.X < 0 ? r.Left : r.Right;
        else q.Y = normal.Y < 0 ? r.Top : r.Bottom;
        return (new SKPoint(q.X + normal.X * 3, q.Y + normal.Y * 3), normal);
    }

    private static SKPoint Centre(SKRect r) => new(r.MidX, r.MidY);

    private static SKPoint BorderPoint(SKRect r, SKPoint toward)
    {
        var c = Centre(r);
        double dx = toward.X - c.X, dy = toward.Y - c.Y;
        if (dx == 0 && dy == 0) return c;
        var tx = dx != 0 ? (r.Width / 2 + 3) / Math.Abs(dx) : double.MaxValue;
        var ty = dy != 0 ? (r.Height / 2 + 3) / Math.Abs(dy) : double.MaxValue;
        var t = Math.Min(tx, ty);
        return new SKPoint((float)(c.X + dx * t), (float)(c.Y + dy * t));
    }

    /// <summary><c>ModPlanView.EdgeShape</c>: a gentle single bend between automatic ends, and a cubic
    /// that leaves a pinned end square to its side of the card.</summary>
    private static (PlanPath Path, SKPoint Tangent, SKPoint Mid) Curve(SKPoint start, SKPoint? startNormal,
        SKPoint end, SKPoint? endNormal)
    {
        var dir = new SKPoint(end.X - start.X, end.Y - start.Y);
        var len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        SKPoint tangent, mid;
        PlanPath path;
        if (startNormal is null && endNormal is null)
        {
            var perp = new SKPoint(-dir.Y, dir.X);
            if (len > 0) perp = new SKPoint(perp.X / len, perp.Y / len);
            var offset = len < 110 ? 0 : Math.Min(38, len * 0.13f);
            var ctrl = new SKPoint((start.X + end.X) / 2 + perp.X * offset, (start.Y + end.Y) / 2 + perp.Y * offset);
            tangent = offset == 0 ? dir : new SKPoint(end.X - ctrl.X, end.Y - ctrl.Y);
            mid = new SKPoint(0.25f * start.X + 0.5f * ctrl.X + 0.25f * end.X, 0.25f * start.Y + 0.5f * ctrl.Y + 0.25f * end.Y);
            path = new PlanPath().MoveTo(start.X, start.Y).QuadTo(ctrl.X, ctrl.Y, end.X, end.Y);
        }
        else
        {
            var reach = Math.Clamp(len * 0.4f, 24, 120);
            var c1 = startNormal is { } n1 ? new SKPoint(start.X + n1.X * reach, start.Y + n1.Y * reach)
                : new SKPoint(start.X + dir.X / 3, start.Y + dir.Y / 3);
            var c2 = endNormal is { } n2 ? new SKPoint(end.X + n2.X * reach, end.Y + n2.Y * reach)
                : new SKPoint(end.X - dir.X / 3, end.Y - dir.Y / 3);
            tangent = new SKPoint(end.X - c2.X, end.Y - c2.Y);
            mid = new SKPoint(0.125f * start.X + 0.375f * (c1.X + c2.X) + 0.125f * end.X,
                0.125f * start.Y + 0.375f * (c1.Y + c2.Y) + 0.125f * end.Y);
            path = new PlanPath().MoveTo(start.X, start.Y).CubicTo(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y);
        }
        var tl = MathF.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
        if (tl < 0.001f) { tangent = dir; tl = len; }
        if (tl > 0) tangent = new SKPoint(tangent.X / tl, tangent.Y / tl);
        return (path, tangent, mid);
    }

    private static float Distance(SKPoint a, SKPoint b) => MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>A line of text vertically centred in a box of <paramref name="boxH"/> starting at
    /// <paramref name="top"/>.</summary>
    private void Line(float x, float top, float boxH, string text, PlanStyle style, uint colour, out float width,
        bool strike = false)
    {
        width = _text.Measure(text, style);
        if (text.Length == 0) return;
        var baseline = top + (boxH - style.LineHeight) / 2 + _text.BaselineOffset(style);
        _ops.Add(new TextOp(x, baseline, text, style, colour, width, strike));
    }

    private static SKRect Inset(SKRect r, float by) => SKRect.Create(r.Left + by, r.Top + by,
        Math.Max(0, r.Width - by * 2), Math.Max(0, r.Height - by * 2));

    private void Grow(SKRect r)
    {
        if (r.IsEmpty) return;
        _bounds = _bounds.IsEmpty ? r : SKRect.Union(_bounds, r);
    }
}
