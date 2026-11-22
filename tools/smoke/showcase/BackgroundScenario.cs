using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher;
using CloudLauncher.Services;

namespace Showcase;

/// <summary>
/// The background picture (Settings, Appearance): imports CL_BG_PICTURE through the real path, shoots
/// the Instances page with Settings open beside it (sharp, blurred, Fit, Tile, Vanilla, Classic), and
/// checks the brushes: the palette's own objects without a picture, see-through with one, and back
/// again for a missing file or after Remove. A file that is not a picture must be refused, and a phone
/// photo stored on its side must come out upright. Results are PASS/FAIL lines in the log.
/// </summary>
public static class BackgroundScenario
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Program.Log($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}");
    }

    private static T Await<T>(Task<T> task)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < until) Program.Pump(40);
        return task.GetAwaiter().GetResult();
    }

    private static bool WaitFor(Func<bool> condition, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until) Program.Pump(40);
        return condition();
    }

    public static void Run()
    {
        var picture = Environment.GetEnvironmentVariable("CL_BG_PICTURE") ?? throw new InvalidOperationException("set CL_BG_PICTURE");
        var settings = App.State.Settings;
        var res = Application.Current.Resources;
        BackgroundImageService.RemoveOthers(null);
        int pw, ph;
        using (var probe = File.OpenRead(picture))
        {
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            (pw, ph) = (frame.PixelWidth, frame.PixelHeight);
        }
        Program.Log($"picture {pw}x{ph}");

        // ── no picture: every brush is the palette's own object ──
        settings.BackgroundImage = new BackgroundImageSettings();
        ThemeService.ApplyLook(settings);
        Check("off: pages paint Surface0Brush itself", ReferenceEquals(res["PageBackgroundBrush"], res["Surface0Brush"]));
        Check("off: sidebar paints Surface1Brush itself", ReferenceEquals(res["SidebarBrush"], res["Surface1Brush"]));
        Check("off: title bar paints TitleBarBrush itself", ReferenceEquals(res["WindowTitleBarBrush"], res["TitleBarBrush"]));
        Check("off: status bars paint Surface1Brush itself", ReferenceEquals(res["StatusBarBrush"], res["Surface1Brush"]));
        Check("off: no picture, no wash", res["BackdropImageBrush"] is null
            && res["BackdropScrimBrush"] is SolidColorBrush { Color.A: 0 });

        // ── refused: not a picture, and the one in use stays ──
        var junk = Path.Combine(Program.Out, "not-a-picture.png");
        File.WriteAllText(junk, "this is text, not a picture");
        try
        {
            Await(BackgroundImageService.ImportAsync(junk));
            Check("a text file named .png is refused", false, "it was accepted");
        }
        catch (InvalidDataException ex) { Check("a text file named .png is refused", true); Program.Log("   message: " + ex.Message); }
        Check("...and nothing was copied", !Directory.Exists(BackgroundImageSettings.Folder)
            || Directory.GetFiles(BackgroundImageSettings.Folder).Length == 0);

        // ── a phone photo stored on its side (EXIF orientation 6) comes out upright ──
        var sideways = Path.Combine(Program.Out, "sideways.jpg");
        WriteSidewaysJpeg(sideways, 300, 100);
        var sidewaysName = Await(BackgroundImageService.ImportAsync(sideways));
        var turned = BackgroundImageService.Preview(new BackgroundImageSettings { File = sidewaysName });
        Check("EXIF orientation 6 is turned upright", turned is { PixelWidth: 100, PixelHeight: 300 },
            turned is null ? "no preview" : $"{turned.PixelWidth}x{turned.PixelHeight}");
        BackgroundImageService.RemoveOthers(null);

        // ── the real picture ──
        var name = Await(BackgroundImageService.ImportAsync(picture));
        Check("import copies into the profile's backgrounds folder", File.Exists(Path.Combine(BackgroundImageSettings.Folder, name)), name);
        settings.BackgroundImage.File = name;
        settings.BackgroundImage.Source = Path.GetFileName(picture);
        settings.Save();
        ThemeService.ApplyLook(settings);
        Check("on: picture brush present at once (the import kept its decode)", res["BackdropImageBrush"] is ImageBrush);
        Check("on: pages see-through", res["PageBackgroundBrush"] is SolidColorBrush { Color.A: 0 });
        Check("on: sidebar partly see-through", res["SidebarBrush"] is Brush { Opacity: > 0.3 and < 0.9 });
        Check("on: wash at the Dim strength", res["BackdropScrimBrush"] is SolidColorBrush s
            && s.Color.A == (byte)Math.Round(BackgroundImageSettings.DefaultDim / 100.0 * 255), (res["BackdropScrimBrush"] as SolidColorBrush)?.Color.ToString() ?? "null");
        Check("on: the picture keeps its own size in DIPs", res["BackdropImageBrush"] is ImageBrush { ImageSource: BitmapSource b }
            && Math.Abs(b.Width - pw) < 2 && Math.Abs(b.Height - ph) < 2,
            res["BackdropImageBrush"] is ImageBrush { ImageSource: BitmapSource bb } ? $"{bb.Width}x{bb.Height} px {bb.PixelWidth}x{bb.PixelHeight}" : "");

        var shell = Program.MakeShell(1280, 820);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
        shell.Show();
        Program.Pump(2500);
        if (shell.FindName("MainFrame") is Frame { Content: Page instances })
        {
            Program.Click(instances, "LayoutCardsButton");
            Program.Pump(900);
        }
        Program.Shoot((FrameworkElement)shell.Content, "bg-instances", 1);

        shell.OpenSettings();
        Program.Pump(2500);
        var settingsPage = ((Frame)shell.FindName("SideFrame")).Content as Page;
        if (settingsPage?.FindName("BackgroundOptionsPanel") is FrameworkElement options)
        {
            options.BringIntoView();
            Program.Pump(800);
            Check("settings: options shown while a picture is set", options.Visibility == Visibility.Visible);
            Check("settings: preview shows the picture", settingsPage.FindName("BackgroundPreview") is Image { Source: not null });
            Program.Log("   hint: " + (settingsPage.FindName("BackgroundHint") as TextBlock)?.Text);
        }
        else Check("settings page has the background section", false);
        Program.Shoot((FrameworkElement)shell.Content, "bg-settings", 1);

        // Blur through the slider, the way a user changes it.
        if (settingsPage?.FindName("BackgroundBlurSlider") is Slider blur)
        {
            blur.Value = 6;
            Check("blur 6: saved", settings.BackgroundImage.Blur == 6);
            Check("blur 6: the sharp picture stays up while the blurred one is made",
                res["BackdropImageBrush"] is ImageBrush);
            var ok = WaitFor(() => res["BackdropImageBrush"] is ImageBrush { ImageSource: BitmapSource bs } && bs.PixelWidth < pw);
            Check("blur 6: blurred picture arrives (smaller, same size in the window)", ok
                && res["BackdropImageBrush"] is ImageBrush { ImageSource: BitmapSource bs2 } && Math.Abs(bs2.Width - pw) < 30,
                res["BackdropImageBrush"] is ImageBrush { ImageSource: BitmapSource bs3 } ? $"{bs3.PixelWidth}x{bs3.PixelHeight} as {bs3.Width:0}x{bs3.Height:0}" : "");
            Program.Pump(300);
            Program.Shoot((FrameworkElement)shell.Content, "bg-blur6", 1);
            blur.Value = 0;
            Check("blur back to 0: sharp picture at once", res["BackdropImageBrush"] is ImageBrush { ImageSource: BitmapSource sharp } && ReferenceEquals(sharp, BackgroundImageService.Preview(settings.BackgroundImage)));
        }
        if (settingsPage?.FindName("BackgroundDimSlider") is Slider dim)
        {
            dim.Value = 80;
            Check("dim 80: wash follows", res["BackdropScrimBrush"] is SolidColorBrush { Color.A: 204 });
            // A render needs a layout pass after the change, or it shows the frame before.
            Program.Pump(300);
            Program.Shoot((FrameworkElement)shell.Content, "bg-dim80", 1);
            dim.Value = BackgroundImageSettings.DefaultDim;
        }

        // Fit and Tile, then the other looks.
        settings.BackgroundImage.Fit = BackgroundImageSettings.FitFit;
        ThemeService.ApplyBackdrop(settings);
        Program.Pump(300);
        Program.Shoot((FrameworkElement)shell.Content, "bg-fit", 1);
        settings.BackgroundImage.Fit = BackgroundImageSettings.FillFit;
        settings.BackgroundImage.Blur = 3;

        settings.Look.Skin = LookSettings.VanillaSkin;
        ThemeService.ApplyLook(settings);
        WaitFor(() => res["BackdropImageBrush"] is ImageBrush { ImageSource: BitmapSource vb } && vb.PixelWidth < pw);
        Check("vanilla: pages see-through over the stone", res["PageBackgroundBrush"] is SolidColorBrush { Color.A: 0 });
        Program.Pump(300);
        Program.Shoot((FrameworkElement)shell.Content, "bg-vanilla", 1);

        settings.Look.Skin = LookSettings.DarkSkin;
        settings.Look.Style = LookSettings.ClassicStyle;
        ThemeService.ApplyLook(settings);
        Check("classic: title bar keeps its gradient, see-through", res["WindowTitleBarBrush"] is LinearGradientBrush { Opacity: < 0.9 });
        Program.Pump(300);
        Program.Shoot((FrameworkElement)shell.Content, "bg-classic", 1);
        settings.Look.Style = LookSettings.SlateStyle;
        settings.BackgroundImage.Blur = 0;
        ThemeService.ApplyLook(settings);

        // Every other page with the picture behind it, master pages alone and the instance page beside.
        shell.CloseSidePanel();
        Program.Pump(600);
        foreach (var nav in new[] { "NavMods", "NavWorlds", "NavResourcePacks", "NavShaders", "NavServers", "NavConfigs", "NavStorage", "NavSharing" })
        {
            Program.Click(shell, nav);
            Program.Pump(2200);
            Program.Shoot((FrameworkElement)shell.Content, "tour-" + nav[3..].ToLowerInvariant(), 1);
        }
        Program.Click(shell, "NavPacks");
        Program.Pump(1500);
        shell.OpenPackDetail(FakeWorld.Hearthstone, "Hearthstone Valley");
        Program.Pump(3000);
        Program.Shoot((FrameworkElement)shell.Content, "tour-instance", 1);
        shell.OpenAccount();
        Program.Pump(2000);
        Program.Shoot((FrameworkElement)shell.Content, "tour-account", 1);
        shell.OpenSettings();
        Program.Pump(2000);
        settingsPage = ((Frame)shell.FindName("SideFrame")).Content as Page;

        // ── the copy disappears: the plain look comes back, with a reason ──
        settings.BackgroundImage.File = "background-gone.png";
        ThemeService.ApplyBackdrop(settings);
        WaitFor(() => BackgroundImageService.Get(settings.BackgroundImage).Error is not null);
        var gone = BackgroundImageService.Get(settings.BackgroundImage);
        Check("missing copy: an error for Settings", gone.Error is not null, gone.Pending ? "still pending" : "no error");
        Check("missing copy: plain look again", ReferenceEquals(res["PageBackgroundBrush"], res["Surface0Brush"]));
        Program.Log("   message: " + gone.Error);

        // ── Remove ──
        settings.BackgroundImage.File = name;
        ThemeService.ApplyBackdrop(settings);
        if (settingsPage is not null) Program.Click(settingsPage, "RemoveBackgroundButton");
        Program.Pump(300);
        Check("remove: setting cleared", !settings.BackgroundImage.IsOn);
        Check("remove: the copy is deleted", !Directory.Exists(BackgroundImageSettings.Folder)
            || Directory.GetFiles(BackgroundImageSettings.Folder).Length == 0);
        Check("remove: original untouched", File.Exists(picture));
        Check("remove: plain look", ReferenceEquals(res["PageBackgroundBrush"], res["Surface0Brush"])
            && res["BackdropImageBrush"] is null);
        Program.Shoot((FrameworkElement)shell.Content, "bg-removed", 1);

        shell.Hide();
        Program.Pump(300);
        Program.Log(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
    }

    /// <summary>A wide JPEG whose EXIF says it must be turned 90° clockwise to be seen upright.</summary>
    private static void WriteSidewaysJpeg(string path, int w, int h)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            pixels[i * 4] = (byte)(i % w * 255 / w);
            pixels[i * 4 + 1] = 120;
            pixels[i * 4 + 2] = (byte)(i / w * 255 / h);
            pixels[i * 4 + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        var meta = new BitmapMetadata("jpg");
        meta.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp, null, meta, null));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
