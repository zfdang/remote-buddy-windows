// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RemoteBuddy.Core.Models;

public enum ButtonActionCategory
{
    DirectPassthrough = 0,
    BasicKeys = 1,
    SystemAndMedia = 2,
    Applications = 3,
    CommonPhrases = 4,
    Custom = 5
}

public enum ButtonAction
{
    Disabled = 0,
    DirectPassthrough = 1,
    RepeatSingleClick = 2,

    // Basic keys
    ReturnKey = 10,
    ShiftReturn = 11,
    CommandReturn = 12,
    Escape = 13,
    Space = 14,
    Tab = 15,
    DeleteBackward = 16,
    DeleteLine = 17,
    Copy = 18,
    Paste = 19,
    Cut = 20,
    SelectAll = 21,
    Undo = 22,
    Redo = 23,
    Find = 24,
    Save = 25,
    Refresh = 26,
    CloseWindow = 27,
    QuitApp = 28,
    ArrowUp = 29,
    ArrowDown = 30,
    ArrowLeft = 31,
    ArrowRight = 32,
    ScrollUp = 33,
    ScrollDown = 34,

    // System & Media
    AppSwitcher = 50,
    TaskView = 51,
    ShowDesktop = 52,
    NotificationCenter = 53,
    SystemSearch = 54,
    DesktopLeft = 55,
    DesktopRight = 56,
    LockScreen = 57,
    Screenshot = 58,
    FocusInput = 59,
    ContextMenu = 60,
    ToggleVoice = 61,
    PlayPause = 62,
    PreviousTrack = 63,
    NextTrack = 64,
    VolumeUp = 65,
    VolumeDown = 66,
    VolumeMute = 67,

    // Common phrases
    OpenCommonPhrases = 80,
    TextSnippet = 81,

    // Applications
    OpenRemoteBuddy = 100,
    OpenWeChat = 101,
    OpenWeCom = 102,
    OpenFeishu = 103,
    OpenDingTalk = 104,
    OpenSlack = 105,
    OpenChatGPT = 106,
    OpenClaude = 107,
    OpenDeepSeek = 108,
    OpenWorkBuddy = 109,
    OpenAntigravity = 110,
    OpenCursor = 111,
    OpenVSCode = 112,
    OpenWindowsTerminal = 113,
    OpenChrome = 114,
    OpenEdge = 115,
    OpenNeteaseMusic = 116,
    OpenSpotify = 117,
    OpenExplorer = 118,
    OpenCustomApplication = 119,

    // Custom
    CustomShortcut = 140,
    OpenUrl = 141
}

public enum PresetApplication
{
    RemoteBuddy = 0,
    WeChat = 1,
    WeCom = 2,
    Feishu = 3,
    DingTalk = 4,
    Slack = 5,
    ChatGPT = 6,
    Claude = 7,
    DeepSeek = 8,
    WorkBuddy = 9,
    Antigravity = 10,
    Cursor = 11,
    VSCode = 12,
    WindowsTerminal = 13,
    Chrome = 14,
    Edge = 15,
    NeteaseMusic = 16,
    Spotify = 17,
    Explorer = 18
}

public sealed class ConfiguredButtonAction : IEquatable<ConfiguredButtonAction>
{
    public ButtonAction Action { get; set; } = ButtonAction.Disabled;
    public string? Shortcut { get; set; }
    public string? Text { get; set; }
    public string? Url { get; set; }
    public string? ApplicationProfileId { get; set; }

    public static ConfiguredButtonAction Disabled => new() { Action = ButtonAction.Disabled };

    public ConfiguredButtonAction Clone() => new()
    {
        Action = Action,
        Shortcut = Shortcut,
        Text = Text,
        Url = Url,
        ApplicationProfileId = ApplicationProfileId
    };

    public bool Equals(ConfiguredButtonAction? other) =>
        other is not null &&
        Action == other.Action &&
        Shortcut == other.Shortcut &&
        Text == other.Text &&
        Url == other.Url &&
        ApplicationProfileId == other.ApplicationProfileId;

    public override bool Equals(object? obj) => Equals(obj as ConfiguredButtonAction);

    public override int GetHashCode() =>
        HashCode.Combine(Action, Shortcut, Text, Url, ApplicationProfileId);
}

public static class CustomActionUrlPolicy
{
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "javascript", "data", "vbscript"
    };

    public static string? NormalizedInput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim();
    }

    public static bool TryValidateUrl(string? raw, out Uri? uri)
    {
        uri = null;
        string? trimmed = NormalizedInput(raw);
        if (trimmed == null) return false;

        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx <= 0) return false;

        string scheme = trimmed[..colonIdx];
        if (!IsValidScheme(scheme) || BlockedSchemes.Contains(scheme))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.Scheme) ||
            !IsValidScheme(parsed.Scheme) ||
            BlockedSchemes.Contains(parsed.Scheme))
        {
            return false;
        }

        if (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(parsed.Host)) return false;
        }

        uri = parsed;
        return true;
    }

    public static string? SummaryLabel(string? raw)
    {
        if (!TryValidateUrl(raw, out var uri) || uri == null)
        {
            return NormalizedInput(raw);
        }

        if ((uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return uri.OriginalString;
    }

    private static bool IsValidScheme(string scheme)
    {
        if (string.IsNullOrEmpty(scheme) || !char.IsAsciiLetter(scheme[0])) return false;
        for (int i = 1; i < scheme.Length; i++)
        {
            char c = scheme[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }
        return true;
    }
}

public static class ButtonActionCatalog
{
    public static bool IsDirectional(RemoteButton button) =>
        button is RemoteButton.Up or RemoteButton.Down or RemoteButton.Left or RemoteButton.Right;

    public static PresetApplication? GetPresetApplication(ButtonAction action) => action switch
    {
        ButtonAction.OpenRemoteBuddy => PresetApplication.RemoteBuddy,
        ButtonAction.OpenWeChat => PresetApplication.WeChat,
        ButtonAction.OpenWeCom => PresetApplication.WeCom,
        ButtonAction.OpenFeishu => PresetApplication.Feishu,
        ButtonAction.OpenDingTalk => PresetApplication.DingTalk,
        ButtonAction.OpenSlack => PresetApplication.Slack,
        ButtonAction.OpenChatGPT => PresetApplication.ChatGPT,
        ButtonAction.OpenClaude => PresetApplication.Claude,
        ButtonAction.OpenDeepSeek => PresetApplication.DeepSeek,
        ButtonAction.OpenWorkBuddy => PresetApplication.WorkBuddy,
        ButtonAction.OpenAntigravity => PresetApplication.Antigravity,
        ButtonAction.OpenCursor => PresetApplication.Cursor,
        ButtonAction.OpenVSCode => PresetApplication.VSCode,
        ButtonAction.OpenWindowsTerminal => PresetApplication.WindowsTerminal,
        ButtonAction.OpenChrome => PresetApplication.Chrome,
        ButtonAction.OpenEdge => PresetApplication.Edge,
        ButtonAction.OpenNeteaseMusic => PresetApplication.NeteaseMusic,
        ButtonAction.OpenSpotify => PresetApplication.Spotify,
        ButtonAction.OpenExplorer => PresetApplication.Explorer,
        _ => null
    };

    public static ButtonActionCategory GetCategory(ButtonAction action)
    {
        if (GetPresetApplication(action) != null || action == ButtonAction.OpenCustomApplication)
        {
            return ButtonActionCategory.Applications;
        }

        return action switch
        {
            ButtonAction.DirectPassthrough => ButtonActionCategory.DirectPassthrough,
            ButtonAction.Disabled or ButtonAction.ReturnKey or ButtonAction.ShiftReturn or ButtonAction.CommandReturn or
                ButtonAction.Escape or ButtonAction.Space or ButtonAction.Tab or ButtonAction.DeleteBackward or
                ButtonAction.DeleteLine or ButtonAction.Copy or ButtonAction.Paste or ButtonAction.Cut or
                ButtonAction.SelectAll or ButtonAction.Undo or ButtonAction.Redo or ButtonAction.Find or
                ButtonAction.Save or ButtonAction.Refresh or ButtonAction.CloseWindow or ButtonAction.QuitApp or
                ButtonAction.ArrowUp or ButtonAction.ArrowDown or ButtonAction.ArrowLeft or ButtonAction.ArrowRight or
                ButtonAction.ScrollUp or ButtonAction.ScrollDown => ButtonActionCategory.BasicKeys,
            ButtonAction.AppSwitcher or ButtonAction.TaskView or ButtonAction.ShowDesktop or
                ButtonAction.NotificationCenter or ButtonAction.SystemSearch or ButtonAction.DesktopLeft or
                ButtonAction.DesktopRight or ButtonAction.LockScreen or ButtonAction.Screenshot or
                ButtonAction.FocusInput or ButtonAction.ContextMenu or ButtonAction.ToggleVoice or
                ButtonAction.PlayPause or ButtonAction.PreviousTrack or ButtonAction.NextTrack or
                ButtonAction.VolumeUp or ButtonAction.VolumeDown or ButtonAction.VolumeMute => ButtonActionCategory.SystemAndMedia,
            ButtonAction.OpenCommonPhrases or ButtonAction.TextSnippet => ButtonActionCategory.CommonPhrases,
            ButtonAction.CustomShortcut or ButtonAction.OpenUrl or ButtonAction.RepeatSingleClick => ButtonActionCategory.Custom,
            _ => ButtonActionCategory.BasicKeys
        };
    }

    public static string GetCategoryDisplayName(ButtonActionCategory category) => category switch
    {
        ButtonActionCategory.DirectPassthrough => "按键直通",
        ButtonActionCategory.BasicKeys => "基础按键",
        ButtonActionCategory.SystemAndMedia => "系统与媒体",
        ButtonActionCategory.Applications => "应用程序",
        ButtonActionCategory.CommonPhrases => "常用语",
        ButtonActionCategory.Custom => "自定义",
        _ => category.ToString()
    };

    public static string GetDisplayName(ButtonAction action) => action switch
    {
        ButtonAction.Disabled => "禁用",
        ButtonAction.DirectPassthrough => "按键直通",
        ButtonAction.RepeatSingleClick => "持续连发",
        ButtonAction.ReturnKey => "回车",
        ButtonAction.ShiftReturn => "换行",
        ButtonAction.CommandReturn => "发送",
        ButtonAction.Escape => "取消",
        ButtonAction.Space => "空格",
        ButtonAction.Tab => "切换焦点",
        ButtonAction.DeleteBackward => "退格",
        ButtonAction.DeleteLine => "删除整行",
        ButtonAction.Copy => "复制",
        ButtonAction.Paste => "粘贴",
        ButtonAction.Cut => "剪切",
        ButtonAction.SelectAll => "全选",
        ButtonAction.Undo => "撤销",
        ButtonAction.Redo => "重做",
        ButtonAction.Find => "查找",
        ButtonAction.Save => "保存",
        ButtonAction.Refresh => "刷新",
        ButtonAction.CloseWindow => "关闭窗口",
        ButtonAction.QuitApp => "退出应用",
        ButtonAction.ArrowUp => "方向上",
        ButtonAction.ArrowDown => "方向下",
        ButtonAction.ArrowLeft => "方向左",
        ButtonAction.ArrowRight => "方向右",
        ButtonAction.ScrollUp => "向上滚动",
        ButtonAction.ScrollDown => "向下滚动",
        ButtonAction.AppSwitcher => "切换应用",
        ButtonAction.TaskView => "任务视图",
        ButtonAction.ShowDesktop => "显示桌面",
        ButtonAction.NotificationCenter => "通知中心",
        ButtonAction.SystemSearch => "系统搜索",
        ButtonAction.DesktopLeft => "上一桌面",
        ButtonAction.DesktopRight => "下一桌面",
        ButtonAction.LockScreen => "锁定屏幕",
        ButtonAction.Screenshot => "截屏",
        ButtonAction.FocusInput => "聚焦输入框",
        ButtonAction.ContextMenu => "上下文菜单",
        ButtonAction.ToggleVoice => "长按/切换语音",
        ButtonAction.PlayPause => "播放 / 暂停",
        ButtonAction.PreviousTrack => "上一曲",
        ButtonAction.NextTrack => "下一曲",
        ButtonAction.VolumeUp => "系统音量 +",
        ButtonAction.VolumeDown => "系统音量 -",
        ButtonAction.VolumeMute => "系统静音",
        ButtonAction.OpenCommonPhrases => "打开常用语",
        ButtonAction.TextSnippet => "直接输入短语",
        ButtonAction.OpenRemoteBuddy => "遥控搭子",
        ButtonAction.OpenWeChat => "微信",
        ButtonAction.OpenWeCom => "企业微信",
        ButtonAction.OpenFeishu => "飞书",
        ButtonAction.OpenDingTalk => "钉钉",
        ButtonAction.OpenSlack => "Slack",
        ButtonAction.OpenChatGPT => "ChatGPT",
        ButtonAction.OpenClaude => "Claude",
        ButtonAction.OpenDeepSeek => "DeepSeek",
        ButtonAction.OpenWorkBuddy => "WorkBuddy",
        ButtonAction.OpenAntigravity => "Antigravity",
        ButtonAction.OpenCursor => "Cursor",
        ButtonAction.OpenVSCode => "VS Code",
        ButtonAction.OpenWindowsTerminal => "Windows 终端",
        ButtonAction.OpenChrome => "Chrome",
        ButtonAction.OpenEdge => "Microsoft Edge",
        ButtonAction.OpenNeteaseMusic => "网易云音乐",
        ButtonAction.OpenSpotify => "Spotify",
        ButtonAction.OpenExplorer => "文件资源管理器",
        ButtonAction.OpenCustomApplication => "打开自定义 APP",
        ButtonAction.CustomShortcut => "自定义快捷键",
        ButtonAction.OpenUrl => "打开网址 / URL Scheme",
        _ => action.ToString()
    };

    public static string? GetShortcutBadge(ButtonAction action) => action switch
    {
        ButtonAction.ReturnKey => "Enter",
        ButtonAction.ShiftReturn => "Shift+Enter",
        ButtonAction.CommandReturn => "Ctrl+Enter",
        ButtonAction.Escape => "Esc",
        ButtonAction.Space => "Space",
        ButtonAction.Tab => "Tab",
        ButtonAction.DeleteBackward => "Backspace",
        ButtonAction.Copy => "Ctrl+C",
        ButtonAction.Paste => "Ctrl+V",
        ButtonAction.Cut => "Ctrl+X",
        ButtonAction.SelectAll => "Ctrl+A",
        ButtonAction.Undo => "Ctrl+Z",
        ButtonAction.Redo => "Ctrl+Y",
        ButtonAction.Find => "Ctrl+F",
        ButtonAction.Save => "Ctrl+S",
        ButtonAction.Refresh => "Ctrl+R",
        ButtonAction.CloseWindow => "Ctrl+W",
        ButtonAction.QuitApp => "Alt+F4",
        ButtonAction.ArrowUp => "↑",
        ButtonAction.ArrowDown => "↓",
        ButtonAction.ArrowLeft => "←",
        ButtonAction.ArrowRight => "→",
        ButtonAction.AppSwitcher => "Alt+Tab",
        ButtonAction.TaskView => "Win+Tab",
        ButtonAction.ShowDesktop => "Win+D",
        ButtonAction.NotificationCenter => "Win+N",
        ButtonAction.SystemSearch => "Win+S",
        ButtonAction.DesktopLeft => "Ctrl+Win+←",
        ButtonAction.DesktopRight => "Ctrl+Win+→",
        ButtonAction.Screenshot => "Win+Shift+S",
        ButtonAction.ContextMenu => "Apps",
        _ => null
    };

    public static string? GetExecutionShortcut(ButtonAction action) => action switch
    {
        ButtonAction.ReturnKey => "Enter",
        ButtonAction.ShiftReturn => "Shift+Enter",
        ButtonAction.CommandReturn => "Ctrl+Enter",
        ButtonAction.Escape => "Escape",
        ButtonAction.Space => "Space",
        ButtonAction.Tab => "Tab",
        ButtonAction.DeleteBackward => "Backspace",
        ButtonAction.Copy => "Ctrl+C",
        ButtonAction.Paste => "Ctrl+V",
        ButtonAction.Cut => "Ctrl+X",
        ButtonAction.SelectAll => "Ctrl+A",
        ButtonAction.Undo => "Ctrl+Z",
        ButtonAction.Redo => "Ctrl+Y",
        ButtonAction.Find => "Ctrl+F",
        ButtonAction.Save => "Ctrl+S",
        ButtonAction.Refresh => "Ctrl+R",
        ButtonAction.CloseWindow => "Ctrl+W",
        ButtonAction.QuitApp => "Alt+F4",
        ButtonAction.ArrowUp => "Up",
        ButtonAction.ArrowDown => "Down",
        ButtonAction.ArrowLeft => "Left",
        ButtonAction.ArrowRight => "Right",
        ButtonAction.AppSwitcher => "Alt+Tab",
        ButtonAction.TaskView => "Win+Tab",
        ButtonAction.ShowDesktop => "Win+D",
        ButtonAction.NotificationCenter => "Win+N",
        ButtonAction.SystemSearch => "Win+S",
        ButtonAction.DesktopLeft => "Ctrl+Win+Left",
        ButtonAction.DesktopRight => "Ctrl+Win+Right",
        ButtonAction.Screenshot => "Win+Shift+S",
        ButtonAction.ContextMenu => "Apps",
        _ => null
    };

    public static MediaCommand GetMediaCommand(ButtonAction action) => action switch
    {
        ButtonAction.PlayPause => MediaCommand.PlayPause,
        ButtonAction.PreviousTrack => MediaCommand.PreviousTrack,
        ButtonAction.NextTrack => MediaCommand.NextTrack,
        ButtonAction.VolumeUp => MediaCommand.VolumeUp,
        ButtonAction.VolumeDown => MediaCommand.VolumeDown,
        ButtonAction.VolumeMute => MediaCommand.Mute,
        _ => MediaCommand.None
    };

    public static IReadOnlyList<ButtonActionCategory> GetVisibleCategories(RemoteButton? button = null, ButtonTrigger? trigger = null)
    {
        if (button is { } btn && IsDirectional(btn) && (trigger == null || trigger == ButtonTrigger.SingleClick))
        {
            return
            [
                ButtonActionCategory.DirectPassthrough,
                ButtonActionCategory.BasicKeys,
                ButtonActionCategory.SystemAndMedia,
                ButtonActionCategory.Applications,
                ButtonActionCategory.CommonPhrases,
                ButtonActionCategory.Custom
            ];
        }

        return
        [
            ButtonActionCategory.BasicKeys,
            ButtonActionCategory.SystemAndMedia,
            ButtonActionCategory.Applications,
            ButtonActionCategory.CommonPhrases,
            ButtonActionCategory.Custom
        ];
    }

    public static IReadOnlyList<ButtonAction> GetPickerActions(
        IReadOnlySet<PresetApplication> installedApplications,
        ButtonAction currentAction,
        RemoteButton? button = null,
        ButtonTrigger? trigger = null)
    {
        var result = new List<ButtonAction>();
        bool allowPassthrough = button is { } btn && IsDirectional(btn) && (trigger == null || trigger == ButtonTrigger.SingleClick);

        foreach (var action in Enum.GetValues<ButtonAction>())
        {
            if (action is ButtonAction.Disabled or ButtonAction.RepeatSingleClick or ButtonAction.OpenCustomApplication)
            {
                continue;
            }

            if (action == ButtonAction.DirectPassthrough)
            {
                if (allowPassthrough || action == currentAction)
                {
                    result.Add(action);
                }
                continue;
            }

            if (GetPresetApplication(action) is { } preset)
            {
                if (installedApplications.Contains(preset) || action == currentAction)
                {
                    result.Add(action);
                }
                continue;
            }

            result.Add(action);
        }

        result.Add(ButtonAction.OpenCustomApplication);
        return result;
    }

    public static string FormatActionSummary(
        RemoteButton button,
        ButtonTrigger trigger,
        ConfiguredButtonAction configured,
        ConfiguredButtonAction singleClick,
        Func<string?, string?>? resolveCustomAppName = null,
        bool detailed = false)
    {
        if (trigger != ButtonTrigger.SingleClick && singleClick.Action == ButtonAction.DirectPassthrough)
        {
            return detailed ? "按键直通已接管该按键，不支持此手势" : "-";
        }

        if (configured.Action == ButtonAction.Disabled)
        {
            return "未设置";
        }

        if (configured.Action == ButtonAction.DirectPassthrough)
        {
            return button switch
            {
                RemoteButton.Up => "↑ 直通",
                RemoteButton.Down => "↓ 直通",
                RemoteButton.Left => "← 直通",
                RemoteButton.Right => "→ 直通",
                _ => "按键直通"
            };
        }

        if (configured.Action == ButtonAction.CustomShortcut)
        {
            return string.IsNullOrWhiteSpace(configured.Shortcut)
                ? "未设置快捷键"
                : configured.Shortcut.Trim();
        }

        if (configured.Action == ButtonAction.OpenCustomApplication)
        {
            string? resolved = resolveCustomAppName?.Invoke(configured.ApplicationProfileId);
            return string.IsNullOrWhiteSpace(resolved) ? "未配置应用" : resolved;
        }

        if (configured.Action == ButtonAction.OpenUrl)
        {
            return CustomActionUrlPolicy.SummaryLabel(configured.Url) ?? "未配置网址";
        }

        if (configured.Action == ButtonAction.TextSnippet)
        {
            if (string.IsNullOrWhiteSpace(configured.Text)) return "未设置短语";
            string trimmed = configured.Text.Trim();
            return trimmed.Length > 8 ? $"\"{trimmed[..8]}…\"" : $"\"{trimmed}\"";
        }

        if (detailed)
        {
            string name = GetDisplayName(configured.Action);
            string? badge = GetShortcutBadge(configured.Action);
            return badge == null ? name : $"{name} ({badge})";
        }

        return configured.Action switch
        {
            ButtonAction.ArrowUp => "↑",
            ButtonAction.ArrowDown => "↓",
            ButtonAction.ArrowLeft => "←",
            ButtonAction.ArrowRight => "→",
            ButtonAction.DeleteBackward => "⌫",
            ButtonAction.VolumeUp => "Vol+",
            ButtonAction.VolumeDown => "Vol-",
            ButtonAction.VolumeMute => "静音",
            _ => GetDisplayName(configured.Action)
        };
    }

    public static ButtonAction? TryMatchPresetFromLegacyShortcut(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return null;
        return shortcut.Trim().ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => ButtonAction.ReturnKey,
            "SHIFT+ENTER" or "SHIFT+RETURN" => ButtonAction.ShiftReturn,
            "CTRL+ENTER" or "CTRL+RETURN" => ButtonAction.CommandReturn,
            "ESC" or "ESCAPE" => ButtonAction.Escape,
            "SPACE" => ButtonAction.Space,
            "TAB" => ButtonAction.Tab,
            "BACK" or "BACKSPACE" => ButtonAction.DeleteBackward,
            "CTRL+C" => ButtonAction.Copy,
            "CTRL+V" => ButtonAction.Paste,
            "CTRL+X" => ButtonAction.Cut,
            "CTRL+A" => ButtonAction.SelectAll,
            "CTRL+Z" => ButtonAction.Undo,
            "CTRL+Y" => ButtonAction.Redo,
            "CTRL+F" => ButtonAction.Find,
            "CTRL+S" => ButtonAction.Save,
            "CTRL+R" => ButtonAction.Refresh,
            "CTRL+W" => ButtonAction.CloseWindow,
            "ALT+F4" => ButtonAction.QuitApp,
            "UP" => ButtonAction.ArrowUp,
            "DOWN" => ButtonAction.ArrowDown,
            "LEFT" => ButtonAction.ArrowLeft,
            "RIGHT" => ButtonAction.ArrowRight,
            "ALT+TAB" => ButtonAction.AppSwitcher,
            "WIN+TAB" => ButtonAction.TaskView,
            "WIN+D" => ButtonAction.ShowDesktop,
            "WIN+N" => ButtonAction.NotificationCenter,
            "WIN+S" => ButtonAction.SystemSearch,
            "CTRL+WIN+LEFT" => ButtonAction.DesktopLeft,
            "CTRL+WIN+RIGHT" => ButtonAction.DesktopRight,
            "WIN+SHIFT+S" => ButtonAction.Screenshot,
            "APPS" => ButtonAction.ContextMenu,
            _ => null
        };
    }

    public static ButtonAction FromLegacyMedia(MediaCommand media) => media switch
    {
        MediaCommand.PlayPause => ButtonAction.PlayPause,
        MediaCommand.VolumeUp => ButtonAction.VolumeUp,
        MediaCommand.VolumeDown => ButtonAction.VolumeDown,
        MediaCommand.Mute => ButtonAction.VolumeMute,
        MediaCommand.NextTrack => ButtonAction.NextTrack,
        MediaCommand.PreviousTrack => ButtonAction.PreviousTrack,
        _ => ButtonAction.Disabled
    };
}
