// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Win32;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Services;

public static class StartupService
{
    private const string AppName = "RemoteBuddy";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsLaunchAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var val = key?.GetValue(AppName) as string;
            if (string.IsNullOrEmpty(val)) return false;
            string? currentExe = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "RemoteBuddy.exe");
            if (string.IsNullOrEmpty(currentExe)) return true;

            string? registeredExe = ExtractExecutablePath(val);
            if (string.IsNullOrEmpty(registeredExe)) return false;

            try
            {
                return string.Equals(Path.GetFullPath(registeredExe), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(registeredExe, currentExe, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to query Run registry key: {ex.Message}", "StartupService");
            return false;
        }
    }

    internal static string? ExtractExecutablePath(string? registryValue)
    {
        if (string.IsNullOrWhiteSpace(registryValue)) return null;
        string trimmed = registryValue.Trim();
        if (trimmed.StartsWith('"'))
        {
            int endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote].Trim() : null;
        }

        const string minimizedFlag = " --minimized";
        if (trimmed.EndsWith(minimizedFlag, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^minimizedFlag.Length].TrimEnd();
        }

        int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            int afterExe = exeIndex + 4;
            if (afterExe == trimmed.Length || char.IsWhiteSpace(trimmed[afterExe]))
            {
                return trimmed[..afterExe].Trim();
            }
        }

        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    public static bool SetLaunchAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enable)
            {
                string? exePath = Environment.ProcessPath
                    ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(AppContext.BaseDirectory, "RemoteBuddy.exe");
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\" --minimized");
                    AppLogger.Info($"Registered startup run key for '{exePath}'", "StartupService");
                    return true;
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
                AppLogger.Info("Removed startup run key", "StartupService");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to update startup run key: {ex.Message}", ex, "StartupService");
            return false;
        }

        return false;
    }
}
