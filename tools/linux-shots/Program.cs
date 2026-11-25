using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Views;

namespace LinuxShots;

/// <summary>
/// Shoots the Linux launcher's window, page by page, with Avalonia's headless platform (Skia drawing, no
/// window anywhere). Usage: <c>ClLinuxShots &lt;out dir&gt; &lt;scenario&gt;[,&lt;scenario&gt;...]</c>.
/// The profile comes from CL_PROFILE (default "showcase", which the showcase fake server seeds).
/// </summary>
/// <remarks>Scenarios are registered in <see cref="Scenarios"/>; each gets the shell and writes as many
/// PNGs as it likes through <see cref="Shoot"/>. Every line of the log is flushed at once, so a run that
/// dies still leaves its log.</remarks>
public static class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter? _log;
    public static string Out { get; private set; } = "";
    public static MainWindow Shell { get; private set; } = null!;

    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("CL_PROFILE") is not { Length: > 0 })
            Environment.SetEnvironmentVariable("CL_PROFILE", "showcase");
        Out = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
        var scenarios = (args.Length > 1 ? args[1] : "shell").Split(',', StringSplitOptions.RemoveEmptyEntries);
        Directory.CreateDirectory(Out);
        _log = new StreamWriter(Path.Combine(Out, "log.txt"), append: false) { AutoFlush = true };

        try
        {
            UiThread.Post = action => Dispatcher.UIThread.Post(action);
            UiThread.HasAccess = () => Dispatcher.UIThread.CheckAccess();
            MinecraftAccountService.OAuthProviderOverride = DeviceCodeSignIn.CreateProvider();

            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            // The fake server seeds the session with the Windows launcher's DPAPI secret store, which the
            // Linux build cannot read: give it the same fake session in its own.
            if (!App.State.Api.IsSignedIn && Environment.GetEnvironmentVariable("CL_SMOKE_SIGNED_OUT") != "1"
                && App.State.Settings.UserId is { } uid)
                App.State.Api.SetTokens(new CloudLauncher.Shared.TokenResponse("showcase-access", "showcase-refresh",
                    DateTimeOffset.UtcNow.AddYears(50), App.State.Settings.Username ?? "mara", uid));
            Log($"profile {AppSettings.DataRootPath}, server {App.State.Settings.ServerUrl}, signed in {App.State.Api.IsSignedIn}");
            Shell = new MainWindow { Width = 1280, Height = 800 };
            Shell.Show();
            Pump(2500);

            foreach (var name in scenarios)
            {
                Log("== " + name);
                try { Scenarios.Run(name); }
                catch (Exception ex) { Log("FAIL " + name + ": " + ex); }
            }
            Log("done");
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED " + ex);
            return 1;
        }
    }

    public static void Log(string s)
    {
        var line = $"{Clock.Elapsed.TotalMilliseconds,9:0.0} {s}";
        Console.WriteLine(line);
        _log?.WriteLine(line);
    }

    /// <summary>Runs the dispatcher's own loop for <paramref name="ms"/>: async loads finish, bindings
    /// settle, DispatcherTimers tick (progress coalescing, toasts, live logs) and frames are drawn.</summary>
    /// <remarks>RunJobs alone drains queued work but never fires timers; the main loop does both. The
    /// headless renderer only draws when its render timer is pushed, hence the 16 ms ticker.</remarks>
    public static void Pump(int ms)
    {
        using var stop = new CancellationTokenSource(ms);
        var ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
        ticker.Start();
        try { Dispatcher.UIThread.MainLoop(stop.Token); }
        finally { ticker.Stop(); }
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Saves the window as it is now to <c>&lt;out&gt;/&lt;name&gt;.png</c>.</summary>
    public static void Shoot(string name)
    {
        Pump(200);
        var frame = Shell.CaptureRenderedFrame();
        if (frame is null) { Log("no frame for " + name); return; }
        var path = Path.Combine(Out, name + ".png");
        frame.Save(path);
        Log("shot " + path);
    }

    /// <summary>Every control of type <typeparamref name="T"/> under <paramref name="root"/>.</summary>
    public static IEnumerable<T> Find<T>(Control root) where T : Control =>
        root.GetLogicalDescendants().OfType<T>();
}
