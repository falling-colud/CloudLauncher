namespace CloudLauncher.Services;

/// <summary>
/// The UI thread of whichever front end hosts the services, for the services that raise their events
/// there. The Windows launcher reaches WPF's dispatcher directly; another front end (the Linux one)
/// sets <see cref="Post"/> and <see cref="HasAccess"/> at startup.
/// </summary>
/// <remarks>With nothing set (a console harness) the work runs straight away on the calling thread,
/// like the WPF code paths do when there is no <c>Application</c>.</remarks>
public static class UiThread
{
    /// <summary>Queues work on the UI thread. Null when there is no UI.</summary>
    public static Action<Action>? Post { get; set; }

    /// <summary>True on the UI thread. Null when there is no UI.</summary>
    public static Func<bool>? HasAccess { get; set; }

    /// <summary>Runs <paramref name="action"/> now when this is the UI thread (or there is no UI),
    /// otherwise queues it there.</summary>
    public static void Run(Action action)
    {
        if (Post is null || HasAccess?.Invoke() != false) action();
        else Post(action);
    }

    /// <summary>Queues <paramref name="action"/> on the UI thread even from the UI thread itself, or
    /// runs it now when there is no UI.</summary>
    public static void Later(Action action)
    {
        if (Post is null) action();
        else Post(action);
    }
}
