using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CloudLauncher.Views;

/// <summary>
/// Attached properties that fill an <see cref="Image"/> from <see cref="ModIconCache"/> without
/// blocking, allocating a decoder, or throwing on the UI thread.
/// </summary>
/// <remarks>
/// Used instead of <c>&lt;Image Source="{Binding IconUrl}"/&gt;</c> on the virtualized browse
/// lists. A value converter has to answer synchronously, leaving the URL to WPF's
/// <c>ImageSourceConverter</c>, which downloads and decodes at native size. An attached property
/// owns the element, so it can return immediately, swap the frozen bitmap in when it arrives, and
/// drop the result if a recycled row was rebound meanwhile.
///
/// Usage:
/// <code>
/// &lt;Image local:IconLoader.Url="{Binding IconUrl}" local:IconLoader.DecodeWidth="44"
///        Stretch="UniformToFill"/&gt;
/// </code>
/// <c>DecodeWidth</c> is the on-screen width of the icon box; the cache decodes at 2x that.
/// <c>RenderOptions.BitmapScalingMode</c> is set here, so the call site does not need it.
/// </remarks>
public static class IconLoader
{
    /// <summary>The icon URL (or local path). Setting it starts the load.</summary>
    public static readonly DependencyProperty UrlProperty =
        DependencyProperty.RegisterAttached("Url", typeof(string), typeof(IconLoader),
            new PropertyMetadata(null, OnUrlChanged));

    public static string? GetUrl(DependencyObject o) => (string?)o.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject o, string? v) => o.SetValue(UrlProperty, v);

    /// <summary>On-screen width of the icon box, in DIPs. Defaults to 32.</summary>
    public static readonly DependencyProperty DecodeWidthProperty =
        DependencyProperty.RegisterAttached("DecodeWidth", typeof(int), typeof(IconLoader),
            new PropertyMetadata(32));

    public static int GetDecodeWidth(DependencyObject o) => (int)o.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject o, int v) => o.SetValue(DecodeWidthProperty, v);

    // Bumped on every (re)bind and on unload. An in-flight load compares the token it captured with
    // this one and drops its result if they differ.
    private static readonly DependencyProperty TokenProperty =
        DependencyProperty.RegisterAttached("Token", typeof(int), typeof(IconLoader), new PropertyMetadata(0));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(IconLoader), new PropertyMetadata(false));

    private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;

        var token = (int)img.GetValue(TokenProperty) + 1;
        img.SetValue(TokenProperty, token);

        var url = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(url)) { img.Source = null; return; }

        var width = GetDecodeWidth(img);
        Hook(img);

        // Cache hits are set synchronously, before any await, so scrolling back shows no blank frames.
        if (ModIconCache.TryGet(url, width) is { } cached) { img.Source = cached; return; }

        // Miss: clear first, since a recycled container still shows the previous mod's icon.
        img.Source = null;
        Begin(img, url!, width, token);
    }

    private static async void Begin(Image img, string url, int width, int token)
    {
        try
        {
            var source = await ModIconCache.LoadAsync(url, width);   // resumes on the UI thread
            if ((int)img.GetValue(TokenProperty) != token) return;   // row recycled or unloaded, drop it
            img.Source = source;
        }
        catch
        {
            // ModIconCache swallows its own failures; this is only here because an async void
            // that throws takes the process with it.
        }
    }

    private static void Hook(Image img)
    {
        if ((bool)img.GetValue(HookedProperty)) return;
        img.SetValue(HookedProperty, true);
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);

        // Leaving the tree abandons the swap-in (the shared download still fills the cache). Loaded
        // asks again, or an element unloaded mid-download would come back blank.
        img.Unloaded += (s, _) =>
        {
            var self = (Image)s;
            self.SetValue(TokenProperty, (int)self.GetValue(TokenProperty) + 1);
        };
        img.Loaded += (s, _) =>
        {
            var self = (Image)s;
            if (self.Source is not null) return;
            var url = GetUrl(self);
            if (string.IsNullOrWhiteSpace(url)) return;
            var token = (int)self.GetValue(TokenProperty) + 1;
            self.SetValue(TokenProperty, token);
            Begin(self, url!, GetDecodeWidth(self), token);
        };
    }
}
