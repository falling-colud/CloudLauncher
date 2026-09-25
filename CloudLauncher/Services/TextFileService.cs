using System.IO;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>
/// Reading and writing the text files inside a pack (configs, KubeJS scripts, server lists) for the
/// built-in editor.
/// </summary>
/// <remarks>
/// Files must round-trip byte for byte apart from the edit: same encoding, BOM and line endings.
/// A Forge <c>.toml</c> written back with a BOM fails to parse, and changed line endings make the
/// next sync diff every line. So both are detected on read and replayed on write.
/// </remarks>
public static class TextFileService
{
    /// <summary>Larger files aren't opened in the editor. They are generated data (recipe dumps,
    /// world-gen caches), and a WPF TextBox would hang on them.</summary>
    public const long MaxEditableBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Extensions the editor offers by default: the usual config formats plus scripts (KubeJS
    /// <c>.js</c>, CraftTweaker <c>.zs</c>) and <c>.snbt</c>.
    /// </summary>
    public static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".cfg", ".conf", ".config", ".toml", ".json", ".json5", ".jsonc", ".yaml", ".yml",
        ".properties", ".ini", ".snbt", ".nbt5", ".js", ".mjs", ".ts", ".zs", ".lua", ".py",
        ".md", ".markdown", ".xml", ".html", ".css", ".csv", ".tsv", ".log", ".sh", ".bat", ".cmd",
        ".mcmeta", ".mcfunction", ".lang", ".env", ".gitignore", ".sk"
    };

    /// <summary>Folders worth showing first in a pack, where hand-editing usually happens.</summary>
    public static readonly string[] PreferredFolders =
        ["config", "kubejs", "defaultconfigs", "scripts", "global_packs", "openloader", "local"];

    /// <summary>Files the editor never offers: jars, images, archives and world data.</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jar", ".zip", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".dat", ".dat_old", ".mca",
        ".mcr", ".nbt", ".exe", ".dll", ".so", ".bin", ".pak", ".ogg", ".wav", ".mp3", ".ttf", ".otf",
        ".pdf", ".db", ".lock", ".class"
    };

    public static bool LooksEditable(string path)
    {
        var ext = Path.GetExtension(path);
        if (BinaryExtensions.Contains(ext)) return false;
        if (EditableExtensions.Contains(ext)) return true;
        // No extension at all (e.g. "servers", "banned-ips") is usually text; anything else we do not
        // recognise is offered only when the user asks to see all files.
        return string.IsNullOrEmpty(ext);
    }

    public static bool IsKnownBinary(string path) => BinaryExtensions.Contains(Path.GetExtension(path));

    /// <summary>A file's contents plus what it takes to write it back unchanged.</summary>
    public sealed record TextFile(string Text, Encoding Encoding, bool HasBom, string Newline, DateTime WrittenAtUtc);

    /// <summary>
    /// Reads <paramref name="path"/>, detecting its encoding, whether it carries a BOM, and which
    /// line ending it uses. Throws for a file that is too large or contains NUL bytes (not text,
    /// whatever the extension).
    /// </summary>
    public static TextFile Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxEditableBytes)
            throw new InvalidOperationException(
                $"{info.Name} is {info.Length / 1024 / 1024} MB - too large to edit here. Open it in an external editor.");

        var bytes = File.ReadAllBytes(path);
        if (LooksBinary(bytes))
            throw new InvalidOperationException($"{info.Name} is not a text file.");

        var (encoding, hasBom, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new TextFile(text, encoding, hasBom, DetectNewline(text), info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Writes <paramref name="text"/> back in the file's original encoding and line ending, via a
    /// temp file in the same folder so an interrupted save can't leave a half-written config.
    /// </summary>
    public static DateTime Write(string path, string text, TextFile original)
    {
        var normalized = NormalizeNewlines(text, original.Newline);
        // GetBytes never emits a BOM, so the preamble is added only when the file had one.
        var body = original.Encoding.GetBytes(normalized);
        var bytes = original.HasBom
            ? [.. original.Encoding.GetPreamble(), .. body]
            : body;

        var tmp = path + ".cl-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return File.GetLastWriteTimeUtc(path);
    }

    /// <summary>True when the file on disk has changed since it was read into the editor.</summary>
    public static bool ChangedOnDisk(string path, TextFile original)
    {
        try { return File.GetLastWriteTimeUtc(path) != original.WrittenAtUtc; }
        catch { return false; }
    }

    public static string NormalizeNewlines(string text, string newline)
    {
        // The editor hands back CRLF whatever came in, so go via LF and then out to the real ending.
        var lf = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return newline == "\n" ? lf : lf.Replace("\n", newline);
    }

    private static string DetectNewline(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }
        // A file with no line breaks at all is written the way Windows tools expect.
        return lf > crlf ? "\n" : "\r\n";
    }

    private static (Encoding Encoding, bool HasBom, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true), true, 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, true), true, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, true), true, 2);
        return (new UTF8Encoding(false), false, 0);
    }

    /// <summary>A NUL byte in the first few KB means this is not text, whatever the extension says.</summary>
    private static bool LooksBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < limit; i++)
            if (bytes[i] == 0) return true;
        return false;
    }

    // ── "why can I not edit this?" ───────────────────────────────────────────

    /// <summary>
    /// Null when <paramref name="path"/> can be opened in the editor; otherwise one sentence a person
    /// can act on.
    /// </summary>
    /// <remarks>
    /// Lets the editor refuse a file before opening a tab, where <see cref="Read"/> would throw.
    /// Checks run cheapest first: extension, length, then a 4 KB NUL sniff.
    /// </remarks>
    public static string? WhyNotEditable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "This file is not on disk any more.";

            if (IsKnownBinary(path))
            {
                var ext = info.Extension.TrimStart('.').ToUpperInvariant();
                return ext.Length == 0
                    ? "This is not a text file."
                    : $"{ext} files are not text - there is nothing here an editor could show you.";
            }

            if (info.Length > MaxEditableBytes)
                return $"This file is {FormatBytes(info.Length)} - larger than the " +
                       $"{MaxEditableBytes / 1024 / 1024} MB the editor will open. Use an external editor for it.";

            using var stream = info.OpenRead();
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
                if (buffer[i] == 0)
                    return "This file holds binary data, not text - its extension is misleading.";

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Log("editor", $"Could not inspect {path}: {ex.Message}");
            return "This file could not be read.";
        }
    }

    public static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.#} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes} bytes";

    // ── syntax ───────────────────────────────────────────────────────────────

    /// <summary>The formats the editor knows enough about to colour and to label.</summary>
    /// <remarks>Display only; it doesn't affect how a file is read or written.</remarks>
    public enum TextSyntax
    {
        PlainText, Json, Toml, Properties, JavaScript, Lua, Python, Snbt, Xml, Yaml, Markdown
    }

    public static TextSyntax DetectSyntax(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" or ".json5" or ".jsonc" or ".mcmeta" => TextSyntax.Json,
        ".toml" => TextSyntax.Toml,
        ".properties" or ".cfg" or ".conf" or ".config" or ".ini" or ".lang" or ".env" => TextSyntax.Properties,
        ".js" or ".mjs" or ".ts" or ".zs" or ".sk" => TextSyntax.JavaScript,
        ".lua" => TextSyntax.Lua,
        ".py" => TextSyntax.Python,
        ".snbt" or ".nbt5" => TextSyntax.Snbt,
        ".xml" or ".html" => TextSyntax.Xml,
        ".yaml" or ".yml" => TextSyntax.Yaml,
        ".md" or ".markdown" => TextSyntax.Markdown,
        _ => TextSyntax.PlainText
    };

    public static string SyntaxLabel(TextSyntax syntax) => syntax switch
    {
        TextSyntax.Json => "JSON",
        TextSyntax.Toml => "TOML",
        TextSyntax.Properties => "key = value",
        TextSyntax.JavaScript => "JavaScript",
        TextSyntax.Lua => "Lua",
        TextSyntax.Python => "Python",
        TextSyntax.Snbt => "SNBT",
        TextSyntax.Xml => "XML",
        TextSyntax.Yaml => "YAML",
        TextSyntax.Markdown => "Markdown",
        _ => "Plain text"
    };

    /// <summary>Formats worth colouring. Plain text gets the ordinary foreground and no overlay.</summary>
    public static bool IsHighlightable(TextSyntax syntax) => syntax != TextSyntax.PlainText;

    /// <summary>True for formats whose comments can span lines, the only case where a caller needs
    /// to look beyond the lines it draws.</summary>
    public static bool HasBlockComments(TextSyntax syntax) => ProfileFor(syntax).BlockOpen is not null;

    public enum TokenKind { Text, Comment, String, Number, Keyword, Key, Punctuation }

    /// <summary>A coloured span within one line. Spans are contiguous and cover the whole line.</summary>
    public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

    /// <summary>
    /// Splits one line into coloured spans, carrying block-comment state in and out.
    /// </summary>
    /// <remarks>
    /// Works a line at a time because the editor only draws the lines on screen, and parsing a huge
    /// recipe dump whole would be expensive. It is only a lexer: it validates nothing (see
    /// <see cref="DescribeJsonError"/>), and the worst it can do is paint a line the wrong colour.
    /// </remarks>
    public static void TokenizeLine(TextSyntax syntax, string line, ref bool inBlockComment, List<SyntaxToken> tokens)
    {
        tokens.Clear();
        var block = inBlockComment;
        try
        {
            if (line.Length == 0) return;
            var p = ProfileFor(syntax);
            var pending = 0;
            var i = 0;
            var separatorSeen = false;

            void Text(int upTo)
            {
                if (upTo > pending) tokens.Add(new SyntaxToken(pending, upTo - pending, TokenKind.Text));
            }

            void Take(int start, int length, TokenKind kind)
            {
                Text(start);
                tokens.Add(new SyntaxToken(start, length, kind));
                pending = start + length;
            }

            // A markdown heading is the whole line, and so is a [section] header in TOML or an .ini.
            var firstNonSpace = 0;
            while (firstNonSpace < line.Length && char.IsWhiteSpace(line[firstNonSpace])) firstNonSpace++;
            if (!block && firstNonSpace < line.Length)
            {
                if (p.HashHeadings && line[firstNonSpace] == '#')
                {
                    tokens.Add(new SyntaxToken(0, line.Length, TokenKind.Keyword));
                    return;
                }
                if (p.SectionHeaders && line[firstNonSpace] == '[')
                {
                    var close = line.IndexOf(']', firstNonSpace);
                    if (close > firstNonSpace)
                    {
                        Take(firstNonSpace, close - firstNonSpace + 1, TokenKind.Keyword);
                        i = pending;
                    }
                }
            }

            while (i < line.Length)
            {
                if (block)
                {
                    var close = p.BlockClose is null ? -1 : line.IndexOf(p.BlockClose, i, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        Take(i, line.Length - i, TokenKind.Comment);
                        i = line.Length;
                        break;
                    }
                    Take(i, close - i + p.BlockClose!.Length, TokenKind.Comment);
                    i = pending;
                    block = false;
                    continue;
                }

                var c = line[i];

                // Block opener first: Lua's --[[ starts with its own line-comment marker, so testing
                // line comments first would swallow the opener and end the comment at the newline.
                if (p.BlockOpen is not null && MatchesAt(line, i, p.BlockOpen))
                {
                    block = true;   // consumed by the block branch, which starts its search here
                    continue;
                }

                if (StartsWithAny(line, i, p.LineComments))
                {
                    Take(i, line.Length - i, TokenKind.Comment);
                    i = line.Length;
                    break;
                }

                if (Array.IndexOf(p.Quotes, c) >= 0)
                {
                    var end = i + 1;
                    while (end < line.Length)
                    {
                        if (p.Escapes && line[end] == '\\') { end += 2; continue; }
                        if (line[end] == c) { end++; break; }
                        end++;
                    }
                    end = Math.Min(end, line.Length);
                    var quotedIsKey = KeyAllowed(p.Keys, separatorSeen) &&
                                      NextNonSpaceIsSeparator(line, end, p.KeySeparators);
                    Take(i, end - i, quotedIsKey ? TokenKind.Key : TokenKind.String);
                    i = pending;
                    continue;
                }

                var digitStart = char.IsDigit(c) || (c == '-' && i + 1 < line.Length && char.IsDigit(line[i + 1]));
                if (digitStart && (i == 0 || !IsWordChar(line[i - 1])))
                {
                    var end = i + (c == '-' ? 1 : 0);
                    while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '.')) end++;
                    Take(i, end - i, TokenKind.Number);
                    i = pending;
                    continue;
                }

                if (IsWordStart(c))
                {
                    var end = i;
                    while (end < line.Length && (IsWordChar(line[end]) || line[end] is '.' or '-')) end++;
                    var word = line.Substring(i, end - i);

                    var afterTagOpen = p.TagNames && i > 0 && (line[i - 1] == '<' || line[i - 1] == '/');
                    if (afterTagOpen)
                    {
                        Take(i, end - i, TokenKind.Keyword);
                    }
                    else if (p.Keywords.Contains(word))
                    {
                        Take(i, end - i, TokenKind.Keyword);
                    }
                    else if (KeyAllowed(p.Keys, separatorSeen) && NextNonSpaceIsSeparator(line, end, p.KeySeparators))
                    {
                        Take(i, end - i, TokenKind.Key);
                    }
                    i = Math.Max(end, pending);
                    continue;
                }

                if (Array.IndexOf(p.KeySeparators, c) >= 0) separatorSeen = true;

                if (p.Punctuation.IndexOf(c) >= 0)
                {
                    Take(i, 1, TokenKind.Punctuation);
                    i = pending;
                    continue;
                }

                i++;
            }

            Text(line.Length);
        }
        catch (Exception)
        {
            // Colour is never worth an exception: fall back to one undifferentiated span.
            tokens.Clear();
            if (line.Length > 0) tokens.Add(new SyntaxToken(0, line.Length, TokenKind.Text));
        }
        finally { inBlockComment = block; }
    }

    /// <summary>
    /// Null when the text parses as JSON; otherwise "Line 12, column 5 - ...".
    /// </summary>
    /// <remarks>A config with a missing comma stops the game from starting, and Minecraft's error names
    /// a class, not a line. Trailing commas and comments are allowed because .json5/.jsonc files use
    /// them.</remarks>
    public static string? DescribeJsonError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                MaxDepth = 256
            });
            return null;
        }
        catch (System.Text.Json.JsonException ex)
        {
            var where = ex.LineNumber is { } line
                ? $"Line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}"
                : "Somewhere in this file";
            var message = ex.Message;
            // Drop the framework's "LineNumber: 4 | BytePositionInLine: 8." suffix; we already said that.
            var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            if (cut > 0) message = message[..cut];
            return $"{where} - {message}";
        }
        catch (Exception) { return null; }
    }

    // ── tokenizer plumbing ───────────────────────────────────────────────────

    private enum KeyScope { None, LineStart, Anywhere }

    private sealed record SyntaxProfile(
        string[] LineComments,
        string? BlockOpen,
        string? BlockClose,
        char[] Quotes,
        bool Escapes,
        HashSet<string> Keywords,
        char[] KeySeparators,
        KeyScope Keys,
        string Punctuation,
        bool SectionHeaders = false,
        bool TagNames = false,
        bool HashHeadings = false);

    private static readonly string[] NoStrings = [];
    private static readonly char[] NoChars = [];

    private static readonly Dictionary<TextSyntax, SyntaxProfile> Profiles = new()
    {
        [TextSyntax.Json] = new(["//"], "/*", "*/", ['"'], true,
            new(StringComparer.Ordinal) { "true", "false", "null" }, [':'], KeyScope.Anywhere, "{}[],:"),

        [TextSyntax.Toml] = new(["#"], null, null, ['"', '\''], true,
            new(StringComparer.Ordinal) { "true", "false" }, ['='], KeyScope.LineStart, "[]{},=",
            SectionHeaders: true),

        // .cfg and .properties both read as "# comment, key separator, value"; an [ini] section
        // header is the only other shape either of them takes.
        [TextSyntax.Properties] = new(["#", "!", "//"], null, null, ['"'], false,
            new(StringComparer.OrdinalIgnoreCase) { "true", "false" }, ['=', ':'], KeyScope.LineStart, "=:[]",
            SectionHeaders: true),

        [TextSyntax.JavaScript] = new(["//"], "/*", "*/", ['"', '\'', '`'], true,
            new(StringComparer.Ordinal)
            {
                "const", "let", "var", "function", "return", "if", "else", "for", "while", "do",
                "new", "class", "extends", "import", "export", "from", "default", "true", "false",
                "null", "undefined", "async", "await", "of", "in", "typeof", "this", "try", "catch",
                "throw", "switch", "case", "break", "continue"
            }, [':'], KeyScope.Anywhere, "{}[]();,:=<>!&|?+-*/%"),

        [TextSyntax.Lua] = new(["--"], "--[[", "]]", ['"', '\''], true,
            new(StringComparer.Ordinal)
            {
                "local", "function", "end", "if", "then", "else", "elseif", "for", "while", "do",
                "return", "nil", "true", "false", "and", "or", "not", "require"
            }, ['='], KeyScope.Anywhere, "{}[](),=.:"),

        [TextSyntax.Python] = new(["#"], null, null, ['"', '\''], true,
            new(StringComparer.Ordinal)
            {
                "def", "class", "return", "if", "elif", "else", "for", "while", "import", "from",
                "as", "None", "True", "False", "and", "or", "not", "in", "is", "with", "try",
                "except", "raise", "pass", "lambda"
            }, NoChars, KeyScope.None, "{}[](),:="),

        [TextSyntax.Snbt] = new(NoStrings, null, null, ['"', '\''], true,
            new(StringComparer.OrdinalIgnoreCase) { "true", "false" }, [':'], KeyScope.Anywhere, "{}[],:;"),

        [TextSyntax.Xml] = new(NoStrings, "<!--", "-->", ['"', '\''], false,
            new(StringComparer.Ordinal), ['='], KeyScope.Anywhere, "<>/=?!", TagNames: true),

        [TextSyntax.Yaml] = new(["#"], null, null, ['"', '\''], true,
            new(StringComparer.OrdinalIgnoreCase) { "true", "false", "null", "yes", "no" },
            [':'], KeyScope.LineStart, "-:[]{},"),

        [TextSyntax.Markdown] = new(NoStrings, null, null, ['`'], false,
            new(StringComparer.Ordinal), NoChars, KeyScope.None, "", HashHeadings: true)
    };

    private static readonly SyntaxProfile PlainProfile =
        new(NoStrings, null, null, NoChars, false, new(StringComparer.Ordinal), NoChars, KeyScope.None, "");

    private static SyntaxProfile ProfileFor(TextSyntax syntax) =>
        Profiles.TryGetValue(syntax, out var profile) ? profile : PlainProfile;

    private static bool KeyAllowed(KeyScope scope, bool separatorSeen) => scope switch
    {
        KeyScope.Anywhere => true,
        KeyScope.LineStart => !separatorSeen,
        _ => false
    };

    private static bool NextNonSpaceIsSeparator(string line, int from, char[] separators)
    {
        for (var i = from; i < line.Length; i++)
        {
            if (line[i] == ' ' || line[i] == '\t') continue;
            return Array.IndexOf(separators, line[i]) >= 0;
        }
        return false;
    }

    private static bool StartsWithAny(string line, int at, string[] markers)
    {
        foreach (var marker in markers)
            if (MatchesAt(line, at, marker)) return true;
        return false;
    }

    private static bool MatchesAt(string line, int at, string marker) =>
        at + marker.Length <= line.Length && string.CompareOrdinal(line, at, marker, 0, marker.Length) == 0;

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c == '$';
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
