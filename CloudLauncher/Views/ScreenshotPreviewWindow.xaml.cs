using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace CloudLauncher.Views;

/// <summary>
/// Full-size viewer for one screenshot — the instance's Screenshots tab, a resource pack's gallery,
/// a shader's preview images.
/// </summary>
/// <remarks>
/// It takes the rest of the set as well as the one that was clicked, so the arrows walk the folder
/// without closing and reopening: looking at screenshots is looking at all of them, and going back
/// to the grid for every next one is the part that felt broken. For local files it also offers the
/// things one wants a picture for — the clipboard, a copy elsewhere, and Explorer.
/// </remarks>
public partial class ScreenshotPreviewWindow : Window
{
    private readonly List<(string Url, string Title)> _items = new();
    private int _index;
    private bool _isClosing;

    public ScreenshotPreviewWindow(string imageUrl, string? title,
        IReadOnlyList<(string Url, string Title)>? siblings = null, int index = 0)
    {
        InitializeComponent();

        if (siblings is { Count: > 0 })
        {
            _items.AddRange(siblings);
            _index = index >= 0 && index < siblings.Count ? index : 0;
        }
        else
        {
            _items.Add((imageUrl, title ?? "Screenshot"));
            _index = 0;
        }

        ApplyCurrent();
    }

    public static void ShowFor(Window? owner, string imageUrl, string? title,
        IReadOnlyList<(string Url, string Title)>? siblings = null, int index = 0)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return;

        var window = new ScreenshotPreviewWindow(imageUrl, title, siblings, index);
        if (owner is not null)
        {
            window.Owner = owner;
            window.SizeToOwner();
        }

        window.ShowDialog();
    }

    private string CurrentUrl => _items[_index].Url;

    /// <summary>The local file behind the current image, or null when it is a remote URL (a store
    /// gallery image). Everything that writes, copies or reveals a file is gated on this.</summary>
    private string? CurrentFilePath
    {
        get
        {
            try
            {
                var uri = new Uri(CurrentUrl, UriKind.Absolute);
                return uri.IsFile && File.Exists(uri.LocalPath) ? uri.LocalPath : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Points the window at <see cref="_index"/> — title, picture, footer and which of the
    /// file-only actions make sense for it.</summary>
    private void ApplyCurrent()
    {
        var (url, title) = _items[_index];
        var displayTitle = string.IsNullOrWhiteSpace(title) ? "Screenshot" : title.Trim();
        Title = displayTitle;
        TitleText.Text = displayTitle;
        UrlText.Text = CurrentFilePath ?? url;
        ImageStatusText.Text = "";

        var hasSiblings = _items.Count > 1;
        PreviousButton.Visibility = hasSiblings ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = hasSiblings ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _items.Count - 1;
        HintText.Text = hasSiblings
            ? $"{_index + 1} of {_items.Count} · ← → to step through · Esc to close"
            : "Press Esc or click outside the image to close.";

        var isLocal = CurrentFilePath is not null;
        CopyImageButton.IsEnabled = isLocal;
        SaveCopyButton.IsEnabled = isLocal;
        // "Open in browser" on a local PNG launches a web browser on a file, which is not what
        // anyone wants from a screenshot; for a local file it becomes Explorer instead.
        OpenExternallyButton.Content = isLocal ? "Show in Explorer" : "Open in browser";
        OpenExternallyButton.ToolTip = isLocal
            ? "Open the containing folder with this file selected"
            : "Open this image in your browser";

        LoadPreviewImage();
    }

    private void Step(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _items.Count) return;
        _index = next;
        ApplyCurrent();
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
            image.UriSource = new Uri(CurrentUrl, UriKind.Absolute);
            // A local file is read and closed, so stepping to the next screenshot does not leave a
            // handle on the last one — which is what stops it being deleted or renamed afterwards.
            image.CacheOption = CurrentFilePath is not null ? BitmapCacheOption.OnLoad : BitmapCacheOption.Default;
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

    private void OnPreviousClicked(object sender, RoutedEventArgs e) => Step(-1);
    private void OnNextClicked(object sender, RoutedEventArgs e) => Step(1);

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                await CloseAnimatedAsync();
                return;
            case Key.Left or Key.Up or Key.PageUp:
                e.Handled = true;
                Step(-1);
                return;
            case Key.Right or Key.Down or Key.PageDown or Key.Space:
                e.Handled = true;
                Step(1);
                return;
            case Key.Home when _items.Count > 1:
                e.Handled = true;
                _index = 0;
                ApplyCurrent();
                return;
            case Key.End when _items.Count > 1:
                e.Handled = true;
                _index = _items.Count - 1;
                ApplyCurrent();
                return;
        }
    }

    private void OnCopyImageClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PreviewImage.Source is not BitmapSource source)
            {
                ShowImageError("the image has not finished loading.");
                return;
            }
            Clipboard.SetImage(source);
            ImageStatusText.Text = "";
        }
        catch (Exception ex)
        {
            ShowImageError(ex.Message);
        }
    }

    private void OnSaveCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentFilePath is not { } path) return;

            var extension = Path.GetExtension(path);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save a copy",
                FileName = Path.GetFileName(path),
                DefaultExt = extension,
                AddExtension = true,
                Filter = $"Image (*{extension})|*{extension}|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;

            File.Copy(path, dialog.FileName, overwrite: true);
            UrlText.Text = "Saved a copy to " + dialog.FileName;
        }
        catch (Exception ex)
        {
            ShowImageError(ex.Message);
        }
    }

    private void OnOpenExternallyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentFilePath is { } path)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }

            Process.Start(new ProcessStartInfo(CurrentUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowImageError(ex.Message);
        }
    }
}
