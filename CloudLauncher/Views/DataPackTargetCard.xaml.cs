using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>What <see cref="DataPackTargetCard"/> needs to ask where a data pack goes.</summary>
/// <param name="PreferredPackId">The instance to start on.</param>
/// <param name="PreferredWorldDir">A save to tick to start with (the world page the store was opened
/// from).</param>
/// <param name="PackName">The listing's title, for the card's heading.</param>
/// <param name="FilePath">The downloaded zip.</param>
/// <param name="GameVersions">The Minecraft versions the store lists for this build, for the version
/// warning. Empty means unknown.</param>
public sealed record DataPackTargetRequest(
    IReadOnlyList<PackSummary> Packs,
    Guid? PreferredPackId,
    string? PreferredWorldDir,
    string PackName,
    string FilePath,
    IReadOnlyList<string> GameVersions);

/// <summary>Where the user said a data pack goes: some worlds of one instance, and possibly the
/// instance's always-on folder.</summary>
public sealed record DataPackTarget(PackSummary Pack, IReadOnlyList<WorldInfo> Worlds, GlobalPackFolder? Global);

/// <summary>A world row in the card.</summary>
public sealed class DataPackWorldChoice(WorldInfo world, bool isChecked)
{
    public WorldInfo World { get; } = world;
    public string Name => World.DisplayName;
    public string Meta =>
        string.Join("  ·  ", new[]
        {
            World.FolderName,
            World.Info.VersionName is { Length: > 0 } v ? $"MC {v}" : null,
            World.Info.LastPlayed is { } played ? $"played {TimeFormat.Date(played)}" : null
        }.Where(s => s is not null));
    public bool IsChecked { get; set; } = isChecked;
}

/// <summary>
/// The "which worlds?" step after a data pack download: one instance, its worlds as ticks, and
/// "every world" when the instance has a mod that loads data packs globally.
/// </summary>
/// <remarks>
/// <para>One instance per install, unlike <see cref="ImportContentCard"/>'s instance list: a data pack
/// goes into saves, and a tick list of every save in every instance would be long and mostly
/// wrong. Installing into another instance is a second Install.</para>
/// <para>Without a global-pack mod the card says why there is no "every world" box, since "why won't
/// my new world have it?" is the question that follows otherwise.</para>
/// </remarks>
public partial class DataPackTargetCard : UserControl
{
    private readonly TaskCompletionSource<DataPackTarget?> _tcs = new();
    private readonly DataPackTargetRequest _request;
    private List<DataPackWorldChoice> _worlds = [];
    private GlobalPackFolder? _global;
    private int _loadGeneration;

    private DataPackTargetCard(DataPackTargetRequest request)
    {
        InitializeComponent();
        _request = request;
        TitleText.Text = $"Install {request.PackName}";
        FileText.Text = Path.GetFileName(request.FilePath);

        var packs = request.Packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        InstanceBox.ItemsSource = packs;
        InstanceBox.SelectedItem = packs.FirstOrDefault(p => p.Id == request.PreferredPackId) ?? packs.FirstOrDefault();
        if (packs.Count == 0)
        {
            WorldsEmptyText.Text = "There are no instances yet. Make one first, then play it once to create a world.";
            WorldsEmptyText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => Animate.SlideFadeIn(this, 0, 14, 200);
    }

    public Task<DataPackTarget?> Result => _tcs.Task;

    public void Cancel() => _tcs.TrySetResult(null);

    /// <summary>Shows the card over <paramref name="host"/>; null when the user backed out.</summary>
    public static async Task<DataPackTarget?> ShowAsync(MainWindow host, DataPackTargetRequest request)
    {
        var card = new DataPackTargetCard(request);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    private PackSummary? SelectedPack => InstanceBox.SelectedItem as PackSummary;

    private async void OnInstanceChanged(object sender, SelectionChangedEventArgs e)
    {
        try { await LoadInstanceAsync(); }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(DataPackTargetCard), ex);
            SummaryText.Text = "That instance's worlds could not be read - " + ex.Message;
        }
    }

    /// <summary>Reads the instance's worlds and its global-pack mods, off the UI thread.</summary>
    private async Task LoadInstanceAsync()
    {
        if (SelectedPack is not { } pack) return;
        var generation = ++_loadGeneration;
        WorldList.ItemsSource = null;
        WorldsEmptyText.Text = "Reading worlds...";
        WorldsEmptyText.Visibility = Visibility.Visible;

        var (worlds, folders) = await Task.Run(() =>
        {
            List<WorldInfo> found;
            try { found = App.State.Worlds.ScanPack(pack); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { found = []; }
            var global = App.State.GlobalPacks.FoldersFor(pack.Id, pack.Name, pack.MinecraftVersion, GlobalPackKind.Data);
            return (found, global);
        });
        if (generation != _loadGeneration) return;

        var preferred = _request.PreferredWorldDir;
        _worlds = worlds
            .OrderByDescending(w => w.Info.LastPlayed ?? w.LastModified)
            .Select(w => new DataPackWorldChoice(w,
                preferred is not null && string.Equals(Path.GetFullPath(w.FolderPath), Path.GetFullPath(preferred),
                    StringComparison.OrdinalIgnoreCase)))
            .ToList();
        WorldList.ItemsSource = _worlds;
        WorldsEmptyText.Text = DataPackService.VersionHasDataPacks(pack.MinecraftVersion)
            ? "This instance has no worlds yet. Play it once to make one, then add the pack from that world's page."
            : $"Minecraft {pack.MinecraftVersion} has no data packs - they arrived in 1.13.";
        WorldsEmptyText.Visibility = _worlds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _global = GlobalPackService.DefaultTarget(folders);
        GlobalCard.Visibility = _global is null ? Visibility.Collapsed : Visibility.Visible;
        NoGlobalNote.Visibility = _global is null ? Visibility.Visible : Visibility.Collapsed;
        GlobalBox.IsChecked = false;
        if (_global is not null)
        {
            GlobalBox.Content = _global.NewWorldsOnly ? "New worlds" : "Every world, new ones too";
            GlobalNote.Text = (_global.NewWorldsOnly
                    ? $"{_global.LoadedByLabel} adds it to each world you create from now on. "
                    : $"Always on, loaded by {_global.LoadedByLabel}: every world in {pack.Name} gets it, and can't switch it off. ")
                + $"It goes in {_global.RelativeDir}/.";
        }

        VersionWarning.Text = VersionNote(pack.MinecraftVersion);
        VersionWarning.Visibility = VersionWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateChrome();
    }

    /// <summary>A warning when the store doesn't list this build for the instance's version.</summary>
    /// <remarks>Only a warning: data packs often work across versions that share a pack format, and
    /// store tags lag.</remarks>
    private string VersionNote(string? mc)
    {
        if (string.IsNullOrWhiteSpace(mc) || _request.GameVersions.Count == 0) return "";
        var listed = _request.GameVersions
            .Where(v => char.IsDigit(v.FirstOrDefault()))
            .ToList();
        if (listed.Count == 0 || listed.Contains(mc, StringComparer.OrdinalIgnoreCase)) return "";
        return $"This build is listed for Minecraft {string.Join(", ", listed.Take(4))}{(listed.Count > 4 ? "..." : "")}, "
             + $"and this instance runs {mc}. It may still load; the game says so in the world's data pack screen if not.";
    }

    private void OnTickChanged(object sender, RoutedEventArgs e) => UpdateChrome();

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var w in _worlds) w.IsChecked = value;
        // Plain rows with no change notification: rebind so the boxes follow.
        WorldList.ItemsSource = null;
        WorldList.ItemsSource = _worlds;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var worlds = _worlds.Count(w => w.IsChecked);
        var global = GlobalBox.IsChecked == true && _global is not null;
        InstallButton.IsEnabled = SelectedPack is not null && (worlds > 0 || global);
        SummaryText.Text = (worlds, global) switch
        {
            (0, false) => "Nothing ticked yet.",
            (0, true) => "Goes into the always-on folder only.",
            (_, true) => $"Goes into {worlds} world{(worlds == 1 ? "" : "s")} and the always-on folder - "
                         + "a world with both would load it twice, so pick one or the other.",
            _ => $"Goes into {worlds} world{(worlds == 1 ? "" : "s")}."
        };
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack) return;
        var worlds = _worlds.Where(w => w.IsChecked).Select(w => w.World).ToList();
        var global = GlobalBox.IsChecked == true ? _global : null;
        if (worlds.Count == 0 && global is null) return;
        _tcs.TrySetResult(new DataPackTarget(pack, worlds, global));
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ── placing ──────────────────────────────────────────────────────────────

    /// <summary>Copies the pack to every target and returns one line for the status bar. Runs on a
    /// worker thread; each target is tried on its own, so one world in use doesn't stop the rest.</summary>
    public static string Place(DataPackTarget target, string sourcePath)
    {
        var placed = new List<string>();
        var failed = new List<string>();
        foreach (var world in target.Worlds)
        {
            try
            {
                App.State.DataPacks.Add(target.Pack.Id, world.FolderPath, sourcePath);
                placed.Add(world.DisplayName);
            }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(DataPackTargetCard), ex);
                failed.Add($"{world.DisplayName}: {ex.Message}");
            }
        }
        if (target.Global is { } folder)
        {
            try
            {
                App.State.GlobalPacks.Install(target.Pack.Id, target.Pack.Name, folder, sourcePath);
                placed.Add($"every world ({folder.LoadedByLabel})");
            }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(DataPackTargetCard), ex);
                failed.Add($"always-on folder: {ex.Message}");
            }
        }

        var name = Path.GetFileName(sourcePath);
        var head = placed.Count == 0
            ? $"{name} was not installed"
            : $"{name} is in {string.Join(", ", placed)} in {target.Pack.Name}"
              + (target.Worlds.Count > 0 && placed.Count > 0 ? " - it loads the next time the world opens" : "");
        return failed.Count == 0 ? head + "." : $"{head}. {failed.Count} failed - {failed[0]}";
    }
}
