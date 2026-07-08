using System.Diagnostics;
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

    /// <summary>All installed mods — lets the menu resolve dependency names and offer a dep picker.</summary>
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
}

/// <summary>
/// Builds the §2 mod options menu for one or more mods. With several targets, flag/priority/side/
/// category changes apply to all of them, a toggle's ✓ shows only when every target has it, and
/// single-mod-only actions (open page, reveal, update-to-version, dependencies) are hidden.
/// </summary>
public static class ModOptionsMenu
{
    private const int IcGlobe = 0xE774, IcEnable = 0xE73E, IcDisable = 0xE711, IcRefresh = 0xE72C, IcHistory = 0xE81C;
    private const int IcPage = 0xE7C3, IcTesting = 0xEC7A, IcSide = 0xE772;
    private const int IcFlag = 0xE7C1, IcTag = 0xE8EC, IcLink = 0xE71B, IcLibrary = 0xE8F1, IcAdd = 0xE710;
    private const int IcWarn = 0xE7BA, IcFolder = 0xE8B7, IcDelete = 0xE74D;

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

        // ── Enable / Disable ──
        if (ctx.OnSetEnabled is not null)
        {
            if (multi)
            {
                menu.Items.Add(Item("Enable all", () => ctx.OnSetEnabled!.Invoke(targets, true), Glyph(IcEnable)));
                menu.Items.Add(Item("Disable all", () => ctx.OnSetEnabled!.Invoke(targets, false), Glyph(IcDisable)));
            }
            else
            {
                menu.Items.Add(primary.Enabled
                    ? Item("Disable", () => ctx.OnSetEnabled!.Invoke(targets, false), Glyph(IcDisable))
                    : Item("Enable", () => ctx.OnSetEnabled!.Invoke(targets, true), Glyph(IcEnable)));
            }
        }

        // ── Updates ──
        if (ctx.OnUpdate is not null)
        {
            var any = targets.Any(t => t.HasUpdate);
            menu.Items.Add(Item(multi ? "Update all to newest" : "Update to newest",
                () => ctx.OnUpdate!.Invoke(targets), Glyph(IcRefresh),
                gesture: any ? "available" : null, enabled: any));
        }
        if (!multi && ctx.OnUpdateToVersion is not null)
            menu.Items.Add(Item("Update to version…", () => ctx.OnUpdateToVersion!.Invoke(primary), Glyph(IcHistory)));

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
        prio.Items.Add(Item("Custom…", () => { _ = SetCustomPriorityAsync(targets, ctx); }));
        menu.Items.Add(prio);

        // ── Category ──
        var cats = Parent("Category", Glyph(IcTag),
            multi ? null : primary.Meta.Categories.Count > 0 ? string.Join(", ", primary.Meta.Categories) : null);
        foreach (var c in ctx.Inventory.Metadata.Categories(ctx.PackId))
        {
            var name = c.Name;
            // The managed "Library" category mirrors the IsLibrary flag — toggle the flag, the category
            // follows. It's built-in, so no rename.
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
        cats.Items.Add(Item("New category…", () => { _ = AddCategoryAsync(targets, ctx); }));
        menu.Items.Add(cats);

        // ── Side ──
        var sameSide = targets.All(t => t.Meta.Side == primary.Meta.Side);
        var side = Parent("Side", Glyph(IcSide),
            !sameSide ? "mixed" : primary.Meta.Side switch { ModSide.Client => "Client", ModSide.Server => "Server", _ => null });
        side.Items.Add(Radio("Client + Server", targets.All(t => t.Meta.Side == ModSide.Both),   () => ApplyAll(targets, ctx, m => m.Side = ModSide.Both)));
        side.Items.Add(Radio("Client only",     targets.All(t => t.Meta.Side == ModSide.Client), () => ApplyAll(targets, ctx, m => m.Side = ModSide.Client)));
        side.Items.Add(Radio("Server only",     targets.All(t => t.Meta.Side == ModSide.Server), () => ApplyAll(targets, ctx, m => m.Side = ModSide.Server)));
        menu.Items.Add(side);

        // ── Store (single mod, only when the jar is listed on both) — pick which listing this mod
        //    follows for its label, page link and update checks. Fixes a jar that flipped to Modrinth. ──
        if (!multi && primary.Modrinth is not null && primary.CurseForge is not null)
        {
            var store = Parent("Store", Glyph(IcGlobe),
                primary.PrimarySource == ModSource.CurseForge ? "CurseForge" : "Modrinth");
            store.Items.Add(Radio("Modrinth",   primary.Meta.PreferredSource != ModSource.CurseForge,
                () => ApplyAll(targets, ctx, m => m.PreferredSource = ModSource.Modrinth)));
            store.Items.Add(Radio("CurseForge", primary.Meta.PreferredSource == ModSource.CurseForge,
                () => ApplyAll(targets, ctx, m => m.PreferredSource = ModSource.CurseForge)));
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
            t.Meta.IncompatibleWithUnknown || t.Meta.UpdateIncompatible || t.Meta.IncompatibleWith.Count > 0);
        var incompat = Parent("Incompatibility", Glyph(IcWarn), anyIncompat ? "✓" : null);
        incompat.Items.Add(FlagToggle("Incompatible with unknown / other mods", targets, ctx,
            m => m.IncompatibleWithUnknown, (m, v) => m.IncompatibleWithUnknown = v));
        incompat.Items.Add(FlagToggle("Updating breaks compatibility", targets, ctx,
            m => m.UpdateIncompatible, (m, v) => m.UpdateIncompatible = v));
        // Specific mod-to-mod conflicts (single mod only): pick exactly which other mod clashes.
        if (!multi)
            AppendIncompatibleMods(incompat, primary, ctx);
        menu.Items.Add(incompat);

        menu.Items.Add(new Separator());

        if (ctx.OnReveal is not null && !multi)
            menu.Items.Add(Item("Reveal in Explorer", () => ctx.OnReveal!.Invoke(primary), Glyph(IcFolder)));
        if (ctx.OnDelete is not null)
            menu.Items.Add(Item(multi ? $"Delete {targets.Count} files" : "Delete file",
                () => ctx.OnDelete!.Invoke(targets), Glyph(IcDelete)));

        return menu;
    }

    // ── dependencies submenu (single mod) ───────────────────────────────────────

    private static MenuItem BuildDependencies(PackMod mod, ModOptionsContext ctx)
    {
        var graph = ModGraph.Build(ctx.AllMods);
        var resolved = graph.DependenciesOf(mod)
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var resolvedSet = new HashSet<PackMod>(resolved);

        // A manual dependency that's already auto-resolved would otherwise show (and count) twice —
        // drop those so the same mod never appears as both an "auto" entry and a manual "remove" one.
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
        parent.Items.Add(Item("Add dependency…", () => AddDependency(mod, ctx), Glyph(IcAdd)));
        return parent;
    }

    private static async void AddDependency(PackMod mod, ModOptionsContext ctx)
    {
        var manual = new HashSet<string>(mod.Meta.ManualDependencies, StringComparer.OrdinalIgnoreCase);
        // Already-auto-resolved dependencies are excluded too, so the user can't create a duplicate.
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
    /// "Incompatible with mod…" picker. A conflict is recorded mutually, so it shows from either side.</summary>
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
        parent.Items.Add(Item("Incompatible with mod…", () => AddIncompatibleMod(mod, ctx), Glyph(IcAdd)));
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

        // Incompatibility is mutual — record it on both mods so it's visible from either menu.
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

    /// <summary>A bool-flag toggle over the whole target set: ✓ shows only when every target has it,
    /// and a click flips them all to the inverse of that.</summary>
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

    /// <summary>Checkable item that shows a right-aligned ✓ when active, stays open so several can
    /// be toggled in a row, and updates its indicator live from <paramref name="toggle"/>'s result.</summary>
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

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
