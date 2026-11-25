using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CloudLauncher.Services;
using CloudLauncher.Views;

namespace CloudLauncher;

/// <summary>The Linux launcher. <see cref="State"/> is the same service set the Windows launcher builds,
/// and the shared services reach it by this name.</summary>
public partial class App : Application
{
    private static AppState? _state;

    /// <summary>Every service, built on first use (after <see cref="Program"/> has set up the UI thread
    /// hooks).</summary>
    public static AppState State => _state ??= new AppState();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            desktop.MainWindow = window;
            LinuxUpdater.CheckInBackground();
            desktop.Exit += (_, _) =>
            {
                try { State.ModPlans.Flush(); } catch { /* exiting */ }
                AppLog.Log("app", "Closed.");
            };
            LinuxSingleInstance.Listen(() => Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
