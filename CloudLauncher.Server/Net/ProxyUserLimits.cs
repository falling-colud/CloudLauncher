using System.Collections.Concurrent;

namespace CloudLauncher.Server.Net;

/// <summary>
/// Per-account limits on the store proxy so one account can't hog the shared upstream queue: a
/// token-bucket request rate and a cap on the places it holds in each bucket's queue.
/// </summary>
/// <remarks>
/// <para>Sized so nothing a launcher does on its own trips them: a bulk update check of a 2,000-mod
/// pack is about 80 batched POSTs, a modpack install about 120, and the launcher paces itself to
/// roughly 110 a second across both stores at the highest update-check rate it offers. Answers served
/// from the response cache are not counted (see <c>ProxyController</c>).</para>
/// <para>A launcher that is not signed in is counted by its network address instead of an account
/// (see <see cref="AnonymousKey"/>).</para>
/// <para>Accounts with a full allowance and nothing queued are swept, so only recently active accounts
/// stay in memory.</para>
/// </remarks>
public sealed class ProxyUserLimits
{
    /// <summary>Sustained proxy requests per second per account, for calls that go upstream.</summary>
    public const double RequestsPerSecond = 120;

    /// <summary>Requests an account that has been quiet may send at once.</summary>
    public const double Burst = 200;

    /// <summary>Places one account may hold in one bucket's queue, waiting or in flight. The launcher
    /// keeps at most twenty-three per store by itself: three for pages and up to twenty for an update
    /// check (see <c>ApiClient.UpdateCheckInFlight</c>).</summary>
    public const int MaxQueuedPerBucket = 24;

    /// <summary>Wait suggested to an account over the queue cap. Its own requests free places as
    /// they finish, so this is short.</summary>
    private static readonly TimeSpan QueueRetry = TimeSpan.FromSeconds(1);

    private const long SweepIntervalMs = 30_000;

    private sealed class Account(long now)
    {
        public double Tokens = Burst;
        public long RefilledAt = now;
        public readonly Dictionary<string, int> Queued = new(StringComparer.Ordinal);
        public int QueuedTotal;
        public bool Dropped;
    }

    private readonly ConcurrentDictionary<Guid, Account> _accounts = new();

    /// <summary>The key a caller without an account is counted under: a hash of its address, so it can
    /// never equal a real account id. IPv6 callers are grouped by their /64, which is what one home
    /// connection is usually given.</summary>
    public static Guid AnonymousKey(System.Net.IPAddress? address)
    {
        var ip = address ?? System.Net.IPAddress.None;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) bytes = bytes[..8];
        var hash = System.Security.Cryptography.SHA256.HashData(
            [.. System.Text.Encoding.ASCII.GetBytes("proxy-anonymous:"), .. bytes]);
        return new Guid(hash.AsSpan(0, 16));
    }
    private long _nextSweep = Environment.TickCount64 + SweepIntervalMs;

    /// <summary>Takes one request from the account's allowance. Null when it may go ahead; otherwise
    /// how long until it could (under a second at this rate; the proxy rounds it up to one).</summary>
    public TimeSpan? TryTake(Guid user)
    {
        var now = Environment.TickCount64;
        SweepIfDue(now);
        while (true)
        {
            var account = _accounts.GetOrAdd(user, static (_, t) => new Account(t), now);
            lock (account)
            {
                if (account.Dropped) continue; // swept a moment ago: take the fresh entry
                Refill(account, now);
                if (account.Tokens >= 1)
                {
                    account.Tokens -= 1;
                    return null;
                }
                return TimeSpan.FromSeconds((1 - account.Tokens) / RequestsPerSecond);
            }
        }
    }

    /// <summary>Claims one of the account's places in <paramref name="bucket"/>'s queue. Null when
    /// claimed, and then it must be given back with <see cref="LeaveQueue"/>; otherwise how long
    /// to wait.</summary>
    public TimeSpan? TryEnterQueue(Guid user, string bucket)
    {
        var now = Environment.TickCount64;
        while (true)
        {
            var account = _accounts.GetOrAdd(user, static (_, t) => new Account(t), now);
            lock (account)
            {
                if (account.Dropped) continue;
                account.Queued.TryGetValue(bucket, out var held);
                if (held >= MaxQueuedPerBucket) return QueueRetry;
                account.Queued[bucket] = held + 1;
                account.QueuedTotal++;
                return null;
            }
        }
    }

    public void LeaveQueue(Guid user, string bucket)
    {
        // An account holding a place is never swept, so this is the entry the place was taken on.
        if (!_accounts.TryGetValue(user, out var account)) return;
        lock (account)
        {
            if (!account.Queued.TryGetValue(bucket, out var held)) return;
            if (held <= 1) account.Queued.Remove(bucket);
            else account.Queued[bucket] = held - 1;
            account.QueuedTotal--;
        }
    }

    private static void Refill(Account account, long now)
    {
        var elapsed = now - account.RefilledAt;
        if (elapsed <= 0) return;
        account.Tokens = Math.Min(Burst, account.Tokens + elapsed * RequestsPerSecond / 1000.0);
        account.RefilledAt = now;
    }

    private void SweepIfDue(long now)
    {
        var due = Interlocked.Read(ref _nextSweep);
        if (now < due || Interlocked.CompareExchange(ref _nextSweep, now + SweepIntervalMs, due) != due) return;

        foreach (var (user, account) in _accounts)
        {
            lock (account)
            {
                if (account.QueuedTotal > 0) continue;
                Refill(account, now);
                if (account.Tokens < Burst) continue;
                account.Dropped = true;
                _accounts.TryRemove(new KeyValuePair<Guid, Account>(user, account));
            }
        }
    }
}
