using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CloudLauncher.Shared;

public static partial class PackText
{
    public const int SummaryMaxLength = 2048;
    public const int DescriptionMaxLength = 2048;

    public static string? Truncate(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }

        return null;
    }

    public static string? SummaryFromOverview(string? overviewOrDescription)
        => Truncate(FirstLine(overviewOrDescription), SummaryMaxLength);

    /// <summary>Strip HTML down to plain text (for search excerpts and plain-text fallbacks).</summary>
    public static string FormatOverview(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = html.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"(?i)<\s*br\s*/?\s*>", "\n");
        text = Regex.Replace(text, @"(?i)</\s*(p|div|h[1-6]|li|ul|ol|blockquote|section|article)\s*>", "\n\n");
        text = Regex.Replace(text, @"(?i)<\s*li[^>]*>", "- ");
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

        return string.IsNullOrWhiteSpace(text) ? "" : text;
    }

    public static bool LooksLikeHtml(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Regex.IsMatch(text, @"<\s*(p|div|h[1-6]|img|ul|ol|li|br|a)\b", RegexOptions.IgnoreCase);

    public static string? SanitizeDescriptionHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var cleaned = Regex.Replace(html, @"(?is)<\s*script[^>]*>.*?</\s*script\s*>", "");
        cleaned = Regex.Replace(cleaned, @"(?is)<\s*iframe[^>]*>.*?</\s*iframe\s*>", "");
        return cleaned.Trim();
    }

    /// <summary>
    /// Build import fields from an external catalog project.
    /// <paramref name="shortSummary"/> is the one-line tagline (search summary).
    /// <paramref name="body"/> is the full project page body (HTML or markdown).
    /// </summary>
    public static PackImportFields FieldsFromExternalProject(string? shortSummary, string? body, bool bodyIsMarkdown = false)
    {
        var summary = Truncate(string.IsNullOrWhiteSpace(shortSummary) ? null : shortSummary.Trim(), SummaryMaxLength);
        if (string.IsNullOrWhiteSpace(body))
        {
            var plain = FormatOverview(shortSummary);
            return new PackImportFields(summary, Truncate(string.IsNullOrWhiteSpace(plain) ? null : plain, DescriptionMaxLength), null, false, null);
        }

        var isMarkdown = bodyIsMarkdown || !LooksLikeHtml(body);
        string? html;
        if (isMarkdown)
            html = MarkdownLiteToHtml(body);
        else
            html = SanitizeDescriptionHtml(body);

        var excerpt = Truncate(FormatOverview(html), DescriptionMaxLength);
        return new PackImportFields(summary, excerpt, html, isMarkdown, body);
    }

    public const string CommandRunUriScheme = "cloudlauncher-cmd";

    public static bool IsMarkdownCommandHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return false;
        href = href.Trim();
        if (!href.StartsWith('/') || href.StartsWith("//", StringComparison.Ordinal))
            return false;

        // A Minecraft command looks like "/word" optionally followed by space-separated
        // args ("/give @p diamond"). Reject site-relative URLs that merely start with "/":
        // CurseForge wraps outbound links as "/linkout?remoteUrl=..." and internal links as
        // "/minecraft/mc-mods/...". Treating those as commands turns real links — and the
        // images inside them — into bogus, HTML-escaped command blocks.
        return MinecraftCommandTextRegex().IsMatch(href);
    }

    public static string NormalizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "/";
        command = command.Trim();
        return command.StartsWith('/') ? command : "/" + command;
    }

    public static string CommandRunUri(string command)
        => CommandRunUriScheme + ":" + Uri.EscapeDataString(NormalizeCommand(command));

    public static bool TryParseCommandRunUri(string? uri, out string command)
    {
        command = "";
        if (string.IsNullOrWhiteSpace(uri)) return false;

        var prefix = CommandRunUriScheme + ":";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        command = NormalizeCommand(Uri.UnescapeDataString(uri[prefix.Length..]));
        return command.Length > 1;
    }

    public static string PrepareDescriptionHtml(string? content, bool isMarkdown = false, bool runnableCommands = false)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "<p><em>No description.</em></p>";

        string html;
        if (isMarkdown || !LooksLikeHtml(content))
            html = MarkdownLiteToHtml(content);
        else
            html = SanitizeDescriptionHtml(content) ?? content;

        html = ResolveCurseForgeLinkouts(html);
        return ApplyCommandLinksToHtml(html, runnableCommands);
    }

    /// <summary>
    /// CurseForge rewrites every outbound link in a description as
    /// <c>/linkout?remoteUrl=&lt;percent-encoded URL&gt;</c>. Decode those back to the real
    /// target so the links work, open externally, and aren't mistaken for site-relative
    /// paths or commands. Modrinth ships absolute URLs already, so this is a no-op there.
    /// </summary>
    public static string ResolveCurseForgeLinkouts(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;

        return CurseForgeLinkoutRegex().Replace(html, match =>
        {
            var url = DecodeLinkoutUrl(match.Groups[2].Value);
            return url is null ? match.Value : $"href={match.Groups[1].Value}{EscapeHtmlAttribute(url)}{match.Groups[1].Value}";
        });
    }

    private static string? DecodeLinkoutUrl(string encoded)
    {
        try
        {
            var url = WebUtility.HtmlDecode(encoded);
            // remoteUrl is percent-encoded (sometimes twice). Unescape until it resolves
            // to an absolute URL or stops changing, capped so a malformed value can't loop.
            for (var i = 0; i < 3 && !IsHttpUrl(url); i++)
            {
                var next = Uri.UnescapeDataString(url);
                if (next == url) break;
                url = next;
            }
            return IsHttpUrl(url) ? url : null;
        }
        catch { return null; }
    }

    private static bool IsHttpUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static string ApplyCommandLinksToHtml(string html, bool runnableCommands = false)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;

        html = HtmlCommandAnchorRegex().Replace(html, match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (!IsMarkdownCommandHref(href)) return match.Value;

            var label = WebUtility.HtmlDecode(match.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(label))
                label = NormalizeCommand(href);

            return RenderCommandBlockHtml(label, href, runnableCommands);
        });

        // Convert any un-normalized editing blocks (mc-cmd-editing) that survived
        // into the proper viewer format — either an expandable block or a run-link.
        html = HtmlEditingCommandBlockRegex().Replace(html, match =>
        {
            var label = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            var command = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();
            return RenderCommandBlockHtml(label, command, runnableCommands);
        });

        if (!runnableCommands) return html;

        return HtmlCommandBlockRegex().Replace(html, match =>
        {
            var label = WebUtility.HtmlDecode(match.Groups[1].Value);
            var command = WebUtility.HtmlDecode(match.Groups[2].Value);
            return RenderCommandBlockHtml(label, command, runnable: true);
        });
    }

    public static string RenderCommandBlockHtml(string label, string command, bool runnable)
    {
        command = NormalizeCommand(command);
        var safeLabel = string.IsNullOrWhiteSpace(label) ? command : label;

        if (runnable)
        {
            // Hosted Minecraft window: render as a clickable link that runs the command when clicked.
            // Wrapped in <p> so it is always a block-level element in the DOM — this prevents the
            // WPF WebBrowser (IE legacy host) from silently dropping adjacent <p> text nodes when
            // the engine falls back to IE7 quirks mode (e.g. on first launch before the
            // FEATURE_BROWSER_EMULATION registry key has taken effect, or when NavigateToString
            // ignores the X-UA-Compatible meta tag for about: URLs).
            // onclick runs the command via window.external (reliable on the first
            // click even when the hosted IE control is inactive); the href is kept so
            // right-click "Open" and the window.external fallback still navigate, which
            // the WPF Navigating handler intercepts as a backup path.
            return $"""<p><a class="mc-cmd-run" href="{EscapeHtmlAttribute(CommandRunUri(command))}" data-cmd="{EscapeHtmlAttribute(command)}" title="{EscapeHtmlAttribute(command)}" onclick="return mcRunCmd(this)">{EscapeHtml(safeLabel)}</a></p>""";
        }

        // Non-hosted (main launcher viewer / saved format): keep the expandable
        // block so the user can still inspect the command from the toggle.
        return $"""
               <div class="mc-cmd">
                 <div class="mc-cmd-head">
                   <button type="button" class="mc-cmd-toggle" aria-expanded="false" onclick="toggleMcCmd(this)">&#9654;</button>
                   <span class="mc-cmd-label">{EscapeHtml(safeLabel)}</span>
                 </div>
                 <div class="mc-cmd-body" style="display:none"><code>{EscapeHtml(command)}</code></div>
               </div>
               """;
    }

    public static string MarkdownLiteToHtml(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var refs = ExtractReferenceLinks(lines);
        var sb = new StringBuilder();
        sb.Append("<div class=\"md\">");

        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i].TrimEnd();
            if (IsReferenceDefinition(line))
            {
                i++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var fence = line.Trim();
                var lang = fence.Length > 3 ? fence[3..].Trim() : "";
                i++;
                var code = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimEnd().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Append(EscapeHtml(lines[i])).Append('\n');
                    i++;
                }
                if (i < lines.Length) i++;
                sb.Append("<pre><code");
                if (!string.IsNullOrWhiteSpace(lang))
                    sb.Append(" class=\"lang-").Append(EscapeHtmlAttribute(lang)).Append('"');
                sb.Append('>').Append(code.ToString().TrimEnd('\n')).Append("</code></pre>");
                continue;
            }

            if (TryParseHeading(line, out var level, out var heading))
            {
                sb.Append("<h").Append(level).Append('>').Append(MarkdownInlineToHtml(heading, refs)).Append("</h").Append(level).Append('>');
                i++;
                continue;
            }

            if (line is "---" or "***" or "___")
            {
                sb.Append("<hr/>");
                i++;
                continue;
            }

            if (IsBlockquoteLine(line, out var quoteText))
            {
                sb.Append("<blockquote>");
                while (i < lines.Length)
                {
                    var bl = lines[i].TrimEnd();
                    if (string.IsNullOrWhiteSpace(bl))
                    {
                        i++;
                        continue;
                    }

                    if (!IsBlockquoteLine(bl, out var qt))
                        break;

                    if (!string.IsNullOrWhiteSpace(qt))
                        sb.Append("<p>").Append(MarkdownInlineToHtml(qt, refs)).Append("</p>");
                    i++;
                }
                sb.Append("</blockquote>");
                continue;
            }

            if (IsUnorderedListItem(line, out var bullet))
            {
                sb.Append("<ul>");
                while (i < lines.Length && IsUnorderedListItem(lines[i].TrimEnd(), out var item))
                {
                    sb.Append("<li>").Append(MarkdownInlineToHtml(item, refs)).Append("</li>");
                    i++;
                }
                sb.Append("</ul>");
                continue;
            }

            if (IsOrderedListItem(line, out var numbered))
            {
                sb.Append("<ol>");
                while (i < lines.Length && IsOrderedListItem(lines[i].TrimEnd(), out var item))
                {
                    sb.Append("<li>").Append(MarkdownInlineToHtml(item, refs)).Append("</li>");
                    i++;
                }
                sb.Append("</ol>");
                continue;
            }

            if (IsPlainCommandLine(line))
            {
                var cmd = NormalizeCommand(line.Trim());
                sb.Append(RenderCommandBlockHtml(cmd, cmd, runnable: false));
                i++;
                continue;
            }

            sb.Append("<p>").Append(MarkdownInlineToHtml(line, refs)).Append("</p>");
            i++;
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    public static string WrapHtmlDocument(string? bodyHtml, bool interactive = true, bool runnableCommands = false, bool legacyIe = true)
    {
        bodyHtml ??= "";
        var script = interactive
            ? """
              <script>
              function toggleMcCmd(btn) {
                var body = btn.parentElement.nextElementSibling;
                if (!body) return;
                var open = body.style.display === 'none' || body.style.display === '';
                body.style.display = open ? 'block' : 'none';
                btn.setAttribute('aria-expanded', open ? 'true' : 'false');
                btn.textContent = open ? '\u25BC' : '\u25B6';
              }
              function mcRunCmd(a) {
                try {
                  window.external.RunCommand(a.getAttribute('data-cmd') || a.href);
                  return false;
                } catch (e) {
                  // window.external unavailable: let the href navigate so the
                  // WPF Navigating handler can still run the command.
                  return true;
                }
              }
              function rewriteMcCmdBlocks() {
                var divs = document.getElementsByTagName('div');
                var blocks = [];
                for (var i = 0; i < divs.length; i++) {
                  var cls = ' ' + (divs[i].className || '') + ' ';
                  if (cls.indexOf(' mc-cmd ') !== -1) blocks.push(divs[i]);
                }
                for (var j = 0; j < blocks.length; j++) {
                  var block = blocks[j];
                  var labelEl = null, codeEl = null;
                  var nodes = block.getElementsByTagName('*');
                  for (var k = 0; k < nodes.length; k++) {
                    var nc = ' ' + (nodes[k].className || '') + ' ';
                    if (!labelEl && nc.indexOf(' mc-cmd-label ') !== -1) labelEl = nodes[k];
                    if (!codeEl && nodes[k].tagName && nodes[k].tagName.toLowerCase() === 'code') codeEl = nodes[k];
                  }
                  if (!labelEl || !codeEl) continue;
                  var label = (labelEl.innerText || labelEl.textContent || '').replace(/^\s+|\s+$/g, '');
                  var command = (codeEl.innerText || codeEl.textContent || '').replace(/^\s+|\s+$/g, '');
                  if (!command) continue;
                  if (command.charAt(0) !== '/') command = '/' + command;
                  if (!label) label = command;
                  var a = document.createElement('a');
                  a.href = 'cloudlauncher-cmd:' + encodeURIComponent(command);
                  a.className = 'mc-cmd-run';
                  a.title = command;
                  a.setAttribute('data-cmd', command);
                  a.onclick = function () { return mcRunCmd(this); };
                  a.appendChild(document.createTextNode(label));
                  var p = document.createElement('p');
                  p.appendChild(a);
                  block.parentNode.replaceChild(p, block);
                }
              }
              </script>
              """
            : "";
        var rewriteCall = runnableCommands && interactive
            ? "<script>rewriteMcCmdBlocks();</script>"
            : "";
        // Drive scrolling from inside the Trident/IE document. The WPF WebBrowser
        // hosts an HWND that swallows the mouse wheel before it can reach WPF
        // (airspace), so routed PreviewMouseWheel handlers never fire over the
        // page. Handling the wheel here scrolls <body> (the overflow:auto
        // container) directly and cancels the default to avoid double-stepping.
        var wheelScript =
            "<script>(function(){" +
            "function s(){var e=window.event||arguments[0];" +
            "var d=e.wheelDelta?e.wheelDelta:(e.detail?-e.detail*40:0);" +
            "var b=document.body,h=document.documentElement;" +
            "var t=b?b.scrollTop:0;if(b)b.scrollTop-=d;" +
            "if(b&&b.scrollTop===t&&h)h.scrollTop-=d;" +
            "if(e.preventDefault)e.preventDefault();e.returnValue=false;return false;}" +
            "if(document.addEventListener){document.addEventListener('mousewheel',s,false);" +
            "document.addEventListener('DOMMouseScroll',s,false);}" +
            "document.onmousewheel=s;" +
            "})();</script>";
        // Spoiler support. The legacy IE/Trident host has no native <details> toggle,
        // and CurseForge ships spoiler bodies with their inner HTML escaped (revealed by
        // JS on their site) — so images and links inside show up as raw &lt;img&gt; text.
        // For every <details>/.spoiler block: un-escape the body so the real tags render,
        // then wire the <summary> as a click-to-toggle (open by default so content shows).
        // &amp; is intentionally left escaped — browsers decode it fine inside href/src,
        // and touching it would corrupt real links that happen to share the block.
        var spoilerScript =
            "<script>(function(){" +
            "function ue(s){var t=s.replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&quot;/g,'\"').replace(/&#39;/g,\"'\");" +
            "return t.replace(/<(\\/?)(script|iframe|object|embed|link|meta)/gi,'&lt;$1$2');}" +
            "var r=[],i,j,d=document.getElementsByTagName('details');" +
            "for(i=0;i<d.length;i++)r.push(d[i]);" +
            "var a=document.getElementsByTagName('*');" +
            "for(j=0;j<a.length;j++){var c=' '+(a[j].className||'')+' ';if(c.indexOf('spoiler')!==-1)r.push(a[j]);}" +
            "for(i=0;i<r.length;i++){try{var h=r[i].innerHTML;if(h&&h.indexOf('&lt;')!==-1)r[i].innerHTML=ue(h);}catch(e){}}" +
            "var sm=document.getElementsByTagName('summary');" +
            "for(i=0;i<sm.length;i++){(function(s){s.onclick=function(){" +
            "var p=s.parentNode;if(!p)return;var o=p.getAttribute('data-open')!=='false';o=!o;" +
            "p.setAttribute('data-open',o?'true':'false');var ch=p.childNodes,n;" +
            "for(n=0;n<ch.length;n++){if(ch[n].nodeType===1&&ch[n].tagName&&ch[n].tagName.toLowerCase()!=='summary')ch[n].style.display=o?'':'none';}" +
            "};})(sm[i]);}" +
            "})();</script>";

        // Chromium (WebView2) path: same spoiler un-escape, but lean on the native <details>
        // toggle instead of the IE manual wiring, and reveal spoilers by default so their
        // images show. (CurseForge still ships spoiler bodies HTML-escaped, so the un-escape
        // pass is needed in both engines.)
        var spoilerScriptModern =
            "<script>(function(){" +
            "function ue(s){var t=s.replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&quot;/g,'\"').replace(/&#39;/g,\"'\");" +
            "return t.replace(/<(\\/?)(script|iframe|object|embed|link|meta)/gi,'&lt;$1$2');}" +
            "var r=[],i,j,d=document.getElementsByTagName('details');" +
            "for(i=0;i<d.length;i++)r.push(d[i]);" +
            "var a=document.getElementsByTagName('*');" +
            "for(j=0;j<a.length;j++){var c=' '+(a[j].className||'')+' ';if(c.indexOf('spoiler')!==-1)r.push(a[j]);}" +
            "for(i=0;i<r.length;i++){try{var h=r[i].innerHTML;if(h&&h.indexOf('&lt;')!==-1)r[i].innerHTML=ue(h);}catch(e){}}" +
            // Some authors' raw <img>/<a> tags arrive HTML-escaped and parse into literal text
            // nodes (rendered as '<img ...>' text) anywhere in the body — not only inside a
            // spoiler. Re-parse those text nodes; skip real <code>/<pre> so samples survive.
            "function sk(el){while(el&&el.nodeType===1){var t=el.tagName.toLowerCase();" +
            "if(t==='code'||t==='pre'||t==='textarea'||t==='script'||t==='style')return true;el=el.parentNode;}return false;}" +
            "var re=/<(?:img|a|br|hr|b|i|u|strong|em|sup|sub|span|kbd|small)\\b[^<]*>/i;" +
            "var tn=[],w=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT,null,false),x;" +
            "while(x=w.nextNode())tn.push(x);" +
            "for(i=0;i<tn.length;i++){var nd=tn[i],tx=nd.nodeValue;" +
            "if(!tx||tx.indexOf('<')===-1||!re.test(tx)||sk(nd.parentNode))continue;" +
            "var safe=tx.replace(/<(\\/?)(script|iframe|object|embed|link|meta|base)/gi,'&lt;$1$2');" +
            "var sp=document.createElement('span');sp.innerHTML=safe;nd.parentNode.replaceChild(sp,nd);}" +
            "var dd=document.getElementsByTagName('details');" +
            "for(i=0;i<dd.length;i++){try{dd[i].open=true;}catch(e){}}" +
            "})();</script>";

        // IE needs a <details> element shim to make the tag stylable; Chromium has it natively.
        // The extra body-level style gives a natively-collapsed spoiler the same ▸ marker the
        // IE data-open path uses (kept out of the shared <style> so it can't fight IE, which
        // never sets the [open] attribute).
        var detailsShim = legacyIe
            ? "<script>try{document.createElement('details');document.createElement('summary');}catch(e){}</script>"
            : "";
        var modernStyle = legacyIe
            ? ""
            : "<style>details:not([open]) > summary:before{content:\"\\25B8  \"}</style>";

        // The X-UA-Compatible meta tag must be one of the FIRST head children
        // (per MS docs) — it forces the WPF WebBrowser host into Edge/IE11 mode
        // even when the FEATURE_BROWSER_EMULATION registry tweak hasn't taken
        // effect yet for this process. Without it, IE7 quirks mode silently
        // drops <p> blocks that sit between inline anchors, which makes the
        // rich-description text vanish around mc-cmd-run links.
        return """
               <!DOCTYPE html>
               <html><head>
               <meta http-equiv="X-UA-Compatible" content="IE=edge"/>
               <meta charset="utf-8"/>
               """ + detailsShim + script + """
               <style>
               html {
                 margin: 0;
                 padding: 0;
                 height: 100%;
                 overflow: hidden;
                 background: #0B0D11;
               }
               body {
                 margin: 0;
                 padding: 0;
                 height: 100%;
                 overflow: auto;
                 background: #0B0D11;
                 color: #A8B0BF;
                 font-family: Segoe UI, sans-serif;
                 font-size: 17px;
                 line-height: 1.6;
                 overflow-wrap: break-word;
                 word-wrap: break-word;
                 scrollbar-face-color: #3A4254;
                 scrollbar-track-color: #14171F;
                 scrollbar-arrow-color: #3A4254;
                 scrollbar-shadow-color: #14171F;
                 scrollbar-highlight-color: #14171F;
                 scrollbar-3dlight-color: #14171F;
                 scrollbar-darkshadow-color: #14171F;
               }
               .content { padding: 10px 16px 20px 10px; box-sizing: border-box; max-width: 100%; }
               img { max-width: 100%; height: auto; border-radius: 10px; margin: 16px 0; display: block; }
               a { color: #5B9DF9; text-decoration: none; }
               a:hover { text-decoration: underline; }
               h1, h2, h3, h4 { color: #EEF1F7; margin: 22px 0 12px; font-weight: 600; }
               p, li { margin: 0 0 14px; }
               ul, ol { margin: 0 0 14px 22px; padding: 0; }
               strong, b { color: #EEF1F7; font-weight: 600; }
               em, i { color: #C4CAD6; }
               hr { border: none; border-top: 1px solid #2E3445; margin: 16px 0; }
               blockquote {
                 margin: 14px 0;
                 padding: 12px 16px;
                 border-left: 3px solid #3A4254;
                 background: #11141B;
                 border-radius: 0 10px 10px 0;
                 color: #A8B0BF;
               }
               blockquote p { margin: 0 0 8px; }
               blockquote p:last-child { margin-bottom: 0; }
               code {
                 font-family: "Cascadia Mono", Consolas, monospace;
                 font-size: 14px;
                 background: #181C25;
                 padding: 1px 5px;
                 border-radius: 4px;
                 color: #EEF1F7;
                 overflow-wrap: break-word;
                 word-break: break-word;
               }
               pre {
                 background: #11141B;
                 border: 1px solid #2E3445;
                 border-radius: 10px;
                 padding: 12px 14px;
                 overflow-x: auto;
                 margin: 14px 0;
               }
               pre code { background: transparent; padding: 0; }
               table { border-collapse: collapse; width: 100%; margin: 12px 0; }
               td, th { border: 1px solid #2E3445; padding: 6px 10px; }
               details {
                 border: 1px solid #2E3445;
                 border-radius: 10px;
                 background: #11141B;
                 padding: 2px 14px;
                 margin: 14px 0;
               }
               summary {
                 display: block;
                 cursor: pointer;
                 font-weight: 600;
                 color: #EEF1F7;
                 padding: 10px 0;
                 list-style: none;
               }
               summary::-webkit-details-marker { display: none; }
               details > summary:before { content: "\025BE  "; color: #5B9DF9; }
               details[data-open="false"] > summary:before { content: "\025B8  "; }
               details > *:not(summary) { margin-left: 2px; }
               .spoiler, .spoiler-content, .spoilertext { display: block; }
               .mc-cmd {
                 margin: 12px 0 18px;
                 border: 1px solid #2E3445;
                 border-radius: 10px;
                 background: #11141B;
                 overflow: hidden;
               }
               .mc-cmd-head {
                 display: flex;
                 align-items: center;
                 gap: 8px;
                 padding: 8px 12px;
               }
               .mc-cmd-toggle {
                 border: none;
                 background: transparent;
                 color: #5B9DF9;
                 cursor: pointer;
                 font-size: 13px;
                 line-height: 1;
                 padding: 2px 4px;
               }
               .mc-cmd-label {
                 color: #EEF1F7;
                 font-weight: 600;
                 text-decoration: none;
                 font-size: 15px;
               }
               a.mc-cmd-label { color: #5B9DF9; cursor: pointer; }
               a.mc-cmd-label:hover { text-decoration: underline; }
               .mc-cmd-body {
                 padding: 0 12px 10px 34px;
               }
               .mc-cmd-body code {
                 display: block;
                 white-space: pre-wrap;
                 word-break: break-word;
               }
               a.mc-cmd-run {
                 color: #5B9DF9;
                 text-decoration: underline;
                 cursor: pointer;
                 font-weight: 600;
               }
               a.mc-cmd-run:hover { color: #8BB9FF; }
               </style></head><body><div class="content">
               """ + bodyHtml + "</div>" + rewriteCall
               + (legacyIe ? wheelScript : "")
               + (legacyIe ? spoilerScript : spoilerScriptModern)
               + modernStyle + "</body></html>";
    }

    public static string WrapEditorDocument(string? bodyHtml)
    {
        bodyHtml = string.IsNullOrWhiteSpace(bodyHtml) ? "<p><br></p>" : PrepareEditorBodyHtml(bodyHtml);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head>");
        sb.Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"/>");
        sb.Append("<meta charset=\"utf-8\"/>");
        sb.Append(EditorScript);
        sb.Append(EditorStyles);
        sb.Append("</head><body><div id=\"toolbar\">");
        sb.Append("<button type=\"button\" title=\"Bold\" onclick=\"execFmt('bold')\"><b>B</b></button>");
        sb.Append("<button type=\"button\" title=\"Italic\" onclick=\"execFmt('italic')\"><i>I</i></button>");
        sb.Append("<button type=\"button\" title=\"Underline\" onclick=\"execFmt('underline')\"><u>U</u></button>");
        sb.Append("<span class=\"sep\"></span>");
        sb.Append("<button type=\"button\" title=\"Heading\" onclick=\"insertHeading(2)\">H</button>");
        sb.Append("<button type=\"button\" title=\"Bullet list\" onclick=\"execFmt('insertUnorderedList')\">&#8226;</button>");
        sb.Append("<button type=\"button\" title=\"Numbered list\" onclick=\"execFmt('insertOrderedList')\">1.</button>");
        sb.Append("<button type=\"button\" title=\"Quote\" onclick=\"execFmt('formatBlock','blockquote')\">&ldquo;</button>");
        sb.Append("<span class=\"sep\"></span>");
        sb.Append("<button type=\"button\" title=\"Insert link\" class=\"wide-btn\" onclick=\"insertLink()\">Link</button>");
        sb.Append("<button type=\"button\" class=\"wide-btn cmd-btn\" title=\"Insert command\" onclick=\"insertCommand()\">/cmd</button>");
        sb.Append("</div><div id=\"editor-wrap\"><div id=\"editor\" class=\"content\" contenteditable=\"true\" ");
        sb.Append("oninput=\"onEditorInput()\" onkeyup=\"onEditorKeyUp(event)\" onpaste=\"onEditorPaste()\">");
        sb.Append(bodyHtml);
        sb.Append("</div></div>");
        sb.Append("<div id=\"modal-overlay\" class=\"modal-hidden\" tabindex=\"-1\" onclick=\"onModalOverlayClick(event)\">");
        sb.Append("<div id=\"modal-dialog\" onclick=\"event.stopPropagation();\">");
        sb.Append("<div id=\"modal-title\"></div>");
        sb.Append("<div id=\"modal-fields\"></div>");
        sb.Append("<div id=\"modal-actions\">");
        sb.Append("<button type=\"button\" id=\"modal-cancel\" onclick=\"hideEditorModal(null)\">Cancel</button>");
        sb.Append("<button type=\"button\" id=\"modal-ok\" onclick=\"submitEditorModal()\">Insert</button>");
        sb.Append("</div></div></div></body></html>");
        return sb.ToString();
    }

    private const string EditorScript =
        "<script>" +
        "var modalCallback=null;" +
        "function notifyChanged(){try{window.external.NotifyChanged();}catch(e){}}" +
        "function getSelectedText(){if(window.getSelection){var sel=window.getSelection();if(sel&&sel.rangeCount>0)return sel.toString();}if(document.selection&&document.selection.type!=='Control'){return document.selection.createRange().text;}return '';}" +
        "function htmlEscape(value){return String(value||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\"/g,'&quot;');}" +
        "function insertHtml(html){var editor=document.getElementById('editor');editor.focus();try{if(document.execCommand('insertHTML',false,html))return true;}catch(e){}editor.innerHTML+=html;return true;}" +
        "function execFmt(cmd,value){var editor=document.getElementById('editor');editor.focus();try{document.execCommand(cmd,false,value||null);}catch(e){}notifyChanged();}" +
        "function insertHeading(level){execFmt('formatBlock','<H'+level+'>');}" +
        "function showEditorModal(title,fields,okLabel,callback){" +
        "var overlay=document.getElementById('modal-overlay');var titleEl=document.getElementById('modal-title');var fieldsEl=document.getElementById('modal-fields');var okBtn=document.getElementById('modal-ok');if(!overlay||!titleEl||!fieldsEl||!okBtn)return;" +
        "modalCallback=callback;titleEl.textContent=title||'';okBtn.textContent=okLabel||'Insert';fieldsEl.innerHTML='';" +
        "for(var i=0;i<fields.length;i++){" +
        "var field=fields[i];var wrap=document.createElement('div');wrap.className='modal-field';" +
        "var label=document.createElement('label');label.setAttribute('for','modal-field-'+i);label.textContent=field.label||'';wrap.appendChild(label);" +
        "var input=document.createElement('input');input.type='text';input.id='modal-field-'+i;input.className=field.mono?'modal-input-mono':'';" +
        "if(field.placeholder)input.placeholder=field.placeholder;input.value=field.value||'';" +
        "input.onfocus=function(){this.select();};" +
        "input.onkeydown=function(e){var key=e.keyCode||e.which;if(key===13){submitEditorModal();if(e.preventDefault)e.preventDefault();}else if(key===27){hideEditorModal(null);if(e.preventDefault)e.preventDefault();}};" +
        "wrap.appendChild(input);fieldsEl.appendChild(wrap);}" +
        "overlay.className='';applyEditorModalLayout();overlay.focus();setTimeout(function(){applyEditorModalLayout();var first=document.getElementById('modal-field-0');if(first){first.focus();first.select();}},0);}" +
        "function hideEditorModal(result){var overlay=document.getElementById('modal-overlay');if(overlay)overlay.className='modal-hidden';var cb=modalCallback;modalCallback=null;if(cb)cb(result);}" +
        "function readEditorModalValues(){var fieldsEl=document.getElementById('modal-fields');if(!fieldsEl)return null;var inputs=fieldsEl.getElementsByTagName('input');var values=[];for(var i=0;i<inputs.length;i++)values.push(inputs[i].value);return values;}" +
        "function submitEditorModal(){hideEditorModal(readEditorModalValues());}" +
        "function onModalOverlayClick(e){if(!e||e.target===document.getElementById('modal-overlay'))hideEditorModal(null);}" +
        "function onModalKeyDown(e){if(!e||!modalCallback)return;var key=e.keyCode||e.which;if(key===27){hideEditorModal(null);if(e.preventDefault)e.preventDefault();}else if(key===13){submitEditorModal();if(e.preventDefault)e.preventDefault();}}" +
        "function insertLink(){var selected=getSelectedText();showEditorModal('Insert link',[" +
        "{label:'Link text',placeholder:'Display text',value:selected||''}," +
        "{label:'URL',placeholder:'https://example.com',value:'https://'}" +
        "],'Add link',function(values){" +
        "if(!values)return;var text=(values[0]||'').replace(/^\\s+|\\s+$/g,'');var url=(values[1]||'').replace(/^\\s+|\\s+$/g,'');if(!url)return;if(!text)text=url;" +
        "insertHtml('<a href=\"'+htmlEscape(url)+'\">'+htmlEscape(text)+'</a>');notifyChanged();});}" +
        "function insertCommand(){showEditorModal('Insert command',[" +
        "{label:'Button label',placeholder:'Run command',value:'Run command'}," +
        "{label:'Minecraft command',placeholder:'give @p diamond',value:'give @p diamond',mono:true}" +
        "],'/cmd',function(values){" +
        "if(!values)return;var label=(values[0]||'').replace(/^\\s+|\\s+$/g,'');var command=(values[1]||'').replace(/^\\s+|\\s+$/g,'');if(!label||!command)return;" +
        "if(command.charAt(0)!=='/')command='/'+command;var editor=document.getElementById('editor');editor.focus();" +
        "var html='<div class=\"mc-cmd mc-cmd-editing\" contenteditable=\"false\"><div class=\"mc-cmd-head\">" +
        "<span class=\"mc-cmd-label\" contenteditable=\"true\">'+label.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')+" +
        "'</span></div><div class=\"mc-cmd-body\"><code contenteditable=\"true\">'+command.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')+" +
        "'</code></div></div><p><br></p>';insertHtml(html);notifyChanged();});}" +
        "function onEditorInput(){notifyChanged();}" +
        "function onEditorKeyUp(e){if(e&&(e.ctrlKey||e.metaKey))notifyChanged();}" +
        "function onEditorPaste(){setTimeout(notifyChanged,0);}" +
        "function initEditorModal(){var overlay=document.getElementById('modal-overlay');if(overlay)overlay.onkeydown=onModalKeyDown;}" +
        "function applyEditorLayout(){" +
        "var toolbar=document.getElementById('toolbar');var wrap=document.getElementById('editor-wrap');if(!wrap)return;" +
        "document.documentElement.style.overflow='hidden';document.body.style.overflow='hidden';" +
        "document.body.style.display='flex';document.body.style.flexDirection='column';document.body.style.height='100%';" +
        "try{document.body.style.zoom='1.35';}catch(e){}" +
        "function fixH(){try{" +
        "var tbH=toolbar?toolbar.offsetHeight:0;" +
        "var h=document.body.clientHeight-tbH;" +
        "if(h>0){wrap.style.flex='none';wrap.style.height=h+'px';}" +
        "}catch(e){}}" +
        "fixH();setTimeout(fixH,0);}" +
        "function applyEditorModalLayout(){try{var overlay=document.getElementById('modal-overlay');var dialog=document.getElementById('modal-dialog');var fields=document.getElementById('modal-fields');if(!overlay||!dialog||!fields)return;var h=document.body.clientHeight||document.documentElement.clientHeight||0;if(h<=0)return;var maxH=Math.max(220,Math.floor(h*0.88));dialog.style.maxHeight=maxH+'px';var used=dialog.offsetHeight-fields.offsetHeight;fields.style.maxHeight=Math.max(84,maxH-used-2)+'px';fields.style.overflowY='auto';}catch(e){}}" +
        "window.onload=function(){applyEditorLayout();initEditorModal();};" +
        "window.onresize=function(){applyEditorLayout();applyEditorModalLayout();};" +
        "</script>";

    private const string EditorStyles =
        "<style>" +
        ".content{padding:0}img{max-width:100%;height:auto;border-radius:10px;margin:14px 0;display:block}" +
        "a{color:#5B9DF9;text-decoration:none}a:hover{text-decoration:underline}" +
        "h1,h2,h3,h4{color:#EEF1F7;margin:20px 0 10px;font-weight:600}" +
        "p,li{margin:0 0 10px}ul,ol{margin:0 0 14px 22px;padding:0}" +
        "strong,b{color:#EEF1F7;font-weight:600}em,i{color:#C4CAD6}" +
        "hr{border:none;border-top:1px solid #2E3445;margin:16px 0}" +
        "blockquote{margin:14px 0;padding:12px 16px;border-left:3px solid #3A4254;background:#11141B;border-radius:0 10px 10px 0;color:#A8B0BF}" +
        "code{font-family:Consolas,monospace;font-size:15px;background:#181C25;padding:2px 6px;border-radius:4px;color:#EEF1F7}" +
        "pre{background:#11141B;border:1px solid #2E3445;border-radius:10px;padding:12px 14px;overflow-x:auto;margin:14px 0}" +
        "pre code{background:transparent;padding:0}" +
        ".mc-cmd:not(.mc-cmd-editing){margin:10px 0 14px;border:1px solid #2E3445;border-radius:10px;background:#11141B;overflow:hidden}" +
        ".mc-cmd-editing{margin:4px 0}" +
        ".mc-cmd-editing .mc-cmd-label{color:#5B9DF9;font-weight:600;text-decoration:underline;cursor:pointer;outline:none}" +
        ".mc-cmd-editing:hover .mc-cmd-label{color:#8BB9FF}" +
        ".mc-cmd-editing .mc-cmd-body{display:block;padding:2px 0 0 0}" +
        ".mc-cmd-editing code{display:inline;outline:none;white-space:pre-wrap;word-break:break-word;font-size:13px;color:#5C6478;background:transparent;padding:0}" +
        "html,body{margin:0;padding:0;height:100%;overflow:hidden;background:#0B0D11;color:#A8B0BF;font-family:Segoe UI,sans-serif;font-size:18px;line-height:1.6}" +
        "#toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:4px;padding:6px 10px;border-bottom:1px solid #2E3445;background:#0B0D11;flex-shrink:0;width:100%;box-sizing:border-box}" +
        "#toolbar button{border:1px solid transparent;background:transparent;color:#C4CAD6;border-radius:6px;min-width:32px;height:26px;padding:0 8px;cursor:pointer;font-size:15px;font-family:Segoe UI,sans-serif;font-weight:600;line-height:1}" +
        "#toolbar button.wide-btn{min-width:52px;padding:0 10px}" +
        "#toolbar button:hover{background:#181C25;color:#EEF1F7;border-color:#2E3445}" +
        "#toolbar .sep{width:1px;height:18px;background:#2E3445;margin:0 3px}" +
        "#toolbar .cmd-btn{color:#5B9DF9;font-weight:700}" +
        "#editor-wrap{flex:1;min-height:0;overflow:auto;overflow-x:hidden}" +
        "#editor{min-height:0;padding:16px 18px 20px;outline:none;box-sizing:border-box;font-size:18px}" +
        "#modal-overlay{position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(7,9,12,0.72);display:flex;align-items:center;justify-content:center;z-index:1000;padding:18px;box-sizing:border-box}" +
        "#modal-overlay.modal-hidden{display:none}" +
        "#modal-dialog{width:520px;max-width:96%;background:#141820;border:1px solid #2E3445;border-radius:12px;box-shadow:0 16px 40px rgba(0,0,0,0.45);padding:18px 20px 16px;font-size:15px;box-sizing:border-box;overflow:hidden}" +
        "#modal-title{color:#EEF1F7;font-size:17px;font-weight:600;margin:0 0 14px}" +
        "#modal-fields{overflow-y:auto;padding-right:2px}" +
        ".modal-field{margin:0 0 12px}" +
        ".modal-field label{display:block;color:#A8B0BF;font-size:13px;font-weight:600;margin:0 0 6px}" +
        ".modal-field input{width:100%;box-sizing:border-box;background:#0B0D11;border:1px solid #2E3445;border-radius:8px;color:#EEF1F7;font-size:15px;padding:9px 11px;font-family:Segoe UI,sans-serif;outline:none}" +
        ".modal-field input.modal-input-mono{font-family:Consolas,monospace;font-size:14px}" +
        ".modal-field input:focus{border-color:#5B9DF9;box-shadow:0 0 0 2px rgba(91,157,249,0.25)}" +
        "#modal-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:16px;padding-top:4px}" +
        "#modal-actions button{border-radius:8px;font-size:14px;font-weight:600;padding:8px 14px;cursor:pointer;font-family:Segoe UI,sans-serif;line-height:1.2}" +
        "#modal-cancel{background:transparent;border:1px solid #2E3445;color:#C4CAD6}" +
        "#modal-cancel:hover{background:#181C25;color:#EEF1F7;border-color:#3A4254}" +
        "#modal-ok{background:#5B9DF9;border:1px solid #5B9DF9;color:#0B0D11}" +
        "#modal-ok:hover{background:#7AB2FF;border-color:#7AB2FF}" +
        "</style>";

    public static string PrepareEditorBodyHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "<p><br></p>";

        html = HtmlCommandBlockRegex().Replace(html, match =>
        {
            var label = WebUtility.HtmlDecode(match.Groups[1].Value);
            var command = WebUtility.HtmlDecode(match.Groups[2].Value);
            return RenderEditingCommandBlockHtml(label, command);
        });

        html = HtmlReadOnlyCommandBlockRegex().Replace(html, match =>
        {
            var label = WebUtility.HtmlDecode(
                !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value);
            var command = WebUtility.HtmlDecode(match.Groups[3].Value);
            return RenderEditingCommandBlockHtml(label, command);
        });

        // Recovery: re-render any editing-form blocks that survived a previous save
        // (e.g. older versions saved them without converting back to the canonical form).
        // This restores the contenteditable attributes so the user can edit the label
        // and command again instead of seeing inert plain text.
        return HtmlEditingCommandBlockRegex().Replace(html, match =>
        {
            var label = WebUtility.HtmlDecode(match.Groups[1].Value);
            var command = WebUtility.HtmlDecode(match.Groups[2].Value);
            return RenderEditingCommandBlockHtml(label, command);
        });
    }

    public static string RenderEditingCommandBlockHtml(string label, string command)
    {
        command = NormalizeCommand(command);
        if (string.IsNullOrWhiteSpace(label))
            label = command;

        return "<div class=\"mc-cmd mc-cmd-editing\" contenteditable=\"false\">"
               + "<div class=\"mc-cmd-head\"><span class=\"mc-cmd-label\" contenteditable=\"true\">"
               + EscapeHtml(label)
               + "</span></div><div class=\"mc-cmd-body\"><code contenteditable=\"true\">"
               + EscapeHtml(command)
               + "</code></div></div>";
    }

    public static string NormalizeEditorHtmlForSave(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        html = HtmlEditingCommandBlockRegex().Replace(html, match =>
        {
            var label = StripInnerHtmlToText(WebUtility.HtmlDecode(match.Groups[1].Value)).Trim();
            var command = StripInnerHtmlToText(WebUtility.HtmlDecode(match.Groups[2].Value)).Trim();
            if (string.IsNullOrWhiteSpace(label))
                label = NormalizeCommand(command);
            return RenderCommandBlockHtml(label, command, runnable: false);
        });

        html = Regex.Replace(html, @"(?i)\scontenteditable\s*=\s*""[^""]*""", "");
        html = Regex.Replace(html, @"(?i)\scontenteditable\s*=\s*'[^']*'", "");
        html = Regex.Replace(html, @"(?i)\scontenteditable\s*=\s*\w+", "");
        html = Regex.Replace(html, @"(?i)\scontenteditable\b", "");
        html = SanitizeDescriptionHtml(html) ?? html;
        return html.Trim();
    }

    /// <summary>
    /// Strip any nested formatting tags that the contenteditable editor may have injected
    /// inside a command block's label or code (e.g. <c>&lt;font&gt;</c>, <c>&lt;span style&gt;</c>)
    /// so the saved label/command text round-trips cleanly.
    /// </summary>
    private static string StripInnerHtmlToText(string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return fragment;
        var withoutTags = Regex.Replace(fragment, "<[^>]+>", "");
        return withoutTags.Replace('\u00a0', ' ');
    }

    public static string PlainTextToEditorHtml(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return "<p><br></p>";

        var paragraphs = plain.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n");
        var sb = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph))
                continue;

            sb.Append("<p>");
            var lines = paragraph.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append("<br>");
                sb.Append(EscapeHtml(lines[i]));
            }
            sb.Append("</p>");
        }

        return sb.Length == 0 ? "<p><br></p>" : sb.ToString();
    }

    public static string ExcerptFromDescriptionHtml(string? html)
        => Truncate(FormatOverview(PrepareDescriptionHtml(html, isMarkdown: false)), DescriptionMaxLength) ?? "";

    /// <summary>
    /// Convert canonical description HTML back into portable markdown for export.
    /// Command blocks are written as <c>[label](/command)</c> links so they round-trip
    /// through <see cref="MarkdownLiteToHtml"/> when the file is imported again.
    /// </summary>
    public static string DescriptionHtmlToMarkdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var s = html.Replace("\r\n", "\n").Replace('\r', '\n');

        // Stash final-form fragments (commands, code) behind sentinels so the
        // structural and inline passes below leave them untouched, then restore
        // them verbatim at the very end.
        var guards = new List<string>();
        string Guard(string value)
        {
            guards.Add(value);
            return "@@CLGUARD" + (guards.Count - 1) + "@@";
        }

        // Command blocks → [label](/command). Cover canonical, legacy read-only and editing forms.
        s = HtmlCommandBlockRegex().Replace(s, m =>
            "\n\n" + Guard(CommandMarkdownLink(m.Groups[1].Value, m.Groups[2].Value)) + "\n\n");
        s = HtmlReadOnlyCommandBlockRegex().Replace(s, m =>
        {
            var label = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
            return "\n\n" + Guard(CommandMarkdownLink(label, m.Groups[3].Value)) + "\n\n";
        });
        s = HtmlEditingCommandBlockRegex().Replace(s, m =>
            "\n\n" + Guard(CommandMarkdownLink(m.Groups[1].Value, m.Groups[2].Value)) + "\n\n");

        // Fenced code blocks (decode entities, protect from the inline pass).
        s = Regex.Replace(s, @"(?is)<pre[^>]*>\s*<code[^>]*>(.*?)</code>\s*</pre>", m =>
        {
            var code = WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).TrimEnd('\n');
            return "\n\n" + Guard("```\n" + code + "\n```") + "\n\n";
        });

        // Inline code → `text`.
        s = Regex.Replace(s, @"(?is)<code[^>]*>(.*?)</code>", m =>
            Guard("`" + WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)) + "`"));

        // Images → ![alt](src) (before the link pass so the leading ! is preserved).
        s = Regex.Replace(s, @"(?is)<img\b[^>]*>", m =>
        {
            var src = AttributeValue(m.Value, "src");
            if (string.IsNullOrWhiteSpace(src)) return "";
            var alt = WebUtility.HtmlDecode(AttributeValue(m.Value, "alt"));
            return Guard($"![{alt}]({WebUtility.HtmlDecode(src)})");
        });

        // Headings.
        s = Regex.Replace(s, @"(?is)<h([1-6])[^>]*>(.*?)</h\1>", m =>
            "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " " + InlineHtmlToMarkdown(m.Groups[2].Value).Trim() + "\n\n");

        // Horizontal rule.
        s = Regex.Replace(s, @"(?is)<hr\b[^>]*?>", "\n\n---\n\n");

        // Blockquotes.
        s = Regex.Replace(s, @"(?is)<blockquote[^>]*>(.*?)</blockquote>", m =>
        {
            var inner = Regex.Replace(m.Groups[1].Value, @"(?is)</p>\s*<p[^>]*>", "\n");
            var text = InlineHtmlToMarkdown(inner).Trim();
            var quoted = string.Join("\n", text.Split('\n').Select(l => "> " + l.TrimEnd()));
            return "\n\n" + quoted + "\n\n";
        });

        // Lists.
        s = Regex.Replace(s, @"(?is)<ul[^>]*>(.*?)</ul>", m => "\n\n" + ConvertListItems(m.Groups[1].Value, ordered: false) + "\n\n");
        s = Regex.Replace(s, @"(?is)<ol[^>]*>(.*?)</ol>", m => "\n\n" + ConvertListItems(m.Groups[1].Value, ordered: true) + "\n\n");

        // Paragraphs and line breaks.
        s = Regex.Replace(s, @"(?is)<p[^>]*>(.*?)</p>", m => "\n\n" + InlineHtmlToMarkdown(m.Groups[1].Value).Trim() + "\n\n");
        s = Regex.Replace(s, @"(?is)<br\s*/?>", "\n");

        // Whatever inline markup is left over (and strip stray block tags like <div>/<span>).
        s = InlineHtmlToMarkdown(s);
        s = WebUtility.HtmlDecode(s);

        // Restore guarded fragments (their content is already final markdown).
        for (var i = guards.Count - 1; i >= 0; i--)
            s = s.Replace("@@CLGUARD" + i + "@@", guards[i]);

        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private static string CommandMarkdownLink(string labelHtml, string commandHtml)
    {
        var label = WebUtility.HtmlDecode(StripTags(labelHtml)).Trim();
        var command = NormalizeCommand(WebUtility.HtmlDecode(StripTags(commandHtml)).Trim());
        if (string.IsNullOrWhiteSpace(label)) label = command;
        return $"[{label}]({command})";
    }

    /// <summary>Convert leftover inline tags to markdown. Does not HTML-decode — the caller decodes once.</summary>
    private static string InlineHtmlToMarkdown(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var s = Regex.Replace(html, @"(?is)<(strong|b)\b[^>]*>(.*?)</\1>", m => "**" + InlineHtmlToMarkdown(m.Groups[2].Value) + "**");
        s = Regex.Replace(s, @"(?is)<(em|i)\b[^>]*>(.*?)</\1>", m => "*" + InlineHtmlToMarkdown(m.Groups[2].Value) + "*");
        s = Regex.Replace(s, @"(?is)<a\b[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>(.*?)</a>", m =>
        {
            var href = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var text = InlineHtmlToMarkdown(m.Groups[3].Value).Trim();
            if (string.IsNullOrWhiteSpace(text)) text = href;
            return $"[{text}]({href})";
        });
        return StripTags(s);
    }

    private static string ConvertListItems(string inner, bool ordered)
    {
        var sb = new StringBuilder();
        var n = 1;
        foreach (Match item in Regex.Matches(inner, @"(?is)<li[^>]*>(.*?)</li>"))
        {
            var text = Regex.Replace(InlineHtmlToMarkdown(item.Groups[1].Value).Trim(), @"\s*\n\s*", " ");
            if (text.Length == 0) continue;
            sb.Append(ordered ? $"{n++}. " : "- ").Append(text).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string StripTags(string html)
        => string.IsNullOrEmpty(html) ? "" : Regex.Replace(html, "<[^>]+>", "");

    private static string AttributeValue(string tag, string name)
    {
        var m = Regex.Match(tag, $@"(?is)\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))");
        if (!m.Success) return "";
        return m.Groups[1].Success ? m.Groups[1].Value
            : m.Groups[2].Success ? m.Groups[2].Value
            : m.Groups[3].Value;
    }

    private static Dictionary<string, string> ExtractReferenceLinks(string[] lines)
    {
        var refs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var match = ReferenceDefinitionRegex().Match(line);
            if (!match.Success) continue;
            refs[NormalizeRefKey(match.Groups[1].Value)] = match.Groups[2].Value.Trim();
        }
        return refs;
    }

    private static bool IsReferenceDefinition(string line)
        => ReferenceDefinitionRegex().IsMatch(line.Trim());

    private static bool TryParseHeading(string line, out int level, out string text)
    {
        level = 0;
        text = "";
        var i = 0;
        while (i < line.Length && line[i] == '#')
        {
            level++;
            i++;
        }

        if (level is < 1 or > 6 || i >= line.Length || line[i] != ' ')
            return false;

        text = line[(i + 1)..].Trim();
        return true;
    }

    private static bool IsBlockquoteLine(string line, out string text)
    {
        text = "";
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('>')) return false;
        text = trimmed[1..].TrimStart();
        return true;
    }

    private static bool IsUnorderedListItem(string line, out string text)
    {
        text = "";
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            text = trimmed[2..];
            return true;
        }
        if (trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            text = trimmed[2..];
            return true;
        }
        return false;
    }

    private static bool IsOrderedListItem(string line, out string text)
    {
        text = "";
        var match = OrderedListRegex().Match(line.TrimStart());
        if (!match.Success) return false;
        text = match.Groups[1].Value;
        return true;
    }

    private static string MarkdownInlineToHtml(string text, IReadOnlyDictionary<string, string> refs)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '`' && TryReadDelimited(text, i, '`', out var code, out var codeLen))
            {
                sb.Append("<code>").Append(EscapeHtml(code)).Append("</code>");
                i += codeLen;
                continue;
            }

            if (text[i] == '!' && TryReadMarkdownLink(text, i, refs, out var imageHtml, out var imageLen, image: true))
            {
                sb.Append(imageHtml);
                i += imageLen;
                continue;
            }

            if (text[i] == '[' && TryReadMarkdownLink(text, i, refs, out var linkHtml, out var linkLen, image: false))
            {
                sb.Append(linkHtml);
                i += linkLen;
                continue;
            }

            if (text[i] == '*' && TryReadEmphasis(text, i, '*', out var emph, out var emphHtml, out var emphLen))
            {
                sb.Append(emphHtml);
                i += emphLen;
                continue;
            }

            if (text[i] == '_' && TryReadEmphasis(text, i, '_', out _, out var underHtml, out var underLen))
            {
                sb.Append(underHtml);
                i += underLen;
                continue;
            }

            sb.Append(EscapeHtml(text[i].ToString()));
            i++;
        }

        return sb.ToString();
    }

    private static bool TryReadDelimited(string text, int start, char delimiter, out string content, out int length)
    {
        content = "";
        length = 0;
        if (start + 1 >= text.Length) return false;

        var end = text.IndexOf(delimiter, start + 1);
        if (end < 0) return false;

        content = text[(start + 1)..end];
        length = end - start + 1;
        return true;
    }

    private static bool TryReadMarkdownLink(
        string text,
        int start,
        IReadOnlyDictionary<string, string> refs,
        out string html,
        out int length,
        bool image)
    {
        html = "";
        length = 0;
        var prefix = image ? "!(" : "[";
        if (!text.AsSpan(start).StartsWith(prefix)) return false;

        var labelStart = start + prefix.Length;
        var labelEnd = text.IndexOf(']', labelStart);
        if (labelEnd < 0) return false;

        var label = text[labelStart..labelEnd];
        var cursor = labelEnd + 1;

        if (cursor < text.Length && text[cursor] == '(')
        {
            var urlEnd = text.IndexOf(')', cursor + 1);
            if (urlEnd < 0) return false;
            var url = text[(cursor + 1)..urlEnd].Trim();
            length = urlEnd - start + 1;
            if (!image && IsMarkdownCommandHref(url))
            {
                html = RenderCommandBlockHtml(label, url, runnable: false);
                return true;
            }

            html = image
                ? $"""<img src="{EscapeHtmlAttribute(url)}" alt="{EscapeHtml(label)}" />"""
                : $"""<a href="{EscapeHtmlAttribute(url)}">{EscapeHtml(label)}</a>""";
            return true;
        }

        if (cursor + 1 < text.Length && text[cursor] == '[')
        {
            var refEnd = text.IndexOf(']', cursor + 1);
            if (refEnd < 0) return false;
            var refId = text[(cursor + 1)..refEnd].Trim();
            if (string.IsNullOrEmpty(refId)) refId = label;
            length = refEnd - start + 1;
            if (refs.TryGetValue(NormalizeRefKey(refId), out var url))
            {
                html = $"""<a href="{EscapeHtmlAttribute(url)}">{EscapeHtml(label)}</a>""";
                return true;
            }
            html = EscapeHtml(text[start..(refEnd + 1)]);
            return true;
        }

        return false;
    }

    private static bool TryReadEmphasis(
        string text,
        int start,
        char marker,
        out string content,
        out string html,
        out int length)
    {
        content = "";
        html = "";
        length = 0;

        var isBold = start + 1 < text.Length && text[start + 1] == marker;
        var openLen = isBold ? 2 : 1;
        var searchFrom = start + openLen;
        var close = text.IndexOf(new string(marker, openLen), searchFrom, StringComparison.Ordinal);
        if (close < 0) return false;

        content = text[searchFrom..close];
        if (string.IsNullOrWhiteSpace(content)) return false;

        html = isBold
            ? "<strong>" + EscapeHtml(content) + "</strong>"
            : "<em>" + EscapeHtml(content) + "</em>";
        length = close + openLen - start;
        return true;
    }

    private static bool IsPlainCommandLine(string line)
    {
        var trimmed = line.Trim();
        return !trimmed.Contains('\n') && IsMarkdownCommandHref(trimmed);
    }

    private static string NormalizeRefKey(string key) => key.Trim().ToLowerInvariant();

    private static string EscapeHtml(string text) => WebUtility.HtmlEncode(text);

    private static string EscapeHtmlAttribute(string text) => WebUtility.HtmlEncode(text);

    [GeneratedRegex(@"^\[([^\]]+)\]:\s*(\S+)")]
    private static partial Regex ReferenceDefinitionRegex();

    // A Minecraft command: a leading slash, a command word, then optional space-separated
    // args. Deliberately does NOT match URL-ish hrefs like "/linkout?remoteUrl=..." or
    // "/minecraft/mc-mods/sodium" (a '?' or '/' immediately after the command word fails).
    [GeneratedRegex(@"^/[A-Za-z][A-Za-z0-9_]*(?:\s.*)?$")]
    private static partial Regex MinecraftCommandTextRegex();

    // CurseForge outbound-link wrapper: href="/linkout?remoteUrl=<encoded>". Group 1 is the
    // quote char, group 2 the encoded target.
    [GeneratedRegex("(?is)href\\s*=\\s*([\"'])/linkout\\?remoteUrl=(.*?)\\1")]
    private static partial Regex CurseForgeLinkoutRegex();

    [GeneratedRegex(@"^\d+\.\s+(.*)$")]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex("(?is)<a\\s+[^>]*href\\s*=\\s*\"(/[^\"]*)\"[^>]*>(.*?)</a>")]
    private static partial Regex HtmlCommandAnchorRegex();

    // Canonical saved form: outer div has class containing 'mc-cmd' (but NOT 'mc-cmd-editing'),
    // and contains a <button>, a <span class="mc-cmd-label">, a body div, and a <code>. Attribute
    // order inside any tag is irrelevant — the IE legacy WebBrowser host frequently reorders
    // attributes when serializing innerHTML, and the previous strict pattern would silently miss
    // those, causing command blocks to lose their formatting on reload (and when sharing packs).
    [GeneratedRegex(
        "(?is)" +
        "<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'](?:[^\"']*\\s)?mc-cmd(?:\\s[^\"']*)?[\"'])(?![^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-editing\\b)[^>]*>" +
        "\\s*<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-head\\b)[^>]*>" +
        "\\s*<button\\b[^>]*>.*?</button>" +
        "\\s*<span\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-label\\b)[^>]*>(.*?)</span>" +
        "\\s*</div>" +
        "\\s*<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-body\\b)[^>]*>" +
        "\\s*<code\\b[^>]*>(.*?)</code>\\s*</div>" +
        "\\s*</div>")]
    private static partial Regex HtmlCommandBlockRegex();

    // Editing form rendered into the live contenteditable editor. Has the 'mc-cmd-editing'
    // class on the outer div and no <button>.
    [GeneratedRegex(
        "(?is)" +
        "<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-editing\\b)[^>]*>" +
        "\\s*<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-head\\b)[^>]*>" +
        "\\s*<span\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-label\\b)[^>]*>(.*?)</span>" +
        "\\s*</div>" +
        "\\s*<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-body\\b)[^>]*>" +
        "\\s*<code\\b[^>]*>(.*?)</code>\\s*</div>" +
        "\\s*</div>")]
    private static partial Regex HtmlEditingCommandBlockRegex();

    // Legacy read-only form: same as canonical, but the label may be an <a> instead of a <span>
    // (older saves rendered a clickable link directly inside the head).
    [GeneratedRegex(
        "(?is)" +
        "<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'](?:[^\"']*\\s)?mc-cmd(?:\\s[^\"']*)?[\"'])(?![^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-editing\\b)[^>]*>" +
        "\\s*<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-head\\b)[^>]*>" +
        "\\s*<button\\b[^>]*>.*?</button>" +
        "\\s*(?:" +
            "<a\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-label\\b)[^>]*>(.*?)</a>" +
            "|" +
            "<span\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-label\\b)[^>]*>(.*?)</span>" +
        ")" +
        "\\s*</div>" +
        "\\s*<div\\b(?=[^>]*?\\bclass\\s*=\\s*[\"'][^\"']*\\bmc-cmd-body\\b)[^>]*>" +
        "\\s*<code\\b[^>]*>(.*?)</code>\\s*</div>" +
        "\\s*</div>")]
    private static partial Regex HtmlReadOnlyCommandBlockRegex();
}

public sealed record PackImportFields(
    string? Summary,
    string? DescriptionExcerpt,
    string? DescriptionHtml,
    bool DescriptionIsMarkdown,
    string? DescriptionSource = null);
