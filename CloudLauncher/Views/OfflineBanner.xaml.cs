using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using CloudLauncher.Animations;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>The launcher's single "you are offline" line, shown by <see cref="MainWindow"/> between
/// the title bar and the content.</summary>
/// <remarks>One banner in the shell rather than one per page, so an outage is described once. Pages
/// still say what they show from cache; this says whether the server is reachable at all.</remarks>
public partial class OfflineBanner : UserControl
{
    // Dismissing only lasts for the current outage. It resets when the server answers, so the next
    // drop is shown again.
    private bool _dismissed;
    private bool _hiding;

    /// <summary>Raised when the user clicks Retry. The shell decides what "retry" means.</summary>
    public event Action? RetryRequested;

    public OfflineBanner()
    {
        InitializeComponent();
    }

    /// <summary>Re-reads <see cref="AppState.IsOffline"/> and shows or hides the banner. Safe to
    /// call any number of times.</summary>
    public void Sync()
    {
        if (!App.State.IsOffline)
        {
            _dismissed = false;
            HideBanner();
            return;
        }

        MessageText.Text = Compose();
        if (_dismissed) return;
        ShowBanner();
    }

    /// <summary>Disables Retry (and says so) while the shell is re-running a page's load.</summary>
    public void SetBusy(bool busy)
    {
        RetryButton.IsEnabled = !busy;
        RetryButton.Content = busy ? "Retrying..." : "Retry";
    }

    /// <summary>"Offline - showing your last known data (the connection was refused). Last updated
    /// 3 hours ago." Clauses whose facts are missing are left out.</summary>
    private static string Compose()
    {
        var sb = new StringBuilder("Offline - showing your last known data");
        if (App.State.OfflineReason is { Length: > 0 } reason)
            sb.Append(" (").Append(reason).Append(')');
        sb.Append('.');
        if (PackListCache.AgeInWords() is { } age)
            sb.Append(" Last updated ").Append(age).Append('.');
        return sb.ToString();
    }

    private void ShowBanner()
    {
        // Cancel a hide that is mid-fade, or its Completed would collapse the banner we just showed.
        _hiding = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (Visibility == Visibility.Visible) return;

        Visibility = Visibility.Visible;
        Animate.SlideFadeIn(this, fromX: 0, fromY: -10, ms: 200);
    }

    private void HideBanner()
    {
        if (Visibility != Visibility.Visible || _hiding) return;

        // Fade, then collapse. Animating the height would re-layout the whole window every frame.
        _hiding = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return;           // superseded by a ShowBanner while we were fading
            _hiding = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            Visibility = Visibility.Collapsed;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        AppLog.Log("net", "Retry requested from the offline banner.");
        RetryRequested?.Invoke();
    }

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        _dismissed = true;
        HideBanner();
    }
}
