using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// An in-window card that picks which instances a config file — or a whole folder of them — is copied
/// into, and says up front what that would do to each one.
/// </summary>
/// <remarks>
/// <para>The point of the checklist is that copying one instance's config into several others is a
/// single decision, not five repetitions of the same dialog. The point of the per-instance summary is
/// that the two things the user cannot see from a file name are whether they are about to replace
/// something and whether the destination's sync rules will push their change out to other people. Both
/// are answered on the row, before anything is written.</para>
///
/// <para>Nothing is ticked when the card opens. The alternative — everything ticked — turns a
/// mis-aimed click into an overwrite across every instance at once.</para>
/// </remarks>
public partial class ConfigCopyCard : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private readonly ObservableCollection<TargetRow> _targets = new();
    private readonly IReadOnlyList<PackSummary> _allTargets;
    private readonly IReadOnlyList<string> _paths;

    /// <summary>The instances the user ticked, or null when the card was cancelled.</summary>
    public List<PackSummary>? ChosenTargets { get; private set; }

    /// <summary>What the copy would do, so the caller can name the numbers in its confirm.</summary>
    public ConfigHubService.CopyPreview? Preview { get; private set; }

    public Task Completion => _tcs.Task;

    /// <param name="sourceName">The instance the files come from.</param>
    /// <param name="paths">Game-relative paths to copy.</param>
    /// <param name="targets">Every instance except the source.</param>
    public ConfigCopyCard(string sourceName, IReadOnlyList<string> paths,
                          IReadOnlyList<PackSummary> targets, Window owner)
    {
        InitializeComponent();
        _allTargets = targets;
        _paths = paths;
        TargetList.ItemsSource = _targets;

        TitleLabel.Text = paths.Count == 1 ? "Copy this file to…" : $"Copy {paths.Count} files to…";
        SubLabel.Text = paths.Count == 1
            ? $"{paths[0]} from {sourceName}."
            : $"{paths.Count} files from {sourceName}, keeping the same folder structure.";
        CountLabel.Text = $"{targets.Count} other instance(s)";
        FooterNote.Text = "Anything replaced is backed up next to itself first.";

        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            Focus();
            _ = LoadPreviewAsync();
        };
    }

    public void Close() => _tcs.TrySetResult(true);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Works out, per instance, how many of the files already exist and whether the destination's rules
    /// make these paths Shared. Reading a dozen <c>.rules.json</c> files and stat-ing a few hundred
    /// paths is quick but it is still disk work, so it happens off the UI thread behind a spinner.
    /// </summary>
    private async Task LoadPreviewAsync()
    {
        try
        {
            var paths = _paths;
            var targets = _allTargets;
            var folders = App.State.Packs;
            var rules = App.State.Rules;

            var preview = await Task.Run(() => ConfigHubService.PreviewCopy(paths, targets, folders, rules));
            Preview = preview;

            foreach (var pack in targets)
            {
                var items = preview.Items.Where(i => i.PackId == pack.Id).ToList();
                _targets.Add(new TargetRow(pack, items.Count(i => i.Exists), items.Count,
                    shared: items.Any(i => i.Shared)));
            }
            BusyState.Visibility = Visibility.Collapsed;

            var sharedCount = _targets.Count(t => t.Shared);
            if (sharedCount > 0)
                FooterNote.Text = $"Anything replaced is backed up first. {sharedCount} instance(s) treat " +
                                  "these paths as Shared, so the change travels to everyone on those packs.";
        }
        catch (Exception ex)
        {
            BusyState.Visibility = Visibility.Collapsed;
            FooterNote.Text = "Could not inspect the other instances: " + ex.Message;
            // Still offer the targets — the copy itself reports its own failures per file.
            foreach (var pack in _allTargets) _targets.Add(new TargetRow(pack, 0, _paths.Count, false));
        }
    }

    private void OnTargetToggled(object sender, RoutedEventArgs e) => UpdateCopyButton();

    private void UpdateCopyButton()
    {
        var chosen = _targets.Count(t => t.IsChecked);
        CopyButton.IsEnabled = chosen > 0;
        CopyButton.Content = chosen switch
        {
            0 => "Copy",
            1 => "Copy into 1 instance",
            _ => $"Copy into {chosen} instances"
        };
        CountLabel.Text = $"{chosen} of {_targets.Count} instance(s) selected";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);
    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        // The rows are plain objects rebound wholesale rather than notifying — cheaper than an
        // INotifyPropertyChanged implementation for a list this size, and the checkbox state is the
        // only thing that changes.
        var snapshot = _targets.ToList();
        foreach (var row in snapshot) row.IsChecked = value;
        _targets.Clear();
        foreach (var row in snapshot) _targets.Add(row);
        UpdateCopyButton();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        ChosenTargets = _targets.Where(t => t.IsChecked).Select(t => t.Pack).ToList();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ChosenTargets = null;
        Close();
    }

    private sealed class TargetRow(PackSummary pack, int existing, int total, bool shared)
    {
        public PackSummary Pack { get; } = pack;
        public string PackName => Pack.Name;
        public bool IsChecked { get; set; }
        public bool Shared { get; } = shared;

        public string MetaLabel { get; } = existing == 0
            ? total == 1 ? "Does not have this file — it will be created" : $"None of the {total} files exist here yet"
            : existing == total
                ? total == 1 ? "Already has this file — it will be replaced" : $"All {total} files exist here and will be replaced"
                : $"{existing} of {total} files exist here and will be replaced";

        public Visibility SharedVisibility => Shared ? Visibility.Visible : Visibility.Collapsed;

        public string SharedHint =>
            $"{PackName}'s sync rules mark this path as Shared, so the copied file will be uploaded and " +
            "will reach everyone else on that pack.";
    }
}
