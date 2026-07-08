using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Microsoft.Win32;

namespace CloudLauncher.Views;

public partial class ModListView : UserControl
{
    private PackDetail? _pack;
    private Window? _ownerWindow;
    private readonly ObservableCollection<ModRow> _rows = new();
    private readonly ICollectionView _view;
    private int _scanGeneration;
    private string _searchText = "";

    private const double SearchCollapsedWidth = 36;
    private const double SearchExpandedWidth = 240;

    public ModListView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = ModFilter;
        ModGrid.ItemsSource = _view;
    }

    public void Load(PackDetail pack, Window owner)
    {
        _pack = pack;
        _ownerWindow = owner;
        _ = ScanModsAsync();
    }

    // ── scan + match ──────────────────────────────────────────────────────────

    private static int FolderPriority(string folder) => folder switch
    {
        "local" => 0,
        _ => 1
    };

    private static string ModFileKey(string path)
    {
        var name = Path.GetFileName(path) ?? "";
        if (name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            return name[..^".disabled".Length];
        return name;
    }

    private async Task ScanModsAsync()
    {
        if (_pack is null) return;
        var generation = ++_scanGeneration;
        StatusLabel.Text = "Scanning mods...";
        _rows.Clear();

        var pack = _pack;
        var jars = await Task.Run(() =>
        {
            var found = new List<(string path, string folder, bool isEnabled)>();
            void AddFolder(string dir, string label)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                        found.Add((f, label, false));
                    else if (f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        found.Add((f, label, true));
                }
            }
            AddFolder(Path.Combine(App.State.Packs.GameDir(pack.Id), "mods"), "game");
            if (pack.IsShared)
                AddFolder(Path.Combine(App.State.Packs.LocalDir(pack.Id), "mods"), "local");
            return found
                .OrderBy(j => FolderPriority(j.folder))
                .Aggregate(new Dictionary<string, (string path, string folder, bool isEnabled)>(StringComparer.OrdinalIgnoreCase),
                    (deduped, item) =>
                    {
                        var key = ModFileKey(item.path);
                        if (!deduped.ContainsKey(key))
                            deduped[key] = item;
                        return deduped;
                    })
                .Values
                .ToList();
        });

        if (generation != _scanGeneration) return;
        if (jars.Count == 0) { StatusLabel.Text = "No mod files found in any mods/ folder."; return; }

        var cache = App.State.ModFingerprints;
        // ConcurrentDictionary: these are written from the Parallel.ForEach below, and plain
        // Dictionary writes from multiple threads can corrupt it (hang / IndexOutOfRangeException).
        var pathToSha = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathToCurseForgeFingerprint = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var toHash = new List<string>();

        foreach (var (path, _, _) in jars)
        {
            if (cache.TryGet(path, out var entry))
            {
                pathToSha[path] = entry.Sha512;
                pathToCurseForgeFingerprint[path] = entry.CurseForgeFingerprint;
            }
            else
            {
                toHash.Add(path);
            }
        }

        if (toHash.Count == 0)
            StatusLabel.Text = "Loading mods...";
        else
            StatusLabel.Text = toHash.Count == jars.Count
                ? $"Identifying {toHash.Count} mod(s)..."
                : $"Identifying {toHash.Count} new/changed mod(s)...";

        var allCached = toHash.Count == 0;
        if (allCached && jars.All(j => cache.TryGetCachedMatch(j.path, out _, out _)))
        {
            if (generation != _scanGeneration) return;
            PopulateRows(
                jars,
                pathToSha,
                pathToCurseForgeFingerprint,
                cache.ResolveModrinthMatches(pathToSha.Values),
                cache.ResolveCurseForgeMatches(pathToCurseForgeFingerprint.Values),
                cache);
            UpdateStatusLabel();
            _ = CheckUpdatesInBackgroundAsync(generation);
            return;
        }

        if (toHash.Count > 0)
        {
            await Task.Run(() =>
            {
                Parallel.ForEach(toHash, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
                {
                    try
                    {
                        var (sha512, fingerprint) = ModFingerprintCache.ComputeHashes(path);
                        cache.Store(path, sha512, fingerprint);
                        pathToSha[path] = sha512;
                        pathToCurseForgeFingerprint[path] = fingerprint;
                    }
                    catch { /* locked file */ }
                });
            });
        }

        if (generation != _scanGeneration) return;

        var modrinthMatches = cache.ResolveModrinthMatches(pathToSha.Values);
        var missingSha = pathToSha.Values
            .Where(h => !modrinthMatches.ContainsKey(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingSha.Count > 0)
        {
            try
            {
                var fresh = await App.State.Modrinth.MatchHashesAsync(missingSha);
                foreach (var (hash, match) in fresh)
                {
                    modrinthMatches[hash] = match;
                    cache.RememberModrinthMatch(hash, match.mod, match.version);
                }
            }
            catch { /* no network / API error */ }
        }

        if (generation != _scanGeneration) return;

        var curseForgeMatches = cache.ResolveCurseForgeMatches(pathToCurseForgeFingerprint.Values);
        var missingFingerprints = pathToCurseForgeFingerprint.Values
            .Where(f => !curseForgeMatches.ContainsKey(f))
            .Distinct()
            .ToList();
        if (missingFingerprints.Count > 0)
        {
            try
            {
                var fresh = await App.State.CurseForge.MatchFingerprintsAsync(missingFingerprints);
                foreach (var (fingerprint, match) in fresh)
                {
                    curseForgeMatches[fingerprint] = match;
                    cache.RememberCurseForgeMatch(fingerprint, match.mod, match.version);
                }
            }
            catch { /* no network / API error */ }
        }

        if (generation != _scanGeneration) return;

        PopulateRows(jars, pathToSha, pathToCurseForgeFingerprint, modrinthMatches, curseForgeMatches, cache);
        cache.Flush();
        UpdateStatusLabel();
        _ = CheckUpdatesInBackgroundAsync(generation);
    }

    private void PopulateRows(
        IReadOnlyList<(string path, string folder, bool isEnabled)> jars,
        IReadOnlyDictionary<string, string> pathToSha,
        IReadOnlyDictionary<string, long> pathToCurseForgeFingerprint,
        Dictionary<string, (ModSummary mod, ModVersion version)> modrinthMatches,
        Dictionary<long, (ModSummary mod, ModVersion version)> curseForgeMatches,
        ModFingerprintCache cache)
    {
        _rows.Clear();
        foreach (var (path, folder, isEnabled) in jars)
        {
            if (pathToSha.TryGetValue(path, out var sha512)
                && modrinthMatches.TryGetValue(sha512, out var modrinthMatch))
            {
                cache.StoreMatch(path, modrinthMatch.mod, modrinthMatch.version);
                _rows.Add(new ModRow(path, folder, isEnabled, modrinthMatch.mod, modrinthMatch.version, null));
            }
            else if (pathToCurseForgeFingerprint.TryGetValue(path, out var fingerprint)
                     && curseForgeMatches.TryGetValue(fingerprint, out var curseForgeMatch))
            {
                cache.StoreMatch(path, curseForgeMatch.mod, curseForgeMatch.version);
                _rows.Add(new ModRow(path, folder, isEnabled, curseForgeMatch.mod, curseForgeMatch.version, null));
            }
            else if (cache.TryGetCachedMatch(path, out var cachedMod, out var cachedVersion))
            {
                _rows.Add(new ModRow(path, folder, isEnabled, cachedMod, cachedVersion, null));
            }
            else
            {
                _rows.Add(new ModRow(path, folder, isEnabled, null, null, null));
            }
        }
    }

    private async Task CheckUpdatesInBackgroundAsync(int generation)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (generation != _scanGeneration) return;
            var row = _rows[i];
            if (row.LinkedMod is null || row.LinkedVersion is null) continue;

            var latestVer = await TryGetLatestCompatibleAsync(row.LinkedMod, row.LinkedVersion);
            if (generation != _scanGeneration) return;
            if (latestVer is null) continue;

            _rows[i] = new ModRow(
                row.FilePath, row.Folder, row.IsEnabled,
                row.LinkedMod, row.LinkedVersion, latestVer);
        }
    }

    private void UpdateStatusLabel()
    {
        var total = _rows.Count;
        var linked = _rows.Count(r => r.LinkedMod != null);
        var external = _rows.Count(r => r.IsExternal);
        var disabled = _rows.Count(r => !r.IsEnabled);
        var visible = _view.Cast<object>().Count();

        var summary = $"{total} mod(s) — {linked} linked, {external} external";
        if (disabled > 0) summary += $", {disabled} disabled";
        if (!string.IsNullOrWhiteSpace(_searchText))
            summary += $" — showing {visible} of {total}";
        StatusLabel.Text = summary;
    }

    private bool ModFilter(object item)
    {
        if (item is not ModRow row) return false;
        var query = _searchText.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        query = query.ToLowerInvariant();
        return row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Version.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Folder.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        _searchText = SearchBox.Text;
        _view.Refresh();
        UpdateStatusLabel();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSearchToggle(object sender, RoutedEventArgs e)
    {
        ExpandSearch();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchBox.Text = "";
        CollapseSearch();
        e.Handled = true;
    }

    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject nextFocus && IsDescendantOf(nextFocus, CompactSearchHost))
            return;
        CollapseSearch();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ExpandSearch();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void ExpandSearch()
    {
        CompactSearchHost.Width = SearchExpandedWidth;
        SearchBox.Visibility = Visibility.Visible;
    }

    private void CollapseSearch()
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        CompactSearchHost.Width = SearchCollapsedWidth;
        SearchBox.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private async Task<ModVersion?> TryGetLatestCompatibleAsync(ModSummary mod, ModVersion installed)
    {
        try
        {
            var mc = _pack?.MinecraftVersion;
            var loader = _pack?.Loader == LoaderKind.None ? null : _pack?.Loader.ToString().ToLowerInvariant();
            var versions = mod.Source == ModSource.CurseForge && int.TryParse(mod.Id, out var curseForgeId)
                ? (await App.State.CurseForge.GetVersionsAsync(curseForgeId))
                    .Where(v => IsCompatibleVersion(v, mc, loader))
                    .ToList()
                : await App.State.Modrinth.GetVersionsAsync(mod.Id, mc, loader);
            var latest = versions.FirstOrDefault(v => v.ReleaseChannel == "release") ?? versions.FirstOrDefault();
            return latest?.Id != installed.Id ? latest : null;
        }
        catch { return null; }
    }

    private static bool IsCompatibleVersion(ModVersion version, string? mc, string? loader)
    {
        var supportsMinecraft = string.IsNullOrEmpty(mc)
            || version.GameVersions.Any(v => string.Equals(v, mc, StringComparison.OrdinalIgnoreCase));
        var supportsLoader = string.IsNullOrEmpty(loader)
            || version.Loaders.Any(v => string.Equals(v, loader, StringComparison.OrdinalIgnoreCase));
        return supportsMinecraft && supportsLoader;
    }

    // When the mods grid gets narrow (e.g. inside a side panel), shed the less
    // important columns in priority order so the mod Name always keeps room and
    // wins over the Source label and the Update button.
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var w = e.NewSize.Width;
        UpdateColumn.Visibility  = w < 440 ? Visibility.Collapsed : Visibility.Visible;
        VersionColumn.Visibility = w < 360 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnRefresh(object s, RoutedEventArgs e) => await ScanModsAsync();

    private void OnOpenManagement(object s, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (_ownerWindow is MainWindow main)
            main.OpenModManagementForPack(_pack);
        else if (_ownerWindow is MinecraftHostWindow host)
            host.OpenModExplorerForPack(_pack); // in-game overlay keeps the lightweight browser
        else
            new ModExplorerWindow(_pack) { Owner = _ownerWindow }.Show();
    }

    private async void OnImportModpack(object s, RoutedEventArgs e)
    {
        if (_pack is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Select modpack file",
            Filter = "Modpack files|*.mrpack;*.zip|Modrinth pack (*.mrpack)|*.mrpack|CurseForge pack (*.zip)|*.zip"
        };
        if (dlg.ShowDialog(_ownerWindow) != true) return;

        var path = dlg.FileName;
        var name = Path.GetFileNameWithoutExtension(path);
        await App.State.ModpackDownload.StartLocalFileImportAsync(path, name);
    }

    private async void OnModAction(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement el && el.DataContext is ModRow row && row.LatestVersion != null)
            await UpdateRowAsync(row);
    }

    private void OnModDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (ModGrid.SelectedItem is ModRow row && row.LinkedMod != null && _pack is not null)
        {
            if (_ownerWindow is MinecraftHostWindow host)
                host.OpenModExplorerForPack(_pack);
            else if (_ownerWindow is MainWindow main)
                main.OpenModExplorerForPack(_pack);
            else
                new ModExplorerWindow(_pack) { Owner = _ownerWindow }.Show();
        }
    }

    // ── context-menu actions ─────────────────────────────────────────────────

    private void OnModGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { } row)
        {
            row.IsSelected = true;
            ModGrid.SelectedItem = row.Item;
        }
    }

    private void OnModContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var row = ModGrid.SelectedItem as ModRow;
        CtxCreateHostedMod.IsEnabled = row is { IsExternal: true };
        CtxEnableMod.Visibility = row is { IsEnabled: false } ? Visibility.Visible : Visibility.Collapsed;
        CtxDisableMod.Visibility = row is { IsEnabled: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCtxOpenPage(object s, RoutedEventArgs e)
    {
        if (ModGrid.SelectedItem is not ModRow row || row.LinkedMod is null) return;
        var slug = string.IsNullOrEmpty(row.LinkedMod.Slug) ? row.LinkedMod.Id : row.LinkedMod.Slug;
        var url = row.LinkedMod.Source == ModSource.CurseForge
            ? $"https://www.curseforge.com/minecraft/mc-mods/{slug}"
            : $"https://modrinth.com/mod/{slug}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnCtxUpdate(object s, RoutedEventArgs e)
    {
        if (ModGrid.SelectedItem is not ModRow row || !row.HasUpdate) return;
        await UpdateRowAsync(row);
    }

    private async void OnCtxEnable(object s, RoutedEventArgs e)
    {
        if (ModGrid.SelectedItem is not ModRow row || row.IsEnabled) return;
        await SetModEnabledAsync(row, true);
    }

    private async void OnCtxDisable(object s, RoutedEventArgs e)
    {
        if (ModGrid.SelectedItem is not ModRow row || !row.IsEnabled) return;
        await SetModEnabledAsync(row, false);
    }

    private async void OnModEnabledToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.DataContext is not ModRow row) return;
        var wantEnabled = toggle.IsChecked == true;
        if (wantEnabled == row.IsEnabled) return;

        var ok = await SetModEnabledAsync(row, wantEnabled);
        if (!ok) toggle.IsChecked = row.IsEnabled;
    }

    private Task<bool> SetModEnabledAsync(ModRow row, bool enabled)
    {
        try
        {
            var newPath = enabled ? EnableModPath(row.FilePath) : DisableModPath(row.FilePath);
            ReplaceRow(row, newPath, enabled);
            StatusLabel.Text = enabled ? $"Enabled {row.Name}." : $"Disabled {row.Name}.";
            UpdateStatusLabel();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = enabled ? "Enable failed: " + ex.Message : "Disable failed: " + ex.Message;
            return Task.FromResult(false);
        }
    }

    private void ReplaceRow(ModRow row, string newPath, bool isEnabled)
    {
        var idx = _rows.IndexOf(row);
        if (idx < 0) return;
        _rows[idx] = row.WithPath(newPath, isEnabled);
    }

    private static string EnableModPath(string path)
    {
        if (!path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mod is already enabled.");
        var enabledPath = path[..^".disabled".Length];
        File.Move(path, enabledPath);
        return enabledPath;
    }

    private static string DisableModPath(string path)
    {
        if (path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mod is already disabled.");
        if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only .jar mods can be disabled.");
        var disabledPath = path + ".disabled";
        File.Move(path, disabledPath);
        return disabledPath;
    }

    private async void OnCtxCreateHostedMod(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (ModGrid.SelectedItem is not ModRow row) return;
        if (!row.IsExternal)
        {
            StatusLabel.Text = "This jar is already linked to a known mod.";
            return;
        }
        if (!File.Exists(row.FilePath))
        {
            StatusLabel.Text = "Jar file no longer exists.";
            return;
        }

        var defaultName = CleanModName(Path.GetFileNameWithoutExtension(row.FilePath));
        var nameDialog = new SimpleInputDialog("Create hosted mod", "Mod name", defaultName)
        {
            Owner = _ownerWindow
        };
        if (nameDialog.ShowDialog() != true) return;

        var modName = (nameDialog.Result ?? "").Trim();
        if (string.IsNullOrWhiteSpace(modName))
        {
            StatusLabel.Text = "Mod name cannot be empty.";
            return;
        }

        var defaultVersion = GuessVersion(row.FilePath);
        var versionDialog = new SimpleInputDialog("Upload first version", "Version string", defaultVersion)
        {
            Owner = _ownerWindow
        };
        if (versionDialog.ShowDialog() != true) return;

        var version = (versionDialog.Result ?? "").Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusLabel.Text = "Version string cannot be empty.";
            return;
        }

        StatusLabel.Text = $"Creating hosted mod for {Path.GetFileName(row.FilePath)}...";
        try
        {
            var loadersCsv = PackLoaderCsv(_pack);
            var created = await App.State.Api.CreateModAsync(new CreateModRequest(
                modName,
                $"Created from {Path.GetFileName(row.FilePath)}",
                null,
                PackVisibility.Private,
                _pack.MinecraftVersion,
                loadersCsv));

            StatusLabel.Text = "Uploading first version...";
            await App.State.Api.UploadModVersionAsync(created.Id, row.FilePath, new CreateModVersionRequest(
                version,
                null,
                "release",
                Path.GetFileName(row.FilePath),
                _pack.MinecraftVersion,
                loadersCsv));

            StatusLabel.Text = $"Created hosted mod '{created.Name}'.";
            await ScanModsAsync();
            if (_ownerWindow is MinecraftHostWindow host)
                host.OpenModDetail(created.Id, created.Name);
            else if (_ownerWindow is MainWindow main)
                main.OpenModDetail(created.Id, created.Name);
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            StatusLabel.Text = "Hosted mod support is not deployed on the server yet.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Create hosted mod failed: " + ex.Message;
        }
    }

    private void OnCtxReveal(object s, RoutedEventArgs e)
    {
        if (ModGrid.SelectedItem is not ModRow row) return;
        var dir = Path.GetDirectoryName(row.FilePath);
        if (dir is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnCtxDelete(object s, RoutedEventArgs e)
    {
        if (ModGrid.SelectedItem is not ModRow row) return;
        if (MessageBox.Show(_ownerWindow,
                $"Delete {Path.GetFileName(row.FilePath)}?",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            File.Delete(row.FilePath);
            await ScanModsAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async Task UpdateRowAsync(ModRow row)
    {
        StatusLabel.Text = $"Updating {row.Name}…";
        var file = row.LatestVersion!.Files.FirstOrDefault(f => f.IsPrimary) ?? row.LatestVersion.Files.FirstOrDefault();
        if (file is null) { StatusLabel.Text = "No downloadable file."; return; }
        try
        {
            file = await EnsureDownloadableFileAsync(row.LatestVersion, file);
            if (string.IsNullOrWhiteSpace(file.DownloadUrl))
            {
                StatusLabel.Text = "No downloadable file.";
                return;
            }

            var dest = Path.Combine(Path.GetDirectoryName(row.FilePath)!, file.Filename);
            await App.State.Modrinth.DownloadFileAsync(file.DownloadUrl, dest);
            if (!string.Equals(row.FilePath, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(row.FilePath))
                File.Delete(row.FilePath);
            StatusLabel.Text = $"Updated {row.Name}.";
            await ScanModsAsync();
        }
        catch (Exception ex) { StatusLabel.Text = "Update failed: " + ex.Message; }
    }

    private static async Task<ModVersionFile> EnsureDownloadableFileAsync(ModVersion version, ModVersionFile file)
    {
        if (version.Source != ModSource.CurseForge || !string.IsNullOrWhiteSpace(file.DownloadUrl)) return file;

        var ids = version.Id.Split(':');
        if (ids.Length != 2
            || !int.TryParse(ids[0], out var modId)
            || !int.TryParse(ids[1], out var fileId))
            return file;

        var url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
        return string.IsNullOrWhiteSpace(url) ? file : file with { DownloadUrl = url };
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string PackLoaderCsv(PackDetail pack) =>
        pack.Loader == LoaderKind.None ? "" : pack.Loader.ToString().ToLowerInvariant();

    private static string CleanModName(string? fileStem)
    {
        var stem = string.IsNullOrWhiteSpace(fileStem) ? "New mod" : fileStem.Trim();
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[-_]+", " ");
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(stem) ? "New mod" : stem;
    }

    private static string GuessVersion(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var match = System.Text.RegularExpressions.Regex.Match(stem, @"(?<!\d)(\d+\.\d+(?:\.\d+)?(?:[-+][A-Za-z0-9_.-]+)?)(?!\d)");
        return match.Success ? match.Groups[1].Value : "1.0.0";
    }
}

// ── row view model ────────────────────────────────────────────────────────────

public sealed class ModRow
{
    public string FilePath     { get; }
    public string Folder       { get; }
    public bool IsEnabled      { get; }
    public ModSummary? LinkedMod     { get; }
    public ModVersion? LinkedVersion { get; }
    public ModVersion? LatestVersion { get; set; }

    public bool IsExternal  => LinkedMod is null;
    public bool HasUpdate   => LatestVersion is not null;

    public string Name       => LinkedMod?.Name ?? DisplayFileStem(FilePath);
    public string Version    => LinkedVersion?.VersionNumber ?? "(unknown)";
    public long VersionSortKey => ComputeVersionSortKey(LinkedVersion?.VersionNumber);
    public string Initial    => (LinkedMod?.Name ?? DisplayFileStem(FilePath) ?? "?").Trim() is { Length: > 0 } name
        ? name[..1].ToUpperInvariant()
        : "?";
    public string IconUrl    => LinkedMod?.IconUrl ?? "";
    public string SourceLabel => LinkedMod?.Source switch
        { ModSource.Modrinth => "Modrinth", ModSource.CurseForge => "CurseForge", _ => "External" }
        ?? "External";
    public string SourceIconUrl => LinkedMod?.Source switch
        {
            ModSource.Modrinth => "https://modrinth.com/favicon.ico",
            ModSource.CurseForge => "https://www.curseforge.com/favicon.ico",
            _ => ""
        }
        ?? "";

    public ModRow(string path, string folder, bool isEnabled, ModSummary? mod, ModVersion? ver, ModVersion? latest)
    {
        FilePath = path; Folder = folder; IsEnabled = isEnabled; LinkedMod = mod; LinkedVersion = ver; LatestVersion = latest;
    }

    public ModRow WithPath(string path, bool isEnabled) =>
        new(path, Folder, isEnabled, LinkedMod, LinkedVersion, LatestVersion);

    private static string DisplayFileStem(string path)
    {
        var name = Path.GetFileName(path) ?? "";
        if (name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            name = name[..^".disabled".Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static long ComputeVersionSortKey(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.Split('.', '-', '_');
        long value = 0;
        for (var i = 0; i < 4; i++)
        {
            value *= 1000;
            if (i >= parts.Length) continue;
            if (!int.TryParse(parts[i], out var part)) break;
            value += Math.Clamp(part, 0, 999);
        }
        return value;
    }
}
