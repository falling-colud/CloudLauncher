using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace CloudLauncher.Services;

/// <summary>App-wide log capture: each line goes to a bounded in-memory buffer, fires
/// <see cref="MessageAppended"/> on the UI thread for the Logs tab, and is queued for
/// <c>launcher.log</c> in the profile's <c>logs</c> folder.</summary>
/// <remarks>Everything passes through <see cref="Redact"/> first, so tokens and passwords in exception
/// messages or response bodies reach neither the screen nor the disk.</remarks>
public static partial class AppLog
{
    /// <summary>How many recent lines are kept in memory. Minecraft's whole stdout/stderr goes through
    /// here, so an unbounded buffer would grow for the life of the process.</summary>
    private const int MaxLines = 5000;

    private static readonly Queue<string> _lines = new();
    private static readonly object _lock = new();

    /// <summary>Fired after a line is appended. Carries the formatted line (with timestamp).</summary>
    public static event Action<string>? MessageAppended;

    /// <summary>Snapshot of the retained lines, joined by newlines.</summary>
    public static string Buffer { get { lock (_lock) return string.Join(Environment.NewLine, _lines); } }

    /// <summary>The folder the log files are written to.</summary>
    public static string LogFolder => LogFile.Folder;

    /// <summary>The file being written to now.</summary>
    public static string LogFilePath => LogFile.CurrentPath;

    public static void Log(string message)
    {
        var now = DateTimeOffset.Now;
        var clean = Redact(message);
        // Invariant 24-hour clock, unlike the rest of the UI: log lines get pasted into issues and
        // compared with server logs, so they should read the same for everyone.
        var line = $"[{TimeFormat.LogClock(now)}] {clean}";
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines) _lines.Dequeue();
        }
        LogFile.Append(now, clean);
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) MessageAppended?.Invoke(line);
        else app.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => MessageAppended?.Invoke(line)));
    }

    public static void Log(string source, string message) => Log($"[{source}] {message}");

    /// <summary>Convenience for logging exceptions with stack.</summary>
    public static void LogError(string context, Exception ex)
    {
        Log($"ERROR in {context}: {ex.GetType().Name}: {ex.Message}");
        var inner = ex.InnerException;
        var depth = 1;
        while (inner is not null && depth < 5)
        {
            Log($"  caused by: {inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
            depth++;
        }
        if (ex.StackTrace is { Length: > 0 } trace)
            Log("  trace:\n" + trace);
    }

    /// <summary>Writes every queued line to the file now. Called on exit and before a crash is
    /// reported, when the background writer may not get another turn.</summary>
    public static void Flush() => LogFile.Flush();

    // ── redaction ────────────────────────────────────────────────────────────

    private const string Masked = "[redacted]";

    /// <summary>The length of the server's refresh tokens: 48 random bytes in base64.</summary>
    private const int OpaqueTokenLength = 64;

    /// <summary>A cheap first pass. Most lines contain none of these and skip the patterns below.</summary>
    private static readonly SearchValues<string> SecretHints = SearchValues.Create(
        ["eyJ", "bearer", "token", "password", "passwd", "api-key", "api_key", "apikey", "cf-key", "secret"],
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Masks anything in <paramref name="text"/> that looks like a credential: JWTs, bearer tokens,
    /// token, key and password values, Minecraft's <c>--accessToken</c> argument, and 64-character
    /// base64 strings shaped like the server's refresh tokens.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var hinted = text.AsSpan().ContainsAny(SecretHints);
        if (!hinted && text.Length < OpaqueTokenLength) return text;

        if (hinted)
        {
            text = JwtPattern().Replace(text, Masked);
            text = BearerPattern().Replace(text, "${scheme} " + Masked);
            text = QuotedSecretPattern().Replace(text, "\"${key}\":\"" + Masked + "\"");
            text = SecretValuePattern().Replace(text, "${key}${sep}" + Masked);
            text = AccessTokenArgumentPattern().Replace(text, "${flag} " + Masked);
        }
        if (text.Length >= OpaqueTokenLength)
            text = OpaqueTokenPattern().Replace(text, m => LooksRandom(m.ValueSpan) ? Masked : m.Value);
        return text;
    }

    /// <summary>A JWT or JWE: a base64url JSON header ("eyJ") and two to four more dot-separated
    /// parts.</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{4,}(?:\.[A-Za-z0-9_-]*){2,4}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"\b(?<scheme>bearer)\s+[A-Za-z0-9\-._~+/]{8,}=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    /// <summary>A JSON string property whose name says it holds a secret.</summary>
    [GeneratedRegex(
        @"""(?<key>(?:access|refresh|id)_?token|pass(?:word|wd)|(?:x-)?api[-_]?key|x-cloudlauncher-cf-key|client_?secret)""\s*:\s*""(?:[^""\\]|\\.)*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedSecretPattern();

    /// <summary>A header, query or form value whose name says it holds a secret. A password only
    /// counts after "=", so a sentence such as "password: too short" is left as it is.</summary>
    [GeneratedRegex(
        @"\b(?<key>(?:access|refresh|id)_?token|(?:x-)?api[-_]?key|x-cloudlauncher-cf-key|client_?secret)\b(?<sep>\s*[:=]\s*)[^\s""'&,;]+" +
        @"|\b(?<key>pass(?:word|wd))\b(?<sep>\s*=\s*)[^\s""'&,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretValuePattern();

    /// <summary>Minecraft's <c>--accessToken</c> launch argument.</summary>
    [GeneratedRegex(@"(?<flag>--accessToken)\s+(?:""[^""]*""|\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenArgumentPattern();

    /// <summary>A standalone run of exactly 64 base64 characters.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9+/=])[A-Za-z0-9+/]{64}(?![A-Za-z0-9+/=])", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueTokenPattern();

    /// <summary>True when a 64-character run reads as random base64 rather than a hash or a path: both
    /// cases of letter, a digit, a character no hex digest has, and only a few slashes.</summary>
    private static bool LooksRandom(ReadOnlySpan<char> s)
    {
        bool upper = false, lower = false, digit = false, notHex = false;
        var slashes = 0;
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z') { upper = true; notHex |= c > 'F'; }
            else if (c is >= 'a' and <= 'z') { lower = true; notHex |= c > 'f'; }
            else if (c is >= '0' and <= '9') digit = true;
            else
            {
                notHex = true;
                if (c == '/') slashes++;
            }
        }
        return upper && lower && digit && notHex && slashes <= 6;
    }

    // ── the file ─────────────────────────────────────────────────────────────

    /// <summary>The copy on disk: <c>launcher.log</c> in the profile's <c>logs</c> folder, rolled at
    /// 2 MB, keeping <c>launcher.1.log</c> (newest) to <c>launcher.4.log</c>.</summary>
    /// <remarks>One background thread writes, so logging never waits on the disk. The queue is bounded;
    /// lines dropped while the disk is slow or unwritable are counted and the count is written
    /// later.</remarks>
    private static class LogFile
    {
        private const long RollAtBytes = 2L * 1024 * 1024;
        private const int KeptFiles = 5;
        private const int MaxQueued = 20_000;

        private static readonly ConcurrentQueue<string> Pending = new();
        private static readonly AutoResetEvent Wake = new(false);
        private static readonly object FileLock = new();
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        private static int _queued;
        private static int _dropped;
        private static int _started;
        private static StreamWriter? _writer;
        private static long _length;
        private static bool _sessionStarted;
        private static DateTime _openRetryAt;
        private static DateTime _rollRetryAt;

        public static readonly string Folder = Path.Combine(AppSettings.DataRootPath, "logs");
        public static readonly string CurrentPath = Path.Combine(Folder, "launcher.log");

        static LogFile()
        {
            // A backstop for exits that never reach App.OnExit.
            try { AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(); }
            catch { /* best effort */ }
        }

        public static void Append(DateTimeOffset at, string message)
        {
            if (Interlocked.Increment(ref _queued) > MaxQueued)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _dropped);
                return;
            }
            Pending.Enqueue(Stamp(at) + "  " + message);
            if (Interlocked.Exchange(ref _started, 1) == 0) StartWriter();
            Wake.Set();
        }

        public static void Flush()
        {
            try
            {
                // Bounded, because this runs on the way out of a crash and must not hang it.
                if (!Monitor.TryEnter(FileLock, TimeSpan.FromSeconds(2))) return;
                try { WriteQueued(); }
                finally { Monitor.Exit(FileLock); }
            }
            catch { /* the log is never a reason to fail */ }
        }

        private static void StartWriter()
        {
            try
            {
                new Thread(Run) { IsBackground = true, Name = "Launcher log writer" }.Start();
            }
            catch { Interlocked.Exchange(ref _started, 0); }
        }

        private static void Run()
        {
            while (true)
            {
                // The timeout retries a file that could not be opened even when nothing new arrives.
                Wake.WaitOne(TimeSpan.FromSeconds(5));
                // A short pause lets a burst of lines (a game starting up) go out in one write.
                Thread.Sleep(50);
                lock (FileLock) WriteQueued();
            }
        }

        private static void WriteQueued()
        {
            if (Pending.IsEmpty && Volatile.Read(ref _dropped) == 0) return;
            if (!EnsureOpen()) return;
            try
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    WriteLine($"{Stamp(DateTimeOffset.Now)}  [log] {dropped} lines were left out because the log file could not keep up.");
                // At most one queue's worth per pass, so a game logging non-stop cannot keep the lock
                // (and a Flush waiting on it) forever.
                for (var n = 0; n < MaxQueued && Pending.TryDequeue(out var line); n++)
                {
                    Interlocked.Decrement(ref _queued);
                    WriteLine(line);
                }
                _writer!.Flush();
            }
            catch
            {
                Close();
                _openRetryAt = DateTime.UtcNow.AddSeconds(30);
            }
        }

        private static bool EnsureOpen()
        {
            if (_writer is not null) return true;
            if (DateTime.UtcNow < _openRetryAt) return false;
            try
            {
                Directory.CreateDirectory(Folder);
                if (DateTime.UtcNow >= _rollRetryAt && File.Exists(CurrentPath)
                    && new FileInfo(CurrentPath).Length >= RollAtBytes)
                    MoveArchives();
                Open();
                WriteHeader();
                return true;
            }
            catch
            {
                Close();
                _openRetryAt = DateTime.UtcNow.AddSeconds(30);
                return false;
            }
        }

        private static void Open()
        {
            // Shared for reading and deleting, so the file can be opened in an editor while the
            // launcher runs and a roll can rename it underneath a reader.
            var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 16 * 1024);
            _length = stream.Length;
            _writer = new StreamWriter(stream, Utf8) { NewLine = "\r\n" };
        }

        private static void Close()
        {
            try { _writer?.Dispose(); }
            catch { /* a full disk: the lines are lost either way */ }
            _writer = null;
        }

        private static void WriteLine(string line)
        {
            if (_length >= RollAtBytes && DateTime.UtcNow >= _rollRetryAt)
            {
                Close();
                var rolled = MoveArchives();
                Open();
                if (rolled) WriteHeader();
            }
            Write(line);
        }

        private static void Write(string line)
        {
            _writer!.WriteLine(line);
            _length += Utf8.GetByteCount(line) + 2;
        }

        /// <summary>Says which build wrote what follows, at the top of every file it opens.</summary>
        private static void WriteHeader()
        {
            var what = _sessionStarted ? "continued" : "started";
            _sessionStarted = true;
            Write($"{Stamp(DateTimeOffset.Now)}  ---- CloudLauncher {AppVersion.CurrentString} {what}, "
                  + $"{RuntimeInformation.OSDescription}, process {Environment.ProcessId}, "
                  + $"local time UTC{DateTimeOffset.Now:zzz} ----");
        }

        /// <summary>Shifts launcher.log to launcher.1.log and each archive one place down, dropping
        /// the oldest. False when a file could not be moved.</summary>
        private static bool MoveArchives()
        {
            try
            {
                var oldest = Archive(KeptFiles - 1);
                if (File.Exists(oldest)) File.Delete(oldest);
                for (var i = KeptFiles - 2; i >= 1; i--)
                    if (File.Exists(Archive(i))) File.Move(Archive(i), Archive(i + 1));
                if (File.Exists(CurrentPath)) File.Move(CurrentPath, Archive(1));
                return true;
            }
            catch
            {
                // Another program holds one of the files. Keep appending to the current one and try
                // again in a minute rather than on every line.
                _rollRetryAt = DateTime.UtcNow.AddMinutes(1);
                return false;
            }
        }

        private static string Archive(int n) => Path.Combine(Folder, $"launcher.{n}.log");

        private static string Stamp(DateTimeOffset at) =>
            at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }
}
