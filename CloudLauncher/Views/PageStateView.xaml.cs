using System.Windows;
using System.Windows.Controls;

namespace CloudLauncher.Views;

/// <summary>
/// The panel a page shows instead of, or on top of, its list while it is loading, has nothing to
/// show, failed, or is offline.
/// </summary>
/// <remarks>
/// <para>Don't drive this directly: it is the presentation half of <see cref="PageState"/>, which
/// owns the rules (never Empty while Loading, no placeholder zero, Refreshing keeps the data).</para>
/// <para>Place it as the last child of the page's content Grid cell, beside the list:
/// <c>&lt;v:PageStateView x:Name="PageStateHost"/&gt;</c>. It draws nothing until
/// <see cref="PageState"/> tells it to.</para>
/// </remarks>
public partial class PageStateView : UserControl
{
    public PageStateView() => InitializeComponent();

    /// <summary>The Retry button on the Error and Offline panels.</summary>
    public event Action? RetryRequested;

    /// <summary>The Cancel button under the loading bar. It only appears when something is listening,
    /// so an un-cancellable scan shows no dead button.</summary>
    public event Action? CancelRequested;

    /// <summary>The optional action button on the Empty panel ("Get shaders", "Create one...").</summary>
    public event Action? EmptyActionRequested;

    /// <summary>Shows one panel, or none. <paramref name="takesInput"/> is true when a panel with a
    /// button is up; otherwise clicks pass through to the list underneath during a refresh.</summary>
    private void Present(UIElement? panel, bool topBar, bool takesInput)
    {
        BusyPanel.Visibility = panel == BusyPanel ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = panel == EmptyPanel ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = panel == ErrorPanel ? Visibility.Visible : Visibility.Collapsed;
        OfflinePanel.Visibility = panel == OfflinePanel ? Visibility.Visible : Visibility.Collapsed;
        TopBar.Visibility = topBar ? Visibility.Visible : Visibility.Collapsed;
        Root.IsHitTestVisible = takesInput;
    }

    /// <summary>Nothing at all: the page is showing real content.</summary>
    public void ShowNothing() => Present(null, topBar: false, takesInput: false);

    /// <summary>Work in flight with data already on screen: just a hairline at the top of the list, so
    /// a list that is still correct isn't blanked.</summary>
    public void ShowRefreshing() => Present(null, topBar: true, takesInput: false);

    /// <summary>Work in flight with no data yet.</summary>
    /// <param name="detail">The per-item line ("Scanning My Pack..."), or null.</param>
    /// <param name="cancellable">Draws a Cancel button under the bar.</param>
    public void ShowBusy(string line, string? detail, bool cancellable)
    {
        BusyLine.Text = line;
        BusyDetail.Text = detail ?? "";
        BusyDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        BusyCancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        Present(BusyPanel, topBar: false, takesInput: cancellable);
    }

    /// <summary>Relabels the busy panel without re-entering the state, so progress does not restart
    /// the bar's animation on every instance.</summary>
    public void UpdateBusy(string line, string? detail)
    {
        BusyLine.Text = line;
        BusyDetail.Text = detail ?? "";
        BusyDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowEmpty(string glyph, string title, string body, string? actionLabel)
    {
        EmptyGlyph.Text = glyph;
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyBody.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        EmptyActionButton.Content = actionLabel ?? "";
        EmptyActionButton.Visibility = actionLabel is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        Present(EmptyPanel, topBar: false, takesInput: true);
    }

    public void ShowError(string title, string body)
    {
        ErrorTitle.Text = title;
        ErrorBody.Text = body;
        ErrorBody.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        Present(ErrorPanel, topBar: false, takesInput: true);
    }

    public void ShowOffline(string title, string body)
    {
        OfflineTitle.Text = title;
        OfflineBody.Text = body;
        OfflineBody.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        Present(OfflinePanel, topBar: false, takesInput: true);
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void OnEmptyActionClick(object sender, RoutedEventArgs e) => EmptyActionRequested?.Invoke();
}
