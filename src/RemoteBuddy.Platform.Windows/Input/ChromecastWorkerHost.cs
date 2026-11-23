// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Principal;
using System.IO.Pipes;
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

    public static void RunWorker(string pipeName, IReadOnlyList<EtwButtonSource> sources)
    {
        AppLogger.Info($"ChromecastWorkerHost starting for pipe '{pipeName}' (PID: {Environment.ProcessId}, Sources: {sources.Count})", "ChromecastWorkerHost");

        try
        {
            BthPortRegistryHelper.TryConfigure();

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            pipe.Connect(10000);
            AppLogger.Info("ChromecastWorkerHost connected to parent pipe", "ChromecastWorkerHost");

            using var receiver = new ChromecastEtwReceiver();

            void Send(byte kind, ulong address, RemoteButton button = RemoteButton.None, bool down = false)
            {
                try
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
                    lock (pipe)
                    {
                        pipe.Write(msg);
                        pipe.Flush();
                    }
                }
                catch (IOException ex)
                {
                    AppLogger.Debug($"Button worker pipe closed: {ex.Message}", "ChromecastWorkerHost");
                }
            }

            receiver.ButtonEvent += (address, button, down) => Send(MsgTypeButtonEvent, address, button, down);
            receiver.TruncatedPacketDetected += address => Send(MsgTypeTruncatedDetected, address);
            receiver.DeviceDisconnected += address => Send(MsgTypeDeviceDisconnected, address);

            if (!receiver.Start(sources))
            {
                AppLogger.Error("ChromecastWorkerHost: receiver.Start() failed", component: "ChromecastWorkerHost");
                // Notify parent that startup failed (0x00)
                pipe.WriteByte(0x00);
                pipe.Flush();
                return;
            }

            // Confirm ETW is running and ready to parent (0x01)
            pipe.WriteByte(0x01);
            pipe.Flush();
            AppLogger.Info("ChromecastWorkerHost: Sent ready handshake (0x01) to parent", "ChromecastWorkerHost");

            // The parent holds the pipe open for the lifetime of this receiver.
            while (pipe.ReadByte() != -1) { }

            receiver.Stop();
            AppLogger.Info("ChromecastWorkerHost parent pipe closed, exiting cleanly", "ChromecastWorkerHost");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exception in ChromecastWorkerHost execution", ex, "ChromecastWorkerHost");
        }
    }
}
