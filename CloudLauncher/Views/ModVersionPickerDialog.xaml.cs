using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>In-window picker for a specific mod version to install, including beta and alpha
/// channels. Compatible versions show first; "Show all" reveals the rest.</summary>
public partial class ModVersionPickerDialog : UserControl
{
    /// <summary>What the picker came back with: the version to install, and whether the mod should
    /// be held at it afterwards.</summary>
    /// <param name="KeepVersion">The "Keep this version" box. True locks the mod to the installed
    /// version; false releases any existing lock (the box starts ticked for a locked mod).</param>
    public sealed record PickResult(ModVersion Version, bool KeepVersion);

    private readonly List<ModVersion> _all;
    private readonly string? _mc;
    private readonly string? _loader;
    private readonly string? _currentVersionId;
    private readonly string? _currentVersionNumber;
    private readonly TaskCompletionSource<PickResult?> _tcs = new();

    /// <summary>The window the picker is a card on, so a changelog opened from it stacks over it
    /// instead of replacing it.</summary>
    private MainWindow? _host;

    public ModVersionPickerDialog(string title, List<ModVersion> versions, string? mc, string? loader,
        string? currentVersionId = null, string? currentVersionNumber = null, bool keepVersion = false)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        _all = versions;
        _mc = string.IsNullOrWhiteSpace(mc) ? null : mc;
        _loader = string.IsNullOrWhiteSpace(loader) ? null : loader;
        _currentVersionId = string.IsNullOrWhiteSpace(currentVersionId) ? null : currentVersionId;
        _currentVersionNumber = string.IsNullOrWhiteSpace(currentVersionNumber) ? null : currentVersionNumber;
        KeepVersionBox.IsChecked = keepVersion;
        Loaded += (_, _) => { Animate.SlideFadeIn(this, 0, 14, 200); Refresh(); Focus(); };
        Focusable = true;
    }

    public Task<PickResult?> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(null);

    /// <param name="keepVersion">Initial state of the "Keep this version" box. Pass the mod's
    /// <see cref="ModMeta.UpdateLocked"/> so a locked mod stays locked by default.</param>
    public static async Task<PickResult?> ShowAsync(MainWindow host, string title, List<ModVersion> versions,
        string? mc, string? loader, string? currentVersionId = null, string? currentVersionNumber = null,
        bool keepVersion = false)
    {
        var card = new ModVersionPickerDialog(title, versions, mc, loader, currentVersionId, currentVersionNumber,
            keepVersion) { _host = host };
        await host.ShowCardAsync(card, card.Result, card.Cancel,
            new ResizableCardSpec("mod-version-picker", 520, 614, MinWidth: 440, MinHeight: 360));
        return card.Result.Result;
    }

    /// <summary>
    /// Applies the picker's "Keep this version" answer to a mod that has just been installed from it.
    /// </summary>
    /// <remarks>
    /// Ticked pins the mod: <see cref="ModMeta.UpdateLocked"/> keeps "Update all" from moving it,
    /// and <see cref="ModMeta.PinnedVersionId"/> records the chosen version for the tooltip and
    /// list. Unticked clears both. Call it only after a successful install, so a failed download
    /// never changes the mod's flags.
    /// </remarks>
    public static void ApplyKeepVersion(Guid packId, PackMod mod, PickResult result)
    {
        mod.Meta.UpdateLocked = result.KeepVersion;
        mod.Meta.PinnedVersionId = result.KeepVersion ? result.Version.Id : null;
        App.State.ModInventory.SaveMeta(packId, mod);
    }

    private void Refresh()
    {
        var showAll = ShowAllBox.IsChecked == true;
        var rows = _all
            .Where(v => showAll || IsCompatible(v))
            .Select(v => new VersionRowVm(v, IsCompatible(v), IsCurrent(v)))
            .ToList();
        List.ItemsSource = rows;
        SubLabel.Text = rows.Count == 0
            ? "No versions found."
            : $"{rows.Count} version(s){(_mc is not null && !showAll ? $" for {_mc}" : "")} - pick one to install.";
        if (rows.Count > 0) List.SelectedIndex = 0;
        UpdateInstallButton();
    }

    private bool IsCompatible(ModVersion v) =>
        (_mc is null || v.GameVersions.Any(g => string.Equals(g, _mc, StringComparison.OrdinalIgnoreCase))) &&
        (_loader is null || v.Loaders.Any(l => string.Equals(l, _loader, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The version currently installed, matched by store version id, or by version number
    /// when the id isn't known. Its row is badged "Current" and cannot be installed.</summary>
    private bool IsCurrent(ModVersion v) =>
        _currentVersionId is not null
            ? string.Equals(v.Id, _currentVersionId, StringComparison.OrdinalIgnoreCase)
            : _currentVersionNumber is not null && string.Equals(v.VersionNumber, _currentVersionNumber, StringComparison.OrdinalIgnoreCase);

    private void OnShowAllChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInstallButton();

    /// <summary>Disables and relabels Install when the installed version is selected.</summary>
    private void UpdateInstallButton()
    {
        var onCurrent = List.SelectedItem is VersionRowVm { IsCurrent: true };
        InstallButton.IsEnabled = !onCurrent;
        InstallButton.Content = onCurrent ? "Current version" : "Install";
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void OnInstall(object sender, RoutedEventArgs e) => Accept();
    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    /// <summary>Right-click menu on a version row: read its changelog, install it, or copy its
    /// number. Clicks on the empty space under the list are ignored.</summary>
    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (VersionRowMenu.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)
            is not { DataContext: VersionRowVm row }) return;
        e.Handled = true;
        List.SelectedItem = row;
        VersionRowMenu.Open(BuildVersionMenu(row));
    }

    private ContextMenu BuildVersionMenu(VersionRowVm row)
    {
        var menu = VersionRowMenu.Create(List);
        VersionRowMenu.AddChangelog(menu, () => _ = ShowChangelogAsync(row));
        // Worded like the Install button, which reads "Current version" on the installed row.
        VersionRowMenu.Add(menu, row.IsCurrent ? "Current version" : "Install this version",
            VersionRowMenu.DownloadGlyph,
            () =>
            {
                List.SelectedItem = row;
                Accept();
            },
            enabled: !row.IsCurrent,
            toolTip: row.IsCurrent ? "This version is already installed" : null);
        menu.Items.Add(new Separator());
        VersionRowMenu.AddCopyVersion(menu, row.Version.VersionNumber);
        return menu;
    }

    /// <summary>Opens the changelog card over this picker, able to step through the versions the
    /// picker is listing, in its order.</summary>
    /// <remarks>Store versions carry their own ids, so the card needs no mod. Focus returns here
    /// afterwards so Enter and Escape reach the picker again.</remarks>
    private async Task ShowChangelogAsync(VersionRowVm row)
    {
        try
        {
            var (versions, index) = VersionChangelogCard.FromList(List.Items, row, r => r.Version);
            if (_host is not null)
                await VersionChangelogCard.ShowAsync(_host, TitleLabel.Text, versions, index);
            else
                await VersionChangelogCard.ShowAsync(this, TitleLabel.Text, versions, index);
        }
        catch (Exception ex) { AppLog.LogError(nameof(ModVersionPickerDialog), ex); }
        finally { Focus(); }
    }

    private void Accept()
    {
        // Never "install" the version that's already there (double-click / Enter on the current row).
        if (List.SelectedItem is VersionRowVm { IsCurrent: false } row)
            _tcs.TrySetResult(new PickResult(row.Version, KeepVersionBox.IsChecked == true));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    public sealed class VersionRowVm
    {
        public ModVersion Version { get; }
        public double Dim { get; }
        public bool IsCurrent { get; }
        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

        public VersionRowVm(ModVersion v, bool compatible, bool isCurrent)
        {
            Version = v;
            IsCurrent = isCurrent;
            Dim = compatible ? 1.0 : 0.55;
        }

        public string VersionLabel => Version.VersionNumber;
        public string Channel => Capitalize(Version.ReleaseChannel);
        /// <summary>The publish date in the reader's own format and time zone.</summary>
        /// <remarks>The column sorts on <see cref="ModVersion.DatePublished"/> itself. The store
        /// sends UTC, which has to be shown as local time or the day can be off.</remarks>
        public string DateLabel => TimeFormat.Date(Version.DatePublished);

        public string MetaLabel
        {
            get
            {
                var mc = Version.GameVersions.Length > 0 ? string.Join(", ", Version.GameVersions) : "any MC";
                var loader = Version.Loaders.Length > 0 ? string.Join(", ", Version.Loaders) : "any loader";
                return $"{mc}  ·  {loader}";
            }
        }

        public Brush ChannelBrush => Version.ReleaseChannel?.ToLowerInvariant() switch
        {
            "release" => Res("SuccessBrush"),
            "beta" => Res("WarningBrush"),
            _ => Res("DangerBrush") // alpha / experimental / anything else
        };

        private static string Capitalize(string? s) =>
            string.IsNullOrEmpty(s) ? "release" : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    }
}
