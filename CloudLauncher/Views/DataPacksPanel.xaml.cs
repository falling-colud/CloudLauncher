using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>One of a world's data packs, as a row.</summary>
public sealed class DataPackRow(DataPackInfo info, ImageSource? icon, bool canToggle)
{
    public DataPackInfo Info { get; } = info;
    public string Name => Info.DisplayName;
    public ImageSource? Icon { get; } = icon;

    public string Initial => string.IsNullOrWhiteSpace(Info.DisplayName)
        ? "?"
        : char.ToUpperInvariant(Info.DisplayName[0]).ToString();

    /// <summary>A "New" pack shows as on: the game switches it on by itself at the next load.</summary>
    public bool IsOn => Info.State != DataPackState.Off;

    public bool CanToggle { get; } = canToggle;

    public string StateLabel => Info.State switch
    {
        DataPackState.On => $"#{Info.Priority}",
        DataPackState.New => "NEW",
        _ => "OFF"
    };

    public Visibility StateVisibility => Visibility.Visible;

    public Visibility WarningVisibility => Info.HasPackMeta ? Visibility.Collapsed : Visibility.Visible;

    public string WarningTip =>
        "There is no pack.mcmeta at the top of this pack, so the game will skip it without a word. "
        + "A zip that holds one folder with the pack inside it is the usual cause - unzip it and zip "
        + "what is inside the folder.";

    public string Meta => string.Join("  ·  ", new[]
    {
        Info.IsFolder ? "folder" : Info.FileName,
        SizeLabel(Info.SizeBytes),
        Info.PackFormat is int f ? $"pack_format {f}" : null,
        Info.Description is { Length: > 0 } d ? d.ReplaceLineEndings(" ") : null
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string Tooltip => $"{Info.DisplayName}\n{Info.PackId}\n" + Info.State switch
    {
        DataPackState.On => $"On - #{Info.Priority} counting the game's own packs; #1 wins where two packs change the same thing.",
        DataPackState.New => "New - the game switches it on by itself the next time this world loads.",
        _ => "Off - the file is here but the game leaves it out."
    };

    public string ToggleTip => Info.State == DataPackState.Off
        ? "Switch it on - it goes to the top, as /datapack enable does"
        : "Switch it off - the file stays, the game leaves it out";

    private static string SizeLabel(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };
}

/// <summary>
/// A world's Data packs tab: the packs in its <c>datapacks/</c> folder with the game's own on/off
/// switch, adding (store, file, drag and drop) and removing, and below them the packs a mod such as
/// Paxi loads in every world, or a note on why there are none.
/// </summary>
/// <remarks>
/// The writes all go through <see cref="DataPackService"/>, which refuses while the world is open and
/// backs up level.dat first; this control only reports what it said.
/// </remarks>
public partial class DataPacksPanel : UserControl
{
    private MainWindow? _shell;
    private PackSummary? _pack;
    private WorldInfo? _world;
    private int _generation;

    public DataPacksPanel()
    {
        InitializeComponent();
        GlobalPanel.Configure(GlobalPackKind.Data, "Move into this world only", MoveIntoWorldAsync);
        GlobalPanel.Changed += ReloadQuietly;
    }

    /// <summary>Points the tab at one save and reads it. Never throws; a failure is said on the
    /// tab.</summary>
    public async Task LoadAsync(MainWindow shell, PackSummary pack, WorldInfo world)
    {
        _shell = shell;
        _pack = pack;
        _world = world;
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(DataPacksPanel), ex);
            Say("This world's data packs could not be read - " + ex.Message, danger: true);
        }
    }

    /// <summary><see cref="ReloadAsync"/> for event handlers, which have nowhere to send a failure.</summary>
    private async void ReloadQuietly()
    {
        try { await ReloadAsync(); }
        catch (Exception ex) { AppLog.LogError(nameof(DataPacksPanel), ex); }
    }

    private bool VersionOk =>DataPackService.VersionHasDataPacks(_pack?.MinecraftVersion);

    /// <summary>Re-reads the world's packs and the always-on folders.</summary>
    public async Task ReloadAsync()
    {
        if (_pack is not { } pack || _world is not { } world) return;
        var generation = ++_generation;

        if (!VersionOk)
        {
            HintText.Text = $"Data packs arrived in Minecraft 1.13, and {pack.Name} runs {pack.MinecraftVersion}.";
            PackList.ItemsSource = null;
            EmptyText.Visibility = Visibility.Collapsed;
            BrowseButton.IsEnabled = AddButton.IsEnabled = false;
            GlobalPanel.Visibility = NoGlobalCard.Visibility = Visibility.Collapsed;
            return;
        }

        var (listing, icons) = await Task.Run(() =>
        {
            var found = App.State.DataPacks.List(world.FolderPath);
            var art = found.Packs.Select(p => ResourcePackService.ReadMeta(p.FilePath, p.IsFolder).Icon).ToList();
            return (found, art);
        });
        if (generation != _generation) return;

        PackList.ItemsSource = listing.Packs.Select((p, i) => new DataPackRow(p, icons[i], listing.Readable)).ToList();
        EmptyText.Visibility = listing.Packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = !listing.Readable
            ? "This world's level.dat could not be read, so the packs can't be switched on or off here."
            : "Loaded when the world opens. A change here takes effect the next time it loads."
              + (listing.BuiltInOn > 0
                  ? $" {listing.BuiltInOn} more come from the game and your mods, and aren't listed."
                  : "");

        await GlobalPanel.LoadAsync(pack.Id, pack.Name, pack.MinecraftVersion);
        if (generation != _generation) return;
        GlobalPanel.Visibility = GlobalPanel.HasFolders ? Visibility.Visible : Visibility.Collapsed;
        NoGlobalCard.Visibility = GlobalPanel.HasFolders ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Say(string text, bool danger = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, danger ? "DangerBrush" : "TextSecondaryBrush");
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Runs one write off the UI thread, reports it, and re-reads.</summary>
    private async Task RunAsync(Func<string> work)
    {
        try
        {
            var message = await Task.Run(work);
            await ReloadAsync();
            Say(message);
        }
        catch (DataPackWriteRefusedException refused) { Say(refused.Message, danger: true); }
        catch (GlobalPackRefusedException refused) { Say(refused.Message, danger: true); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(DataPacksPanel), ex);
            Say("That didn't work - " + ex.Message, danger: true);
            await ReloadAsync();
        }
    }

    // ── switching ────────────────────────────────────────────────────────────

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: DataPackRow row } toggle || _pack is not { } pack || _world is not { } world) return;
        // The binding is one-way; the reload paints the real state, including after a refusal.
        var wanted = row.Info.State == DataPackState.Off;
        toggle.IsChecked = row.IsOn;
        await RunAsync(() =>
        {
            App.State.DataPacks.SetEnabled(pack.Id, world.FolderPath, row.Info.FileName, wanted);
            return wanted
                ? $"{row.Name} is on, at the top - it loads the next time the world opens."
                : $"{row.Name} is off - the file stays, the game leaves it out from the next load.";
        });
    }

    // ── adding ───────────────────────────────────────────────────────────────

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (_shell is null || _pack is not { } pack || _world is not { } world) return;
        var page = _shell.OpenDataPackBrowser([pack], pack.Id, world.FolderPath, pushed: true);
        page.Installed += id => { if (id == pack.Id) ReloadQuietly(); };
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add data packs to this world",
            Filter = "Data packs (*.zip)|*.zip|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await AddAsync(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = VersionOk && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        await AddAsync(paths);
    }

    /// <summary>Copies zips and pack folders into the world, skipping anything else with a note.</summary>
    public async Task AddAsync(IReadOnlyList<string> paths)
    {
        if (_pack is not { } pack || _world is not { } world || !VersionOk) return;
        var usable = paths.Where(p => Directory.Exists(p) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        var skipped = paths.Count - usable.Count;
        if (usable.Count == 0)
        {
            Say("Only .zip files and pack folders can be added as data packs.", danger: true);
            return;
        }
        await RunAsync(() =>
        {
            var added = usable.Select(p => Path.GetFileName(App.State.DataPacks.Add(pack.Id, world.FolderPath, p))).ToList();
            var missingMeta = added.Count(name =>
            {
                var path = Path.Combine(DataPackService.FolderFor(world.FolderPath), name);
                return !DataPackService.HasPackMetaFile(path, Directory.Exists(path));
            });
            return (added.Count == 1 ? $"Added {added[0]}" : $"Added {added.Count} data packs")
                   + " - it loads the next time the world opens."
                   + (skipped > 0 ? $" {skipped} other file(s) were not packs and were left out." : "")
                   + (missingMeta > 0 ? $" {missingMeta} of them has no pack.mcmeta at the top, so the game will skip it." : "");
        });
    }

    // ── moving and removing ──────────────────────────────────────────────────

    private void OnRowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DataPackRow row } anchor) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (GlobalPackService.DefaultTarget(GlobalPanel.Folders) is { } target)
            menu.Items.Add(Item($"Move to every world ({target.LoadedByLabel})", () => MoveToEveryWorldAsync(row, target)));
        menu.Items.Add(Item("Reveal in Explorer", () =>
        {
            if (!SafeLaunch.RevealFile(row.Info.FilePath)) Say("Explorer could not be opened there.", danger: true);
            return Task.CompletedTask;
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove from this world...", () => RemoveAsync(row)));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { AppLog.LogError(nameof(DataPacksPanel), ex); }
        };
        return item;
    }

    private async Task RemoveAsync(DataPackRow row)
    {
        if (_pack is not { } pack || _world is not { } world) return;
        var ok = await AppDialog.ConfirmAsync(Window.GetWindow(this), "Remove this data pack?",
            $"{row.Info.FileName} will be deleted from {world.DisplayName}'s datapacks folder. What it already "
            + "added to the world (blocks, advancements, scoreboards) stays in the save. This can't be undone.",
            "Remove", danger: true);
        if (!ok) return;
        await RunAsync(() =>
        {
            App.State.DataPacks.Remove(pack.Id, world.FolderPath, row.Info);
            return $"Removed {row.Info.FileName}.";
        });
    }

    /// <summary>Moves a world's pack into the always-on folder, so every world gets it.</summary>
    /// <remarks>Moved rather than copied: with both, this world would load it twice (once as
    /// <c>file/…</c>, once from the mod), and a pack's functions would run twice.</remarks>
    private async Task MoveToEveryWorldAsync(DataPackRow row, GlobalPackFolder target)
    {
        if (_pack is not { } pack || _world is not { } world) return;
        await RunAsync(() =>
        {
            App.State.DataPacks.EnsureWritable(pack.Id, world.FolderPath);
            App.State.GlobalPacks.Install(pack.Id, pack.Name, target, row.Info.FilePath);
            App.State.DataPacks.Remove(pack.Id, world.FolderPath, row.Info);
            return $"{row.Name} loads in every world of {pack.Name} now, through {target.LoadedByLabel}.";
        });
    }

    /// <summary>The always-on panel's "move out": into this world's own folder, and out of the
    /// always-on one.</summary>
    private async Task<string> MoveIntoWorldAsync(GlobalPackEntry entry)
    {
        if (_pack is not { } pack || _world is not { } world) return "";
        return await Task.Run(() =>
        {
            App.State.DataPacks.Add(pack.Id, world.FolderPath, entry.FilePath);
            App.State.GlobalPacks.Remove(pack.Id, pack.Name, entry);
            return $"{entry.DisplayName} is only in {world.DisplayName} now; other worlds stop loading it.";
        });
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_world is not { } world) return;
        try
        {
            var dir = DataPackService.FolderFor(world.FolderPath);
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Say("Explorer could not be opened there.", danger: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("That folder could not be made - " + ex.Message, danger: true);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await ReloadAsync(); Say(""); }
        catch (Exception ex) { Say("The list could not be read - " + ex.Message, danger: true); }
    }
}
