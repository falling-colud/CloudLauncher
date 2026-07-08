using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Local per-pack assets (icon, rich description HTML) stored under the pack folder.</summary>
public sealed class PackAssetService(PackFolderService folders, ModrinthService downloads)
{
    private const string AssetsDir = ".cloudlauncher";
    private const string IconFileName = "icon.png";
    private const string DescriptionFileName = "description.html";
    private const string DescriptionMarkdownFileName = "description.md";
    private const string DescriptionImportedMarker = "description.imported";

    public string DescriptionMarkdownPathFor(Guid packId) => Path.Combine(AssetsDirFor(packId), DescriptionMarkdownFileName);

    public string AssetsDirFor(Guid packId) => Path.Combine(folders.PackRoot(packId), AssetsDir);

    public string IconPathFor(Guid packId) => Path.Combine(AssetsDirFor(packId), IconFileName);

    public string DescriptionHtmlPathFor(Guid packId) => Path.Combine(AssetsDirFor(packId), DescriptionFileName);

    /// <summary>
    /// Assets placed in game/ so they are included in the direct game/-sync upload.
    /// Previously this pointed to shared/.cloudlauncher; shared/ is no longer used.
    /// </summary>
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
            content = File.ReadAllText(mdPath);
            isMarkdown = true;
            return !string.IsNullOrWhiteSpace(content);
        }

        var htmlPath = Path.Combine(dir, DescriptionFileName);
        if (!File.Exists(htmlPath)) return false;

        content = File.ReadAllText(htmlPath);
        isMarkdown = !PackText.LooksLikeHtml(content);
        return !string.IsNullOrWhiteSpace(content);
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
            content = File.ReadAllText(htmlPath);
            isMarkdown = false;
            return !string.IsNullOrWhiteSpace(content);
        }

        var mdPath = Path.Combine(dir, DescriptionMarkdownFileName);
        if (!File.Exists(mdPath)) return false;

        content = File.ReadAllText(mdPath);
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

    public async Task SaveIconFromUrlAsync(Guid packId, string? iconUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return;
        var dir = AssetsDirFor(packId);
        Directory.CreateDirectory(dir);
        await downloads.DownloadFileAsync(iconUrl, IconPathFor(packId), null, ct);
        MirrorToSharedFolder(packId);
    }

    /// <summary>Longest edge the saved icon is downscaled to, so custom art stays small enough to sync.</summary>
    private const int MaxIconDimension = 512;

    /// <summary>
    /// Save a user-picked local image file as the pack icon. Decoding validates the file is a real
    /// image; oversized art is downscaled and everything is re-encoded to PNG (matching icon.png).
    /// </summary>
    public void SaveIconFromFile(Guid packId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Image file not found.", sourcePath);

        // OnLoad reads the whole file up-front, so it leaves no lock on the source and is safe even
        // if the source happens to be the destination icon.png.
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

    /// <summary>Install overview assets from game/.cloudlauncher/ after a download (copies to local asset cache).</summary>
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
            File.Copy(src, Path.Combine(localDir, fileName), overwrite: true);
        }
    }

    public ImageSource? TryLoadIconImage(Guid packId)
    {
        var path = IconPathFor(packId);
        if (!File.Exists(path)) return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // Bypass WPF's per-URI decoded-image cache so a freshly changed icon.png isn't stale.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
