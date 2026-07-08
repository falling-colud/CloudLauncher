using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public static class RichDescriptionHelper
{
    private static readonly ConditionalWeakTable<WebBrowser, object> Configured = new();
    private static readonly ConditionalWeakTable<FrameworkElement, WebBrowser> HostBrowsers = new();
    private static readonly ConditionalWeakTable<WebBrowser, RichDescriptionOptions> BrowserOptions = new();
    private static readonly ConditionalWeakTable<WebBrowser, DescriptionCommandScriptHost> CommandHosts = new();

    public static void Show(WebBrowser browser, string? content, bool isMarkdown = false, RichDescriptionOptions? options = null)
    {
        EnsureConfigured(browser);
        BrowserOptions.Remove(browser);
        if (options is not null)
            BrowserOptions.Add(browser, options);

        AttachCommandScriptHost(browser, options);

        if (string.IsNullOrWhiteSpace(content))
        {
            browser.NavigateToString(PackText.WrapHtmlDocument("<p><em>No description.</em></p>"));
            return;
        }

        var runnable = options?.EnableCommandRun == true;
        var html = PackText.PrepareDescriptionHtml(content, isMarkdown, runnable);
        browser.NavigateToString(PackText.WrapHtmlDocument(html, runnableCommands: runnable));
    }

    /// <summary>
    /// Route mouse-wheel input through the host because the WebBrowser HWND swallows it.
    /// </summary>
    public static void AttachHost(FrameworkElement host, WebBrowser browser)
    {
        EnsureConfigured(browser);
        HostBrowsers.Remove(host);
        HostBrowsers.Add(host, browser);
        host.PreviewMouseWheel -= OnHostPreviewMouseWheel;
        host.PreviewMouseWheel += OnHostPreviewMouseWheel;
    }

    public static bool TryScroll(WebBrowser browser, int delta) => ScrollBrowser(browser, delta);

    private static void OnHostPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement host) return;
        if (!HostBrowsers.TryGetValue(host, out var browser)) return;
        if (host.Visibility != Visibility.Visible || browser.Visibility != Visibility.Visible) return;
        if (!ScrollBrowser(browser, e.Delta)) return;
        e.Handled = true;
    }

    internal static void EnsureConfiguredPublic(WebBrowser browser) => EnsureConfigured(browser);

    /// <summary>
    /// Expose <c>window.external.RunCommand</c> when command links are runnable so a
    /// DOM <c>onclick</c> can invoke the command directly (reliable on the first click),
    /// instead of depending on the WebBrowser's swallow-prone default link navigation.
    /// Must be set before navigation so the page sees it on load.
    /// </summary>
    private static void AttachCommandScriptHost(WebBrowser browser, RichDescriptionOptions? options)
    {
        CommandHosts.Remove(browser);

        if (options?.EnableCommandRun == true && options.OnCommandRun is { } onCommandRun)
        {
            var host = new DescriptionCommandScriptHost(command =>
            {
                var normalized = PackText.NormalizeCommand(command);
                if (normalized.Length > 1)
                    onCommandRun(normalized);
            });
            CommandHosts.Add(browser, host);
            browser.ObjectForScripting = host;
        }
        else
        {
            browser.ObjectForScripting = null;
        }
    }

    private static void EnsureConfigured(WebBrowser browser)
    {
        if (Configured.TryGetValue(browser, out _)) return;
        browser.LoadCompleted += (_, _) =>
        {
            SuppressScriptErrors(browser);
            ConfigureViewport(browser);
        };
        browser.Navigating += OnBrowserNavigating;
        Configured.Add(browser, browser);
    }

    private static void SuppressScriptErrors(WebBrowser browser)
    {
        try
        {
            var activeX = browser.GetType().InvokeMember(
                "ActiveXInstance",
                BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                browser,
                null);
            if (activeX is null) return;

            activeX.GetType().InvokeMember(
                "Silent",
                BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.Public,
                null,
                activeX,
                [true]);
        }
        catch
        {
            /* COM interop */
        }
    }

    private static void OnBrowserNavigating(object sender, System.Windows.Navigation.NavigatingCancelEventArgs e)
    {
        if (sender is not WebBrowser browser) return;

        var target = e.Uri?.OriginalString;

        // Command links: cancel the navigation and run the command instead.
        if (PackText.TryParseCommandRunUri(target, out var command))
        {
            e.Cancel = true;
            if (BrowserOptions.TryGetValue(browser, out var options)
                && options.EnableCommandRun
                && options.OnCommandRun is not null)
            {
                options.OnCommandRun(command);
            }
            return;
        }

        // Allow only the in-place content load (NavigateToString surfaces a null/"about:" URI).
        // Any other navigation is the embedded IE control trying to follow a real link — e.g. a
        // maven/mod URL embedded in a pack description. Previously this fell through WITHOUT
        // cancelling, so the control handed the URL to the system default browser, popping a
        // browser window open unexpectedly (notably while the detail page is on screen as the
        // game launches). Block it so the description view never navigates away or spawns a
        // browser. (Deliberate link-opening is offered separately via the "Open link" menu.)
        if (string.IsNullOrEmpty(target) || target.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
    }

    private static void ConfigureViewport(WebBrowser browser)
    {
        browser.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                dynamic doc = browser.Document;
                if (doc?.body is null) return;

                var isEditor = doc.getElementById("editor-wrap") != null;
                if (isEditor)
                {
                    doc.documentElement.style.overflow = "hidden";
                    doc.body.style.overflow = "hidden";
                    doc.body.style.margin = "0";
                    doc.body.style.padding = "0";
                    doc.body.style.height = "100%";
                    try { doc.body.style.zoom = 1.35; } catch { /* IE */ }
                    return;
                }

                doc.documentElement.style.overflow = "hidden";
                doc.body.style.overflow = "auto";
                doc.body.style.margin = "0";
                doc.body.style.padding = "0";
                doc.body.style.height = "100%";
                // No CSS zoom: DPI scaling is handled correctly by the FEATURE_96DPI_PIXEL
                // feature-control flag set at startup (see App.EnableModernIeRendering), so the
                // content already renders at the right physical size on scaled displays.
                // Any zoom > 1 here would also overflow the width (<html> is overflow:hidden).
                try { doc.body.style.zoom = 1.0; } catch { /* IE */ }
            }
            catch
            {
                /* COM interop */
            }
        }, DispatcherPriority.Loaded);
    }

    private static bool ScrollBrowser(WebBrowser browser, int delta)
    {
        try
        {
            dynamic doc = browser.Document;
            if (doc?.documentElement is null) return false;

            if (TryScrollOpenEditorModal(doc, delta))
                return true;

            // The editor scrolls #editor-wrap; the read-only description scrolls
            // <body> (it has overflow:auto while <html> is overflow:hidden, so
            // scrolling documentElement is a no-op).
            dynamic scrollHost = doc.getElementById("editor-wrap");
            if (scrollHost is null)
                scrollHost = doc.body;
            if (scrollHost is null)
                scrollHost = doc.documentElement;
            if (scrollHost is null) return false;

            return ScrollElement(scrollHost, delta, swallowWhenNoScroll: false);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryScrollOpenEditorModal(dynamic doc, int delta)
    {
        try
        {
            dynamic overlay = doc.getElementById("modal-overlay");
            if (overlay is null) return false;

            var className = Convert.ToString(overlay.className) ?? "";
            if (className.Contains("modal-hidden", StringComparison.Ordinal)) return false;

            dynamic modalFields = doc.getElementById("modal-fields");
            if (modalFields is null) return true;

            return ScrollElement(modalFields, delta, swallowWhenNoScroll: true);
        }
        catch
        {
            return false;
        }
    }

    private static bool ScrollElement(dynamic scrollHost, int delta, bool swallowWhenNoScroll)
    {
        var scrollTop = (double)scrollHost.scrollTop;
        var maxScroll = (double)scrollHost.scrollHeight - (double)scrollHost.clientHeight;
        if (maxScroll <= 0) return swallowWhenNoScroll;

        var next = scrollTop - delta / 3.0;
        if (next < 0) next = 0;
        if (next > maxScroll) next = maxScroll;
        scrollHost.scrollTop = next;
        return true;
    }
}
