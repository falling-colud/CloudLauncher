using System.Collections;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>One version's changelog, opened from the right-click menu of any list of a mod's
/// versions. Previous / Next (or the arrow keys) step through that list in its on-screen
/// order.</summary>
/// <remarks>Store versions load their notes on demand through
/// <see cref="ModVersionCatalog.GetChangelogAsync"/> (Modrinth writes Markdown, CurseForge HTML),
/// cached for the session. Versions hosted on the launcher's server bring their notes with them and
/// skip the catalog, which would treat their id as a Modrinth one.</remarks>
public partial class VersionChangelogCard : UserControl
{
    /// <summary>Page-state wording, so loading, empty, error and offline look the way they do on every
    /// other page. Shared with <see cref="VersionChangelogPane"/>.</summary>
    internal static readonly PageCopy ChangelogCopy = new()
    {
        Glyph = "",
        FilteredGlyph = "",
        Verb = "Loading",
        Noun = "changelog",
        LoadingLine = "Fetching the changelog...",
        EmptyTitle = "This version has no changelog",
        EmptyBody = "Nothing was written for it.",
        FilteredTitle = "This version has no changelog",
        FilteredBody = "Nothing was written for it.",
        ErrorTitle = "Could not load the changelog",
        OfflineTitle = "You are offline",
        OfflineBody = "The store cannot be reached ({0}). Try again once the connection is back."
    };

    private readonly string _name;
    private readonly IReadOnlyList<ModVersion> _versions;
    private readonly ModSummary? _mod;
    private readonly PageState _state;
    private readonly TaskCompletionSource<bool> _tcs = new();
    private int _index;
    private int _generation;
    private CancellationTokenSource? _loadCts;

    /// <param name="name">The mod (or pack) the versions belong to, for the title.</param>
    /// <param name="versions">The list the card was opened from, in its on-screen order.</param>
    /// <param name="index">Which of <paramref name="versions"/> to open on.</param>
    /// <param name="mod">The store project, when the caller has it. Optional: the catalog reads a
    /// CurseForge version's two ids from the version itself and only falls back to this.</param>
    public VersionChangelogCard(string name, IReadOnlyList<ModVersion> versions, int index, ModSummary? mod = null)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Count == 0) throw new ArgumentException("There is no version to show.", nameof(versions));

        InitializeComponent();
        _name = string.IsNullOrWhiteSpace(name) ? "Changelog" : name.Trim();
        _versions = versions;
        _index = Math.Clamp(index, 0, versions.Count - 1);
        _mod = mod;

        _state = new PageState(ChangelogView, StateHost, nameof(VersionChangelogCard)).Copy(ChangelogCopy);
        _state.RetryRequested += () => _ = LoadAsync();

        Focusable = true;
        Loaded += (_, _) => { Animate.SlideFadeIn(this, 0, 14, 200); Focus(); };

        // Started now rather than on Loaded, so the request is already on the wire while the card
        // slides in.
        _ = LoadAsync();
    }

    /// <summary>Completes when the card is dismissed.</summary>
    public Task<bool> Result => _tcs.Task;

    /// <summary>The backdrop's cancel, handed to <see cref="MainWindow.ShowCardAsync"/>.</summary>
    public void Cancel() => _tcs.TrySetResult(false);

    // ── showing it ───────────────────────────────────────────────────────────

    /// <summary>Opens the card over <paramref name="host"/>'s dialog layer and waits for it to close.</summary>
    /// <remarks>A second card stacks over any card already open, which is how the version picker
    /// shows a changelog without closing itself.</remarks>
    public static async Task ShowAsync(MainWindow host, string name, IReadOnlyList<ModVersion> versions,
        int index, ModSummary? mod = null)
    {
        var card = new VersionChangelogCard(name, versions, index, mod);
        try
        {
            await host.ShowCardAsync(card, card.Result, card.Cancel,
                new ResizableCardSpec("version-changelog", 720, 620, MinWidth: 460, MinHeight: 320));
        }
        finally { card.Release(); }
    }

    /// <summary>Opens the card over whichever window <paramref name="origin"/> is in.</summary>
    /// <param name="shell">The launcher window, for a caller that may not be in any window at the
    /// moment of the click.</param>
    /// <remarks>Windows without a card layer (the browse pages beside a running game in
    /// <see cref="MinecraftHostWindow"/>, the explorer window) get the card as a small owned window,
    /// since the launcher window may be minimised.</remarks>
    public static Task ShowAsync(FrameworkElement origin, string name, IReadOnlyList<ModVersion> versions,
        int index, ModSummary? mod = null, MainWindow? shell = null)
    {
        var window = Window.GetWindow(origin);
        if (window is MainWindow main) return ShowAsync(main, name, versions, index, mod);
        if (window is null && (shell ?? Application.Current?.MainWindow as MainWindow) is { } host)
            return ShowAsync(host, name, versions, index, mod);
        return ShowInWindowAsync(window, new VersionChangelogCard(name, versions, index, mod));
    }

    /// <summary>The card as a window of its own, for a caller outside the launcher window.</summary>
    /// <remarks>A plain window, not a borderless layered one like the screenshot viewer: WebView2
    /// cannot draw inside a window with <c>AllowsTransparency</c>. Modeless, so a changelog opened
    /// beside a running game does not freeze the game behind it.</remarks>
    private static async Task ShowInWindowAsync(Window? owner, VersionChangelogCard card)
    {
        // The window provides the frame, so drop the card's own outline and shadow.
        card.Chrome.CornerRadius = new CornerRadius(0);
        card.Chrome.BorderThickness = new Thickness(0);
        card.Chrome.Effect = null;

        var window = new Window
        {
            Title = card._name + " · Changelog",
            Content = card,
            Width = 720,
            Height = 620,
            MinWidth = 460,
            MinHeight = 320,
            Owner = owner,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        window.SetResourceReference(BackgroundProperty, "Surface2Brush");
        window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");

        var closed = false;
        window.Closed += (_, _) =>
        {
            closed = true;
            card.Cancel();
        };

        try
        {
            window.Show();
            await card.Result;
        }
        finally
        {
            if (!closed) window.Close();
            card.Release();
        }
    }

    /// <summary>
    /// The versions a list is showing, in the order it shows them, and where the clicked row sits.
    /// </summary>
    /// <param name="rows">The list's items. Pass <c>ItemsControl.Items</c>, which follows the user's
    /// column sort, rather than the source collection, which does not.</param>
    /// <param name="versionOf">The row's version, or null for a row that has none.</param>
    public static (IReadOnlyList<ModVersion> Versions, int Index) FromList<TRow>(
        IEnumerable rows, TRow clicked, Func<TRow, ModVersion?> versionOf) where TRow : class
    {
        var list = new List<ModVersion>();
        var index = 0;
        foreach (var row in rows.OfType<TRow>())
        {
            if (versionOf(row) is not { } version) continue;
            if (ReferenceEquals(row, clicked)) index = list.Count;
            list.Add(version);
        }
        if (list.Count == 0 && versionOf(clicked) is { } only) list.Add(only);
        return (list, index);
    }

    /// <summary>A version uploaded to the launcher's own server, in the shape the card reads.</summary>
    public static ModVersion FromHosted(HostedModVersionInfo version) => new(
        version.Id.ToString(),
        version.VersionString,
        version.VersionString,
        SplitCsv(version.McVersionsCsv),
        SplitCsv(version.LoadersCsv),
        version.ReleaseChannel,
        version.PublishedAt,
        0,
        version.Changelog,
        ModSource.External,
        Array.Empty<ModVersionFile>(),
        Array.Empty<ModDependency>());

    private static string[] SplitCsv(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>Points the card at the current version and fetches its notes.</summary>
    /// <remarks>Stepping quickly can start a fetch before the last one finishes. The previous one is
    /// cancelled, and the generation check drops its answer if it arrives anyway.</remarks>
    private async Task LoadAsync()
    {
        var generation = ++_generation;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        var version = _versions[_index];

        ShowHeader(version);
        _state.Begin(refreshing: false);

        try
        {
            var (text, isMarkdown) = await FetchAsync(_mod, version, ct);
            if (generation != _generation) return;

            if (IsBlank(text, isMarkdown))
            {
                // CurseForge returns no text on failure, so being offline is the only failure that
                // can be told apart from an empty changelog here.
                if (version.Source == ModSource.CurseForge && App.State.IsOffline)
                {
                    _state.Offline(App.State.OfflineReason ?? "the connection failed");
                    return;
                }
                _state.EmptyNext(ChangelogCopy.EmptyTitle, EmptyBodyFor(version.Source), ChangelogCopy.Glyph);
                _state.Content(0);
                return;
            }

            // Shown before the viewer is made visible again, so the previous version's notes are
            // not what appears for the first frame.
            ChangelogView.Show(text, isMarkdown);
            _state.Content(1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a step, or the card closed.
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            if (OfflineReason(ex, ct) is { } why) _state.Offline(why);
            else _state.Error(PlainReason(ex), ex);
        }
    }

    /// <summary>Why nothing answered, or null when something did.</summary>
    /// <remarks>An <see cref="HttpRequestException"/> with a status code (404, 429, 500) is the store
    /// answering, not a dead connection, even though the transport classifier treats every
    /// HttpRequestException as offline.</remarks>
    internal static string? OfflineReason(Exception ex, CancellationToken ct) => ex switch
    {
        OfflineException offline => offline.Reason ?? App.State.OfflineReason ?? "the connection failed",
        HttpRequestException { StatusCode: not null } => null,
        _ => Connectivity.DescribeTransportFailure(ex, ct)
    };

    /// <summary>One version's notes, and whether they are Markdown.</summary>
    /// <remarks>
    /// The catalog is never asked about a hosted version: it would send the id to Modrinth, which
    /// throws. The Modrinth path throws on a failed call and the CurseForge path returns null, so the
    /// caller has to handle both.
    /// </remarks>
    internal static async Task<(string? Text, bool IsMarkdown)> FetchAsync(ModSummary? mod, ModVersion version,
        CancellationToken ct)
    {
        if (version.Source == ModSource.External)
            return (PlainTextAsMarkdown(version.Changelog), true);

        // The catalog only needs the mod as a CurseForge fallback (it reads "modId:fileId" from the
        // version id first), so a stand-in is enough when the caller has no ModSummary.
        var summary = mod is not null && mod.Source == version.Source ? mod : StandIn(version);
        return await App.State.ModVersions.GetChangelogAsync(summary, version, ct);
    }

    private static ModSummary StandIn(ModVersion version)
    {
        var modId = version.Source == ModSource.CurseForge && version.Id.Split(':', 2) is [var id, _] ? id : "";
        return new ModSummary(modId, "", "", null, null, 0, null, version.Source, Array.Empty<string>());
    }

    /// <summary>Keeps a hand-typed changelog's line breaks.</summary>
    /// <remarks>Hosted changelogs come from a plain text box, and Markdown joins single line breaks, so
    /// two trailing spaces are added to each. Structured Markdown renders the same either way.</remarks>
    internal static string? PlainTextAsMarkdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join("\n", lines.Select(line => line.Trim().Length == 0 ? "" : line.TrimEnd() + "  "));
    }

    /// <summary>True when there is nothing to read. CurseForge stores an untouched editor as empty
    /// markup, such as <c>&lt;p&gt;&lt;/p&gt;</c> or a lone non-breaking space.</summary>
    internal static bool IsBlank(string? text, bool isMarkdown)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (isMarkdown) return false;
        return PackText.FormatOverview(text).Length == 0
            && !Regex.IsMatch(text, @"<\s*img\b", RegexOptions.IgnoreCase);
    }

    internal static string EmptyBodyFor(ModSource source) => source switch
    {
        ModSource.CurseForge => "CurseForge has no notes for this file.",
        ModSource.External => "Nothing was written for it when it was uploaded.",
        _ => "Its author did not write any notes for it."
    };

    /// <summary>A sentence a person can act on. The exception itself goes to the launcher log.</summary>
    internal static string PlainReason(Exception ex) => ex switch
    {
        SessionExpiredException => "You have been signed out. Sign in again, then retry.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "The store no longer has this version.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable }
            => "The store is busy right now. Wait a moment, then retry.",
        _ => "The store answered with an error."
    };

    // ── the header ───────────────────────────────────────────────────────────

    private void ShowHeader(ModVersion version)
    {
        var title = TitleFor(_name, version.VersionNumber);
        TitleLabel.Text = title;
        TitleLabel.ToolTip = title;
        MetaLabel.Text = MetaFor(version);

        var count = _versions.Count;
        var several = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.Visibility = several;
        NextButton.Visibility = several;
        PreviousButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < count - 1;
        PreviousButton.ToolTip = _index > 0
            ? $"Previous in the list: {_versions[_index - 1].VersionNumber} (Left arrow)"
            : "This is the first version in the list";
        NextButton.ToolTip = _index < count - 1
            ? $"Next in the list: {_versions[_index + 1].VersionNumber} (Right arrow)"
            : "This is the last version in the list";
        FooterNote.Text = count > 1
            ? $"{_index + 1} of {count} · < > to step through · Esc to close"
            : "Esc to close";
    }

    /// <summary>"Sodium 0.6.3", but not "Sodium Sodium 0.6.3 for NeoForge": CurseForge names a
    /// version after its file, which usually starts with the mod's own name already.</summary>
    internal static string TitleFor(string name, string? versionNumber)
    {
        var number = versionNumber?.Trim() ?? "";
        if (number.Length == 0) return name;
        return number.Contains(name, StringComparison.OrdinalIgnoreCase) ? number : $"{name} {number}";
    }

    /// <summary>Channel, date, Minecraft versions, loaders and where the notes come from.</summary>
    /// <remarks>CurseForge puts loaders, sides and Java versions in the same "game versions" array as
    /// Minecraft versions, and the launcher splits them by a <c>1.</c> prefix that the newer <c>26.x</c>
    /// numbering lacks, so both lines are filtered here.</remarks>
    internal static string MetaFor(ModVersion version)
    {
        var parts = new List<string> { ModUpdateChannel.Label(version.ReleaseChannel) };
        if (version.DatePublished != default)
            parts.Add("published " + TimeFormat.Date(version.DatePublished));

        var minecraft = version.GameVersions
            .Where(v => v.Length > 0 && char.IsDigit(v[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (minecraft.Count > 0) parts.Add("MC " + JoinCapped(minecraft, 5));

        var loaders = version.Loaders
            .Where(IsLoaderName)
            .Select(LoaderLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (loaders.Count > 0) parts.Add(JoinCapped(loaders, 4));

        parts.Add(version.Source switch
        {
            ModSource.CurseForge => "CurseForge",
            ModSource.External => "CloudLauncher",
            _ => "Modrinth"
        });
        return string.Join(" · ", parts);
    }

    private static bool IsLoaderName(string value) =>
        value.Length > 0
        && !char.IsDigit(value[0])
        && !value.StartsWith("Java", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("Client", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("Server", StringComparison.OrdinalIgnoreCase)
        // Modrinth's "loader" for a resource pack or data pack is the game itself.
        && !value.Equals("minecraft", StringComparison.OrdinalIgnoreCase);

    private static string LoaderLabel(string value) => value.ToLowerInvariant() switch
    {
        "neoforge" => "NeoForge",
        "optifine" => "OptiFine",
        _ => char.ToUpperInvariant(value[0]) + value[1..]
    };

    private static string JoinCapped(IReadOnlyList<string> values, int max) =>
        values.Count <= max
            ? string.Join(", ", values)
            : string.Join(", ", values.Take(max)) + $" and {values.Count - max} more";

    // ── stepping and closing ─────────────────────────────────────────────────

    private void Step(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _versions.Count) return;
        _index = next;
        _ = LoadAsync();
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => Step(-1);
    private void OnNext(object sender, RoutedEventArgs e) => Step(1);
    private void OnClose(object sender, RoutedEventArgs e) => _tcs.TrySetResult(true);

    /// <remarks>These also arrive while the changelog itself has focus: the WebView2 control hands
    /// Escape and the arrow keys back to WPF as key events before the page sees them.</remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _tcs.TrySetResult(true);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Right)
        {
            Step(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Stops the fetch and lets go of the browser surface once the card is gone.</summary>
    /// <remarks>The viewer subscribes to the app-wide theme event and only unsubscribes when disposed,
    /// so a card that was just dropped would stay alive, WebView2 included, for the rest of the
    /// session.</remarks>
    private void Release()
    {
        _generation++;
        _loadCts?.Cancel();
        ChangelogView.Dispose();
    }
}
