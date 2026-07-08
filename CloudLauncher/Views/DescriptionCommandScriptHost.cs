using System.Runtime.InteropServices;

namespace CloudLauncher.Views;

/// <summary>
/// Bridge exposed to the rich-description viewer as <c>window.external</c> so a
/// command link can run its command directly from a DOM click handler. This
/// avoids relying on the WPF WebBrowser's default anchor navigation, which a
/// hosted (inactive) IE control silently swallows on the click that activates
/// it — the cause of command links only firing intermittently / when spammed.
/// </summary>
[ComVisible(true)]
public sealed class DescriptionCommandScriptHost
{
    private readonly Action<string> _onCommand;

    public DescriptionCommandScriptHost(Action<string> onCommand) => _onCommand = onCommand;

    public void RunCommand(string? command) => _onCommand(command ?? "");
}
