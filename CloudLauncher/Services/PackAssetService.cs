using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

// The store client is unused here (icons use their own size-capped client below); the parameter
// stays so the existing constructor call keeps compiling.
#pragma warning disable CS9113
/// <summary>Local per-pack assets (icon, rich description HTML) stored under the pack folder.</summary>
public sealed class PackAssetService(PackFolderService folders, ModrinthService downloads)
#pragma warning restore CS9113
{
    private const string AssetsDir = ".cloudlauncher";
    private const string IconFileName = "icon.png";
    private const string DescriptionFileName = "description.html";
    private const string DescriptionMarkdownFileName = "description.md";
    private const string DescriptionImportedMarker = "description.imported";

    /// <summary>Largest icon taken from a store listing or from the shared folder. Real icons are far
    /// smaller.</summary>
    private const long MaxIconBytes = 4L * 1024 * 1024;

    /// <summary>Largest description file read or copied in from the shared folder. That folder comes
    /// from the server and these files are read whole.</summary>
    private const long MaxDescriptionBytes = 16L * 1024 * 1024;

    private static readonly HttpClient IconHttp = CreateIconClient();

    private static HttpClient CreateIconClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromMinutes(2) };
        ApiClient.ApplyUserAgent(client);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    public string DescriptionMarkdownPathFor(Guid packId) => Path.Combine(AssetsDirFor(packId), DescriptionMarkdownFileName);

    public string AssetsDirFor(Guid packId) => Path.Combine(folders.PackRoot(packId), AssetsDir);

    public string IconPathFor(Guid packId) => Path.Combine(AssetsDirFor(packId), IconFileName);

    public string DescriptionHtmlPathFor(Guid packId) => Path.Combine(AssetsDirFor(packId), DescriptionFileName);

    /// <summary>Assets live under game/ so the game/ sync upload includes them.</summary>
    public string SharedAssetsDirFor(Guid packId) => Path.Combine(folders.GameDir(packId), AssetsDir);

    public bool HasIcon(Guid packId) => File.Exists(IconPathFor(packId));

    public bool TryReadDescriptionForDisplay(Guid packId, out string content, out bool isMarkdown)
        => TryReadDescriptionFromDir(AssetsDirFor(packId), out content, out isMarkdown)
           || TryReadDescriptionFromDir(SharedAssetsDirFor(packId), out content, out isMarkdown);

    private static bool TryReadDescriptionFromDir(string dir, out string content, out bool isMarkdown)
    {
        content = "";
        isMarkdown = false;
        if (!Directory.Exists(dir)) return false;

        var mdPath = Path.Combine(dir, DescriptionMarkdownFileName);
        if (File.Exists(mdPath))
        {
            content = ReadDescriptionFile(mdPath);
            isMarkdown = true;
            return !string.IsNullOrWhiteSpace(content);
        }

        var htmlPath = Path.Combine(dir, DescriptionFileName);
        if (!File.Exists(htmlPath)) return false;

        content = ReadDescriptionFile(htmlPath);
        isMarkdown = !PackText.LooksLikeHtml(content);
        return !string.IsNullOrWhiteSpace(content);
    }

    /// <summary>The file's text, or "" when it is larger than a description can sensibly be.</summary>
    private static string ReadDescriptionFile(string path)
    {
        if (new FileInfo(path).Length > MaxDescriptionBytes)
        {
            AppLog.Log(nameof(PackAssetService), $"Skipped {path}: it is larger than a pack description can be.");
            return "";
        }
        return File.ReadAllText(path);
    }

    public bool TryReadDescriptionHtml(Guid packId, out string html)
    {
        if (TryReadDescriptionForDisplay(packId, out var content, out var isMarkdown))
        {
            html = PackText.PrepareDescriptionHtml(content, isMarkdown);
            return true;
        }

        html = "";
        return false;
    }

    public bool IsImportedDescription(Guid packId)
        => File.Exists(Path.Combine(AssetsDirFor(packId), DescriptionImportedMarker))
           || File.Exists(Path.Combine(SharedAssetsDirFor(packId), DescriptionImportedMarker));

    public bool TryReadCustomDescription(Guid packId, out string content, out bool isMarkdown)
    {
        if (IsImportedDescription(packId))
        {
            content = "";
            isMarkdown = false;
            return false;
        }

        if (TryReadCustomDescriptionFromDir(AssetsDirFor(packId), out content, out isMarkdown))
            return true;

        return TryReadCustomDescriptionFromDir(SharedAssetsDirFor(packId), out content, out isMarkdown);
    }

    private static bool TryReadCustomDescriptionFromDir(string dir, out string content, out bool isMarkdown)
    {
        content = "";
        isMarkdown = false;
        if (!Directory.Exists(dir)) return false;

        var htmlPath = Path.Combine(dir, DescriptionFileName);
        if (File.Exists(htmlPath))
        {
            content = ReadDescriptionFile(htmlPath);
            isMarkdown = false;
            return !string.IsNullOrWhiteSpace(content);
        }

        var mdPath = Path.Combine(dir, DescriptionMarkdownFileName);
        if (!File.Exists(mdPath)) return false;

        content = ReadDescriptionFile(mdPath);
        isMarkdown = true;
        return !string.IsNullOrWhiteSpace(content);
    }

    public void MarkDescriptionImported(Guid packId)
    {
        var dir = AssetsDirFor(packId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DescriptionImportedMarker), "1");
    }

    public void SaveDescriptionHtml(Guid packId, string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return;
        var dir = AssetsDirFor(packId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(DescriptionHtmlPathFor(packId), html);
        TryDelete(DescriptionMarkdownPathFor(packId));
        MarkDescriptionImported(packId);
        MirrorToSharedFolder(packId);
    }

    public void SaveDescriptionMarkdown(Guid packId, string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return;
        var dir = AssetsDirFor(packId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(DescriptionMarkdownPathFor(packId), markdown);
        TryDelete(DescriptionHtmlPathFor(packId));
        MarkDescriptionImported(packId);
        MirrorToSharedFolder(packId);
    }

    public void SaveCustomDescriptionHtml(Guid packId, string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            TryDelete(DescriptionHtmlPathFor(packId));
            TryDelete(DescriptionMarkdownPathFor(packId));
            TryDelete(Path.Combine(AssetsDirFor(packId), DescriptionImportedMarker));
            MirrorToSharedFolder(packId);
            return;
        }

        var dir = AssetsDirFor(packId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(DescriptionHtmlPathFor(packId), html);
        TryDelete(DescriptionMarkdownPathFor(packId));
        TryDelete(Path.Combine(dir, DescriptionImportedMarker));
        MirrorToSharedFolder(packId);
    }

    public void SaveCustomDescriptionMarkdown(Guid packId, string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            TryDelete(DescriptionMarkdownPathFor(packId));
            TryDelete(Path.Combine(AssetsDirFor(packId), DescriptionImportedMarker));
            return;
        }

        var dir = AssetsDirFor(packId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(DescriptionMarkdownPathFor(packId), markdown);
        TryDelete(DescriptionHtmlPathFor(packId));
        TryDelete(Path.Combine(dir, DescriptionImportedMarker));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    /// <summary>Downloads a store listing's icon as the pack icon.</summary>
    /// <remarks>The URL comes from store metadata, so only http and https are fetched, and the body is
    /// buffered up to <see cref="MaxIconBytes"/> before anything is written so an oversized or endless
    /// response can't fill the disk.</remarks>
    /// <exception cref="InvalidDataException">The icon is larger than <see cref="MaxIconBytes"/>.</exception>
    public async Task SaveIconFromUrlAsync(Guid packId, string? iconUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return;
        if (!SafeLaunch.IsWebUrl(iconUrl, out var uri))
        {
            AppLog.Log(nameof(PackAssetService), "Skipped a pack icon whose address is not http or https.");
            return;
        }
        var dir = AssetsDirFor(packId);
        Directory.CreateDirectory(dir);

        byte[] bytes;
        using (var resp = await IconHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > MaxIconBytes)
                throw new InvalidDataException("The pack icon is larger than 4 MB.");
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxIconBytes)
                    throw new InvalidDataException("The pack icon is larger than 4 MB.");
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
        }

        // Written beside the icon and swapped in, so a failed write leaves the old icon alone.
        var path = IconPathFor(packId);
        var part = path + ".part";
        try
        {
            await File.WriteAllBytesAsync(part, bytes, ct);
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
            throw;
        }
        MirrorToSharedFolder(packId);
    }

    /// <summary>Longest edge the saved icon is downscaled to, so custom art stays small enough to sync.</summary>
    private const int MaxIconDimension = 512;

    /// <summary>
    /// Saves a user-picked image file as the pack icon. Decoding checks it is a real image; large art
    /// is downscaled and everything is re-encoded to PNG to match icon.png.
    /// </summary>
    public void SaveIconFromFile(Guid packId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Image file not found.", sourcePath);

        // OnLoad reads the whole file up front, so no lock is left on the source, which may even be the
        // destination icon.png.
        var decoder = BitmapDecoder.Create(
            new Uri(sourcePath, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];

        var longestEdge = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longestEdge > MaxIconDimension)
        {
            var scale = (double)MaxIconDimension / longestEdge;
            frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }

        var dir = AssetsDirFor(packId);
        Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using (var fs = File.Create(IconPathFor(packId)))
            encoder.Save(fs);

        MirrorToSharedFolder(packId);
    }

    /// <summary>Remove the custom/imported icon, reverting to the generated cover art.</summary>
    public void RemoveIcon(Guid packId)
    {
        TryDelete(IconPathFor(packId));
        TryDelete(Path.Combine(SharedAssetsDirFor(packId), IconFileName));
    }

    /// <summary>Copy local overview assets into game/.cloudlauncher/ so the next upload includes them.</summary>
    public void MirrorToSharedFolder(Guid packId)
    {
        var localDir = AssetsDirFor(packId);
        if (!Directory.Exists(localDir)) return;

        var sharedDir = SharedAssetsDirFor(packId);
        Directory.CreateDirectory(sharedDir);

        foreach (var fileName in new[] { IconFileName, DescriptionFileName, DescriptionMarkdownFileName, DescriptionImportedMarker })
        {
            var src = Path.Combine(localDir, fileName);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(sharedDir, fileName), overwrite: true);
        }
    }

    /// <summary>Install overview assets from game/.cloudlauncher/ after a download (copies to local
    /// asset cache).</summary>
    /// <remarks>Only the four known file names are copied, and files over the size limits are
    /// skipped.</remarks>
    public void ApplyFromSharedFolder(Guid packId)
    {
        var sharedDir = SharedAssetsDirFor(packId);
        if (!Directory.Exists(sharedDir)) return;

        var localDir = AssetsDirFor(packId);
        Directory.CreateDirectory(localDir);

        foreach (var fileName in new[] { IconFileName, DescriptionFileName, DescriptionMarkdownFileName, DescriptionImportedMarker })
        {
            var src = Path.Combine(sharedDir, fileName);
            if (!File.Exists(src)) continue;
            var limit = fileName == IconFileName ? MaxIconBytes : MaxDescriptionBytes;
            if (new FileInfo(src).Length > limit)
            {
                AppLog.Log(nameof(PackAssetService), $"Skipped the shared {fileName} of {packId}: it is larger than the launcher accepts.");
                continue;
            }
            File.Copy(src, Path.Combine(localDir, fileName), overwrite: true);
        }
    }

    public ImageSource? TryLoadIconImage(Guid packId)
    {
        var path = IconPathFor(packId);
        if (!File.Exists(path)) return null;

        try
        {
            // Decode from bytes so WPF's per-URI image cache can't return a stale icon.png, and through
            // SafeImage because the icon may be another user's (it arrives through the shared folder).
            if (new FileInfo(path).Length > SafeImage.MaxEncodedBytes) return null;
            return SafeImage.Decode(File.ReadAllBytes(path), maxSide: 512);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Decodes images other people supply (shared pack icons, resource pack art, store and
/// hosted icons) without letting a small file claim an enormous picture.</summary>
/// <remarks>A few KB of PNG can declare dimensions that cost gigabytes to decode, so the header is
/// checked first and the decode is bounded on the longer side.</remarks>
public static class SafeImage
{
    /// <summary>The largest encoded image read into memory to decode.</summary>
    public const long MaxEncodedBytes = 32L * 1024 * 1024;

    /// <summary>The largest source dimension decoded at all.</summary>
    public const int MaxSourcePixels = 16384;

    /// <summary>Decodes <paramref name="bytes"/> and freezes the result, scaled so neither side is
    /// larger than <paramref name="maxSide"/>. Null when the bytes are not an image WPF can read or
    /// claim dimensions past <see cref="MaxSourcePixels"/>.</summary>
    /// <param name="scaleUp">Also scale smaller images up to <paramref name="maxSide"/>, for callers that
    /// always decoded at a fixed display size.</param>
    public static BitmapImage? Decode(byte[] bytes, int maxSide,
                                       BitmapCreateOptions options = BitmapCreateOptions.None, bool scaleUp = false)
    {
        try
        {
            // Only the header: with delayed creation and no caching, no pixels are decoded here.
            var header = BitmapFrame.Create(new MemoryStream(bytes, writable: false),
                                            BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int width = header.PixelWidth, height = header.PixelHeight;
            if (width <= 0 || height <= 0 || width > MaxSourcePixels || height > MaxSourcePixels) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = options;
            // Bounded on the longer side: a width alone would let a tall, narrow image get any height.
            if (scaleUp || Math.Max(width, height) > maxSide)
            {
                if (height > width) image.DecodePixelHeight = maxSide;
                else image.DecodePixelWidth = maxSide;
            }
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // Codecs report a bad image as many different exception types; each means "no image".
            return null;
        }
    }
}
