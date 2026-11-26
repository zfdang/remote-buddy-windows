// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Platform.Windows.Input;

/// <summary>
/// Client running in the main application that manages an elevated child worker process
/// to stream Chromecast ETW button events over a secure local named pipe.
/// </summary>
public sealed class ChromecastWorkerClient : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle Pipe, out uint ClientProcessId);

    public event Action<ulong, RemoteButton, bool>? ButtonEvent;
    public event Action<ulong>? TruncatedPacketDetected;
    public event Action<ulong>? DeviceDisconnected;
    public event Action<ulong>? SourceBound;
    public event Action<bool>? Disconnected;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _controlWriteLock = new(1, 1);
    private readonly object _lock = new();
    private NamedPipeServerStream? _pipeServer;
    private Process? _workerProcess;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _startCts;
    private Task? _readTask;
    private ulong[] _latestReadyAddresses = [];
    private volatile bool _isRunning;
    private volatile bool _intentionalStop;

    public bool IsRunning => _isRunning;
    public bool WasElevationDeclined { get; private set; }
    public string? LastError { get; private set; }

    public async Task<bool> StartAsync(IReadOnlyList<EtwButtonSource> sources, CancellationToken ct = default)
    {
        WasElevationDeclined = false;
        LastError = null;

        CancellationTokenSource localStartCts;
        lock (_lock)
        {
            if (_isRunning) return true;
            _startCts?.Cancel();
            _startCts?.Dispose();
            localStartCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _startCts = localStartCts;
            _intentionalStop = false;
        }

        try
        {
            await _lifecycleLock.WaitAsync(localStartCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        NamedPipeServerStream? pipeServer = null;
        Process? workerProcess = null;
        CancellationTokenSource? loopCts = null;

        try
        {
            if (_isRunning || localStartCts.IsCancellationRequested) return _isRunning;

            string pipeName = "RemoteBuddy_ChromecastEtw_" + Guid.NewGuid().ToString("N");
            var pipeSecurity = new PipeSecurity();
            using var currentIdentity = WindowsIdentity.GetCurrent();
            if (currentIdentity.User == null)
            {
                LastError = "Unable to resolve current Windows user SID for named pipe ACL";
                AppLogger.Warn(LastError, "ChromecastWorkerClient");
                return false;
            }
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                currentIdentity.User,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            pipeServer = NamedPipeServerStreamAcl.Create(
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
                LastError = "Unable to locate executable path for ETW worker";
                AppLogger.Warn("Unable to determine executable path to launch Chromecast worker", "ChromecastWorkerClient");
                DisposeResources(pipeServer, null, null);
                return false;
            }

            string workerArgs = $"--chromecast-worker {pipeName} {Environment.ProcessId} {Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sources))}";
            if (!AppLogger.IsEnabled)
            {
                workerArgs += " --no-log";
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = workerArgs,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            AppLogger.Info($"Spawning elevated Chromecast worker via pipe '{pipeName}'", "ChromecastWorkerClient");
            workerProcess = await Task.Run(() => Process.Start(psi), localStartCts.Token).ConfigureAwait(false);
            if (workerProcess == null)
            {
                LastError = "Process.Start returned null";
                AppLogger.Warn("Process.Start returned null for Chromecast worker", "ChromecastWorkerClient");
                DisposeResources(pipeServer, null, null);
                return false;
            }

            using var timeoutCts = new CancellationTokenSource(12000);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(localStartCts.Token, timeoutCts.Token);

            await pipeServer.WaitForConnectionAsync(linkedCts.Token).ConfigureAwait(false);

            if (!GetNamedPipeClientProcessId(pipeServer.SafePipeHandle, out uint clientPid) ||
                (clientPid != (uint)workerProcess.Id && clientPid != 0))
            {
                LastError = $"Unexpected worker PID connected to named pipe (expected={workerProcess.Id}, actual={clientPid})";
                AppLogger.Warn(LastError, "ChromecastWorkerClient");
                DisposeResources(pipeServer, workerProcess, null);
                return false;
            }

            byte[] readyBuf = new byte[1];
            int read = await pipeServer.ReadAsync(readyBuf.AsMemory(), linkedCts.Token).ConfigureAwait(false);
            if (read == 0 || readyBuf[0] != 0x01)
            {
                LastError = $"Worker startup failed (byte={(read > 0 ? readyBuf[0] : -1)})";
                AppLogger.Warn($"Chromecast worker reported startup failure (byte={(read > 0 ? readyBuf[0] : -1)})", "ChromecastWorkerClient");
                DisposeResources(pipeServer, workerProcess, null);
                return false;
            }

            loopCts = new CancellationTokenSource();
            lock (_lock)
            {
                if (_intentionalStop || localStartCts.IsCancellationRequested)
                {
                    DisposeResources(pipeServer, workerProcess, loopCts);
                    return false;
                }
                _pipeServer = pipeServer;
                _workerProcess = workerProcess;
                _cts = loopCts;
                _isRunning = true;
                var serverRef = pipeServer;
                var ctRef = loopCts.Token;
                _readTask = Task.Run(() => ListenToPipeAsync(serverRef, ctRef), CancellationToken.None);
            }

            AppLogger.Info("ChromecastWorkerClient: Received ready confirmation (0x01) from worker", "ChromecastWorkerClient");
            ulong[] readySnapshot;
            lock (_lock) readySnapshot = _latestReadyAddresses;
            if (readySnapshot.Length > 0)
            {
                await SendControlFrameAsync(BuildSetReadyAddressesFrame(readySnapshot), localStartCts.Token).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "Timed out or cancelled waiting for ETW worker";
            AppLogger.Warn("Timeout or cancellation waiting for Chromecast worker connection/handshake", "ChromecastWorkerClient");
            DisposeResources(pipeServer, workerProcess, loopCts);
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223 /* ERROR_CANCELLED (UAC declined) */)
        {
            WasElevationDeclined = true;
            LastError = "用户取消了管理员权限提升 (UAC)";
            AppLogger.Warn("User declined UAC elevation prompt for Chromecast ETW button receiver", "ChromecastWorkerClient");
            DisposeResources(pipeServer, workerProcess, loopCts);
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLogger.Error("Exception starting ChromecastWorkerClient", ex, "ChromecastWorkerClient");
            DisposeResources(pipeServer, workerProcess, loopCts);
            return false;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_startCts, localStartCts)) _startCts = null;
            }
            localStartCts.Dispose();
            _lifecycleLock.Release();
        }
    }

    private async Task ListenToPipeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            byte[] typeBuf = new byte[1];
            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                int read = await server.ReadAsync(typeBuf.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (read == 0) break; // Pipe disconnected / EOF

                byte msgType = typeBuf[0];
                if (msgType == ChromecastWorkerHost.MsgTypeButtonEvent)
                {
                    byte[] payload = new byte[10];
                    await ReadExactAsync(server, payload, ct).ConfigureAwait(false);
                    var button = (RemoteButton)payload[0];
                    bool isDown = payload[1] != 0;
                    AppLogger.Info($"ChromecastWorkerClient: Received button {button}, isDown={isDown} from worker pipe", "ChromecastWorkerClient");
                    ButtonEvent?.Invoke(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(2)), button, isDown);
                }
                else if (msgType is ChromecastWorkerHost.MsgTypeTruncatedDetected
                    or ChromecastWorkerHost.MsgTypeDeviceDisconnected
                    or ChromecastWorkerHost.MsgTypeSourceBound)
                {
                    var payload = new byte[8];
                    await ReadExactAsync(server, payload, ct).ConfigureAwait(false);
                    ulong address = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload);
                    if (msgType == ChromecastWorkerHost.MsgTypeTruncatedDetected)
                        TruncatedPacketDetected?.Invoke(address);
                    else if (msgType == ChromecastWorkerHost.MsgTypeDeviceDisconnected)
                        DeviceDisconnected?.Invoke(address);
                    else
                        SourceBound?.Invoke(address);
                }
                else
                {
                    AppLogger.Warn($"Unknown ETW worker pipe message type 0x{msgType:X2}; terminating listener to avoid frame desync.", "ChromecastWorkerClient");
                    break;
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
            bool unexpected = false;
            lock (_lock)
            {
                if (ReferenceEquals(_pipeServer, server))
                {
                    _isRunning = false;
                    unexpected = !_intentionalStop;
                    CleanupLocked();
                    disconnected = true;
                }
            }
            if (disconnected) Disconnected?.Invoke(unexpected);
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int r = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (r == 0) throw new EndOfStreamException();
            total += r;
        }
    }

    internal async Task SendReadyAddressesAsync(ulong[] addresses, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _latestReadyAddresses = (ulong[])addresses.Clone();
        }
        if (!_isRunning) return;
        await SendControlFrameAsync(BuildSetReadyAddressesFrame(addresses), ct).ConfigureAwait(false);
    }

    internal Task SendBeginProbeAsync(ulong address, ulong round, CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;
        return SendControlFrameAsync(BuildBeginProbeFrame(address, round), ct);
    }

    internal Task SendEndProbeAsync(ulong address, ulong round, CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;
        return SendControlFrameAsync(BuildEndProbeFrame(address, round), ct);
    }

    internal static byte[] BuildSetReadyAddressesFrame(IReadOnlyList<ulong> addresses)
    {
        ushort count = (ushort)Math.Min(addresses.Count, 256);
        var frame = new byte[3 + count * 8];
        frame[0] = ChromecastWorkerHost.CmdSetReadyAddresses;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), count);
        for (int i = 0; i < count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(3 + i * 8, 8), addresses[i]);
        }
        return frame;
    }

    internal static byte[] BuildBeginProbeFrame(ulong address, ulong round)
    {
        var frame = new byte[17];
        frame[0] = ChromecastWorkerHost.CmdBeginProbe;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1, 8), address);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(9, 8), round);
        return frame;
    }

    internal static byte[] BuildEndProbeFrame(ulong address, ulong round)
    {
        var frame = new byte[17];
        frame[0] = ChromecastWorkerHost.CmdEndProbe;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1, 8), address);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(9, 8), round);
        return frame;
    }

    private async Task SendControlFrameAsync(byte[] frame, CancellationToken ct)
    {
        NamedPipeServerStream? server;
        lock (_lock) server = _pipeServer;
        if (server == null || !server.IsConnected) return;

        await _controlWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_lock) server = _pipeServer;
            if (server == null || !server.IsConnected) return;
            await server.WriteAsync(frame, ct).ConfigureAwait(false);
            await server.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _controlWriteLock.Release();
        }
    }

    public void Stop()
    {
        bool wasRunning;
        lock (_lock)
        {
            _intentionalStop = true;
            try { _startCts?.Cancel(); } catch { }
            wasRunning = _isRunning;
            if (!_isRunning) return;
            _isRunning = false;
            CleanupLocked();
        }

        if (wasRunning)
        {
            Disconnected?.Invoke(false);
        }
    }

    private void CleanupLocked()
    {
        var cts = _cts;
        var pipe = _pipeServer;
        var proc = _workerProcess;

        _pipeServer = null;
        _workerProcess = null;
        _cts = null;
        _readTask = null;

        DisposeResources(pipe, proc, cts);
    }

    private static void DisposeResources(NamedPipeServerStream? pipe, Process? proc, CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); } catch { }
        try { pipe?.Dispose(); } catch { }
        cts?.Dispose();

        if (proc != null)
        {
            // Best-effort wait & kill off-thread so Windows UIPI (Access Denied from Medium to High integrity) never throws or blocks UI
            _ = Task.Run(() =>
            {
                try
                {
                    if (!proc.HasExited && !proc.WaitForExit(1500))
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            });
        }
    }

    public void Dispose()
    {
        Stop();
        // Do not dispose _lifecycleLock while StartAsync may be releasing; let GC reclaim WaitHandle when unused.
    }
}
