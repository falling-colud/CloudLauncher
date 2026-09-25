namespace CloudLauncher;

/// <summary>
/// Implemented by a side-panel <see cref="System.Windows.Controls.Page"/> that wants first go at the
/// Back button, e.g. to step back through its own state before the panel pops. Return true if Back
/// was handled, false to let the panel close as usual.
/// </summary>
public interface ISidePanelBackHandler
{
    bool TryHandleBack();
}
