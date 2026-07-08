using System.Diagnostics;
using System.Windows;

namespace CloudLauncher.Services;

public enum MinecraftInstanceStatus
{
    Idle,
    Launching,
    Running
}

public sealed class MinecraftLaunchHandle : IDisposable
{
    private readonly MinecraftInstanceService _owner;
    private bool _isCompleted;
    private bool _isDisposed;

    internal MinecraftLaunchHandle(MinecraftInstanceService owner, Guid packId, CancellationTokenSource cancellation)
    {
        _owner = owner;
        PackId = packId;
        Cancellation = cancellation;
    }

    public Guid PackId { get; }
    internal CancellationTokenSource Cancellation { get; }
    public CancellationToken Token => Cancellation.Token;

    public bool Complete(Process process)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(MinecraftLaunchHandle));

        _isCompleted = true;
        return _owner.CompleteLaunch(this, process);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        if (!_isCompleted)
            _owner.FinishLaunch(this);
    }
}

public sealed class MinecraftInstanceService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, MinecraftLaunchHandle> _launches = new();
    private readonly Dictionary<Guid, Process> _running = new();

    public event Action<Guid>? StateChanged;

    public MinecraftInstanceStatus GetStatus(Guid packId)
    {
        bool pruned;
        MinecraftInstanceStatus status;
        lock (_gate)
        {
            pruned = PruneExitedProcess(packId);
            status = _running.ContainsKey(packId)
                ? MinecraftInstanceStatus.Running
                : _launches.ContainsKey(packId)
                    ? MinecraftInstanceStatus.Launching
                    : MinecraftInstanceStatus.Idle;
        }

        if (pruned)
            RaiseStateChanged(packId);

        return status;
    }

    public bool IsBusy(Guid packId) => GetStatus(packId) != MinecraftInstanceStatus.Idle;

    public MinecraftLaunchHandle BeginLaunch(Guid packId, CancellationToken externalCancellation = default)
    {
        lock (_gate)
        {
            PruneExitedProcess(packId);
            if (_running.ContainsKey(packId))
                throw new InvalidOperationException("This instance already has a running Minecraft process.");
            if (_launches.ContainsKey(packId))
                throw new InvalidOperationException("This instance is already launching.");

            var cancellation = externalCancellation.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalCancellation)
                : new CancellationTokenSource();
            var handle = new MinecraftLaunchHandle(this, packId, cancellation);
            _launches[packId] = handle;
            RaiseStateChanged(packId);
            return handle;
        }
    }

    public void Stop(Guid packId)
    {
        Process? process;
        MinecraftLaunchHandle? launch;
        bool pruned;

        lock (_gate)
        {
            pruned = PruneExitedProcess(packId);
            _running.TryGetValue(packId, out process);
            _launches.TryGetValue(packId, out launch);
        }

        launch?.Cancellation.Cancel();

        if (process is not null)
        {
            KillProcess(process);
            RemoveRunning(packId, process);
        }
        else if (launch is not null)
        {
            RaiseStateChanged(packId);
        }
        else if (pruned)
        {
            RaiseStateChanged(packId);
        }
    }

    internal bool CompleteLaunch(MinecraftLaunchHandle handle, Process process)
    {
        var packId = handle.PackId;
        var cancelRequested = handle.Cancellation.IsCancellationRequested;

        lock (_gate)
        {
            if (_launches.TryGetValue(packId, out var current) && ReferenceEquals(current, handle))
                _launches.Remove(packId);
        }

        handle.Cancellation.Dispose();

        if (cancelRequested)
        {
            KillProcess(process);
            RaiseStateChanged(packId);
            return false;
        }

        try
        {
            process.EnableRaisingEvents = true;
        }
        catch { }

        EventHandler? exited = null;
        exited = (_, _) =>
        {
            process.Exited -= exited;
            RemoveRunning(packId, process);
        };

        lock (_gate)
        {
            process.Exited += exited;
            _running[packId] = process;
        }

        // Close the check-then-subscribe race: if the process exited just before/while we
        // subscribed, the Exited event may never reach our handler, leaving the pack stuck
        // "Running" forever. Re-check now and remove if already gone. RemoveRunning is
        // idempotent (ReferenceEquals guard), so a duplicate removal from a late Exited
        // event is harmless.
        if (HasExited(process))
        {
            RemoveRunning(packId, process);
            return false;
        }

        RaiseStateChanged(packId);
        return true;
    }

    internal void FinishLaunch(MinecraftLaunchHandle handle)
    {
        var packId = handle.PackId;
        lock (_gate)
        {
            if (_launches.TryGetValue(packId, out var current) && ReferenceEquals(current, handle))
                _launches.Remove(packId);
        }

        handle.Cancellation.Dispose();
        RaiseStateChanged(packId);
    }

    public bool TryHandoffRunningProcess(Guid packId, Process oldProcess, Process newProcess)
    {
        EventHandler? exited = null;
        var handoff = false;

        lock (_gate)
        {
            if (HasExited(newProcess))
                return false;

            if (_running.TryGetValue(packId, out var current))
            {
                if (!ReferenceEquals(current, oldProcess))
                    return false;
            }
            else if (!HasExited(oldProcess))
            {
                return false;
            }

            _running[packId] = newProcess;
            handoff = true;
        }

        if (!handoff)
            return false;

        try
        {
            newProcess.EnableRaisingEvents = true;
        }
        catch { }

        exited = (_, _) =>
        {
            newProcess.Exited -= exited;
            RemoveRunning(packId, newProcess);
        };

        try
        {
            newProcess.Exited += exited;
        }
        catch { }

        // Same race guard as CompleteLaunch: if newProcess exited during the handoff, make
        // sure it doesn't stay registered as running.
        if (HasExited(newProcess))
            RemoveRunning(packId, newProcess);

        RaiseStateChanged(packId);
        return true;
    }

    private void RemoveRunning(Guid packId, Process process)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(packId, out var current) && ReferenceEquals(current, process))
                _running.Remove(packId);
        }

        RaiseStateChanged(packId);
    }

    private bool PruneExitedProcess(Guid packId)
    {
        if (_running.TryGetValue(packId, out var process) && HasExited(process))
        {
            _running.Remove(packId);
            return true;
        }

        return false;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!HasExited(process))
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            AppLog.LogError("kill-instance", ex);
            throw new InvalidOperationException("Could not kill the Minecraft instance: " + ex.Message, ex);
        }
    }

    private void RaiseStateChanged(Guid packId)
    {
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) StateChanged?.Invoke(packId);
        else app.Dispatcher.BeginInvoke(() => StateChanged?.Invoke(packId));
    }
}
