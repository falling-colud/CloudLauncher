using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>One instance the import can land in, as step 2 draws it.</summary>
/// <remarks>Holds no brush: the theme service hands out fresh brushes on every retheme, so a cached
/// one stops repainting. The template decides how the row looks.</remarks>
/// <param name="note">The sentence under the instance name: the library's own
/// <see cref="LibraryTargetState.StateLabel"/> where there is one, otherwise what this import will
/// do to this instance.</param>
/// <param name="canTake">False when this instance can't receive the file at all, with
/// <paramref name="hint"/> saying why.</param>
public sealed class ImportTargetRow(PackSummary pack, string note, bool canTake, string hint)
{
    /// <summary>The instance this row stands for.</summary>
    public PackSummary Pack { get; } = pack;

    /// <summary>The instance's name.</summary>
    public string Name { get; } = pack.Name;

    /// <summary>One clause about what this import means for this instance.</summary>
    public string Note { get; } = note;

    /// <summary>False greys the row out; <see cref="Hint"/> is shown on the disabled control.</summary>
    public bool CanTake { get; } = canTake;

    /// <summary>Tooltip: why the row is off, or what ticking it will do.</summary>
    public string Hint { get; } = hint;

    /// <summary>Two-way from the row's checkbox. The tick state lives here, not in the control, so
    /// re-templating the list does not lose it.</summary>
    public bool IsChecked { get; set; }
}

/// <summary>
/// The one Import flow: pick the file, say which instances get it, optionally publish it to
/// CloudLauncher, then a receipt saying what actually happened.
/// </summary>
/// <remarks>
/// <para>Steps are panel-visibility swaps with a Back/Next pair, like
/// <see cref="UploadBundleVersionDialog"/>. Shown as an in-window card through
/// <see cref="MainWindow.ShowCardAsync"/> and resizable through <see cref="ResizableCard"/>.</para>
/// <para>The common case is two clicks ("Choose a file..." then "Import"). Publishing is reached
/// only through its own button, and its fields sit in a collapsed expander.</para>
/// <para>Writes go through the services that already own each kind: <see cref="ContentLibraryService"/>
/// stores anything the library can hold and places the kinds it hands out; <see cref="WorldService"/>
/// places saves, <see cref="ContentBundleService.InstallFromZip"/> bundles,
/// <see cref="ShaderPackService.InstallAsync"/> shaders the library isn't holding, and
/// <c>ModpackDownloadService</c> modpacks (no instance step, since importing one creates the
/// instance). Publishing uses the existing <see cref="ApiClient"/> and
/// <see cref="ContentBundleService"/> calls.</para>
/// </remarks>
public partial class ImportContentCard : UserControl
{
    /// <summary>The tightest of the four endpoints' changelog caps (mods 4096, bundles 8192), so
    /// nothing typed here gets rejected later.</summary>
    private const int MaxChangelogLength = 4096;

    private readonly TaskCompletionSource<ImportOutcome?> _tcs = new();
    private readonly MainWindow _shell;
    private readonly ImportRequest _request;
    private readonly ImportContentSpec _spec;
    private readonly Reentrancy _filling = new();

    /// <summary>The steps, in the order they are offered. Publish sits between the instance list and
    /// the writes, and is skipped entirely unless the user asks for it.</summary>
    private enum Step { File, Where, Publish, Run }

    private Step _step = Step.File;
    private string? _sourcePath;
    private bool _sourceIsFolder;
    private List<ImportTargetRow> _rows = [];
    private bool _ticksSeeded;
    private CancellationTokenSource? _inspectCts;

    /// <summary>Non-null while the writes run. Also stops Escape and the backdrop from cancelling,
    /// since a half-written import has nowhere else to report itself.</summary>
    private CancellationTokenSource? _runCts;

    private ImportOutcome? _outcome;

    /// <summary>A temporary archive this card built from a chosen folder, shared by the install and
    /// the upload and deleted when both are done. The source folder is never touched.</summary>
    private string? _tempZip;

    /// <summary>Opens the new item's detail page. Run once the card is gone, or the side panel would
    /// open behind the backdrop.</summary>
    private Action? _openWhenClosed;

    /// <summary>Set when a world was placed, so the hosted world can be linked to the local save and
    /// that save's own page can be opened afterwards.</summary>
    private string? _placedWorldKey;

    /// <summary>The instance a modpack import created. That kind has no instance step because this
    /// is its answer, and the page behind the card still needs to know which instance appeared.</summary>
    private Guid? _createdPackId;

    /// <summary>The last name this card put into the hosting form by itself, so it can tell its own
    /// suggestion from something the user typed over it.</summary>
    private string? _hostNameSeed;

    /// <summary>Why there is no library copy when there was meant to be one. Non-null adds a clause to
    /// the receipt: "in your library" and "only in these instances" are different outcomes.</summary>
    private string? _libraryNote;

    private ImportContentCard(MainWindow shell, ImportRequest request)
    {
        InitializeComponent();
        _shell = shell;
        _request = request;
        _spec = ImportContentSpec.For(request.Kind);

        TitleLabel.Text = $"Import a {_spec.LowerNoun}";
        PickFolderButton.Visibility = _spec.AllowsFolder ? Visibility.Visible : Visibility.Collapsed;
        FileHint.Text = _spec.AllowsFolder
            ? $"You can drop a file or a folder onto this box instead. An unpacked {_spec.LowerNoun} "
            + "folder is as valid as an archive."
            : "You can drop a file onto this box instead.";

        using (_filling.Hold())
        {
            HostVisibilityBox.Items.Add(new ComboBoxItem { Content = "Private - only people I add", Tag = PackVisibility.Private });
            HostVisibilityBox.Items.Add(new ComboBoxItem { Content = "Teams I share it with", Tag = PackVisibility.Team });
            HostVisibilityBox.Items.Add(new ComboBoxItem { Content = "Public - anyone signed in", Tag = PackVisibility.Public });
            HostVisibilityBox.SelectedIndex = 0;

            if (_spec.HostBundleKind is { } bundleKind)
            {
                foreach (var kind in ContentBundleService.Kinds)
                    HostBundleKindBox.Items.Add(new ComboBoxItem
                    {
                        Content = ContentBundleService.KindLabel(kind),
                        Tag = kind
                    });
                HostBundleKindBox.SelectedItem = HostBundleKindBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (BundleKind?)i.Tag == bundleKind);
                HostRootBox.Text = BundleTargets.DefaultFor(bundleKind);
                HostBundlePanel.Visibility = Visibility.Visible;
            }
        }

        HostLoaderPanel.Visibility = _spec.WantsLoaders ? Visibility.Visible : Visibility.Collapsed;
        HostChannelPanel.Visibility = _spec.WantsChannel ? Visibility.Visible : Visibility.Collapsed;
        HostMcLabel.Text = _spec.SingleMcVersion ? "Minecraft version" : "Minecraft versions";
        HostChangelogBox.MaxLength = MaxChangelogLength;
        // Seeded rather than guessed from the file name; the version guesser is private to
        // UploadModVersionDialog.
        HostVersionBox.Text = "1.0.0";

        foreach (var control in new FrameworkElement[]
                 { NextButton, PublishJumpButton, HostExpander, LibraryOnlyBox })
            ToolTipService.SetShowOnDisabled(control, true);

        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            Focus();
            if (_request.PrePickedPath is { Length: > 0 } dropped) AcceptSource(dropped, skipAhead: true);
            else GoTo(Step.File);
        };
        Focusable = true;
        Unloaded += (_, _) => _inspectCts?.Cancel();
    }

    /// <summary>Completes with what the import did, or null when the user backed out.</summary>
    public Task<ImportOutcome?> Result => _tcs.Task;

    /// <summary>Escape / backdrop cancel. Ignored once the writes have started: files are already on
    /// disk by then and the receipt is the only place that says which.</summary>
    public void Cancel()
    {
        if (_runCts is not null) return;
        _inspectCts?.Cancel();
        _tcs.TrySetResult(null);
    }

    /// <summary>
    /// Runs the whole import flow over <paramref name="host"/> and returns what it did.
    /// </summary>
    /// <remarks>The entry point for every content page's Import action. The card owns the writes and
    /// the upload, so a page only reloads afterwards; the outcome names which instances to
    /// invalidate.</remarks>
    /// <returns>Null when the user cancelled, so a page can tell "nothing happened" from "nothing
    /// changed".</returns>
    public static async Task<ImportOutcome?> ShowAsync(MainWindow host, ImportRequest req)
    {
        var card = new ImportContentCard(host, req);
        await host.ShowCardAsync(card, card.Result, card.Cancel,
            new ResizableCardSpec("import-content", 720, 640, MinWidth: 520, MinHeight: 420));

        var outcome = card.Result.Result;
        // Only after the overlay is gone, or the side panel would open behind the backdrop.
        if (outcome is not null) card._openWhenClosed?.Invoke();
        return outcome;
    }

    // ── step 1: the file ─────────────────────────────────────────────────────

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the {_spec.LowerNoun} to import",
            Filter = _spec.FileFilter,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AcceptSource(dialog.FileName, skipAhead: false);
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Choose the {_spec.LowerNoun} folder to import",
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AcceptSource(dialog.FolderName, skipAhead: false);
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPath(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DroppedPath(e) is { } path) AcceptSource(path, skipAhead: false);
    }

    /// <summary>The single file or folder in a drag payload.</summary>
    /// <remarks>Several at once are ignored rather than guessed at, since this flow imports one thing.
    /// A folder is only accepted for kinds whose table row allows one.</remarks>
    private string? DroppedPath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        if (files.Length != 1) return null;
        if (File.Exists(files[0])) return files[0];
        return _spec.AllowsFolder && Directory.Exists(files[0]) ? files[0] : null;
    }

    /// <summary>Takes a chosen path, describes it, and checks whether it looks like what the page
    /// says it is.</summary>
    /// <param name="skipAhead">True for drag and drop: a drop already answers "which file", so the
    /// flow starts at the instance list. A kind with no instance step (a modpack) still stops here,
    /// since the next step would be the writes themselves, which need a press.</param>
    private void AcceptSource(string path, bool skipAhead)
    {
        _sourceIsFolder = Directory.Exists(path);
        if (!_sourceIsFolder && !File.Exists(path))
        {
            Status($"{Path.GetFileName(path)} is not there any more.", danger: true);
            return;
        }

        _sourcePath = path;
        _ticksSeeded = false;   // a different file means a different set of sensible instances
        FileNameLabel.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        FileMetaLabel.Text = _sourceIsFolder ? "Folder" : "Reading...";
        Status(null, danger: false);

        // Seed the hosting name from the file, but never over a name the user typed, even if they go
        // back and swap the file.
        if (HostNameBox.Text.Trim().Length == 0 || HostNameBox.Text == _hostNameSeed)
        {
            _hostNameSeed = Path.GetFileNameWithoutExtension(FileNameLabel.Text);
            HostNameBox.Text = _hostNameSeed;
        }

        _ = DescribeSourceAsync(path, _sourceIsFolder);

        if (skipAhead && _spec.WantsInstanceStep) GoTo(Step.Where);
        else GoTo(Step.File);
    }

    /// <summary>Fills in the size line and the "this may not be what you think it is" warning, off
    /// the UI thread because both read the archive.</summary>
    private async Task DescribeSourceAsync(string path, bool isFolder)
    {
        string meta;
        string? warning;
        try
        {
            (meta, warning) = await Task.Run(() =>
            {
                var size = isFolder ? 0L : SafeLength(path);
                var label = isFolder
                    ? "Folder"
                    : ContentBundleService.FormatSize(size);
                // Publishing is the only thing with a size limit; the local copy has none, so an
                // over-sized file is a note on the line rather than a refusal.
                if (!isFolder && _spec.CanHost && size > _spec.MaxHostBytes)
                    label += $" · too large to publish (the server accepts up to "
                           + $"{ContentBundleService.FormatSize(_spec.MaxHostBytes)})";
                return (label, SanityWarning(path, isFolder));
            });
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ImportContentCard), ex);
            meta = isFolder ? "Folder" : "";
            warning = null;
        }

        // The user may have picked something else while this was reading the archive.
        if (!string.Equals(path, _sourcePath, StringComparison.Ordinal)) return;

        FileMetaLabel.Text = meta;
        FileWarning.Text = warning ?? "";
        FileWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateChrome();
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }   // a size is a label, not a reason to fail
    }

    /// <summary>
    /// Null when the source looks like the kind the page asked for, else one sentence saying what is
    /// missing.
    /// </summary>
    /// <remarks>
    /// <para>Only a warning, never a refusal: people knowingly import odd archives (a coremod with
    /// no metadata, a pack they're about to fix, a save with level.dat one folder deeper). The
    /// services keep their own rules: <see cref="WorldService.ImportZipAsync"/> deletes what it
    /// extracted if no level.dat turns up, and <see cref="BundleSafePath"/> refuses unsafe
    /// entries.</para>
    /// <para>The resource-pack and mod checks are written out here because the equivalents are private
    /// helpers on other pages. If another caller needs them, move them to their services.</para>
    /// </remarks>
    private string? SanityWarning(string path, bool isFolder)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        switch (_spec.Kind)
        {
            case ImportContentKind.Mod:
                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    return $"{name} is not a .jar. Minecraft only loads jars out of mods/, so this will "
                         + "sit there doing nothing unless you know otherwise.";
                return HasAnyEntry(path, "fabric.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml",
                                   "quilt.mod.json", "mcmod.info")
                    ? null
                    : $"No mod metadata was found in {name}. Loaders identify a mod by that file, so this "
                    + "one may be a library or a coremod - or may not be a mod at all.";

            case ImportContentKind.ResourcePack:
            case ImportContentKind.DataPack:
                return HasPackMeta(path, isFolder)
                    ? null
                    : $"No pack.mcmeta was found in {name}. Minecraft ignores a pack without one and says "
                    + "nothing about it.";

            case ImportContentKind.ShaderPack:
                return isFolder
                    ? Directory.Exists(Path.Combine(path, "shaders")) ? null
                        : $"{name} has no shaders/ folder inside it, which is where Iris and OptiFine look."
                    : HasEntryUnder(path, "shaders/") ? null
                        : $"{name} has no shaders/ folder inside it, which is where Iris and OptiFine look.";

            case ImportContentKind.World:
                if (isFolder)
                    return WorldService.IsWorldFolder(path) ? null
                        : $"{name} has no level.dat in it, so it may not be a save.";
                return WorldService.ZipContainsWorld(path) ? null
                    : $"No level.dat was found near the top of {name}, so this may not be a world archive. "
                    + "The import will say so if it turns out not to be one.";

            case ImportContentKind.Modpack:
                return name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"{name} is neither a .mrpack nor a CurseForge .zip, which are the two formats the "
                    + "importer understands.";

            default:
                // Config, KubeJS: any set of files is a valid bundle; there's no marker file to check.
                return null;
        }
    }

    private static bool HasPackMeta(string path, bool isFolder) =>
        isFolder ? File.Exists(Path.Combine(path, "pack.mcmeta")) : HasAnyEntry(path, "pack.mcmeta");

    /// <summary>True when the archive holds any of these entries. An archive that can't be opened
    /// counts as "don't warn".</summary>
    private static bool HasAnyEntry(string archivePath, params string[] entries)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            return entries.Any(e => zip.GetEntry(e) is not null);
        }
        catch { return true; }
    }

    private static bool HasEntryUnder(string archivePath, string prefix)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            return zip.Entries.Any(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        catch { return true; }
    }

    // ── step 2: where it goes ────────────────────────────────────────────────

    private void OnLibraryOnlyChanged(object sender, RoutedEventArgs e)
    {
        var libraryOnly = LibraryOnlyBox.IsChecked == true;
        if (libraryOnly) foreach (var row in _rows) row.IsChecked = false;
        RenderTargets();
        TargetList.IsEnabled = !libraryOnly;
        SelectAllTargets.IsEnabled = !libraryOnly;
        SelectNoTargets.IsEnabled = !libraryOnly;
        UpdateChrome();
    }

    private void OnTargetTicked(object sender, RoutedEventArgs e)
    {
        // The tick lives on the row object, so nothing above hears about it unless it is told.
        if (_rows.Any(r => r.IsChecked) && LibraryOnlyBox.IsChecked == true)
            LibraryOnlyBox.IsChecked = false;
        UpdateTargetCount();
        UpdateChrome();
    }

    private void OnSelectAllTargets(object sender, RoutedEventArgs e) => SetAllTargets(true);
    private void OnSelectNoTargets(object sender, RoutedEventArgs e) => SetAllTargets(false);

    private void SetAllTargets(bool value)
    {
        foreach (var row in _rows) row.IsChecked = value && row.CanTake;
        if (value) LibraryOnlyBox.IsChecked = false;
        RenderTargets();
        UpdateChrome();
    }

    /// <summary>
    /// Builds the instance list, asking the library how each instance already stands.
    /// </summary>
    /// <remarks>
    /// The per-instance sentence tells a no-op ("already using the library copy") from a surprise ("has
    /// its own copy; the library version is not in use"), and only
    /// <see cref="ContentLibraryService.InspectAsync"/> knows which applies.
    /// <para>Inspect needs a <see cref="LibraryItem"/>, but the file isn't in the library yet. So this
    /// uses the real item when the library already holds one under that name, and otherwise a stand-in
    /// pointing at the source path. Inspect compares file identity, not content, so a stand-in can only
    /// under-report, never claim a false "already linked".</para>
    /// </remarks>
    private async Task RefreshTargetsAsync()
    {
        _inspectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _inspectCts = cts;
        var ct = cts.Token;

        var states = new Dictionary<Guid, LibraryTargetState>();
        // Only when the library also places the file: its sentences are about a link in local/, so for
        // a world or bundle (stored in the library, placed by another service) they'd describe a file
        // that will never be there.
        if (_spec.LibraryApplies && _spec.ResolveLibraryKind() is { } libraryKind
            && _sourcePath is { } path && _request.Packs.Count > 0)
        {
            try
            {
                var item = LibraryItemFor(libraryKind, path);
                foreach (var state in await App.State.Library.InspectAsync(item, _request.Packs, ct))
                    states[state.PackId] = state;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // A library that cannot be read costs the sentences, not the step: every row falls
                // back to "will be added" and the write reports its own failure properly.
                AppLog.LogError(nameof(ImportContentCard), ex);
            }
        }

        if (ct.IsCancellationRequested) return;
        BuildTargetRows(states);
    }

    /// <summary>The library's own record for this file where there is one, else a stand-in so
    /// <see cref="ContentLibraryService.Inspect"/> has something to compare against.</summary>
    /// <remarks>Named arguments: <see cref="LibraryItem"/> gains fields as the library learns new
    /// kinds, and positional arguments here would shift.</remarks>
    private static LibraryItem LibraryItemFor(LibraryKind kind, string path)
    {
        var fileName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var key = ContentLibraryService.KeyFor(kind, fileName);
        if (App.State.Library.Find(key) is { } existing) return existing;

        return new LibraryItem(
            Key: key,
            Kind: kind,
            FileName: fileName,
            Path: path,
            DisplayName: Path.GetFileNameWithoutExtension(fileName),
            IsFolder: Directory.Exists(path),
            SizeBytes: SafeLength(path),
            AddedAt: DateTimeOffset.Now,
            LastModified: DateTimeOffset.Now,
            KeepLocal: false,
            AppliedTo: [],
            OriginPath: path,
            Source: null,
            ProjectId: null,
            VersionId: null,
            VersionNumber: null);
    }

    private void BuildTargetRows(Dictionary<Guid, LibraryTargetState> states)
    {
        var ticked = _rows.Where(r => r.IsChecked).Select(r => r.Pack.Id).ToHashSet();

        _rows = _request.Packs
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(pack =>
            {
                var note = states.TryGetValue(pack.Id, out var state)
                    ? state.StateLabel
                    : $"will be added to {_spec.InstanceFolder}/";
                if (states.TryGetValue(pack.Id, out var s) && s.LoaderMissing)
                    note += " · nothing in this instance can load one yet";

                return new ImportTargetRow(pack, note, canTake: true,
                    hint: $"Put this {_spec.LowerNoun} into {pack.Name}");
            })
            .ToList();

        if (!_ticksSeeded)
        {
            // Default tick: the instance the page was scoped to, or the only one there is.
            var preferred = _request.PreferredPackId
                            ?? (_request.Packs.Count == 1 ? _request.Packs[0].Id : null);
            foreach (var row in _rows)
                row.IsChecked = ticked.Contains(row.Pack.Id) || row.Pack.Id == preferred;
            _ticksSeeded = true;
        }
        else
        {
            foreach (var row in _rows) row.IsChecked = ticked.Contains(row.Pack.Id);
        }

        RenderTargets();
    }

    private void RenderTargets()
    {
        // Re-assigned rather than refreshed: the rows are plain objects with no change notification,
        // which is all a list this short needs.
        TargetList.ItemsSource = null;
        TargetList.ItemsSource = _rows;

        var empty = _rows.Count == 0;
        TargetListNote.Text = empty
            ? _spec.LibraryCanHold
                ? $"You have no instances yet. The {_spec.LowerNoun} can still be imported now, and "
                + "you can hand it to an instance the moment you make one."
                : $"You have no instances yet, and a {_spec.LowerNoun} lives inside one. Make an instance "
                + "first and this import will have somewhere to go."
            : "";
        TargetListNote.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        UpdateTargetCount();
    }

    private void UpdateTargetCount()
    {
        var ticks = _rows.Count(r => r.IsChecked);
        TargetCountLabel.Text = _rows.Count == 0
            ? ""
            : ticks == 0 ? $"{_rows.Count} instance(s) · none ticked"
            : $"{ticks} of {_rows.Count} ticked";
    }

    // ── step 3: publish it online ────────────────────────────────────────────

    private bool Hosting => _spec.CanHost && HostExpander.IsEnabled && HostExpander.IsExpanded;

    private void OnHostExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (HostNameBox is null) return;   // raised once while the template is still being built
        if (HostExpander.IsExpanded && HostNameBox.Text.Trim().Length == 0 && _sourcePath is { } path)
        {
            _hostNameSeed = Path.GetFileNameWithoutExtension(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            HostNameBox.Text = _hostNameSeed;
        }
        UpdateChrome();
    }

    private void OnHostFormChanged(object sender, TextChangedEventArgs e) => UpdateChrome();
    private void OnLoaderChanged(object sender, RoutedEventArgs e) => UpdateChrome();

    private void OnBundleKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy || HostRootBox is null) return;
        // As in BundleDetailView: the kind decides which folder the bundle unpacks into, so changing
        // the kind re-seeds the folder instead of leaving a root the new kind would refuse.
        if (TagOf<BundleKind>(HostBundleKindBox) is { } kind)
        {
            using (_filling.Hold()) HostRootBox.Text = BundleTargets.DefaultFor(kind);
        }
        UpdateChrome();
    }

    /// <summary>Whether the hosting step can be offered at all, and the sentence saying why not.</summary>
    private void ApplyHostAvailability()
    {
        string? why = null;
        if (!_spec.CanHost)
            why = $"A {_spec.LowerNoun} is not something the server hosts - importing one makes an "
                + "instance, and an instance is shared by inviting people to it.";
        else if (App.State.IsOffline)
            why = $"Publishing needs the server, and it is not answering ({App.State.OfflineReason}). "
                + $"The {_spec.LowerNoun} will still be imported locally - publish it later from its page.";

        HostExpander.IsEnabled = why is null;
        HostExpander.ToolTip = why ?? $"Put this {_spec.LowerNoun} on CloudLauncher as well";
        HostBlockedNote.Text = why ?? "";
        HostBlockedNote.Visibility = why is null ? Visibility.Collapsed : Visibility.Visible;
        if (why is not null) HostExpander.IsExpanded = false;
    }

    /// <summary>Why the hosting form is not publishable yet, or null when it is.</summary>
    /// <remarks>
    /// Only what the server or format needs: a name, a version string, a loader where the content is
    /// loader-specific, and a folder the bundle installer accepts. Minecraft versions aren't required:
    /// the endpoint accepts null and they can be edited on the item's page later (see
    /// <see cref="UpdateModRequest"/>).
    /// </remarks>
    private string? HostFormProblem()
    {
        if (!Hosting) return null;
        if (HostNameBox.Text.Trim().Length == 0) return $"Give the {_spec.LowerNoun} a name to publish it under.";
        if (HostVersionBox.Text.Trim().Length == 0) return "Give the first version a name, e.g. 1.0.0.";
        if (_spec.WantsLoaders && LoadersCsv() is null) return "Tick at least one loader - a build with none matches no instance.";
        if (_spec.HostBundleKind is not null && TagOf<BundleKind>(HostBundleKindBox) is { } kind)
        {
            BundleSafePath.NormalizeRoot(kind, HostRootBox.Text.Trim(), out var rootError);
            if (rootError is not null) return $"That folder will not work - {rootError}.";
        }
        return null;
    }

    /// <summary>The ticked loaders as the server's CSV, or null when none are ticked.</summary>
    private string? LoadersCsv()
    {
        var loaders = new List<string>(4);
        if (LoaderFabric.IsChecked == true) loaders.Add("fabric");
        if (LoaderForge.IsChecked == true) loaders.Add("forge");
        if (LoaderNeoForge.IsChecked == true) loaders.Add("neoforge");
        if (LoaderQuilt.IsChecked == true) loaders.Add("quilt");
        return loaders.Count == 0 ? null : string.Join(',', loaders);
    }

    /// <summary>Trims and de-duplicates typed Minecraft versions into the CSV the server stores.</summary>
    private static string? NormalizeCsv(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    private static T? TagOf<T>(ComboBox box) where T : struct =>
        (box.SelectedItem as ComboBoxItem)?.Tag as T?;

    private static string? Trimmed(TextBox box) =>
        box.Text.Trim() is { Length: > 0 } text ? text : null;

    // ── navigation ───────────────────────────────────────────────────────────

    private void GoTo(Step step)
    {
        _step = step;
        FileStep.Visibility = step == Step.File ? Visibility.Visible : Visibility.Collapsed;
        WhereStep.Visibility = step == Step.Where ? Visibility.Visible : Visibility.Collapsed;
        PublishStep.Visibility = step == Step.Publish ? Visibility.Visible : Visibility.Collapsed;
        RunStep.Visibility = step == Step.Run ? Visibility.Visible : Visibility.Collapsed;

        if (step == Step.Where) _ = RefreshTargetsAsync();
        if (step == Step.Publish) ApplyHostAvailability();
        UpdateChrome();
    }

    /// <summary>The step Back leads to from the current one, or null when there is nowhere to go
    /// back to.</summary>
    /// <remarks>Used by both the handler and the chrome so they can't disagree. Step 1 isn't a
    /// destination when <see cref="ImportRequest.SourceIsFixed"/> is set: the caller staged those bytes
    /// and records where they came from, so swapping the file would leave wrong provenance. Step 1's
    /// pickers still work, so a caller whose staged file went missing isn't stuck.</remarks>
    private Step? BackTarget() => _step switch
    {
        Step.Where when !_request.SourceIsFixed => Step.File,
        Step.Publish when _spec.WantsInstanceStep => Step.Where,
        Step.Publish when !_request.SourceIsFixed => Step.File,
        _ => null
    };

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (BackTarget() is { } step) GoTo(step);
    }

    private void OnPublishJump(object sender, RoutedEventArgs e) => GoTo(Step.Publish);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // On the last step the writes are done and there is nothing left to abandon, so the same
        // button closes with the outcome rather than throwing the receipt away.
        if (_step == Step.Run && _runCts is null) { _tcs.TrySetResult(_outcome); return; }
        Cancel();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.File when _spec.WantsInstanceStep: GoTo(Step.Where); break;
            case Step.File: _ = RunAsync(); break;
            case Step.Where: _ = RunAsync(); break;
            case Step.Publish: _ = RunAsync(); break;
            case Step.Run: _tcs.TrySetResult(_outcome); break;
        }
    }

    /// <summary>Re-labels and re-enables the button row for the step the card is on, and puts the
    /// reason for a disabled primary on the primary itself.</summary>
    private void UpdateChrome()
    {
        if (NextButton is null) return;

        var libraryOnly = LibraryOnlyBox.IsChecked == true;
        var ticks = _rows.Count(r => r.IsChecked);
        // Hosting can be switched on in step 3 and then left behind with Back, so the form is checked
        // (and the primary names what it will do) on every step that can start the writes.
        var hostProblem = HostFormProblem();
        var importVerb = Hosting ? "Import and publish" : "Import";
        string? blocked = null;

        switch (_step)
        {
            case Step.File:
                StepLabel.Text = "The file";
                SubtitleLabel.Text = _spec.AllowsFolder
                    ? $"Pick the {_spec.LowerNoun} you already have - an archive or an unpacked folder."
                    : $"Pick the {_spec.LowerNoun} you already have.";
                NextButton.Content = _spec.WantsInstanceStep ? "Next" : importVerb;
                if (_sourcePath is null) blocked = "Choose the file to import first.";
                else blocked = hostProblem;
                break;

            case Step.Where:
                StepLabel.Text = "Where it goes";
                SubtitleLabel.Text = "Tick every instance that should get it. Nothing is switched on - the "
                                   + "file is only put where the game looks for it.";
                NextButton.Content = importVerb;
                if (_rows.Count == 0 && !_spec.LibraryCanHold)
                    blocked = $"Make an instance first - a {_spec.LowerNoun} lives inside one.";
                else if (!libraryOnly && ticks == 0)
                    blocked = _spec.LibraryCanHold
                        ? "Tick an instance, or choose not to put it in one yet."
                        : "Tick at least one instance.";
                else blocked = hostProblem;
                break;

            case Step.Publish:
                StepLabel.Text = "Publish it online";
                // Only bundles have share links, so only promise one for a bundle.
                SubtitleLabel.Text = _spec.HostBundleKind is not null
                    ? "Optional. Publishing gives collaborators, teams and share links something to point at."
                    : "Optional. Publishing gives the people and teams you share it with something to download.";
                NextButton.Content = importVerb;
                blocked = hostProblem;
                break;

            case Step.Run:
                StepLabel.Text = _runCts is null ? "Done" : "Doing it";
                SubtitleLabel.Text = "";
                NextButton.Content = "Done";
                if (_runCts is not null) blocked = "Still working...";
                break;
        }

        NextButton.IsEnabled = blocked is null;
        NextButton.ToolTip = blocked ?? NextButton.Content as string;

        BackButton.Visibility = BackTarget() is null ? Visibility.Collapsed : Visibility.Visible;
        PublishJumpButton.Visibility =
            _spec.CanHost && _step is Step.File or Step.Where ? Visibility.Visible : Visibility.Collapsed;
        PublishJumpButton.IsEnabled = _sourcePath is not null;
        PublishJumpButton.ToolTip = _sourcePath is null
            ? "Choose the file first."
            : $"Also put this {_spec.LowerNoun} on CloudLauncher";
        CancelButton.Content = _step == Step.Run && _runCts is null ? "Close" : "Cancel";
        CancelButton.IsEnabled = _runCts is null;

        // The library-only row is offered for every kind so the answer is always in the same place,
        // and says which kinds it cannot take rather than vanishing.
        LibraryOnlyBox.IsEnabled = _spec.LibraryCanHold;
        LibraryOnlyNote.Text =
            _spec.LibraryApplies
                ? "One copy, kept outside every instance. Hand it to instances whenever you like."
            : _spec.LibraryCanHold
                ? _spec.Kind == ImportContentKind.World
                    ? "Kept as a template. A save is never shared between instances - two copies of "
                    + "Minecraft writing one set of region files is corruption, not a merge - so each "
                    + "instance that wants it gets its own copy from here."
                    : "Kept as the original. Each instance that wants it gets the files unpacked into "
                    + "it from here."
                : $"The launcher cannot keep {_spec.LowerPluralNoun} on their own, so this one goes "
                + "straight into the instances you tick.";
        LibraryOnlyBox.ToolTip = _spec.LibraryCanHold
            ? "Keep it, and put it in an instance whenever you like"
            : LibraryOnlyNote.Text;
    }

    // ── step 4: do it ────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        if (_runCts is not null || _sourcePath is not { } path) return;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        GoTo(Step.Run);
        RunBar.Visibility = Visibility.Visible;
        RunBar.IsIndeterminate = true;
        ReceiptLabel.Text = "";
        ReceiptExtra.Visibility = Visibility.Collapsed;

        var targets = _rows.Where(r => r.IsChecked).Select(r => r.Pack).ToList();
        if (LibraryOnlyBox.IsChecked == true) targets.Clear();

        var placed = 0;
        var library = false;
        var summary = "";
        Guid? hostedId = null;
        string? trouble = null;

        try
        {
            (placed, library, summary) = await PlaceAsync(path, targets, ct);
        }
        catch (OperationCanceledException) { summary = "Import cancelled."; }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ImportContentCard), ex);
            summary = $"That {_spec.LowerNoun} could not be imported - {ContentBundleService.Explain(ex)}";
        }

        if (Hosting && !ct.IsCancellationRequested)
        {
            try
            {
                hostedId = await PublishAsync(path, ct);
            }
            catch (OperationCanceledException) { trouble = "The upload was cancelled."; }
            catch (Exception ex)
            {
                // The local import already happened and is worth reporting, so a failed publish is a
                // second line rather than a thrown-away receipt.
                AppLog.LogError(nameof(ImportContentCard), ex);
                trouble = "It was not published - " + ContentBundleService.Explain(ex);
            }
        }

        // One archive per run, deleted once both the install and the upload are done with it.
        if (_tempZip is { } temp)
        {
            try { File.Delete(temp); } catch { /* a temp zip that outlives us is harmless */ }
            _tempZip = null;
        }

        // Our own write is the one change a folder stamp cannot notice, so the remembered scan goes
        // before the page behind the card reloads.
        foreach (var target in targets) ScanCaches.InvalidatePack(target.Id, _spec.Scope);

        var written = targets.Select(t => t.Id).ToList();
        if (_createdPackId is { } created) written.Add(created);
        _outcome = new ImportOutcome(placed, written, library, hostedId, summary);

        RunBar.Visibility = Visibility.Collapsed;
        RunLog.Text = "";
        ReceiptLabel.Text = summary;
        ReceiptExtra.Text = trouble ?? "";
        ReceiptExtra.Visibility = trouble is null ? Visibility.Collapsed : Visibility.Visible;
        ReceiptExtra.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

        _runCts.Dispose();
        _runCts = null;
        UpdateChrome();
    }

    /// <summary>Progress line for the step-4 log, reported from a worker thread.</summary>
    private void Log(string line) => RunLog.Text = line;

    /// <summary>
    /// Puts the file where it belongs, using whichever service already owns that kind.
    /// </summary>
    /// <returns>Files written into instances, whether a library copy now exists, and the receipt.</returns>
    private async Task<(int Placed, bool Library, string Summary)> PlaceAsync(
        string path, List<PackSummary> targets, CancellationToken ct)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // A modpack has no instance step because importing one creates the instance. The download then
        // continues in the background under the instance's own job, so the card doesn't block on a
        // multi-GB pack.
        if (_spec.Kind == ImportContentKind.Modpack)
        {
            Log($"Creating an instance for {name}...");
            var created = await App.State.ModpackDownload.StartLocalFileImportAsync(
                path, Path.GetFileNameWithoutExtension(name), ct);
            _createdPackId = created.Id;
            return (0, false,
                $"Importing {name} as a new instance called {created.Name}. Its progress is on the "
                + "Instances page - it keeps going if you close this.");
        }

        // The library first, for every kind it can keep, so the import is a personal copy and not just
        // a file dropped into one instance.
        LibraryItem? stored = null;
        if (_spec.ResolveLibraryKind() is { } libraryKind)
        {
            Log($"Adding {name}...");
            stored = await StoreInLibraryAsync(path, libraryKind, ct);
        }

        // Placing from the library is a separate capability that three kinds lack: Apply refuses worlds
        // (a shared save would be two games writing one set of region files), and a config or KubeJS
        // bundle is a zip in the library but a tree of files in the instance. Those use their own
        // placement below and keep the library copy anyway.
        if (stored is { } item && _spec.LibraryApplies)
        {
            if (targets.Count == 0)
                return (0, true,
                    $"{item.FileName} was added. No instance has it yet - hand it to one "
                    + "whenever you want it there.");

            Log($"Handing it to {targets.Count} instance(s)...");
            var result = await App.State.Library.ApplyAsync(
                [item], targets, replaceOwnCopy: false, new Progress<string>(Log), ct);
            return (result.Linked + result.Copied, true, result.Summary());
        }

        var library = stored is not null;
        if (targets.Count == 0)
            return (0, library, library
                ? $"{stored!.FileName} was added. Nothing was put in an instance - each one "
                + "that wants it gets its own copy from here."
                : _libraryNote is { } declined
                    ? $"Nothing happened - {declined}, and no instance was ticked."
                    : "No instance was ticked, so nothing was written.");

        var (placed, _, summary) = _spec.Kind switch
        {
            ImportContentKind.World => await PlaceWorldAsync(path, targets, ct),
            ImportContentKind.ShaderPack => await PlaceShadersAsync(path, name, targets, ct),
            ImportContentKind.ConfigBundle or ImportContentKind.KubeJsBundle or ImportContentKind.DataPack
                => await PlaceBundleAsync(path, name, targets, ct),
            _ => await CopyIntoInstancesAsync(path, name, targets, ct),
        };

        var prefix = library ? "Kept a copy for other instances" : _libraryNote;
        return (placed, library, prefix is null ? summary : $"{prefix}  ·  {summary}");
    }

    /// <summary>
    /// Puts a copy in the library, or null when the library wouldn't keep it.
    /// </summary>
    /// <remarks>
    /// <para><see cref="ContentLibraryService.Add"/> can return null despite its signature: it looks the
    /// new item up through its own scan, which only lists files its rule recognises (a save folder with
    /// a <c>level.dat</c>, a <c>.jar</c> mod, a bundle zip). An odd archive that step 1 only warned about
    /// is then copied but not found, and the import continues without a library copy.</para>
    /// <para>Config and KubeJS bundles pass the archive, not the picked folder, because the library's
    /// copy of a bundle is the zip.</para>
    /// </remarks>
    private async Task<LibraryItem?> StoreInLibraryAsync(string path, LibraryKind kind, CancellationToken ct)
    {
        _libraryNote = null;
        var source = _spec.Kind is ImportContentKind.ConfigBundle or ImportContentKind.KubeJsBundle
            ? await EnsureArchiveAsync(path, ct)
            : path;

        try
        {
            // Provenance goes in with the library copy, which is the durable record: ApplyAsync carries
            // it to every instance the item is handed to, including ones ticked much later.
            var item = await App.State.Library.AddAsync(source, kind, _request.DisplayName,
                _request.OriginSource, _request.OriginProjectId, _request.OriginVersionId,
                _request.OriginVersionNumber, ct);
            if (item is not null) return item;

            _libraryNote = $"the launcher did not keep a copy (it is not a {_spec.LowerNoun} as far as "
                         + "it can tell)";
            AppLog.Log("library", $"Declined {Path.GetFileName(source)} as a {kind}: it does not match "
                                + "that kind's own rule, so the import went straight to the instances.");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ImportContentCard), ex);
            _libraryNote = "the launcher could not keep a copy - " + ContentBundleService.Explain(ex);
            return null;
        }
    }

    /// <summary>Shader packs, when the library isn't holding them.</summary>
    /// <remarks>Unreachable while <see cref="LibraryKind.ShaderPack"/> exists; kept because installing
    /// shader packs belongs to <see cref="ShaderPackService"/>.</remarks>
    private async Task<(int Placed, bool Library, string Summary)> PlaceShadersAsync(
        string path, string name, List<PackSummary> targets, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            Log($"Adding {name} to {target.Name}...");
            await App.State.Shaders.InstallAsync(target.Id, path, replace: false, ct);
        }
        return (targets.Count, false, Copied(name, targets));
    }

    /// <summary>Worlds, through the service that knows what a save is.</summary>
    /// <remarks><see cref="WorldService.ImportZipAsync"/> flattens a single wrapping folder and deletes
    /// what it extracted when no level.dat turns up, so an odd archive is refused cleanly here rather
    /// than half imported.</remarks>
    private async Task<(int Placed, bool Library, string Summary)> PlaceWorldAsync(
        string path, List<PackSummary> targets, CancellationToken ct)
    {
        var label = Path.GetFileNameWithoutExtension(path.TrimEnd(Path.DirectorySeparatorChar,
                                                                  Path.AltDirectorySeparatorChar));
        var placed = 0;
        var failures = new List<string>();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            Log($"Importing {label} into {target.Name}...");
            try
            {
                var folder = _sourceIsFolder
                    ? await App.State.Worlds.ImportFolderAsync(path, target.Id, target.Name, null, ct)
                    : await App.State.Worlds.ImportZipAsync(path, target.Id, target.Name, label, null, ct);
                // The first copy is the one the flow can open afterwards and the one a hosted world
                // is linked to; a save copied into five instances is five separate saves from now on.
                _placedWorldKey ??= WorldService.Key(target.Id, folder);
                placed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(ImportContentCard), ex);
                failures.Add($"{target.Name}: {ex.Message}");
            }
        }

        var summary = placed == 0
            ? failures.Count > 0 ? $"Nothing was imported - {failures[0]}" : "Nothing to import."
            : $"{label} is in {placed} instance(s)"
              + (failures.Count > 0 ? $"  ·  {failures.Count} failed - {failures[0]}" : ".");
        return (placed, false, summary);
    }

    /// <summary>
    /// Config, KubeJS and data-pack content, through the bundle installer.
    /// </summary>
    /// <remarks>The installer checks every entry against <see cref="BundleSafePath"/> and the instance
    /// root before writing, and backs up anything it replaces. A folder source is zipped first so it
    /// takes the same path; the picked folder becomes the bundle root, so entries land at the same
    /// relative places inside <c>config/</c> or <c>kubejs/</c>.</remarks>
    private async Task<(int Placed, bool Library, string Summary)> PlaceBundleAsync(
        string path, string name, List<PackSummary> targets, CancellationToken ct)
    {
        if (targets.Count == 0) return (0, false, "No instance was ticked, so nothing was written.");

        var kind = _spec.HostBundleKind ?? BundleKind.Other;
        var zip = await EnsureArchiveAsync(path, ct);
        var bundles = new ContentBundleService(App.State.Api, App.State.Settings, App.State.Packs);
        var log = new Progress<string>(Log);

        Log($"Unpacking {name} into {targets.Count} instance(s)...");
        var result = await Task.Run(
            () => bundles.InstallFromZip(zip, name, kind, _spec.InstanceFolder, targets, log, ct), ct);

        return (result.FilesWritten, false, result.Summary());
    }

    /// <summary>
    /// The plain copy, for a kind with no service of its own: a jar into <c>mods/</c>, a pack into
    /// <c>resourcepacks/</c> while the library can't hold one.
    /// </summary>
    /// <remarks>Unique names come from <see cref="ShaderPackService.NextFreePath"/>, as in
    /// <see cref="ContentLibraryService.Add"/>, so a second copy is "name-2.jar" here too.</remarks>
    private async Task<(int Placed, bool Library, string Summary)> CopyIntoInstancesAsync(
        string path, string name, List<PackSummary> targets, CancellationToken ct)
    {
        if (targets.Count == 0) return (0, false, "No instance was ticked, so nothing was written.");
        // The name travels into every instance, so it has to be one plain name there. A drive root
        // picked as a folder, or a reserved name such as CON, is refused before anything is copied.
        if (!PathSafety.IsSafeFileName(name))
            return (0, false, $"Nothing was copied: \"{name}\" is not a name Windows allows inside an instance.");

        var placed = 0;
        var failures = new List<string>();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            Log($"Copying {name} into {target.Name}...");
            try
            {
                await Task.Run(() =>
                {
                    App.State.Packs.EnsurePackFolder(target.Id, target.Name, target.IsShared);
                    var dir = PathSafety.ResolveInside(App.State.Packs.GameDir(target.Id, target.Name), _spec.InstanceFolder)
                              ?? throw new IOException($"'{_spec.InstanceFolder}' is not a folder inside the instance.");
                    Directory.CreateDirectory(dir);
                    var dest = ShaderPackService.NextFreePath(dir, name);
                    if (Directory.Exists(path)) CopyFolder(path, dest);
                    else File.Copy(path, dest);
                }, ct);
                placed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(ImportContentCard), ex);
                failures.Add($"{target.Name}: {ex.Message}");
            }
        }

        var summary = placed == 0
            ? failures.Count > 0 ? $"Nothing was copied - {failures[0]}" : "Nothing to copy."
            : Copied(name, targets.Take(placed).ToList())
              + (failures.Count > 0 ? $"  ·  {failures.Count} failed - {failures[0]}" : "");
        return (placed, false, summary);
    }

    private string Copied(string name, List<PackSummary> targets) =>
        targets.Count == 1
            ? $"{name} is in {targets[0].Name}'s {_spec.InstanceFolder}/ folder."
            : $"{name} is in the {_spec.InstanceFolder}/ folder of {targets.Count} instances.";

    /// <summary>Copies a picked folder's tree under <paramref name="dest"/>. A name that cannot exist
    /// inside an instance (one made on another system, say) is logged and left out.</summary>
    private static void CopyFolder(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (PathSafety.ResolveInside(dest, Path.GetRelativePath(source, dir)) is { } target)
                Directory.CreateDirectory(target);
            else
                AppLog.Log(nameof(ImportContentCard), $"Left {dir} out: its name cannot be used inside an instance.");
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (PathSafety.ResolveInside(dest, Path.GetRelativePath(source, file)) is { } target)
                File.Copy(file, target, overwrite: false);
            else
                AppLog.Log(nameof(ImportContentCard), $"Left {file} out: its name cannot be used inside an instance.");
        }
    }

    /// <summary>
    /// The single archive for this run: the chosen file itself, or a temporary zip of the chosen folder.
    /// </summary>
    /// <remarks>
    /// <para>Built at most once per run and deleted by <see cref="RunAsync"/>, since both the install
    /// and the upload need the same bytes.</para>
    /// <para>The existing zip helpers (<c>WorldService.ZipToTempAsync</c>,
    /// <see cref="ContentBundleService.Compose"/>) need a world or an instance; here there's only a
    /// folder. Entries are relative to that folder, so a picked <c>config</c> folder unpacks back into
    /// <c>config/</c>.</para>
    /// </remarks>
    private async Task<string> EnsureArchiveAsync(string path, CancellationToken ct)
    {
        if (!_sourceIsFolder) return path;
        if (_tempZip is { } existing && File.Exists(existing)) return existing;

        var temp = Path.Combine(Path.GetTempPath(), $"cloudlauncher-import-{Guid.NewGuid():N}.zip");
        Log("Packing the folder...");
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ZipFile.CreateFromDirectory(path, temp, CompressionLevel.Optimal, includeBaseDirectory: false);
        }, ct);
        _tempZip = temp;
        return temp;
    }

    // ── publishing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the item on the server and uploads its first version, through the same calls the
    /// hosting dialogs use.
    /// </summary>
    /// <remarks>Then arranges for the new item's own page to open, since collaborators, teams and share
    /// links live there.</remarks>
    private async Task<Guid?> PublishAsync(string path, CancellationToken ct)
    {
        var file = await EnsureArchiveAsync(path, ct);
        var size = SafeLength(file);
        if (size > _spec.MaxHostBytes)
            throw new InvalidOperationException(
                $"That file is {ContentBundleService.FormatSize(size)} and the server accepts up to "
                + $"{ContentBundleService.FormatSize(_spec.MaxHostBytes)}.");

        var fileName = Path.GetFileName(file);
        var name = HostNameBox.Text.Trim();
        var summary = Trimmed(HostSummaryBox);
        var description = Trimmed(HostDescriptionBox);
        var visibility = TagOf<PackVisibility>(HostVisibilityBox) ?? PackVisibility.Private;
        var mc = NormalizeCsv(HostMcBox.Text);
        var loaders = _spec.WantsLoaders ? LoadersCsv() : null;
        var version = HostVersionBox.Text.Trim();
        var changelog = Trimmed(HostChangelogBox);
        var channel = _spec.WantsChannel
            ? (HostChannelBox.SelectedItem as ComboBoxItem)?.Content as string ?? "release"
            : "release";

        var progress = new Progress<long>(sent => Log(size > 0
            ? $"Uploading {ContentBundleService.FormatSize(sent)} of {ContentBundleService.FormatSize(size)}..."
            : "Uploading..."));

        Log($"Creating {name} on CloudLauncher...");

        switch (_spec.Kind)
        {
            case ImportContentKind.Mod:
            {
                var created = await App.State.Api.CreateModAsync(
                    new CreateModRequest(name, summary, description, visibility, mc, loaders), ct);
                await App.State.Api.UploadModVersionAsync(created.Id, file,
                    new CreateModVersionRequest(version, changelog, channel, fileName, mc, loaders), ct, progress);
                AppLog.Log("mods", $"Published {created.Name} {version} from the import flow.");
                _openWhenClosed = () => _shell.OpenModDetail(created.Id, created.Name);
                return created.Id;
            }

            case ImportContentKind.ResourcePack:
            {
                var created = await App.State.Api.CreateResourcePackAsync(
                    new CreateResourcePackRequest(name, summary, description, visibility, mc), ct);
                await App.State.Api.UploadResourcePackVersionAsync(created.Id, file,
                    new CreateResourcePackVersionRequest(version, changelog, channel, fileName, mc), ct, progress);
                AppLog.Log("resourcepacks", $"Published {created.Name} {version} from the import flow.");
                _openWhenClosed = () => _shell.OpenResourcePackDetail(created.Id, created.Name);
                return created.Id;
            }

            case ImportContentKind.World:
            {
                // The world family stores one Minecraft version, not a CSV, so use the first typed value.
                var single = mc?.Split(',').FirstOrDefault()?.Trim();
                var created = await App.State.Api.CreateSharedWorldAsync(
                    new CreateWorldRequest(name, summary, description, visibility,
                        string.IsNullOrWhiteSpace(single) ? null : single), ct);
                await App.State.Api.UploadSharedWorldVersionAsync(created.Id, file,
                    new CreateWorldVersionRequest(version, changelog, fileName, single), ct, progress);
                AppLog.Log("worlds", $"Published {created.Name} {version} from the import flow.");

                // Tie the save we just placed to the hosted world, so that save's own page shows its
                // versions and its sharing controls instead of offering to create a second one.
                if (_placedWorldKey is { } key)
                {
                    App.State.Worlds.UpdateOverview(key, summary, description, visibility);
                    App.State.Worlds.LinkSharedWorld(key, created.Id);
                    _openWhenClosed = () => _shell.OpenWorldDetail(key, created.Name);
                }
                return created.Id;
            }

            default:
            {
                var kind = TagOf<BundleKind>(HostBundleKindBox) ?? _spec.HostBundleKind ?? BundleKind.Other;
                var root = BundleSafePath.NormalizeRoot(kind, HostRootBox.Text.Trim(), out var rootError);
                if (rootError is not null)
                    throw new InvalidOperationException($"That folder will not work - {rootError}.");

                var bundles = new ContentBundleService(App.State.Api, App.State.Settings, App.State.Packs);
                var created = await bundles.CreateAsync(
                    new CreateBundleRequest(kind, name, summary, description, visibility, root, mc, loaders), ct);
                await bundles.PublishVersionAsync(created.Id, file,
                    new CreateBundleVersionRequest(version, changelog, channel, fileName, mc, loaders),
                    progress, ct);
                AppLog.Log("bundles", $"Published {created.Name} {version} from the import flow.");

                // Its own page, where people, invitations and the share link live. The Sharing overview
                // wouldn't list it yet, since it isn't shared with anyone.
                _openWhenClosed = () => _shell.OpenBundleDetail(created.Id, created.Name);
                return created.Id;
            }
        }
    }

    // ── chrome ───────────────────────────────────────────────────────────────

    private void Status(string? text, bool danger)
    {
        StatusLabel.Text = text ?? "";
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty,
                                         danger ? "DangerBrush" : "TextSecondaryBrush");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
