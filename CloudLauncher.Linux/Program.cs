using Avalonia;
using Avalonia.Threading;
using CloudLauncher.Services;

namespace CloudLauncher;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Log("crash", "Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Log("crash", "Unobserved task: " + e.Exception);
            e.SetObserved();
        };

        if (args.Contains("--version"))
        {
            Console.WriteLine($"CloudLauncher {AppVersion.CurrentString} (Linux)");
            return 0;
        }

        // One launcher per profile: a second start shows the first and leaves.
        if (!LinuxSingleInstance.Claim()) return 0;

        // The shared services raise their events on the UI thread through these.
        UiThread.Post = action => Dispatcher.UIThread.Post(action);
        UiThread.HasAccess = () => Dispatcher.UIThread.CheckAccess();

        // Microsoft sign-in by device code: there is no embedded browser here.
        MinecraftAccountService.OAuthProviderOverride = DeviceCodeSignIn.CreateProvider();

        AppLog.Log("app", $"CloudLauncher {AppVersion.CurrentString} for Linux starting ({Environment.OSVersion}, "
                          + $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}).");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The window class the desktop entry names (StartupWMClass), so docks group the window with it.
            .With(new X11PlatformOptions { WmClass = "cloudlauncher" })
            .WithInterFont()
            .LogToTrace();
}
