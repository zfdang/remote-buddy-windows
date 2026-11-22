// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.Platform.Windows.Bluetooth;

/// <summary>
/// Helper to detect and restart the Windows Bluetooth hardware adapter (PnP device).
/// Restarting the adapter forces the kernel driver (bthport.sys) to reload its parameters,
/// allowing EtwLogSensitiveData=1 to take effect without rebooting Windows.
/// </summary>
public static class BluetoothAdapterHelper
{
    public static Func<Task<(bool Success, string Message)>>? CustomRestartHandler { get; set; }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static List<string> GetBluetoothAdapterInstanceIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Check registry under Services for Enum entries (BTHUSB, BthMini, IBTUSB, RtkBtfilter, etc.)
        string[] services = ["BTHUSB", "BthMini", "IBTUSB", "RtkBtfilter"];
        foreach (var svc in services)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}\Enum");
                if (key != null)
                {
                    int count = Convert.ToInt32(key.GetValue("Count", 0));
                    for (int i = 0; i < count; i++)
                    {
                        string? id = key.GetValue(i.ToString())?.ToString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            result.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Failed to read registry enum for {svc}: {ex.Message}", "BluetoothAdapterHelper");
            }
        }

        // 2. Fall back to PowerShell query if registry did not yield instances
        if (result.Count == 0)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"(Get-PnpDevice -Class Bluetooth | Where-Object { $_.InstanceId -match '^(USB|PCI)' }).InstanceId\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            result.Add(trimmed);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to query Bluetooth adapters via PowerShell: {ex.Message}", "BluetoothAdapterHelper");
            }
        }

        return result.ToList();
    }

    public static async Task<(bool Success, string Message)> RestartBluetoothAdapterAsync()
    {
        if (CustomRestartHandler != null)
        {
            return await CustomRestartHandler();
        }

        // Ensure BthPort registry parameters are configured first
        BthPortRegistryHelper.TryConfigure();

        var adapterIds = GetBluetoothAdapterInstanceIds();
        if (adapterIds.Count == 0)
        {
            return (false, "未检测到可重启的蓝牙硬件适配器。");
        }

        bool isAdmin = IsAdministrator();
        AppLogger.Info($"RestartBluetoothAdapterAsync: Found {adapterIds.Count} adapter(s). IsAdmin={isAdmin}", "BluetoothAdapterHelper");

        bool anySuccess = false;
        foreach (var id in adapterIds)
        {
            try
            {
                AppLogger.Info($"Restarting Bluetooth adapter device: {id}", "BluetoothAdapterHelper");
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/restart-device \"{id}\"",
                    UseShellExecute = !isAdmin,
                    Verb = isAdmin ? "" : "runas",
                    CreateNoWindow = isAdmin,
                    RedirectStandardOutput = isAdmin,
                    RedirectStandardError = isAdmin
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode == 0)
                    {
                        anySuccess = true;
                        AppLogger.Info($"Successfully restarted Bluetooth adapter: {id}", "BluetoothAdapterHelper");
                    }
                    else
                    {
                        string err = isAdmin ? proc.StandardError.ReadToEnd() : "";
                        AppLogger.Warn($"pnputil returned exit code {proc.ExitCode} for {id}: {err}", "BluetoothAdapterHelper");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Exception restarting Bluetooth adapter {id}", ex, "BluetoothAdapterHelper");
            }
        }

        if (anySuccess)
        {
            return (true, "蓝牙适配器已重启！驱动脱敏设置已生效，请重新按下遥控器按键测试。");
        }
        else
        {
            return (false, isAdmin
                ? "重启蓝牙适配器失败，请检查驱动状态或在设备管理器中手动禁用再启用。"
                : "需要管理员权限来重启蓝牙适配器。建议以管理员身份运行本程序。");
        }
    }
}
