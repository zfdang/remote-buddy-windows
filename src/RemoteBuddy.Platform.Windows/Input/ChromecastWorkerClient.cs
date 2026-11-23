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
    public event Action? TruncatedPacketDetected;
    public event Action? Disconnected;

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

    public bool Start(Guid marker, EtwProtocolKind protocolKind = EtwProtocolKind.GoogleG9N9N)
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            string pipeName = "RemoteBuddy_ChromecastEtw_" + Guid.NewGuid().ToString("N");
            _cts = new CancellationTokenSource();

            try
            {
                var pipeSecurity = new PipeSecurity();
                using var currentIdentity = WindowsIdentity.GetCurrent();
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
                    Arguments = $"--chromecast-worker {pipeName} {marker:D} {protocolKind}",
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

                // Wait for worker process to connect and verify that ETW is ready (up to 12 seconds)
                using var timeoutCts = new CancellationTokenSource(12000);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);

                _pipeServer.WaitForConnectionAsync(linkedCts.Token).GetAwaiter().GetResult();

                byte[] readyBuf = new byte[1];
                int read = _pipeServer.ReadAsync(readyBuf.AsMemory(), linkedCts.Token).AsTask().GetAwaiter().GetResult();
                if (read == 0 || readyBuf[0] != 0x01)
                {
                    AppLogger.Warn($"Chromecast worker reported startup failure (byte={(read > 0 ? readyBuf[0] : -1)})", "ChromecastWorkerClient");
                    Cleanup();
                    return false;
                }

                AppLogger.Info("ChromecastWorkerClient: Received ready confirmation (0x01) from worker", "ChromecastWorkerClient");
                _isRunning = true;
                var ct = _cts.Token;
                var server = _pipeServer;
                _readTask = Task.Run(() => ListenToPipeAsync(server, ct), ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warn("Timeout or cancellation waiting for Chromecast worker connection/handshake", "ChromecastWorkerClient");
                Cleanup();
                return false;
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
            byte[] typeBuf = new byte[1];
            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                int read = await server.ReadAsync(typeBuf.AsMemory(0, 1), ct);
                if (read == 0) break; // Pipe disconnected / EOF

                byte msgType = typeBuf[0];
                if (msgType == ChromecastWorkerHost.MsgTypeButtonEvent)
                {
                    byte[] payload = new byte[2];
                    await ReadExactAsync(server, payload, ct);
                    var button = (RemoteButton)payload[0];
                    bool isDown = payload[1] != 0;
                    AppLogger.Info($"ChromecastWorkerClient: Received button {button}, isDown={isDown} from worker pipe", "ChromecastWorkerClient");
                    ButtonEvent?.Invoke(button, isDown);
                }
                else if (msgType == ChromecastWorkerHost.MsgTypeTruncatedDetected)
                {
                    TruncatedPacketDetected?.Invoke();
                }
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
            bool disconnected = false;
            lock (_lock)
            {
                if (ReferenceEquals(_pipeServer, server))
                {
                    _isRunning = false;
                    Cleanup();
                    disconnected = true;
                }
            }
            if (disconnected) Disconnected?.Invoke();
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int r = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (r == 0) throw new EndOfStreamException();
            total += r;
        }
    }

    public void Stop()
    {
        bool wasRunning;
        lock (_lock)
        {
            wasRunning = _isRunning;
            if (!_isRunning) return;
            _isRunning = false;
            Cleanup();
        }

        if (wasRunning)
        {
            Disconnected?.Invoke();
        }
    }

    private void Cleanup()
    {
        try { _cts?.Cancel(); } catch { }
        try { _pipeServer?.Dispose(); } catch { }
        // Closing the pipe lets the worker stop its ETW session and exit.
        _workerProcess?.Dispose();

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
