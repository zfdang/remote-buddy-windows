// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Platform.Windows.Input;

/// <summary>
/// Hosted worker process that runs with Administrator elevation to capture
/// Chromecast ETW Bluetooth ACL events and pipe them back to the main application.
/// </summary>
public static class ChromecastWorkerHost
{
    public const byte MsgTypeButtonEvent = 0x01;
    public const byte MsgTypeTruncatedDetected = 0x02;
    public const byte MsgTypeDeviceDisconnected = 0x03;
    public const byte MsgTypeSourceBound = 0x04;

    public const byte CmdSetReadyAddresses = 0x10;
    public const byte CmdBeginProbe = 0x11;
    public const byte CmdEndProbe = 0x12;

    public static void RunWorker(string pipeName, IReadOnlyList<EtwButtonSource> sources)
        => RunWorker(pipeName, parentPid: 0, sources);

    public static void RunWorker(string pipeName, int parentPid, IReadOnlyList<EtwButtonSource> sources)
    {
        AppLogger.Info($"ChromecastWorkerHost starting for pipe '{pipeName}' (PID: {Environment.ProcessId}, ParentPID: {parentPid}, Sources: {sources.Count})", "ChromecastWorkerHost");

        try
        {
            RunWorkerAsync(pipeName, parentPid, sources).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exception in ChromecastWorkerHost execution", ex, "ChromecastWorkerHost");
        }
    }

    private static async Task RunWorkerAsync(string pipeName, int parentPid, IReadOnlyList<EtwButtonSource> sources)
    {
        BthPortRegistryHelper.TryConfigure();

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(10000).ConfigureAwait(false);
        AppLogger.Info("ChromecastWorkerHost connected to parent pipe", "ChromecastWorkerHost");

        using var receiver = new ChromecastEtwReceiver();
        using var shutdownCts = new CancellationTokenSource();
        var outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        void Enqueue(byte kind, ulong address, RemoteButton button = RemoteButton.None, bool down = false)
        {
            bool isButton = kind == MsgTypeButtonEvent;
            var msg = new byte[isButton ? 11 : 9];
            msg[0] = kind;
            if (isButton)
            {
                msg[1] = (byte)button;
                msg[2] = down ? (byte)1 : (byte)0;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(isButton ? 3 : 1), address);
            outbound.Writer.TryWrite(msg);
        }

        receiver.ButtonEvent += (address, button, down) => Enqueue(MsgTypeButtonEvent, address, button, down);
        receiver.TruncatedPacketDetected += address => Enqueue(MsgTypeTruncatedDetected, address);
        receiver.DeviceDisconnected += address => Enqueue(MsgTypeDeviceDisconnected, address);
        receiver.SourceBound += address => Enqueue(MsgTypeSourceBound, address);
        receiver.UnexpectedStop += reason =>
        {
            AppLogger.Warn($"ChromecastWorkerHost receiver stopped unexpectedly: {reason}", "ChromecastWorkerHost");
            shutdownCts.Cancel();
        };

        if (!receiver.Start(sources))
        {
            AppLogger.Error("ChromecastWorkerHost: receiver.Start() failed", component: "ChromecastWorkerHost");
            await pipe.WriteAsync(new byte[] { 0x00 }).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);
            return;
        }

        // Confirm ETW is running and ready to parent (0x01)
        await pipe.WriteAsync(new byte[] { 0x01 }).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
        AppLogger.Info("ChromecastWorkerHost: Sent ready handshake (0x01) to parent", "ChromecastWorkerHost");

        var writerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in outbound.Reader.ReadAllAsync(shutdownCts.Token).ConfigureAwait(false))
                {
                    await pipe.WriteAsync(msg, shutdownCts.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(shutdownCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex)
            {
                AppLogger.Debug($"Button worker pipe closed while writing: {ex.Message}", "ChromecastWorkerHost");
                shutdownCts.Cancel();
            }
        });

        var pipeReadTask = Task.Run(async () =>
        {
            try
            {
                await ReadControlMessagesAsync(pipe, receiver, shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                AppLogger.Debug($"Worker control pipe read ended: {ex.Message}", "ChromecastWorkerHost");
            }
            finally
            {
                shutdownCts.Cancel();
            }
        });

        Task? parentWatchTask = null;
        if (parentPid > 0)
        {
            parentWatchTask = Task.Run(async () =>
            {
                try
                {
                    using var parentProc = Process.GetProcessById(parentPid);
                    await parentProc.WaitForExitAsync(shutdownCts.Token).ConfigureAwait(false);
                    AppLogger.Warn($"ChromecastWorkerHost detected parent PID {parentPid} exit; terminating worker", "ChromecastWorkerHost");
                }
                catch (ArgumentException)
                {
                    AppLogger.Warn($"ChromecastWorkerHost parent PID {parentPid} no longer exists; terminating worker", "ChromecastWorkerHost");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Parent process monitor exited: {ex.Message}", "ChromecastWorkerHost");
                }
                finally
                {
                    shutdownCts.Cancel();
                }
            });
        }

        await (parentWatchTask != null
            ? Task.WhenAny(pipeReadTask, parentWatchTask)
            : pipeReadTask).ConfigureAwait(false);

        shutdownCts.Cancel();
        outbound.Writer.TryComplete();
        receiver.Stop();
        try { await Task.WhenAny(writerTask, Task.Delay(500)).ConfigureAwait(false); } catch { }
        AppLogger.Info("ChromecastWorkerHost parent terminated or pipe closed, exiting cleanly", "ChromecastWorkerHost");
    }

    internal static async Task ReadControlMessagesAsync(Stream stream, ChromecastEtwReceiver receiver, CancellationToken ct)
    {
        byte[] cmdBuf = new byte[1];
        while (!ct.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(cmdBuf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0) break;

            byte cmd = cmdBuf[0];
            if (cmd == CmdSetReadyAddresses)
            {
                byte[] countBuf = new byte[2];
                await ReadExactAsync(stream, countBuf, ct).ConfigureAwait(false);
                ushort count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(countBuf);
                if (count > 256) throw new InvalidDataException($"Invalid ready address count {count}");
                var addresses = new ulong[count];
                if (count > 0)
                {
                    byte[] addrBuf = new byte[count * 8];
                    await ReadExactAsync(stream, addrBuf, ct).ConfigureAwait(false);
                    for (int i = 0; i < count; i++)
                    {
                        addresses[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(addrBuf.AsSpan(i * 8, 8));
                    }
                }
                receiver.SetReadyAddresses(addresses);
            }
            else if (cmd == CmdBeginProbe)
            {
                byte[] payload = new byte[16];
                await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
                ulong address = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0, 8));
                ulong round = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8, 8));
                receiver.BeginProbe(address, round);
            }
            else if (cmd == CmdEndProbe)
            {
                byte[] payload = new byte[16];
                await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
                ulong address = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0, 8));
                ulong round = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8, 8));
                receiver.EndProbe(address, round);
            }
            else
            {
                AppLogger.Warn($"Unknown parent control command 0x{cmd:X2}; terminating worker control reader.", "ChromecastWorkerHost");
                break;
            }
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
}
