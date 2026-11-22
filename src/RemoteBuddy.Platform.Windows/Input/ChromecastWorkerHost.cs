// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

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
                    byte[] msg = [(byte)button, (byte)(isDown ? 1 : 0)];
                    lock (pipe)
                    {
                        pipe.Write(msg, 0, 2);
                        pipe.Flush();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Failed to write button event to pipe: {ex.Message}", "ChromecastWorkerHost");
                }
            };

            if (!receiver.Start())
            {
                AppLogger.Error("ChromecastWorkerHost: receiver.Start() failed", component: "ChromecastWorkerHost");
                return;
            }

            // Keep worker alive until parent closes the pipe
            byte[] buffer = new byte[1];
            while (pipe.IsConnected)
            {
                int read = pipe.Read(buffer, 0, 1);
                if (read == 0) break; // End of stream
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
