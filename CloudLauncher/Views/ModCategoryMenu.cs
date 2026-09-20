using System.Windows.Controls;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The shared "Categories" submenu — create, rename and delete a pack's mod categories.
///
/// Built in one place so the List view, the Graph view and the planning board offer the same actions
/// with the same safeguards: the managed "Library" category can't be edited, a rename follows through
/// to any planning board's group cards, and a delete says how many mods it will untag before doing it.
/// </summary>
public static class ModCategoryMenu
{
    /// <param name="onChanged">Called after any edit lands, so the calling view can re-render.</param>
    /// <param name="onCreated">Optional extra step for a freshly created category — the planning
    /// board uses it to drop a group card, since a new category matches nothing and would otherwise
    /// be invisible.</param>
    public static MenuItem Build(Guid packId, IReadOnlyList<PackMod> mods, MainWindow? owner,
        Action? onChanged, Action<string>? onCreated = null)
    {
        var parent = new MenuItem { Header = "Categories" };
        parent.Items.Add(Item("New category…", () => _ = NewAsync(packId, owner, onChanged, onCreated)));

        var cats = App.State.ModMetadata.Categories(packId)
            .Where(c => !c.Builtin)   // "Library" mirrors the IsLibrary flag — not editable by hand
            .ToList();                // stored order — matches the Categories page
        if (cats.Count == 0) return parent;

        parent.Items.Add(new Separator());
        var rename = new MenuItem { Header = "Rename" };
        var delete = new MenuItem { Header = "Delete" };
        foreach (var c in cats)
        {
            var name = c.Name;
            var count = mods.Count(m => m.Meta.Categories.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)));
            rename.Items.Add(Item(name, () => _ = RenameAsync(packId, owner, name, onChanged), count.ToString()));
            delete.Items.Add(Item(name, () => _ = DeleteAsync(packId, owner, mods, name, onChanged), count.ToString()));
        }
        parent.Items.Add(rename);
        parent.Items.Add(delete);
        return parent;
    }

    /// <summary>Prompts for a name, creates the category and gives it the next unused palette colour
    /// so a fresh category is visually distinct from the start.</summary>
    public static async Task NewAsync(Guid packId, MainWindow? owner, Action? onChanged, Action<string>? onCreated = null)
    {
        var input = await PromptAsync(owner, "New category", "Category name", "");
        if (string.IsNullOrWhiteSpace(input)) return;

        var name = input!.Trim();
        App.State.ModMetadata.AddCategory(packId, name);
        App.State.ModMetadata.SetCategoryColor(packId, name, NextFreeColor(packId));
        onCreated?.Invoke(name);
        onChanged?.Invoke();
    }

    /// <summary>The first palette entry no category is using yet, cycling once they're all taken.</summary>
    private static string NextFreeColor(Guid packId)
    {
        var used = App.State.ModMetadata.Categories(packId)
            .Select(c => c.Color)
            .Where(c => c is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, hex) in AccentPalette.Colors)
            if (!used.Contains(hex)) return hex;

        return AccentPalette.Colors[used.Count % AccentPalette.Colors.Length].Hex;
    }

    public static async Task RenameAsync(Guid packId, MainWindow? owner, string oldName, Action? onChanged)
    {
        var input = await PromptAsync(owner, "Rename category", "New category name", oldName);
        if (string.IsNullOrWhiteSpace(input)) return;

        var newName = input!.Trim();
        if (string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;

        App.State.ModMetadata.RenameCategory(packId, oldName, newName);
        App.State.ModPlans.RenameCategoryReferences(packId, oldName, newName); // keep board cards pointed at it
        onChanged?.Invoke();
    }

    public static async Task DeleteAsync(Guid packId, MainWindow? owner, IReadOnlyList<PackMod> mods,
        string name, Action? onChanged)
    {
        var members = mods.Count(m => m.Meta.Categories.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)));

        if (owner is not null && !await AppDialog.ConfirmAsync(owner, "Delete category",
                members == 0
                    ? $"Delete the category “{name}”?"
                    : $"Delete “{name}” and untag {members} mod(s)?\n\nThe mods themselves aren't touched.",
                "Delete", "Cancel", danger: true))
            return;

        App.State.ModMetadata.RemoveCategory(packId, name);
        onChanged?.Invoke();
    }

    private static async Task<string?> PromptAsync(MainWindow? owner, string title, string label, string initial)
    {
        if (owner is not null) return await owner.PromptAsync(title, label, initial);
        var dlg = new SimpleInputDialog(title, label, initial);
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private static MenuItem Item(string header, Action onClick, string? gesture = null)
    {
        var mi = new MenuItem { Header = header };
        if (gesture is not null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }
}
