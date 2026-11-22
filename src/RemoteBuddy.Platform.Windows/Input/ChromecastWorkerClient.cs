// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
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
    public event Action<ushort>? ConnectionHandleBound;
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

                // Wait for worker process to connect and verify that ETW is ready (up to 12 seconds)
                using var timeoutCts = new CancellationTokenSource(12000);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);

                _pipeServer.WaitForConnectionAsync(linkedCts.Token).GetAwaiter().GetResult();

                byte[] readyBuf = new byte[1];
                int read = _pipeServer.Read(readyBuf, 0, 1);
                if (read == 0 || readyBuf[0] != 0x01)
                {
                    AppLogger.Warn($"Chromecast worker reported startup failure (byte={(read > 0 ? readyBuf[0] : -1)})", "ChromecastWorkerClient");
                    Cleanup();
                    return false;
                }

                AppLogger.Info("ChromecastWorkerClient: Received ready confirmation (0x01) from worker", "ChromecastWorkerClient");
                _isRunning = true;
                var ct = _cts.Token;
                _readTask = Task.Run(() => ListenToPipeAsync(_pipeServer, ct), ct);
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

    public void SendKnownGattHandles(IEnumerable<ushort> handles)
    {
        lock (_lock)
        {
            if (!_isRunning || _pipeServer == null || !_pipeServer.IsConnected) return;
            try
            {
                var list = handles.Where(h => h != 0).Take(255).ToList();
                byte[] buffer = new byte[2 + list.Count * 2];
                buffer[0] = ChromecastWorkerHost.CmdSetKnownGattHandles;
                buffer[1] = (byte)list.Count;
                for (int i = 0; i < list.Count; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2 + i * 2, 2), list[i]);
                }
                _pipeServer.Write(buffer, 0, buffer.Length);
                _pipeServer.Flush();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"SendKnownGattHandles pipe error: {ex.Message}", "ChromecastWorkerClient");
            }
        }
    }

    public void SendResetBinding()
    {
        lock (_lock)
        {
            if (!_isRunning || _pipeServer == null || !_pipeServer.IsConnected) return;
            try
            {
                byte[] buffer = [ChromecastWorkerHost.CmdResetBinding];
                _pipeServer.Write(buffer, 0, 1);
                _pipeServer.Flush();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"SendResetBinding pipe error: {ex.Message}", "ChromecastWorkerClient");
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
                    AppLogger.Warn("ChromecastWorkerClient: Received truncated packet notification from worker pipe", "ChromecastWorkerClient");
                    TruncatedPacketDetected?.Invoke();
                }
                else if (msgType == ChromecastWorkerHost.MsgTypeConnectionHandleBound)
                {
                    byte[] payload = new byte[2];
                    await ReadExactAsync(server, payload, ct);
                    ushort handle = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                    AppLogger.Info($"ChromecastWorkerClient: Received bound ConnectionHandle 0x{handle:X4} from worker pipe", "ChromecastWorkerClient");
                    ConnectionHandleBound?.Invoke(handle);
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
            Stop();
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
