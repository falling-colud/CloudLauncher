using System.Runtime.InteropServices;

namespace CloudLauncher.Views;

[ComVisible(true)]
public sealed class DescriptionEditorScriptHost
{
    private readonly Action _onChanged;

    public DescriptionEditorScriptHost(Action onChanged) => _onChanged = onChanged;

    public void NotifyChanged() => _onChanged();
}
