// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Bluetooth;

/// <summary>
/// Coordinates event-driven automatic reconnection, single-flight deduplication,
/// user-disconnect suppression, and bounded retries for paired BLE remotes.
/// </summary>
internal sealed class BluetoothAutoReconnectCoordinator : IDisposable
{
    internal static readonly int[] RetryDelaysMs = [1000, 2000, 4000];
    internal static readonly int[] ResumeSweepDelaysMs = [1200, 3500];
    internal const int DisconnectProbeDelayMs = 1500;
    internal const int HidArrivalDebounceMs = 500;
    internal const int HidArrivalMinIntervalMs = 1000;

    private sealed class TargetState
    {
        public bool IsTarget;
        public bool SuppressedByUser;
        public int RetryAttempt;
        public Task<bool>? InFlightConnect;
        public bool InFlightInvalidated;
        public CancellationTokenSource? RetryCts;
        public CancellationTokenSource? DisconnectProbeCts;
    }

    private readonly Func<ulong, bool> _isDeviceReady;
    private readonly Func<ulong, Task<bool>> _isDeviceOsConnectedAsync;
    private readonly Func<ulong, Task<bool>> _connectImplAsync;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private readonly Func<long> _nowMs;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, TargetState> _targets = new();
    private CancellationTokenSource? _resumeSweepCts;
    private CancellationTokenSource? _hidArrivalCts;
    private long _lastHidArrivalProbeMs;
    private bool _disposed;

    public BluetoothAutoReconnectCoordinator(
        Func<ulong, bool> isDeviceReady,
        Func<ulong, Task<bool>> isDeviceOsConnectedAsync,
        Func<ulong, Task<bool>> connectImplAsync,
        Func<int, CancellationToken, Task>? delayAsync = null,
        Func<long>? nowMs = null)
    {
        _isDeviceReady = isDeviceReady;
        _isDeviceOsConnectedAsync = isDeviceOsConnectedAsync;
        _connectImplAsync = connectImplAsync;
        _delayAsync = delayAsync ?? Task.Delay;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public bool HasTargets
    {
        get
        {
            lock (_lock)
            {
                return _targets.Values.Any(t => t.IsTarget);
            }
        }
    }

    public void SetTargets(IEnumerable<ulong> addresses)
    {
        var desired = addresses.Where(addr => addr != 0).ToHashSet();
        var toCancel = new List<CancellationTokenSource?>();

        lock (_lock)
        {
            if (_disposed) return;

            foreach (var (addr, state) in _targets.ToArray())
            {
                if (!desired.Contains(addr))
                {
                    state.IsTarget = false;
                    toCancel.Add(DetachCts(ref state.RetryCts));
                    toCancel.Add(DetachCts(ref state.DisconnectProbeCts));
                    if (state.InFlightConnect == null)
                    {
                        _targets.Remove(addr);
                    }
                }
            }

            foreach (ulong addr in desired)
            {
                if (!_targets.TryGetValue(addr, out var state))
                {
                    state = new TargetState();
                    _targets[addr] = state;
                }
                state.IsTarget = true;
            }
        }

        CancelAndDisposeAll(toCancel);
    }

    public void MarkUserDisconnected(ulong address)
    {
        if (address == 0) return;
        CancellationTokenSource? retryCts = null;
        CancellationTokenSource? probeCts = null;

        lock (_lock)
        {
            if (_disposed) return;
            if (!_targets.TryGetValue(address, out var state))
            {
                state = new TargetState();
                _targets[address] = state;
            }

            state.SuppressedByUser = true;
            state.RetryAttempt = 0;
            if (state.InFlightConnect is { IsCompleted: false })
            {
                state.InFlightInvalidated = true;
            }
            retryCts = DetachCts(ref state.RetryCts);
            probeCts = DetachCts(ref state.DisconnectProbeCts);
        }

        CancelAndDispose(retryCts);
        CancelAndDispose(probeCts);
    }

    public void RemoveTarget(ulong address)
    {
        if (address == 0) return;
        CancellationTokenSource? retryCts = null;
        CancellationTokenSource? probeCts = null;

        lock (_lock)
        {
            if (!_targets.TryGetValue(address, out var state)) return;
            state.IsTarget = false;
            state.SuppressedByUser = false;
            state.RetryAttempt = 0;
            if (state.InFlightConnect is { IsCompleted: false })
            {
                state.InFlightInvalidated = true;
            }
            retryCts = DetachCts(ref state.RetryCts);
            probeCts = DetachCts(ref state.DisconnectProbeCts);
            if (state.InFlightConnect == null)
            {
                _targets.Remove(address);
            }
        }

        CancelAndDispose(retryCts);
        CancelAndDispose(probeCts);
    }

    public Task<bool> ConnectAsync(ulong address)
    {
        if (address == 0) return Task.FromResult(false);

        Task<bool>? priorInvalidated = null;
        Task<bool>? activeConnect = null;
        CancellationTokenSource? retryCts = null;
        CancellationTokenSource? probeCts = null;

        lock (_lock)
        {
            if (_disposed) return Task.FromResult(false);
            if (!_targets.TryGetValue(address, out var state))
            {
                state = new TargetState();
                _targets[address] = state;
            }

            state.SuppressedByUser = false;
            state.RetryAttempt = 0;
            retryCts = DetachCts(ref state.RetryCts);
            probeCts = DetachCts(ref state.DisconnectProbeCts);

            if (state.InFlightConnect is { IsCompleted: false } existing)
            {
                if (state.InFlightInvalidated)
                {
                    priorInvalidated = existing;
                }
                else
                {
                    activeConnect = existing;
                }
            }
            else
            {
                activeConnect = StartConnectLocked(address, state, isUserInitiated: true, reason: "explicit");
            }
        }

        CancelAndDispose(retryCts);
        CancelAndDispose(probeCts);

        if (activeConnect != null) return activeConnect;
        return ConnectAfterInvalidatedAsync(address, priorInvalidated!);
    }

    private async Task<bool> ConnectAfterInvalidatedAsync(ulong address, Task<bool> priorInvalidated)
    {
        try
        {
            await priorInvalidated.ConfigureAwait(false);
        }
        catch
        {
        }

        Task<bool> nextConnect;
        lock (_lock)
        {
            if (_disposed) return false;
            if (!_targets.TryGetValue(address, out var state))
            {
                state = new TargetState();
                _targets[address] = state;
            }

            if (state.InFlightConnect is { IsCompleted: false } existing && !state.InFlightInvalidated)
            {
                nextConnect = existing;
            }
            else
            {
                nextConnect = StartConnectLocked(address, state, isUserInitiated: true, reason: "explicit");
            }
        }

        return await nextConnect.ConfigureAwait(false);
    }

    public void OnOsDeviceConnected(ulong address, string reason)
    {
        if (address == 0) return;
        CancellationTokenSource? retryCts = null;
        CancellationTokenSource? probeCts = null;

        lock (_lock)
        {
            if (_disposed || !_targets.TryGetValue(address, out var state) || !state.IsTarget || state.SuppressedByUser)
            {
                return;
            }

            if (_isDeviceReady(address))
            {
                return;
            }

            state.RetryAttempt = 0;
            retryCts = DetachCts(ref state.RetryCts);
            probeCts = DetachCts(ref state.DisconnectProbeCts);

            if (state.InFlightConnect is { IsCompleted: false })
            {
                return;
            }

            _ = StartConnectLocked(address, state, isUserInitiated: false, reason);
        }

        CancelAndDispose(retryCts);
        CancelAndDispose(probeCts);
    }

    public void OnOsDeviceDisconnected(ulong address)
    {
        if (address == 0) return;
        CancellationTokenSource? retryCts = null;
        CancellationTokenSource? probeCts = null;

        lock (_lock)
        {
            if (!_targets.TryGetValue(address, out var state)) return;
            state.RetryAttempt = 0;
            retryCts = DetachCts(ref state.RetryCts);
            probeCts = DetachCts(ref state.DisconnectProbeCts);
        }

        CancelAndDispose(retryCts);
        CancelAndDispose(probeCts);
    }

    public void OnConnectionStatusChanged(ulong address, bool connected)
    {
        if (address == 0) return;
        CancellationTokenSource? oldRetryCts = null;
        CancellationTokenSource? oldProbeCts = null;
        CancellationTokenSource? newProbeCts = null;

        lock (_lock)
        {
            if (_disposed || !_targets.TryGetValue(address, out var state)) return;

            if (connected)
            {
                state.RetryAttempt = 0;
                oldRetryCts = DetachCts(ref state.RetryCts);
                oldProbeCts = DetachCts(ref state.DisconnectProbeCts);
            }
            else if (state.IsTarget && !state.SuppressedByUser && state.InFlightConnect is not { IsCompleted: false })
            {
                oldProbeCts = DetachCts(ref state.DisconnectProbeCts);
                newProbeCts = new CancellationTokenSource();
                state.DisconnectProbeCts = newProbeCts;
            }
        }

        CancelAndDispose(oldRetryCts);
        CancelAndDispose(oldProbeCts);

        if (newProbeCts != null)
        {
            _ = RunDisconnectProbeAsync(address, newProbeCts);
        }
    }

    public void NotifySystemResume()
    {
        CancellationTokenSource? oldSweepCts = null;
        CancellationTokenSource? newSweepCts = null;
        var toCancel = new List<CancellationTokenSource?>();

        lock (_lock)
        {
            if (_disposed) return;

            bool anyEligible = false;
            foreach (var state in _targets.Values)
            {
                if (!state.IsTarget || state.SuppressedByUser) continue;
                anyEligible = true;
                state.RetryAttempt = 0;
                toCancel.Add(DetachCts(ref state.RetryCts));
            }

            oldSweepCts = DetachCts(ref _resumeSweepCts);
            if (anyEligible)
            {
                newSweepCts = new CancellationTokenSource();
                _resumeSweepCts = newSweepCts;
            }
        }

        CancelAndDispose(oldSweepCts);
        CancelAndDisposeAll(toCancel);

        if (newSweepCts != null)
        {
            _ = RunResumeSweepAsync(newSweepCts);
        }
    }

    public void NotifyHidDeviceArrival()
    {
        CancellationTokenSource? newCts = null;
        int delayMs = 0;

        lock (_lock)
        {
            if (_disposed) return;

            bool anyCandidate = _targets.Any(pair =>
                pair.Value.IsTarget &&
                !pair.Value.SuppressedByUser &&
                !_isDeviceReady(pair.Key) &&
                pair.Value.InFlightConnect is not { IsCompleted: false });
            if (!anyCandidate) return;

            if (_hidArrivalCts != null)
            {
                // Already scheduled and waiting to coalesce bursty HID arrivals.
                return;
            }

            long elapsed = _nowMs() - _lastHidArrivalProbeMs;
            int cooldownRemaining = elapsed < HidArrivalMinIntervalMs
                ? (int)(HidArrivalMinIntervalMs - elapsed)
                : 0;
            delayMs = Math.Max(HidArrivalDebounceMs, cooldownRemaining);
            newCts = new CancellationTokenSource();
            _hidArrivalCts = newCts;
        }

        _ = RunDebouncedHidArrivalAsync(newCts, delayMs);
    }

    private Task<bool> StartConnectLocked(ulong address, TargetState state, bool isUserInitiated, string reason)
    {
        AppLogger.Info($"AutoReconnect initiating ConnectAsync for 0x{address:X12} (reason={reason})", "BluetoothManager");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.InFlightConnect = tcs.Task;
        state.InFlightInvalidated = false;

        _ = ExecuteConnectAsync(address, state, isUserInitiated, tcs);
        return tcs.Task;
    }

    private async Task ExecuteConnectAsync(
        ulong address,
        TargetState state,
        bool isUserInitiated,
        TaskCompletionSource<bool> tcs)
    {
        bool ok = false;
        try
        {
            ok = await _connectImplAsync(address).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"AutoReconnect ConnectAsync failed with exception for 0x{address:X12}", ex, "BluetoothManager");
            ok = false;
        }
        finally
        {
            tcs.TrySetResult(ok);
        }

        CancellationTokenSource? retryCts = null;
        int retryDelayMs = 0;
        bool shouldCheckOsForRetry = false;

        lock (_lock)
        {
            bool isCurrentTask = ReferenceEquals(state.InFlightConnect, tcs.Task);
            if (isCurrentTask)
            {
                state.InFlightConnect = null;
                state.InFlightInvalidated = false;
            }

            if (_disposed || !state.IsTarget)
            {
                if (!state.IsTarget && state.InFlightConnect == null)
                {
                    _targets.Remove(address);
                }
                return;
            }

            if (ok || _isDeviceReady(address))
            {
                state.RetryAttempt = 0;
                return;
            }

            if (!state.SuppressedByUser && state.RetryAttempt < RetryDelaysMs.Length && isCurrentTask)
            {
                shouldCheckOsForRetry = true;
            }
        }

        if (!shouldCheckOsForRetry) return;

        bool osConnected = false;
        try
        {
            osConnected = await _isDeviceOsConnectedAsync(address).ConfigureAwait(false);
        }
        catch
        {
            osConnected = false;
        }

        if (!osConnected)
        {
            AppLogger.Debug($"0x{address:X12} is disconnected at OS Bluetooth level; waiting for online event", "BluetoothManager");
            return;
        }

        CancellationTokenSource? oldRetryCts = null;
        lock (_lock)
        {
            if (_disposed || !state.IsTarget || state.SuppressedByUser || _isDeviceReady(address) ||
                state.InFlightConnect is { IsCompleted: false } || state.RetryAttempt >= RetryDelaysMs.Length)
            {
                return;
            }

            retryDelayMs = RetryDelaysMs[state.RetryAttempt++];
            oldRetryCts = DetachCts(ref state.RetryCts);
            retryCts = new CancellationTokenSource();
            state.RetryCts = retryCts;
        }

        CancelAndDispose(oldRetryCts);
        AppLogger.Info(
            $"0x{address:X12} is OS-connected after {(isUserInitiated ? "explicit" : "auto")} ConnectAsync failure; scheduling retry in {retryDelayMs}ms",
            "BluetoothManager");
        _ = RunRetryAfterDelayAsync(address, state, retryDelayMs, retryCts);
    }

    private async Task RunRetryAfterDelayAsync(ulong address, TargetState state, int delayMs, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            await _delayAsync(delayMs, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(state.RetryCts, cts))
                {
                    state.RetryCts = null;
                }
            }
            try { cts.Dispose(); } catch { }
        }

        if (!await IsEligibleAndOsConnectedAsync(address).ConfigureAwait(false)) return;

        lock (_lock)
        {
            if (_disposed || !state.IsTarget || state.SuppressedByUser || _isDeviceReady(address) ||
                state.InFlightConnect is { IsCompleted: false })
            {
                return;
            }

            _ = StartConnectLocked(address, state, isUserInitiated: false, reason: "retry");
        }
    }

    private async Task RunDisconnectProbeAsync(ulong address, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            await _delayAsync(DisconnectProbeDelayMs, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (_lock)
            {
                if (_targets.TryGetValue(address, out var state) && ReferenceEquals(state.DisconnectProbeCts, cts))
                {
                    state.DisconnectProbeCts = null;
                }
            }
            try { cts.Dispose(); } catch { }
        }

        if (!await IsEligibleAndOsConnectedAsync(address).ConfigureAwait(false)) return;

        lock (_lock)
        {
            if (_disposed || !_targets.TryGetValue(address, out var state) ||
                !state.IsTarget || state.SuppressedByUser || _isDeviceReady(address) ||
                state.InFlightConnect is { IsCompleted: false })
            {
                return;
            }

            _ = StartConnectLocked(address, state, isUserInitiated: false, reason: "disconnect_probe");
        }
    }

    private async Task RunResumeSweepAsync(CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            int elapsed = 0;
            foreach (int targetMs in ResumeSweepDelaysMs)
            {
                int stepDelay = Math.Max(0, targetMs - elapsed);
                elapsed = targetMs;
                await _delayAsync(stepDelay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                await ProbeAndReconnectDisconnectedTargetsAsync("system_resume").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_resumeSweepCts, cts))
                {
                    _resumeSweepCts = null;
                }
            }
            try { cts.Dispose(); } catch { }
        }
    }

    private async Task RunDebouncedHidArrivalAsync(CancellationTokenSource cts, int delayMs)
    {
        CancellationToken token = cts.Token;
        try
        {
            if (delayMs > 0)
            {
                await _delayAsync(delayMs, token).ConfigureAwait(false);
            }
            if (token.IsCancellationRequested) return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_hidArrivalCts, cts))
                {
                    _hidArrivalCts = null;
                }
            }
            try { cts.Dispose(); } catch { }
        }

        lock (_lock)
        {
            if (_disposed) return;
            _lastHidArrivalProbeMs = _nowMs();
        }

        await ProbeAndReconnectDisconnectedTargetsAsync("hid_arrival").ConfigureAwait(false);
    }

    private async Task ProbeAndReconnectDisconnectedTargetsAsync(string reason)
    {
        ulong[] candidates;
        lock (_lock)
        {
            if (_disposed) return;
            candidates = _targets
                .Where(pair => pair.Value.IsTarget &&
                               !pair.Value.SuppressedByUser &&
                               !_isDeviceReady(pair.Key) &&
                               pair.Value.InFlightConnect is not { IsCompleted: false })
                .Select(pair => pair.Key)
                .ToArray();
        }

        foreach (ulong address in candidates)
        {
            if (!await IsEligibleAndOsConnectedAsync(address).ConfigureAwait(false)) continue;

            lock (_lock)
            {
                if (_disposed || !_targets.TryGetValue(address, out var state) ||
                    !state.IsTarget || state.SuppressedByUser || _isDeviceReady(address) ||
                    state.InFlightConnect is { IsCompleted: false })
                {
                    continue;
                }

                _ = StartConnectLocked(address, state, isUserInitiated: false, reason);
            }
        }
    }

    private async Task<bool> IsEligibleAndOsConnectedAsync(ulong address)
    {
        lock (_lock)
        {
            if (_disposed || !_targets.TryGetValue(address, out var state) ||
                !state.IsTarget || state.SuppressedByUser || _isDeviceReady(address))
            {
                return false;
            }
        }

        try
        {
            return await _isDeviceOsConnectedAsync(address).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static CancellationTokenSource? DetachCts(ref CancellationTokenSource? slot)
    {
        var cts = slot;
        slot = null;
        return cts;
    }

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private static void CancelAndDisposeAll(List<CancellationTokenSource?> list)
    {
        foreach (var cts in list) CancelAndDispose(cts);
    }

    public void Dispose()
    {
        List<CancellationTokenSource?> toCancel;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            toCancel = new List<CancellationTokenSource?>
            {
                DetachCts(ref _resumeSweepCts),
                DetachCts(ref _hidArrivalCts)
            };
            foreach (var state in _targets.Values)
            {
                state.IsTarget = false;
                toCancel.Add(DetachCts(ref state.RetryCts));
                toCancel.Add(DetachCts(ref state.DisconnectProbeCts));
            }
            _targets.Clear();
        }

        CancelAndDisposeAll(toCancel);
    }
}
