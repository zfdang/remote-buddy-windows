// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Security.Principal;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Platform.Windows.Input;

public enum EtwCaptureStatus
{
    Idle,
    Starting,
    RunningInProcess,
    RunningElevatedWorker,
    ElevationDeclined,
    Failed,
    Disconnected
}

/// <summary>
/// Unified manager for capturing Chromecast (G9N9N) buttons.
/// Seamlessly runs in-process when elevated, or orchestrates a secure worker process when standard-user.
/// </summary>
public sealed class ChromecastButtonReceiver : IDisposable
{
    public event Action<ulong, RemoteButton, bool>? ButtonEvent;
    public event Action<ulong>? TruncatedPacketDetected;
    public event Action<ulong>? DeviceDisconnected;
    public event Action<ulong>? SourceBound;
    public event Action? UnexpectedDisconnect;
    public event Action<EtwCaptureStatus>? StatusChanged;

    private readonly ChromecastEtwReceiver _etwReceiver = new();
    private readonly ChromecastWorkerClient _workerClient = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly object _lock = new();
    private CancellationTokenSource? _startCts;
    private volatile EtwCaptureStatus _status = EtwCaptureStatus.Idle;

    public EtwCaptureStatus Status => _status;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _etwReceiver.IsRunning || _workerClient.IsRunning;
            }
        }
    }

    public ChromecastButtonReceiver()
    {
        _etwReceiver.ButtonEvent += (address, btn, down) => ButtonEvent?.Invoke(address, btn, down);
        _etwReceiver.TruncatedPacketDetected += address => TruncatedPacketDetected?.Invoke(address);
        _etwReceiver.DeviceDisconnected += address => DeviceDisconnected?.Invoke(address);
        _etwReceiver.SourceBound += address => SourceBound?.Invoke(address);
        _etwReceiver.UnexpectedStop += OnEtwUnexpectedStop;

        _workerClient.ButtonEvent += (address, btn, down) => ButtonEvent?.Invoke(address, btn, down);
        _workerClient.TruncatedPacketDetected += address => TruncatedPacketDetected?.Invoke(address);
        _workerClient.DeviceDisconnected += address => DeviceDisconnected?.Invoke(address);
        _workerClient.SourceBound += address => SourceBound?.Invoke(address);
        _workerClient.Disconnected += OnWorkerDisconnected;
    }

    private void SetStatus(EtwCaptureStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private void OnEtwUnexpectedStop(string reason)
    {
        AppLogger.Warn($"ChromecastButtonReceiver: In-process ETW receiver stopped unexpectedly: {reason}", "ChromecastButtonReceiver");
        SetStatus(EtwCaptureStatus.Disconnected);
        UnexpectedDisconnect?.Invoke();
    }

    private void OnWorkerDisconnected(bool unexpected)
    {
        if (unexpected)
        {
            AppLogger.Warn("ChromecastButtonReceiver: Elevated ETW worker disconnected unexpectedly", "ChromecastButtonReceiver");
            SetStatus(EtwCaptureStatus.Disconnected);
            UnexpectedDisconnect?.Invoke();
        }
        else
        {
            AppLogger.Debug("ChromecastButtonReceiver: Worker stopped", "ChromecastButtonReceiver");
        }
    }

    internal void RaiseUnexpectedDisconnectForTest()
    {
        SetStatus(EtwCaptureStatus.Disconnected);
        UnexpectedDisconnect?.Invoke();
    }

    public void SetReadyAddresses(IEnumerable<ulong> addresses) => _etwReceiver.SetReadyAddresses(addresses);

    public void NotifyProbeCompleted() => _etwReceiver.ArmFallbackIfNoOutgoingAcl();

    public async Task<bool> StartAsync(IReadOnlyList<EtwButtonSource> sources, CancellationToken ct = default)
    {
        if (IsRunningInTestHost())
        {
            AppLogger.Debug("Skipping ETW startup: test host detected", "ChromecastButtonReceiver");
            return false;
        }

        CancellationTokenSource localCts;
        lock (_lock)
        {
            if (IsRunning) return true;
            _startCts?.Cancel();
            _startCts?.Dispose();
            localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _startCts = localCts;
        }

        try
        {
            await _startLock.WaitAsync(localCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (IsRunning) return true;
            if (localCts.IsCancellationRequested) return false;

            SetStatus(EtwCaptureStatus.Starting);

            // 1. If elevated or permitted (e.g. Performance Log Users group), start in-process ETW session
            bool startedInProcess = await Task.Run(() => _etwReceiver.Start(sources), localCts.Token).ConfigureAwait(false);
            if (startedInProcess)
            {
                SetStatus(EtwCaptureStatus.RunningInProcess);
                AppLogger.Info("Chromecast buttons running via in-process ETW receiver", "ChromecastButtonReceiver");
                return true;
            }

            if (localCts.IsCancellationRequested)
            {
                SetStatus(EtwCaptureStatus.Idle);
                return false;
            }

            if (!IsAdministrator())
            {
                // 2. Fall back to elevated helper worker via UAC
                AppLogger.Info("In-process ETW unavailable; activating elevated worker process", "ChromecastButtonReceiver");
                if (await _workerClient.StartAsync(sources, localCts.Token).ConfigureAwait(false))
                {
                    SetStatus(EtwCaptureStatus.RunningElevatedWorker);
                    AppLogger.Info("Chromecast buttons running via elevated worker process", "ChromecastButtonReceiver");
                    return true;
                }

                SetStatus(_workerClient.WasElevationDeclined ? EtwCaptureStatus.ElevationDeclined : EtwCaptureStatus.Failed);
                return false;
            }

            SetStatus(EtwCaptureStatus.Failed);
            return false;
        }
        catch (OperationCanceledException)
        {
            if (!IsRunning) SetStatus(EtwCaptureStatus.Idle);
            return false;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_startCts, localCts)) _startCts = null;
            }
            localCts.Dispose();
            _startLock.Release();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            try { _startCts?.Cancel(); } catch { }
            _etwReceiver.Stop();
            _workerClient.Stop();
            SetStatus(EtwCaptureStatus.Idle);
            AppLogger.Info("ChromecastButtonReceiver stopped", "ChromecastButtonReceiver");
        }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRunningInTestHost()
    {
        string? procName = Process.GetCurrentProcess().ProcessName;
        return procName != null && (procName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                                   procName.Contains("vstest", StringComparison.OrdinalIgnoreCase) ||
                                   procName.Contains("xunit", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        Stop();
        _etwReceiver.UnexpectedStop -= OnEtwUnexpectedStop;
        _workerClient.Disconnected -= OnWorkerDisconnected;
        _etwReceiver.Dispose();
        _workerClient.Dispose();
    }
}
