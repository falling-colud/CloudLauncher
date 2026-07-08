using System.Windows;
using System.Windows.Controls;

namespace CloudLauncher.Controls;

/// <summary>
/// Tab header panel that wraps like <see cref="TabPanel"/> but keeps tab order stable
/// when the selected tab changes (WPF's TabPanel moves the selected tab to the front).
/// </summary>
public class StableTabPanel : WrapPanel
{
    public StableTabPanel()
    {
        Orientation = Orientation.Horizontal;
    }
}
