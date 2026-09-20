using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CloudLauncher.Views;

/// <summary>
/// Process-wide cache of decoded mod icons, keyed by URL + target width. The graph and planning
/// boards draw every node at once (no list virtualization), so without this each rebuild — and a
/// rebuild happens on every tab switch — re-created and re-decoded hundreds of <see cref="BitmapImage"/>
/// objects, which was the main source of the tab-switch lag.
///
/// One cached <see cref="ImageSource"/> is shared by every node that shows that icon and reused
/// across rebuilds, so an icon is downloaded and decoded once. Images are decoded to roughly display
/// size (<c>DecodePixelWidth</c>), cutting both decode time and memory versus the full-resolution art.
/// UI-thread only — WPF imaging is thread-affine and that's where nodes are built.
/// </summary>
public static class ModIconCache
{
    private static readonly Dictionary<string, ImageSource?> _cache = new();

    /// <summary>A cached, downscaled image for <paramref name="url"/>, or null when it's empty or
    /// fails to load. <paramref name="displayWidth"/> is the on-screen icon width; the source is
    /// decoded at 2× that for crispness on high-DPI displays.</summary>
    public static ImageSource? Get(string? url, int displayWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var decodeWidth = Math.Clamp(displayWidth * 2, 16, 256);
        var key = decodeWidth + "|" + url;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode up front, then drop the stream
            bmp.DecodePixelWidth = decodeWidth;           // downscale to display size
            bmp.UriSource = new Uri(url!, UriKind.Absolute);
            bmp.EndInit();
            // Remote images finish downloading asynchronously, so we can't freeze immediately; the
            // cached object is still shared across every node and every rebuild, which is the win.
            if (bmp.CanFreeze) bmp.Freeze();
            image = bmp;
        }
        catch { /* bad or offline URL — cache the null so we don't retry it every rebuild */ }

        _cache[key] = image;
        return image;
    }

    /// <summary>Builds an <see cref="Image"/> for a mod icon, or null when there's nothing to show.</summary>
    public static System.Windows.Controls.Image? Image(string? url, int displayWidth)
    {
        var src = Get(url, displayWidth);
        if (src is null) return null;
        var img = new System.Windows.Controls.Image { Source = src, Stretch = Stretch.UniformToFill };
        // Tiny icons: fast scaling is imperceptible and cheaper to render.
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
        return img;
    }
}
