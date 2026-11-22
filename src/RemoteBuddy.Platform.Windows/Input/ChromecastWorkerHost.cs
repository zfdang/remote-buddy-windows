// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
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
    public const byte MsgTypeConnectionHandleBound = 0x03;

    public const byte CmdSetKnownGattHandles = 0x01;
    public const byte CmdResetBinding = 0x02;

    public static void RunWorker(string pipeName)
    {
        AppLogger.Info($"ChromecastWorkerHost starting for pipe '{pipeName}' (PID: {Environment.ProcessId})", "ChromecastWorkerHost");

        try
        {
            BthPortRegistryHelper.TryConfigure();

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(10000);
            AppLogger.Info("ChromecastWorkerHost connected to parent pipe", "ChromecastWorkerHost");

            using var receiver = new ChromecastEtwReceiver();

            receiver.ButtonEvent += (button, isDown) =>
            {
                try
                {
                    AppLogger.Info($"ChromecastWorkerHost: Dispatched button {button}, isDown={isDown} to pipe", "ChromecastWorkerHost");
                    byte[] msg = [MsgTypeButtonEvent, (byte)button, (byte)(isDown ? 1 : 0)];
                    lock (pipe)
                    {
                        pipe.Write(msg, 0, 3);
                        pipe.Flush();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Failed to write button event to pipe: {ex.Message}", "ChromecastWorkerHost");
                }
            };

            receiver.TruncatedPacketDetected += () =>
            {
                try
                {
                    AppLogger.Warn("ChromecastWorkerHost: Dispatched truncated packet notification to pipe", "ChromecastWorkerHost");
                    byte[] msg = [MsgTypeTruncatedDetected];
                    lock (pipe)
                    {
                        pipe.Write(msg, 0, 1);
                        pipe.Flush();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Failed to write truncated packet notification to pipe: {ex.Message}", "ChromecastWorkerHost");
                }
            };

            receiver.ConnectionHandleBound += (handle) =>
            {
                try
                {
                    AppLogger.Info($"ChromecastWorkerHost: Dispatched connection handle 0x{handle:X4} to pipe", "ChromecastWorkerHost");
                    byte[] msg = [MsgTypeConnectionHandleBound, (byte)(handle & 0xFF), (byte)((handle >> 8) & 0xFF)];
                    lock (pipe)
                    {
                        pipe.Write(msg, 0, 3);
                        pipe.Flush();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Failed to write bound handle to pipe: {ex.Message}", "ChromecastWorkerHost");
                }
            };

            if (!receiver.Start())
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

            // Read control commands from parent pipe in the main loop until pipe disconnects
            byte[] cmdBuf = new byte[1];
            while (pipe.IsConnected)
            {
                int read = pipe.Read(cmdBuf, 0, 1);
                if (read == 0) break; // Parent closed pipe

                byte cmd = cmdBuf[0];
                if (cmd == CmdSetKnownGattHandles)
                {
                    int count = pipe.ReadByte();
                    if (count < 0) break;
                    var handles = new List<ushort>(count);
                    byte[] hBuf = new byte[2];
                    for (int i = 0; i < count; i++)
                    {
                        if (pipe.Read(hBuf, 0, 2) < 2) break;
                        handles.Add(BinaryPrimitives.ReadUInt16LittleEndian(hBuf));
                    }
                    receiver.SetKnownGattHandles(handles);
                }
                else if (cmd == CmdResetBinding)
                {
                    receiver.ResetConnectionBinding();
                }
            }

            receiver.Stop();
            AppLogger.Info("ChromecastWorkerHost parent pipe closed, exiting cleanly", "ChromecastWorkerHost");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exception in ChromecastWorkerHost execution", ex, "ChromecastWorkerHost");
        }
    }
}
