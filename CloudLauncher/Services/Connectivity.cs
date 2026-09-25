using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace CloudLauncher.Services;

/// <summary>
/// Tells "the server answered no" apart from "nothing answered at all".
/// </summary>
/// <remarks>
/// A dead network must not be read as a revoked session, which would throw away the stored refresh
/// token. All reachability checks go through here so call sites agree on what "offline" means.
/// </remarks>
public static class Connectivity
{
    /// <summary>
    /// A short phrase naming the transport failure, or null when <paramref name="ex"/> is not one
    /// (the server answered, or the caller cancelled).
    /// </summary>
    /// <param name="callerToken">The token the caller passed in. A cancellation it asked for is not
    /// a network failure; any other cancellation is a timeout, which is.</param>
    public static string? DescribeTransportFailure(Exception ex, CancellationToken callerToken) => ex switch
    {
        // HttpClient reports its timeout as a cancellation wrapping a TimeoutException. Check this first,
        // since the handler's token is already cancelled by then and can't tell the two apart.
        OperationCanceledException when HasInner<TimeoutException>(ex) => "the server did not answer in time",
        OperationCanceledException when callerToken.IsCancellationRequested => null,
        OperationCanceledException => "the server did not answer in time",
        SocketException se => Describe(se.SocketErrorCode),
        IOException => "the connection dropped",
        // A status code means the server (or the store behind it) answered: an error, not an outage.
        // EnsureSuccessStatusCode throws this shape.
        HttpRequestException { StatusCode: not null } => null,
        HttpRequestException => FindInner<SocketException>(ex) is { } inner
            ? Describe(inner.SocketErrorCode)
            : HasInner<TimeoutException>(ex) ? "the connection timed out" : "the connection failed",
        _ => null
    };

    /// <summary>True when the exception is a failure to reach the server rather than an answer from it.</summary>
    public static bool IsTransportFailure(Exception ex, CancellationToken callerToken) =>
        DescribeTransportFailure(ex, callerToken) is not null;

    private static string Describe(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            "the server's address could not be looked up",
        SocketError.ConnectionRefused => "the connection was refused",
        SocketError.TimedOut => "the connection timed out",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
            "the network is unreachable",
        SocketError.ConnectionReset or SocketError.ConnectionAborted => "the connection was reset",
        _ => "the connection failed"
    };

    private static bool HasInner<T>(Exception ex) where T : Exception => FindInner<T>(ex) is not null;

    private static T? FindInner<T>(Exception? ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is T match) return match;
        return null;
    }
}

/// <summary>
/// Whether the server is reachable, judged from the launcher's real traffic.
/// </summary>
/// <remarks>
/// <c>NetworkInterface.GetIsNetworkAvailable()</c> is wrong behind captive portals and some VPN
/// adapters, and a ping is just one more request. <see cref="ConnectivityHandler"/> records what
/// the launcher's own calls see.
/// </remarks>
public sealed class ConnectivityState
{
    private readonly object _gate = new();
    private bool _offline;
    private string? _reason;
    private DateTimeOffset? _since;

    /// <summary>Raised when the state flips; true means online. Raised outside the lock on whatever
    /// thread noticed; <see cref="AppState"/> marshals it to the UI.</summary>
    public event Action<bool>? Changed;

    public bool IsOffline { get { lock (_gate) return _offline; } }

    /// <summary>Short phrase naming why, e.g. "the connection was refused". Null while online.</summary>
    public string? OfflineReason { get { lock (_gate) return _reason; } }

    /// <summary>When the launcher first noticed, so a page can say how long it has been out.</summary>
    public DateTimeOffset? OfflineSince { get { lock (_gate) return _since; } }

    public void MarkOnline()
    {
        lock (_gate)
        {
            if (!_offline) return; // the common case: already online, nothing to announce
            _offline = false;
            _reason = null;
            _since = null;
        }
        AppLog.Log("net", "The server is answering again.");
        Changed?.Invoke(true);
    }

    public void MarkOffline(string reason)
    {
        bool flipped;
        lock (_gate)
        {
            flipped = !_offline;
            if (flipped) _since = DateTimeOffset.UtcNow;
            _offline = true;
            _reason = reason; // update the reason even when already offline; it can change
        }
        if (!flipped) return;
        AppLog.Log("net", $"The server is unreachable: {reason}.");
        Changed?.Invoke(false);
    }
}

/// <summary>
/// Every request the launcher makes passes through here, so the offline flag reflects real
/// traffic instead of relying on call sites to set it.
/// </summary>
/// <remarks>
/// Any response counts as online. A 500 or a captive portal's login page still proves packets are
/// moving, and must never be mistaken for a lost session.
/// </remarks>
public sealed class ConnectivityHandler(ConnectivityState state, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var resp = await base.SendAsync(request, ct);
            state.MarkOnline();
            return resp;
        }
        catch (Exception ex) when (Connectivity.DescribeTransportFailure(ex, ct) is { } why)
        {
            state.MarkOffline(why);
            throw;
        }
    }
}
