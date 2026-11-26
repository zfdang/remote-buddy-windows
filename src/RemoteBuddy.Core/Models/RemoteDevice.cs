// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteBuddy.Core.Models;

public enum RemoteModelType
{
    Unknown = 0,
    XiaomiRC001,
    XiaomiRC003,
    XiaomiARN9,
    GoogleG9N9N,
    GoogleGY3LE,
    T1Remote
}

public enum G9N9NVoiceButtonMode
{
    HoldToTalk = 0,
    SmartToggle = 1
}

public partial class RemoteDevice : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedAddress))]
    private ulong _bluetoothAddress;

    [ObservableProperty]
    private Guid? _containerId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelDisplayName))]
    private RemoteModelType _modelType = RemoteModelType.Unknown;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isConnected;

    [ObservableProperty]
    [property: JsonIgnore]
    private int _batteryLevel = -1;

    [ObservableProperty]
    [property: JsonIgnore]
    private RemoteDeviceInformation? _hardwareInformation;

    public const float DefaultMicrophoneGain = 3.1622777f; // +10 dB (20 * log10(3.1622777) = 10.0 dB)

    [ObservableProperty]
    private float _gain = DefaultMicrophoneGain;

    [ObservableProperty]
    private G9N9NVoiceButtonMode _voiceButtonMode = G9N9NVoiceButtonMode.SmartToggle;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(HasHidInfo))]
    [NotifyPropertyChangedFor(nameof(HidSpecSummary))]
    private string? _hidDevicePath;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(HidSpecSummary))]
    private ushort _hidUsagePage;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(HidSpecSummary))]
    private ushort _hidUsage;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(HidSpecSummary))]
    private int _hidReportLength;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _hidCaptureStatus = "待匹配";

    [JsonIgnore]
    public bool HasHidInfo => !string.IsNullOrEmpty(HidDevicePath);

    [JsonIgnore]
    public string HidSpecSummary => HidUsagePage != 0
        ? $"Page 0x{HidUsagePage:X4} · Usage 0x{HidUsage:X4} · 报告 {HidReportLength} 字节"
        : "标准 Consumer Control (待检测)";

    [JsonIgnore]
    public bool IsG9N9N => ModelType is RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE;
    [JsonIgnore]
    public bool IsG9N9NLayout => ModelType == RemoteModelType.GoogleG9N9N;
    [JsonIgnore]
    public bool IsGY3LELayout => ModelType == RemoteModelType.GoogleGY3LE;
    [JsonIgnore]
    public bool IsT1 => ModelType == RemoteModelType.T1Remote;
    [JsonIgnore]
    public bool IsXiaomi => !IsG9N9N && !IsT1;

    public bool MatchesPhysicalDevice(RemoteDevice? other)
    {
        if (other == null) return false;
        if (BluetoothAddress != 0 && other.BluetoothAddress != 0)
        {
            return BluetoothAddress == other.BluetoothAddress;
        }
        return !string.IsNullOrEmpty(Id) && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    [JsonIgnore]
    public string FormattedAddress
    {
        get
        {
            if (BluetoothAddress == 0) return "未知地址";
            byte[] bytes = BitConverter.GetBytes(BluetoothAddress);
            return $"{bytes[5]:X2}:{bytes[4]:X2}:{bytes[3]:X2}:{bytes[2]:X2}:{bytes[1]:X2}:{bytes[0]:X2}";
        }
    }

    [JsonIgnore]
    public string ModelDisplayName => ModelType switch
    {
        RemoteModelType.GoogleG9N9N => "Chromecast Voice Remote (G9N9N)",
        RemoteModelType.GoogleGY3LE => "Google TV Streamer Remote (GY3LE)",
        RemoteModelType.T1Remote => "T1 智能飞鼠遥控器",
        RemoteModelType.XiaomiRC001 => "小米蓝牙语音遥控器 (RC001)",
        RemoteModelType.XiaomiRC003 => "小米蓝牙语音遥控器 (RC003)",
        RemoteModelType.XiaomiARN9 => "小米电视语音遥控器 (ARN9)",
        _ => "未知型号"
    };

    [JsonIgnore]
    public string IllustrationSource => IsGY3LELayout
        ? "/Resources/GoogleStreamerGY3LEIllustration.png"
        : (IsG9N9N
            ? "/Resources/ChromecastG9N9NPhoto.png"
            : (IsT1 ? "/Resources/T1RemoteIllustration.png" : "/Resources/BuddyRemoteIllustration.png"));

    [JsonIgnore]
    public string VoiceModeHelp => VoiceButtonMode == G9N9NVoiceButtonMode.SmartToggle
        ? "短按并松开后保持免提收音，再按一次停止；若长按超过 0.7 秒则松手即停。"
        : "按住 Google Assistant 语音键说话，松开后立即停止收音。";

    [JsonIgnore]
    public bool IsSmartToggleMode
    {
        get => VoiceButtonMode == G9N9NVoiceButtonMode.SmartToggle;
        set
        {
            if (value && VoiceButtonMode != G9N9NVoiceButtonMode.SmartToggle)
            {
                VoiceButtonMode = G9N9NVoiceButtonMode.SmartToggle;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHoldToTalkMode));
            }
        }
    }

    [JsonIgnore]
    public bool IsHoldToTalkMode
    {
        get => VoiceButtonMode == G9N9NVoiceButtonMode.HoldToTalk;
        set
        {
            if (value && VoiceButtonMode != G9N9NVoiceButtonMode.HoldToTalk)
            {
                VoiceButtonMode = G9N9NVoiceButtonMode.HoldToTalk;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSmartToggleMode));
            }
        }
    }

    partial void OnVoiceButtonModeChanged(G9N9NVoiceButtonMode value)
    {
        OnPropertyChanged(nameof(VoiceModeHelp));
        OnPropertyChanged(nameof(IsSmartToggleMode));
        OnPropertyChanged(nameof(IsHoldToTalkMode));
    }

    partial void OnModelTypeChanged(RemoteModelType value)
    {
        OnPropertyChanged(nameof(IsG9N9N));
        OnPropertyChanged(nameof(IsG9N9NLayout));
        OnPropertyChanged(nameof(IsGY3LELayout));
        OnPropertyChanged(nameof(IsT1));
        OnPropertyChanged(nameof(IsXiaomi));
        OnPropertyChanged(nameof(IllustrationSource));
    }

    [ObservableProperty]
    private List<ButtonMapping> _mappings = CreateDefaultMappings(RemoteModelType.Unknown);

    public static ConfiguredButtonAction GetDefaultAction(
        RemoteModelType type,
        RemoteButton button,
        ButtonTrigger trigger)
    {
        var defaults = CreateDefaultMappings(type);
        var match = defaults.FirstOrDefault(m => m.Button == button);
        return match?.GetConfiguredAction(trigger).Clone() ?? ConfiguredButtonAction.Disabled;
    }

    public static List<ButtonMapping> CreateDefaultMappings(RemoteModelType type)
    {
        var list = new List<ButtonMapping>();
        void Add(
            RemoteButton btn,
            ButtonAction singleAction,
            ButtonAction? longPressOverride = null)
        {
            bool repeats = singleAction != ButtonAction.Disabled &&
                btn is RemoteButton.Up or RemoteButton.Down or RemoteButton.Left or RemoteButton.Right or
                    RemoteButton.Power or RemoteButton.VolumeUp or RemoteButton.VolumeDown;
            var longPress = longPressOverride ?? (repeats ? ButtonAction.RepeatSingleClick : ButtonAction.Disabled);
            list.Add(new ButtonMapping
            {
                Button = btn,
                SingleClick = new ConfiguredButtonAction { Action = singleAction },
                DoubleClick = ConfiguredButtonAction.Disabled,
                LongPress = new ConfiguredButtonAction { Action = longPress }
            });
        }

        if (type == RemoteModelType.T1Remote)
        {
            // T1 front-panel 7 customizable buttons (OK/arrows and rear QWERTY keyboard stay native).
            Add(RemoteButton.Power, ButtonAction.DeleteBackward);
            Add(RemoteButton.Back, ButtonAction.Escape);
            Add(RemoteButton.Home, ButtonAction.TaskView);
            Add(RemoteButton.Menu, ButtonAction.ContextMenu);
            Add(RemoteButton.VolumeUp, ButtonAction.VolumeUp);
            Add(RemoteButton.VolumeDown, ButtonAction.VolumeDown);
            Add(RemoteButton.Mute, ButtonAction.VolumeMute);
            return list;
        }

        if (type is RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE)
        {
            Add(RemoteButton.Power, ButtonAction.DeleteBackward);
            Add(RemoteButton.Up, ButtonAction.ArrowUp);
            Add(RemoteButton.Left, ButtonAction.ArrowLeft);
            Add(RemoteButton.Ok, ButtonAction.ReturnKey, ButtonAction.CommandReturn);
            Add(RemoteButton.Right, ButtonAction.ArrowRight);
            Add(RemoteButton.Down, ButtonAction.ArrowDown);
            Add(RemoteButton.Back, ButtonAction.Escape);
            Add(RemoteButton.Home, ButtonAction.TaskView);
            Add(RemoteButton.VolumeUp, ButtonAction.VolumeUp);
            Add(RemoteButton.VolumeDown, ButtonAction.VolumeDown);
            Add(RemoteButton.Mute, ButtonAction.VolumeMute);
            Add(RemoteButton.YouTube, ButtonAction.PreviousTrack);
            Add(RemoteButton.Netflix, ButtonAction.NextTrack);
            if (type == RemoteModelType.GoogleG9N9N)
            {
                Add(RemoteButton.Input, ButtonAction.PlayPause);
            }
            else
            {
                Add(RemoteButton.Custom, ButtonAction.Disabled);
            }
            return list;
        }

        // Xiaomi (RC001 / RC003 / ARN9 / Unknown) — 12 buttons matching macOS layout
        Add(RemoteButton.Power, ButtonAction.DeleteBackward);
        Add(RemoteButton.Up, ButtonAction.ArrowUp);
        Add(RemoteButton.Left, ButtonAction.ArrowLeft);
        Add(RemoteButton.Ok, ButtonAction.ReturnKey, ButtonAction.CommandReturn);
        Add(RemoteButton.Right, ButtonAction.ArrowRight);
        Add(RemoteButton.Down, ButtonAction.ArrowDown);
        Add(RemoteButton.Back, ButtonAction.Escape);
        Add(RemoteButton.VolumeUp, ButtonAction.VolumeUp);
        Add(RemoteButton.Home, ButtonAction.TaskView);
        Add(RemoteButton.VolumeDown, ButtonAction.VolumeDown);
        Add(RemoteButton.Menu, ButtonAction.ContextMenu);
        Add(RemoteButton.LiveTv, ButtonAction.PlayPause);
        return list;
    }
}

public enum TargetInputTool
{
    WeChatInput,
    DoubaoInput,
    Chatterfly,
    Typeless,
    WindowsDictation,
    Custom
}

public enum VoiceTriggerMode
{
    HoldToTalk,
    ClickToToggle
}

public class InputToolConfig
{
    public const string DefaultWeChatShortcut = "Ctrl+F5";
    public TargetInputTool Tool { get; set; } = TargetInputTool.WeChatInput;
    public VoiceTriggerMode TriggerMode { get; set; } = VoiceTriggerMode.HoldToTalk;
    public string Shortcut { get; set; } = DefaultWeChatShortcut;
    public string PreferredAudioInputDevice { get; set; } = "CABLE Output";
}

public class AppConfig
{
    public int Version { get; set; } = 1;
    public string Language { get; set; } = "system"; // "system", "zh-CN", "en-US"
    public bool LaunchAtStartup { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ReceiveUpdateNotifications { get; set; } = true;
    public bool DiagnosticLoggingEnabled { get; set; } = true;
    public bool HasCompletedOnboarding { get; set; } = false;
    public bool CustomMappingEnabled { get; set; } = true;

    public InputToolConfig InputTool { get; set; } = new();
    public List<RemoteDevice> Devices { get; set; } = new();
    public string? SelectedDeviceId { get; set; }
    public Dictionary<string, bool> AppIntegrations { get; set; } = new();
    public List<CustomApplicationProfile> CustomApplications { get; set; } = new();
}
