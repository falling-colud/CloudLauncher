namespace CloudLauncher;

/// <summary>
/// Implemented by a side-panel <see cref="System.Windows.Controls.Page"/> that wants first chance
/// at the Back button — to navigate its own internal state before the side panel pops. Return
/// true if Back was consumed; false to let the panel close/pop as usual.
/// </summary>
public interface ISidePanelBackHandler
{
    bool TryHandleBack();
}
