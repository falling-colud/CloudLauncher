using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Callbacks + context for the shared mod options menu. Flag/property edits act on the
/// whole target set; enable/disable/update/delete are handled by the view via the list callbacks.</summary>
public sealed class ModOptionsContext
{
    public required Guid PackId { get; init; }
    public required PackModInventory Inventory { get; init; }
    public Window? Owner { get; init; }

    /// <summary>All installed mods, so the menu can resolve dependency names and offer a dependency
    /// picker.</summary>
    public IReadOnlyList<PackMod> AllMods { get; init; } = Array.Empty<PackMod>();

    /// <summary>Raised after a change was applied + persisted (the view refreshes).</summary>
    public Action? OnChanged { get; init; }
    /// <summary>Opens the mod's in-launcher detail page; shown as the top item (single-mod menus only).</summary>
    public Action<PackMod>? OnOpenPage { get; init; }
    public Action<IReadOnlyList<PackMod>>? OnUpdate { get; init; }
    public Action<PackMod>? OnUpdateToVersion { get; init; }
    public Action<IReadOnlyList<PackMod>>? OnDelete { get; init; }
    public Action<PackMod>? OnReveal { get; init; }
    public Action<IReadOnlyList<PackMod>, bool>? OnSetEnabled { get; init; }
    /// <summary>Publishes the jar as a new hosted mod on the launcher's own server.</summary>
    /// <remarks>Only the pack page's Mods tab supplies this; elsewhere it is null and the item is
    /// hidden.</remarks>
    public Action<PackMod>? OnCreateHostedMod { get; init; }
    /// <summary>Raised after an edit that changes which versions count as an update (the channel or
    /// the followed store), so the view re-runs the update check for those mods.</summary>
    public Action<IReadOnlyList<PackMod>>? OnRecheckUpdates { get; init; }
}

/// <summary>
/// Builds the mod options menu for one or more mods. With several targets, flag, priority, side and
/// category changes apply to all of them, a toggle shows its check mark only when every target has
/// it, and single-mod actions (open page, reveal, update to version, dependencies) are hidden.
/// </summary>
public static class ModOptionsMenu
{
    private const int IcGlobe = 0xE774, IcEnable = 0xE73E, IcDisable = 0xE711, IcRefresh = 0xE72C, IcHistory = 0xE81C;
    private const int IcPage = 0xE7C3, IcTesting = 0xEC7A, IcSide = 0xE772;
    private const int IcFlag = 0xE7C1, IcTag = 0xE8EC, IcLink = 0xE71B, IcLibrary = 0xE8F1, IcAdd = 0xE710;
    private const int IcWarn = 0xE7BA, IcFolder = 0xE8B7, IcDelete = 0xE74D, IcNote = 0xE70B;
    private const int IcLock = 0xE72E;
    private const int IcCopy = 0xE8C8; // "Copy"
    private const int IcMore = 0xE712; // "More": the same three dots as the button that opens this menu
    private const int IcUpload = 0xE898; // "Upload": publish a local jar as a hosted mod
    private const int IcChannel = 0xE8AB; // "Switch": which release channel updates come from
    private const int IcSize = 0xE9D9; // "BarChart": matches the three-bar meter on the cards

    public static ContextMenu Build(PackMod mod, ModOptionsContext ctx) => Build(new[] { mod }, ctx);

    public static ContextMenu Build(IReadOnlyList<PackMod> targets, ModOptionsContext ctx)
    {
        var menu = new ContextMenu { MinWidth = 230 };
        if (targets.Count == 0) return menu;

        var multi = targets.Count > 1;
        var primary = targets[0];

        menu.Items.Add(BuildHeader(targets));
        menu.Items.Add(new Separator());

        // Top action: open the mod's in-launcher detail page (single mod only).
        if (!multi && ctx.OnOpenPage is not null)
            menu.Items.Add(Item("Open page", () => ctx.OnOpenPage!.Invoke(primary), Glyph(IcPage)));

        if (!multi && primary.PageUrl is not null)
            menu.Items.Add(Item("Open website", () => OpenUrl(primary.PageUrl!), Glyph(IcGlobe)));

        menu.Items.Add(BuildCopy(targets));

        // ── Note ── one free-text note per mod, so this stays single-target.
        if (!multi)
            menu.Items.Add(Item(primary.HasNote ? "Edit note..." : "Add note...",
                () => OpenNote(primary, ctx), Glyph(IcNote), gesture: primary.HasNote ? "✓" : null));

        // ── Enable / Disable ──
        AppendEnableItems(menu, targets, ctx);

        // ── Updates ──
        if (UpdateItem(targets, ctx) is { } update) menu.Items.Add(update);
        if (!multi && ctx.OnUpdateToVersion is not null)
            menu.Items.Add(Item("Update to version...", () => ctx.OnUpdateToVersion!.Invoke(primary), Glyph(IcHistory)));
        // Kept beside the update actions it governs rather than down in the flag list.
        menu.Items.Add(FlagToggle("Lock updates", targets, ctx,
            m => m.UpdateLocked, (m, v) => m.UpdateLocked = v, Glyph(IcLock)));

        // ── Update channel ── release only / + beta / + alpha, per mod, or follow the pack.
        var sameChannel = targets.All(t => string.Equals(t.Meta.UpdateChannel, primary.Meta.UpdateChannel, StringComparison.OrdinalIgnoreCase));
        var channel = Parent("Update channel", Glyph(IcChannel),
            !sameChannel ? "mixed" : primary.Meta.UpdateChannel is null ? null : ModUpdateChannel.Label(primary.Meta.UpdateChannel));
        var packDefaultItem = Radio($"Pack default ({ModUpdateChannel.Label(primary.DefaultUpdateChannel)})",
            targets.All(t => t.Meta.UpdateChannel is null),
            () => { ApplyAll(targets, ctx, m => m.UpdateChannel = null); ctx.OnRecheckUpdates?.Invoke(targets); });
        packDefaultItem.ToolTip = "Follow the pack's channel (Modpack Management > Advanced), which itself can follow Settings > Mods";
        channel.Items.Add(packDefaultItem);
        channel.Items.Add(new Separator());
        foreach (var ch in ModUpdateChannel.All)
        {
            var value = ch;
            var item = Radio(ModUpdateChannel.Label(value),
                targets.All(t => string.Equals(t.Meta.UpdateChannel, value, StringComparison.OrdinalIgnoreCase)),
                () => { ApplyAll(targets, ctx, m => m.UpdateChannel = value); ctx.OnRecheckUpdates?.Invoke(targets); });
            item.ToolTip = ModUpdateChannel.Describe(value);
            channel.Items.Add(item);
        }
        menu.Items.Add(channel);

        menu.Items.Add(new Separator());

        // ── Priority ──
        var samePrio = targets.All(t => t.Meta.Priority == primary.Meta.Priority);
        var prio = Parent("Priority", Glyph(IcFlag),
            !samePrio ? "mixed" : primary.Meta.Priority != 0 ? PriorityPalette.Label(primary.Meta.Priority) : null);
        for (var p = 0; p <= 5; p++)
        {
            var pv = p;
            prio.Items.Add(Radio(p == 0 ? "Normal (0)" : $"P{p}", targets.All(t => t.Meta.Priority == pv),
                () => ApplyAll(targets, ctx, m => m.Priority = pv)));
        }
        prio.Items.Add(new Separator());
        prio.Items.Add(Item("Custom...", () => { _ = SetCustomPriorityAsync(targets, ctx); }));
        menu.Items.Add(prio);

        // ── Content size ── how much the mod brings, as opposed to how much you care about it.
        var sameSize = targets.All(t => t.Meta.ContentSize == primary.Meta.ContentSize);
        var size = Parent("Content size", Glyph(IcSize),
            !sameSize ? "mixed" : primary.Meta.ContentSize != 0 ? ModContentSize.Label(primary.Meta.ContentSize) : null);
        for (var s = 0; s <= ModContentSize.Max; s++)
        {
            var sv = s;
            var item = Radio(s == 0 ? "Unset" : ModContentSize.Label(s), targets.All(t => t.Meta.ContentSize == sv),
                () => ApplyAll(targets, ctx, m => m.ContentSize = sv));
            item.ToolTip = ModContentSize.Describe(s);
            size.Items.Add(item);
        }
        menu.Items.Add(size);

        // ── Category ──
        var cats = Parent("Category", Glyph(IcTag),
            multi ? null : primary.Meta.Categories.Count > 0 ? string.Join(", ", primary.Meta.Categories) : null);
        foreach (var c in ctx.Inventory.Metadata.Categories(ctx.PackId))
        {
            var name = c.Name;
            // The built-in "Library" category mirrors the IsLibrary flag, so this toggles the flag.
            // It can't be renamed.
            if (string.Equals(name, ModMetadataService.LibraryCategory, StringComparison.OrdinalIgnoreCase))
            {
                cats.Items.Add(Toggle(name, targets.All(t => t.Meta.IsLibrary), () =>
                {
                    var v = !targets.All(t => t.Meta.IsLibrary);
                    ApplyAll(targets, ctx, m => m.IsLibrary = v);
                    return targets.All(t => t.Meta.IsLibrary);
                }));
                continue;
            }
            var item = Toggle(name, targets.All(t => t.Meta.Categories.Contains(name)), () =>
            {
                var allIn = targets.All(t => t.Meta.Categories.Contains(name));
                ApplyAll(targets, ctx, m => { if (allIn) m.Categories.Remove(name); else if (!m.Categories.Contains(name)) m.Categories.Add(name); });
                return targets.All(t => t.Meta.Categories.Contains(name));
            });
            // Right-click a category to rename it (left-click still toggles membership).
            item.ToolTip = "Right-click to rename";
            item.PreviewMouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                CloseMenu((DependencyObject)s);
                _ = RenameCategoryAsync(name, ctx);
            };
            cats.Items.Add(item);
        }
        if (cats.Items.Count > 0) cats.Items.Add(new Separator());
        cats.Items.Add(Item("New category...", () => { _ = AddCategoryAsync(targets, ctx); }));
        menu.Items.Add(cats);

        // ── Side ──
        var sameSide = targets.All(t => t.Meta.Side == primary.Meta.Side);
        var side = Parent("Side", Glyph(IcSide),
            !sameSide ? "mixed" : primary.Meta.Side switch { ModSide.Client => "Client", ModSide.Server => "Server", _ => null });
        side.Items.Add(Radio("Client + Server", targets.All(t => t.Meta.Side == ModSide.Both),   () => ApplyAll(targets, ctx, m => m.Side = ModSide.Both)));
        side.Items.Add(Radio("Client only",     targets.All(t => t.Meta.Side == ModSide.Client), () => ApplyAll(targets, ctx, m => m.Side = ModSide.Client)));
        side.Items.Add(Radio("Server only",     targets.All(t => t.Meta.Side == ModSide.Server), () => ApplyAll(targets, ctx, m => m.Side = ModSide.Server)));
        menu.Items.Add(side);

        // ── Store ── only when a target is listed on both: which listing the mod follows for its
        // label, page link and update checks, or the pack-wide default from the Advanced tab.
        if (targets.Any(t => t.IsCrossListed))
        {
            var samePref = targets.All(t => t.Meta.PreferredSource == primary.Meta.PreferredSource);
            var store = Parent("Store", Glyph(IcGlobe),
                !samePref ? "mixed" : multi ? null : primary.PrimarySource == ModSource.CurseForge ? "CurseForge" : "Modrinth");
            var packDefault = primary.DefaultSource switch
            {
                ModSource.CurseForge => "CurseForge",
                ModSource.Modrinth => "Modrinth",
                _ => "Modrinth first"
            };
            store.Items.Add(Radio($"Pack default ({packDefault})", targets.All(t => t.Meta.PreferredSource is null),
                () => { ApplyAll(targets, ctx, m => m.PreferredSource = null); ctx.OnRecheckUpdates?.Invoke(targets); }));
            store.Items.Add(new Separator());
            store.Items.Add(Radio("CurseForge", targets.All(t => t.Meta.PreferredSource == ModSource.CurseForge),
                () => { ApplyAll(targets, ctx, m => m.PreferredSource = ModSource.CurseForge); ctx.OnRecheckUpdates?.Invoke(targets); }));
            store.Items.Add(Radio("Modrinth", targets.All(t => t.Meta.PreferredSource == ModSource.Modrinth),
                () => { ApplyAll(targets, ctx, m => m.PreferredSource = ModSource.Modrinth); ctx.OnRecheckUpdates?.Invoke(targets); }));
            menu.Items.Add(store);
        }

        // ── Dependencies (single mod only) ──
        if (!multi)
            menu.Items.Add(BuildDependencies(primary, ctx));

        menu.Items.Add(new Separator());

        // ── Flags ──
        menu.Items.Add(FlagToggle("Library", targets, ctx, m => m.IsLibrary, (m, v) => m.IsLibrary = v, Glyph(IcLibrary)));
        menu.Items.Add(FlagToggle("Testing", targets, ctx, m => m.IsTesting, (m, v) => m.IsTesting = v, Glyph(IcTesting)));
        menu.Items.Add(FlagToggle("Extra (optional)", targets, ctx, m => m.IsExtra, (m, v) => m.IsExtra = v, Glyph(IcAdd)));

        // ── Incompatibility ──
        var anyIncompat = targets.Any(t =>
            t.Meta.IncompatibleWithUnknown || t.Meta.UpdateIncompatible
            || t.Meta.UpdateIncompatibleWithUnknown || t.Meta.IncompatibleWith.Count > 0);
        var incompat = Parent("Incompatibility", Glyph(IcWarn), anyIncompat ? "✓" : null);
        incompat.Items.Add(FlagToggle("Incompatible with unknown / other mods", targets, ctx,
            m => m.IncompatibleWithUnknown, (m, v) => m.IncompatibleWithUnknown = v));
        incompat.Items.Add(FlagToggle("Updating breaks compatibility", targets, ctx,
            m => m.UpdateIncompatible, (m, v) => m.UpdateIncompatible = v));
        var unknownUpdate = FlagToggle("Updating breaks compatibility with unknown mods", targets, ctx,
            m => m.UpdateIncompatibleWithUnknown, (m, v) => m.UpdateIncompatibleWithUnknown = v);
        unknownUpdate.ToolTip = "For a mod whose updates are known to break things you have not installed yet - a warning to your future self, since the launcher cannot see the clash.";
        incompat.Items.Add(unknownUpdate);
        // Specific mod-to-mod conflicts (single mod only): pick which other mod clashes.
        if (!multi)
            AppendIncompatibleMods(incompat, primary, ctx);
        menu.Items.Add(incompat);

        menu.Items.Add(new Separator());

        if (!multi && RevealItem(primary, ctx) is { } reveal) menu.Items.Add(reveal);
        // Only for jars no store recognises; an identified mod is already published by someone. The
        // gesture text says why the item is disabled.
        if (ctx.OnCreateHostedMod is not null && !multi)
            menu.Items.Add(Item("Create hosted mod from jar",
                () => ctx.OnCreateHostedMod!.Invoke(primary), Glyph(IcUpload),
                gesture: primary.IsExternal ? null : "already linked",
                enabled: primary.IsExternal));
        if (DeleteItem(targets, ctx) is { } delete) menu.Items.Add(delete);

        return menu;
    }

    // ── quick menu (left-click on a row's "...") ──────────────────────────────────

    /// <summary>
    /// The short menu a left-click on a row's "..." button opens: open the page, update, toggle,
    /// reveal, delete, and "More options..." for the full <see cref="Build"/> menu.
    /// </summary>
    /// <remarks>
    /// Built from the same helpers as the full menu so both look the same. Every item checks its
    /// callback for null, since the planning board and Categories tab supply far fewer callbacks.
    /// </remarks>
    public static ContextMenu BuildQuick(IReadOnlyList<PackMod> targets, ModOptionsContext ctx)
    {
        var menu = new ContextMenu { MinWidth = 230 };
        if (targets.Count == 0) return menu;

        var multi = targets.Count > 1;
        var primary = targets[0];

        menu.Items.Add(BuildHeader(targets));
        menu.Items.Add(new Separator());

        // The store page if there is one, else the in-launcher page (external jars have no website).
        if (!multi && primary.PageUrl is not null)
            menu.Items.Add(Item("Open website", () => OpenUrl(primary.PageUrl!), Glyph(IcGlobe)));
        else if (!multi && ctx.OnOpenPage is not null)
            menu.Items.Add(Item("Open page", () => ctx.OnOpenPage!.Invoke(primary), Glyph(IcPage)));

        // Unlike the full menu, leave the update item out when there is nothing to update.
        if (targets.Any(t => t.HasUpdate) && UpdateItem(targets, ctx) is { } update)
            menu.Items.Add(update);

        AppendEnableItems(menu, targets, ctx);

        var tail = new List<MenuItem>();
        if (!multi && RevealItem(primary, ctx) is { } reveal) tail.Add(reveal);
        if (DeleteItem(targets, ctx) is { } delete) tail.Add(delete);
        if (tail.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var item in tail) menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("More options...", () => ShowFullMenu(targets, ctx, menu), Glyph(IcMore)));
        return menu;
    }

    /// <summary>Opens the full menu where the quick one stood, once the quick one has closed.</summary>
    /// <remarks>Deferred because a popup opened inline ends up behind the closing (topmost) menu.
    /// Placement is read at click time since the caller sets
    /// <see cref="ContextMenu.PlacementTarget"/> after <see cref="BuildQuick"/> returns.</remarks>
    private static void ShowFullMenu(IReadOnlyList<PackMod> targets, ModOptionsContext ctx, ContextMenu quick)
    {
        var target = quick.PlacementTarget;
        var placement = quick.Placement;
        Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                var full = Build(targets, ctx);
                full.PlacementTarget = target;
                full.Placement = placement;
                full.IsOpen = true;
            }));
    }

    // ── items shared by both menus ──────────────────────────────────────────────

    /// <summary>The enable/disable pair (multi) or the single opposite-of-now item. Used by both
    /// menus.</summary>
    private static void AppendEnableItems(ContextMenu menu, IReadOnlyList<PackMod> targets, ModOptionsContext ctx)
    {
        if (ctx.OnSetEnabled is null) return;
        if (targets.Count > 1)
        {
            menu.Items.Add(Item("Enable all", () => ctx.OnSetEnabled!.Invoke(targets, true), Glyph(IcEnable)));
            menu.Items.Add(Item("Disable all", () => ctx.OnSetEnabled!.Invoke(targets, false), Glyph(IcDisable)));
        }
        else
        {
            menu.Items.Add(targets[0].Enabled
                ? Item("Disable", () => ctx.OnSetEnabled!.Invoke(targets, false), Glyph(IcDisable))
                : Item("Enable", () => ctx.OnSetEnabled!.Invoke(targets, true), Glyph(IcEnable)));
        }
    }

    /// <summary>"Update to newest", with gesture text saying how much of the selection the update
    /// lock is holding back.</summary>
    private static MenuItem? UpdateItem(IReadOnlyList<PackMod> targets, ModOptionsContext ctx)
    {
        if (ctx.OnUpdate is null) return null;
        var updatable = targets.Count(t => t.HasUpdate);
        var held = targets.Count(t => t.HasUpdate && t.Meta.UpdateLocked);
        return Item(targets.Count > 1 ? "Update all to newest" : "Update to newest",
            () => ctx.OnUpdate!.Invoke(targets), Glyph(IcRefresh),
            gesture: updatable == 0     ? null
                   : held == 0          ? "available"
                   : held == updatable  ? "locked"
                   :                      $"{updatable - held} of {updatable}",
            enabled: updatable > 0);
    }

    private static MenuItem? RevealItem(PackMod mod, ModOptionsContext ctx) =>
        ctx.OnReveal is null ? null : Item("Reveal in Explorer", () => ctx.OnReveal!.Invoke(mod), Glyph(IcFolder));

    private static MenuItem? DeleteItem(IReadOnlyList<PackMod> targets, ModOptionsContext ctx) =>
        ctx.OnDelete is null ? null
            : Item(targets.Count > 1 ? $"Delete {targets.Count} files" : "Delete file",
                () => ctx.OnDelete!.Invoke(targets), Glyph(IcDelete));

    // ── copy ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Copy the mod's name, page link or jar filename to the clipboard.
    /// </summary>
    /// <remarks>
    /// The jar filename is what crash reports name, and often differs from the display name. With
    /// several mods selected, the whole selection is copied, one per line.
    /// </remarks>
    private static MenuItem BuildCopy(IReadOnlyList<PackMod> targets)
    {
        var multi = targets.Count > 1;
        var primary = targets[0];
        var parent = Parent("Copy", Glyph(IcCopy), null);

        parent.Items.Add(Item(multi ? $"{targets.Count} names" : "Name",
            () => Services.ClipboardHelper.TrySetText(Lines(targets.Select(t => t.DisplayName)))));

        var links = targets.Where(t => t.PageUrl is not null).Select(t => t.PageUrl!).ToList();
        parent.Items.Add(Item(multi ? $"{links.Count} page links" : "Page link",
            () => Services.ClipboardHelper.TrySetText(Lines(links)), enabled: links.Count > 0));

        parent.Items.Add(Item(multi ? $"{targets.Count} file names" : "File name",
            () => Services.ClipboardHelper.TrySetText(
                Lines(targets.Select(t => System.IO.Path.GetFileName(t.FilePath))))));

        if (!multi)
            parent.Items.Add(Item("Name and version",
                () => Services.ClipboardHelper.TrySetText($"{primary.DisplayName} {primary.VersionLabel}")));

        return parent;

        static string Lines(IEnumerable<string> values) => string.Join(Environment.NewLine, values);
    }

    // ── note ────────────────────────────────────────────────────────────────────

    /// <summary>Opens the note popup once the menu has closed. Opened inline, it would sit behind the
    /// closing (topmost) menu and lose keyboard focus to it.</summary>
    private static void OpenNote(PackMod mod, ModOptionsContext ctx)
    {
        Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => ModNotePopup.Show(null, mod, ctx.PackId, ctx.Inventory, ctx.OnChanged)));
    }

    // ── dependencies submenu (single mod) ───────────────────────────────────────

    private static MenuItem BuildDependencies(PackMod mod, ModOptionsContext ctx)
    {
        var graph = ModGraph.Build(ctx.AllMods);
        var resolved = graph.DependenciesOf(mod)
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var resolvedSet = new HashSet<PackMod>(resolved);

        // Skip manual dependencies that are also auto-resolved, so a mod isn't listed twice.
        var manualMods = mod.Meta.ManualDependencies
            .Select(k => (key: k, mod: ctx.AllMods.FirstOrDefault(m => m.CandidateKeys.Contains(k, StringComparer.OrdinalIgnoreCase))))
            .Where(x => x.mod is null || !resolvedSet.Contains(x.mod))
            .ToList();

        var count = resolved.Count + manualMods.Count;
        var parent = Parent("Dependencies", Glyph(IcLink), count > 0 ? count.ToString() : null);

        foreach (var dep in resolved)
            parent.Items.Add(Item(dep.DisplayName, () => { }, gesture: "auto", enabled: false));

        if (resolved.Count > 0 && manualMods.Count > 0) parent.Items.Add(new Separator());
        foreach (var (key, depMod) in manualMods)
        {
            var label = depMod?.DisplayName ?? key;
            parent.Items.Add(Item(label, () => { mod.Meta.ManualDependencies.Remove(key); Persist(mod, ctx); }, gesture: "remove"));
        }

        if (parent.Items.Count > 0) parent.Items.Add(new Separator());
        parent.Items.Add(Item("Add dependency...", () => AddDependency(mod, ctx), Glyph(IcAdd)));
        return parent;
    }

    private static async void AddDependency(PackMod mod, ModOptionsContext ctx)
    {
        var manual = new HashSet<string>(mod.Meta.ManualDependencies, StringComparer.OrdinalIgnoreCase);
        // Auto-resolved dependencies are excluded too, so the user can't add a duplicate.
        var alreadyDep = new HashSet<PackMod>(ModGraph.Build(ctx.AllMods).DependenciesOf(mod));
        var candidates = ctx.AllMods
            .Where(m => !ReferenceEquals(m, mod)
                        && !m.CandidateKeys.Any(k => manual.Contains(k))
                        && !alreadyDep.Contains(m))
            .ToList();
        if (candidates.Count == 0 || ctx.Owner is not MainWindow host) return;

        var pick = await ModPickerDialog.ShowAsync(host, "Add dependency", candidates);
        if (pick is not null)
        {
            if (!mod.Meta.ManualDependencies.Contains(pick.Key)) mod.Meta.ManualDependencies.Add(pick.Key);
            Persist(mod, ctx);
        }
    }

    // ── specific incompatible mods (single mod) ──────────────────────────────────

    /// <summary>Lists the mods this one is explicitly marked incompatible with (each removable) plus an
    /// "Incompatible with mod..." picker. A conflict is recorded on both mods, so it shows from either side.</summary>
    private static void AppendIncompatibleMods(MenuItem parent, PackMod mod, ModOptionsContext ctx)
    {
        var conflicts = mod.Meta.IncompatibleWith
            .Select(k => (key: k, mod: ctx.AllMods.FirstOrDefault(m => m.CandidateKeys.Contains(k, StringComparer.OrdinalIgnoreCase))))
            .ToList();

        parent.Items.Add(new Separator());
        foreach (var (key, conflictMod) in conflicts)
        {
            var label = conflictMod?.DisplayName ?? key;
            parent.Items.Add(Item(label, () => RemoveIncompatibleMod(mod, key, conflictMod, ctx), gesture: "remove"));
        }
        parent.Items.Add(Item("Incompatible with mod...", () => AddIncompatibleMod(mod, ctx), Glyph(IcAdd)));
    }

    private static async void AddIncompatibleMod(PackMod mod, ModOptionsContext ctx)
    {
        var existing = new HashSet<string>(mod.Meta.IncompatibleWith, StringComparer.OrdinalIgnoreCase);
        var candidates = ctx.AllMods
            .Where(m => !ReferenceEquals(m, mod) && !m.CandidateKeys.Any(k => existing.Contains(k)))
            .ToList();
        if (candidates.Count == 0 || ctx.Owner is not MainWindow host) return;

        var pick = await ModPickerDialog.ShowAsync(host, "Mark incompatible mod", candidates);
        if (pick is null) return;

        // Incompatibility is mutual: record it on both mods so it shows in either menu.
        if (!mod.Meta.IncompatibleWith.Contains(pick.Key)) mod.Meta.IncompatibleWith.Add(pick.Key);
        if (!pick.Meta.IncompatibleWith.Contains(mod.Key)) pick.Meta.IncompatibleWith.Add(mod.Key);
        ctx.Inventory.SaveMeta(ctx.PackId, pick);
        Persist(mod, ctx);
    }

    private static void RemoveIncompatibleMod(PackMod mod, string key, PackMod? conflictMod, ModOptionsContext ctx)
    {
        mod.Meta.IncompatibleWith.Remove(key);
        if (conflictMod is not null)
        {
            // Drop the reverse link too (under whichever of this mod's candidate keys it was stored).
            var modKeys = new HashSet<string>(mod.CandidateKeys, StringComparer.OrdinalIgnoreCase);
            conflictMod.Meta.IncompatibleWith.RemoveAll(modKeys.Contains);
            ctx.Inventory.SaveMeta(ctx.PackId, conflictMod);
        }
        Persist(mod, ctx);
    }

    private static async Task SetCustomPriorityAsync(IReadOnlyList<PackMod> targets, ModOptionsContext ctx)
    {
        var input = await PromptAsync(ctx, "Set priority", "Priority number", targets[0].Meta.Priority.ToString());
        if (input is not null && int.TryParse(input.Trim(), out var v)) ApplyAll(targets, ctx, m => m.Priority = v);
    }

    private static async Task AddCategoryAsync(IReadOnlyList<PackMod> targets, ModOptionsContext ctx)
    {
        var input = await PromptAsync(ctx, "New category", "Category name", "");
        if (string.IsNullOrWhiteSpace(input)) return;
        var nm = input.Trim();
        ctx.Inventory.Metadata.AddCategory(ctx.PackId, nm);
        ApplyAll(targets, ctx, m => { if (!m.Categories.Contains(nm)) m.Categories.Add(nm); });
    }

    /// <summary>Renames a category pack-wide (from a right-click on its menu item); refreshes the view.</summary>
    private static async Task RenameCategoryAsync(string oldName, ModOptionsContext ctx)
    {
        var input = await PromptAsync(ctx, "Rename category", "New category name", oldName);
        if (string.IsNullOrWhiteSpace(input)) return;
        var newName = input.Trim();
        if (string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;
        ctx.Inventory.Metadata.RenameCategory(ctx.PackId, oldName, newName);
        App.State.ModPlans.RenameCategoryReferences(ctx.PackId, oldName, newName); // keep board group cards pointed at it
        ctx.OnChanged?.Invoke();
    }

    /// <summary>Closes the open context menu containing <paramref name="from"/>, so an in-window prompt
    /// isn't hidden behind the (topmost) menu popup after a right-click action.</summary>
    private static void CloseMenu(DependencyObject? from)
    {
        for (var d = from; d is not null;)
        {
            if (d is ContextMenu cm) { cm.IsOpen = false; return; }
            var next = LogicalTreeHelper.GetParent(d);
            if (next is null && d is Visual v) next = VisualTreeHelper.GetParent(v);
            d = next;
        }
    }

    /// <summary>In-window prompt when owned by the main window; falls back to the legacy input window otherwise.</summary>
    private static async Task<string?> PromptAsync(ModOptionsContext ctx, string title, string label, string initial)
    {
        if (ctx.Owner is MainWindow mw) return await mw.PromptAsync(title, label, initial);
        var dlg = new SimpleInputDialog(title, label, initial) { Owner = ctx.Owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    // ── apply + helpers ──────────────────────────────────────────────────────────

    /// <summary>Apply a meta mutation to every target, persist each, then fire OnChanged once.</summary>
    private static void ApplyAll(IReadOnlyList<PackMod> targets, ModOptionsContext ctx, Action<ModMeta> apply)
    {
        foreach (var t in targets) { apply(t.Meta); ctx.Inventory.SaveMeta(ctx.PackId, t); }
        ctx.OnChanged?.Invoke();
    }

    private static void Persist(PackMod mod, ModOptionsContext ctx)
    {
        ctx.Inventory.SaveMeta(ctx.PackId, mod);
        ctx.OnChanged?.Invoke();
    }

    /// <summary>A bool-flag toggle over the whole target set: checked only when every target has the
    /// flag, and a click sets them all to the opposite.</summary>
    private static MenuItem FlagToggle(string header, IReadOnlyList<PackMod> targets, ModOptionsContext ctx,
        Func<ModMeta, bool> get, Action<ModMeta, bool> set, object? icon = null)
    {
        return Toggle(header, targets.All(t => get(t.Meta)), () =>
        {
            var v = !targets.All(t => get(t.Meta));
            ApplyAll(targets, ctx, m => set(m, v));
            return targets.All(t => get(t.Meta));
        }, icon);
    }

    private static MenuItem BuildHeader(IReadOnlyList<PackMod> targets)
    {
        var sp = new StackPanel { Margin = new Thickness(2, 2, 2, 2) };
        if (targets.Count == 1)
        {
            var mod = targets[0];
            sp.Children.Add(new TextBlock
            {
                Text = mod.DisplayName, FontWeight = FontWeights.SemiBold, MaxWidth = 250,
                Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
            });
            sp.Children.Add(new TextBlock
            {
                Text = mod.SourceLabel + (mod.Enabled ? "" : "  ·  disabled"),
                FontSize = 10, Foreground = Res("TextSecondaryBrush")
            });
        }
        else
        {
            sp.Children.Add(new TextBlock
            {
                Text = $"{targets.Count} mods selected", FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextPrimaryBrush")
            });
            sp.Children.Add(new TextBlock { Text = "Changes apply to all", FontSize = 10, Foreground = Res("TextSecondaryBrush") });
        }
        return new MenuItem { Header = sp, IsHitTestVisible = false, Focusable = false };
    }

    private static MenuItem Item(string header, Action onClick, object? icon = null, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (icon is not null) mi.Icon = icon;
        if (gesture is not null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static MenuItem Parent(string header, object? icon, string? gesture)
    {
        var mi = new MenuItem { Header = header };
        if (icon is not null) mi.Icon = icon;
        if (gesture is not null) mi.InputGestureText = gesture;
        return mi;
    }

    /// <summary>Checkable item that shows a right-aligned check mark when active, stays open so several
    /// can be toggled in a row, and updates its indicator from <paramref name="toggle"/>'s result.</summary>
    private static MenuItem Toggle(string header, bool active, Func<bool> toggle, object? icon = null)
    {
        var mi = new MenuItem { Header = header, StaysOpenOnClick = true, InputGestureText = active ? "✓" : "" };
        if (icon is not null) mi.Icon = icon;
        mi.Click += (_, _) => mi.InputGestureText = toggle() ? "✓" : "";
        return mi;
    }

    private static MenuItem Radio(string header, bool active, Action onClick)
    {
        var mi = new MenuItem { Header = header, InputGestureText = active ? "✓" : "" };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    /// <summary>Builds a fixed-width icon cell from an MDL2 glyph code (0 = a blank, aligned gutter).</summary>
    private static TextBlock Glyph(int code) => new()
    {
        Text = code <= 0 ? "" : char.ConvertFromUtf32(code),
        Width = 16,
        TextAlignment = TextAlignment.Center,
        FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
        FontSize = 13,
        Foreground = Res("TextSecondaryBrush"),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Hands a mod page URL to the default browser. Page URLs come from store metadata and
    /// shared packs, so only http and https links are opened; anything else is refused and logged.</summary>
    /// <remarks>Internal so the mod lists' clickable names go through it too.</remarks>
    internal static void OpenUrl(string? url) => SafeLaunch.OpenUrl(url);
}
