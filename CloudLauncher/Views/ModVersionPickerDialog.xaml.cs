using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>In-window picker for choosing a specific mod version to install — including beta /
/// alpha (experimental) channels. Compatible versions show first; "Show all" reveals the rest.</summary>
public partial class ModVersionPickerDialog : UserControl
{
    /// <summary>What the picker came back with: the version to install, and whether the mod should
    /// be held at it afterwards.</summary>
    /// <param name="KeepVersion">The "Keep this version" box. True locks the mod to what is being
    /// installed; false releases any lock it already had, since the box comes up pre-ticked for an
    /// already-locked mod and unticking it is the only way to say "let updates move this again".</param>
    public sealed record PickResult(ModVersion Version, bool KeepVersion);

    private readonly List<ModVersion> _all;
    private readonly string? _mc;
    private readonly string? _loader;
    private readonly string? _currentVersionId;
    private readonly string? _currentVersionNumber;
    private readonly TaskCompletionSource<PickResult?> _tcs = new();

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

    /// <param name="keepVersion">Initial state of the "Keep this version" box — pass the mod's
    /// existing <see cref="ModMeta.UpdateLocked"/> so a locked mod stays locked unless the user
    /// says otherwise.</param>
    public static async Task<PickResult?> ShowAsync(MainWindow host, string title, List<ModVersion> versions,
        string? mc, string? loader, string? currentVersionId = null, string? currentVersionNumber = null,
        bool keepVersion = false)
    {
        var card = new ModVersionPickerDialog(title, versions, mc, loader, currentVersionId, currentVersionNumber,
            keepVersion);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    /// <summary>
    /// Applies the picker's "Keep this version" answer to a mod that has just been installed from it.
    /// </summary>
    /// <remarks>
    /// Ticked pins the mod: <see cref="ModMeta.UpdateLocked"/> stops "Update all" moving it, and
    /// <see cref="ModMeta.PinnedVersionId"/> records exactly which version the user chose so the
    /// tooltip and the list can say so. Unticked clears both, which is how a lock is released —
    /// the box arrives pre-ticked for an already-locked mod, so leaving it unticked is a decision.
    /// Call it after a successful install, so a failed download never changes the mod's flags.
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
            : $"{rows.Count} version(s){(_mc is not null && !showAll ? $" for {_mc}" : "")} — pick one to install.";
        if (rows.Count > 0) List.SelectedIndex = 0;
        UpdateInstallButton();
    }

    private bool IsCompatible(ModVersion v) =>
        (_mc is null || v.GameVersions.Any(g => string.Equals(g, _mc, StringComparison.OrdinalIgnoreCase))) &&
        (_loader is null || v.Loaders.Any(l => string.Equals(l, _loader, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The version currently installed — matched by store version id, or version number when the
    /// id isn't known. Its row is badged "Current" and the Install button is disabled for it.</summary>
    private bool IsCurrent(ModVersion v) =>
        _currentVersionId is not null
            ? string.Equals(v.Id, _currentVersionId, StringComparison.OrdinalIgnoreCase)
            : _currentVersionNumber is not null && string.Equals(v.VersionNumber, _currentVersionNumber, StringComparison.OrdinalIgnoreCase);

    private void OnShowAllChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInstallButton();

    /// <summary>Greys out + relabels the Install button when the selected row is the already-installed version.</summary>
    private void UpdateInstallButton()
    {
        var onCurrent = List.SelectedItem is VersionRowVm { IsCurrent: true };
        InstallButton.IsEnabled = !onCurrent;
        InstallButton.Content = onCurrent ? "Current version" : "Install";
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void OnInstall(object sender, RoutedEventArgs e) => Accept();
    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

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
        public string DateLabel => Version.DatePublished.ToString("yyyy-MM-dd");

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
