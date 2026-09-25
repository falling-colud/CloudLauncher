using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>
/// One running launcher per profile. Starting a second copy brings the first one to the front
/// instead of opening another window.
/// </summary>
/// <remarks>
/// Each process writes the whole settings file from its own copy (<see cref="AppSettings.Save"/>),
/// so two on one profile would undo each other's changes. The lock is per profile, named by a hash
/// of the data folder. A relaunch overlaps the old process's exit, so a held lock is waited on for a
/// few seconds before giving up.
/// </remarks>
public static class SingleInstance
{
    // Held for the life of the process. Static so they aren't collected: a collected Mutex is
    // released and would let a second copy in.
    private static Mutex? _lock;
    private static EventWaitHandle? _activate;
    private static RegisteredWaitHandle? _listener;

    private static string BaseName()
    {
        var root = AppSettings.DataRootPath.TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
        return @"Local\CloudLauncher-" + hash;
    }

    /// <summary>
    /// Takes the profile's lock. Returns false when another launcher on the same profile is running;
    /// it has then already been asked to come to the front, and this process should exit.
    /// </summary>
    public static bool Claim()
    {
        try
        {
            var name = BaseName();
            _lock = new Mutex(initiallyOwned: false, name + "-instance");
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-activate");

            if (TryTake(TimeSpan.Zero)) return true;

            // Let the running copy take the foreground. Windows only allows that for the process the user
            // just interacted with, which is this one.
            AllowSetForegroundWindow(AsfwAny);
            _activate.Set();

            return TryTake(TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            // A launcher that cannot create a named object (a locked-down session) must still open.
            AppLog.LogError("single-instance", ex);
            return true;
        }
    }

    private static bool TryTake(TimeSpan wait)
    {
        try { return _lock!.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; } // the previous owner died without letting go
    }

    /// <summary>Runs <paramref name="bringToFront"/> (on a pool thread) whenever a second copy asks
    /// this one to show itself.</summary>
    public static void ListenForActivation(Action bringToFront)
    {
        if (_activate is null || _listener is not null) return;
        _listener = ThreadPool.RegisterWaitForSingleObject(_activate,
            (_, _) =>
            {
                try { bringToFront(); }
                catch (Exception ex) { AppLog.LogError("single-instance", ex); }
            },
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
