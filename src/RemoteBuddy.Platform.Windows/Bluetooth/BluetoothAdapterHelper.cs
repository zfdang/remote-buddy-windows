// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.Platform.Windows.Bluetooth;

/// <summary>
/// Helper to detect, verify permissions, and restart the Windows Bluetooth hardware adapter (PnP device).
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

    public static void OpenDeviceManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open Device Manager: {ex.Message}", "BluetoothAdapterHelper");
        }
    }

    public static bool RestartAppAsAdministrator()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            return Process.Start(psi) != null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to restart app as administrator: {ex.Message}", "BluetoothAdapterHelper");
            return false;
        }
    }

    public static bool IsValidPnpInstanceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 256 || instanceId.EndsWith('\\'))
            return false;

        foreach (char ch in instanceId)
        {
            bool valid = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '\\' or '_' or '&' or '-' or '{' or '}' or '.' or ':' or '#';
            if (!valid) return false;
        }
        return true;
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
                        string? id = key.GetValue(i.ToString())?.ToString()?.Trim();
                        if (IsValidPnpInstanceId(id))
                        {
                            result.Add(id!);
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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    using var cts = new CancellationTokenSource(5000);
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                    var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
                    try
                    {
                        proc.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
                        string output = stdoutTask.GetAwaiter().GetResult();
                        _ = stderrTask.GetAwaiter().GetResult();
                        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                        {
                            string trimmed = line.Trim();
                            if (IsValidPnpInstanceId(trimmed))
                            {
                                result.Add(trimmed);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        AppLogger.Warn("Timed out querying Bluetooth adapters via PowerShell", "BluetoothAdapterHelper");
                        try { proc.Kill(entireProcessTree: true); } catch { }
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
                AppLogger.Info($"Attempting to restart Bluetooth adapter device: {id}", "BluetoothAdapterHelper");
                bool ok = await TryRestartAdapterPnpAsync(id, isAdmin);
                if (ok)
                {
                    anySuccess = true;
                    AppLogger.Info($"Successfully cycled Bluetooth adapter device: {id}", "BluetoothAdapterHelper");
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
                ? "未能自动重启该硬件。请在设备管理器中找到 Bluetooth 适配器，右键“禁用设备”再“启用设备”（或重启一次电脑）。"
                : "需要管理员权限重置蓝牙驱动。请右键以管理员身份运行本程序。");
        }
    }

    private static async Task<bool> TryRestartAdapterPnpAsync(string id, bool isAdmin)
    {
        if (!IsValidPnpInstanceId(id))
        {
            AppLogger.Warn($"Rejected invalid PnP instance ID: '{id}'", "BluetoothAdapterHelper");
            return false;
        }

        // 1. Try pnputil /restart-device
        int code = await RunCommandAsync("pnputil.exe", $"/restart-device \"{id}\"", isAdmin);
        if (code == 0) return true;

        AppLogger.Info($"pnputil /restart-device returned exit code {code}. Trying disable-device then enable-device...", "BluetoothAdapterHelper");

        // 2. Try pnputil /disable-device then /enable-device
        int disableCode = await RunCommandAsync("pnputil.exe", $"/disable-device \"{id}\"", isAdmin);
        await Task.Delay(800);
        int enableCode = await RunCommandAsync("pnputil.exe", $"/enable-device \"{id}\"", isAdmin);
        if (enableCode == 0) return true;

        AppLogger.Info($"pnputil disable/enable returned ({disableCode}, {enableCode}). Trying PowerShell PnP cmdlets...", "BluetoothAdapterHelper");

        // 3. Try PowerShell Disable-PnpDevice / Enable-PnpDevice
        string psScript = $"Disable-PnpDevice -InstanceId '{id}' -Confirm:$false -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800; Enable-PnpDevice -InstanceId '{id}' -Confirm:$false -ErrorAction SilentlyContinue; Restart-Service bthserv -Force -ErrorAction SilentlyContinue";
        int psCode = await RunCommandAsync("powershell.exe", $"-NoProfile -Command \"{psScript}\"", isAdmin);
        if (psCode == 0) return true;

        // 4. Try restarting bthserv service directly
        int svcCode = await RunCommandAsync("powershell.exe", "-NoProfile -Command \"Restart-Service bthserv -Force\"", isAdmin);
        return svcCode == 0;
    }

    private static async Task<int> RunCommandAsync(string fileName, string args, bool isAdmin)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = !isAdmin,
                Verb = isAdmin ? "" : "runas",
                CreateNoWindow = isAdmin,
                RedirectStandardOutput = isAdmin,
                RedirectStandardError = isAdmin
            };

            using var proc = Process.Start(psi);
            if (proc == null) return -1;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task? stdoutTask = isAdmin ? proc.StandardOutput.ReadToEndAsync(timeoutCts.Token) : null;
            Task? stderrTask = isAdmin ? proc.StandardError.ReadToEndAsync(timeoutCts.Token) : null;
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                if (stdoutTask != null) await stdoutTask.ConfigureAwait(false);
                if (stderrTask != null) await stderrTask.ConfigureAwait(false);
                return proc.ExitCode;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warn($"RunCommandAsync timed out for '{fileName} {args}'", "BluetoothAdapterHelper");
                try { proc.Kill(entireProcessTree: true); } catch { }
                return -1;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"RunCommandAsync failed for '{fileName} {args}': {ex.Message}", "BluetoothAdapterHelper");
            return -1;
        }
    }
}
