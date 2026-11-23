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
    private float _deviceGain = RemoteDevice.DefaultMicrophoneGain;

    public event Action? DevicesChanged;

    public bool HasDevices => DiscoveredDevices.Count > 0;

    public DevicesViewModel(ConfigService config, IBluetoothManager bt, Action<WizardMode>? openWizard = null)
    {
        _config = config;
        _bt = bt;
        _openWizard = openWizard;

        foreach (var dev in _config.Current.Devices)
        {
            dev.PropertyChanged += OnDevicePropertyChanged;
            dev.IsConnected = _bt.IsDeviceReady(dev.BluetoothAddress);

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
            _bt.SetDeviceVoiceSettings(_selectedDevice);
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
                foreach (var dev in DiscoveredDevices)
                {
                    dev.IsConnected = _bt.IsDeviceReady(dev.BluetoothAddress);
                }
                var connectedDev = DiscoveredDevices.FirstOrDefault(d => d.IsConnected);
                if (connectedDev != null)
                {
                    SelectedDevice = connectedDev;
                }
                RefreshHardwareDetails();
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
        _bt.ConnectedDeviceInformationChanged += OnConnectedDeviceInformationChanged;
        RefreshHardwareDetails();
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
            dev.IsConnected = _bt.IsDeviceReady(dev.BluetoothAddress);

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
            _bt.SetDeviceVoiceSettings(SelectedDevice);
        }
        OnPropertyChanged(nameof(HasDevices));
        RefreshHardwareDetails();
    }

    private void OnConnectedDeviceInformationChanged()
    {
        if (App.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshHardwareDetails);
        }
        else
        {
            RefreshHardwareDetails();
        }
    }

    private void RefreshHardwareDetails()
    {
        foreach (var device in DiscoveredDevices)
        {
            device.HardwareInformation = _bt.GetDeviceInformation(device.BluetoothAddress);
        }
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is RemoteDevice dev)
        {
            if (e.PropertyName == nameof(RemoteDevice.Gain))
            {
                if (dev.Id == _config.Current.SelectedDeviceId || dev.BluetoothAddress == SelectedDevice?.BluetoothAddress)
                {
                    _bt.SetDeviceVoiceSettings(dev);
                }
                _config.Save();
            }
            else if (e.PropertyName == nameof(RemoteDevice.VoiceButtonMode))
            {
                if (dev.Id == _config.Current.SelectedDeviceId || dev.BluetoothAddress == SelectedDevice?.BluetoothAddress)
                {
                    _bt.SetDeviceVoiceSettings(dev);
                }
                _config.Save();
            }
        }
    }

    public void Dispose()
    {
        _bt.DeviceDiscovered -= _onDeviceDiscovered;
        _bt.ConnectionStatusChanged -= _onConnectionChanged;
        _bt.ConnectedDeviceInformationChanged -= OnConnectedDeviceInformationChanged;
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

        _bt.SetDeviceVoiceSettings(device);
        bool ok = await _bt.ConnectAsync(device.BluetoothAddress);
        if (ok && _bt.IsDeviceReady(device.BluetoothAddress))
        {
            SelectedDevice = device;
            _bt.SetDeviceVoiceSettings(device);
            foreach (var connectedDevice in DiscoveredDevices)
                connectedDevice.IsConnected = _bt.IsDeviceReady(connectedDevice.BluetoothAddress);
            device.ContainerId = _bt.GetDeviceContainerId(device.BluetoothAddress);
            if (device.Mappings == null || device.Mappings.Count == 0)
            {
                device.Mappings = RemoteDevice.CreateDefaultMappings(device.ModelType);
            }
            _config.Current.SelectedDeviceId = device.Id;
            if (!_config.Current.Devices.Any(d => d.Id == device.Id))
            {
                _config.Current.Devices.Add(device);
                Reload();
                DevicesChanged?.Invoke();
            }
            _config.Save();
        }
    }

    [RelayCommand]
    public void DisconnectDevice(RemoteDevice? device = null)
    {
        if (device == null) return;
        _bt.DisconnectDevice(device.BluetoothAddress);
        device.IsConnected = false;
    }

    [RelayCommand]
    public async Task ReconnectDeviceAsync(RemoteDevice? device)
    {
        if (device == null) return;
        _bt.DisconnectDevice(device.BluetoothAddress);
        await Task.Delay(250);
        await ConnectDeviceAsync(device);
    }

    public static Func<RemoteDevice, bool>? ConfirmDeviceRemovalHandler { get; set; }

    [RelayCommand]
    public void RemoveDevice(RemoteDevice? device)
    {
        if (device == null) return;

        bool confirmed;
        if (ConfirmDeviceRemovalHandler != null)
        {
            confirmed = ConfirmDeviceRemovalHandler(device);
        }
        else if (IsRunningInTestHost())
        {
            confirmed = true;
        }
        else
        {
            var result = System.Windows.MessageBox.Show(
                $"确定要删除设备 “{device.Name}” 吗？\n\n将删除该设备及其在遥控搭子中的按键设置和音量增益，并停止自动连接。\n此操作不会删除 Windows 蓝牙配对；使用“添加设备”时，之前删除的设备仍可再次被发现并添加。",
                "删除此设备？",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            confirmed = result == System.Windows.MessageBoxResult.Yes;
        }

        if (!confirmed) return;

        if (device.BluetoothAddress == SelectedDevice?.BluetoothAddress || device.IsConnected)
        {
            _bt.DisconnectDevice(device.BluetoothAddress);
            if (SelectedDevice?.BluetoothAddress == device.BluetoothAddress) SelectedDevice = null;
        }
        DiscoveredDevices.Remove(device);
        _config.Current.Devices.RemoveAll(d => d.Id == device.Id);
        DevicesChanged?.Invoke();
        if (_config.Current.SelectedDeviceId == device.Id)
        {
            _config.Current.SelectedDeviceId = null;
        }
        _config.Save();
        OnPropertyChanged(nameof(HasDevices));
    }

    [RelayCommand]
    public void SaveGain(RemoteDevice? device)
    {
        if (device == null) return;
        _bt.SetDeviceVoiceSettings(device);
        _config.Save();
    }

    partial void OnSelectedDeviceChanged(RemoteDevice? value)
    {
        if (value != null)
        {
            _bt.SetDeviceVoiceSettings(value);
            _config.Current.SelectedDeviceId = value.Id;
            if (value.Mappings == null || value.Mappings.Count == 0)
            {
                value.Mappings = RemoteDevice.CreateDefaultMappings(value.ModelType);
            }
            _config.Save();
        }
    }

    internal static bool IsRunningInTestHost()
    {
        string processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
        if (processName.Contains("testhost") || processName.Contains("vstest") || processName.Contains("xunit"))
        {
            return true;
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.GetName().Name?.ToLowerInvariant().Contains("xunit") == true ||
                      a.GetName().Name?.ToLowerInvariant().Contains("test") == true);
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
        RemoteButton.PlayPause => "播放 / 暂停", RemoteButton.Input => "输入源",
        _ => Button.ToString()
    };

    public MappingGestureViewModel SingleClick { get; }
    public MappingGestureViewModel LongPress { get; }
    public MappingGestureViewModel DoubleClick { get; }

    public string SingleClickSummary => SingleClick.DisplaySummary;
    public string LongPressSummary => LongPress.DisplaySummary;
    public string DoubleClickSummary => DoubleClick.DisplaySummary;

    public MappingItemViewModel(ButtonMapping source, Action? onModified = null)
    {
        Source = source;
        Action wrapModified = () =>
        {
            OnPropertyChanged(nameof(SingleClickSummary));
            OnPropertyChanged(nameof(LongPressSummary));
            OnPropertyChanged(nameof(DoubleClickSummary));
            onModified?.Invoke();
        };
        SingleClick = new MappingGestureViewModel(source, ButtonTrigger.SingleClick, wrapModified);
        LongPress = new MappingGestureViewModel(source, ButtonTrigger.LongPress, wrapModified);
        DoubleClick = new MappingGestureViewModel(source, ButtonTrigger.DoubleClick, wrapModified);
    }

    public void Clear()
    {
        SingleClick.Clear();
        LongPress.Clear();
        DoubleClick.Clear();
        OnPropertyChanged(nameof(SingleClickSummary));
        OnPropertyChanged(nameof(LongPressSummary));
        OnPropertyChanged(nameof(DoubleClickSummary));
    }
}

public partial class MappingGestureViewModel : ObservableObject
{
    private static readonly ButtonActionType[] StandardActions =
        [ButtonActionType.None, ButtonActionType.KeyboardShortcut, ButtonActionType.MediaControl, ButtonActionType.TextSnippet];

    private static readonly ButtonActionType[] LongPressActions =
        [.. StandardActions, ButtonActionType.ContinuousRepeat];

    private readonly ButtonMapping _mapping;
    private readonly ButtonTrigger _trigger;
    private readonly Action? _onModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Help))]
    [NotifyPropertyChangedFor(nameof(DisplaySummary))]
    [NotifyPropertyChangedFor(nameof(IsConfigured))]
    private ButtonActionType _action;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySummary))]
    [NotifyPropertyChangedFor(nameof(IsConfigured))]
    private string _shortcut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySummary))]
    [NotifyPropertyChangedFor(nameof(IsConfigured))]
    private MediaCommand _media;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySummary))]
    [NotifyPropertyChangedFor(nameof(IsConfigured))]
    private string _text;

    public bool IsConfigured => Action switch
    {
        ButtonActionType.None => false,
        ButtonActionType.KeyboardShortcut => !string.IsNullOrWhiteSpace(Shortcut),
        ButtonActionType.MediaControl => Media != MediaCommand.None,
        ButtonActionType.TextSnippet => !string.IsNullOrWhiteSpace(Text),
        ButtonActionType.ContinuousRepeat => true,
        _ => false
    };

    public string DisplaySummary => Action switch
    {
        ButtonActionType.None => "未配置",
        ButtonActionType.KeyboardShortcut => string.IsNullOrWhiteSpace(Shortcut) ? "快捷键 (未配置)" : $"快捷键: {Shortcut}",
        ButtonActionType.MediaControl => Media switch
        {
            MediaCommand.PlayPause => "媒体: 播放/暂停",
            MediaCommand.VolumeUp => "媒体: 音量加 (+)",
            MediaCommand.VolumeDown => "媒体: 音量减 (-)",
            MediaCommand.Mute => "媒体: 静音",
            MediaCommand.NextTrack => "媒体: 下一首",
            MediaCommand.PreviousTrack => "媒体: 上一首",
            _ => "多媒体控制"
        },
        ButtonActionType.TextSnippet => string.IsNullOrWhiteSpace(Text) ? "文本短语 (未设置)" : $"短语: \"{(Text.Length > 8 ? Text[..8] + "…" : Text)}\"",
        ButtonActionType.ContinuousRepeat => "持续连发",
        _ => "无"
    };

    public static MediaCommand[] AvailableMediaCommands { get; } =
        [MediaCommand.PlayPause, MediaCommand.VolumeUp, MediaCommand.VolumeDown, MediaCommand.Mute, MediaCommand.NextTrack, MediaCommand.PreviousTrack];

    public static IReadOnlyList<KeyValuePair<string, string>> ShortcutPresets { get; } =
    [
        new("复制 (Ctrl+C)", "Ctrl+C"), new("粘贴 (Ctrl+V)", "Ctrl+V"), new("撤销 (Ctrl+Z)", "Ctrl+Z"),
        new("全选 (Ctrl+A)", "Ctrl+A"), new("保存 (Ctrl+S)", "Ctrl+S"),
        new("切换应用 (Alt+Tab)", "Alt+Tab"), new("任务视图 (Win+Tab)", "Win+Tab"),
        new("显示桌面 (Win+D)", "Win+D"), new("截图 (Win+Shift+S)", "Win+Shift+S"),
        new("上一页 (PageUp)", "PageUp"), new("下一页 (PageDown)", "PageDown"),
        new("回车 (Enter)", "Return"), new("空格 (Space)", "Space"), new("Esc 取消", "Escape"),
        new("全屏 (F11)", "F11")
    ];

    public ButtonActionType[] AvailableActions => _trigger == ButtonTrigger.LongPress ? LongPressActions : StandardActions;

    public string Help => _trigger switch
    {
        ButtonTrigger.SingleClick => "没有配置其他手势时，按下即执行。配置独立长按后，短按在松开时执行。配置双击后，单击会等待 300 毫秒。",
        ButtonTrigger.LongPress when Action == ButtonActionType.ContinuousRepeat => "按住 350 毫秒后开始重复单击动作，松开即停。",
        ButtonTrigger.LongPress => "按住约半秒即可执行长按动作。长按动作与持续连发可任选一种。",
        _ => "连续按两次并松开即可执行双击动作。设置后，单击会等待 300 毫秒。"
    };

    public MappingGestureViewModel(ButtonMapping mapping, ButtonTrigger trigger, Action? onModified)
    {
        _mapping = mapping;
        _trigger = trigger;
        _onModified = onModified;
        var assignment = mapping.GetAssignment(trigger);
        _action = assignment.Action;
        _shortcut = assignment.Shortcut ?? string.Empty;
        _media = assignment.Media == MediaCommand.None ? MediaCommand.PlayPause : assignment.Media;
        _text = assignment.Text ?? string.Empty;
    }

    partial void OnActionChanged(ButtonActionType value) => Apply();
    partial void OnShortcutChanged(string value) => Apply();
    partial void OnMediaChanged(MediaCommand value) => Apply();
    partial void OnTextChanged(string value) => Apply();

    internal void Clear()
    {
        Action = ButtonActionType.None;
        Shortcut = string.Empty;
        Media = MediaCommand.PlayPause;
        Text = string.Empty;
    }

    private void Apply()
    {
        var assignment = Action switch
        {
            ButtonActionType.KeyboardShortcut when !string.IsNullOrWhiteSpace(Shortcut) => new ButtonAssignment(Action, Shortcut.Trim()),
            ButtonActionType.MediaControl => new ButtonAssignment(Action, Media: Media),
            ButtonActionType.TextSnippet when !string.IsNullOrWhiteSpace(Text) => new ButtonAssignment(Action, Text: Text),
            _ => new ButtonAssignment(Action)
        };
        _mapping.SetAssignment(_trigger, assignment);
        _onModified?.Invoke();
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
    private ButtonTrigger _selectedTrigger = ButtonTrigger.SingleClick;

    public MappingGestureViewModel? ActiveGesture => SelectedMapping == null ? null : SelectedTrigger switch
    {
        ButtonTrigger.SingleClick => SelectedMapping.SingleClick,
        ButtonTrigger.LongPress => SelectedMapping.LongPress,
        ButtonTrigger.DoubleClick => SelectedMapping.DoubleClick,
        _ => SelectedMapping.SingleClick
    };

    public string ActiveTriggerTitle => SelectedTrigger switch
    {
        ButtonTrigger.SingleClick => "👆 单击设置",
        ButtonTrigger.LongPress => "⏱️ 长按设置",
        ButtonTrigger.DoubleClick => "✌️ 双击设置",
        _ => "手势设置"
    };

    partial void OnSelectedMappingChanged(MappingItemViewModel? value)
    {
        OnPropertyChanged(nameof(ActiveGesture));
    }

    partial void OnSelectedTriggerChanged(ButtonTrigger value)
    {
        OnPropertyChanged(nameof(ActiveGesture));
        OnPropertyChanged(nameof(ActiveTriggerTitle));
    }

    [RelayCommand]
    public void SelectTrigger(string triggerName)
    {
        if (Enum.TryParse<ButtonTrigger>(triggerName, true, out var trigger))
        {
            SelectedTrigger = trigger;
        }
    }

    [ObservableProperty]
    private bool _isMappingSelectionLocked = true;

    [ObservableProperty]
    private bool _customMappingEnabled = true;

    internal event Action<RemoteButton>? HardwareButtonPressed;

    public bool IsG9N9N => SelectedDevice?.ModelType is RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE;
    public bool IsT1 => SelectedDevice?.ModelType == RemoteModelType.T1Remote;
    public bool IsXiaomi => !IsG9N9N && !IsT1;

    public string DeviceNote => IsG9N9N
        ? "选择 Chromecast 遥控器按键，设置单击、长按和双击动作。"
        : IsT1
            ? "T1 飞鼠遥控器：正面 7 颗按键（电源、返回、主页、菜单、音量、静音）支持自定义；确认、方向键、飞鼠与背面 QWERTY 全键盘保持系统默认输入。"
            : "选择按键，设置单击、长按和双击动作。";

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

    internal void NotifyHardwarePress(RemoteButton button)
    {
        if (!IsMappingSelectionLocked) SelectButton(button.ToString());
        HardwareButtonPressed?.Invoke(button);
    }

    [RelayCommand]
    public void ClearSelectedMapping()
    {
        if (SelectedMapping != null)
        {
            SelectedMapping.Clear();
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

    public void SyncSelectedDeviceWithConfig()
    {
        var targetId = _config.Current.SelectedDeviceId;
        if (!string.IsNullOrEmpty(targetId))
        {
            var match = _config.Current.Devices.FirstOrDefault(d => d.Id == targetId);
            if (match != null)
            {
                if (SelectedDevice == null || SelectedDevice.Id != match.Id)
                {
                    SelectedDevice = match;
                }
                else
                {
                    LoadMappings();
                }
                return;
            }
        }
        else if (SelectedDevice == null && _config.Current.Devices.Count > 0)
        {
            SelectedDevice = _config.Current.Devices[0];
            return;
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

    public TargetInputTool SelectedTool => _config.Current.InputTool.Tool;

    public string CurrentToolDisplayName => SelectedTool switch
    {
        TargetInputTool.WeChatInput => "微信输入法",
        TargetInputTool.DoubaoInput => "豆包输入法 / 客户端",
        TargetInputTool.WindowsDictation => "Windows 语音听写 (Win+H)",
        TargetInputTool.Typeless => "Typeless 智能转写",
        _ => "自定义快捷键"
    };

    public string CurrentShortcutDisplay => string.IsNullOrWhiteSpace(_config.Current.InputTool.Shortcut) ? "未配置" : _config.Current.InputTool.Shortcut;

    public string CurrentTriggerModeDisplay => _config.Current.InputTool.TriggerMode == VoiceTriggerMode.HoldToTalk ? "按住说话" : "单击切换";

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
        OnPropertyChanged(nameof(SelectedTool));
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

    public bool IsAdministrator => BluetoothAdapterHelper.IsAdministrator();

    public string AdminStatusBadgeText => IsAdministrator ? "已就绪" : "未获取 (建议提权)";

    public string AdminStatusBadgeColor => IsAdministrator ? "#44FF88" : "#FFB347";

    public string AdminStatusBadgeBgColor => IsAdministrator ? "#1C3827" : "#3D2E18";

    public string AdminStatusBadgeBorderColor => IsAdministrator ? "#2D6A42" : "#8A5A1A";

    public string AdminStatusDescription => IsAdministrator
        ? "程序当前已获得 Windows 管理员权限，内核 ETW 驱动参数与底层硬件事件实时捕获均处于最佳状态。"
        : "当前以普通用户权限运行。我们建议以管理员身份运行本程序；请在下次启动时右键选择“以管理员身份运行”，或点击下方按钮立即以管理员权限重启。";

    [ObservableProperty]
    private string _statusDescription = string.Empty;

    public EnvironmentViewModel()
    {
        RefreshStatus();
    }

    [RelayCommand]
    public void RestartAsAdministrator()
    {
        BluetoothAdapterHelper.RestartAppAsAdministrator();
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
        OnPropertyChanged(nameof(IsAdministrator));
        OnPropertyChanged(nameof(AdminStatusBadgeText));
        OnPropertyChanged(nameof(AdminStatusBadgeColor));
        OnPropertyChanged(nameof(AdminStatusBadgeBgColor));
        OnPropertyChanged(nameof(AdminStatusBadgeBorderColor));
        OnPropertyChanged(nameof(AdminStatusDescription));
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

    [RelayCommand]
    public void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
