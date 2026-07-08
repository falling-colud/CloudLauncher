using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace CloudLauncher.Views;

public partial class ScreenshotPreviewWindow : Window
{
    private readonly string _imageUrl;
    private bool _isClosing;

    public ScreenshotPreviewWindow(string imageUrl, string? title)
    {
        InitializeComponent();

        _imageUrl = imageUrl;
        var displayTitle = string.IsNullOrWhiteSpace(title) ? "Screenshot" : title.Trim();
        Title = displayTitle;
        TitleText.Text = displayTitle;
        UrlText.Text = imageUrl;
        LoadPreviewImage();
    }

    public static void ShowFor(Window? owner, string imageUrl, string? title)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return;

        var window = new ScreenshotPreviewWindow(imageUrl, title);
        if (owner is not null)
        {
            window.Owner = owner;
            window.SizeToOwner();
        }

        window.ShowDialog();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BeginOpenAnimation();
    }

    private void LoadPreviewImage()
    {
        try
        {
            var image = new BitmapImage();
            image.DownloadFailed += (_, args) => ShowImageError(args.ErrorException.Message);
            image.DecodeFailed += (_, args) => ShowImageError(args.ErrorException.Message);
            image.DownloadCompleted += (_, _) => ImageStatusText.Text = "";

            image.BeginInit();
            image.UriSource = new Uri(_imageUrl, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.Default;
            image.EndInit();

            PreviewImage.Source = image;
        }
        catch (Exception ex)
        {
            ShowImageError(ex.Message);
        }
    }

    private void SizeToOwner()
    {
        if (Owner is null) return;

        var ownerWidth = Owner.ActualWidth > 0 ? Owner.ActualWidth : Owner.Width;
        var ownerHeight = Owner.ActualHeight > 0 ? Owner.ActualHeight : Owner.Height;
        if (!double.IsFinite(ownerWidth) || ownerWidth <= 0)
            ownerWidth = Width;
        if (!double.IsFinite(ownerHeight) || ownerHeight <= 0)
            ownerHeight = Height;

        Width = Math.Min(1180, Math.Max(560, ownerWidth * 0.9));
        Height = Math.Min(820, Math.Max(420, ownerHeight * 0.9));
        PreviewCard.Width = Math.Max(520, Width - 80);
        PreviewCard.MaxHeight = Math.Max(380, Height - 80);
    }

    private void BeginOpenAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        PreviewCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        PreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        PreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private async Task CloseAnimatedAsync()
    {
        if (_isClosing) return;
        _isClosing = true;

        var duration = TimeSpan.FromMilliseconds(120);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        PreviewCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        PreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, duration) { EasingFunction = ease });
        PreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, duration) { EasingFunction = ease });

        await Task.Delay(duration);
        Close();
    }

    private void ShowImageError(string message)
    {
        ImageStatusText.Text = $"Couldn't load screenshot: {message}";
    }

    private async void OnCloseClicked(object sender, RoutedEventArgs e) => await CloseAnimatedAsync();

    private async void OnBackdropMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => await CloseAnimatedAsync();

    private void OnPreviewCardMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        await CloseAnimatedAsync();
    }

    private void OnOpenInBrowserClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_imageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowImageError(ex.Message);
        }
    }
}
