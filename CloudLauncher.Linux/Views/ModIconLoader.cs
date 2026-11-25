using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// Mod and modpack icons from the stores' CDNs, for lists: set <c>ModIconLoader.Url</c> on an
/// <see cref="Image"/> (and <c>ModIconLoader.DecodeWidth</c> to its size). The Windows launcher's
/// IconLoader and ModIconCache in one.
/// </summary>
/// <remarks>Downloads run off the UI thread, six at a time, decode close to display size and are kept in
/// a small LRU cache shared by every row with the same icon; failures are remembered as nulls so a dead
/// address is not asked again. A row that has moved on to another icon by the time a download lands
/// keeps its own. Only http and https addresses are fetched.</remarks>
public sealed class ModIconLoader
{
    private ModIconLoader() { }

    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<ModIconLoader, Image, string?>("Url");

    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<ModIconLoader, Image, int>("DecodeWidth", 40);

    public static string? GetUrl(Image image) => image.GetValue(UrlProperty);
    public static void SetUrl(Image image, string? value) => image.SetValue(UrlProperty, value);
    public static int GetDecodeWidth(Image image) => image.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);

    private const int MaxEntries = 400;
    private const int MaxConcurrentDownloads = 6;
    private const int MaxIconBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, Bitmap? Image)>> Index = new(StringComparer.Ordinal);
    private static readonly LinkedList<(string Key, Bitmap? Image)> Recency = new();
    private static readonly Dictionary<string, Task<Bitmap?>> InFlight = new(StringComparer.Ordinal);

    static ModIconLoader()
    {
        UrlProperty.Changed.AddClassHandler<Image>((image, _) => Apply(image));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = MaxConcurrentDownloads,
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        ApiClient.ApplyUserAgent(client);
        return client;
    }

    private static void Apply(Image image)
    {
        var url = GetUrl(image);
        if (string.IsNullOrWhiteSpace(url) || !SafeLaunch.IsWebUrl(url, out _))
        {
            image.Source = null;
            return;
        }
        var width = Math.Clamp(GetDecodeWidth(image) * 2, 16, 256);
        var key = width + "|" + url;
        if (TryGet(key, out var cached))
        {
            image.Source = cached;
            return;
        }
        image.Source = null;
        _ = LoadIntoAsync(image, url, key, width);
    }

    private static async Task LoadIntoAsync(Image image, string url, string key, int width)
    {
        Bitmap? bitmap;
        try { bitmap = await LoadAsync(url, key, width).ConfigureAwait(false); }
        catch { bitmap = null; }
        if (bitmap is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            // The row may show another mod by now (lists recycle their rows).
            if (string.Equals(GetUrl(image), url, StringComparison.Ordinal)) image.Source = bitmap;
        });
    }

    private static bool TryGet(string key, out Bitmap? image)
    {
        lock (Sync)
        {
            if (Index.TryGetValue(key, out var node))
            {
                Recency.Remove(node);
                Recency.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }
        image = null;
        return false;
    }

    private static Task<Bitmap?> LoadAsync(string url, string key, int width)
    {
        lock (Sync)
        {
            if (InFlight.TryGetValue(key, out var running)) return running;
            var task = FetchAsync(url, key, width);
            InFlight[key] = task;
            return task;
        }
    }

    private static async Task<Bitmap?> FetchAsync(string url, string key, int width)
    {
        Bitmap? bitmap = null;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.IsSuccessStatusCode
                && (response.Content.Headers.ContentLength is not { } length || length <= MaxIconBytes))
            {
                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxIconBytes) throw new InvalidDataException("Not an icon.");
                }
                buffer.Position = 0;
                bitmap = Bitmap.DecodeToWidth(buffer, width, BitmapInterpolationMode.HighQuality);
            }
        }
        catch
        {
            // A dead link, a format Skia cannot read (SVG), a timeout: the row keeps its letter.
            bitmap = null;
        }
        finally { Gate.Release(); }

        lock (Sync)
        {
            InFlight.Remove(key);
            if (!Index.ContainsKey(key))
            {
                Index[key] = Recency.AddFirst((key, bitmap));
                while (Recency.Count > MaxEntries && Recency.Last is { } last)
                {
                    Recency.RemoveLast();
                    Index.Remove(last.Value.Key);
                }
            }
        }
        return bitmap;
    }
}
