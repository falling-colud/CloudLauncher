using System.IO;
using System.Net.Sockets;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>The server rejected the RCON password. Kept separate from other failures because the
/// user can fix this one.</summary>
public sealed class RconAuthenticationException(string message) : Exception(message);

/// <summary>
/// A client for Minecraft's remote console protocol (RCON), the one remote-command channel vanilla
/// servers speak.
/// </summary>
/// <remarks>
/// <para>Framing: int32 length (of everything after it), int32 request id, int32 type, the body and
/// two NUL bytes. All little-endian, unlike every other Minecraft protocol, since it comes from
/// Source engine servers.</para>
/// <para>Type 3 authenticates with the password as the body; the server answers type 2 with the same
/// request id on success or -1 on failure, after which the socket is unusable. Type 2 runs a command
/// and the output comes back as type 0.</para>
/// <para>RCON is unencrypted TCP with one shared password, so the console is only offered for servers
/// the user configures, and the password is never logged or echoed.</para>
/// </remarks>
public sealed class RconClient : IDisposable
{
    public const int DefaultPort = 25575;

    private const int TypeResponse = 0;
    private const int TypeCommand = 2;
    private const int TypeLogin = 3;

    /// <summary>Largest packet accepted, going by its length field. Minecraft splits replies at 4096
    /// bytes; the headroom is for servers that don't, and the cap stops a wrong port (some other
    /// service) from causing huge allocations.</summary>
    private const int MaxPacketLength = 1024 * 1024;

    /// <summary>The most reply text one command may gather across all of its fragments.</summary>
    private const int MaxReplyLength = 4 * 1024 * 1024;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    /// <summary>One command at a time: the fragment reassembly reads until its sentinel, so two
    /// concurrent commands would eat each other's fragments.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int _nextRequestId = 1;
    private bool _disposed;

    /// <summary>Set once a command is abandoned with its reply still in flight. The socket then holds
    /// stray packets and the connection is unusable; see <see cref="SendCommandAsync"/>.</summary>
    private volatile bool _poisoned;

    private RconClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>True while this connection can be trusted to answer. Goes false after
    /// <see cref="Dispose"/> or a command that didn't finish (timed out, cut off at
    /// <see cref="MaxReplyLength"/>, or failed part-way); the caller should then reconnect.</summary>
    public bool IsConnected => !_disposed && !_poisoned && _tcp.Connected;

    /// <summary>
    /// Opens a connection and logs in, or throws.
    /// </summary>
    /// <exception cref="RconAuthenticationException">The password was refused.</exception>
    /// <remarks>Connection failures surface as the underlying <see cref="SocketException"/> or a
    /// <see cref="TimeoutException"/>, which the console pane shows on its status line.</remarks>
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

        // Some servers send an empty type-0 packet before the auth answer. Skip it, or a correct password
        // would look rejected.
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
    /// <para>Replies over 4096 bytes come in several packets with no "last fragment" flag, so an empty
    /// sentinel command is sent right after this one. The server answers in order, so fragments are
    /// joined until the sentinel's reply shows up.</para>
    /// <para>A timeout or <see cref="MaxReplyLength"/> ends the read early and returns what arrived.
    /// The sentinel gets a fresh id per command and other ids are dropped, so an abandoned command's
    /// leftovers are never returned. An early end also poisons the connection
    /// (<see cref="IsConnected"/> goes false).</para>
    /// </remarks>
    public async Task<string> SendCommandAsync(string command, TimeSpan? timeout = null,
                                               CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned)
            throw new IOException("This RCON connection is out of step after a command that did not " +
                                  "finish. Reconnect before sending more commands.");

        await _gate.WaitAsync(ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
            var token = deadline.Token;

            var id = NextId();
            var sentinelId = NextId();
            await WritePacketAsync(id, TypeCommand, command ?? "", token);
            await WritePacketAsync(sentinelId, TypeCommand, "", token);

            var body = new StringBuilder();
            try
            {
                while (true)
                {
                    var packet = await ReadPacketAsync(token);
                    if (packet.RequestId == sentinelId) break;
                    if (packet.RequestId == id)
                    {
                        // The rest of the reply is still coming, so stopping here desyncs the socket like a
                        // timeout does.
                        if (body.Length + packet.Body.Length > MaxReplyLength)
                        {
                            _poisoned = true;
                            break;
                        }
                        body.Append(packet.Body);
                    }
                    // Any other id is a leftover from an earlier, abandoned command (ids only go up);
                    // drop it.
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The rest of this command's reply is still coming, so the socket is out of step. Return
                // the partial output, but this client can't be used again.
                _poisoned = true;
                if (body.Length == 0)
                    throw new TimeoutException("The server did not answer that command in time.");
            }
            catch
            {
                // Any other failure part-way through a reply (cancel, bad frame, dropped socket) leaves
                // the stream out of step too.
                _poisoned = true;
                throw;
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

    // ── framing ──

    private readonly record struct RconPacket(int RequestId, int Type, string Body);

    private async Task WritePacketAsync(int requestId, int type, string body, CancellationToken ct)
    {
        // UTF-8 rather than the ASCII of the Source engine spec: Minecraft uses UTF-8 both ways, and ASCII
        // would turn colour-code section signs into "?" that can't be stripped.
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
        // 10 is the smallest legal packet (two ints plus two NULs); negative lengths fail too. Checked
        // before anything is allocated.
        if (length is < 10 or > MaxPacketLength)
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
