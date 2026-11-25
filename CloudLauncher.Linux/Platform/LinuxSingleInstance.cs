using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CloudLauncher.Services;

namespace CloudLauncher;

/// <summary>
/// One running launcher per profile, as on Windows (each copy writes the whole settings file): a second
/// start asks the first to come to the front and exits. A named mutex holds the profile (the one named
/// object .NET supports on Linux), and a Unix socket in the runtime folder carries the "show yourself".
/// </summary>
public static class LinuxSingleInstance
{
    private static Mutex? _lock;
    private static Socket? _listener;

    private static string Hash()
    {
        var root = AppSettings.DataRootPath.TrimEnd('/');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16].ToLowerInvariant();
    }

    private static string SocketPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var dir = !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime) ? runtime : Path.GetTempPath();
            return Path.Combine(dir, $"cloudlauncher-{Hash()}.sock");
        }
    }

    /// <summary>Takes the profile. False when another launcher has it; that one has been asked to show
    /// itself, and this process should exit.</summary>
    public static bool Claim()
    {
        try
        {
            _lock = new Mutex(initiallyOwned: false, "CloudLauncher-" + Hash());
            if (TryTake(TimeSpan.Zero)) return true;
            AskToShow();
            // A relaunch (after an update) overlaps the old process's exit.
            return TryTake(TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            // A launcher that cannot make the lock must still open.
            AppLog.LogError("single-instance", ex);
            return true;
        }
    }

    private static bool TryTake(TimeSpan wait)
    {
        try { return _lock!.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }

    private static void AskToShow()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send("show\n"u8);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            AppLog.Log("single-instance", "Another launcher holds this profile but did not answer: " + ex.Message);
        }
    }

    /// <summary>Runs <paramref name="show"/> (on a pool thread) whenever a second start asks.</summary>
    public static void Listen(Action show)
    {
        if (_listener is not null) return;
        try
        {
            var path = SocketPath;
            if (File.Exists(path)) File.Delete(path); // left by a launcher that did not exit cleanly
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            _listener = listener;
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var client = listener.Accept();
                        var buffer = new byte[16];
                        client.Receive(buffer);
                        show();
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (Exception ex) { AppLog.Log("single-instance", ex.Message); }
                }
            }) { IsBackground = true, Name = "Single instance" };
            thread.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { listener.Dispose(); File.Delete(path); } catch { /* exiting */ }
            };
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            AppLog.Log("single-instance", "Could not listen for a second start: " + ex.Message);
        }
    }
}
