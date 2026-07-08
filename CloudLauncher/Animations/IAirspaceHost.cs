namespace CloudLauncher.Animations;

/// <summary>
/// Implemented by controls that host a native, HWND-based ("airspace") surface — e.g. a WebView2 —
/// which cannot be moved by a WPF <c>RenderTransform</c>. During a slide transition such a surface
/// stays fixed and "teleports" to its final spot at the end, so the transition code asks the host
/// to hide the surface for the duration of the animation and reveal it again on completion.
/// Implementations must be idempotent and tolerate the surface not existing yet (it may be created
/// asynchronously partway through the transition).
/// </summary>
public interface IAirspaceHost
{
    /// <summary>Suppress (false) or allow (true) the native surface while keeping the WPF control in
    /// the layout. When allowed again, the host shows the surface only once its content is ready.</summary>
    void SetAirspaceContentVisible(bool visible);
}
