using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>One pack in an always-on folder, as a row.</summary>
public sealed class AlwaysOnRow(GlobalPackEntry entry)
{
    public GlobalPackEntry Entry { get; } = entry;
    public string Name => Entry.DisplayName;

    public string Initial => string.IsNullOrWhiteSpace(Entry.DisplayName)
        ? "?"
        : char.ToUpperInvariant(Entry.DisplayName[0]).ToString();

    public string Meta => string.Join("  ·  ", new[]
    {
        Entry.AlwaysOnLabel,
        Entry.FileName,
        SizeLabel(Entry.SizeBytes),
        Entry.Folder.CanOrder && Entry.Order == 0 ? "not in the order file - loads below the listed ones" : null
    }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Only a folder whose mod has an order file shows numbers and arrows.</summary>
    public Visibility OrderVisibility => Entry.Folder.CanOrder ? Visibility.Visible : Visibility.Collapsed;

    public string OrderLabel => Entry.Order > 0 ? $"#{Entry.Order}" : "-";

    public string Tooltip =>
        $"{Entry.DisplayName}\n{Entry.Folder.RelativeDir}/{Entry.FileName}\n{Entry.Folder.OrderNote}";

    private static string SizeLabel(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };
}

/// <summary>
/// The packs a mod such as Paxi loads in every world, for one instance and one kind: listed with
/// their order, reorderable where the mod has an order file, and addable, removable and movable out.
/// </summary>
/// <remarks>
/// <para>Shared by the instance's Resources tab (resource packs) and a world's Data packs tab (data
/// packs). The host says what "move out" means there - back into <c>resourcepacks/</c>, or into this
/// one world - and hears <see cref="Changed"/> after any write so its own list can follow.</para>
/// <para>Collapsed by the host when <see cref="HasFolders"/> is false: an instance without such a mod
/// has nothing to show here.</para>
/// </remarks>
public partial class AlwaysOnPacksPanel : UserControl
{
    private GlobalPackKind _kind = GlobalPackKind.Resource;
    private Guid _packId;
    private string _packName = "";
    private string? _mcVersion;
    private string? _moveOutLabel;
    private Func<GlobalPackEntry, Task<string>>? _moveOut;
    private IReadOnlyList<GlobalPackFolder> _folders = [];
    private List<GlobalPackEntry> _entries = [];
    private int _generation;

    public AlwaysOnPacksPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised after this panel wrote something, so the host can re-read its own list.</summary>
    public event Action? Changed;

    /// <summary>True when the instance has a mod with an always-on folder for this kind.</summary>
    public bool HasFolders => _folders.Count > 0;

    /// <summary>The instance's always-on folders for this kind, best install target first.</summary>
    public IReadOnlyList<GlobalPackFolder> Folders => _folders;

    /// <summary>Sets which kind this lists and what the row menu's "move out" does.</summary>
    /// <param name="moveOut">Moves the pack out of the always-on folder and returns a status line;
    /// null leaves the item out of the menu.</param>
    public void Configure(GlobalPackKind kind, string? moveOutLabel, Func<GlobalPackEntry, Task<string>>? moveOut)
    {
        _kind = kind;
        _moveOutLabel = moveOutLabel;
        _moveOut = moveOut;
    }

    /// <summary>Re-reads the instance's mods and its always-on folders, off the UI thread.</summary>
    public async Task LoadAsync(Guid packId, string packName, string? minecraftVersion)
    {
        _packId = packId;
        _packName = packName;
        _mcVersion = minecraftVersion;
        var generation = ++_generation;
        var kind = _kind;

        var (folders, entries) = await Task.Run(() =>
        {
            var found = App.State.GlobalPacks.FoldersFor(packId, packName, minecraftVersion, kind);
            var gameDir = App.State.Packs.GameDir(packId, packName);
            return (found, GlobalPackService.Scan(gameDir, found));
        });
        if (generation != _generation) return;

        _folders = folders;
        _entries = entries;
        EntryList.ItemsSource = entries.Select(e => new AlwaysOnRow(e)).ToList();

        var target = GlobalPackService.DefaultTarget(folders);
        var noun = kind == GlobalPackKind.Data ? "data packs" : "resource packs";
        TitleText.Text = kind == GlobalPackKind.Data ? "Always on in every world" : "Always on";
        HintText.Text = target is null
            ? ""
            : $"{Capitalise(noun)} {target.LoadedByLabel} loads for every world, whatever the game's own "
              + $"pack screen says. New ones go in {target.RelativeDir}/. "
              + (folders.Any(f => f.CanOrder) ? "The top one wins." : target.OrderNote);
        EmptyText.Text = $"No {noun} in the always-on folder{(folders.Count == 1 ? "" : "s")} yet. "
                         + "Add one here, or drop a .zip on this box.";
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = target is not null;
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private Task ReloadAsync() => LoadAsync(_packId, _packName, _mcVersion);

    private void Say(string text, bool danger = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, danger ? "DangerBrush" : "TextSecondaryBrush");
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Runs one write, reports it, and reloads. Refusals arrive as their own sentence.</summary>
    private async Task RunAsync(Func<Task<string>> work)
    {
        try
        {
            var message = await work();
            await ReloadAsync();
            Say(message);
            Changed?.Invoke();
        }
        catch (GlobalPackRefusedException refused) { Say(refused.Message, danger: true); }
        catch (DataPackWriteRefusedException refused) { Say(refused.Message, danger: true); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(AlwaysOnPacksPanel), ex);
            Say("That didn't work - " + ex.Message, danger: true);
            await ReloadAsync();
        }
    }

    // ── adding ───────────────────────────────────────────────────────────────

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _kind == GlobalPackKind.Data ? "Add an always-on data pack" : "Add an always-on resource pack",
            Filter = "Packs (*.zip)|*.zip|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await AddAsync(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GlobalPackService.DefaultTarget(_folders) is not null && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        await AddAsync(paths.Where(p => Directory.Exists(p) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList());
    }

    /// <summary>Copies each pack into the best always-on folder.</summary>
    public async Task AddAsync(IReadOnlyList<string> paths)
    {
        if (GlobalPackService.DefaultTarget(_folders) is not { } target || paths.Count == 0) return;
        await RunAsync(async () =>
        {
            var placed = await Task.Run(() => paths
                .Select(p => Path.GetFileName(App.State.GlobalPacks.Install(_packId, _packName, target, p)))
                .ToList());
            return placed.Count == 1
                ? $"{placed[0]} is always on now, loaded by {target.LoadedByLabel}."
                : $"{placed.Count} packs are always on now, loaded by {target.LoadedByLabel}.";
        });
    }

    // ── order ────────────────────────────────────────────────────────────────

    private async void OnMoveUp(object sender, RoutedEventArgs e) => await MoveAsync(sender, -1);

    private async void OnMoveDown(object sender, RoutedEventArgs e) => await MoveAsync(sender, +1);

    private async Task MoveAsync(object sender, int delta)
    {
        if (sender is not FrameworkElement { Tag: AlwaysOnRow row }) return;
        var entry = row.Entry;
        var sameFolder = _entries.Where(x => x.Folder.RelativeDir == entry.Folder.RelativeDir).ToList();
        await RunAsync(async () =>
        {
            await Task.Run(() => App.State.GlobalPacks.Move(_packId, _packName, entry, sameFolder, delta));
            return $"Moved {entry.DisplayName} {(delta < 0 ? "up" : "down")} in {entry.Folder.ModName}'s order.";
        });
    }

    // ── row menu ─────────────────────────────────────────────────────────────

    private void OnRowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AlwaysOnRow row } anchor) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (_moveOut is not null && _moveOutLabel is { } label)
            menu.Items.Add(Item(label, async () => await RunAsync(() => _moveOut(row.Entry))));
        menu.Items.Add(Item("Reveal in Explorer", () =>
        {
            if (!SafeLaunch.RevealFile(row.Entry.FilePath)) Say("Explorer could not be opened there.", danger: true);
            return Task.CompletedTask;
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete...", () => DeleteAsync(row.Entry)));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { AppLog.LogError(nameof(AlwaysOnPacksPanel), ex); }
        };
        return item;
    }

    private async Task DeleteAsync(GlobalPackEntry entry)
    {
        var ok = await AppDialog.ConfirmAsync(Window.GetWindow(this), "Delete this pack?",
            $"{entry.FileName} will be deleted from {entry.Folder.RelativeDir}/ in {_packName}, and "
            + $"{entry.Folder.LoadedByLabel} stops loading it. This can't be undone.",
            "Delete", danger: true);
        if (!ok) return;
        await RunAsync(async () =>
        {
            await Task.Run(() => App.State.GlobalPacks.Remove(_packId, _packName, entry));
            return $"Deleted {entry.FileName}.";
        });
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (GlobalPackService.DefaultTarget(_folders) is not { } target) return;
        try
        {
            var dir = PathSafety.ResolveInside(App.State.Packs.GameDir(_packId, _packName), target.RelativeDir);
            if (dir is null) return;
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Say("Explorer could not be opened there.", danger: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("That folder could not be made - " + ex.Message, danger: true);
        }
    }
}
