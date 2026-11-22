// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Win32;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Input;

/// <summary>
/// Verifies and configures Windows Bluetooth driver (BthPort) registry settings
/// required for real-time ETW event capture of Chromecast (G9N9N) button ACL frames.
/// </summary>
public static class BthPortRegistryHelper
{
    private const string BthPortParametersKey = @"SYSTEM\CurrentControlSet\Services\BthPort\Parameters";
    private const string EtwLogSensitiveData = "EtwLogSensitiveData";
    private const string MaxEtwBytes = "MaxEtwBytes";
    private const string EtwDropLargeEvents = "EtwDropLargeEvents";

    public static bool IsConfigured()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BthPortParametersKey, false);
            if (key == null) return false;

            object? sensitive = key.GetValue(EtwLogSensitiveData);
            object? maxBytes = key.GetValue(MaxEtwBytes);
            object? dropLarge = key.GetValue(EtwDropLargeEvents);

            bool sensitiveOk = sensitive is int s && s == 1;
            bool maxBytesOk = maxBytes is int m && m >= 1024;
            bool dropLargeOk = dropLarge is int d && d == 0;

            return sensitiveOk && maxBytesOk && dropLargeOk;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Failed to check BthPort registry settings: {ex.Message}", "BthPortRegistryHelper");
            return false;
        }
    }

    public static bool TryConfigure()
    {
        if (IsConfigured()) return true;

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(BthPortParametersKey, RegistryKeyPermissionCheck.ReadWriteSubTree);
            if (key == null) return false;

            key.SetValue(EtwLogSensitiveData, 1, RegistryValueKind.DWord);
            key.SetValue(MaxEtwBytes, 1024, RegistryValueKind.DWord);
            key.SetValue(EtwDropLargeEvents, 0, RegistryValueKind.DWord);
            key.Flush();

            AppLogger.Info("Successfully configured BthPort ETW registry parameters", "BthPortRegistryHelper");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            AppLogger.Warn("Administrator privileges required to write BthPort registry parameters", "BthPortRegistryHelper");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exception configuring BthPort registry parameters", ex, "BthPortRegistryHelper");
            return false;
        }
    }
}
