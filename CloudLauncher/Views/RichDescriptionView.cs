using System.Diagnostics;
using System.IO;
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

/// <summary>
/// Read-only rich-description viewer backed by WebView2 (Chromium), used by the mod /
/// resource-pack / world / pack browse pages. It renders the same HTML
/// <see cref="PackText.WrapHtmlDocument"/> produces, but Chromium handles DPI scaling
/// correctly (no post-load blow-up), renders SVG badges, and toggles native
/// &lt;details&gt; spoilers — so the IE-era hacks (DPI feature flags, manual wheel
/// routing, X-UA-Compatible quirks) aren't needed here. The legacy
/// <see cref="RichDescriptionHelper"/> still backs the description editor, which leans on
/// IE-specific contenteditable behaviour.
///
/// WebView2 is a native ("airspace") surface WPF can't move with a RenderTransform, so during a
/// page/tab slide it would sit still and teleport into place at the end. To animate smoothly we
/// keep a bitmap snapshot of the rendered description and, while a transition is in flight on an
/// ancestor, show that snapshot (a normal WPF visual that slides with the transform) in place of
/// the live surface, swapping back once the slide settles.
/// </summary>
public sealed class RichDescriptionView : UserControl, IDisposable
{
    // A per-user writable folder is required; the default (next to the exe) is read-only
    // once the launcher is installed under Program Files.
    private static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudLauncher", "WebView2");

    private readonly WebView2 _web = new();
    // Overlay shown during a transition: the last rendered frame, which slides with the animation.
    private readonly Image _snapshot = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _revealTimer = new();

    private bool _initStarted;
    private bool _ready;
    private bool _loadingOwnContent;
    private bool _disposed;
    private bool _transitionActive;
    private string? _pendingHtml;
    private string? _lastHtml;
    private RichDescriptionOptions? _options;

    public RichDescriptionView()
    {
        // Paint the document background underneath so there's no white flash before the
        // first frame renders (matches the body colour in WrapHtmlDocument, whatever the theme).
        ApplyThemeBackground();
        _web.NavigationStarting += OnNavigationStarting;
        _web.NavigationCompleted += OnNavigationCompleted;
        _web.CoreWebView2InitializationCompleted += OnCoreInitialized;

        var grid = new Grid();
        grid.Children.Add(_web);
        grid.Children.Add(_snapshot); // overlays the web view while a transition slides
        Content = grid;

        // Show a blank document until the first real Show() so the panel isn't an empty void.
        _pendingHtml = PackText.WrapHtmlDocument("<p><em>No description.</em></p>", legacyIe: false);
        _lastHtml = _pendingHtml;

        _revealTimer.Tick += OnRevealTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Services.ThemeService.Changed += OnThemeChanged;
    }

    /// <summary>
    /// A description is a web document, so changing the launcher's colours does not repaint it —
    /// the HTML has to be generated again. Re-showing the same content is enough, and it is what
    /// makes "colours apply immediately" true on this panel too.
    /// </summary>
    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnThemeChanged); return; }
        if (_disposed) return;
        ApplyThemeBackground();
        if (_lastContent is not null) Show(_lastContent, _lastIsMarkdown, _options);
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

    private string? _lastContent;
    private bool _lastIsMarkdown;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Animations.Animate.AirspaceTransitionChanged -= OnAirspaceTransitionChanged; // avoid double-subscribe
        Animations.Animate.AirspaceTransitionChanged += OnAirspaceTransitionChanged;
        // If we loaded into a transition already in flight, cover with the snapshot immediately.
        if (Animations.Animate.IsTransitionActiveFor(this)) BeginTransitionOverlay();
        EnsureWebViewInitialized();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Animations.Animate.AirspaceTransitionChanged -= OnAirspaceTransitionChanged;
        _revealTimer.Stop();
    }

    /// <summary>Render a description (HTML or markdown) into the view.</summary>
    public void Show(string? content, bool isMarkdown = false, RichDescriptionOptions? options = null)
    {
        _lastContent = content;
        _lastIsMarkdown = isMarkdown;
        _options = options;
        var runnable = options?.EnableCommandRun == true;

        string html;
        if (string.IsNullOrWhiteSpace(content))
        {
            html = PackText.WrapHtmlDocument("<p><em>No description.</em></p>", legacyIe: false);
        }
        else
        {
            var body = PackText.PrepareDescriptionHtml(content, isMarkdown, runnable);
            html = PackText.WrapHtmlDocument(body, runnableCommands: runnable, legacyIe: false);
        }

        _lastHtml = html;
        if (_ready)
            NavigateOwn(html);
        else
        {
            _pendingHtml = html;
            EnsureWebViewInitialized();
        }
    }

    // ── transition handling (airspace-safe slide) ─────────────────────────────

    private void OnAirspaceTransitionChanged()
    {
        if (Animations.Animate.IsTransitionActiveFor(this))
            BeginTransitionOverlay();
    }

    /// <summary>Cover the live surface with the (sliding) snapshot for the rest of the transition.</summary>
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
        // A newer transition may have started while this one was settling — keep covering if so.
        if (Animations.Animate.IsTransitionActiveFor(this)) { BeginTransitionOverlay(); return; }
        _transitionActive = false;
        _web.Visibility = Visibility.Visible;
        _snapshot.Visibility = Visibility.Collapsed;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_transitionActive) _web.Visibility = Visibility.Visible;
        _ = CaptureSnapshotAsync();
    }

    /// <summary>Grab the current rendered frame so a subsequent transition has something to slide.</summary>
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

    // ── WebView2 plumbing ─────────────────────────────────────────────────────

    private void NavigateOwn(string html)
    {
        _loadingOwnContent = true;
        _web.NavigateToString(html);
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
            if (e.InitializationException is { } ex)
                AppLog.LogError("WebView2", ex);
            return;
        }

        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;

        _web.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

        _ready = true;
        if (_pendingHtml is { } html)
        {
            _pendingHtml = null;
            NavigateOwn(html);
        }
    }

    // Allow only our own NavigateToString content (and the about:/data: load it surfaces as)
    // through. Command links fire the command callback; any other in-page link opens in the
    // user's real browser rather than hijacking this read-only panel.
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_loadingOwnContent)
        {
            _loadingOwnContent = false;
            return;
        }

        var uri = e.Uri;
        if (string.IsNullOrEmpty(uri)
            || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;

        if (PackText.TryParseCommandRunUri(uri, out var command))
        {
            if (_options?.EnableCommandRun == true)
                _options.OnCommandRun?.Invoke(command);
            return;
        }

        OpenExternally(uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    private static void OpenExternally(string uri)
    {
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* best effort — a dead link shouldn't throw into the UI */ }
    }

    /// <summary>Release the native browser host. Optional — the control is collectible once its
    /// page is discarded (the navigation journal no longer pins it); hosts may call this for
    /// prompt cleanup (e.g. a browse window closing).</summary>
    public void Dispose()
    {
        Services.ThemeService.Changed -= OnThemeChanged;
        if (_disposed) return;
        _disposed = true;
        Animations.Animate.AirspaceTransitionChanged -= OnAirspaceTransitionChanged;
        _revealTimer.Stop();
        _web.NavigationStarting -= OnNavigationStarting;
        _web.NavigationCompleted -= OnNavigationCompleted;
        _web.CoreWebView2InitializationCompleted -= OnCoreInitialized;
        if (_web.CoreWebView2 is not null)
            _web.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
        _web.Dispose();
    }
}
