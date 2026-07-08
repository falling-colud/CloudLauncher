using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public static class DescriptionEditorHelper
{
    private static readonly ConditionalWeakTable<WebBrowser, DescriptionEditorScriptHost> ScriptHosts = new();
    private static readonly ConditionalWeakTable<WebBrowser, Action?> ChangeHandlers = new();

    public static void ShowEditor(
        WebBrowser browser,
        string? content,
        bool isMarkdown,
        Action? onChanged = null)
    {
        RichDescriptionHelper.EnsureConfiguredPublic(browser);
        ChangeHandlers.Remove(browser);
        if (onChanged is not null)
            ChangeHandlers.Add(browser, onChanged);

        var bodyHtml = BuildInitialBodyHtml(content, isMarkdown);
        var host = new DescriptionEditorScriptHost(() =>
        {
            if (ChangeHandlers.TryGetValue(browser, out var handler))
                handler?.Invoke();
        });
        ScriptHosts.Remove(browser);
        ScriptHosts.Add(browser, host);
        browser.ObjectForScripting = host;
        browser.NavigateToString(PackText.WrapEditorDocument(bodyHtml));
    }

    public static void ShowViewer(
        WebBrowser browser,
        string? content,
        bool isMarkdown,
        RichDescriptionOptions? options = null)
    {
        browser.ObjectForScripting = null;
        ScriptHosts.Remove(browser);
        ChangeHandlers.Remove(browser);
        RichDescriptionHelper.Show(browser, content, isMarkdown, options);
    }

    public static bool TryGetEditorHtml(WebBrowser browser, out string html)
    {
        html = "";
        try
        {
            dynamic doc = browser.Document;
            if (doc is null) return false;

            dynamic editor = doc.getElementById("editor");
            if (editor is null) return false;

            html = PackText.NormalizeEditorHtmlForSave((string)editor.innerHTML);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void FlushPendingChanges(WebBrowser browser, Action? onChanged)
    {
        if (!TryGetEditorHtml(browser, out _))
            return;

        onChanged?.Invoke();
    }

    private static string BuildInitialBodyHtml(string? content, bool isMarkdown)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "<p><br></p>";

        if (isMarkdown || !PackText.LooksLikeHtml(content))
            return PackText.PrepareEditorBodyHtml(PackText.MarkdownLiteToHtml(content));

        return PackText.PrepareEditorBodyHtml(PackText.SanitizeDescriptionHtml(content) ?? content);
    }
}
