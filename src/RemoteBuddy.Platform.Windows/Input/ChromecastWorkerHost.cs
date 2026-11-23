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

    public static void RunWorker(string pipeName, Guid marker, EtwProtocolKind protocolKind = EtwProtocolKind.GoogleG9N9N)
    {
        AppLogger.Info($"ChromecastWorkerHost starting for pipe '{pipeName}' (PID: {Environment.ProcessId}, Protocol: {protocolKind})", "ChromecastWorkerHost");

        try
        {
            BthPortRegistryHelper.TryConfigure();

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
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

            if (!receiver.Start(marker, protocolKind))
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
