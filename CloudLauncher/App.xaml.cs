using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CloudLauncher.Services;
using Microsoft.Win32;

namespace CloudLauncher;

public partial class App : Application
{
    static App()
    {
        EnableModernIeRendering();
    }

    public static AppState State { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Backstop so an exception escaping an event handler degrades gracefully instead of
        // crashing the whole launcher (which may be mid Minecraft session). Individual call
        // sites should still guard their own known-failure paths; this is the safety net.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.LogError("UI", args.Exception);
            MessageBox.Show(
                $"Something went wrong:\n\n{args.Exception.Message}",
                "CloudLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true; // keep the app alive for recoverable UI faults
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.LogError("AppDomain", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.LogError("Task", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// Opt this executable's embedded WPF <c>WebBrowser</c> (IE legacy host) into modern
    /// behaviour via per-user IE feature-control keys. Set in the static ctor so it lands
    /// before any WebBrowser control is created (the values are read at first IE init).
    /// <list type="bullet">
    /// <item><c>FEATURE_BROWSER_EMULATION = 11001</c> — IE11 standards forced. Without it the
    /// control defaults to IE7 standards, which mangles <c>innerHTML</c> attribute ordering and
    /// drops CSS3 selectors like <c>:not()</c>, breaking the description editor's command blocks.</item>
    /// <item><c>FEATURE_96DPI_PIXEL = 1</c> — render at the system DPI instead of the legacy
    /// 96-DPI behaviour. Without it, on a scaled (125%/150%) display the control blew the embedded
    /// HTML up to an oversized, clipped scale a beat after each page finished loading.</item>
    /// </list>
    /// </summary>
    private static void EnableModernIeRendering()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath)) return;

            var exeName = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(exeName)) return;

            // 11001 = IE11 standards forced (ignore X-UA-Compatible / DOCTYPE quirks).
            SetIeFeatureControl("FEATURE_BROWSER_EMULATION", exeName, 11001);
            // 1 = scale the page for the system DPI instead of legacy 96-DPI assumptions.
            SetIeFeatureControl("FEATURE_96DPI_PIXEL", exeName, 1);
        }
        catch
        {
            // Best-effort: a per-user registry write may fail under locked-down policies.
        }
    }

    private static void SetIeFeatureControl(string feature, string exeName, int value)
    {
        var keyPath = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\" + feature;
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (key is null) return;
        if (key.GetValue(exeName) is int current && current == value) return;
        key.SetValue(exeName, value, RegistryValueKind.DWord);
    }
}
