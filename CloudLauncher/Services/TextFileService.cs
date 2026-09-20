using System.IO;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>
/// Reading and writing the text files inside a pack — configs, KubeJS scripts, server lists — for the
/// built-in editor.
/// </summary>
/// <remarks>
/// The point of this class is that a config file edited in the launcher must come back byte-for-byte
/// identical apart from the change: same encoding, same BOM or lack of one, same line endings. Several
/// of the formats involved care. A Forge <c>.toml</c> read as UTF-8 and written back as UTF-8 with a
/// BOM fails to parse (the loader reads the BOM as part of the first key); a <c>.properties</c> file
/// whose CRLFs become LFs is fine for Minecraft but produces a diff touching every line the next time
/// the pack is synced. So the encoding and newline style are detected on read and replayed on write.
/// </remarks>
public static class TextFileService
{
    /// <summary>Anything larger is not opened in the editor. A config that big is generated data
    /// (a recipe dump, a world-gen cache), and loading it into a WPF TextBox would hang the UI.</summary>
    public const long MaxEditableBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Extensions the editor offers by default. Everything Minecraft packs keep hand-edited settings
    /// in, plus the script formats (KubeJS is <c>.js</c>, CraftTweaker <c>.zs</c>, Ice and Fire and
    /// friends use <c>.snbt</c>).
    /// </summary>
    public static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".cfg", ".conf", ".config", ".toml", ".json", ".json5", ".jsonc", ".yaml", ".yml",
        ".properties", ".ini", ".snbt", ".nbt5", ".js", ".mjs", ".ts", ".zs", ".lua", ".py",
        ".md", ".markdown", ".xml", ".html", ".css", ".csv", ".tsv", ".log", ".sh", ".bat", ".cmd",
        ".mcmeta", ".mcfunction", ".lang", ".env", ".gitignore", ".sk"
    };

    /// <summary>Folders worth showing first in a pack: this is where hand-editing actually happens.</summary>
    public static readonly string[] PreferredFolders =
        ["config", "kubejs", "defaultconfigs", "scripts", "global_packs", "openloader", "local"];

    /// <summary>Files the editor never offers — jars, images, archives and the world data. Listing
    /// them is noise, and opening one is a mistake waiting to happen.</summary>
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
    /// line ending it uses. Throws for a file that is too large or that contains NUL bytes (i.e. is
    /// not text however its extension reads).
    /// </summary>
    public static TextFile Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxEditableBytes)
            throw new InvalidOperationException(
                $"{info.Name} is {info.Length / 1024 / 1024} MB — too large to edit here. Open it in an external editor.");

        var bytes = File.ReadAllBytes(path);
        if (LooksBinary(bytes))
            throw new InvalidOperationException($"{info.Name} is not a text file.");

        var (encoding, hasBom, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new TextFile(text, encoding, hasBom, DetectNewline(text), info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Writes <paramref name="text"/> back in the file's original encoding and line ending, via a
    /// temporary file in the same folder so an interrupted save cannot leave a half-written config
    /// (which, for a mod config, means a game that will not start).
    /// </summary>
    public static DateTime Write(string path, string text, TextFile original)
    {
        var normalized = NormalizeNewlines(text, original.Newline);
        // GetBytes never emits a BOM; the preamble is prepended only when the file had one. That is
        // what keeps a BOM-less Forge .toml BOM-less.
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
}
