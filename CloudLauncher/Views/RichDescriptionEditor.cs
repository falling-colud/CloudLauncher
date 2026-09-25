using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CloudLauncher.Views;

/// <summary>The description panel: a WebView2 surface that either renders a description or lets its
/// owner write one, with the same engine for both.</summary>
/// <remarks>
/// <para>The page posts every edit back as HTML via <c>postMessage</c>; it is cached here so the
/// synchronous save path can read it without a script call.</para>
/// <para>WebView2 is a native "airspace" surface that WPF can't move with a RenderTransform, so a
/// bitmap of the last frame covers it during transitions, as in
/// <see cref="RichDescriptionView"/>.</para>
/// </remarks>
public sealed class RichDescriptionEditor : UserControl, IDisposable
{
    /// <summary>Largest image that can be inserted. Images are embedded in the description text, which
    /// syncs to the server.</summary>
    private const long MaxImageBytes = 4L * 1024 * 1024;

    // Needs a per-user writable folder: the default next to the exe is read-only under Program
    // Files. Shared with RichDescriptionView so both use one WebView2 process tree.
    private static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudLauncher", "WebView2");

    private readonly WebView2 _web = new();
    private readonly Image _snapshot = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _revealTimer = new();

    /// <summary>
    /// The origin the document is served from. A made-up host, matched by the resource filter
    /// below, so nothing ever leaves the machine to fetch the page itself.
    /// </summary>
    private const string DocumentOrigin = "https://description.cloudlauncher.invalid";
    private const string DocumentUrl = DocumentOrigin + "/index.html";

    private bool _initStarted;
    private bool _ready;
    private bool _disposed;
    private bool _transitionActive;

    /// <summary>What the virtual origin serves on the next request.</summary>
    private string _documentHtml = "";
    private string? _pendingHtml;
    private string? _lastContent;
    private bool _lastIsMarkdown;
    private bool _editing;
    private RichDescriptionOptions? _options;
    private Action? _onChanged;

    /// <summary>The editor's current document, as the page last reported it.</summary>
    private string _cachedHtml = "";

    public RichDescriptionEditor()
    {
        ApplyThemeBackground();
        _web.NavigationStarting += OnNavigationStarting;
        _web.NavigationCompleted += OnNavigationCompleted;
        _web.CoreWebView2InitializationCompleted += OnCoreInitialized;

        var grid = new Grid();
        grid.Children.Add(_web);
        grid.Children.Add(_snapshot);
        Content = grid;

        // Placeholder until the first Show*() call.
        _documentHtml = PackText.WrapHtmlDocument("<p><em>No description.</em></p>", legacyIe: false);
        _pendingHtml = _documentHtml;

        _revealTimer.Tick += OnRevealTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // ThemeService.Changed is subscribed in OnLoaded: it is a static event, and subscribing here
        // would keep this control (and its WebView2) alive for the process lifetime, since nothing
        // calls Dispose().
    }

    /// <summary>True while the control is showing the editor rather than the rendered description.</summary>
    public bool IsEditing => _editing;

    // ── modes ────────────────────────────────────────────────────────────────

    /// <summary>Opens the description for writing. <paramref name="onChanged"/> fires (debounced by
    /// the page) whenever the document changes.</summary>
    public void ShowEditor(string? content, bool isMarkdown, Action? onChanged = null)
    {
        _editing = true;
        _onChanged = onChanged;
        _options = null;
        _lastContent = content;
        _lastIsMarkdown = isMarkdown;
        _cachedHtml = EditorBodyFor(content, isMarkdown);
        Navigate(PackText.WrapEditorDocument(_cachedHtml));
    }

    /// <summary>Renders the description read-only.</summary>
    public void ShowViewer(string? content, bool isMarkdown, RichDescriptionOptions? options = null)
    {
        // Already showing this document read-only, loaded, with nothing queued.
        var wasViewing = !_editing && _ready && _pendingHtml is null;
        _editing = false;
        _onChanged = null;
        _options = options;
        _lastContent = content;
        _lastIsMarkdown = isMarkdown;

        var runnable = options?.EnableCommandRun == true;
        var html = string.IsNullOrWhiteSpace(content)
            ? PackText.WrapHtmlDocument("<p><em>No description.</em></p>", legacyIe: false)
            : PackText.WrapHtmlDocument(
                PackText.PrepareDescriptionHtml(content, isMarkdown, runnable),
                runnableCommands: runnable, legacyIe: false);
        // Re-navigating to the same document would blank and redraw the view. A theme change still gets
        // through, since the palette is baked into the HTML.
        if (wasViewing && html == _documentHtml) return;
        Navigate(html);
    }

    /// <summary>
    /// The document currently in the editor, ready to save. Synchronous because it reads the copy
    /// the page pushes on every change rather than asking the browser for it.
    /// </summary>
    public bool TryGetHtml(out string html)
    {
        html = PackText.NormalizeEditorHtmlForSave(_cachedHtml);
        return _editing && !string.IsNullOrEmpty(_cachedHtml);
    }

    private static string EditorBodyFor(string? content, bool isMarkdown)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        return isMarkdown || !PackText.LooksLikeHtml(content)
            ? PackText.MarkdownLiteToHtml(content)
            : PackText.SanitizeDescriptionHtml(content) ?? content;
    }

    // ── page -> host ─────────────────────────────────────────────────────────

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the document this control serves may talk to it, and only while it is the editor.
        if (!_editing || !IsOwnDocument(e.Source)) return;

        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch { return; }   // a non-string message is not ours

        string? type = null;
        string? html = null;
        string? text = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t)) type = t.GetString();
            if (root.TryGetProperty("html", out var h)) html = h.GetString();
            if (root.TryGetProperty("text", out var s)) text = s.GetString();
        }
        catch { return; }

        switch (type)
        {
            case PackText.EditorMessage.Changed:
                if (!_editing) return;
                _cachedHtml = html ?? "";
                _onChanged?.Invoke();
                break;

            case PackText.EditorMessage.PickImage:
                PickImage();
                break;

            case PackText.EditorMessage.ApplySource:
                ApplySource(text ?? "");
                break;
        }
    }

    /// <summary>
    /// Inserts an image from this PC as a data URL, so the description stays one self-contained
    /// document: a file path would break for everyone the instance is shared with.
    /// </summary>
    private void PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > MaxImageBytes)
            {
                _ = AppDialog.MessageAsync(Window.GetWindow(this), "Image too large",
                    $"{Path.GetFileName(dialog.FileName)} is {info.Length / (1024.0 * 1024.0):0.#} MB. " +
                    "A description carries its images with it, so they need to stay under 4 MB.");
                return;
            }

            var bytes = File.ReadAllBytes(dialog.FileName);
            var mime = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png"
            };
            var url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            _ = RunScriptAsync($"insertImageSrc({JsonSerializer.Serialize(url)},{JsonSerializer.Serialize(Path.GetFileName(dialog.FileName))})");
        }
        catch (Exception ex)
        {
            AppLog.LogError("description-image", ex);
        }
    }

    /// <summary>Takes the source view's text back into the editor. HTML stays HTML and anything else is
    /// treated as Markdown, so a pasted README works.</summary>
    private void ApplySource(string text)
    {
        var body = string.IsNullOrWhiteSpace(text)
            ? "<p><br></p>"
            : PackText.LooksLikeHtml(text)
                ? PackText.SanitizeDescriptionHtml(text) ?? ""
                : PackText.MarkdownLiteToHtml(text);

        // Markdown turned into HTML has not been through the sanitiser, so the prepared body is.
        body = PackText.SanitizeDescriptionHtml(PackText.PrepareEditorBodyHtml(body)) ?? "<p><br></p>";
        _cachedHtml = body;
        _ = RunScriptAsync($"setEditorHtml({JsonSerializer.Serialize(body)})");
    }

    private async Task RunScriptAsync(string script)
    {
        if (!_ready || _web.CoreWebView2 is null) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex) { AppLog.LogError("description-script", ex); }
    }

    // ── theme ────────────────────────────────────────────────────────────────

    /// <summary>The palette is baked into the HTML, so a theme change means regenerating the
    /// document.</summary>
    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnThemeChanged); return; }
        if (_disposed) return;
        ApplyThemeBackground();

        // Reload the live document rather than the last loaded content, so nothing typed is lost.
        if (_editing) Navigate(PackText.WrapEditorDocument(_cachedHtml));
        else ShowViewer(_lastContent, _lastIsMarkdown, _options);
    }

    private void ApplyThemeBackground()
    {
        var hex = PackText.HtmlPalette.Current.Background;
        try
        {
            var colour = (Color)ColorConverter.ConvertFromString(hex);
            Background = new SolidColorBrush(colour);
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, colour.R, colour.G, colour.B);
        }
        catch { /* a malformed colour must not take the panel down */ }
    }

    // ── transition handling (airspace-safe slide) ────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Animations.Animate.AirspaceTransitionChanged -= OnAirspaceTransitionChanged;
        Animations.Animate.AirspaceTransitionChanged += OnAirspaceTransitionChanged;
        MainWindow.OverlayChanged -= OnOverlayChanged;
        MainWindow.OverlayChanged += OnOverlayChanged;
        ThemeService.Changed -= OnThemeChanged;   // never double-subscribe across reloads
        ThemeService.Changed += OnThemeChanged;
        if (Animations.Animate.IsTransitionActiveFor(this)) BeginTransitionOverlay();
        if (MainWindow.IsOverlayVisible) OnOverlayChanged(true);
        EnsureWebViewInitialized();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Animations.Animate.AirspaceTransitionChanged -= OnAirspaceTransitionChanged;
        MainWindow.OverlayChanged -= OnOverlayChanged;
        ThemeService.Changed -= OnThemeChanged;
        _revealTimer.Stop();
    }

    private bool _overlayCover;

    /// <summary>A card is over the window. The native surface would paint on top of it, so the editor
    /// hides behind its snapshot until the card closes (same as RichDescriptionView).</summary>
    private void OnOverlayChanged(bool visible)
    {
        _overlayCover = visible;
        if (visible)
        {
            if (_snapshot.Source is not null) _snapshot.Visibility = Visibility.Visible;
            _web.Visibility = Visibility.Hidden;
        }
        else if (!_transitionActive)
        {
            _web.Visibility = Visibility.Visible;
            _snapshot.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAirspaceTransitionChanged()
    {
        if (Animations.Animate.IsTransitionActiveFor(this)) BeginTransitionOverlay();
    }

    private void BeginTransitionOverlay()
    {
        if (_snapshot.Source is not null)
        {
            _snapshot.Visibility = Visibility.Visible;
            _web.Visibility = Visibility.Hidden;
        }
        _transitionActive = true;
        _revealTimer.Stop();
        _revealTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Animations.Animate.TransitionRemainingMs));
        _revealTimer.Start();
    }

    private void OnRevealTimerTick(object? sender, EventArgs e)
    {
        _revealTimer.Stop();
        if (Animations.Animate.IsTransitionActiveFor(this)) { BeginTransitionOverlay(); return; }
        _transitionActive = false;
        if (_overlayCover) return;   // still under a card: stays covered until it closes
        _web.Visibility = Visibility.Visible;
        _snapshot.Visibility = Visibility.Collapsed;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_transitionActive && !_overlayCover) _web.Visibility = Visibility.Visible;
        _ = CaptureSnapshotAsync();
    }

    private async Task CaptureSnapshotAsync()
    {
        if (_web.CoreWebView2 is null) return;
        try
        {
            using var ms = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            _snapshot.Source = bmp;
        }
        catch { /* snapshot is best-effort; without it a transition just hides the surface */ }
    }

    // ── WebView2 plumbing ────────────────────────────────────────────────────

    /// <summary>Loads the document from a virtual origin instead of <c>NavigateToString</c>, whose 2 MB
    /// limit one embedded image (a base64 data URL) can exceed.</summary>
    private void Navigate(string html)
    {
        _documentHtml = html;
        if (_ready && _web.CoreWebView2 is not null)
        {
            _pendingHtml = null;
            _web.CoreWebView2.Navigate(DocumentUrl);
        }
        else
        {
            _pendingHtml = html;
            EnsureWebViewInitialized();
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = _web.CoreWebView2;
        if (core is null) return;
        try
        {
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_documentHtml));
            e.Response = core.Environment.CreateWebResourceResponse(
                stream, 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
        catch (Exception ex) { AppLog.LogError("description-document", ex); }
    }

    private async void EnsureWebViewInitialized()
    {
        if (_initStarted || _disposed) return;
        _initStarted = true;
        try
        {
            Directory.CreateDirectory(UserDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, null);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // Leave _initStarted set: a retry would almost certainly hit the same fault
            // (missing WebView2 runtime, locked profile). Surface it in the log instead.
            AppLog.LogError("WebView2", ex);
        }
    }

    private void OnCoreInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _web.CoreWebView2 is null)
        {
            if (e.InitializationException is { } ex) AppLog.LogError("WebView2", ex);
            return;
        }

        DescriptionWebView.Harden(_web.CoreWebView2);
        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        // Ctrl+B / Ctrl+I / Ctrl+Z have to reach the document, so browser accelerators stay on here
        // (the read-only viewer turns them off).
        s.AreBrowserAcceleratorKeysEnabled = true;

        _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _web.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        _web.CoreWebView2.AddWebResourceRequestedFilter(
            DocumentOrigin + "/*", CoreWebView2WebResourceContext.Document);
        _web.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

        _ready = true;
        if (_pendingHtml is not null)
        {
            _pendingHtml = null;
            _web.CoreWebView2.Navigate(DocumentUrl);
        }
    }

    /// <summary>True for the page this control serves, and nothing else: a prefix test would also
    /// match a real host such as <c>description.cloudlauncher.invalid.example.com</c>.</summary>
    private static bool IsOwnDocument(string? uri) =>
        uri is not null
        && (uri.Equals(DocumentUrl, StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith(DocumentUrl + "#", StringComparison.OrdinalIgnoreCase));

    // Only our own document may load. Everything else is cancelled: a clicked command link fires the
    // command callback, a clicked http(s) link opens in the real browser, and navigations the page
    // starts on its own open nothing.
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        if (IsOwnDocument(uri) || uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        if (!e.IsUserInitiated) return;

        if (PackText.TryParseCommandRunUri(uri, out var command))
        {
            if (_options?.EnableCommandRun == true) _options.OnCommandRun?.Invoke(command);
            return;
        }

        OpenExternally(uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated) OpenExternally(e.Uri);
    }

    private static void OpenExternally(string? uri)
    {
        // A relative link resolves against the made-up origin; there is nothing there to open.
        if (uri is not null && uri.StartsWith(DocumentOrigin + "/", StringComparison.OrdinalIgnoreCase)) return;
        SafeLaunch.OpenUrl(uri);
    }

    public void Dispose()
    {
        ThemeService.Changed -= OnThemeChanged;
        if (_disposed) return;
        _disposed = true;
        Animations.Animate.AirspaceTransitionChanged -= OnAirspaceTransitionChanged;
        _revealTimer.Stop();
        _web.NavigationStarting -= OnNavigationStarting;
        _web.NavigationCompleted -= OnNavigationCompleted;
        _web.CoreWebView2InitializationCompleted -= OnCoreInitialized;
        if (_web.CoreWebView2 is not null)
        {
            _web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            _web.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            _web.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
        }
        _web.Dispose();
    }
}
