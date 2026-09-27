using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>The card that adds or edits a placeholder on a planning board: a mod the pack doesn't have
/// yet. The result task yields the edited <see cref="Draft"/>, or null if cancelled.</summary>
/// <remarks>
/// <para>Only the name is required. The store search is the stores' own search, run once per press of
/// Search and without a version or loader filter, since the usual reason for a placeholder is that
/// the mod exists but not for this pack. Attaching a result gives the card its icon, link and project
/// id, which is how the board recognises the mod once it is installed.</para>
/// <para>"Is there a version?" is one request, made only when asked, so a board full of placeholders
/// never queries the stores by itself.</para>
/// </remarks>
public partial class ModPlaceholderDialog : UserControl
{
    /// <summary>A placeholder's editable fields, copied off the card and back again.</summary>
    public sealed class Draft
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = PlanPlaceholderStatus.NeedsPort;
        public string Link { get; set; } = "";
        public string Note { get; set; } = "";

        /// <summary>The attached store project as a mod key (<c>modrinth:...</c>, <c>curseforge:...</c>).</summary>
        public string? ProjectKey { get; set; }
        public string? ProjectName { get; set; }
        public string? IconUrl { get; set; }

        public string? CheckedFor { get; set; }
        public bool? CheckedFound { get; set; }
        public DateTimeOffset? CheckedAt { get; set; }
    }

    private readonly TaskCompletionSource<Draft?> _tcs = new();
    private readonly Draft _draft;
    private readonly string? _mc, _loader;
    private readonly Dictionary<string, RadioButton> _statusRadios = new();

    /// <summary>The link the last attached project filled in, so attaching another replaces it but a
    /// link typed by hand is never overwritten.</summary>
    private string? _autoLink;

    public ModPlaceholderDialog(Draft draft, bool isNew, string? mcVersion, string? loader)
    {
        InitializeComponent();
        _draft = draft;
        _mc = string.IsNullOrWhiteSpace(mcVersion) ? null : mcVersion.Trim();
        _loader = string.IsNullOrWhiteSpace(loader) ? null : loader.Trim();

        TitleText.Text = isNew ? "Add placeholder" : "Edit placeholder";
        OkButton.Content = isNew ? "Add" : "Save";
        NameBox.Text = draft.Name;
        LinkBox.Text = draft.Link;
        NoteBox.Text = draft.Note;
        SearchBox.Text = draft.Name;
        if (draft.ProjectKey?.StartsWith("curseforge:", StringComparison.OrdinalIgnoreCase) == true)
            CurseForgeRadio.IsChecked = true;
        // A store page next to an attached project is taken to be the one attaching filled in.
        if (draft.ProjectKey is not null && ModPlanService.StoreProjectOf(draft.Link) is not null) _autoLink = draft.Link;

        foreach (var status in PlanPlaceholderStatus.All)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("SegmentRadio"),
                GroupName = "PlaceholderStatus",
                MinWidth = 0, FontSize = 12, Margin = new Thickness(0, 0, 6, 6),
                IsChecked = status == PlanPlaceholderStatus.Normalize(draft.Status),
                Content = StatusContent(status)
            };
            _statusRadios[status] = radio;
            StatusPanel.Children.Add(radio);
        }

        ShowAttached();
        ShowCheck();
        OnNameChanged(this, null!);
        OnLinkChanged(this, null!);

        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public Task<Draft?> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(null);

    /// <summary>Shows the card as an in-window overlay and returns the edited draft, or null.</summary>
    public static async Task<Draft?> ShowAsync(MainWindow host, Draft draft, bool isNew, string? mcVersion, string? loader)
    {
        var card = new ModPlaceholderDialog(draft, isNew, mcVersion, loader);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    /// <summary>A status radio's label with its coloured dot, as the card's pill shows it.</summary>
    private static FrameworkElement StatusContent(string status)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Ellipse
        {
            Width = 7, Height = 7, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = AccentPalette.Brush(PlanPlaceholderStatus.Hex(status), Brushes.Gray)
        });
        sp.Children.Add(new TextBlock { Text = PlanPlaceholderStatus.Label(status), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private string SelectedStatus =>
        _statusRadios.FirstOrDefault(kv => kv.Value.IsChecked == true).Key ?? PlanPlaceholderStatus.NeedsPort;

    // ── fields ────────────────────────────────────────────────────────────────

    private void OnNameChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void OnLinkChanged(object sender, TextChangedEventArgs e)
    {
        LinkHint.Visibility = string.IsNullOrEmpty(LinkBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ShowCheck(); // a store link alone is enough to check against
    }

    // ── store search ──────────────────────────────────────────────────────────

    /// <summary>One search result as the list shows it.</summary>
    private sealed record Hit(ModSummary Mod)
    {
        public string Name => Mod.Name;
        public string? IconUrl => Mod.IconUrl;
        public string Initial => Mod.Name.Trim() is { Length: > 0 } n ? n[..1].ToUpperInvariant() : "?";
        public string Meta => (Mod.Author is { Length: > 0 } a ? $"by {a}  ·  " : "")
                              + $"{Mod.DownloadCount:N0} downloads  ·  {(Mod.Source == ModSource.CurseForge ? "CurseForge" : "Modrinth")}";
    }

    private int _searchRun;

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // Enter searches here rather than saving the card
        OnSearch(sender, e);
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) query = NameBox.Text.Trim();
        if (query.Length == 0) return;

        var run = ++_searchRun;
        var curseForge = CurseForgeRadio.IsChecked == true;
        var store = curseForge ? "CurseForge" : "Modrinth";
        Say(SearchStatus, $"Searching {store}...");
        try
        {
            var found = curseForge
                ? await App.State.CurseForge.SearchAsync(query, limit: 12)
                : await App.State.Modrinth.SearchAsync(query, limit: 12);
            if (run != _searchRun) return; // a newer search took over

            Results.ItemsSource = found.Select(m => new Hit(m)).ToList();
            ResultsHost.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Say(SearchStatus, found.Count == 0
                ? $"Nothing on {store} matches \"{query}\"."
                : "Pick one to attach it. Results include every version and loader.");
        }
        catch (Exception ex)
        {
            if (run != _searchRun) return;
            ResultsHost.Visibility = Visibility.Collapsed;
            Say(SearchStatus, $"Could not search {store}: {ex.Message}");
        }
    }

    private void OnResultPicked(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is not Hit hit) return;
        var mod = hit.Mod;
        _draft.ProjectKey = PlaceholderStores.KeyFor(mod);
        _draft.ProjectName = mod.Name;
        _draft.IconUrl = mod.IconUrl;
        ClearCheck(); // an answer about another project says nothing about this one

        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = mod.Name;
        // Fill the link from the project, unless the user typed one of their own.
        if (string.IsNullOrWhiteSpace(LinkBox.Text) || string.Equals(LinkBox.Text.Trim(), _autoLink, StringComparison.Ordinal))
        {
            _autoLink = PlaceholderStores.PageUrl(mod);
            LinkBox.Text = _autoLink;
        }

        ResultsHost.Visibility = Visibility.Collapsed;
        Say(SearchStatus, null);
        ShowAttached();
        ShowCheck();
    }

    private void OnDetach(object sender, RoutedEventArgs e)
    {
        _draft.ProjectKey = null;
        _draft.ProjectName = null;
        _draft.IconUrl = null;
        if (string.Equals(LinkBox.Text.Trim(), _autoLink, StringComparison.Ordinal)) LinkBox.Text = "";
        _autoLink = null;
        ClearCheck();
        ShowAttached();
        ShowCheck();
    }

    private void ShowAttached()
    {
        if (_draft.ProjectKey is not { } key)
        {
            AttachedRow.Visibility = Visibility.Collapsed;
            return;
        }
        AttachedRow.Visibility = Visibility.Visible;
        var name = _draft.ProjectName is { Length: > 0 } n ? n : NameBox.Text.Trim();
        AttachedName.Text = name.Length > 0 ? name : key;
        AttachedInitial.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        AttachedMeta.Text = "Attached  ·  " + (key.StartsWith("curseforge:", StringComparison.OrdinalIgnoreCase) ? "CurseForge" : "Modrinth");
        IconLoader.SetDecodeWidth(AttachedIcon, 28);
        IconLoader.SetUrl(AttachedIcon, _draft.IconUrl);
    }

    // ── version check ─────────────────────────────────────────────────────────

    /// <summary>The project a check would ask about: the attached one, else a store link's.</summary>
    private (ModSource Source, string IdOrSlug)? CheckTarget => PlaceholderStores.ProjectOf(_draft.ProjectKey, LinkBox.Text);

    private void ShowCheck()
    {
        if (!IsInitialized) return;
        var can = _mc is not null && CheckTarget is not null;
        CheckRow.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        if (_mc is not null) CheckButton.Content = $"Is there a {PlaceholderStores.TargetLabel(_mc, _loader)} version?";

        // The last answer, while it is still about this pack's version and loader.
        if (can && _draft.CheckedFound is { } found
            && string.Equals(_draft.CheckedFor, PlaceholderStores.Target(_mc!, _loader), StringComparison.OrdinalIgnoreCase))
            Say(CheckText, PlaceholderStores.Answer(found, _mc!, _loader, _draft.CheckedAt));
        else if (!can) Say(CheckText, null);
    }

    private void ClearCheck()
    {
        _draft.CheckedFor = null;
        _draft.CheckedFound = null;
        _draft.CheckedAt = null;
        Say(CheckText, null);
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        if (_mc is null || CheckTarget is not { } target) return;
        CheckButton.IsEnabled = false;
        Say(CheckText, $"Asking {(target.Source == ModSource.CurseForge ? "CurseForge" : "Modrinth")}...");
        try
        {
            var found = await PlaceholderStores.HasVersionAsync(target.Source, target.IdOrSlug, _mc, _loader);
            _draft.CheckedFor = PlaceholderStores.Target(_mc, _loader);
            _draft.CheckedFound = found;
            _draft.CheckedAt = DateTimeOffset.Now;
            var text = PlaceholderStores.Answer(found, _mc, _loader, _draft.CheckedAt);
            // A yes answers the placeholder's question, so an open status moves to Found.
            if (found && PlanPlaceholderStatus.IsOpen(SelectedStatus))
            {
                _statusRadios[PlanPlaceholderStatus.Found].IsChecked = true;
                text += " Marked it Found.";
            }
            Say(CheckText, text);
        }
        catch (Exception ex)
        {
            Say(CheckText, "Could not check: " + ex.Message);
        }
        finally { CheckButton.IsEnabled = true; }
    }

    // ── finish ────────────────────────────────────────────────────────────────

    private static void Say(TextBlock target, string? text)
    {
        target.Text = text ?? "";
        target.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e) => Accept();
    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    private void Accept()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { NameBox.Focus(); return; }
        _draft.Name = name;
        _draft.Status = SelectedStatus;
        _draft.Link = LinkBox.Text.Trim();
        _draft.Note = NoteBox.Text.TrimEnd();
        _tcs.TrySetResult(_draft);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        // Enter saves from the single-line fields; the note takes it as a new line.
        else if (e.Key == Key.Enter && !NoteBox.IsKeyboardFocusWithin && !SearchBox.IsKeyboardFocusWithin)
        {
            Accept();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}

/// <summary>The store side of placeholder cards: project keys and pages, and the one on-demand
/// "is there a version for this pack?" request. Shared by the dialog and the card's menu.</summary>
internal static class PlaceholderStores
{
    public static string? KeyFor(ModSummary mod) => mod.Source switch
    {
        ModSource.Modrinth   => $"modrinth:{mod.Id}",
        ModSource.CurseForge => $"curseforge:{mod.Id}",
        _                    => null
    };

    /// <summary>A project's store page, by slug when it is a real one (CurseForge summaries carry the
    /// name when a project has no slug, as <see cref="PackMod.PageUrl"/> notes), else by id.</summary>
    public static string PageUrl(ModSummary mod)
    {
        var segment = string.IsNullOrWhiteSpace(mod.Slug) || mod.Slug.Any(char.IsWhiteSpace) ? mod.Id : mod.Slug;
        return mod.Source == ModSource.CurseForge
            ? $"https://www.curseforge.com/minecraft/mc-mods/{segment}"
            : $"https://modrinth.com/mod/{segment}";
    }

    /// <summary>The project to ask about: the attached one, else the one a store link points at.</summary>
    public static (ModSource Source, string IdOrSlug)? ProjectOf(string? key, string? link)
    {
        if (key is { Length: > 0 })
        {
            var i = key.IndexOf(':');
            if (i > 0 && i < key.Length - 1)
            {
                var id = key[(i + 1)..];
                if (key.StartsWith("modrinth:", StringComparison.OrdinalIgnoreCase)) return (ModSource.Modrinth, id);
                if (key.StartsWith("curseforge:", StringComparison.OrdinalIgnoreCase)) return (ModSource.CurseForge, id);
            }
        }
        return ModPlanService.StoreProjectOf(link) is { } fromLink ? (fromLink.Source, fromLink.Slug) : null;
    }

    /// <summary>What a check is stored as having asked about: <c>"1.21.1 neoforge"</c>.</summary>
    public static string Target(string mc, string? loader) => loader is null ? mc : $"{mc} {loader.ToLowerInvariant()}";

    /// <summary>"1.21.1 NeoForge", for the user.</summary>
    public static string TargetLabel(string mc, string? loader) => loader is null ? mc : $"{mc} {LoaderLabel(loader)}";

    private static string LoaderLabel(string loader) => loader.ToLowerInvariant() switch
    {
        "neoforge" => "NeoForge",
        "forge"    => "Forge",
        "fabric"   => "Fabric",
        "quilt"    => "Quilt",
        _          => loader
    };

    public static string Answer(bool found, string mc, string? loader, DateTimeOffset? at)
    {
        var when = at is { } t ? $" (checked {t.LocalDateTime:d MMM})" : "";
        return found
            ? $"Yes: there is a {TargetLabel(mc, loader)} version{when}."
            : $"No {TargetLabel(mc, loader)} version yet{when}.";
    }

    /// <summary>Whether the project has any version for this Minecraft version and loader. One request
    /// on Modrinth (the store filters it); on CurseForge one page of files, plus a slug lookup first
    /// when all that is known is a link. Throws when the store can't be asked, so "could not ask" never
    /// reads as "no".</summary>
    public static async Task<bool> HasVersionAsync(ModSource source, string idOrSlug, string mc, string? loader)
    {
        if (source == ModSource.Modrinth)
            return (await App.State.Modrinth.GetVersionsStrictAsync(idOrSlug, mc, loader)).Count > 0;

        if (!int.TryParse(idOrSlug, out var modId))
        {
            var bySlug = await App.State.CurseForge.SearchBySlugAsync(idOrSlug);
            if (bySlug.FirstOrDefault() is not { } project || !int.TryParse(project.Id, out modId))
                throw new InvalidOperationException("CurseForge has no project at that link.");
        }
        return (await App.State.CurseForge.GetVersionsAsync(modId, mc, loader, maxPages: 1)).Count > 0;
    }
}
