using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Microsoft.Win32;

namespace CloudLauncher.Views;

/// <summary>
/// The Shaders page: every shader pack installed across the user's instances, which one each instance
/// is set to load, and a search over Modrinth and CurseForge to get more.
/// </summary>
/// <remarks>
/// Shaders had no home in the launcher — they are neither mods nor resource packs, so the only way to
/// add one was to find the instance folder in Explorer. This page also does the thing that actually
/// needs the launcher: switching the active pack writes the shader loader's config, so you can tell
/// what is enabled (and change it) without starting the game.
/// </remarks>
public partial class ShaderPacksView : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<ShaderRow> _rows = new();
    private readonly ObservableCollection<StoreRow> _storeRows = new();
    private List<PackSummary> _packs = new();
    private CancellationTokenSource? _searchCts;
    private bool _loading;

    public ShaderPacksView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        ShaderList.ItemsSource = _rows;
        StoreResults.ItemsSource = _storeRows;
        Loaded += async (_, _) => await LoadAsync();
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        StatusLabel.Text = "Loading…";
        try
        {
            _packs = await App.State.Api.ListPacksAsync();
            _packs = _packs.Where(p => !App.State.Settings.IsPackHidden(p.Id)).ToList();
            RebuildPackFilter();
            Refresh();
            StatusLabel.Text = App.State.Api.PackListStale is { Length: > 0 } why
                ? $"Showing your last known instances — the server is not answering ({why})."
                : "";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally { _loading = false; }
    }

    private void RebuildPackFilter()
    {
        var items = new List<PackFilterItem> { new(null, "All instances") };
        items.AddRange(_packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .Select(p => new PackFilterItem(p.Id, p.Name)));
        var previous = (PackFilterBox.SelectedItem as PackFilterItem)?.Id;
        PackFilterBox.ItemsSource = items;
        PackFilterBox.SelectedItem = items.FirstOrDefault(i => i.Id == previous) ?? items[0];
    }

    private Guid? SelectedPackId => (PackFilterBox.SelectedItem as PackFilterItem)?.Id;

    /// <summary>The instance a new shader is installed into: the filtered one, or the single instance
    /// that already has shaders, or the first in the list. Asking every time would be worse.</summary>
    private Guid? TargetPackId => SelectedPackId ?? _packs.FirstOrDefault()?.Id;

    private void Refresh()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var wanted = SelectedPackId is { } id
            ? _packs.Where(p => p.Id == id).ToList()
            : _packs;

        var found = App.State.Shaders.ScanAll(wanted);
        _rows.Clear();
        foreach (var shader in found)
        {
            if (query.Length > 0
                && !shader.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !shader.SourcePackName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            _rows.Add(new ShaderRow(shader));
        }

        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var total = found.Count;
        var active = found.Count(s => s.IsActive);
        SubLabel.Text = total == 0
            ? "Shaders installed in your instances. Pick one to make it the active pack."
            : $"{total} shader pack(s) across {wanted.Count} instance(s) · {active} active";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();
    private void OnPackFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void OnShaderSelected(object sender, SelectionChangedEventArgs e) { }

    // ── installed shader actions ─────────────────────────────────────────────

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShaderRow row }) return;
        try
        {
            var turnOff = row.Shader.IsActive;
            App.State.Shaders.SetActiveShader(row.Shader.SourcePackId, turnOff ? null : row.Shader.FileName);
            StatusLabel.Text = turnOff
                ? $"Shaders turned off for {row.Shader.SourcePackName}."
                : $"{row.Shader.DisplayName} is now the shader pack for {row.Shader.SourcePackName}. " +
                  "It applies the next time that instance starts.";
            Refresh();
        }
        catch (Exception ex) { StatusLabel.Text = "Could not change the shader: " + ex.Message; }
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShaderRow row }) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = row.Shader.IsFolder ? $"\"{row.Shader.FilePath}\"" : $"/select,\"{row.Shader.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnCopyTo(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShaderRow row }) return;
        var targets = _packs.Where(p => p.Id != row.Shader.SourcePackId).ToList();
        if (targets.Count == 0) { StatusLabel.Text = "There is no other instance to copy it into."; return; }

        var picker = new PackPickerDialog(targets, "Copy shader to…",
            $"Pick the instance to copy {row.Shader.DisplayName} into.", "Copy") { Owner = _shell };
        if (picker.ShowDialog() != true || picker.SelectedPackId is not { } targetId) return;
        try
        {
            App.State.Shaders.CopyTo(row.Shader, targetId);
            StatusLabel.Text = $"Copied {row.Shader.DisplayName} into {targets.First(p => p.Id == targetId).Name}.";
            Refresh();
        }
        catch (Exception ex) { StatusLabel.Text = "Copy failed: " + ex.Message; }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShaderRow row }) return;
        if (!await AppDialog.ConfirmAsync(_shell, "Delete shader pack",
                $"Delete {row.Shader.DisplayName} from {row.Shader.SourcePackName}?\n\nThis removes the file from disk.",
                "Delete", "Cancel", danger: true))
            return;
        try
        {
            App.State.Shaders.Delete(row.Shader);
            StatusLabel.Text = $"Deleted {row.Shader.DisplayName}.";
            Refresh();
        }
        catch (Exception ex) { StatusLabel.Text = "Delete failed: " + ex.Message; }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (TargetPackId is not { } packId) { StatusLabel.Text = "Pick an instance first."; return; }
        try
        {
            var dir = App.State.Shaders.FolderFor(packId);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnAddFromFile(object sender, RoutedEventArgs e)
    {
        if (TargetPackId is not { } packId) { StatusLabel.Text = "Pick an instance first."; return; }
        var dialog = new OpenFileDialog
        {
            Title = "Add shader packs",
            Filter = "Shader packs (*.zip)|*.zip|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        var added = 0;
        foreach (var file in dialog.FileNames)
        {
            try { App.State.Shaders.Install(packId, file); added++; }
            catch (Exception ex) { StatusLabel.Text = $"Could not add {Path.GetFileName(file)}: {ex.Message}"; }
        }
        if (added > 0) StatusLabel.Text = $"Added {added} shader pack(s).";
        Refresh();
    }

    // ── browse the stores ────────────────────────────────────────────────────

    private void OnToggleBrowse(object sender, RoutedEventArgs e)
    {
        var opening = BrowsePanel.Visibility != Visibility.Visible;
        BrowsePanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        BrowseColumn.Width = opening ? new GridLength(360) : new GridLength(0);
        BrowseButton.Content = opening ? "Hide search" : "Get shaders";
        if (opening) StoreQueryBox.Focus();
    }

    private void OnStoreQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSearchStores(sender, e);
    }

    private async void OnSearchStores(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var query = StoreQueryBox.Text?.Trim() ?? "";

        _storeRows.Clear();
        BrowseStatus.Text = "Searching…";
        try
        {
            // Both stores, because shader packs are split across them — Complementary is on both,
            // BSL and Rethinking Voxels effectively live on CurseForge.
            var modrinth = App.State.Modrinth.SearchAsync(query, projectType: "shader", limit: 20, ct: ct);
            var curse = App.State.CurseForge.SearchAsync(query, limit: 20,
                classId: CurseForgeService.ClassIdShaders, ct: ct);
            await Task.WhenAll(modrinth, curse);

            foreach (var summary in (await modrinth).Concat(await curse))
                _storeRows.Add(new StoreRow(summary));

            BrowseStatus.Text = _storeRows.Count == 0
                ? "No shader packs matched."
                : $"{_storeRows.Count} result(s). Install goes into {NameOf(TargetPackId)}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { BrowseStatus.Text = "Search failed: " + ex.Message; }
    }

    private string NameOf(Guid? packId) =>
        packId is null ? "no instance" : _packs.FirstOrDefault(p => p.Id == packId)?.Name ?? "the instance";

    private async void OnInstallFromStore(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StoreRow row }) return;
        if (TargetPackId is not { } packId) { BrowseStatus.Text = "Pick an instance first."; return; }

        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        BrowseStatus.Text = $"Downloading {row.Name}…";
        try
        {
            var pack = _packs.First(p => p.Id == packId);
            // Shaders are not loader-specific, but they are Minecraft-version specific, so the
            // instance's version is the filter that matters.
            var versions = await App.State.ModVersions.GetVersionsAsync(row.Summary, pack.MinecraftVersion, null);
            var version = versions.FirstOrDefault()
                          ?? (await App.State.ModVersions.GetVersionsAsync(row.Summary)).FirstOrDefault();
            if (version is null) { BrowseStatus.Text = "That pack has no downloadable version."; return; }

            var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
            var url = file?.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url) && version.Source == ModSource.CurseForge)
            {
                var ids = version.Id.Split(':');
                if (ids.Length == 2 && int.TryParse(ids[0], out var modId) && int.TryParse(ids[1], out var fileId))
                    url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
            }
            if (string.IsNullOrWhiteSpace(url) || file is null)
            {
                BrowseStatus.Text = "No download link for that pack — install it from its site instead.";
                return;
            }

            var dir = App.State.Shaders.FolderFor(packId);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Path.GetFileName(file.Filename));
            await App.State.Modrinth.DownloadFileAsync(url, dest);

            BrowseStatus.Text = $"Installed {row.Name} into {pack.Name}.";
            Refresh();
        }
        catch (Exception ex) { BrowseStatus.Text = "Install failed: " + ex.Message; }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    // ── rows ─────────────────────────────────────────────────────────────────

    private sealed record PackFilterItem(Guid? Id, string Label);

    private sealed class ShaderRow(ShaderPackInfo shader)
    {
        public ShaderPackInfo Shader { get; } = shader;
        public string DisplayName => Shader.DisplayName;
        public string MetaLabel => Shader.MetaLabel;
        public string ApplyLabel => Shader.IsActive ? "Turn off" : "Use";
        public Visibility ActiveVisibility => Shader.IsActive ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FolderVisibility => Shader.IsFolder ? Visibility.Visible : Visibility.Collapsed;

        public Brush BorderBrushForState => (Brush)Application.Current.Resources[
            Shader.IsActive ? "AccentBrush" : "BorderSubtleBrush"];
    }

    private sealed class StoreRow(ModSummary summary)
    {
        public ModSummary Summary { get; } = summary;
        public string Name => Summary.Name;
        public string Meta =>
            $"{(Summary.Source == ModSource.CurseForge ? "CurseForge" : "Modrinth")}" +
            $"{(Summary.Author is { Length: > 0 } a ? $"  ·  {a}" : "")}" +
            $"{(Summary.DownloadCount > 0 ? $"  ·  {Summary.DownloadCount:N0} downloads" : "")}";
    }
}
