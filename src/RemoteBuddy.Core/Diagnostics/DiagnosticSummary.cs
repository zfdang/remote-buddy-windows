// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;

namespace RemoteBuddy.Core.Diagnostics;

/// <summary>
/// Captures a redacted Windows runtime environment and diagnostic summary for troubleshooting
/// and clipboard export, matching the macOS diagnostic export format.
/// </summary>
public sealed record WindowsDiagnosticSnapshot(
    string AppVersion,
    string OsVersion,
    string Architecture,
    bool IsAdministrator,
    bool RemoteConnected,
    int ConnectedRemoteCount,
    string RemoteModel,
    string VirtualMicrophoneStatus,
    string CableOutputStatus,
    string EtwCaptureStatus,
    string VoiceTool,
    string VoiceTriggerMode,
    string VoiceShortcutName,
    string PreferredAudioInput,
    bool RuntimeLogEnabled,
    bool RuntimeLogAvailable,
    string RuntimeLogPath,
    long? RuntimeLogSizeBytes,
    DateTimeOffset? GeneratedAt = null)
{
    public DateTimeOffset EffectiveGeneratedAt => GeneratedAt ?? DateTimeOffset.UtcNow;

    public string FormattedLogSize => FormatByteSize(RuntimeLogSizeBytes);

    public string BuildSummary()
    {
        string sanitizedLogPath = SanitizePath(RuntimeLogPath);
        string[] lines =
        [
            "### Remote Buddy 运行环境与诊断信息 / Runtime Diagnostics",
            $"- **应用版本 / App Version:** {AppVersion}",
            $"- **系统版本 / Windows:** {OsVersion} ({Architecture})",
            $"- **管理员权限 / Administrator:** {(IsAdministrator ? "是 (Yes)" : "否 (No)")}",
            $"- **生成时间 / Generated At:** {EffectiveGeneratedAt.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}",
            string.Empty,
            "#### 设备与驱动 / Devices & Drivers",
            $"- 遥控器连接 / Remote Connected: {(RemoteConnected ? $"已连接 (Yes, {ConnectedRemoteCount})" : "未连接 (No)")} [{RemoteModel}]",
            $"- 虚拟麦克风驱动 / Virtual Mic Driver: {VirtualMicrophoneStatus} (CABLE Output: {CableOutputStatus})",
            $"- ETW 按键捕获 / ETW Button Capture: {EtwCaptureStatus}",
            string.Empty,
            "#### 配置与集成 / Configuration",
            $"- 语音工具 / Voice Tool: {VoiceTool} ({VoiceTriggerMode})",
            $"- 语音快捷键 / Voice Shortcut: {VoiceShortcutName}",
            $"- 首选录音设备 / Preferred Audio Input: {PreferredAudioInput}",
            string.Empty,
            "#### 诊断日志 / Runtime Logs",
            $"- 日志记录状态 / Logging Enabled: {(RuntimeLogEnabled ? "已开启 (Enabled)" : "已关闭 (Disabled)")}",
            $"- 日志文件状态 / Log Available: {(RuntimeLogAvailable ? $"存在 ({FormattedLogSize})" : "未找到 (Not Found)")}",
            $"- 本地路径 / Log Path: {sanitizedLogPath}"
        ];
        return string.Join(Environment.NewLine, lines);
    }

    public string BuildClipboardText(IReadOnlyList<string>? recentLogLines = null)
    {
        string summary = BuildSummary();
        IReadOnlyList<string> lines = recentLogLines ?? AppLogger.GetRecentLogLines();
        if (lines.Count == 0)
        {
            return summary;
        }

        var sb = new StringBuilder(summary.Length + lines.Count * 96 + 80);
        sb.Append(summary);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("### 最近运行日志 / Recent Runtime Log");
        sb.AppendLine();
        sb.AppendLine("~~~text");
        foreach (string line in lines)
        {
            sb.AppendLine(SanitizePath(line));
        }
        sb.Append("~~~");
        return sb.ToString();
    }

    public string BuildAuditLog() =>
        $"ABOUT DIAGNOSTICS copied app_version={AppVersion} arch={Architecture} " +
        $"admin={IsAdministrator} remote_connected={RemoteConnected} " +
        $"remote_count={ConnectedRemoteCount} remote_model={RemoteModel} " +
        $"driver={VirtualMicrophoneStatus} etw={EtwCaptureStatus}";

    public static string SanitizePath(string? text, string? userProfileDirectory = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string home = (userProfileDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(home)) return text;

        return text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    internal static string FormatByteSize(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes.Value;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return unitIndex == 0
            ? $"{(long)value} B"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unitIndex]}");
    }
}
