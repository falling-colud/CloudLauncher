using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The "Update all" review card: every mod with an update, each with a checkbox, and the selected
/// mod's changelog on the right. Returns the (mod, target version) pairs to install, or null.
/// </summary>
/// <remarks>Locked and update-incompatible mods start unticked and show a badge, so a bulk update
/// never overrides those flags unnoticed.</remarks>
public partial class ModUpdateReviewDialog : UserControl
{
    private readonly PackDetail _pack;
    private readonly List<UpdateRowVm> _rows;
    private readonly TaskCompletionSource<List<(PackMod Mod, ModVersion Target)>?> _tcs = new();
    private int _changelogGeneration;

    public ModUpdateReviewDialog(PackDetail pack, IReadOnlyList<PackMod> updatable)
    {
        InitializeComponent();
        _pack = pack;
        TitleLabel.Text = $"Update mods · {pack.Name}";
        _rows = updatable
            .Where(m => m.LatestVersion is not null)
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new UpdateRowVm(m, m.LatestVersion!))
            .ToList();
        List.ItemsSource = _rows;
        Focusable = true;
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            Focus();
            if (_rows.Count > 0 && List.SelectedIndex < 0) List.SelectedIndex = 0;
        };
        UpdateCounts();
    }

    public Task<List<(PackMod Mod, ModVersion Target)>?> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(null);

    /// <summary>Shows the card as an in-window overlay. Returns the ticked updates, or null if
    /// cancelled.</summary>
    public static async Task<List<(PackMod Mod, ModVersion Target)>?> ShowAsync(
        MainWindow host, PackDetail pack, IReadOnlyList<PackMod> updatable)
    {
        var card = new ModUpdateReviewDialog(pack, updatable);
        await host.ShowCardAsync(card, card.Result, card.Cancel,
            new ResizableCardSpec("mod-update-review", 980, 660, MinWidth: 720, MinHeight: 420));
        return card.Result.Result;
    }

    private void UpdateCounts()
    {
        var selected = _rows.Count(r => r.IsSelected);
        var held = _rows.Count(r => !r.IsSelected && (r.IsLocked || r.IsIncompatible));
        CountLabel.Text = $"{_rows.Count} update(s) · {selected} selected";
        UpdateButton.Content = selected == 0 ? "Update selected" : $"Update {selected} selected";
        UpdateButton.IsEnabled = selected > 0;
        FooterNote.Text = held > 0
            ? $"{held} locked or risky update(s) are unticked - tick them to include them anyway."
            : "";
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) => UpdateCounts();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = true;
        UpdateCounts();
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = false;
        UpdateCounts();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is UpdateRowVm row) _ = ShowChangelogAsync(row);
    }

    /// <summary>
    /// Shows what changed between the installed version and the offered one: every release in between,
    /// newest first, each under its own heading.
    /// </summary>
    /// <remarks>
    /// An update often skips several releases, so the target's notes alone aren't enough. Notes come from
    /// the shared version catalog (mostly cached by the update check), and the count is capped.
    /// </remarks>
    private async Task ShowChangelogAsync(UpdateRowVm row)
    {
        var gen = ++_changelogGeneration;
        ChangelogTitle.Text = $"{row.Name} {row.Target.VersionNumber}";
        // DatePublished is the store's UTC instant; TimeFormat converts and localises it.
        ChangelogMeta.Text = $"{ModUpdateChannel.Label(row.Target.ReleaseChannel)} · published {TimeFormat.Date(row.Target.DatePublished)} · {row.Target.Source}";
        ChangelogStatus.Text = "Loading changelog...";
        ChangelogStatus.Visibility = Visibility.Visible;
        ChangelogView.Visibility = Visibility.Collapsed;

        var span = await VersionsInJumpAsync(row);
        if (gen != _changelogGeneration) return;

        if (span.Versions.Count > 1)
            ChangelogMeta.Text += $" · {span.Versions.Count} version(s) in this jump" +
                                  (span.Truncated ? " (showing the most recent)" : "");

        string html;
        try
        {
            html = await BuildChangelogHtmlAsync(row, span.Versions);
        }
        catch (Exception ex)
        {
            if (gen != _changelogGeneration) return;
            ChangelogStatus.Text = "Could not load the changelog: " + ex.Message;
            return;
        }
        if (gen != _changelogGeneration) return;

        if (string.IsNullOrWhiteSpace(html))
        {
            ChangelogStatus.Text = span.Versions.Count > 1
                ? "None of the versions in this jump published a changelog."
                : "No changelog was published for this version.";
            return;
        }
        ChangelogStatus.Visibility = Visibility.Collapsed;
        ChangelogView.Visibility = Visibility.Visible;
        ChangelogView.Show(html, isMarkdown: false);
    }

    /// <summary>How many releases to read per mod. Beyond twenty the notes stop being useful; it's just
    /// a big jump.</summary>
    private const int MaxChangelogVersions = 20;

    /// <summary>The releases this update spans: newer than what is installed, no newer than the
    /// target, and on a channel this mod follows. Newest first, target always included.</summary>
    private async Task<(List<ModVersion> Versions, bool Truncated)> VersionsInJumpAsync(UpdateRowVm row)
    {
        var target = new List<ModVersion> { row.Target };
        try
        {
            if (row.Mod.PrimaryMod is null || row.Mod.PrimaryVersion is null) return (target, false);

            var loader = ModUpdater.LoaderTag(_pack);
            var all = await App.State.ModVersions.GetLatestVersionsAsync(
                row.Mod.PrimaryMod, _pack.MinecraftVersion, loader);

            var installedAt = row.Mod.PrimaryVersion.DatePublished;
            var between = all
                .Where(v => v.DatePublished > installedAt && v.DatePublished <= row.Target.DatePublished)
                .Where(v => ModUpdater.IsCompatible(v, _pack.MinecraftVersion, loader))
                .Where(v => ModUpdateChannel.Admits(row.Mod.EffectiveUpdateChannel, v.ReleaseChannel))
                .GroupBy(v => v.Id)                       // the target is in the list too
                .Select(g => g.First())
                .OrderByDescending(v => v.DatePublished)
                .ToList();

            if (between.Count == 0) return (target, false);
            if (!between.Any(v => v.Id == row.Target.Id)) between.Insert(0, row.Target);

            var truncated = between.Count > MaxChangelogVersions;
            return (between.Take(MaxChangelogVersions).ToList(), truncated);
        }
        catch { return (target, false); }
    }

    /// <summary>
    /// One document from several versions' notes: a heading per release, then its changelog, oldest at
    /// the bottom. Each version is converted with its own markdown flag, since Modrinth publishes
    /// markdown, CurseForge publishes HTML, and a jump can span both.
    /// </summary>
    private async Task<string> BuildChangelogHtmlAsync(UpdateRowVm row, IReadOnlyList<ModVersion> versions)
    {
        if (row.Mod.PrimaryMod is null) return "";

        // A few at a time: cached versions come back instantly and ApiClient paces the rest, but there's
        // no need for twenty requests at once.
        var gate = new SemaphoreSlim(3, 3);
        var sections = new string?[versions.Count];
        await Task.WhenAll(versions.Select(async (version, index) =>
        {
            await gate.WaitAsync();
            try
            {
                var (text, isMarkdown) = await App.State.ModVersions.GetChangelogAsync(row.Mod.PrimaryMod!, version);
                sections[index] = string.IsNullOrWhiteSpace(text)
                    ? null
                    : CloudLauncher.Shared.PackText.PrepareDescriptionHtml(text!, isMarkdown, runnableCommands: false);
            }
            catch { sections[index] = null; }
            finally { gate.Release(); }
        }));

        var builder = new System.Text.StringBuilder();
        var any = false;
        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i];
            var isTarget = version.Id == row.Target.Id;
            if (i > 0) builder.Append("<hr/>");

            var heading = System.Net.WebUtility.HtmlEncode(version.VersionNumber);
            var meta = System.Net.WebUtility.HtmlEncode(
                $"{ModUpdateChannel.Label(version.ReleaseChannel)} · {TimeFormat.Date(version.DatePublished)}" +
                (isTarget ? " · the version you would install" : ""));
            builder.Append($"<h3>{heading}</h3><p><em>{meta}</em></p>");

            if (sections[i] is { Length: > 0 } body) { builder.Append(body); any = true; }
            else builder.Append("<p><em>No changelog published for this version.</em></p>");
        }
        return any ? builder.ToString() : "";
    }

    private void OnUpdate(object sender, RoutedEventArgs e) => Accept();
    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    private void Accept()
    {
        var chosen = _rows.Where(r => r.IsSelected).Select(r => (r.Mod, r.Target)).ToList();
        if (chosen.Count == 0) return;
        _tcs.TrySetResult(chosen);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        else if (e.Key == Key.Enter && !(Keyboard.FocusedElement is CheckBox)) { Accept(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    public sealed class UpdateRowVm : INotifyPropertyChanged
    {
        public PackMod Mod { get; }
        public ModVersion Target { get; }

        private bool _selected;
        public bool IsSelected
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        }

        public UpdateRowVm(PackMod mod, ModVersion target)
        {
            Mod = mod;
            Target = target;
            // Flags the user set are respected by default; ticking the box is the explicit override.
            _selected = !mod.Meta.UpdateLocked && !mod.Meta.UpdateIncompatible;
        }

        public string Name => Mod.DisplayName;
        public string Initial => Mod.Initial;
        public string IconUrl => Mod.IconUrl ?? "";
        public string VersionsLabel => $"{Mod.VersionLabel}  >  {Target.VersionNumber}";
        public string Channel => ModUpdateChannel.Label(Target.ReleaseChannel);
        public bool IsLocked => Mod.Meta.UpdateLocked;
        public bool IsIncompatible => Mod.Meta.UpdateIncompatible;
        public Visibility LockedVisibility => IsLocked ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IncompatibleVisibility => IsIncompatible ? Visibility.Visible : Visibility.Collapsed;

        public Brush ChannelBrush => ModUpdateChannel.Normalize(Target.ReleaseChannel) switch
        {
            ModUpdateChannel.Release => Res("SuccessBrush"),
            ModUpdateChannel.Beta => Res("WarningBrush"),
            _ => Res("DangerBrush")
        };

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
