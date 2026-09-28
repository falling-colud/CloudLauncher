using System.Windows.Controls;
using System.Windows.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>The selected version's changelog, shown beside or under a list of a mod's versions: the
/// version picker ("Update to version...") and the Versions tab of a mod's page.</summary>
/// <remarks>
/// Fetches and renders the notes the way <see cref="VersionChangelogCard"/> does (the right-click
/// "Changelog" item), with the same wording for loading, empty, error and offline.
/// <para>Easy on the stores' rate limits: only the selected version is ever fetched, and only once
/// the selection has stayed put for <see cref="SettleDelay"/>, so holding an arrow key down the list
/// costs one request rather than one per row. Answers are kept for the life of the pane, so going
/// back to a version is instant, with the catalog's session cache behind that. An answer that
/// arrives after the selection has moved on is dropped.</para>
/// </remarks>
public partial class VersionChangelogPane : UserControl
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>More than a picker ever shows in one sitting; past it the cache starts over rather
    /// than growing for the life of a long-open mod page.</summary>
    private const int CacheLimit = 64;

    private readonly PageState _state;
    private readonly Dictionary<string, (string? Text, bool IsMarkdown)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _settle;
    private ModVersion? _version;
    private ModSummary? _mod;
    private int _generation;
    private CancellationTokenSource? _loadCts;

    public VersionChangelogPane()
    {
        InitializeComponent();
        _state = new PageState(ChangelogView, StateHost, nameof(VersionChangelogPane))
            .Copy(VersionChangelogCard.ChangelogCopy);
        _state.RetryRequested += () =>
        {
            if (_version is not { } version) return;
            _state.Begin(refreshing: false);
            _ = LoadAsync(version, _mod, ++_generation);
        };
        _settle = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = SettleDelay };
        _settle.Tick += OnSettled;
        ShowNothingSelected();
    }

    /// <summary>Shows <paramref name="version"/>'s changelog, or the "pick a version" note for null.
    /// Call it from the list's selection change.</summary>
    /// <param name="mod">The store project, when the caller has it. Optional, as for the card.</param>
    public void Show(ModVersion? version, ModSummary? mod = null)
    {
        _settle.Stop();
        _loadCts?.Cancel();
        var generation = ++_generation;
        _version = version;
        _mod = mod;
        if (version is null)
        {
            ShowNothingSelected();
            return;
        }

        HeaderLabel.Text = $"Changelog for {version.VersionNumber}";
        HeaderLabel.ToolTip = HeaderLabel.Text;
        if (_cache.TryGetValue(Key(version), out var known))
        {
            Render(version, known);
            return;
        }

        // Loading straight away, so the last version's notes never sit under this one's name.
        _state.Begin(refreshing: false);
        // A version hosted on the launcher's server brings its notes with it: nothing to wait for.
        if (version.Source == ModSource.External || !string.IsNullOrWhiteSpace(version.Changelog))
            _ = LoadAsync(version, mod, generation);
        else
            _settle.Start();
    }

    /// <summary>Stops any fetch and lets go of the browser surface. Call it when the host goes away
    /// for good (a card closing), as the card does for its own viewer.</summary>
    public void Release()
    {
        _generation++;
        _settle.Stop();
        _loadCts?.Cancel();
        ChangelogView.Dispose();
    }

    private void OnSettled(object? sender, EventArgs e)
    {
        _settle.Stop();
        if (_version is { } version) _ = LoadAsync(version, _mod, _generation);
    }

    private async Task LoadAsync(ModVersion version, ModSummary? mod, int generation)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var notes = await VersionChangelogCard.FetchAsync(mod, version, cts.Token);
            var blank = VersionChangelogCard.IsBlank(notes.Text, notes.IsMarkdown);

            // CurseForge answers a failed call with no text, so offline is the only failure that can
            // be told from an empty changelog (see the card). Neither is remembered: a retry or a
            // second look should ask again.
            if (blank && version.Source == ModSource.CurseForge && App.State.IsOffline)
            {
                if (generation == _generation) _state.Offline(App.State.OfflineReason ?? "the connection failed");
                return;
            }
            if (notes.Text is not null || version.Source != ModSource.CurseForge) Remember(version, notes);

            if (generation != _generation) return;
            Render(version, notes);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The selection moved on, or the pane was released.
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            if (VersionChangelogCard.OfflineReason(ex, cts.Token) is { } why) _state.Offline(why);
            else _state.Error(VersionChangelogCard.PlainReason(ex), ex);
        }
    }

    private void Render(ModVersion version, (string? Text, bool IsMarkdown) notes)
    {
        if (VersionChangelogCard.IsBlank(notes.Text, notes.IsMarkdown))
        {
            _state.EmptyNext(VersionChangelogCard.ChangelogCopy.EmptyTitle,
                VersionChangelogCard.EmptyBodyFor(version.Source), VersionChangelogCard.ChangelogCopy.Glyph);
            _state.Content(0);
            return;
        }
        // Shown before the viewer is made visible again, so the previous notes are not what appears
        // for the first frame.
        ChangelogView.Show(notes.Text, notes.IsMarkdown);
        _state.Content(1);
    }

    private void ShowNothingSelected()
    {
        HeaderLabel.Text = "Changelog";
        HeaderLabel.ToolTip = null;
        _state.EmptyNext("No version selected", "Pick a version to read its changelog.", VersionChangelogCard.ChangelogCopy.Glyph);
        _state.Content(0);
    }

    private void Remember(ModVersion version, (string? Text, bool IsMarkdown) notes)
    {
        if (_cache.Count >= CacheLimit) _cache.Clear();
        _cache[Key(version)] = notes;
    }

    private static string Key(ModVersion version) => $"{version.Source}:{version.Id}";
}
