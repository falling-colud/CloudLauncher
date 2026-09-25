namespace CloudLauncher;

/// <summary>
/// Implemented by a master <see cref="System.Windows.Controls.Page"/> that can re-run its own load
/// in place, which is what the offline banner's Retry asks for.
/// </summary>
/// <remarks>
/// Optional. Pages without it are rebuilt from scratch by <see cref="MainWindow"/>, which loses
/// scroll position and selection. The page decides which of its panes need reloading.
/// </remarks>
public interface IRefreshablePage
{
    Task RefreshAsync();
}
