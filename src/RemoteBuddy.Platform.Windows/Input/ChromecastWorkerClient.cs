// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Platform.Windows.Input;

/// <summary>
/// Client running in the main application that manages an elevated child worker process
/// to stream Chromecast ETW button events over a secure local named pipe.
/// </summary>
public sealed class ChromecastWorkerClient : IDisposable
{
    public event Action<RemoteButton, bool>? ButtonEvent;

    private readonly object _lock = new();
    private NamedPipeServerStream? _pipeServer;
    private Process? _workerProcess;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private bool _isRunning;

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _isRunning;
        }
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            string pipeName = "RemoteBuddy_ChromecastEtw_" + Guid.NewGuid().ToString("N");
            _cts = new CancellationTokenSource();

            try
            {
                var pipeSecurity = new PipeSecurity();
                var currentIdentity = WindowsIdentity.GetCurrent();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    currentIdentity.User ?? new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                _pipeServer = NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    1024,
                    1024,
                    pipeSecurity);

                string exePath = Environment.ProcessPath ?? string.Empty;
                if (string.IsNullOrEmpty(exePath))
                {
                    using var curProc = Process.GetCurrentProcess();
                    exePath = curProc.MainModule?.FileName ?? string.Empty;
                }

                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    AppLogger.Warn("Unable to determine executable path to launch Chromecast worker", "ChromecastWorkerClient");
                    Cleanup();
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--chromecast-worker \"{pipeName}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                AppLogger.Info($"Spawning elevated Chromecast worker via pipe '{pipeName}'", "ChromecastWorkerClient");
                _workerProcess = Process.Start(psi);
                if (_workerProcess == null)
                {
                    AppLogger.Warn("Process.Start returned null for Chromecast worker", "ChromecastWorkerClient");
                    Cleanup();
                    return false;
                }

                _isRunning = true;
                var ct = _cts.Token;
                _readTask = Task.Run(() => ListenToPipeAsync(_pipeServer, ct), ct);
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223 /* ERROR_CANCELLED (UAC declined) */)
            {
                AppLogger.Warn("User declined UAC elevation prompt for Chromecast ETW button receiver", "ChromecastWorkerClient");
                Cleanup();
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Exception starting ChromecastWorkerClient", ex, "ChromecastWorkerClient");
                Cleanup();
                return false;
            }
        }
    }

    private async Task ListenToPipeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(15000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await server.WaitForConnectionAsync(linked.Token);

            AppLogger.Info("ChromecastWorkerClient established connection with worker process", "ChromecastWorkerClient");

            byte[] buffer = new byte[2];
            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                int totalRead = 0;
                while (totalRead < 2)
                {
                    int bytesRead = await server.ReadAsync(buffer.AsMemory(totalRead, 2 - totalRead), ct);
                    if (bytesRead == 0) break; // Pipe disconnected
                    totalRead += bytesRead;
                }

                if (totalRead < 2) break;

                var button = (RemoteButton)buffer[0];
                bool isDown = buffer[1] != 0;

                ButtonEvent?.Invoke(button, isDown);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                AppLogger.Debug($"Exception in ChromecastWorkerClient pipe read loop: {ex.Message}", "ChromecastWorkerClient");
            }
        }
        finally
        {
            Stop();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            Cleanup();
        }
    }

    private void Cleanup()
    {
        try { _cts?.Cancel(); } catch { }
        try { _pipeServer?.Dispose(); } catch { }
        try
        {
            if (_workerProcess != null && !_workerProcess.HasExited)
            {
                _workerProcess.Kill();
            }
            _workerProcess?.Dispose();
        }
        catch { }

        _pipeServer = null;
        _workerProcess = null;
        _cts?.Dispose();
        _cts = null;
        _readTask = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
