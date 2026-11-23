// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Security.Principal;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Platform.Windows.Input;

/// <summary>
/// Unified manager for capturing Chromecast (G9N9N) buttons.
/// Seamlessly runs in-process when elevated, or orchestrates a secure worker process when standard-user.
/// </summary>
public sealed class ChromecastButtonReceiver : IDisposable
{
    public event Action<RemoteButton, bool>? ButtonEvent;
    public event Action? TruncatedPacketDetected;


    private readonly ChromecastEtwReceiver _etwReceiver = new();
    private readonly ChromecastWorkerClient _workerClient = new();
    private readonly object _lock = new();

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
        _etwReceiver.ButtonEvent += (btn, down) => ButtonEvent?.Invoke(btn, down);
        _etwReceiver.TruncatedPacketDetected += () => TruncatedPacketDetected?.Invoke();

        _workerClient.ButtonEvent += (btn, down) => ButtonEvent?.Invoke(btn, down);
        _workerClient.TruncatedPacketDetected += () => TruncatedPacketDetected?.Invoke();
        _workerClient.Disconnected += OnWorkerDisconnected;
    }

    private void OnWorkerDisconnected()
    {
        AppLogger.Warn("ChromecastButtonReceiver: Worker disconnected; status restored to stopped and ready for retry", "ChromecastButtonReceiver");
    }

    public bool Start(Guid marker)
    {
        lock (_lock)
        {
            if (IsRunning) return true;

            // 1. If elevated or permitted, attempt in-process ETW session first
            if (IsAdministrator())
            {
                if (_etwReceiver.Start(marker))
                {
                    AppLogger.Info("Chromecast buttons running via in-process ETW receiver", "ChromecastButtonReceiver");
                    return true;
                }
            }
            else
            {
                // Attempt in-process trace (e.g. member of Performance Log Users group)
                if (_etwReceiver.Start(marker))
                {
                    AppLogger.Info("Chromecast buttons running via in-process ETW receiver (non-admin)", "ChromecastButtonReceiver");
                    return true;
                }

                if (IsRunningInTestHost())
                {
                    AppLogger.Debug("Skipping elevated worker launch: test host detected", "ChromecastButtonReceiver");
                    return false;
                }

                // 2. Fall back to elevated helper worker via UAC
                AppLogger.Info("In-process ETW unavailable; activating elevated worker process", "ChromecastButtonReceiver");
                if (_workerClient.Start(marker))
                {
                    AppLogger.Info("Chromecast buttons running via elevated worker process", "ChromecastButtonReceiver");
                    return true;
                }
            }

            return false;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _etwReceiver.Stop();
            _workerClient.Stop();
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
        _workerClient.Disconnected -= OnWorkerDisconnected;
        _etwReceiver.Dispose();
        _workerClient.Dispose();
    }
}
