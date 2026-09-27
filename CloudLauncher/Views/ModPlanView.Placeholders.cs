using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

// Placeholder cards: mods the pack doesn't have yet (still to be found, or ported), kept on the board
// beside the real ones so a port-in-progress can be planned as a whole. They move, snap, link, copy,
// select and undo like any card; what is theirs is here: the card, its menu, the add/edit card, the
// toolbar's "to find" list, and swapping in the real mod card once the mod is installed.
public partial class ModPlanView
{
    // A mod card's footprint, so a placeholder turns into one without its neighbours moving: 9 x 3
    // cells, or 9 x 4 with a line for its note or its "in the pack now" prompt.
    private const double PlaceholderW = 216, PlaceholderH = 72, PlaceholderInfoH = 96;

    /// <summary>Placeholders whose mod is in the pack now, by card id, worked out at the start of each
    /// rebuild (see <see cref="RefreshPlaceholderMatches"/>).</summary>
    private readonly Dictionary<string, PackMod> _placeholderMatches = new();

    /// <summary>Matches the status line has already pointed out, so it names each one once rather than
    /// on every reload of the mods.</summary>
    private readonly HashSet<string> _announcedMatches = new();

    /// <summary>One dashed outline per colour, for this rebuild (the theme's colour can change between
    /// rebuilds, which is why they aren't kept longer).</summary>
    private readonly Dictionary<Color, Brush> _dashBrushes = new();

    /// <summary>The pack's Minecraft version and loader tag ("neoforge"), for "is there a version?"
    /// checks. Null when the host didn't say, which hides the check.</summary>
    private string? _mcVersion, _loader;

    private IEnumerable<PlanNode> PlaceholderNodes =>
        _board?.Nodes.Where(n => n.Kind == PlanNodeKind.Placeholder) ?? Enumerable.Empty<PlanNode>();

    private static string StatusOf(PlanNode n) => PlanPlaceholderStatus.Normalize(n.Placeholder?.Status);

    private static string PlaceholderName(PlanNode n) => n.Title.Length > 0 ? n.Title : "Unnamed mod";

    // ── matching installed mods ─────────────────────────────────────────────────

    /// <summary>Finds which placeholders the pack now has the mod for. Runs before the cards are
    /// sized, since a match adds the prompt line.</summary>
    /// <remarks>Only while the board has placeholders; the matcher is a few dictionary fills over the
    /// mods, then a handful of lookups per placeholder.</remarks>
    private void RefreshPlaceholderMatches()
    {
        _placeholderMatches.Clear();
        _dashBrushes.Clear();
        if (!PlaceholderNodes.Any()) return;
        var matcher = new PlanPlaceholderMatcher(_mods);
        foreach (var n in PlaceholderNodes)
            if (matcher.Find(n) is { } mod) _placeholderMatches[n.Id] = mod;
    }

    /// <summary>After the mods were reloaded: says so once when a placeholder's mod has turned up in
    /// the pack, pointing at the card's Replace.</summary>
    private void AnnouncePlaceholderMatches()
    {
        var fresh = _placeholderMatches.Where(kv => _announcedMatches.Add(kv.Key)).ToList();
        _announcedMatches.IntersectWith(_placeholderMatches.Keys); // a match that went away can be announced again
        if (fresh.Count == 0) return;
        PlanStatus.ToolTip = null;
        PlanStatus.Text = fresh.Count == 1
            ? $"{fresh[0].Value.DisplayName} is in the pack now - Replace on its placeholder swaps in the real card"
            : $"{fresh.Count} placeholders are in the pack now - use Replace on each, or the to-find list";
    }

    // ── the card ────────────────────────────────────────────────────────────────

    private (double w, double h) PlaceholderSize(PlanNode n, bool grid)
    {
        var h = _placeholderMatches.ContainsKey(n.Id) || n.Body.Length > 0 ? PlaceholderInfoH : PlaceholderH;
        // Kept current for older launchers, which size a kind they don't know from W and H (see
        // PlanNodeKind.Placeholder). Written with the next save; nothing here reads it back.
        n.H = h;
        return (CardW(n, PlaceholderW, grid), h);
    }

    /// <summary>A placeholder: hollow (the board's own colour inside a dashed outline) where a mod
    /// card is solid, with a status pill where a mod card shows its version. Once the mod is in the
    /// pack a line offers to swap the real card in.</summary>
    private Border CreatePlaceholderCard(PlanNode node)
    {
        var ph = node.Placeholder;
        var status = StatusOf(node);
        var dropped = status == PlanPlaceholderStatus.Dropped;
        _placeholderMatches.TryGetValue(node.Id, out var match);

        var card = new CloudLauncher.Controls.SlateBorder
        {
            CornerRadius = new CornerRadius(9),
            Background = Res("Surface1Brush"),
            BorderBrush = DashBrush(node),
            BorderThickness = new Thickness(1.4),
            Cursor = Cursors.SizeAll,
            ToolTip = Tip(PlaceholderTooltip(node, match))
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The icon slot: the attached project's icon if there is one, else the name's initial, both
        // on the board colour so the slot reads as empty-ish like the card.
        var icon = new CloudLauncher.Controls.SlateBorder
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(7),
            Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1),
            Margin = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true
        };
        var iconGrid = new Grid();
        iconGrid.Children.Add(new TextBlock
        {
            Text = PlaceholderName(node)[..1].ToUpperInvariant(), FontWeight = FontWeights.SemiBold, FontSize = 12,
            Foreground = Res("TextTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        TryAddIcon(iconGrid, ph?.IconUrl);
        icon.Child = iconGrid;
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        text.Children.Add(new TextBlock
        {
            Text = PlaceholderName(node), FontWeight = FontWeights.SemiBold, FontSize = 13,
            Foreground = Res(dropped ? "TextTertiaryBrush" : "TextPrimaryBrush"),
            TextDecorations = dropped ? TextDecorations.Strikethrough : null,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var statusRow = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.Children.Add(StatusPill(node));
        var meta = new TextBlock
        {
            Text = PlaceholderMeta(node), FontSize = 11, Foreground = Res("TextTertiaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        };
        Grid.SetColumn(meta, 1);
        statusRow.Children.Add(meta);
        text.Children.Add(statusRow);

        if (match is not null) text.Children.Add(MatchRow(node, match));
        else if (node.Body.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = node.Body.Split('\n', 2)[0].Trim(), FontSize = 11, FontStyle = FontStyles.Italic,
                Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0)
            });

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        AddConnector(grid, card, node, 1);
        card.Child = grid;

        card.MouseRightButtonUp += (_, e) => { e.Handled = true; SelectForMenu(node); ShowPlaceholderMenu(node, card); };
        return card;
    }

    /// <summary>The status as a pill with its coloured dot. Clicking it opens the status list, the
    /// quickest way to move a placeholder along.</summary>
    private FrameworkElement StatusPill(PlanNode node)
    {
        var status = StatusOf(node);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Ellipse
        {
            Width = 6, Height = 6, Margin = new Thickness(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = AccentPalette.Brush(PlanPlaceholderStatus.Hex(status), Res("TextTertiaryBrush"))
        });
        content.Children.Add(new TextBlock { Text = PlanPlaceholderStatus.Label(status), Style = (Style)FindResource("PillText") });

        var pill = new CloudLauncher.Controls.SlateBorder
        {
            Style = (Style)FindResource("Pill"),
            Padding = new Thickness(7, 1, 8, 1),
            Cursor = Cursors.Hand,
            Tag = NoAnchorTag,
            ToolTip = new ToolTip { Content = "Click to change the status" },
            Child = content
        };
        // Its own press, so a click never starts dragging the card.
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SelectOnly(node.Id);
            var menu = new ContextMenu { PlacementTarget = pill };
            AddStatusItems(menu.Items, node);
            menu.IsOpen = true;
        };
        return pill;
    }

    /// <summary>"In the pack: Sodium · Replace": the offer to turn the placeholder into the real card.</summary>
    private FrameworkElement MatchRow(PlanNode node, PackMod mod)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = "In the pack: " + mod.DisplayName, FontSize = 11, Foreground = Res("AccentTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        });
        var replace = new Button
        {
            Style = (Style)FindResource("LinkButton"), Content = "Replace", FontSize = 11,
            MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            ToolTip = $"Turn this placeholder into {mod.DisplayName}'s card, keeping its spot and its links"
        };
        replace.Click += (_, _) => ReplaceWith(node, mod);
        Grid.SetColumn(replace, 1);
        row.Children.Add(replace);
        return row;
    }

    /// <summary>The line beside the status: the last version check while it's still about this pack,
    /// else where the link goes, else nothing.</summary>
    private string PlaceholderMeta(PlanNode node)
    {
        var ph = node.Placeholder;
        if (_mcVersion is not null && ph?.CheckedFound is { } found
            && string.Equals(ph.CheckedFor, PlaceholderStores.Target(_mcVersion, _loader), StringComparison.OrdinalIgnoreCase))
            return found ? $"has {PlaceholderStores.TargetLabel(_mcVersion, _loader)}" : $"no {PlaceholderStores.TargetLabel(_mcVersion, _loader)} yet";
        if (SafeLaunch.IsWebUrl(ph?.Link, out var uri) && uri is not null)
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return ""; // the dashed outline already says "not in the pack"
    }

    private string PlaceholderTooltip(PlanNode node, PackMod? match)
    {
        var ph = node.Placeholder;
        var lines = new List<string> { $"{PlaceholderName(node)} - not in the pack yet  ·  {PlanPlaceholderStatus.Label(StatusOf(node))}" };
        if (ph?.Link is { Length: > 0 } link) lines.Add(link);
        if (_mcVersion is not null && ph?.CheckedFound is { } found
            && string.Equals(ph.CheckedFor, PlaceholderStores.Target(_mcVersion, _loader), StringComparison.OrdinalIgnoreCase))
            lines.Add(PlaceholderStores.Answer(found, _mcVersion, _loader, ph.CheckedAt));
        if (node.Body.Length > 0) lines.Add("\n" + node.Body);
        if (match is not null) lines.Add($"\n{match.DisplayName} is in the pack now: Replace swaps in its card.");
        lines.Add("\nDouble-click to edit  ·  click the status to change it  ·  right-click for more");
        return string.Join("\n", lines);
    }

    /// <summary>The dashed outline that marks a placeholder.</summary>
    /// <remarks>A checkerboard of 4-unit squares used as the border's brush: a border thinner than a
    /// square crosses a single row (or column) of them along each side, which reads as dashes. Unlike a
    /// dashed shape laid over the card, it follows the card's real outline in both looks (Slate's
    /// stepped corners too), and selection recolours it like any card's border. Frozen and shared by
    /// every placeholder of a colour, so a board of them costs one small tile, not a dashed stroke per
    /// card to re-tessellate on every zoom step.</remarks>
    private Brush DashBrush(PlanNode node)
    {
        var fallback = (Res("TextTertiaryBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x8A, 0x94, 0xA6);
        var color = (ColorOf(node, Brushes.Transparent) as SolidColorBrush) is { Color.A: > 0 } custom ? custom.Color : fallback;
        if (_dashBrushes.TryGetValue(color, out var cached)) return cached;

        var ink = new SolidColorBrush(color);
        ink.Freeze();
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 4, 4)));
        squares.Children.Add(new RectangleGeometry(new Rect(4, 4, 4, 4)));
        var brush = new DrawingBrush(new GeometryDrawing(ink, null, squares))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        _dashBrushes[color] = brush;
        return brush;
    }

    // ── menus ─────────────────────────────────────────────────────────────────

    private void ShowPlaceholderMenu(PlanNode node, FrameworkElement anchor) =>
        BuildPlaceholderMenu(node, anchor).IsOpen = true;

    private ContextMenu BuildPlaceholderMenu(PlanNode node, FrameworkElement anchor)
    {
        _placeholderMatches.TryGetValue(node.Id, out var match);
        var link = node.Placeholder?.Link;
        var menu = new ContextMenu { PlacementTarget = anchor };

        menu.Items.Add(Item("Edit placeholder...", () => _ = EditPlaceholderAsync(node)));
        var statusParent = new MenuItem { Header = "Status" };
        AddStatusItems(statusParent.Items, node);
        menu.Items.Add(statusParent);
        menu.Items.Add(Item("Open link", () => { if (!SafeLaunch.OpenUrl(link)) PlanStatus.Text = "That link can't be opened."; },
            enabled: SafeLaunch.IsWebUrl(link, out _)));
        if (_mcVersion is not null)
            menu.Items.Add(Item($"Is there a {PlaceholderStores.TargetLabel(_mcVersion, _loader)} version?",
                () => _ = CheckPlaceholderVersionAsync(node),
                enabled: PlaceholderStores.ProjectOf(node.ModKey, link) is not null));

        menu.Items.Add(new Separator());
        if (match is not null)
            menu.Items.Add(Item($"Replace with {match.DisplayName}", () => ReplaceWith(node, match)));
        menu.Items.Add(Item("Link to a mod in the pack...", () => _ = LinkPlaceholderToModAsync(node), enabled: _mods.Count > 0));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Draw arrow from here", () => BeginPendingArrow(node)));
        menu.Items.Add(ColorMenu(node.Color, hex => { node.Color = hex; Commit(); Rebuild(); }));
        menu.Items.Add(Item("Duplicate", () =>
        {
            if (_board is null) return;
            var copy = node.Clone();
            var (x, y) = ShownAt(node);
            copy.X = Place(x + GridCell);
            copy.Y = Place(y + GridCell);
            _board.Nodes.Add(copy);
            Commit();
            Rebuild();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete placeholder", () => RemoveNode(node), gesture: _selected.Count > 1 ? null : "Del"));
        if (MultiDeleteItem(node) is { } multi) menu.Items.Add(multi);
        return menu;
    }

    private void AddStatusItems(ItemCollection items, PlanNode node)
    {
        var current = StatusOf(node);
        foreach (var status in PlanPlaceholderStatus.All)
        {
            var item = Radio(PlanPlaceholderStatus.Label(status), status == current, () => SetPlaceholderStatus(node, status));
            item.Icon = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = AccentPalette.Brush(PlanPlaceholderStatus.Hex(status), Res("TextTertiaryBrush"))
            };
            items.Add(item);
        }
    }

    private void SetPlaceholderStatus(PlanNode node, string status)
    {
        (node.Placeholder ??= new PlanPlaceholder()).Status = status;
        Commit();
        Rebuild();
        PlanStatus.Text = $"{PlaceholderName(node)}: {PlanPlaceholderStatus.Label(status)}.";
    }

    // ── adding and editing ────────────────────────────────────────────────────

    private void OnAddPlaceholder(object sender, RoutedEventArgs e) => _ = AddPlaceholderAsync(ViewCenter());

    /// <summary>Asks for a placeholder's details and drops it at <paramref name="at"/> (the view centre
    /// from the toolbar, the click point from the board's menu, a section's next slot from its menu).</summary>
    private async Task AddPlaceholderAsync(Point at)
    {
        if (_board is null) return;
        var draft = await PlaceholderCardAsync(new ModPlaceholderDialog.Draft(), isNew: true);
        if (draft is null || _board is null) return;

        var h = draft.Note.Length > 0 ? PlaceholderInfoH : PlaceholderH;
        var (x, y) = ResolveNewCardPosition(at, PlaceholderW, h, out var sectionId);
        var node = new PlanNode
        {
            Kind = PlanNodeKind.Placeholder, X = x, Y = y, W = PlaceholderW, H = h, SectionId = sectionId,
            Placeholder = new PlanPlaceholder()
        };
        ApplyDraft(node, draft);
        _board.Nodes.Add(node);
        Commit();
        Rebuild();
        SelectOnly(node.Id);
        _announcedMatches.Add(node.Id); // the line below says it, so a reload doesn't repeat it
        PlanStatus.Text = _placeholderMatches.TryGetValue(node.Id, out var mod)
            ? $"{mod.DisplayName} is already in the pack - Replace on the card swaps in its real card."
            : $"Added a placeholder for {PlaceholderName(node)}.";
    }

    private async Task EditPlaceholderAsync(PlanNode node)
    {
        var ph = node.Placeholder;
        var draft = await PlaceholderCardAsync(new ModPlaceholderDialog.Draft
        {
            Name = node.Title, Status = StatusOf(node), Link = ph?.Link ?? "", Note = node.Body,
            ProjectKey = node.ModKey, IconUrl = ph?.IconUrl, ProjectName = node.ModKey is null ? null : node.Title,
            CheckedFor = ph?.CheckedFor, CheckedFound = ph?.CheckedFound, CheckedAt = ph?.CheckedAt
        }, isNew: false);
        if (draft is null || _board?.Node(node.Id) is not { } live) return; // cancelled, or the card went meanwhile
        ApplyDraft(live, draft);
        Commit();
        Rebuild();
    }

    private async Task<ModPlaceholderDialog.Draft?> PlaceholderCardAsync(ModPlaceholderDialog.Draft draft, bool isNew)
    {
        if (_owner is null) return null;
        return await ModPlaceholderDialog.ShowAsync(_owner, draft, isNew, _mcVersion, _loader);
    }

    private static void ApplyDraft(PlanNode node, ModPlaceholderDialog.Draft d)
    {
        var ph = node.Placeholder ??= new PlanPlaceholder();
        node.Title = d.Name.Trim();
        node.Body = d.Note.TrimEnd();
        node.ModKey = d.ProjectKey;
        ph.Status = PlanPlaceholderStatus.Normalize(d.Status);
        ph.Link = string.IsNullOrWhiteSpace(d.Link) ? null : d.Link.Trim();
        ph.IconUrl = d.ProjectKey is null ? null : d.IconUrl;
        ph.CheckedFor = d.CheckedFor;
        ph.CheckedFound = d.CheckedFound;
        ph.CheckedAt = d.CheckedAt;
    }

    /// <summary>The card menu's version check: one request to the store the placeholder points at. A
    /// yes moves an open placeholder to Found (one undo step with the answer).</summary>
    private async Task CheckPlaceholderVersionAsync(PlanNode node)
    {
        if (_mcVersion is not { } mc || PlaceholderStores.ProjectOf(node.ModKey, node.Placeholder?.Link) is not { } target) return;
        var label = PlaceholderStores.TargetLabel(mc, _loader);
        PlanStatus.Text = $"Asking {(target.Source == ModSource.CurseForge ? "CurseForge" : "Modrinth")} about {PlaceholderName(node)}...";
        try
        {
            var found = await PlaceholderStores.HasVersionAsync(target.Source, target.IdOrSlug, mc, _loader);
            if (_board?.Node(node.Id) is not { } live) return;
            var ph = live.Placeholder ??= new PlanPlaceholder();
            ph.CheckedFor = PlaceholderStores.Target(mc, _loader);
            ph.CheckedFound = found;
            ph.CheckedAt = DateTimeOffset.Now;
            var marked = found && PlanPlaceholderStatus.IsOpen(ph.Status);
            if (marked) ph.Status = PlanPlaceholderStatus.Found;
            Commit();
            Rebuild();
            PlanStatus.Text = found
                ? $"{PlaceholderName(live)} has a {label} version{(marked ? " - marked it Found" : "")}."
                : $"No {label} version of {PlaceholderName(live)} yet.";
        }
        catch (Exception ex)
        {
            PlanStatus.Text = "Could not check: " + ex.Message;
        }
    }

    // ── turning into the real card ────────────────────────────────────────────

    /// <summary>Replaces one placeholder with its mod's card, as one undo step.</summary>
    private void ReplaceWith(PlanNode node, PackMod mod)
    {
        if (_board?.Node(node.Id) is not { } live) return;
        var noteMoved = ReplacePlaceholder(live, mod);
        Commit();
        Rebuild();
        SelectOnly(_board?.Nodes.FirstOrDefault(n => n.Kind == PlanNodeKind.Mod && n.ModKey is not null
                                                    && mod.CandidateKeys.Contains(n.ModKey, StringComparer.OrdinalIgnoreCase))?.Id);
        if (noteMoved) _onModsChanged?.Invoke();
        PlanStatus.Text = $"Replaced the placeholder with {mod.DisplayName}" + (noteMoved ? " - its note is on the mod now." : ".");
    }

    /// <summary>Turns a placeholder into <paramref name="mod"/>'s card in place: same id (so every link
    /// stays), spot, width, section and colour. When the board already has a card for that mod, that
    /// card takes over the placeholder's links instead and the placeholder goes. The placeholder's
    /// note moves onto the mod's own note (added below one it already has), since a mod card shows
    /// the mod's note rather than one of its own. Returns whether a note moved.</summary>
    /// <remarks>Changes only; the caller commits and rebuilds, so replacing several is one undo step.
    /// The mod note is pack data outside the board, so undo brings the placeholder back but leaves the
    /// note on the mod.</remarks>
    private bool ReplacePlaceholder(PlanNode node, PackMod mod)
    {
        if (_board is null) return false;
        var note = node.Body.Trim();

        var existing = _board.Nodes.FirstOrDefault(n => n.Kind == PlanNodeKind.Mod && n.ModKey is not null
                                                        && mod.CandidateKeys.Contains(n.ModKey, StringComparer.OrdinalIgnoreCase));
        if (existing is not null)
        {
            foreach (var edge in _board.Edges.Where(e => e.FromId == node.Id || e.ToId == node.Id).ToList())
            {
                var from = edge.FromId == node.Id ? existing.Id : edge.FromId;
                var to = edge.ToId == node.Id ? existing.Id : edge.ToId;
                // A pair links only once, and never to itself.
                if (from == to || _board.Edges.Any(x => !ReferenceEquals(x, edge)
                        && ((x.FromId == from && x.ToId == to) || (x.FromId == to && x.ToId == from))))
                {
                    _board.Edges.Remove(edge);
                    continue;
                }
                edge.FromId = from;
                edge.ToId = to;
            }
            _board.Nodes.Remove(node);
            _selected.Remove(node.Id);
        }
        else
        {
            node.Kind = PlanNodeKind.Mod;
            node.ModKey = mod.Key;
            node.Placeholder = null;
            node.Title = "";
            node.Body = "";
            node.H = 0;
        }
        _placeholderMatches.Remove(node.Id);

        if (note.Length == 0) return false;
        if (mod.Note is { } had && had.Contains(note, StringComparison.Ordinal)) return false;
        mod.Meta.Note = mod.Note is { } old ? old + "\n\n" + note : note;
        App.State.ModInventory.SaveMeta(_packId, mod);
        return true;
    }

    /// <summary>"Link to a mod in the pack...": for when the names differ too much to match by
    /// themselves (a port renamed, a fork).</summary>
    private async Task LinkPlaceholderToModAsync(PlanNode node)
    {
        if (_owner is null) return;
        var pick = await ModPickerDialog.ShowAsync(_owner, $"Which mod is {PlaceholderName(node)}?", _mods);
        if (pick is not null) ReplaceWith(node, pick);
    }

    // ── what's left: the toolbar's list ─────────────────────────────────────────

    /// <summary>The toolbar button that counts what is still to find, shown while the board has any
    /// placeholders. Refreshed on every rebuild.</summary>
    private void UpdatePlaceholderSummary()
    {
        var all = PlaceholderNodes.ToList();
        if (all.Count == 0)
        {
            PlaceholderSummary.Visibility = Visibility.Collapsed;
            return;
        }
        var open = all.Count(n => PlanPlaceholderStatus.IsOpen(n.Placeholder?.Status));
        var inPack = _placeholderMatches.Count;
        PlaceholderSummary.Visibility = Visibility.Visible;
        PlaceholderSummary.Content = (open > 0 ? $"{open} to find" : "All found")
                                     + (inPack > 0 ? $"  ·  {inPack} in the pack" : "") + " ▾";
        PlaceholderSummary.ToolTip = Tip($"{all.Count} placeholder{(all.Count == 1 ? "" : "s")} on this board: mods the pack doesn't have yet. "
                                         + $"{open} still need{(open == 1 ? "s" : "")} a port or a find."
                                         + (inPack > 0 ? $" {inPack} {(inPack == 1 ? "is" : "are")} in the pack now." : "")
                                         + "\nClick for the list, grouped by status.");
    }

    private void OnPlaceholderSummary(object sender, RoutedEventArgs e)
    {
        if (BuildPlaceholderSummaryMenu(sender as UIElement) is { } menu) menu.IsOpen = true;
    }

    /// <summary>The to-find list: placeholders grouped by status (each one jumps to its card), the
    /// ones whose mod is in the pack now, and a plain-text copy of the lot.</summary>
    private ContextMenu? BuildPlaceholderSummaryMenu(UIElement? anchor)
    {
        var all = PlaceholderNodes.OrderBy(n => n.Y).ThenBy(n => n.X).ToList();
        if (all.Count == 0) return null;
        var menu = new ContextMenu { PlacementTarget = anchor };

        var open = all.Count(n => PlanPlaceholderStatus.IsOpen(n.Placeholder?.Status));
        menu.Items.Add(new MenuItem
        {
            Header = $"{open} still to find  ·  {all.Count - open} settled", IsEnabled = false
        });
        menu.Items.Add(new Separator());

        foreach (var status in PlanPlaceholderStatus.All)
        {
            var these = all.Where(n => StatusOf(n) == status).ToList();
            if (these.Count == 0) continue;
            var parent = new MenuItem
            {
                Header = PlanPlaceholderStatus.Label(status), InputGestureText = these.Count.ToString(),
                Icon = new Ellipse { Width = 8, Height = 8, Fill = AccentPalette.Brush(PlanPlaceholderStatus.Hex(status), Res("TextTertiaryBrush")) }
            };
            parent.Items.Add(Item(these.Count == 1 ? "Select it" : $"Select all {these.Count}", () => SelectCards(these)));
            parent.Items.Add(new Separator());
            foreach (var n in these) parent.Items.Add(Item(PlaceholderName(n), () => JumpToCard(n)));
            menu.Items.Add(parent);
        }

        var matched = all.Where(n => _placeholderMatches.ContainsKey(n.Id)).ToList();
        if (matched.Count > 0)
        {
            menu.Items.Add(new Separator());
            var parent = new MenuItem { Header = "In the pack now", InputGestureText = matched.Count.ToString() };
            foreach (var n in matched)
            {
                var mod = _placeholderMatches[n.Id];
                parent.Items.Add(Item($"Replace {PlaceholderName(n)} with {mod.DisplayName}", () => ReplaceWith(n, mod)));
            }
            menu.Items.Add(parent);
            if (matched.Count > 1)
                menu.Items.Add(Item($"Replace all {matched.Count} with their mods", () => ReplaceAllMatched(matched)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy the list", () => CopyPlaceholderList(all)));
        menu.Items.Add(Item("Add placeholder...", () => _ = AddPlaceholderAsync(ViewCenter())));
        return menu;
    }

    private void ReplaceAllMatched(IReadOnlyList<PlanNode> matched)
    {
        var pairs = matched.Where(n => _placeholderMatches.ContainsKey(n.Id)).Select(n => (n, _placeholderMatches[n.Id])).ToList();
        var notes = 0;
        foreach (var (node, mod) in pairs)
            if (ReplacePlaceholder(node, mod)) notes++;
        Commit(); // one undo step for the lot
        Rebuild();
        if (notes > 0) _onModsChanged?.Invoke();
        PlanStatus.Text = $"Replaced {pairs.Count} placeholders with their mods" + (notes > 0 ? $" ({notes} note(s) moved onto the mods)." : ".");
    }

    private void SelectCards(IEnumerable<PlanNode> nodes)
    {
        _selected.Clear();
        foreach (var n in nodes) _selected.Add(n.Id);
        ApplySelectionVisual();
        ReportSelection();
        Viewport.Focus();
    }

    /// <summary>Centres the board on a card at the current zoom and selects it, as the rail's section
    /// rows do for sections.</summary>
    private void JumpToCard(PlanNode node)
    {
        if (!_rect.TryGetValue(node.Id, out var r)) return;
        var s = ZoomT.ScaleX;
        PanT.X = Viewport.ActualWidth / 2 - (r.X + r.Width / 2) * s;
        PanT.Y = Viewport.ActualHeight / 2 - (r.Y + r.Height / 2) * s;
        SyncGrid();
        StashViewport();
        SelectOnly(node.Id);
        Viewport.Focus();
    }

    /// <summary>The list as plain text, grouped by status, with links: ready to paste into a
    /// to-do, an issue or a message asking around for ports.</summary>
    private void CopyPlaceholderList(IReadOnlyList<PlanNode> all)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var status in PlanPlaceholderStatus.All)
        {
            var these = all.Where(n => StatusOf(n) == status).OrderBy(PlaceholderName, StringComparer.OrdinalIgnoreCase).ToList();
            if (these.Count == 0) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"{PlanPlaceholderStatus.Label(status)} ({these.Count})");
            foreach (var n in these)
                sb.AppendLine("- " + PlaceholderName(n) + (n.Placeholder?.Link is { Length: > 0 } link ? "  " + link : ""));
        }
        try
        {
            Clipboard.SetText(sb.ToString().TrimEnd());
            PlanStatus.Text = $"Copied {all.Count} placeholder(s) as a list.";
        }
        catch { PlanStatus.Text = "Could not copy - another app is holding the clipboard."; }
    }
}
