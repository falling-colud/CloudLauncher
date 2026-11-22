using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CloudLauncher.Services;

/// <summary>
/// The background picture (see <see cref="BackgroundImageSettings"/>) for <see cref="ThemeService"/>:
/// copied in when chosen, then decoded, turned the way the camera meant it, capped in size and
/// blurred, all off the UI thread.
/// </summary>
/// <remarks>
/// <para>The decoded picture and the one on screen are kept, keyed by the file and the blur level, so
/// the Dim slider and the fit cost nothing; only a new picture or a new blur level does any work.</para>
/// <para>Blur is a three-pass box blur (close to a Gaussian) on a copy shrunk until the radius is a
/// few pixels. A strong blur then costs no more than a light one, and the window stretching the small
/// copy back up keeps it smooth. Edges repeat their last pixel, so the window's edges do not darken
/// the way a blur effect over the window would make them.</para>
/// </remarks>
public static class BackgroundImageService
{
    /// <summary>Pictures with more pixels than this are decoded smaller: 4K is plenty for a window, and
    /// an 8K screenshot would otherwise hold about 130 MB.</summary>
    private const double MaxPixels = 3840.0 * 2160.0;

    /// <summary>Files larger than this are refused when chosen.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>Pictures with more pixels than this are refused before decoding (about 12000 × 12000).</summary>
    private const double MaxSourcePixels = 150e6;

    /// <summary>The blur radius per step, as a share of the picture's longer side.</summary>
    private const double BlurPerStep = 0.0045;

    /// <summary>The blur radius the shrunken copy is blurred at, in its own pixels.</summary>
    private const double SmallRadius = 8;

    /// <summary>What the file picker offers. Windows reads others too (HEIC with its extension), so
    /// "All files" stays available.</summary>
    public const string PickerFilter =
        "Pictures|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*";

    /// <summary>What is known about one picture: ready, or failed with a message for the user.</summary>
    private sealed record Entry(string Key, BitmapSource? Bitmap, string? Error);

    /// <summary>The background for one set of settings, as <see cref="Get"/> found it.</summary>
    /// <param name="Bitmap">The picture to show, when it is ready.</param>
    /// <param name="Error">Why it cannot be shown, in words for the user.</param>
    /// <param name="Pending">Still being made; <see cref="Ready"/> follows.</param>
    public readonly record struct Result(BitmapSource? Bitmap, string? Error, bool Pending);

    private static readonly object Gate = new();
    private static Entry? _decoded;   // the picture as decoded, keyed by the file
    private static Entry? _shown;     // what the window shows (decoded or blurred), keyed by file and blur
    private static string? _wanted;   // the key asked for last; results for any other key are dropped

    /// <summary>Raised on the UI thread when a picture that <see cref="Get"/> started is ready or failed.</summary>
    public static event Action? Ready;

    /// <summary>
    /// The picture for <paramref name="settings"/>: ready, failed, or pending. A pending one is made in
    /// the background and <see cref="Ready"/> is raised when it is done. Call on the UI thread.
    /// </summary>
    public static Result Get(BackgroundImageSettings settings)
    {
        if (!settings.IsOn) return default;
        // A hand-edited value that is no file name at all ("C:\").
        if (settings.FullPath is not { } path)
            return new Result(null, "The background setting names no picture. Choose one again.", false);
        var fileKey = FileKey(path);
        var blur = settings.EffectiveBlur;
        var key = fileKey + "|blur=" + blur;
        lock (Gate)
        {
            if (_shown is { } shown && shown.Key == key)
            {
                _wanted = key;
                return new Result(shown.Bitmap, shown.Error, false);
            }
            // Back to sharp: the decoded picture is the answer, with no trip through the thread pool.
            if (blur == 0 && _decoded is { Bitmap: not null } decoded && decoded.Key == fileKey)
            {
                _wanted = key;
                _shown = new Entry(key, decoded.Bitmap, null);
                return new Result(decoded.Bitmap, null, false);
            }
            if (_wanted != key)
            {
                _wanted = key;
                _ = Task.Run(() => Make(path, fileKey, blur, key));
            }
            return new Result(null, null, true);
        }
    }

    /// <summary>The picture itself, unblurred, once it has been decoded: for the preview in Settings.</summary>
    public static BitmapSource? Preview(BackgroundImageSettings settings)
    {
        if (settings.FullPath is not { } path) return null;
        var fileKey = FileKey(path);
        lock (Gate) return _decoded is { } d && d.Key == fileKey ? d.Bitmap : null;
    }

    /// <summary>
    /// Checks that <paramref name="source"/> is a picture this PC can read, then copies it into
    /// <see cref="BackgroundImageSettings.Folder"/> and returns the copy's file name. The picture in
    /// use is not touched; <see cref="RemoveOthers"/> clears the old copies once the new one is saved.
    /// </summary>
    /// <exception cref="InvalidDataException">With a message for the user, when the file will not do.</exception>
    public static async Task<string> ImportAsync(string source)
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new InvalidDataException("That file is not there any more.");
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException(
                $"That picture is {info.Length / (1024 * 1024)} MB. Pick one under {MaxFileBytes / (1024 * 1024)} MB; a screenshot or a photo is plenty.");

        // Decode it first: a file that is not a picture must not take the place of the one in use.
        BitmapSource decoded;
        try { decoded = await Task.Run(() => Decode(source)); }
        catch (Exception ex)
        {
            AppLog.LogError("background", ex);
            throw new InvalidDataException(Describe(ex), ex);
        }

        var folder = BackgroundImageSettings.Folder;
        var ext = info.Extension.ToLowerInvariant();
        if (ext.Length is < 2 or > 6 || !ext.Skip(1).All(char.IsAsciiLetterOrDigit)) ext = ".img";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"background-{stamp}{ext}";
        for (var i = 2; File.Exists(Path.Combine(folder, name)); i++) name = $"background-{stamp}-{i}{ext}";
        var dest = Path.Combine(folder, name);

        await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            // Through a temporary name, so a copy cut short never looks like a picture.
            var tmp = dest + ".tmp";
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, dest);
        });

        // The copy is the same picture: keep the decode instead of doing it again for the window.
        lock (Gate) _decoded = new Entry(FileKey(dest), decoded, null);
        return name;
    }

    /// <summary>Deletes the launcher's earlier copies, keeping <paramref name="keep"/> (a file name in
    /// the folder), or all of them when it is null. Only files the launcher named are touched.</summary>
    public static void RemoveOthers(string? keep)
    {
        try
        {
            var folder = BackgroundImageSettings.Folder;
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "background-*"))
            {
                if (keep is not null && string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: next time */ }
            }
        }
        catch (Exception ex) { AppLog.LogError("background", ex); }
    }

    // ── making the picture ───────────────────────────────────────────────────

    private static void Make(string path, string fileKey, int blur, string key)
    {
        Entry result;
        try
        {
            BitmapSource? decoded;
            lock (Gate) decoded = _decoded is { } d && d.Key == fileKey ? d.Bitmap : null;
            if (decoded is null)
            {
                if (!File.Exists(path)) throw new FileNotFoundException("The background picture is gone.", path);
                decoded = Decode(path);
                lock (Gate) _decoded = new Entry(fileKey, decoded, null);
            }
            result = new Entry(key, blur > 0 ? Blur(decoded, blur) : decoded, null);
        }
        catch (Exception ex)
        {
            AppLog.LogError("background", ex);
            result = new Entry(key, null, Describe(ex));
        }

        if (Application.Current is not { } app) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            lock (Gate)
            {
                // Something else was asked for meanwhile (another picture, another blur level).
                if (_wanted != key) return;
                _shown = result;
            }
            Ready?.Invoke();
        });
    }

    /// <summary>Identifies a file's contents cheaply: a replaced file with the same name is a new key.</summary>
    private static string FileKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path + "|missing";
        }
        catch { return path + "|unreadable"; }
    }

    /// <summary>
    /// Decodes <paramref name="path"/> into a frozen Pbgra32 bitmap no larger than <see cref="MaxPixels"/>,
    /// turned by its EXIF orientation. Its size in the window is the picture's own pixel size, whatever
    /// size it was decoded at and whatever DPI the file claims (a 72 DPI photo would otherwise come out a
    /// third larger in Center and Tile).
    /// </summary>
    private static BitmapSource Decode(string path)
    {
        int width, height, orientation;
        using (var probe = OpenRead(path))
        {
            // Only the header: the size and the camera's orientation, without decoding any pixels.
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            orientation = ReadOrientation(frame);
        }
        if (width < 1 || height < 1) throw new InvalidDataException("The picture is empty.");
        // A small file can still be an enormous picture (a PNG of one colour), and some formats decode
        // all of it before shrinking it.
        if ((double)width * height > MaxSourcePixels)
            throw new InvalidDataException(
                $"That picture is {width} × {height}, too large to use. Anything up to about 12000 × 12000 works.");

        var scale = Math.Min(1.0, Math.Sqrt(MaxPixels / ((double)width * height)));
        var decodeWidth = scale < 1 ? Math.Max(1, (int)Math.Round(width * scale)) : 0;
        BitmapSource bitmap;
        try { bitmap = Load(path, decodeWidth, ignoreColourProfile: false); }
        catch (Exception ex) when (ex is FileFormatException or COMException or ArgumentException or NotSupportedException)
        {
            // Some files carry a colour profile Windows cannot use; the picture itself is usually fine.
            bitmap = Load(path, decodeWidth, ignoreColourProfile: true);
        }
        bitmap = Orient(bitmap, orientation);

        var converted = bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var w = converted.PixelWidth;
        var h = converted.PixelHeight;
        var pixels = new byte[checked(w * h * 4)];
        converted.CopyPixels(pixels, w * 4, 0);
        var (shownW, shownH) = orientation >= 5 ? (height, width) : (width, height);
        return Create(pixels, w, h, 96.0 * w / shownW, 96.0 * h / shownH);
    }

    private static BitmapSource Load(string path, int decodeWidth, bool ignoreColourProfile)
    {
        using var stream = OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = ignoreColourProfile ? BitmapCreateOptions.IgnoreColorProfile : BitmapCreateOptions.None;
        image.StreamSource = stream;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The EXIF orientation (1 to 8), or 1 when the file has none.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta
                && meta.GetQuery("System.Photo.Orientation") is ushort value
                && value is >= 1 and <= 8)
                return value;
        }
        catch { /* a format without photo metadata, or a damaged block: as stored */ }
        return 1;
    }

    /// <summary>Turns a photo the way the camera recorded it was held. Windows shows it that way, and
    /// WPF does not do it by itself.</summary>
    private static BitmapSource Orient(BitmapSource bitmap, int orientation)
    {
        Transform? turn = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (turn is null) return bitmap;
        try
        {
            var turned = new TransformedBitmap(bitmap, turn);
            turned.Freeze();
            return turned;
        }
        catch (Exception ex)
        {
            AppLog.LogError("background", ex);
            return bitmap;
        }
    }

    // ── blur ─────────────────────────────────────────────────────────────────

    /// <summary>A blurred copy of <paramref name="source"/> (Pbgra32) with the same size in the window.</summary>
    internal static BitmapSource Blur(BitmapSource source, int steps)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        var pixels = new byte[checked(w * h * 4)];
        source.CopyPixels(pixels, w * 4, 0);

        var radius = steps * BlurPerStep * Math.Max(w, h);
        var factor = Math.Max(1, (int)Math.Floor(radius / SmallRadius));
        var (small, sw, sh) = factor > 1 ? Shrink(pixels, w, h, factor) : (pixels, w, h);
        BoxBlur(small, sw, sh, Math.Max(1, (int)Math.Round(radius / factor)));
        return Create(small, sw, sh, source.DpiX * sw / w, source.DpiY * sh / h);
    }

    private static BitmapSource Create(byte[] pixels, int w, int h, double dpiX, double dpiY)
    {
        var bitmap = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Pbgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Averages each <paramref name="factor"/> by <paramref name="factor"/> block into one
    /// pixel. The last partial row and column of blocks are left out.</summary>
    private static (byte[] Pixels, int W, int H) Shrink(byte[] src, int w, int h, int factor)
    {
        var sw = Math.Max(1, w / factor);
        var sh = Math.Max(1, h / factor);
        var dst = new byte[sw * sh * 4];
        var sums = new int[sw * 4];
        var count = factor * factor;
        for (var y = 0; y < sh; y++)
        {
            Array.Clear(sums);
            for (var row = y * factor; row < y * factor + factor && row < h; row++)
            {
                var s = row * w * 4;
                for (var x = 0; x < sw; x++)
                {
                    var o = x * 4;
                    var p = s + x * factor * 4;
                    for (var i = 0; i < factor; i++, p += 4)
                    {
                        sums[o] += src[p];
                        sums[o + 1] += src[p + 1];
                        sums[o + 2] += src[p + 2];
                        sums[o + 3] += src[p + 3];
                    }
                }
            }
            var d = y * sw * 4;
            for (var i = 0; i < sums.Length; i++) dst[d + i] = (byte)((sums[i] + count / 2) / count);
        }
        return (dst, sw, sh);
    }

    /// <summary>Three box blurs across and down, in place: close to a Gaussian blur. Pixels past an
    /// edge count as the edge pixel.</summary>
    private static void BoxBlur(byte[] pixels, int w, int h, int radius)
    {
        var tmp = new byte[pixels.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BlurLine(pixels, tmp, lines: h, length: w, lineStride: w * 4, step: 4, radius);   // across
            BlurLine(tmp, pixels, lines: w, length: h, lineStride: 4, step: w * 4, radius);   // down
        }
    }

    private static void BlurLine(byte[] src, byte[] dst, int lines, int length, int lineStride, int step, int radius)
    {
        var size = 2 * radius + 1;
        var last = length - 1;
        for (var line = 0; line < lines; line++)
        {
            var start = line * lineStride;
            for (var c = 0; c < 4; c++)
            {
                var b = start + c;
                var sum = 0;
                for (var i = -radius; i <= radius; i++) sum += src[b + Math.Clamp(i, 0, last) * step];
                for (var i = 0; i < length; i++)
                {
                    dst[b + i * step] = (byte)((sum + radius) / size);
                    sum += src[b + Math.Min(i + radius + 1, last) * step] - src[b + Math.Max(i - radius, 0) * step];
                }
            }
        }
    }

    /// <summary>What went wrong, in words for the user.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException =>
            "The picture is no longer in the launcher's backgrounds folder. Choose it again.",
        OutOfMemoryException or InsufficientExecutionStackException =>
            "That picture is too large to open. Try a smaller one.",
        InvalidDataException => ex.Message,
        UnauthorizedAccessException or IOException when ex is not FileFormatException =>
            "Windows would not let the launcher read that file.",
        _ => "This PC cannot read that kind of picture. PNG and JPG always work.",
    };
}
