using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace CloudLauncher.Shared;

public static partial class PackText
{
    // GFM-like features for community descriptions (tables, task lists, autolinks, ~~strike~~,
    // heading anchors) without Markdig's extras such as maths and smarty-pants, which can misfire on
    // arbitrary READMEs. Built once; the pipeline is immutable and thread-safe.
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseGridTables().UseAutoLinks().UseTaskLists()
        .UseEmphasisExtras().UseListExtras().UseAutoIdentifiers().UseFootnotes()
        .Build();

    /// <summary>
    /// Renders community Markdown to display HTML with Markdig (CommonMark/GFM). Unlike
    /// <see cref="MarkdownLiteToHtml"/>, it handles linked-image badges, tables and Markdown with
    /// embedded raw HTML (e.g. a <c>&lt;div align="center"&gt;</c> around a badge row). The result is
    /// sanitized, since Markdig passes untrusted HTML straight through, and wrapped in <c>.md</c> so the
    /// description CSS applies.
    /// </summary>
    public static string MarkdownToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "<div class=\"md\"><p><em>No description.</em></p></div>";
        var html = Markdown.ToHtml(markdown, MarkdownPipeline);
        return "<div class=\"md\">" + (SanitizeDescriptionHtml(html) ?? "") + "</div>";
    }

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

    /// <summary>True when HTML-looking content still carries raw Markdown the store never converted:
    /// an <c>![alt](url)</c> image or a <c>#</c> heading, neither of which survives in real rendered
    /// HTML. Typical of a README pasted into a store's editor.</summary>
    public static bool LooksLikeUnconvertedMarkdown(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        // Real block structure means this is genuine HTML. Without this check, one "# comment" line in
        // a code block or one literal ![alt](url) would send a good description through
        // HtmlBlocksToLineBreaks, which removes every table, list, heading and <pre>.
        && !HasStructuralHtml(text!)
        && (Regex.IsMatch(text, @"!\[[^\]]*\]\([^)]*\)")
            || Regex.IsMatch(text, @"(?m)^[ \t]{0,3}#{1,6}[ \t]"));

    /// <summary>
    /// True when the markup has block structure a Markdown reflow would destroy: the tags
    /// <see cref="HtmlBlocksToLineBreaks"/> turns into blank lines.
    /// </summary>
    private static bool HasStructuralHtml(string html) =>
        Regex.IsMatch(html, @"(?i)<\s*(table|thead|tbody|tr|td|th|pre|ul|ol|li|h[1-6]|blockquote)\b");

    /// <summary>Turns an HTML wrapper's block tags (<c>&lt;p&gt;</c>, <c>&lt;br&gt;</c>, list items...)
    /// into the blank lines Markdown needs, so pasted-but-unconverted Markdown reflows into real
    /// paragraphs. Inline tags (<c>&lt;a&gt;</c>, <c>&lt;img&gt;</c>, emphasis) are left for Markdig
    /// to pass through.</summary>
    private static string HtmlBlocksToLineBreaks(string html)
    {
        var s = Regex.Replace(html, @"(?i)<\s*br\s*/?\s*>", "\n");
        s = Regex.Replace(s, @"(?i)<\s*/?\s*(p|div|section|article|header|footer|ul|ol|li|blockquote|h[1-6]|hr|table|thead|tbody|tr|pre)\b[^>]*>", "\n\n");
        return s;
    }

    /// <summary>Strips everything that can run code or pull in another document from untrusted
    /// description HTML before it is shown in the (script-enabled) WebView2, while leaving ordinary
    /// formatting, images and links (the badges) intact.</summary>
    /// <remarks>
    /// <para>The input is read tag by tag the way a browser's tokenizer reads it, and the output is
    /// rebuilt from what was read: text has its <c>&lt;</c> escaped, kept tags are written out again
    /// with double-quoted attributes, and comments, doctypes and processing instructions are dropped.
    /// Nothing is copied through that a browser could parse differently, which a pattern-based filter
    /// can't guarantee.</para>
    /// <para>Removed: script-bearing, embedding and form elements (with their content where a browser
    /// would not show it anyway), <c>on*</c> handlers, and URLs whose scheme is not http, https, mailto,
    /// a command link or <c>data:image</c>. <c>xmp</c>, <c>textarea</c> and <c>plaintext</c> become
    /// <c>pre</c> blocks showing the same text.</para>
    /// </remarks>
    public static string? SanitizeDescriptionHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var sb = new StringBuilder(html.Length + 64);
        var n = html.Length;
        var i = 0;
        while (i < n)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                AppendHtmlText(sb, html, i, n);
                break;
            }
            AppendHtmlText(sb, html, i, lt);

            // What follows '<' decides whether this is markup, as in a browser's tokenizer.
            var next = lt + 1 < n ? html[lt + 1] : '\0';
            if (IsAsciiLetter(next) || (next == '/' && lt + 2 < n && IsAsciiLetter(html[lt + 2])))
            {
                // A tag that runs off the end of the input is dropped, which is what a browser does.
                if (!TryReadHtmlTag(html, lt, out var tag)) break;
                i = WriteSanitizedTag(sb, html, tag);
            }
            else if (next == '!')
                i = SkipMarkupDeclaration(html, lt);
            else if (next == '/' && lt + 2 < n && html[lt + 2] == '>')
                i = lt + 3;                          // "</>" is ignored
            else if (next == '?' || (next == '/' && lt + 2 < n))
                i = SkipToTagClose(html, lt);        // a bogus comment
            else
            {
                sb.Append("&lt;");
                i = lt + 1;
            }
        }
        return sb.ToString().Trim();
    }

    // ── description sanitiser internals ──────────────────────────────────────

    private sealed class HtmlTag
    {
        public string Name = "";
        public bool IsEnd;
        public bool SelfClosing;
        public int End;
        public readonly List<(string Name, string? Value)> Attributes = new();
    }

    /// <summary>Removed together with everything up to their end tag: a browser would not show that
    /// content, or would run or embed it.</summary>
    private static readonly HashSet<string> DescriptionDropWithContent = new(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "object", "embed", "applet", "noscript", "noembed", "noframes",
        "template", "title",
    };

    /// <summary>Removed as tags; whatever they wrap stays.</summary>
    private static readonly HashSet<string> DescriptionDropTag = new(StringComparer.Ordinal)
    {
        "link", "meta", "base", "form", "input", "button", "frame", "frameset", "portal", "fencedframe",
        "isindex", "keygen", "param", "bgsound", "html", "head", "body",
        "animate", "set", "animatemotion", "animatetransform", "animatecolor", "handler", "listener",
    };

    /// <summary>Elements whose content a browser shows as literal text; they become pre blocks.</summary>
    private static readonly HashSet<string> DescriptionTextElements = new(StringComparer.Ordinal)
    {
        "xmp", "textarea", "plaintext",
    };

    /// <summary>Attributes a browser loads or navigates to as a URL.</summary>
    private static readonly HashSet<string> DescriptionUrlAttributes = new(StringComparer.Ordinal)
    {
        "href", "src", "action", "formaction", "poster", "background", "cite", "data", "codebase",
        "longdesc", "lowsrc", "dynsrc", "usemap", "manifest", "icon", "profile", "archive", "classid",
        "xlink:href",
    };

    private static readonly HashSet<string> DescriptionDropAttributes = new(StringComparer.Ordinal)
    {
        "srcdoc", "ping", "download", "xml:base", "http-equiv", "attributename",
    };

    private static void AppendHtmlText(StringBuilder sb, string html, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            var c = html[i];
            if (c == '<') sb.Append("&lt;");
            else sb.Append(c);
        }
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsHtmlSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static string LowerAscii(string s)
    {
        foreach (var c in s)
            if (c is >= 'A' and <= 'Z')
                return string.Create(s.Length, s, (span, src) =>
                {
                    for (var k = 0; k < src.Length; k++)
                        span[k] = src[k] is >= 'A' and <= 'Z' ? (char)(src[k] + 32) : src[k];
                });
        return s;
    }

    /// <summary>Reads one start or end tag at <paramref name="start"/> (a '&lt;') the way the HTML
    /// tokenizer does, attributes included. False when the input ends inside the tag.</summary>
    private static bool TryReadHtmlTag(string html, int start, out HtmlTag tag)
    {
        var n = html.Length;
        var result = new HtmlTag { IsEnd = html[start + 1] == '/' };
        tag = result;
        var p = start + (result.IsEnd ? 2 : 1);
        var nameStart = p;
        while (p < n && !IsHtmlSpace(html[p]) && html[p] != '/' && html[p] != '>') p++;
        result.Name = LowerAscii(html[nameStart..p]);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (p < n)
        {
            var c = html[p];
            if (IsHtmlSpace(c)) { p++; continue; }
            if (c == '>')
            {
                result.End = p + 1;
                return true;
            }
            if (c == '/')
            {
                // A slash is self-closing only right before '>'; anywhere else it is skipped.
                if (p + 1 < n && html[p + 1] == '>')
                {
                    result.SelfClosing = true;
                    result.End = p + 2;
                    return true;
                }
                p++;
                continue;
            }

            // An attribute name takes its first character whatever it is (even '='), then runs to
            // whitespace, '/', '>' or '='.
            var nameFrom = p++;
            while (p < n && !IsHtmlSpace(html[p]) && html[p] != '/' && html[p] != '>' && html[p] != '=') p++;
            var attrName = LowerAscii(html[nameFrom..p]);

            var q = p;
            while (q < n && IsHtmlSpace(html[q])) q++;
            string? value = null;
            if (q < n && html[q] == '=')
            {
                q++;
                while (q < n && IsHtmlSpace(html[q])) q++;
                if (q >= n) return false;
                var quote = html[q];
                if (quote is '"' or '\'')
                {
                    var close = html.IndexOf(quote, q + 1);
                    if (close < 0) return false;
                    value = html[(q + 1)..close];
                    p = close + 1;
                }
                else if (quote == '>')
                {
                    value = "";
                    p = q;
                }
                else
                {
                    var valueFrom = q;
                    while (q < n && !IsHtmlSpace(html[q]) && html[q] != '>') q++;
                    value = html[valueFrom..q];
                    p = q;
                }
            }
            else
            {
                p = q;
            }

            // A repeated attribute is ignored by the browser, so it is here too.
            if (seen.Add(attrName)) result.Attributes.Add((attrName, value));
        }
        return false;
    }

    /// <summary>Writes the sanitised form of <paramref name="tag"/> and returns where reading
    /// resumes.</summary>
    private static int WriteSanitizedTag(StringBuilder sb, string html, HtmlTag tag)
    {
        var name = tag.Name;
        if (!IsPlainHtmlName(name)) return tag.End;

        if (tag.IsEnd)
        {
            if (!DescriptionDropWithContent.Contains(name) && !DescriptionDropTag.Contains(name)
                && !DescriptionTextElements.Contains(name))
                sb.Append("</").Append(name).Append('>');
            return tag.End;
        }

        if (DescriptionDropWithContent.Contains(name))
        {
            var close = FindHtmlEndTag(html, tag.End, name);
            if (close < 0) return tag.End;
            return TryReadHtmlTag(html, close, out var endTag) ? endTag.End : html.Length;
        }

        if (DescriptionTextElements.Contains(name))
        {
            int contentEnd, resume;
            var close = name == "plaintext" ? -1 : FindHtmlEndTag(html, tag.End, name);
            if (close < 0)
            {
                contentEnd = html.Length;
                resume = html.Length;
            }
            else
            {
                contentEnd = close;
                resume = TryReadHtmlTag(html, close, out var endTag) ? endTag.End : html.Length;
            }
            var content = html[tag.End..contentEnd];
            // A textarea still decodes character references; the other two show every character as typed.
            var text = name == "textarea" ? content.Replace("<", "&lt;") : content.Replace("&", "&amp;").Replace("<", "&lt;");
            sb.Append("<pre>").Append(text).Append("</pre>");
            return resume;
        }

        if (DescriptionDropTag.Contains(name)) return tag.End;

        sb.Append('<').Append(name);
        var written = 0;
        foreach (var (attrName, value) in tag.Attributes)
        {
            if (!IsAllowedDescriptionAttribute(name, attrName, value)) continue;
            sb.Append(' ').Append(attrName);
            if (value is not null) sb.Append("=\"").Append(value.Replace("\"", "&quot;")).Append('"');
            written++;
        }
        if (tag.SelfClosing) sb.Append(written > 0 ? " /" : "/");
        sb.Append('>');
        return tag.End;
    }

    /// <summary>Index of the first <c>&lt;/name</c> followed by whitespace, '/' or '&gt;' at or after
    /// <paramref name="from"/>, or -1. The same test a browser uses to end raw text.</summary>
    private static int FindHtmlEndTag(string html, int from, string name)
    {
        var at = from;
        while ((at = html.IndexOf("</", at, StringComparison.Ordinal)) >= 0)
        {
            var after = at + 2 + name.Length;
            if (after < html.Length
                && string.Compare(html, at + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (IsHtmlSpace(html[after]) || html[after] is '/' or '>'))
                return at;
            at += 2;
        }
        return -1;
    }

    /// <summary>Skips a comment, doctype or other <c>&lt;!</c> declaration.</summary>
    private static int SkipMarkupDeclaration(string html, int from)
    {
        if (string.CompareOrdinal(html, from, "<!--", 0, 4) != 0) return SkipToTagClose(html, from);
        var body = from + 4;
        if (body < html.Length && html[body] == '>') return body + 1;
        if (body + 1 < html.Length && html[body] == '-' && html[body + 1] == '>') return body + 2;
        var dash = html.IndexOf("--", body, StringComparison.Ordinal);
        while (dash >= 0)
        {
            if (dash + 2 < html.Length && html[dash + 2] == '>') return dash + 3;
            if (dash + 3 < html.Length && html[dash + 2] == '!' && html[dash + 3] == '>') return dash + 4;
            dash = html.IndexOf("--", dash + 1, StringComparison.Ordinal);
        }
        return html.Length;
    }

    private static int SkipToTagClose(string html, int from)
    {
        var close = html.IndexOf('>', from + 1);
        return close < 0 ? html.Length : close + 1;
    }

    private static bool IsPlainHtmlName(string name)
    {
        if (name.Length == 0 || !IsAsciiLetter(name[0])) return false;
        foreach (var c in name)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or ':' or '.')) return false;
        return true;
    }

    private static bool IsAllowedDescriptionAttribute(string tagName, string name, string? value)
    {
        if (name.Length == 0 || !(IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (var c in name)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or ':' or '.')) return false;
        if (name.StartsWith("on", StringComparison.Ordinal)) return false;
        if (DescriptionDropAttributes.Contains(name)) return false;
        // A named image becomes a property of document, which could shadow what the page's own
        // script calls.
        if (name == "name" && tagName is "img" or "image") return false;
        if (DescriptionUrlAttributes.Contains(name))
        {
            // A link to an image data URL is never a real link; an image source often is one.
            var navigates = name is "href" or "xlink:href" or "action" or "formaction";
            return IsSafeDescriptionUrl(DecodeCharacterReferences(value ?? ""), allowDataImage: !navigates);
        }
        if (name is "srcset" or "imagesrcset") return IsSafeSrcset(DecodeCharacterReferences(value ?? ""));
        if (name == "style") return IsSafeInlineStyle(DecodeCharacterReferences(value ?? ""));
        return true;
    }

    /// <summary>True when a (decoded) URL is relative or uses a scheme a description may carry.</summary>
    private static bool IsSafeDescriptionUrl(string url, bool allowDataImage = true)
    {
        // The part before the first ':', '/', '?' or '#', skipping the control characters and spaces a
        // browser ignores in and around a scheme.
        var prefix = new StringBuilder();
        var delimiter = '\0';
        var scanned = Math.Min(url.Length, 4096);
        for (var k = 0; k < scanned && prefix.Length <= 64; k++)
        {
            var c = url[k];
            if (c <= ' ' || c == '\u007F') continue;
            if (c is ':' or '/' or '?' or '#') { delimiter = c; break; }
            prefix.Append(c);
        }
        var text = prefix.ToString();
        // An entity this reader does not know could be hiding the colon of a scheme.
        if (text.IndexOf('&') >= 0) return false;
        if (delimiter != ':')
            // Relative, unless ignorable characters filled the whole window without reaching the end.
            return delimiter != '\0' || prefix.Length > 64 || url.Length <= 4096;
        return text.ToLowerInvariant() switch
        {
            "http" or "https" or "mailto" or CommandRunUriScheme => true,
            "data" => allowDataImage && IsDataImageUrl(url),
            _ => false
        };
    }

    private static bool IsDataImageUrl(string url)
    {
        var head = new StringBuilder(16);
        foreach (var c in url)
        {
            if (c <= ' ' || c == '\u007F') continue;
            head.Append(c);
            if (head.Length == 11) break;
        }
        return head.ToString().Equals("data:image/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeSrcset(string srcset)
    {
        foreach (var candidate in srcset.Split(','))
        {
            var url = candidate.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (url is not null && !IsSafeDescriptionUrl(url)) return false;
        }
        return true;
    }

    private static bool IsSafeInlineStyle(string css)
    {
        var s = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        // CSS escapes can spell anything, and ordinary inline styles never need them.
        if (s.IndexOf('\\') >= 0) return false;
        var squashed = new string(s.Where(c => c > ' ').ToArray()).ToLowerInvariant();
        if (squashed.Contains("expression(") || squashed.Contains("javascript:") || squashed.Contains("vbscript:")
            || squashed.Contains("behavior:") || squashed.Contains("-moz-binding") || squashed.Contains("@import"))
            return false;
        foreach (Match m in CssUrlRegex().Matches(s))
            if (!IsSafeDescriptionUrl(m.Groups[2].Value)) return false;
        return true;
    }

    /// <summary>Decodes character references the way a browser does in an attribute value, including
    /// numeric ones without a closing semicolon, so a scheme cannot hide behind them.</summary>
    private static string DecodeCharacterReferences(string raw)
    {
        if (raw.IndexOf('&') < 0) return raw;
        var numeric = NumericCharacterReferenceRegex().Replace(raw, m =>
        {
            var hex = m.Groups[1].Success;
            var digits = (hex ? m.Groups[1].Value : m.Groups[2].Value).TrimStart('0');
            if (digits.Length == 0 || digits.Length > 8) return "�";
            var code = Convert.ToInt64(digits, hex ? 16 : 10);
            if (code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF) return "�";
            return char.ConvertFromUtf32((int)code);
        });
        return WebUtility.HtmlDecode(numeric);
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
        // "/minecraft/mc-mods/...". Treating those as commands would turn real links (and the
        // images inside them) into bogus command blocks.
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

        // Read-only display renderer (mod / pack browse). Markdig handles badges, tables and
        // Markdown with embedded HTML; the HTML branch is for stores that ship HTML (CurseForge).
        // Auto-detect only when the source didn't declare a format. The description editor calls
        // MarkdownLiteToHtml directly, so its round-trip HTML is unaffected.
        string html;
        if (isMarkdown || !LooksLikeHtml(content))
            html = MarkdownToHtml(content);
        else if (LooksLikeUnconvertedMarkdown(content))
            // "HTML" that's really Markdown the author pasted into a store's editor without it being
            // converted (CF wraps a README in <p>/<br> tags, leaving ![badge](...) literal). Turn the
            // block tags back into line breaks so the Markdown structure re-emerges, then render it.
            html = MarkdownToHtml(HtmlBlocksToLineBreaks(content));
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

        // Convert any un-normalized editing blocks (mc-cmd-editing) that survived into the
        // viewer format: either an expandable block or a run-link.
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
            // Hosted Minecraft window: a link that runs the command when clicked. Wrapped in <p> so it
            // is always a block element; the legacy IE WebBrowser host can drop adjacent <p> text in
            // IE7 quirks mode. onclick runs the command via window.external (works on the first click
            // even when the IE control is inactive); the href stays so right-click "Open" still
            // navigates, and the WPF Navigating handler catches that as a fallback.
            return $"""<p><a class="mc-cmd-run" href="{EscapeHtmlAttribute(CommandRunUri(command))}" data-cmd="{EscapeHtmlAttribute(command)}" title="{EscapeHtmlAttribute(command)}" onclick="{RunCommandHandler}">{EscapeHtml(safeLabel)}</a></p>""";
        }

        // Non-hosted (main launcher viewer / saved format): keep the expandable
        // block so the user can still inspect the command from the toggle.
        return $"""
               <div class="mc-cmd">
                 <div class="mc-cmd-head">
                   <button type="button" class="mc-cmd-toggle" aria-expanded="false" onclick="{ToggleCommandHandler}">&#9654;</button>
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

    /// <summary>
    /// The colours the generated description and editor HTML is painted with.
    /// </summary>
    /// <remarks>
    /// Descriptions are a web document, so WPF theming doesn't reach them. The client sets
    /// <see cref="Current"/> from the active theme; the stylesheet below is written in the default
    /// colours and <see cref="Recolour"/> maps them across, so there is one stylesheet and the default
    /// theme costs nothing.
    /// </remarks>
    public sealed record HtmlPalette(
        string Background, string Surface, string SurfaceAlt, string ScrollTrack,
        string Border, string BorderStrong,
        string Text, string TextStrong, string TextSoft,
        string Link, string LinkHover)
    {
        /// <summary>The colours the stylesheet is written in. Replacing these with themselves is a
        /// no-op, so the default theme's output is unchanged.</summary>
        public static readonly HtmlPalette Default = new(
            Background: "#0B0D11", Surface: "#11141B", SurfaceAlt: "#181C25", ScrollTrack: "#14171F",
            Border: "#2E3445", BorderStrong: "#3A4254",
            Text: "#A8B0BF", TextStrong: "#EEF1F7", TextSoft: "#C4CAD6",
            Link: "#5B9DF9", LinkHover: "#8BB9FF");

        public static HtmlPalette Current { get; set; } = Default;

        public bool IsDefault => this == Default;
    }

    /// <summary>Maps the stylesheet's default colours onto the active palette. Case-sensitive because
    /// each of these appears in the CSS as an upper-case hex colour and nowhere else.</summary>
    private static string Recolour(string html)
    {
        var p = HtmlPalette.Current;
        if (p.IsDefault) return html;
        var d = HtmlPalette.Default;
        return html
            .Replace(d.Background, p.Background)
            .Replace(d.Surface, p.Surface)
            .Replace(d.SurfaceAlt, p.SurfaceAlt)
            .Replace(d.ScrollTrack, p.ScrollTrack)
            .Replace(d.Border, p.Border)
            .Replace(d.BorderStrong, p.BorderStrong)
            .Replace(d.Text, p.Text)
            .Replace(d.TextStrong, p.TextStrong)
            .Replace(d.TextSoft, p.TextSoft)
            .Replace(d.LinkHover, p.LinkHover)   // before Link: the two share no prefix, but order is cheap insurance
            .Replace(d.Link, p.Link);
    }

    /// <summary>The inline handlers the viewer's own command blocks use. They are allowed by hash in the
    /// page's content security policy, so the markup and the policy share these strings.</summary>
    private const string ToggleCommandHandler = "toggleMcCmd(this)";
    private const string RunCommandHandler = "return mcRunCmd(this)";

    /// <summary>Nonce for the scripts the launcher writes into its own description and editor pages.
    /// One per process, so the same description always renders to the same document.</summary>
    private static readonly string ScriptNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    /// <summary>A content security policy meta tag that lets through the launcher's nonce-carrying
    /// scripts and the given inline handlers, and nothing else that could run or leave the page.</summary>
    private static string ContentSecurityPolicyMeta(IEnumerable<string> inlineHandlers)
    {
        var hashes = inlineHandlers.Distinct(StringComparer.Ordinal)
            .Select(h => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(h))) + "'");
        var policy = "default-src 'none'; img-src http: https: data:; media-src http: https:; style-src 'unsafe-inline'; "
                     + $"script-src 'nonce-{ScriptNonce}' 'unsafe-hashes' {string.Join(' ', hashes)}; "
                     + "base-uri 'none'; form-action 'none'; frame-src 'none'; object-src 'none'";
        return $"\n<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\"/>";
    }

    public static string WrapHtmlDocument(string? bodyHtml, bool interactive = true, bool runnableCommands = false, bool legacyIe = false)
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
        // Drive scrolling from inside the Trident/IE document: the WPF WebBrowser's HWND
        // swallows the mouse wheel before WPF sees it (airspace), so PreviewMouseWheel never
        // fires over the page. This scrolls <body> (the overflow:auto container) directly and
        // cancels the default to avoid double-stepping.
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
        // Spoiler support. The legacy IE host has no native <details> toggle, and CurseForge ships
        // spoiler bodies with their inner HTML escaped (their site reveals them with JS), so images
        // and links show up as raw &lt;img&gt; text. For each <details>/.spoiler block: un-escape the
        // body, then wire the <summary> as a click-to-toggle (open by default). &amp; stays escaped:
        // browsers decode it in href/src, and un-escaping it would corrupt real links in the block.
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

        // Chromium (WebView2) path: the same spoiler un-escape, but using the native <details>
        // toggle, with spoilers open by default so their images show. Markup revived this way never
        // went through SanitizeDescriptionHtml, so put() parses it into an inert template and clean()
        // applies the same rules before it joins the page. Escaped <img>/<a> tags elsewhere in the
        // body (which parse into literal text) are re-parsed the same way; real <code>/<pre> is
        // skipped so samples survive.
        var spoilerScriptModern = """
            <script>(function(){
            function ok(u,img){var s=String(u==null?'':u).replace(/[\u0000- \u007f]+/g,'').toLowerCase(),m=/^[^:\/?#]*/.exec(s)[0];
            if(s.charAt(m.length)!==':')return true;
            if(m==='http'||m==='https'||m==='mailto'||m==='cloudlauncher-cmd')return true;
            return !!img&&s.indexOf('data:image/')===0;}
            function okSet(v){var p=String(v).split(','),i,t;
            for(i=0;i<p.length;i++){t=p[i].replace(/^\s+/,'').split(/\s+/)[0];if(t&&!ok(t,true))return false;}return true;}
            function okCss(v){var s=String(v).replace(/\/\*[\s\S]*?\*\//g,''),q,m,re=/url\(\s*(['"]?)([\s\S]*?)\1\s*\)/gi;
            if(s.indexOf('\\')>=0)return false;
            q=s.replace(/[\u0000- ]+/g,'').toLowerCase();
            if(/expression\(|javascript:|vbscript:|behavior:|-moz-binding|@import/.test(q))return false;
            while((m=re.exec(s)))if(!ok(m[2],true))return false;return true;}
            var DROP=/^(script|style|iframe|object|embed|applet|noscript|noembed|noframes|template|title|link|meta|base|form|input|button|frame|frameset|portal|fencedframe|isindex|keygen|param|bgsound|xmp|textarea|plaintext|animate|set|animatemotion|animatetransform|animatecolor|handler|listener)$/;
            var NAV=/^(href|xlink:href|action|formaction)$/;
            var RES=/^(src|poster|background|cite|data|codebase|longdesc|lowsrc|dynsrc|usemap|manifest|icon|profile|archive|classid)$/;
            var GONE=/^(srcdoc|ping|download|xml:base|http-equiv|attributename)$/;
            function clean(root){var els=root.querySelectorAll('*'),i,j,el,t,a,n,v,bad;
            for(i=els.length-1;i>=0;i--){el=els[i];t=String(el.localName||'').toLowerCase();
            if(DROP.test(t)){if(el.parentNode)el.parentNode.removeChild(el);continue;}
            for(j=el.attributes.length-1;j>=0;j--){a=el.attributes[j];n=a.name.toLowerCase();v=a.value;
            bad=n.indexOf('on')===0||GONE.test(n)||(n==='name'&&(t==='img'||t==='image'))
            ||(NAV.test(n)&&!ok(v,false))||(RES.test(n)&&!ok(v,true))
            ||((n==='srcset'||n==='imagesrcset')&&!okSet(v))||(n==='style'&&!okCss(v));
            if(bad)el.removeAttribute(a.name);}}}
            function put(el,html){var t=document.createElement('template');t.innerHTML=html;clean(t.content);
            while(el.firstChild)el.removeChild(el.firstChild);el.appendChild(t.content);}
            function ue(s){var t=s.replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&quot;/g,'"').replace(/&#39;/g,"'");
            return t.replace(/<(\/?)(script|iframe|object|embed|link|meta)/gi,'&lt;$1$2');}
            var r=[],i,j,d=document.getElementsByTagName('details');
            for(i=0;i<d.length;i++)r.push(d[i]);
            var a=document.getElementsByTagName('*');
            for(j=0;j<a.length;j++){var c=' '+(a[j].className||'')+' ';if(c.indexOf('spoiler')!==-1)r.push(a[j]);}
            for(i=0;i<r.length;i++){try{var h=r[i].innerHTML;if(h&&h.indexOf('&lt;')!==-1)put(r[i],ue(h));}catch(e){}}
            function sk(el){while(el&&el.nodeType===1){var t=el.tagName.toLowerCase();
            if(t==='code'||t==='pre'||t==='textarea'||t==='script'||t==='style')return true;el=el.parentNode;}return false;}
            var re=/<(?:img|a|br|hr|b|i|u|strong|em|sup|sub|span|kbd|small)\b[^<]*>/i;
            var tn=[],w=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT,null,false),x;
            while(x=w.nextNode())tn.push(x);
            for(i=0;i<tn.length;i++){var nd=tn[i],tx=nd.nodeValue;
            if(!tx||tx.indexOf('<')===-1||!re.test(tx)||sk(nd.parentNode))continue;
            var safe=tx.replace(/<(\/?)(script|iframe|object|embed|link|meta|base)/gi,'&lt;$1$2');
            var sp=document.createElement('span');put(sp,safe);nd.parentNode.replaceChild(sp,nd);}
            var dd=document.getElementsByTagName('details');
            for(i=0;i<dd.length;i++){try{dd[i].open=true;}catch(e){}}
            })();</script>
            """;

        // Every script the launcher writes into the page carries the per-process nonce, and the
        // content security policy allows nothing else: no other script, frame, form target or
        // connection, whatever a description contains.
        var contentSecurityPolicy = "";
        if (!legacyIe)
        {
            var open = $"<script nonce=\"{ScriptNonce}\">";
            script = script.Replace("<script>", open);
            rewriteCall = rewriteCall.Replace("<script>", open);
            spoilerScriptModern = spoilerScriptModern.Replace("<script>", open);
            contentSecurityPolicy = ContentSecurityPolicyMeta([ToggleCommandHandler, RunCommandHandler]);
        }

        // IE needs a <details> element shim to make the tag stylable; Chromium has it natively.
        // The extra body-level style gives a natively collapsed spoiler the same marker the IE
        // data-open path uses. It is kept out of the shared <style> because IE never sets [open].
        var detailsShim = legacyIe
            ? "<script>try{document.createElement('details');document.createElement('summary');}catch(e){}</script>"
            : "";
        var modernStyle = legacyIe
            ? ""
            : "<style>details:not([open]) > summary:before{content:\"\\25B8  \"}</style>";

        // The X-UA-Compatible meta tag must be one of the first head children (per MS docs). It
        // forces the WPF WebBrowser host into IE11 mode even before FEATURE_BROWSER_EMULATION takes
        // effect for this process; IE7 quirks mode drops <p> blocks between inline anchors, which
        // hides description text around mc-cmd-run links.
        return Recolour("""
               <!DOCTYPE html>
               <html><head>
               <meta http-equiv="X-UA-Compatible" content="IE=edge"/>
               <meta charset="utf-8"/>
               """ + contentSecurityPolicy + detailsShim + script + """
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
               th { background: #181C25; color: #EEF1F7; font-weight: 600; text-align: left; }
               /* Checklists written in the editor. Spans rather than <input>, because the
                  description sanitizer drops inputs - see SanitizeDescriptionHtml. */
               ul.task-list { list-style: none; margin-left: 2px; padding-left: 0; }
               ul.task-list li.task { display: flex; align-items: flex-start; gap: 9px; margin: 0 0 8px; }
               li.task .task-box {
                 flex: 0 0 auto;
                 width: 16px;
                 height: 16px;
                 margin-top: 4px;
                 border-radius: 5px;
                 border: 1.5px solid #3A4254;
                 background: #11141B;
                 position: relative;
               }
               li.task.done .task-box { background: #5B9DF9; border-color: #5B9DF9; }
               li.task.done .task-box:after {
                 content: "";
                 position: absolute;
                 left: 4px;
                 top: 1px;
                 width: 4px;
                 height: 8px;
                 border-right: 2px solid #0B0D11;
                 border-bottom: 2px solid #0B0D11;
                 transform: rotate(40deg);
               }
               li.task.done .task-text { color: #5C6478; text-decoration: line-through; }
               li.task .task-text { flex: 1; min-width: 0; }
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
               """) + bodyHtml + "</div>" + rewriteCall
               + (legacyIe ? wheelScript : "")
               + (legacyIe ? spoilerScript : spoilerScriptModern)
               + modernStyle + "</body></html>";
    }

    public static string PrepareEditorBodyHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "<p><br></p>";

        // NormalizeEditorHtmlForSave strips contenteditable from everything on the way out, so
        // a saved checklist comes back with a box the caret can be typed into and backspaced
        // through. Put the guard back, the same way command blocks get theirs back below.
        html = Regex.Replace(
            html,
            @"(?i)<span(?![^>]*\bcontenteditable\b)(?=[^>]*\bclass\s*=\s*""[^""]*\btask-box\b)",
            "<span contenteditable=\"false\"");

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

        // Recovery: re-render editing-form blocks left in saved HTML (older versions saved them
        // without converting back to the canonical form), restoring contenteditable so the label
        // and command can be edited again.
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

        // Command blocks -> [label](/command). Cover canonical, legacy read-only and editing forms.
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

        // Inline code -> `text`.
        s = Regex.Replace(s, @"(?is)<code[^>]*>(.*?)</code>", m =>
            Guard("`" + WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)) + "`"));

        // Images -> ![alt](src) (before the link pass so the leading ! is preserved).
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

    /// <summary>Convert leftover inline tags to markdown. Does not HTML-decode; the caller decodes
    /// once.</summary>
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
        // Image syntax is ![alt](url), so an image starts with "![", not "!(".
        var prefix = image ? "![" : "[";
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

    // &#106; &#x6A; and the same without the semicolon, which browsers still decode.
    [GeneratedRegex(@"&#(?:[xX]([0-9a-fA-F]+)|([0-9]+));?")]
    private static partial Regex NumericCharacterReferenceRegex();

    // url(...) in an inline style. Group 2 is the target, quotes removed.
    [GeneratedRegex(@"url\(\s*(['""]?)(.*?)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CssUrlRegex();

    // A Minecraft command: a leading slash, a command word, then optional space-separated
    // args. Doesn't match URL-like hrefs such as "/linkout?remoteUrl=..." or
    // "/minecraft/mc-mods/sodium" (a '?' or '/' right after the command word fails).
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

    // Canonical saved form: outer div with a class containing 'mc-cmd' (but not 'mc-cmd-editing'),
    // containing a <button>, a <span class="mc-cmd-label">, a body div and a <code>. Attribute
    // order inside a tag doesn't matter: the legacy IE WebBrowser host reorders attributes when
    // serializing innerHTML.
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
