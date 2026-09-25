using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Process-wide cache of decoded mod icons, keyed by URL and target width, plus the fetch
/// pipeline that fills it.</summary>
/// <remarks>The graph and planning boards draw every node at once (no virtualization) and rebuild
/// on tab switches, and the browse lists realize rows quickly while scrolling. Binding a URL
/// straight to <c>Image.Source</c> would start an uncancellable download per row and decode at full
/// size on the UI thread. Here downloads run off the UI thread, six at a time, decode close to
/// display size, and the bitmap is frozen so it can be handed to the render thread and shared by
/// every row with the same icon. Failures are cached as nulls so dead URLs aren't retried, and the
/// cache is LRU-bounded.</remarks>
public static class ModIconCache
{
    /// <summary>How many decoded bitmaps (and null markers) to keep. A ~44px icon decoded at 2x is
    /// about 30 KB, so this is a few tens of MB at worst and covers far more rows than are realized
    /// at once.</summary>
    private const int MaxEntries = 512;

    /// <summary>Concurrent downloads. Enough to keep up with fast scrolling, few enough not to
    /// starve the API calls the page is also making.</summary>
    private const int MaxConcurrentDownloads = 6;

    /// <summary>An icon is a thumbnail. Anything bigger than this is a mis-tagged URL.</summary>
    private const int MaxIconBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(MaxConcurrentDownloads, MaxConcurrentDownloads);

    private sealed class Entry
    {
        public required string Key;
        public ImageSource? Image;
    }

    // One lock for all three: they are only ever touched together, and the critical sections are a
    // handful of dictionary operations.
    private static readonly object Sync = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Index = new(StringComparer.Ordinal);
    private static readonly LinkedList<Entry> Recency = new();      // most recently used at the head
    private static readonly Dictionary<string, Task<ImageSource?>> InFlight = new(StringComparer.Ordinal);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // Long-lived client, but not an immortal connection: CDNs rotate and a pinned socket
            // outlives the DNS answer that opened it.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = MaxConcurrentDownloads,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        ApiClient.ApplyUserAgent(client);
        return client;
    }

    /// <summary>The decode width for a given on-screen width: 2x for high-DPI, clamped so tiny
    /// badges and big tiles both get a sane bitmap size.</summary>
    public static int DecodeWidthFor(int displayWidth) => Math.Clamp(displayWidth * 2, 16, 256);

    private static string KeyFor(string url, int decodeWidth) => decodeWidth + "|" + url;

    /// <summary>A cached, downscaled image for <paramref name="url"/>, or null when it is empty,
    /// still downloading, or known bad. A miss starts the fetch in the background; use
    /// <see cref="LoadAsync"/> or <see cref="IconLoader"/> to get the result when it arrives.</summary>
    public static ImageSource? Get(string? url, int displayWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var decodeWidth = DecodeWidthFor(displayWidth);
        var key = KeyFor(url!, decodeWidth);

        if (TryTouch(key, out var cached)) return cached;
        _ = Fetch(url!, decodeWidth, key);      // fire and forget; it lands in the cache
        return null;
    }

    /// <summary>Cache lookup only, never starts a download. A recycled row asks this first, so a
    /// hit can be set synchronously without a blank frame.</summary>
    public static ImageSource? TryGet(string? url, int displayWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return TryTouch(KeyFor(url!, DecodeWidthFor(displayWidth)), out var cached) ? cached : null;
    }

    /// <summary>The cached image, downloading and decoding it off the UI thread if this is the first
    /// ask. Never throws: a bad URL resolves to null and is remembered as null.</summary>
    public static Task<ImageSource?> LoadAsync(string? url, int displayWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource?>(null);
        var decodeWidth = DecodeWidthFor(displayWidth);
        var key = KeyFor(url!, decodeWidth);

        return TryTouch(key, out var cached) ? Task.FromResult(cached) : Fetch(url!, decodeWidth, key);
    }

    /// <summary>Warms the cache for an icon that is about to be shown.</summary>
    public static void Prime(string? url, int displayWidth) => _ = LoadAsync(url, displayWidth);

    /// <summary>Builds an <see cref="Image"/> for a mod icon, or null when there is nothing to show.
    /// The element is returned immediately and fills in when the download lands.</summary>
    public static System.Windows.Controls.Image? Image(string? url, int displayWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var img = new System.Windows.Controls.Image { Stretch = Stretch.UniformToFill };
        // Tiny icons: fast scaling is imperceptible and cheaper to render.
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
        IconLoader.SetDecodeWidth(img, displayWidth);
        IconLoader.SetUrl(img, url);
        return img;
    }

    /// <summary>How many entries are retained right now. Exists for the perf harness.</summary>
    public static int Count { get { lock (Sync) return Index.Count; } }

    // ── cache internals ───────────────────────────────────────────────────────

    private static bool TryTouch(string key, out ImageSource? image)
    {
        lock (Sync)
        {
            if (!Index.TryGetValue(key, out var node)) { image = null; return false; }
            // Move to the head: eviction takes from the tail, so icons still in use are not the
            // ones evicted.
            if (!ReferenceEquals(node, Recency.First))
            {
                Recency.Remove(node);
                Recency.AddFirst(node);
            }
            image = node.Value.Image;
            return true;
        }
    }

    private static void Store(string key, ImageSource? image)
    {
        lock (Sync)
        {
            if (Index.TryGetValue(key, out var existing))
            {
                existing.Value.Image = image;
                Recency.Remove(existing);
                Recency.AddFirst(existing);
                return;
            }

            Index[key] = Recency.AddFirst(new Entry { Key = key, Image = image });
            while (Index.Count > MaxEntries && Recency.Last is { } oldest)
            {
                Recency.RemoveLast();
                Index.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>Starts (or joins) the single fetch for this key. Deduplicated because fast
    /// scrolling can realize the same icon in several rows before the first download finishes.</summary>
    private static Task<ImageSource?> Fetch(string url, int decodeWidth, string key)
    {
        lock (Sync)
        {
            if (Index.TryGetValue(key, out var done)) return Task.FromResult(done.Value.Image);
            if (InFlight.TryGetValue(key, out var running)) return running;

            // Task.Run rather than a bare async call: this is called from a property-changed
            // callback on the UI thread, and none of the body should run there.
            var task = Task.Run(() => DownloadAsync(url, decodeWidth, key));
            InFlight[key] = task;
            _ = task.ContinueWith(
                _ => { lock (Sync) InFlight.Remove(key); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private static async Task<ImageSource?> DownloadAsync(string url, int decodeWidth, string key)
    {
        ImageSource? image = null;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var response = await Http
                        .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is > MaxIconBytes)
                        throw new InvalidOperationException("icon is larger than an icon has any business being");

                    // Capped while reading too: a response with no length can run on for ever.
                    await using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    SafeZip.CopyCapped(body, buffer, MaxIconBytes);
                    image = Decode(buffer.ToArray(), decodeWidth);
                }
                finally { Gate.Release(); }
            }
            else
            {
                image = DecodeLocal(url, decodeWidth);
            }
        }
        catch (Exception ex)
        {
            // Once per URL, since the null is cached, so this doesn't flood the log.
            AppLog.Log("icons", $"icon unavailable ({url}): {ex.Message}");
        }

        Store(key, image);
        return image;
    }

    /// <summary>Decodes to roughly display size and freezes, on the current thread. Freezing is
    /// what makes the result safe to hand to the UI thread.</summary>
    private static ImageSource Decode(byte[] bytes, int decodeWidth) =>
        // Icons come from store and shared metadata, so the decode is bounded on the longer side and
        // absurd dimensions are refused. Colour management on a 44px badge is pure cost.
        SafeImage.Decode(bytes, decodeWidth, BitmapCreateOptions.IgnoreColorProfile, scaleUp: true)
        ?? throw new InvalidDataException("not an image the launcher will decode");

    /// <summary>A row's icon can also be a file already on disk (a pack's cached artwork).</summary>
    private static ImageSource? DecodeLocal(string url, int decodeWidth)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : url;
        // Icon URLs come from store and shared metadata. Touching a path on another computer makes
        // Windows connect to it, so such a path is only read from the launcher's own folders.
        if ((uri is { IsUnc: true } || IsNetworkPath(path)) && !InLauncherFolders(path))
        {
            AppLog.Log("icons", $"icon not read from another computer: {url}");
            return null;
        }
        if (File.Exists(path))
            return new FileInfo(path).Length > MaxIconBytes ? null : Decode(File.ReadAllBytes(path), decodeWidth);

        // Anything else absolute (pack://, an embedded resource): let WPF resolve it. OnLoad keeps
        // this synchronous so the freeze below is valid.
        if (uri is null) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = decodeWidth;
        bmp.UriSource = uri;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A UNC or device path (\\server\share, \\?\...), in either slash style.</summary>
    private static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>True when <paramref name="path"/> is under the launcher's data folder or the instances
    /// folder, either of which the user may have put on a network share.</summary>
    private static bool InLauncherFolders(string path)
    {
        try
        {
            return PathSafety.IsInside(AppSettings.DataRootPath, path)
                || PathSafety.IsInside(App.State.Settings.PacksRoot, path);
        }
        catch
        {
            return false;
        }
    }
}
