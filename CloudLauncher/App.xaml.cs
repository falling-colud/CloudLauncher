using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using CloudLauncher.Services;
using Microsoft.Win32;

namespace CloudLauncher;

public partial class App : Application
{
    // Declared before State: static initialisers run in order, so a crash while the shared
    // state below is being built still reaches the log file.
    internal static readonly bool CrashLoggingInstalled = InstallCrashLogging();

    public static AppState State { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before anything can save settings: a second launcher on the same profile would write its
        // own copy over this one's (see SingleInstance). The running copy has been asked to come to the
        // front, so this one exits without opening a window.
        if (!SingleInstance.Claim())
        {
            StartupUri = null;
            Shutdown();
            return;
        }
        SingleInstance.ListenForActivation(() => Dispatcher.BeginInvoke(BringMainWindowToFront));

        base.OnStartup(e);

        // Backstop so an exception escaping an event handler doesn't crash the whole launcher (which
        // may be mid Minecraft session). Call sites should still guard their own known failures.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.LogError("UI", args.Exception);
            AppLog.Flush();
            MessageBox.Show(
                $"Something went wrong:\n\n{args.Exception.Message}\n\n"
                + "The details are in the launcher log (Settings, About, Open logs folder).",
                "CloudLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true; // keep the app alive for recoverable UI faults
        };

        UseTheSystemLocaleForDatesAndTimes();
        RemoveLegacyBrowserSettings();

        // Apply the user's look (Slate by default, or Classic with their colours) before the first
        // window is shown, so nothing paints in the stock palette first. It swallows its own errors so a
        // theme problem can't stop the launcher from opening.
        ThemeService.ApplyLook(State.Settings);
        UiSounds.Install();

        WarmRememberedScans();
        MigrateManualContentCompatibility();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Flush();
        base.OnExit(e);
    }

    /// <summary>
    /// Sends exceptions that nothing else catches to the log, and gets them onto disk before the
    /// process goes.
    /// </summary>
    /// <remarks>Runs from a static initialiser ahead of <see cref="State"/>, because building the shared
    /// state can fail before OnStartup is reached. The UI-thread handler needs the Application and is
    /// added in OnStartup.</remarks>
    private static bool InstallCrashLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.LogError("AppDomain", ex);
            // The process is going down; the background writer will not get another turn.
            AppLog.Flush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.LogError("Task", args.Exception);
            args.SetObserved();
        };
        return true;
    }

    /// <summary>Shows the main window when a second copy of the launcher was started.</summary>
    /// <remarks>Restores through the system command instead of setting <c>WindowState</c>, so a window
    /// that was maximized before it was minimized comes back maximized. Toggling Topmost wins the
    /// foreground when Activate alone is refused.</remarks>
    private void BringMainWindowToFront()
    {
        if (MainWindow is not { } w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(w);
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    /// <summary>
    /// Converts the pre-1.7.0 per-item "which instances may use this" lists into rules, once.
    /// </summary>
    /// <remarks>
    /// <para>Worlds and resource packs each had a <c>CompatibleWithAll</c> flag and a list of pack ids.
    /// Converted policies start switched off: the old flag meant "may use this", not "add this to every
    /// instance automatically", so the user decides whether to turn them on.</para>
    /// <para>Version-stamped in the library index so it only runs once. Runs off the UI thread and
    /// swallows its own errors so a migration can't stop the launcher from opening.</para>
    /// </remarks>
    private static void MigrateManualContentCompatibility() => Task.Run(() =>
    {
        try
        {
            var report = State.ContentDefaults.MigrateManualCompatibility();
            if (!report.AlreadyDone) AppLog.Log("content-defaults", report.Summary());
        }
        catch (Exception ex)
        {
            AppLog.LogError("content-defaults", ex);
        }
    });

    /// <summary>
    /// Reads the remembered folder scans off the UI thread, before any page asks for one.
    /// </summary>
    /// <remarks>A cache only joins the registry when its owner's static initialiser calls
    /// <c>ScanCaches.For</c>, so the owners' class constructors are run first; a new cache owner has to
    /// be added to the list below. Fire and forget: a cold cache only costs a slower first paint.</remarks>
    private static void WarmRememberedScans()
    {
        try
        {
            foreach (var owner in new[]
                     {
                         typeof(ConfigHubService),
                         typeof(Views.WorldsView),
                         typeof(Views.ResourcePacksView),
                         typeof(Views.ShaderPacksView),
                     })
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(owner.TypeHandle);

            _ = ScanCaches.PreloadAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError("scan-cache", ex);
        }
    }

    /// <summary>
    /// Points WPF's own language at the machine's locale, so anything the framework formats agrees
    /// with what <see cref="Services.TimeFormat"/> produces.
    /// </summary>
    /// <remarks>.NET already takes <see cref="CultureInfo.CurrentCulture"/> from Windows, but WPF's
    /// <c>FrameworkElement.Language</c> defaults to <c>en-US</c>, which a binding's <c>StringFormat</c>
    /// or a <c>DatePicker</c> would use. The current cultures are left alone.</remarks>
    private static void UseTheSystemLocaleForDatesAndTimes()
    {
        // A locale with no IETF tag, or a metadata override that has already happened on a second
        // App instance (the test harness constructs one), must not stop the launcher opening.
        try
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(
                    XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
        }
        catch (Exception ex)
        {
            AppLog.LogError("locale", ex);
        }
    }

    /// <summary>
    /// Deletes the per-user Internet Explorer feature-control values that older versions set for this
    /// exe to host the WebBrowser control, once per profile.
    /// </summary>
    /// <remarks>Nothing hosts that control now (descriptions use WebView2). A value is only removed if it
    /// still holds what we wrote, so one set by hand is left alone.</remarks>
    private static void RemoveLegacyBrowserSettings()
    {
        var settings = State.Settings;
        if (settings.LegacyBrowserKeysRemoved) return;
        try
        {
            var exeName = Path.GetFileName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(exeName))
            {
                DeleteFeatureControlValue("FEATURE_BROWSER_EMULATION", exeName, 11001);
                DeleteFeatureControlValue("FEATURE_96DPI_PIXEL", exeName, 1);
            }
        }
        catch (Exception ex)
        {
            // A locked-down registry: the values are harmless, so this is not worth retrying.
            AppLog.LogError("registry", ex);
        }

        settings.LegacyBrowserKeysRemoved = true;
        try { settings.Save(); }
        catch { /* read-only profile: it runs again next time, which is harmless */ }
    }

    private static void DeleteFeatureControlValue(string feature, string exeName, int written)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Internet Explorer\Main\FeatureControl\" + feature, writable: true);
        if (key?.GetValue(exeName) is int value && value == written)
            key.DeleteValue(exeName, throwOnMissingValue: false);
    }
}
