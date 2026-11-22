// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

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

    private readonly ChromecastEtwReceiver _etwReceiver = new();
    private readonly ChromecastWorkerClient _workerClient = new();
    private readonly object _lock = new();
    private bool _isRunning;

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _isRunning;
        }
    }

    public ChromecastButtonReceiver()
    {
        _etwReceiver.ButtonEvent += OnButtonEvent;
        _workerClient.ButtonEvent += OnButtonEvent;
    }

    private void OnButtonEvent(RemoteButton button, bool isDown)
    {
        ButtonEvent?.Invoke(button, isDown);
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            // 1. If elevated or permitted, attempt in-process ETW session first
            if (IsAdministrator())
            {
                if (_etwReceiver.Start())
                {
                    _isRunning = true;
                    AppLogger.Info("Chromecast buttons running via in-process ETW receiver", "ChromecastButtonReceiver");
                    return true;
                }
            }
            else
            {
                // Attempt in-process trace (e.g. member of Performance Log Users group)
                if (_etwReceiver.Start())
                {
                    _isRunning = true;
                    AppLogger.Info("Chromecast buttons running via in-process ETW receiver (non-admin)", "ChromecastButtonReceiver");
                    return true;
                }

                // 2. Fall back to elevated helper worker via UAC
                AppLogger.Info("In-process ETW unavailable; activating elevated worker process", "ChromecastButtonReceiver");
                if (_workerClient.Start())
                {
                    _isRunning = true;
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
            if (!_isRunning) return;
            _isRunning = false;
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

    public void Dispose()
    {
        Stop();
        _etwReceiver.ButtonEvent -= OnButtonEvent;
        _workerClient.ButtonEvent -= OnButtonEvent;
        _etwReceiver.Dispose();
        _workerClient.Dispose();
    }
}
