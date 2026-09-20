using System.IO;
using System.Net.Sockets;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>The server rejected the RCON password. Separate from every other failure because it is
/// the one the user can fix, and the fix is a specific field in a specific dialog.</summary>
public sealed class RconAuthenticationException(string message) : Exception(message);

/// <summary>
/// A client for Minecraft's remote console protocol (RCON), the one remote-command channel vanilla
/// servers speak.
/// </summary>
/// <remarks>
/// <para>The framing is small and old: a little-endian int32 length covering everything after it, a
/// little-endian int32 request id, a little-endian int32 type, the body, and two NUL bytes.
/// Little-endian is worth saying out loud — every other Minecraft protocol is big-endian, and the
/// original implementation was inherited from Source engine servers rather than written for
/// Minecraft.</para>
/// <para>Type 3 authenticates with the password as the body; the server answers type 2 with the same
/// request id on success, or with request id -1 on failure — that -1 is the only signal that the
/// password was wrong, and the socket is unusable afterwards. Type 2 runs a command and the output
/// comes back as type 0.</para>
/// <para>RCON is plain TCP with no encryption and a single shared password, so the password crosses
/// the network in clear. That is the protocol, not a choice made here; it is why the console is only
/// offered for servers the user explicitly configures, and why the password is never written to the
/// log or echoed into the console scrollback.</para>
/// </remarks>
public sealed class RconClient : IDisposable
{
    public const int DefaultPort = 25575;

    private const int TypeResponse = 0;
    private const int TypeCommand = 2;
    private const int TypeLogin = 3;

    /// <summary>Request id used for the trailing empty command that marks the end of a long reply.
    /// Any value that cannot collide with a real command id will do; this one is recognisable in a
    /// packet capture.</summary>
    private const int SentinelRequestId = 0x7F0F_0F0F;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    /// <summary>One command at a time. The protocol has no way to tell two outstanding replies apart
    /// beyond the request id, and the fragment-reassembly below reads until its sentinel — two
    /// concurrent commands would each eat the other's fragments.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int _nextRequestId = 1;
    private bool _disposed;

    private RconClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public bool IsConnected => !_disposed && _tcp.Connected;

    /// <summary>
    /// Opens a connection and logs in, or throws.
    /// </summary>
    /// <exception cref="RconAuthenticationException">The password was refused.</exception>
    /// <remarks>Failure to connect surfaces as the underlying <see cref="SocketException"/> or a
    /// <see cref="TimeoutException"/>, both of which the console pane reports as text on its own
    /// status line. Unlike the status ping, being unable to reach a console is worth saying plainly:
    /// the user asked for this connection by pressing Connect.</remarks>
    public static async Task<RconClient> ConnectAsync(string host, int port, string password,
                                                      TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("No RCON host.", nameof(host));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));

        var tcp = new TcpClient { NoDelay = true };
        RconClient? client = null;
        try
        {
            await tcp.ConnectAsync(host, port, deadline.Token);
            client = new RconClient(tcp);
            await client.LoginAsync(password, deadline.Token);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client?.Dispose();
            tcp.Dispose();
            throw new TimeoutException($"{host}:{port} did not answer in time.");
        }
        catch
        {
            client?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    private async Task LoginAsync(string password, CancellationToken ct)
    {
        var id = NextId();
        await WritePacketAsync(id, TypeLogin, password ?? "", ct);
        var reply = await ReadPacketAsync(ct);

        // Some servers send an empty type-0 packet ahead of the auth answer. Skip past it rather than
        // reading it as the verdict, which would look like a rejection on a correct password.
        if (reply.Type == TypeResponse && reply.RequestId != -1)
            reply = await ReadPacketAsync(ct);

        if (reply.RequestId == -1)
            throw new RconAuthenticationException("The server refused that RCON password.");
        if (reply.RequestId != id)
            throw new IOException("The server's RCON login reply did not match the request.");
    }

    /// <summary>
    /// Runs one command and returns everything the server said in reply, with no trailing newline.
    /// </summary>
    /// <remarks>
    /// <para>A reply longer than 4096 bytes arrives split across several packets, and the protocol
    /// gives no "last fragment" flag. The standard answer, and the one used here, is to send a second,
    /// empty command straight after the real one: the server processes them in order, so the empty
    /// command's reply cannot arrive until every fragment of the real one has. Fragments are
    /// concatenated until that sentinel reply shows up, which means long output (a big <c>help</c>, a
    /// full <c>whitelist list</c>) comes back whole instead of cut off at the first packet.</para>
    /// <para>A server that ignores the sentinel would leave this waiting, so the read has its own
    /// timeout; anything already accumulated is returned rather than thrown away.</para>
    /// </remarks>
    public async Task<string> SendCommandAsync(string command, TimeSpan? timeout = null,
                                               CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
            var token = deadline.Token;

            var id = NextId();
            await WritePacketAsync(id, TypeCommand, command ?? "", token);
            await WritePacketAsync(SentinelRequestId, TypeCommand, "", token);

            var body = new StringBuilder();
            try
            {
                while (true)
                {
                    var packet = await ReadPacketAsync(token);
                    if (packet.RequestId == SentinelRequestId) break;
                    if (packet.RequestId == id) body.Append(packet.Body);
                    // Anything else is a stray reply to a command that already timed out; drop it.
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (body.Length == 0)
                    throw new TimeoutException("The server did not answer that command in time.");
                // Partial output beats no output, and the caller shows it as-is.
            }

            return body.ToString().TrimEnd('\r', '\n');
        }
        finally { _gate.Release(); }
    }

    private int NextId() => Interlocked.Increment(ref _nextRequestId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch (IOException) { }
        _tcp.Dispose();
        _gate.Dispose();
    }

    // ── framing ──────────────────────────────────────────────────────────────

    private readonly record struct RconPacket(int RequestId, int Type, string Body);

    private async Task WritePacketAsync(int requestId, int type, string body, CancellationToken ct)
    {
        // UTF-8, not the ASCII the Source-engine original specified: Minecraft's own RCON encodes
        // both directions as UTF-8, and decoding as ASCII turns every section sign in a coloured
        // reply into "?" — which then cannot be stripped, so the console fills with punctuation.
        var payload = Encoding.UTF8.GetBytes(body);
        var buffer = new byte[4 + 4 + 4 + payload.Length + 2];
        WriteLe(buffer, 0, 4 + 4 + payload.Length + 2);   // length excludes itself
        WriteLe(buffer, 4, requestId);
        WriteLe(buffer, 8, type);
        payload.CopyTo(buffer, 12);
        // The two trailing NULs are the terminator for the body and for the (always empty) second
        // string the packet format nominally carries.
        buffer[^2] = 0;
        buffer[^1] = 0;

        await _stream.WriteAsync(buffer, ct);
        await _stream.FlushAsync(ct);
    }

    private async Task<RconPacket> ReadPacketAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactlyAsync(header, ct);
        var length = ReadLe(header, 0);
        // 10 is the smallest legal packet (two ints plus the two NULs); the ceiling is a sanity
        // bound so a wrong port pointing at some other service cannot make us allocate wildly.
        if (length is < 10 or > 1024 * 1024)
            throw new IOException("That port answered with something that is not RCON.");

        var rest = new byte[length];
        await ReadExactlyAsync(rest, ct);
        var requestId = ReadLe(rest, 0);
        var type = ReadLe(rest, 4);
        var bodyLength = Math.Max(0, length - 10);
        var body = Encoding.UTF8.GetString(rest, 8, bodyLength);
        return new RconPacket(requestId, type, body);
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read <= 0) throw new IOException("The server closed the RCON connection.");
            offset += read;
        }
    }

    private static void WriteLe(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadLe(byte[] buffer, int offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
}
