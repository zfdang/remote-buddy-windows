// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.Services;

internal static class WindowsApplicationLauncher
{
    private const int SwRestore = 9;

    private sealed record PresetSpec(
        string[] ProcessNames,
        string[] ExeNames,
        string[] CandidatePaths,
        string? UriFallback = null);

    private static readonly IReadOnlyDictionary<PresetApplication, PresetSpec> PresetSpecs =
        new Dictionary<PresetApplication, PresetSpec>
        {
            [PresetApplication.RemoteBuddy] = new(["RemoteBuddy.App"], ["RemoteBuddy.App.exe"], []),
            [PresetApplication.WeChat] = new(
                ["WeChat", "Weixin"],
                ["WeChat.exe", "Weixin.exe"],
                [
                    @"%ProgramFiles%\Tencent\WeChat\WeChat.exe",
                    @"%ProgramFiles(x86)%\Tencent\WeChat\WeChat.exe",
                    @"%ProgramFiles%\Tencent\Weixin\Weixin.exe"
                ]),
            [PresetApplication.WeCom] = new(
                ["WXWork"],
                ["WXWork.exe"],
                [
                    @"%ProgramFiles%\WXWork\WXWork.exe",
                    @"%ProgramFiles(x86)%\WXWork\WXWork.exe"
                ]),
            [PresetApplication.Feishu] = new(
                ["Feishu", "Lark"],
                ["Feishu.exe", "Lark.exe"],
                [
                    @"%LocalAppData%\Programs\Feishu\Feishu.exe",
                    @"%ProgramFiles%\Feishu\Feishu.exe",
                    @"%LocalAppData%\Programs\Lark\Lark.exe"
                ]),
            [PresetApplication.DingTalk] = new(
                ["DingTalk"],
                ["DingtalkLauncher.exe", "DingTalk.exe"],
                [
                    @"%ProgramFiles(x86)%\DingDing\DingtalkLauncher.exe",
                    @"%ProgramFiles%\DingDing\DingtalkLauncher.exe"
                ]),
            [PresetApplication.Slack] = new(
                ["slack"],
                ["slack.exe"],
                [
                    @"%LocalAppData%\slack\slack.exe",
                    @"%ProgramFiles%\Slack\slack.exe"
                ]),
            [PresetApplication.ChatGPT] = new(
                ["ChatGPT"],
                ["ChatGPT.exe"],
                [
                    @"%LocalAppData%\Programs\ChatGPT\ChatGPT.exe",
                    @"%ProgramFiles%\OpenAI\ChatGPT\ChatGPT.exe"
                ]),
            [PresetApplication.Claude] = new(
                ["Claude"],
                ["Claude.exe"],
                [
                    @"%LocalAppData%\AnthropicClaude\Claude.exe",
                    @"%LocalAppData%\Programs\Claude\Claude.exe"
                ]),
            [PresetApplication.DeepSeek] = new(
                ["DeepSeek", "DeepSeek Harness"],
                ["DeepSeek.exe"],
                [
                    @"%LocalAppData%\Programs\DeepSeek\DeepSeek.exe"
                ]),
            [PresetApplication.WorkBuddy] = new(
                ["WorkBuddy"],
                ["WorkBuddy.exe"],
                [
                    @"%LocalAppData%\Programs\WorkBuddy\WorkBuddy.exe",
                    @"%ProgramFiles%\WorkBuddy\WorkBuddy.exe"
                ]),
            [PresetApplication.Antigravity] = new(
                ["Antigravity", "Antigravity Agent"],
                ["Antigravity.exe"],
                [
                    @"%LocalAppData%\Programs\Antigravity\Antigravity.exe",
                    @"%ProgramFiles%\Antigravity\Antigravity.exe"
                ]),
            [PresetApplication.Cursor] = new(
                ["Cursor"],
                ["Cursor.exe"],
                [
                    @"%LocalAppData%\Programs\cursor\Cursor.exe",
                    @"%ProgramFiles%\Cursor\Cursor.exe"
                ]),
            [PresetApplication.VSCode] = new(
                ["Code"],
                ["Code.exe"],
                [
                    @"%LocalAppData%\Programs\Microsoft VS Code\Code.exe",
                    @"%ProgramFiles%\Microsoft VS Code\Code.exe"
                ]),
            [PresetApplication.WindowsTerminal] = new(
                ["WindowsTerminal"],
                ["wt.exe"],
                [
                    @"%LocalAppData%\Microsoft\WindowsApps\wt.exe"
                ]),
            [PresetApplication.Chrome] = new(
                ["chrome"],
                ["chrome.exe"],
                [
                    @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
                    @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
                    @"%LocalAppData%\Google\Chrome\Application\chrome.exe"
                ]),
            [PresetApplication.Edge] = new(
                ["msedge"],
                ["msedge.exe"],
                [
                    @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                    @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
                ]),
            [PresetApplication.NeteaseMusic] = new(
                ["cloudmusic"],
                ["cloudmusic.exe"],
                [
                    @"%ProgramFiles(x86)%\Netease\CloudMusic\cloudmusic.exe",
                    @"%ProgramFiles%\Netease\CloudMusic\cloudmusic.exe"
                ]),
            [PresetApplication.Spotify] = new(
                ["Spotify"],
                ["Spotify.exe"],
                [
                    @"%AppData%\Spotify\Spotify.exe",
                    @"%LocalAppData%\Microsoft\WindowsApps\Spotify.exe"
                ]),
            [PresetApplication.Explorer] = new(
                ["explorer"],
                ["explorer.exe"],
                [
                    @"%SystemRoot%\explorer.exe"
                ])
        };

    private static readonly object CacheLock = new();
    private static HashSet<PresetApplication>? _cachedInstalled;
    private static DateTime _cacheTimestampUtc;

    public static IReadOnlySet<PresetApplication> DetectInstalledPresetApplications(bool forceRefresh = false)
    {
        lock (CacheLock)
        {
            if (!forceRefresh && _cachedInstalled != null && (DateTime.UtcNow - _cacheTimestampUtc) < TimeSpan.FromSeconds(30))
            {
                return _cachedInstalled;
            }
        }

        var installed = new HashSet<PresetApplication>
        {
            PresetApplication.RemoteBuddy,
            PresetApplication.Explorer
        };

        foreach (var (app, spec) in PresetSpecs)
        {
            if (installed.Contains(app)) continue;
            if (ResolvePresetLaunchTarget(app, spec) != null || IsAnyProcessRunning(spec.ProcessNames))
            {
                installed.Add(app);
            }
        }

        lock (CacheLock)
        {
            _cachedInstalled = installed;
            _cacheTimestampUtc = DateTime.UtcNow;
            return _cachedInstalled;
        }
    }

    public static bool LaunchOrActivatePreset(PresetApplication app, Action? activateRemoteBuddy = null)
    {
        if (app == PresetApplication.RemoteBuddy)
        {
            activateRemoteBuddy?.Invoke();
            return true;
        }

        if (!PresetSpecs.TryGetValue(app, out var spec)) return false;

        if (TryActivateRunningProcess(spec.ProcessNames))
        {
            return true;
        }

        string? launchTarget = ResolvePresetLaunchTarget(app, spec);
        if (string.IsNullOrWhiteSpace(launchTarget))
        {
            if (!string.IsNullOrWhiteSpace(spec.UriFallback))
            {
                return OpenUrl(spec.UriFallback);
            }
            AppLogger.Warn($"Preset application not found: {app}", "WindowsApplicationLauncher");
            return false;
        }

        return StartProcess(launchTarget);
    }

    public static bool LaunchOrActivateCustom(CustomApplicationProfile? profile)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.ExecutablePath)) return false;
        if (!File.Exists(profile.ExecutablePath))
        {
            AppLogger.Warn($"Custom application executable missing: {profile.ExecutablePath}", "WindowsApplicationLauncher");
            return false;
        }

        string processName = Path.GetFileNameWithoutExtension(profile.ExecutablePath);
        bool launched = TryActivateRunningProcess([processName]) || StartProcess(profile.ExecutablePath);
        if (!launched) return false;

        if (profile.FocusStrategy == CustomApplicationFocusStrategy.KeyboardShortcut &&
            !string.IsNullOrWhiteSpace(profile.FocusShortcut))
        {
            string shortcut = profile.FocusShortcut;
            _ = Task.Run(async () =>
            {
                await Task.Delay(260).ConfigureAwait(false);
                NativeInputSender.SendShortcutClick(shortcut);
            });
        }

        return true;
    }

    public static bool OpenUrl(string? rawUrl)
    {
        if (!CustomActionUrlPolicy.TryValidateUrl(rawUrl, out var uri) || uri == null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open URL '{uri.Scheme}': {ex.Message}", "WindowsApplicationLauncher");
            return false;
        }
    }

    private static bool StartProcess(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to launch '{filePath}': {ex.Message}", "WindowsApplicationLauncher");
            return false;
        }
    }

    private static string? ResolvePresetLaunchTarget(PresetApplication app, PresetSpec spec)
    {
        foreach (string rawPath in spec.CandidatePaths)
        {
            string expanded = Environment.ExpandEnvironmentVariables(rawPath);
            if (File.Exists(expanded))
            {
                return expanded;
            }
        }

        foreach (string exeName in spec.ExeNames)
        {
            if (TryResolveAppPathRegistry(exeName, out string? path))
            {
                return path;
            }
        }

        if (TryResolveStartMenuShortcut(app, spec.ProcessNames, out string? lnkPath))
        {
            return lnkPath;
        }

        return null;
    }

    private static bool TryResolveAppPathRegistry(string exeName, out string? resolvedPath)
    {
        resolvedPath = null;
        if (!OperatingSystem.IsWindows()) return false;

        string subKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    string cleaned = value.Trim().Trim('"');
                    if (File.Exists(cleaned))
                    {
                        resolvedPath = cleaned;
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    private static bool TryResolveStartMenuShortcut(PresetApplication app, string[] processNames, out string? shortcutPath)
    {
        shortcutPath = null;
        if (!OperatingSystem.IsWindows()) return false;

        string[] keywords = app switch
        {
            PresetApplication.WeChat => ["WeChat", "微信"],
            PresetApplication.WeCom => ["WeCom", "企业微信", "WXWork"],
            PresetApplication.Feishu => ["Feishu", "飞书", "Lark"],
            PresetApplication.DingTalk => ["DingTalk", "钉钉"],
            PresetApplication.VSCode => ["Visual Studio Code"],
            PresetApplication.WindowsTerminal => ["Terminal", "终端"],
            PresetApplication.NeteaseMusic => ["网易云音乐", "CloudMusic"],
            _ => [app.ToString(), .. processNames]
        };

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        ];

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (string lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileNameWithoutExtension(lnk);
                    if (keywords.Any(k => fileName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        shortcutPath = lnk;
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    private static bool IsAnyProcessRunning(string[] processNames)
    {
        foreach (string name in processNames)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool TryActivateRunningProcess(string[] processNames)
    {
        foreach (string name in processNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var proc in processes)
                {
                    using (proc)
                    {
                        if (proc.MainWindowHandle == IntPtr.Zero) continue;
                        if (IsIconic(proc.MainWindowHandle))
                        {
                            ShowWindow(proc.MainWindowHandle, SwRestore);
                        }
                        SetForegroundWindow(proc.MainWindowHandle);
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
