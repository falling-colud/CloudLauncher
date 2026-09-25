using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// One shader pack's page: what it is, and which instances it goes into.
/// </summary>
/// <remarks>
/// Mirrors <see cref="LocalResourcePackDetailView"/>; the copy covers the differences (an instance
/// loads one shader pack, and a pack does nothing without Iris, Oculus or OptiFine). Every instance
/// starts ticked and unticking narrows it. The page can open for a pack only inside an instance
/// (<see cref="_item"/> is null) or for a library pack no instance has yet (second constructor).
/// </remarks>
public partial class ShaderPackDetailView : Page
{
    private readonly MainWindow _shell;

    /// <summary>One instance's copy of the pack: <c>&lt;instanceGuid&gt;:&lt;fileName&gt;</c>. Null
    /// when the page was opened for the library item instead.</summary>
    private readonly string? _packKey;

    /// <summary>The library item this page is about, by <see cref="LibraryItem.Key"/>. Null on the
    /// installed-key route.</summary>
    /// <remarks>Re-resolved on every reload: <see cref="ContentPlacement.SetTicked"/> rewrites the
    /// item's policy, so a copy kept from when the page opened goes stale.</remarks>
    private readonly string? _libraryKey;

    private readonly ObservableCollection<PackCompatibilityRow> _packRows = new();

    private ShaderPackInfo? _pack;
    private LibraryItem? _item;
    private List<PackSummary> _allPacks = new();

    /// <summary>Which instances hold a copy of <see cref="_item"/> right now, by instance id. Null
    /// means unknown (not asked, or the read failed), which is different from none.</summary>
    private Dictionary<Guid, LibraryTargetState>? _placement;

    /// <summary>Set while filling the boxes: assigning IsChecked can raise Click, which would write
    /// back the list being read.</summary>
    private bool _suppress;

    /// <summary>The page for one instance's copy of a pack.</summary>
    public ShaderPackDetailView(MainWindow shell, string packKey) : this(shell) =>
        _packKey = packKey;

    /// <summary>The same page for a pack the launcher holds, whether or not any instance has a copy
    /// yet.</summary>
    /// <inheritdoc cref="LocalResourcePackDetailView(MainWindow,LibraryItem)" path="/remarks"/>
    public ShaderPackDetailView(MainWindow shell, LibraryItem item) : this(shell) =>
        _libraryKey = item.Key;

    private ShaderPackDetailView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        PackCheckList.ItemsSource = _packRows;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _allPacks = await App.State.Api.ListPacksAsync();
            if (_libraryKey is { } libraryKey) await ReloadFromLibraryAsync(libraryKey);
            else if (_packKey is { } packKey) await ReloadFromInstanceAsync(packKey);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async Task ReloadFromInstanceAsync(string packKey)
    {
        _pack = FindPack(packKey);

        if (_pack is null) { StatusLabel.Text = "That shader pack is no longer there."; return; }
        if (!File.Exists(_pack.FilePath) && !Directory.Exists(_pack.FilePath))
        {
            StatusLabel.Text = "This shader pack was deleted on disk.";
            return;
        }

        _item = FindLibraryItem(_pack);
        await LoadPlacementAsync();

        _suppress = true;
        try
        {
            PackNameLabel.Text = _pack.DisplayName;
            NameBox.Text = _pack.DisplayName;
            SourcePackLabel.Text = _pack.SourcePackName.ToUpperInvariant();
            MetaLabel.Text = $"Modified {ModifiedLabel(_pack.LastModified)} · {_pack.SizeLabel}";
            FilePathLabel.Text = _pack.FilePath;
            ApplyPackMeta(_pack);
            FillInstances();
        }
        finally { _suppress = false; }
    }

    /// <summary>
    /// Builds the page from the launcher's own copy, with no instance.
    /// </summary>
    /// <remarks>Per-instance facts (whether the shader config selects this pack, whether a loader is
    /// installed) are shown on each row of the list, not in the hero.</remarks>
    private async Task ReloadFromLibraryAsync(string libraryKey)
    {
        _pack = null;
        _item = App.State.Library.Scan(LibraryKind.ShaderPack)
            .FirstOrDefault(i => string.Equals(i.Key, libraryKey, StringComparison.OrdinalIgnoreCase));

        if (_item is null)
        {
            StatusLabel.Text = "The launcher no longer has that shader pack.";
            PackListWrap.Visibility = Visibility.Collapsed;
            InstanceStateNote.Text = "The launcher no longer has a copy of this pack, so there is "
                                   + "nothing to hand out and nothing to tick here.";
            return;
        }

        await LoadPlacementAsync();

        _suppress = true;
        try
        {
            var item = _item;
            PackNameLabel.Text = item.DisplayName;
            NameBox.Text = item.DisplayName;
            // Same wording as the content pages and the Resources tab. The installed-key route shows the
            // instance name here instead; ".library" would mean nothing to the user.
            SourcePackLabel.Text = "SHARED COPY";
            MetaLabel.Text = $"Modified {TimeFormat.DateTime(item.LastModified)} · {item.SizeLabel}";
            FilePathLabel.Text = item.Path;
            FileScopeNote.Text = "This is the shared copy - one file, linked into each instance that "
                               + "gets it. \"Reveal in Explorer\" opens it here. " + HoldingSentence();
            FileScopeNote.Visibility = Visibility.Visible;
            ApplyLibraryMeta(item);
            FillInstances();
        }
        finally { _suppress = false; }
    }

    /// <summary>The pack behind this page's key, re-scanned on every open.</summary>
    /// <remarks>The key carries the instance id, so only that instance's folder is scanned. The Shaders
    /// page may still be showing an older scan.</remarks>
    private ShaderPackInfo? FindPack(string packKey)
    {
        var parts = packKey.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var sourceId)) return null;
        var pack = _allPacks.FirstOrDefault(p => p.Id == sourceId);
        if (pack is null) return null;
        try { return App.State.Shaders.ScanPack(pack.Id, pack.Name).FirstOrDefault(s => s.Key == packKey); }
        catch (Exception ex) { AppLog.LogError(nameof(ShaderPackDetailView), ex); return null; }
    }

    /// <summary>The library's copy of this pack, or null when the launcher has none.</summary>
    /// <remarks>Matched by file name and content, like the Shaders list: an instance's own pack with
    /// the same name is a different file.</remarks>
    private static LibraryItem? FindLibraryItem(ShaderPackInfo pack)
    {
        try
        {
            return App.State.Library.Scan(LibraryKind.ShaderPack).FirstOrDefault(i =>
                string.Equals(i.FileName, pack.FileName, StringComparison.OrdinalIgnoreCase)
                && PackFolderService.EntriesReferToSameContent(i.Path, pack.FilePath));
        }
        catch (Exception ex) { AppLog.LogError(nameof(ShaderPackDetailView), ex); return null; }
    }

    private void ApplyPackMeta(ShaderPackInfo pack)
    {
        PackInitialLabel.Text = string.IsNullOrWhiteSpace(pack.DisplayName)
            ? "?"
            : pack.DisplayName.Trim()[..1].ToUpperInvariant();

        EnabledPill.Visibility = pack.IsActive ? Visibility.Visible : Visibility.Collapsed;
        EnabledPillText.Text = "LOADED HERE";

        // Detected live, since the page may open from an older scan that hasn't checked this instance.
        // If detection fails, show no warning rather than a guessed one.
        var blind = false;
        try
        {
            blind = App.State.Shaders.DetectLoader(pack.SourcePackId, pack.SourcePackName) == ShaderLoader.None;
        }
        catch (Exception ex) { AppLog.LogError(nameof(ShaderPackDetailView), ex); }
        LoaderPill.Visibility = blind ? Visibility.Visible : Visibility.Collapsed;

        var kind = pack.IsFolder ? "SHADER FOLDER" : pack.IsLocal ? "LOCAL ONLY" : null;
        KindPill.Visibility = kind is null ? Visibility.Collapsed : Visibility.Visible;
        KindPillText.Text = kind ?? "";

        var provenance = pack.ProvenanceLabel;
        ProvenanceLabel.Visibility = provenance is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        ProvenanceLabel.Text = provenance ?? "";

        PlacementPill.Visibility = Visibility.Collapsed;
    }

    /// <summary>The same hero, read from the library file rather than an instance's.</summary>
    /// <remarks>The "loaded here" and "needs Iris" pills are per-instance facts, so they stay hidden;
    /// the list below shows both for each instance.</remarks>
    private void ApplyLibraryMeta(LibraryItem item)
    {
        PackInitialLabel.Text = string.IsNullOrWhiteSpace(item.DisplayName)
            ? "?"
            : item.DisplayName.Trim()[..1].ToUpperInvariant();

        EnabledPill.Visibility = Visibility.Collapsed;
        LoaderPill.Visibility = Visibility.Collapsed;

        // No pill when the count is unknown; the Local file card's note already says the instances
        // couldn't be read.
        var holding = HoldingCount();
        PlacementPill.Visibility = holding is null ? Visibility.Collapsed : Visibility.Visible;
        PlacementPillText.Text = holding switch
        {
            null => "",
            0 => "IN NO INSTANCE YET",
            1 => "IN 1 INSTANCE",
            _ => $"IN {holding} INSTANCES"
        };
        PlacementPill.ToolTip = HoldingSentence();

        KindPill.Visibility = item.IsFolder ? Visibility.Visible : Visibility.Collapsed;
        KindPillText.Text = item.IsFolder ? "SHADER FOLDER" : "";

        // Provenance as the library recorded it at install time, same as an instance copy's.
        var provenance = !item.HasProvenance
            ? ""
            : (item.Source == ModSource.CurseForge ? "CurseForge" : "Modrinth")
              + (item.VersionNumber is { Length: > 0 } v ? $"  ·  {v}" : "");
        ProvenanceLabel.Visibility = provenance.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProvenanceLabel.Text = provenance;
    }

    // ── the Instances tab ────────────────────────────────────────────────────

    /// <inheritdoc cref="LocalResourcePackDetailView.LoadPlacementAsync"/>
    private async Task LoadPlacementAsync()
    {
        _placement = null;
        if (_item is null) return;
        try
        {
            var states = await App.State.Library.InspectAsync(_item, _allPacks);
            _placement = states.ToDictionary(s => s.PackId);
        }
        catch (Exception ex) { AppLog.LogError(nameof(ShaderPackDetailView), ex); }
    }

    /// <inheritdoc cref="_placement"/>
    private int? HoldingCount() => _placement?.Values.Count(s => s.Applied);

    /// <summary>One sentence on how many instances hold the file, and when the rest get it.</summary>
    private string HoldingSentence() => HoldingCount() switch
    {
        null => "The launcher could not read your instances just now, so it cannot say which of them "
                + "hold a copy.",
        0 => "No instance has taken a copy yet - every instance ticked below takes one the next time "
             + "it starts.",
        1 => "One instance holds a copy; the rest that are ticked below take one the next time they "
             + "start.",
        var n when n == _allPacks.Count => $"All {n} of your instances hold a copy.",
        var n => $"{n} of your {_allPacks.Count} instances hold a copy; the rest that are ticked "
                 + "below take one the next time they start."
    };

    /// <inheritdoc cref="LocalResourcePackDetailView.PlacementClause"/>
    private string PlacementClause(PackSummary instance, bool goesIn)
    {
        if (_placement is null || !_placement.TryGetValue(instance.Id, out var state)) return "";
        if (state.HasOwnCopy)
            return $" {instance.Name} has its own file of that name, which wins at launch, so the "
                 + "shared copy sits there unused.";
        if (state.Applied)
            return goesIn
                ? $" {instance.Name} has a copy already."
                : $" {instance.Name} has a copy already, and it stays there.";
        return goesIn ? $" {instance.Name} takes its copy the next time it starts." : "";
    }

    /// <summary>
    /// Fills the per-instance list: every instance ticked except those this pack is narrowed away from.
    /// </summary>
    /// <remarks>
    /// Same list and wording as the resource pack page. Ticks write the item's narrowing
    /// (<see cref="ContentPlacement.SetTicked"/>); whether the pack is on at all is the toggle on the
    /// Shaders list. Instances without a shader loader keep their box, and their line says the pack
    /// will work once Iris is installed.
    /// </remarks>
    private void FillInstances()
    {
        _packRows.Clear();

        // Only on the installed-key route: a pack inside an instance that the library doesn't hold.
        if (_item is null)
        {
            PackListWrap.Visibility = Visibility.Collapsed;
            InstanceStateNote.Text =
                "This pack is only inside " + (_pack?.SourcePackName ?? "one instance") + ", so there is "
                + "nothing to hand out yet. Choose it on the Shaders page and the launcher takes its own "
                + "copy - then this list decides which instances get it.";
            return;
        }

        PackListWrap.Visibility = Visibility.Visible;
        var on = _item.AutoApply;
        InstanceStateNote.Text = on
            ? "Every instance is ticked to begin with. Untick one and the launcher leaves it alone - "
              + "anything already there stays where it is. An instance loads one shader pack, so this "
              + "one being on means the others are off. A ticked instance that has no copy yet takes "
              + "one the next time it starts, so \"will get it\" and \"has it\" are two different "
              + "answers and each line below says which it is."
            : "Every instance is ticked to begin with. This pack is switched OFF on the Shaders page, "
              + "so nothing is placed anywhere yet - these ticks are what will happen when you switch "
              + "it on.";

        foreach (var pack in _allPacks.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            InstanceContentOverrides? overrides = null;
            try { overrides = App.State.ContentDefaults.OverridesFor(pack.Id); }
            catch (Exception ex) { AppLog.LogError(nameof(ShaderPackDetailView), ex); }

            _packRows.Add(new PackCompatibilityRow
            {
                PackId = pack.Id,
                PackName = pack.Name,
                VersionLabel = pack.IsEmpty ? "EMPTY"
                    : pack.Loader == LoaderKind.None ? $"MC {pack.MinecraftVersion}"
                    : $"MC {pack.MinecraftVersion} · {pack.Loader}",
                StateLine = Describe(pack, overrides),
                IsCompatible = ContentPlacement.IsTicked(_item, pack, overrides),
                IsEditable = true
            });
        }
    }

    /// <summary>One sentence about this instance, from the same call the list's rows use.</summary>
    private string Describe(PackSummary pack, InstanceContentOverrides? overrides)
    {
        if (_item is null) return "";
        if (overrides?.OptOutOfAllDefaults == true)
            return $"{pack.Name} takes nothing the launcher hands out, whatever is ticked here.";

        ShaderLoader loader;
        try { loader = App.State.Shaders.DetectLoader(pack.Id, pack.Name); }
        catch { loader = ShaderLoader.Iris; }
        if (loader == ShaderLoader.None)
            return "Nothing here can load a shader pack - install Iris (Fabric/NeoForge) or Oculus "
                 + "(Forge) and this starts working.";

        var goesIn = ContentPlacement.GoesInto(_item, pack, overrides, loader, out var why);
        return why + PlacementClause(pack, goesIn);
    }

    /// <summary>
    /// A box was ticked or unticked: the whole list is written back.
    /// </summary>
    /// <remarks>The whole list, because an item can carry older narrowing from several stores (see
    /// <see cref="ContentPlacement.SetTicked"/>). Reloads afterwards so changed rows update.</remarks>
    private void OnPackCompatibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _item is null) return;
        try
        {
            var ticked = _packRows.Where(r => r.IsCompatible).Select(r => r.PackId).ToHashSet();
            ContentPlacement.SetTicked(_item, _allPacks, ticked);
            StatusLabel.Text = ticked.Count == _allPacks.Count
                ? "Every instance gets this pack."
                : $"{ticked.Count} of {_allPacks.Count} instance(s) get this pack. The rest are left "
                  + "alone - anything already in them stays.";
            _ = ReloadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = "That could not be saved: " + ex.Message; }
    }

    // ── overview ─────────────────────────────────────────────────────────────

    private void OnSaveName(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Name cannot be empty."; return; }
        if (_pack is null && _item is null) return;

        // The Shaders entry is keyed by an instance's copy, so it is only written when there is one.
        // The library item is renamed either way, which is what the lists read.
        if (_pack is not null) App.State.Shaders.Rename(_pack.Key, name);
        if (_item is not null) App.State.Library.Rename(_item.Key, name);
        PackNameLabel.Text = name;
        StatusLabel.Text = "Name saved.";
    }

    /// <summary>Reveals the copy this page is about: the instance's or the library's. The Local file
    /// card says which one.</summary>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = _pack?.FilePath ?? _item?.Path;
        if (path is not { Length: > 0 }) { StatusLabel.Text = "There is no file to reveal."; return; }
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            StatusLabel.Text = "That file is no longer on disk.";
            return;
        }
        // A folder pack opens as a folder; a zip is shown selected in its folder.
        if (!SafeLaunch.RevealFile(path)) StatusLabel.Text = "That file could not be shown in Explorer.";
    }

    /// <summary>Formats a shader file's timestamp. The scanner leaves it at <c>default</c> when it
    /// can't stat the file, and a local <c>DateTime.MinValue</c> can't become a
    /// <see cref="DateTimeOffset"/> east of UTC.</summary>
    private static string ModifiedLabel(DateTime local) =>
        local == default ? "date unknown" : TimeFormat.DateTime(TimeFormat.FromLocal(local));
}
