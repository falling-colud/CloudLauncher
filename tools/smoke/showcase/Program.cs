using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CloudLauncher;
using CloudLauncher.Services;

namespace Showcase;

/// <summary>Renders the real launcher off-screen against a fake local server and a fictional
/// account, for checking layouts and for the website's screenshots. Windows are parked at -24000
/// and never activated, and the real profile, server and stores are never touched.</summary>
public static class Program
{
    public const string ProfileName = "showcase";
    public const int Port = 5088;
    private static string _out = "";
    private static StreamWriter? _log;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static string ProfileDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudLauncher", ProfileName);
    public static string PacksRoot => Path.Combine(AppContext.BaseDirectory, "showcase-packs");

    [STAThread]
    public static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("CL_PROFILE", ProfileName);
        Environment.SetEnvironmentVariable("CL_CURSEFORGE_BASE", "http://127.0.0.1:9");   // never the real store
        _out = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        var scenario = args.Length > 1 ? args[1] : "sharing";
        Directory.CreateDirectory(_out);
        _log = new StreamWriter(Path.Combine(_out, $"log-{scenario}.txt"), append: false) { AutoFlush = true };

        try
        {
            Seed.Profile(ProfileDir, PacksRoot, Port);
            StartFakeServer();

            // Not `new App()`: its constructor queues OnStartup and StartupUri for the first
            // dispatcher turn even without Run(), which opens a MainWindow on screen.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/CloudLauncher;component/Themes/Palette.xaml") });
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/CloudLauncher;component/Themes/Controls.xaml") });
            resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse("""
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                    xmlns:anim="clr-namespace:CloudLauncher.Animations;assembly=CloudLauncher">
                    <Style TargetType="Window">
                        <Setter Property="Background" Value="{DynamicResource Surface1Brush}"/>
                        <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
                        <Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
                        <Setter Property="FontSize" Value="13"/>
                        <Setter Property="TextOptions.TextFormattingMode" Value="Ideal"/>
                        <Setter Property="TextOptions.TextRenderingMode" Value="ClearType"/>
                    </Style>
                    <Style TargetType="Page">
                        <Setter Property="Background" Value="{DynamicResource Surface0Brush}"/>
                        <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
                        <Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
                    </Style>
                    <Style TargetType="UserControl">
                        <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
                        <Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
                        <Setter Property="FontSize" Value="13"/>
                    </Style>
                </ResourceDictionary>
                """));
            app.Resources = resources;
            Pump(50);
            if (Application.Current.Windows.Count != 0) throw new InvalidOperationException("a window exists before the harness made one");
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
            {
                if (s is Window w && w.Left > -10000) { Log("PARKING stray window " + w.GetType().Name); w.Left = -24000; w.Top = -24000; }
            }));
            app.DispatcherUnhandledException += (_, e) => { Log("UNHANDLED(dispatcher) " + e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UNHANDLED(domain) " + e.ExceptionObject);
            ThemeService.ApplyLook(App.State.Settings);
            Log($"app ready; look={App.State.Settings.Look.Style}/{App.State.Settings.Look.Skin} accent={App.State.Settings.Look.Accent}");

            Scenarios.Run(scenario);
            Log("done");
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED " + ex);
            return 1;
        }
    }

    private static readonly object LogGate = new();
    public static void Log(string s) { lock (LogGate) _log?.WriteLine($"{Clock.Elapsed.TotalMilliseconds,9:0.0} {s}"); }
    public static string Out => _out;

    // ── fake server ──────────────────────────────────────────────────────────

    private static void StartFakeServer()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    var path = ctx.Request.Url!.AbsolutePath.Trim('/');
                    await Task.Delay(60);
                    object? answer = null;
                    try { answer = FakeWorld.Answer(ctx.Request.HttpMethod, path, ctx.Request.QueryString); }
                    catch (Exception ex) { Log("server threw on /" + path + ": " + ex.Message); }
                    var status = answer is null ? 404 : 200;
                    var body = answer is null ? "{\"error\":\"not found\"}" : JsonSerializer.Serialize(answer, answer.GetType(), FakeWorld.Json);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    try { await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close(); } catch { }
                    Log($"server {ctx.Request.HttpMethod} /{path}{ctx.Request.Url.Query} -> {status}");
                });
            }
        });
    }

    // ── pumping and rendering ────────────────────────────────────────────────

    public static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>Renders an element to a PNG at <paramref name="scale"/> times its size.</summary>
    public static void Shoot(FrameworkElement element, string name, double scale = 2)
    {
        var w = (int)Math.Ceiling(element.ActualWidth * scale);
        var h = (int)Math.Ceiling(element.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(element);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var path = Path.Combine(_out, name + ".png");
        using (var fs = File.Create(path)) enc.Save(fs);
        Log($"shot {path} {w}x{h}");
    }

    public static MainWindow MakeShell(double w, double h)
    {
        var shell = new MainWindow
        {
            Left = -24000, Top = -24000, Width = w, Height = h,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        return shell;
    }

    public static void Click(FrameworkElement root, string name)
    {
        if (root.FindName(name) is ButtonBase b) b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
        else Log("no button " + name);
    }

    public static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Find<T>(c)) yield return d;
        }
    }
}
