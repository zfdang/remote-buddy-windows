// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Audio;
using RemoteBuddy.Platform.Windows.Bluetooth;
using RemoteBuddy.Platform.Windows.Input;
using RemoteBuddy.Platform.Windows.Services;

namespace RemoteBuddy.App.ViewModels;

public partial class DevicesViewModel : ObservableObject, IDisposable
{
    private readonly ConfigService _config;
    private readonly IBluetoothManager _bt;
    private readonly Action<WizardMode>? _openWizard;
    private readonly Action<RemoteDevice> _onDeviceDiscovered;
    private readonly Action<bool, string> _onConnectionChanged;

    [ObservableProperty]
    private ObservableCollection<RemoteDevice> _discoveredDevices = new();

    [ObservableProperty]
    private RemoteDevice? _selectedDevice;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private float _deviceGain = 1.0f;

    public string ConnectedDeviceName => _bt.IsConnected ? _bt.ConnectedDeviceName : "未连接设备";
    public bool IsConnected => _bt.IsConnected;
    public bool HasDevices => DiscoveredDevices.Count > 0;

    public string VoiceIntegrationSummary
    {
        get
        {
            var tool = _config.Current.InputTool;
            string toolName = tool.Tool switch
            {
                TargetInputTool.WeChatInput => "微信输入法",
                TargetInputTool.DoubaoInput => "豆包输入法",
                TargetInputTool.WindowsDictation => "Windows 听写",
                TargetInputTool.Typeless => "Typeless",
                _ => "自定义工具"
            };
            string mode = tool.TriggerMode == VoiceTriggerMode.HoldToTalk ? "长按说话" : "点按切换";
            return $"{toolName} ({mode} · {tool.Shortcut})";
        }
    }

    public DevicesViewModel(ConfigService config, IBluetoothManager bt, Action<WizardMode>? openWizard = null)
    {
        _config = config;
        _bt = bt;
        _openWizard = openWizard;

        foreach (var dev in _config.Current.Devices)
        {
            dev.PropertyChanged += OnDevicePropertyChanged;
            dev.IsConnected = _bt.IsConnected && (_bt.ConnectedDeviceName == dev.Name || dev.BluetoothAddress == _bt.ConnectedDeviceAddress || dev.BluetoothAddress.ToString("X") == _config.Current.SelectedDeviceId);

            if (string.IsNullOrEmpty(dev.HidDevicePath) && dev.BluetoothAddress != 0)
            {
                var info = HidDeviceReader.FindBestDevice(dev.BluetoothAddress);
                if (info != null)
                {
                    dev.HidDevicePath = info.Path;
                    dev.HidUsagePage = info.UsagePage;
                    dev.HidUsage = info.Usage;
                    dev.HidReportLength = info.InputReportByteLength;
                    dev.HidCaptureStatus = "已就绪 (RawInput & Direct Stream)";
                }
            }

            DiscoveredDevices.Add(dev);
        }

        _selectedDevice = DiscoveredDevices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId);
        if (_selectedDevice != null)
        {
            _bt.CurrentGain = _selectedDevice.Gain;
            _bt.VoiceButtonMode = _selectedDevice.VoiceButtonMode;
        }

        _onDeviceDiscovered = (dev) =>
        {
            void Update()
            {
                var existing = DiscoveredDevices.FirstOrDefault(d => d.BluetoothAddress == dev.BluetoothAddress || d.Id == dev.Id);
                if (existing != null)
                {
                    existing.Name = dev.Name;
                    existing.ModelType = dev.ModelType;
                }
            }

            if (App.Current?.Dispatcher != null && !App.Current.Dispatcher.CheckAccess())
            {
                App.Current.Dispatcher.BeginInvoke(Update);
            }
            else
            {
                Update();
            }
        };
        _bt.DeviceDiscovered += _onDeviceDiscovered;

        _onConnectionChanged = (connected, name) =>
        {
            void Update()
            {
                OnPropertyChanged(nameof(ConnectedDeviceName));
                OnPropertyChanged(nameof(IsConnected));
                foreach (var dev in DiscoveredDevices)
                {
                    dev.IsConnected = connected && (dev.Name == name || dev.BluetoothAddress == _bt.ConnectedDeviceAddress || dev.BluetoothAddress.ToString("X") == _config.Current.SelectedDeviceId);
                }
            }

            if (App.Current?.Dispatcher != null && !App.Current.Dispatcher.CheckAccess())
            {
                App.Current.Dispatcher.BeginInvoke(Update);
            }
            else
            {
                Update();
            }
        };
        _bt.ConnectionStatusChanged += _onConnectionChanged;
    }

    public DevicesViewModel(ConfigService config, IBluetoothManager bt, Action openOnboarding)
        : this(config, bt, mode => openOnboarding())
    {
    }

    public void Reload()
    {
        foreach (var dev in DiscoveredDevices)
        {
            dev.PropertyChanged -= OnDevicePropertyChanged;
        }
        DiscoveredDevices.Clear();
        foreach (var dev in _config.Current.Devices)
        {
            dev.PropertyChanged += OnDevicePropertyChanged;
            dev.IsConnected = _bt.IsConnected && (_bt.ConnectedDeviceName == dev.Name || dev.BluetoothAddress == _bt.ConnectedDeviceAddress || dev.BluetoothAddress.ToString("X") == _config.Current.SelectedDeviceId);

            if (string.IsNullOrEmpty(dev.HidDevicePath) && dev.BluetoothAddress != 0)
            {
                var info = HidDeviceReader.FindBestDevice(dev.BluetoothAddress);
                if (info != null)
                {
                    dev.HidDevicePath = info.Path;
                    dev.HidUsagePage = info.UsagePage;
                    dev.HidUsage = info.Usage;
                    dev.HidReportLength = info.InputReportByteLength;
                    dev.HidCaptureStatus = "已就绪 (RawInput & Direct Stream)";
                }
            }

            DiscoveredDevices.Add(dev);
        }
        SelectedDevice = DiscoveredDevices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId);
        if (SelectedDevice != null)
        {
            _bt.CurrentGain = SelectedDevice.Gain;
            _bt.VoiceButtonMode = SelectedDevice.VoiceButtonMode;
        }
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(ConnectedDeviceName));
        OnPropertyChanged(nameof(IsConnected));
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is RemoteDevice dev)
        {
            if (e.PropertyName == nameof(RemoteDevice.Gain))
            {
                if (dev.Id == _config.Current.SelectedDeviceId || dev.BluetoothAddress == SelectedDevice?.BluetoothAddress)
                {
                    _bt.CurrentGain = dev.Gain;
                }
                _config.Save();
            }
            else if (e.PropertyName == nameof(RemoteDevice.VoiceButtonMode))
            {
                if (dev.Id == _config.Current.SelectedDeviceId || dev.BluetoothAddress == SelectedDevice?.BluetoothAddress)
                {
                    _bt.VoiceButtonMode = dev.VoiceButtonMode;
                }
                _config.Save();
            }
        }
    }

    public void Dispose()
    {
        _bt.DeviceDiscovered -= _onDeviceDiscovered;
        _bt.ConnectionStatusChanged -= _onConnectionChanged;
        foreach (var dev in DiscoveredDevices)
        {
            dev.PropertyChanged -= OnDevicePropertyChanged;
        }
    }

    [RelayCommand]
    public void AddDevice()
    {
        _openWizard?.Invoke(WizardMode.DeviceOnly);
    }

    [RelayCommand]
    public void ToggleScan()
    {
        IsScanning = !IsScanning;
        if (IsScanning)
        {
            _bt.StartScanning();
        }
        else
        {
            _bt.StopScanning();
        }
    }

    [RelayCommand]
    public void ScanForDevices()
    {
        if (!IsScanning)
        {
            ToggleScan();
        }
    }

    [RelayCommand]
    public async Task ConnectDeviceAsync(RemoteDevice? device)
    {
        if (device == null) return;
        bool ok = await _bt.ConnectAsync(device.BluetoothAddress);
        if (ok)
        {
            SelectedDevice = device;
            _bt.CurrentGain = device.Gain;
            device.IsConnected = true;
            device.ContainerId = _bt.ConnectedDeviceContainerId;
            if (device.Mappings == null || device.Mappings.Count == 0)
            {
                device.Mappings = RemoteDevice.CreateDefaultMappings(device.ModelType);
            }
            _config.Current.SelectedDeviceId = device.Id;
            if (!_config.Current.Devices.Any(d => d.Id == device.Id))
            {
                _config.Current.Devices.Add(device);
            }
            _config.Save();
            OnPropertyChanged(nameof(ConnectedDeviceName));
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    [RelayCommand]
    public void DisconnectDevice()
    {
        _bt.Disconnect();
        if (SelectedDevice != null)
        {
            SelectedDevice.IsConnected = false;
        }
        OnPropertyChanged(nameof(ConnectedDeviceName));
        OnPropertyChanged(nameof(IsConnected));
    }

    [RelayCommand]
    public void RemoveDevice(RemoteDevice? device)
    {
        if (device == null) return;
        if (device.BluetoothAddress == SelectedDevice?.BluetoothAddress)
        {
            _bt.Disconnect();
            SelectedDevice = null;
        }
        DiscoveredDevices.Remove(device);
        _config.Current.Devices.RemoveAll(d => d.Id == device.Id);
        if (_config.Current.SelectedDeviceId == device.Id)
        {
            _config.Current.SelectedDeviceId = null;
        }
        _config.Save();
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(ConnectedDeviceName));
        OnPropertyChanged(nameof(IsConnected));
    }

    [RelayCommand]
    public void SaveGain(RemoteDevice? device)
    {
        if (device == null) return;
        _bt.CurrentGain = device.Gain;
        _config.Save();
    }

    partial void OnSelectedDeviceChanged(RemoteDevice? value)
    {
        if (value != null)
        {
            _bt.CurrentGain = value.Gain;
            _config.Current.SelectedDeviceId = value.Id;
            if (value.Mappings == null || value.Mappings.Count == 0)
            {
                value.Mappings = RemoteDevice.CreateDefaultMappings(value.ModelType);
            }
            _config.Save();
        }
    }
}

public partial class MappingItemViewModel : ObservableObject
{
    public ButtonMapping Source { get; }
    public RemoteButton Button => Source.Button;
    public string ButtonName => Button switch
    {
        RemoteButton.Power => "电源键", RemoteButton.Voice => "语音键",
        RemoteButton.Up => "上键", RemoteButton.Down => "下键",
        RemoteButton.Left => "左键", RemoteButton.Right => "右键",
        RemoteButton.Ok => "确定键", RemoteButton.Back => "返回键",
        RemoteButton.Home => "主页键", RemoteButton.Menu => "菜单键",
        RemoteButton.VolumeUp => "音量 +", RemoteButton.VolumeDown => "音量 −",
        RemoteButton.LiveTv => "TV 键", RemoteButton.Mute => "静音键",
        RemoteButton.PlayPause => "播放 / 暂停", _ => Button.ToString()
    };

    [ObservableProperty]
    private ButtonActionType _clickAction;

    [ObservableProperty]
    private string _clickShortcut = string.Empty;

    [ObservableProperty]
    private MediaCommand _clickMedia;

    [ObservableProperty]
    private string _clickText = string.Empty;

    [ObservableProperty]
    private string _parameterText = string.Empty;

    [ObservableProperty]
    private ButtonActionType _longPressAction;

    public System.Windows.Visibility TextOrShortcutVisibility =>
        (ClickAction == ButtonActionType.KeyboardShortcut || ClickAction == ButtonActionType.TextSnippet)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public string ParameterLabel => ClickAction == ButtonActionType.TextSnippet ? "常用短语：" : "目标键盘快捷键：";

    public string ParameterHint => ClickAction == ButtonActionType.TextSnippet
        ? "输入要在按下遥控器按键时插入的文字；留空时不输入内容。"
        : "提示: 支持组合键，如 'Ctrl+Alt+A'、'Enter'、'Escape'、'Up'、'Alt+Tab'、'Win+D' 等。";

    public System.Windows.Visibility MediaVisibility =>
        ClickAction == ButtonActionType.MediaControl
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public static ButtonActionType[] AvailableActions { get; } =
        [ButtonActionType.None, ButtonActionType.KeyboardShortcut, ButtonActionType.MediaControl, ButtonActionType.TextSnippet];

    public static MediaCommand[] AvailableMediaCommands { get; } =
        [MediaCommand.PlayPause, MediaCommand.VolumeUp, MediaCommand.VolumeDown, MediaCommand.Mute, MediaCommand.NextTrack, MediaCommand.PreviousTrack];

    public static ButtonActionType[] AvailableLongPressActions { get; } =
        [ButtonActionType.None, ButtonActionType.ContinuousRepeat];

    private readonly Action? _onModified;
    private bool _isInitializing;

    public MappingItemViewModel(ButtonMapping source, Action? onModified = null)
    {
        _isInitializing = true;
        try
        {
            Source = source;
            _onModified = onModified;
            ClickAction = source.ClickAction;
            ClickShortcut = source.ClickShortcut ?? string.Empty;
            ClickMedia = source.ClickMedia != MediaCommand.None ? source.ClickMedia : MediaCommand.PlayPause;
            ClickText = source.ClickText ?? string.Empty;
            LongPressAction = source.LongPressAction;

            if (source.ClickAction == ButtonActionType.TextSnippet)
            {
                ParameterText = ClickText;
            }
            else
            {
                ParameterText = ClickShortcut;
            }
        }
        finally
        {
            _isInitializing = false;
        }
    }

    partial void OnClickActionChanged(ButtonActionType value)
    {
        OnPropertyChanged(nameof(TextOrShortcutVisibility));
        OnPropertyChanged(nameof(MediaVisibility));
        OnPropertyChanged(nameof(ParameterLabel));
        OnPropertyChanged(nameof(ParameterHint));

        if (_isInitializing) return;

        if (value == ButtonActionType.TextSnippet && string.IsNullOrEmpty(ParameterText))
        {
            ParameterText = ClickText;
        }
        else if (value == ButtonActionType.KeyboardShortcut && string.IsNullOrEmpty(ParameterText))
        {
            ParameterText = ClickShortcut;
        }

        ApplyBack();
        _onModified?.Invoke();
    }

    partial void OnParameterTextChanged(string value)
    {
        if (_isInitializing) return;
        ApplyBack();
        _onModified?.Invoke();
    }

    partial void OnClickMediaChanged(MediaCommand value)
    {
        if (_isInitializing) return;
        ApplyBack();
        _onModified?.Invoke();
    }

    partial void OnLongPressActionChanged(ButtonActionType value)
    {
        if (_isInitializing) return;
        ApplyBack();
        _onModified?.Invoke();
    }

    public void ApplyBack()
    {
        Source.ClickAction = ClickAction;
        Source.LongPressAction = LongPressAction;

        if (ClickAction == ButtonActionType.KeyboardShortcut)
        {
            ClickShortcut = ParameterText;
            Source.ClickShortcut = string.IsNullOrWhiteSpace(ParameterText) ? null : ParameterText.Trim();
            Source.ClickText = null;
            Source.ClickMedia = MediaCommand.None;
        }
        else if (ClickAction == ButtonActionType.TextSnippet)
        {
            ClickText = ParameterText;
            Source.ClickText = string.IsNullOrWhiteSpace(ParameterText) ? null : ParameterText;
            Source.ClickShortcut = null;
            Source.ClickMedia = MediaCommand.None;
        }
        else if (ClickAction == ButtonActionType.MediaControl)
        {
            Source.ClickMedia = ClickMedia;
            Source.ClickShortcut = null;
            Source.ClickText = null;
        }
        else
        {
            Source.ClickShortcut = null;
            Source.ClickText = null;
            Source.ClickMedia = MediaCommand.None;
        }
    }
}

public partial class MappingViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly Dictionary<string, List<MappingItemViewModel>> _drafts = new();

    [ObservableProperty]
    private ObservableCollection<MappingItemViewModel> _currentMappings = new();

    public IReadOnlyList<RemoteDevice> Devices => _config.Current.Devices;

    [ObservableProperty]
    private RemoteDevice? _selectedDevice;

    [ObservableProperty]
    private MappingItemViewModel? _selectedMapping;

    [ObservableProperty]
    private bool _isMappingSelectionLocked = true;

    [ObservableProperty]
    private bool _customMappingEnabled = true;

    [ObservableProperty]
    private RemoteButton _activeHardwareButton = RemoteButton.None;

    public bool IsG9N9N => SelectedDevice?.ModelType is RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE;
    public bool IsT1 => SelectedDevice?.ModelType == RemoteModelType.T1Remote;
    public bool IsXiaomi => !IsG9N9N && !IsT1;

    public string DeviceNote => IsG9N9N
        ? "Google G9N9N 遥控器：支持 14 颗实体按键自定义单击与长按动作，支持智能切换免提收音。"
        : IsT1
            ? "T1 飞鼠遥控器：正面 7 颗按键（电源、返回、主页、菜单、音量、静音）支持自定义；确认、方向键、飞鼠与背面 QWERTY 全键盘保持系统默认输入。"
            : "选择实体按键设置单击与长按动作。点击左侧遥控器按键快速定位，修改后自动保存。";

    public MappingViewModel(ConfigService config)
    {
        _config = config;
        _selectedDevice = config.Current.Devices.FirstOrDefault(d => d.Id == config.Current.SelectedDeviceId);
        LoadMappings();
    }

    partial void OnSelectedDeviceChanged(RemoteDevice? value)
    {
        OnPropertyChanged(nameof(DeviceNote));
        OnPropertyChanged(nameof(IsG9N9N));
        OnPropertyChanged(nameof(IsT1));
        OnPropertyChanged(nameof(IsXiaomi));
        LoadMappings();
    }

    [RelayCommand]
    public void SelectButton(string buttonName)
    {
        if (Enum.TryParse<RemoteButton>(buttonName, out var btn))
        {
            var match = CurrentMappings.FirstOrDefault(m => m.Button == btn);
            if (match != null)
            {
                SelectedMapping = match;
            }
        }
    }

    [RelayCommand]
    public void ClearSelectedMapping()
    {
        if (SelectedMapping != null)
        {
            SelectedMapping.ClickAction = ButtonActionType.None;
            SelectedMapping.LongPressAction = ButtonActionType.None;
            SelectedMapping.ParameterText = string.Empty;
            SelectedMapping.ClickShortcut = string.Empty;
            SelectedMapping.ClickText = string.Empty;
            SelectedMapping.ApplyBack();
            SaveMappings();
        }
    }

    [RelayCommand]
    public void ResetToDefaults()
    {
        var selectedDev = SelectedDevice;
        if (selectedDev != null)
        {
            selectedDev.Mappings = RemoteDevice.CreateDefaultMappings(selectedDev.ModelType);
            _drafts.Remove(selectedDev.Id);
            _config.Save();
            LoadMappings();
        }
    }

    public void LoadMappings()
    {
        CurrentMappings.Clear();
        OnPropertyChanged(nameof(Devices));
        if (SelectedDevice == null && _config.Current.Devices.Count > 0)
        {
            SelectedDevice = _config.Current.Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId)
                ?? _config.Current.Devices[0];
            return;
        }
        var selectedDev = SelectedDevice;
        if (selectedDev != null)
        {
            if (_drafts.TryGetValue(selectedDev.Id, out var draft))
            {
                foreach (var item in draft) CurrentMappings.Add(item);
                SelectedMapping = CurrentMappings.FirstOrDefault();
                return;
            }
            if (selectedDev.Mappings == null || selectedDev.Mappings.Count == 0)
            {
                selectedDev.Mappings = RemoteDevice.CreateDefaultMappings(selectedDev.ModelType);
                _config.Save();
            }
            var buttons = selectedDev.ModelType is RemoteModelType.XiaomiRC001 or RemoteModelType.XiaomiRC003
                ? new[] { RemoteButton.Power, RemoteButton.Up, RemoteButton.Left, RemoteButton.Right, RemoteButton.Ok,
                    RemoteButton.Down, RemoteButton.Back, RemoteButton.Home, RemoteButton.Menu,
                    RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.LiveTv }
                : selectedDev.Mappings.Select(m => m.Button).ToArray();
            foreach (var button in buttons)
            {
                var mapping = selectedDev.Mappings.FirstOrDefault(m => m.Button == button)
                    ?? new ButtonMapping { Button = button, ClickAction = ButtonActionType.None };
                CurrentMappings.Add(new MappingItemViewModel(mapping, OnMappingItemModified));
            }
            _drafts[selectedDev.Id] = CurrentMappings.ToList();
        }
        SelectedMapping = CurrentMappings.FirstOrDefault();
    }

    private void OnMappingItemModified()
    {
        var selectedDev = SelectedDevice;
        if (selectedDev != null)
        {
            var visibleButtons = CurrentMappings.Select(i => i.Button).ToHashSet();
            selectedDev.Mappings = selectedDev.Mappings.Where(m => !visibleButtons.Contains(m.Button))
                .Concat(CurrentMappings.Select(i => i.Source)).ToList();
            _config.Save();
        }
    }

    [RelayCommand]
    public void SaveMappings()
    {
        var selectedDev = SelectedDevice;
        if (selectedDev != null)
        {
            foreach (var item in CurrentMappings)
            {
                item.ApplyBack();
            }
            var visibleButtons = CurrentMappings.Select(i => i.Button).ToHashSet();
            selectedDev.Mappings = selectedDev.Mappings.Where(m => !visibleButtons.Contains(m.Button))
                .Concat(CurrentMappings.Select(i => i.Source)).ToList();
            _config.Save();
        }
    }
}

public partial class IntegrationsViewModel : ObservableObject
{
    private readonly ConfigService _config;

    [ObservableProperty]
    private TargetInputTool _selectedTool;

    [ObservableProperty]
    private VoiceTriggerMode _triggerMode;

    public Action? OnConfigurationSaved { get; set; }

    [ObservableProperty]
    private string _shortcutText = string.Empty;

    partial void OnShortcutTextChanged(string value)
    {
        SaveNoticeVisible = false;
        OnPropertyChanged(nameof(CurrentShortcutDisplay));
    }

    [ObservableProperty]
    private bool _saveNoticeVisible;

    public string CurrentToolDisplayName => SelectedTool switch
    {
        TargetInputTool.WeChatInput => "微信输入法",
        TargetInputTool.DoubaoInput => "豆包输入法 / 客户端",
        TargetInputTool.WindowsDictation => "Windows 语音听写 (Win+H)",
        TargetInputTool.Typeless => "Typeless 智能转写",
        _ => "自定义快捷键"
    };

    public string CurrentShortcutDisplay => string.IsNullOrWhiteSpace(ShortcutText) ? "未配置" : ShortcutText;

    public string CurrentTriggerModeDisplay => TriggerMode == VoiceTriggerMode.HoldToTalk ? "按住说话 (Hold to Talk)" : "点按切换 (Click to Toggle)";

    public string VirtualCableStatusDescription
    {
        get
        {
            var status = VirtualAudioAuditor.AuditCableInputDevice();
            return status switch
            {
                DriverReadinessStatus.Ready => "CABLE Input (VB-Audio Virtual Cable) 驱动已就绪",
                DriverReadinessStatus.NotInstalled => "未检测到 VB-CABLE 驱动，请前往【环境与驱动】安装",
                DriverReadinessStatus.RebootRequired => "VB-CABLE 需要重启系统完成初始化",
                _ => "虚拟音频设备未就绪"
            };
        }
    }

    public bool IsHoldToTalk
    {
        get => TriggerMode == VoiceTriggerMode.HoldToTalk;
        set
        {
            if (value && TriggerMode != VoiceTriggerMode.HoldToTalk)
            {
                TriggerMode = VoiceTriggerMode.HoldToTalk;
                OnPropertyChanged(nameof(IsHoldToTalk));
                OnPropertyChanged(nameof(IsClickToToggle));
                OnPropertyChanged(nameof(CurrentTriggerModeDisplay));
                Save();
            }
        }
    }

    public bool IsClickToToggle
    {
        get => TriggerMode == VoiceTriggerMode.ClickToToggle;
        set
        {
            if (value && TriggerMode != VoiceTriggerMode.ClickToToggle)
            {
                TriggerMode = VoiceTriggerMode.ClickToToggle;
                OnPropertyChanged(nameof(IsHoldToTalk));
                OnPropertyChanged(nameof(IsClickToToggle));
                OnPropertyChanged(nameof(CurrentTriggerModeDisplay));
                Save();
            }
        }
    }

    private readonly Action<WizardMode>? _openWizard;

    public IntegrationsViewModel(ConfigService config, Action<WizardMode>? openWizard = null)
    {
        _config = config;
        _openWizard = openWizard;
        Reload();
    }

    [RelayCommand]
    public void RunWizard() => _openWizard?.Invoke(WizardMode.Integration);

    public void Reload()
    {
        SelectedTool = _config.Current.InputTool.Tool;
        TriggerMode = _config.Current.InputTool.TriggerMode;
        ShortcutText = _config.Current.InputTool.Shortcut;
        SaveNoticeVisible = false;
        OnPropertyChanged(nameof(IsHoldToTalk));
        OnPropertyChanged(nameof(IsClickToToggle));
        OnPropertyChanged(nameof(CurrentToolDisplayName));
        OnPropertyChanged(nameof(CurrentShortcutDisplay));
        OnPropertyChanged(nameof(CurrentTriggerModeDisplay));
        OnPropertyChanged(nameof(VirtualCableStatusDescription));
    }

    [RelayCommand]
    public void ApplyPreset(string toolName)
    {
        if (Enum.TryParse<TargetInputTool>(toolName, out var tool))
        {
            SelectedTool = tool;
            ShortcutText = tool switch
            {
                TargetInputTool.WeChatInput => InputToolConfig.DefaultWeChatShortcut,
                TargetInputTool.DoubaoInput => "F1",
                TargetInputTool.WindowsDictation => "Win+H",
                TargetInputTool.Typeless => "Ctrl+Alt+V",
                _ => "Ctrl+Shift+V"
            };
            TriggerMode = (tool == TargetInputTool.WindowsDictation)
                ? VoiceTriggerMode.ClickToToggle
                : VoiceTriggerMode.HoldToTalk;
            OnPropertyChanged(nameof(IsHoldToTalk));
            OnPropertyChanged(nameof(IsClickToToggle));
            OnPropertyChanged(nameof(CurrentToolDisplayName));
            OnPropertyChanged(nameof(CurrentShortcutDisplay));
            OnPropertyChanged(nameof(CurrentTriggerModeDisplay));
            Save();
        }
    }

    partial void OnTriggerModeChanged(VoiceTriggerMode value)
    {
        OnPropertyChanged(nameof(IsHoldToTalk));
        OnPropertyChanged(nameof(IsClickToToggle));
        OnPropertyChanged(nameof(CurrentTriggerModeDisplay));
    }

    [RelayCommand]
    public void Save()
    {
        _config.Current.InputTool.Tool = SelectedTool;
        _config.Current.InputTool.TriggerMode = TriggerMode;
        _config.Current.InputTool.Shortcut = ShortcutText;
        _config.Save();
        SaveNoticeVisible = true;
        OnConfigurationSaved?.Invoke();
        OnPropertyChanged(nameof(CurrentToolDisplayName));
        OnPropertyChanged(nameof(CurrentShortcutDisplay));
        OnPropertyChanged(nameof(CurrentTriggerModeDisplay));
        OnPropertyChanged(nameof(VirtualCableStatusDescription));
    }
}

public partial class EnvironmentViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeText))]
    private DriverReadinessStatus _driverStatus;

    public string DriverStatusBadgeText => DriverStatus switch
    {
        DriverReadinessStatus.Ready => "已就绪",
        DriverReadinessStatus.NotInstalled => "未安装",
        DriverReadinessStatus.RebootRequired => "需重启",
        _ => "初始化失败"
    };

    [ObservableProperty]
    private string _statusDescription = string.Empty;

    public EnvironmentViewModel()
    {
        RefreshStatus();
    }

    [RelayCommand]
    public void RefreshStatus()
    {
        DriverStatus = VirtualAudioAuditor.AuditCableInputDevice();
        StatusDescription = DriverStatus switch
        {
            DriverReadinessStatus.Ready => "VB-CABLE 虚拟麦克风已就绪，音频服务工作正常。",
            DriverReadinessStatus.NotInstalled => "未检测到 VB-CABLE。请从官方网站下载并安装。",
            DriverReadinessStatus.RebootRequired => "检测到虚拟声卡安装未完全生效，请重启电脑完成系统音频服务初始化。",
            _ => "虚拟麦克风初始化失败，请在设备管理器中检查设备状态。"
        };
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly Action<WizardMode>? _openWizard;

    [ObservableProperty]
    private bool _launchAtStartup;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    [ObservableProperty]
    private string _appVersion;

    [ObservableProperty]
    private string _logDirectoryPath;

    public SettingsViewModel(ConfigService config, Action<WizardMode>? openWizard = null)
    {
        _config = config;
        _openWizard = openWizard;
        LaunchAtStartup = StartupService.IsLaunchAtStartupEnabled();
        MinimizeToTrayOnClose = _config.Current.MinimizeToTrayOnClose;
        LogDirectoryPath = AppLogger.LogDirectory;

        var infoVer = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrEmpty(infoVer))
        {
            AppVersion = "v" + infoVer.Split('+')[0];
        }
        else
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            AppVersion = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";
        }
    }

    public SettingsViewModel(ConfigService config, Action openOnboarding)
        : this(config, mode => openOnboarding())
    {
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        StartupService.SetLaunchAtStartup(value);
        _config.Current.LaunchAtStartup = value;
        _config.Save();
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        _config.Current.MinimizeToTrayOnClose = value;
        _config.Save();
    }

    [RelayCommand]
    public void RerunWizard()
    {
        _openWizard?.Invoke(WizardMode.Full);
    }

    [RelayCommand]
    public void OpenLogFolder()
    {
        AppLogger.OpenLogFolder();
    }
}

public partial class GeneralViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly IBluetoothManager _bt;
    private readonly Action<WizardMode> _openWizard;
    private readonly Action<string> _navigate;

    [ObservableProperty]
    private bool _launchAtStartup;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionsReady))]
    [NotifyPropertyChangedFor(nameof(PermissionsStatusText))]
    [NotifyPropertyChangedFor(nameof(PermissionsStatusBrush))]
    [NotifyPropertyChangedFor(nameof(PermissionsBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(PermissionsBorderBrush))]
    [NotifyPropertyChangedFor(nameof(PermissionsIcon))]
    [NotifyPropertyChangedFor(nameof(PermissionsSummary))]
    private DriverReadinessStatus _driverStatus = VirtualAudioAuditor.GetCachedStatus();

    public bool PermissionsReady => DriverStatus == DriverReadinessStatus.Ready;
    public string PermissionsStatusText => PermissionsReady ? "驱动就绪" : "缺少驱动";
    public string PermissionsStatusBrush => PermissionsReady ? "#44FF88" : "#FFAA33";
    public string PermissionsBadgeBackground => PermissionsReady ? "#1C3827" : "#332200";
    public string PermissionsBorderBrush => PermissionsReady ? "#2D6A42" : "#664400";
    public string PermissionsIcon => PermissionsReady ? "✓" : "⚠";
    public string PermissionsSummary => PermissionsReady ? "VB-CABLE 虚拟麦克风已就绪，音频服务工作正常" : "未检测到 VB-CABLE 虚拟音频驱动，请进入检查并安装";

    public bool RemoteReady => _bt.IsConnected;
    public string RemoteStatusText => RemoteReady ? "已连接" : "未连接";
    public string RemoteStatusBrush => RemoteReady ? "#44FF88" : "#FFAA33";
    public string RemoteBadgeBackground => RemoteReady ? "#1C3827" : "#332200";
    public string RemoteBorderBrush => RemoteReady ? "#2D6A42" : "#664400";
    public string RemoteIcon => RemoteReady ? "✓" : "⚠";
    public string RemoteSummary => _bt.IsConnected ? (!string.IsNullOrEmpty(_bt.ConnectedDeviceName) ? $"已连接 {_bt.ConnectedDeviceName}，语音服务就绪" : "已连接蓝牙遥控器") : "未连接遥控器";

    public bool IntegrationReady => !string.IsNullOrEmpty(_config.Current.InputTool.Shortcut);
    public string IntegrationStatusText => IntegrationReady ? "已配置" : "未配置";
    public string IntegrationStatusBrush => IntegrationReady ? "#44FF88" : "#FFAA33";
    public string IntegrationBadgeBackground => IntegrationReady ? "#1C3827" : "#332200";
    public string IntegrationBorderBrush => IntegrationReady ? "#2D6A42" : "#664400";
    public string IntegrationIcon => IntegrationReady ? "✓" : "⚠";
    public string IntegrationSummary
    {
        get
        {
            var tool = _config.Current.InputTool;
            string toolName = tool.Tool switch
            {
                TargetInputTool.WeChatInput => "微信输入法",
                TargetInputTool.DoubaoInput => "豆包输入法",
                TargetInputTool.WindowsDictation => "Windows 听写",
                TargetInputTool.Typeless => "Typeless",
                _ => "自定义工具"
            };
            string mode = tool.TriggerMode == VoiceTriggerMode.HoldToTalk ? "长按说话" : "点按切换";
            return $"{toolName} ({mode} · {tool.Shortcut})";
        }
    }

    public GeneralViewModel(ConfigService config, IBluetoothManager bt, Action<WizardMode> openWizard, Action<string> navigate)
    {
        _config = config;
        _bt = bt;
        _openWizard = openWizard;
        _navigate = navigate;

        LaunchAtStartup = StartupService.IsLaunchAtStartupEnabled();
        MinimizeToTrayOnClose = _config.Current.MinimizeToTrayOnClose;

        _bt.ConnectionStatusChanged += (_, _) =>
        {
            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(RefreshState));
            }
            else
            {
                RefreshState();
            }
        };
    }

    public GeneralViewModel(ConfigService config, IBluetoothManager bt, Action openOnboarding, Action<string> navigate)
        : this(config, bt, mode => openOnboarding(), navigate)
    {
    }

    public void RefreshState()
    {
        DriverStatus = VirtualAudioAuditor.GetCachedStatus();
        _ = Task.Run(() =>
        {
            var status = VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => DriverStatus = status));
            }
            else
            {
                DriverStatus = status;
            }
        });

        OnPropertyChanged(nameof(PermissionsReady));
        OnPropertyChanged(nameof(PermissionsStatusText));
        OnPropertyChanged(nameof(PermissionsStatusBrush));
        OnPropertyChanged(nameof(PermissionsBadgeBackground));
        OnPropertyChanged(nameof(PermissionsBorderBrush));
        OnPropertyChanged(nameof(PermissionsIcon));
        OnPropertyChanged(nameof(PermissionsSummary));

        OnPropertyChanged(nameof(RemoteReady));
        OnPropertyChanged(nameof(RemoteStatusText));
        OnPropertyChanged(nameof(RemoteStatusBrush));
        OnPropertyChanged(nameof(RemoteBadgeBackground));
        OnPropertyChanged(nameof(RemoteBorderBrush));
        OnPropertyChanged(nameof(RemoteIcon));
        OnPropertyChanged(nameof(RemoteSummary));

        OnPropertyChanged(nameof(IntegrationReady));
        OnPropertyChanged(nameof(IntegrationStatusText));
        OnPropertyChanged(nameof(IntegrationStatusBrush));
        OnPropertyChanged(nameof(IntegrationBadgeBackground));
        OnPropertyChanged(nameof(IntegrationBorderBrush));
        OnPropertyChanged(nameof(IntegrationIcon));
        OnPropertyChanged(nameof(IntegrationSummary));
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        StartupService.SetLaunchAtStartup(value);
        _config.Current.LaunchAtStartup = value;
        _config.Save();
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        _config.Current.MinimizeToTrayOnClose = value;
        _config.Save();
    }

    [RelayCommand]
    public void RerunWizard() => _openWizard(WizardMode.Full);

    [RelayCommand]
    public void RunDeviceWizard() => _openWizard(WizardMode.DeviceOnly);

    [RelayCommand]
    public void RunIntegrationWizard() => _openWizard(WizardMode.Integration);

    [RelayCommand]
    public void GoToPermissions() => _navigate("Permissions");

    [RelayCommand]
    public void GoToDevices() => _navigate("Devices");

    [RelayCommand]
    public void GoToIntegrations() => _navigate("Integrations");
}

public partial class AppIntegrationItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    private readonly Action _onChanged;

    public AppIntegrationItemViewModel(string id, string name, string detail, bool isEnabled, Action onChanged)
    {
        Id = id;
        Name = name;
        Detail = detail;
        _isEnabled = isEnabled;
        _onChanged = onChanged;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _onChanged.Invoke();
    }
}

public partial class AppIntegrationViewModel : ObservableObject
{
    private readonly ConfigService _config;

    public ObservableCollection<AppIntegrationItemViewModel> Applications { get; } = new();

    public AppIntegrationViewModel(ConfigService config)
    {
        _config = config;
        LoadIntegrations();
    }

    public void LoadIntegrations()
    {
        Applications.Clear();
        var map = _config.Current.AppIntegrations ?? new();

        var presets = new (string Id, string Name, string Detail)[]
        {
            ("wechat", "微信 (WeChat)", "优先定位微信会话窗口输入框，支持消息输入与快捷发送"),
            ("antigravity", "Antigravity", "优先聚焦 Antigravity 对话输入框，无缝输入提示词与指令"),
            ("chatgpt", "ChatGPT", "优先聚焦 ChatGPT 桌面客户端对话输入框"),
            ("claude", "Claude", "优先聚焦 Claude 桌面客户端对话输入框"),
            ("deepseek", "DeepSeek", "优先聚焦 DeepSeek 桌面客户端对话输入框"),
            ("workbuddy", "WorkBuddy", "优先聚焦 WorkBuddy 输入框，辅助快速录入与日常沟通")
        };

        foreach (var (id, name, detail) in presets)
        {
            bool enabled = map.TryGetValue(id, out var val) && val;
            Applications.Add(new AppIntegrationItemViewModel(id, name, detail, enabled, Save));
        }
    }

    private void Save()
    {
        _config.Current.AppIntegrations ??= new();
        foreach (var app in Applications)
        {
            _config.Current.AppIntegrations[app.Id] = app.IsEnabled;
        }
        _config.Save();
    }

    [RelayCommand]
    public void EnableAll()
    {
        foreach (var app in Applications)
        {
            app.IsEnabled = true;
        }
    }

    [RelayCommand]
    public void DisableAll()
    {
        foreach (var app in Applications)
        {
            app.IsEnabled = false;
        }
    }
}

public partial class AboutViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appVersion;

    [ObservableProperty]
    private string _architecture = Environment.Is64BitProcess ? "x64 (64位)" : "x86 (32位)";

    [ObservableProperty]
    private string _runtime = $".NET {Environment.Version}";

    [ObservableProperty]
    private string _osVersion = $"{Environment.OSVersion}";

    [ObservableProperty]
    private string _logDirectoryPath;

    public AboutViewModel()
    {
        LogDirectoryPath = AppLogger.LogDirectory;

        var infoVer = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrEmpty(infoVer))
        {
            AppVersion = "v" + infoVer.Split('+')[0];
        }
        else
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            AppVersion = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";
        }
    }

    [RelayCommand]
    public void OpenLogFolder()
    {
        AppLogger.OpenLogFolder();
    }

    [RelayCommand]
    public void OpenWebsite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://remote.zfdang.com") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    public void OpenGitHub()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/zfdang/remote-buddy-windows") { UseShellExecute = true });
        }
        catch { }
    }
}
