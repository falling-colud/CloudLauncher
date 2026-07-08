using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// </summary>
public sealed class RichDescriptionView : UserControl
{
    // A per-user writable folder is required; the default (next to the exe) is read-only
    // once the launcher is installed under Program Files.
    private static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudLauncher", "WebView2");

    private readonly WebView2 _web = new();
    private bool _initStarted;
    private bool _ready;
    private bool _loadingOwnContent;
    private string? _pendingHtml;
    private RichDescriptionOptions? _options;

    public RichDescriptionView()
    {
        // Paint the document background underneath so there's no white flash before the
        // first frame renders (matches the body colour in WrapHtmlDocument).
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x0B, 0x0D, 0x11);
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x11));
        Content = _web;

        _web.NavigationStarting += OnNavigationStarting;
        _web.CoreWebView2InitializationCompleted += OnCoreInitialized;

        // Show a blank document until the first real Show() so the panel isn't an empty void.
        _pendingHtml = PackText.WrapHtmlDocument("<p><em>No description.</em></p>", legacyIe: false);
        Loaded += (_, _) => EnsureWebViewInitialized();
    }

    /// <summary>Render a description (HTML or markdown) into the view.</summary>
    public void Show(string? content, bool isMarkdown = false, RichDescriptionOptions? options = null)
    {
        _options = options;
        var runnable = options?.EnableCommandRun == true;

        DumpDescriptionForDebug(content); // TEMP diagnostic — remove once spoiler markup is confirmed

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

        if (_ready)
            NavigateOwn(html);
        else
        {
            _pendingHtml = html;
            EnsureWebViewInitialized();
        }
    }

    // TEMP diagnostic: capture the raw description HTML so the real spoiler markup can be
    // inspected. Remove once the spoiler/image handling is confirmed.
    private static void DumpDescriptionForDebug(string? content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content) || content.Length < 24) return;
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudLauncher", "last-description.html");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        catch { /* diagnostic only */ }
    }

    private void NavigateOwn(string html)
    {
        _loadingOwnContent = true;
        _web.NavigateToString(html);
    }

    private async void EnsureWebViewInitialized()
    {
        if (_initStarted) return;
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
}
