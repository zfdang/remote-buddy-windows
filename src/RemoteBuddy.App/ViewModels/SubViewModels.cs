// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteBuddy.App.Services;
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
    private readonly Action<ulong, bool, string> _onConnectionChanged;
    private readonly object _gainSaveLock = new();
    private System.Threading.Timer? _gainSaveTimer;
    private bool _hasPendingGainSave;
    private bool _isReloading;
    private bool _disposed;
    private const int GainSaveDebounceMs = 300;

    [ObservableProperty]
    private ObservableCollection<RemoteDevice> _discoveredDevices = new();

    [ObservableProperty]
    private RemoteDevice? _selectedDevice;

    [ObservableProperty]
    private float _deviceGain = RemoteDevice.DefaultMicrophoneGain;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionFeedback))]
    private string _connectionFeedbackMessage = string.Empty;

    public bool HasConnectionFeedback => !string.IsNullOrEmpty(ConnectionFeedbackMessage);

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
                var existing = DiscoveredDevices.FirstOrDefault(d =>
                    (dev.BluetoothAddress != 0 && d.BluetoothAddress == dev.BluetoothAddress) ||
                    (!string.IsNullOrEmpty(dev.Id) && d.Id == dev.Id));
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

        _onConnectionChanged = (addr, connected, name) =>
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
                    ConnectionFeedbackMessage = string.Empty;
                }
                if (connectedDev != null && (SelectedDevice == null || !DiscoveredDevices.Contains(SelectedDevice)))
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
        _isReloading = true;
        try
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
        finally
        {
            _isReloading = false;
        }
    }

    private void OnConnectedDeviceInformationChanged(ulong address)
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
        if (_isReloading) return;
        if (sender is RemoteDevice dev)
        {
            if (e.PropertyName == nameof(RemoteDevice.Gain))
            {
                if (dev.IsConnected || dev.Id == _config.Current.SelectedDeviceId || dev.BluetoothAddress == SelectedDevice?.BluetoothAddress)
                {
                    _bt.SetDeviceVoiceSettings(dev);
                }
                ScheduleGainSave();
            }
            else if (e.PropertyName == nameof(RemoteDevice.VoiceButtonMode))
            {
                if (dev.IsConnected || dev.Id == _config.Current.SelectedDeviceId || dev.BluetoothAddress == SelectedDevice?.BluetoothAddress)
                {
                    _bt.SetDeviceVoiceSettings(dev);
                }
                _config.Save();
            }
        }
    }

    private void ScheduleGainSave()
    {
        lock (_gainSaveLock)
        {
            if (_disposed) return;
            _hasPendingGainSave = true;
            _gainSaveTimer ??= new System.Threading.Timer(_ => FlushPendingGainSave(), null, GainSaveDebounceMs, Timeout.Infinite);
            _gainSaveTimer.Change(GainSaveDebounceMs, Timeout.Infinite);
        }
    }

    public void FlushPendingGainSave()
    {
        bool shouldSave;
        lock (_gainSaveLock)
        {
            shouldSave = _hasPendingGainSave;
            _hasPendingGainSave = false;
            _gainSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (shouldSave)
        {
            _config.Save();
        }
    }

    public void Dispose()
    {
        FlushPendingGainSave();
        lock (_gainSaveLock)
        {
            _disposed = true;
            _gainSaveTimer?.Dispose();
            _gainSaveTimer = null;
        }
        _bt.DeviceDiscovered -= _onDeviceDiscovered;
        _bt.ConnectionStatusChanged -= _onConnectionChanged;
        _bt.ConnectedDeviceInformationChanged -= OnConnectedDeviceInformationChanged;
        foreach (var dev in DiscoveredDevices)
        {
            dev.PropertyChanged -= OnDevicePropertyChanged;
        }
    }

    internal Action<string> OpenUrlAction { get; set; } = AppLinks.OpenExternalUrl;

    [RelayCommand]
    public void AddDevice()
    {
        _openWizard?.Invoke(WizardMode.DeviceOnly);
    }

    [RelayCommand]
    public void OpenDeviceGuide() => OpenUrlAction(AppLinks.DevicesFaqUrl);

    [RelayCommand]
    public void DismissConnectionFeedback() => ConnectionFeedbackMessage = string.Empty;

    [RelayCommand]
    public async Task ConnectDeviceAsync(RemoteDevice? device)
    {
        if (device == null) return;

        ConnectionFeedbackMessage = string.Empty;
        bool ok = false;
        string? errorMessage = null;
        try
        {
            _bt.SetDeviceVoiceSettings(device);
            ok = await _bt.ConnectAsync(device.BluetoothAddress);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            AppLogger.Error($"ConnectDeviceAsync failed for '{device.Name}' (0x{device.BluetoothAddress:X12})", ex, "DevicesViewModel");
        }

        if (ok && _bt.IsDeviceReady(device.BluetoothAddress))
        {
            ConnectionFeedbackMessage = string.Empty;
            if (string.IsNullOrEmpty(device.Id))
            {
                device.Id = device.BluetoothAddress != 0 ? device.BluetoothAddress.ToString("X12") : Guid.NewGuid().ToString("N");
            }
            device.ContainerId = _bt.GetDeviceContainerId(device.BluetoothAddress);
            if (device.Mappings == null || device.Mappings.Count == 0)
            {
                device.Mappings = RemoteDevice.CreateDefaultMappings(device.ModelType);
            }
            bool hadPhysicalMatch = _config.Current.Devices.Any(d => d.MatchesPhysicalDevice(device));
            var canonical = _config.UpsertDevice(device, select: true, save: true);
            SelectedDevice = canonical;
            _bt.SetDeviceVoiceSettings(canonical);
            foreach (var connectedDevice in DiscoveredDevices)
                connectedDevice.IsConnected = _bt.IsDeviceReady(connectedDevice.BluetoothAddress);
            if (!hadPhysicalMatch || !DiscoveredDevices.Contains(canonical))
            {
                Reload();
                DevicesChanged?.Invoke();
            }
        }
        else
        {
            ConnectionFeedbackMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? $"无法连接「{device.Name}」的蓝牙语音服务。请确保遥控器已配对、电量充足，且未被其他应用或设备独占连接。"
                : $"连接「{device.Name}」失败：{errorMessage}";
            AppLogger.Warn($"DevicesViewModel connect failed for '{device.Name}' (0x{device.BluetoothAddress:X12}): {ConnectionFeedbackMessage}", "DevicesViewModel");
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

        device.PropertyChanged -= OnDevicePropertyChanged;
        if (device.BluetoothAddress != 0)
        {
            _bt.RemoveDevice(device.BluetoothAddress);
        }
        else if (device.BluetoothAddress == SelectedDevice?.BluetoothAddress || device.IsConnected)
        {
            _bt.DisconnectDevice(device.BluetoothAddress);
        }
        if (SelectedDevice?.MatchesPhysicalDevice(device) == true) SelectedDevice = null;
        for (int i = DiscoveredDevices.Count - 1; i >= 0; i--)
        {
            var entry = DiscoveredDevices[i];
            if (entry.MatchesPhysicalDevice(device) || entry.Id == device.Id)
            {
                entry.PropertyChanged -= OnDevicePropertyChanged;
                DiscoveredDevices.RemoveAt(i);
            }
        }
        _config.Update(cfg =>
        {
            cfg.Devices.RemoveAll(d => d.MatchesPhysicalDevice(device) || d.Id == device.Id);
            if (!string.IsNullOrEmpty(cfg.SelectedDeviceId) &&
                !cfg.Devices.Any(d => string.Equals(d.Id, cfg.SelectedDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                cfg.SelectedDeviceId = null;
            }
        });
        DevicesChanged?.Invoke();
        OnPropertyChanged(nameof(HasDevices));
    }

    [RelayCommand]
    public void SaveGain(RemoteDevice? device)
    {
        if (device == null) return;
        _bt.SetDeviceVoiceSettings(device);
        lock (_gainSaveLock)
        {
            _hasPendingGainSave = false;
            _gainSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _config.Save();
    }

    partial void OnSelectedDeviceChanged(RemoteDevice? value)
    {
        if (value != null)
        {
            _bt.SetDeviceVoiceSettings(value);
            if (_isReloading) return;
            _config.Update(cfg =>
            {
                cfg.SelectedDeviceId = value.Id;
                if (value.Mappings == null || value.Mappings.Count == 0)
                {
                    value.Mappings = RemoteDevice.CreateDefaultMappings(value.ModelType);
                }
            });
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
    public string OwnerDeviceId { get; }
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
        RemoteButton.Custom => "自定义键 (★)",
        _ => Button.ToString()
    };

    public MappingGestureViewModel SingleClick { get; }
    public MappingGestureViewModel LongPress { get; }
    public MappingGestureViewModel DoubleClick { get; }

    public string SingleClickSummary => SingleClick.DisplaySummary;
    public string LongPressSummary => LongPress.DisplaySummary;
    public string DoubleClickSummary => DoubleClick.DisplaySummary;

    public MappingItemViewModel(ButtonMapping source, Action? onModified = null, string ownerDeviceId = "")
    {
        Source = source;
        OwnerDeviceId = ownerDeviceId;
        Action wrapModified = () =>
        {
            OnPropertyChanged(nameof(SingleClickSummary));
            OnPropertyChanged(nameof(LongPressSummary));
            OnPropertyChanged(nameof(DoubleClickSummary));
            LongPress?.NotifyRepeatDependencyChanged();
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
        LongPress.NotifyRepeatDependencyChanged();
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
    [NotifyPropertyChangedFor(nameof(ShowRepeatNeedsClickWarning))]
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

    private string _shortcutValidationError = string.Empty;
    public string ShortcutValidationError
    {
        get => _shortcutValidationError;
        private set
        {
            if (SetProperty(ref _shortcutValidationError, value))
            {
                OnPropertyChanged(nameof(HasShortcutError));
            }
        }
    }

    public bool HasShortcutError => !string.IsNullOrEmpty(ShortcutValidationError);

    public bool ShowRepeatNeedsClickWarning =>
        _trigger == ButtonTrigger.LongPress &&
        Action == ButtonActionType.ContinuousRepeat &&
        !HasConfiguredSingleClick();

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

    private bool HasConfiguredSingleClick() => _mapping.ClickAction switch
    {
        ButtonActionType.KeyboardShortcut => !string.IsNullOrWhiteSpace(_mapping.ClickShortcut),
        ButtonActionType.MediaControl => _mapping.ClickMedia != MediaCommand.None,
        ButtonActionType.TextSnippet => !string.IsNullOrWhiteSpace(_mapping.ClickText),
        _ => false
    };

    internal void NotifyRepeatDependencyChanged() => OnPropertyChanged(nameof(ShowRepeatNeedsClickWarning));

    internal void Clear()
    {
        Action = ButtonActionType.None;
        Shortcut = string.Empty;
        Media = MediaCommand.PlayPause;
        Text = string.Empty;
    }

    private void Apply()
    {
        if (Action == ButtonActionType.KeyboardShortcut && !string.IsNullOrWhiteSpace(Shortcut))
        {
            string trimmed = Shortcut.Trim();
            if (!NativeInputSender.TryParseShortcut(trimmed, out _, out _))
            {
                ShortcutValidationError = $"无法识别快捷键“{trimmed}”，请使用如 Ctrl+C、Alt+Tab、F11 等格式";
                return;
            }
            ShortcutValidationError = string.Empty;
        }
        else
        {
            ShortcutValidationError = string.Empty;
        }

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
    private readonly Action<WizardMode>? _openWizard;
    private readonly Dictionary<string, List<MappingItemViewModel>> _drafts = new();

    public int MappingRevision { get; private set; }

    [ObservableProperty]
    private ObservableCollection<MappingItemViewModel> _currentMappings = new();

    public ObservableCollection<RemoteDevice> Devices { get; } = new();

    public bool HasDevices => Devices.Count > 0;

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
    public bool IsG9N9NLayout => SelectedDevice?.ModelType == RemoteModelType.GoogleG9N9N;
    public bool IsGY3LELayout => SelectedDevice?.ModelType == RemoteModelType.GoogleGY3LE;
    public bool IsT1 => SelectedDevice?.ModelType == RemoteModelType.T1Remote;
    public bool IsXiaomi => SelectedDevice != null && !IsG9N9N && !IsT1;

    public string DeviceNote => SelectedDevice == null
        ? "请先添加并连接遥控器后再配置自定义按键映射。"
        : IsGY3LELayout
            ? "Google TV Streamer (GY3LE) 遥控器：包含方向键、实体电源/静音、自定义星标键 (★) 与侧边音量键，支持单击、长按和双击映射。"
            : IsG9N9NLayout
                ? "选择 Chromecast (G9N9N) 遥控器按键，设置单击、长按和双击动作。"
                : IsT1
                    ? "T1 飞鼠遥控器：正面 7 颗按键（电源、返回、主页、菜单、音量、静音）支持自定义；确认、方向键、飞鼠与背面 QWERTY 全键盘保持系统默认输入。"
                    : "选择按键，设置单击、长按和双击动作。";

    public MappingViewModel(ConfigService config) : this(config, null)
    {
    }

    public MappingViewModel(ConfigService config, Action<WizardMode>? openWizard)
    {
        _config = config;
        _openWizard = openWizard;
        SyncDevicesCollection();
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId);
        LoadMappings();
    }

    private bool _isSyncingDevices;

    [RelayCommand]
    public void AddDevice()
    {
        _openWizard?.Invoke(WizardMode.DeviceOnly);
    }

    partial void OnSelectedDeviceChanged(RemoteDevice? value)
    {
        OnPropertyChanged(nameof(DeviceNote));
        OnPropertyChanged(nameof(IsG9N9N));
        OnPropertyChanged(nameof(IsG9N9NLayout));
        OnPropertyChanged(nameof(IsGY3LELayout));
        OnPropertyChanged(nameof(IsT1));
        OnPropertyChanged(nameof(IsXiaomi));
        if (!_isSyncingDevices)
        {
            LoadMappings();
        }
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

    public void ResetToDefaults()
    {
        var selectedDev = SelectedDevice;
        if (selectedDev != null && _config.Current.Devices.Contains(selectedDev))
        {
            var defaults = RemoteDevice.CreateDefaultMappings(selectedDev.ModelType);
            _drafts.Remove(selectedDev.Id);
            _config.Update(cfg =>
            {
                if (cfg.Devices.Contains(selectedDev))
                {
                    selectedDev.Mappings = defaults;
                }
            });
            LoadMappings();
        }
    }

    public void SyncSelectedDeviceWithConfig()
    {
        SyncDevicesCollection();
        var targetId = _config.Current.SelectedDeviceId;
        if (!string.IsNullOrEmpty(targetId))
        {
            var match = Devices.FirstOrDefault(d => d.Id == targetId);
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
        else if (SelectedDevice == null && Devices.Count > 0)
        {
            SelectedDevice = Devices[0];
            return;
        }
    }

    private void SyncDevicesCollection()
    {
        if (_isSyncingDevices) return;
        _isSyncingDevices = true;
        try
        {
            var desired = ConfigService.DeduplicateDevices(_config.Current.Devices, _config.Current.SelectedDeviceId);
            var configuredIds = desired.Select(d => d.Id).ToHashSet();

            foreach (var staleId in _drafts.Keys.Where(id => !configuredIds.Contains(id)).ToArray())
            {
                _drafts.Remove(staleId);
            }

            // Reconcile Devices in-place rather than Clear()+Add() so WPF ComboBox preserves
            // selection and re-entrant CollectionChanged callbacks never duplicate entries.
            for (int i = Devices.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(Devices[i]))
                {
                    Devices.RemoveAt(i);
                }
            }

            for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
            {
                var item = desired[targetIndex];
                int currentIndex = Devices.IndexOf(item);
                if (currentIndex < 0)
                {
                    Devices.Insert(targetIndex, item);
                }
                else if (currentIndex != targetIndex)
                {
                    Devices.Move(currentIndex, targetIndex);
                }
            }

            OnPropertyChanged(nameof(HasDevices));
        }
        finally
        {
            _isSyncingDevices = false;
        }
    }

    public void SyncWithDevices()
    {
        SyncDevicesCollection();
        if (SelectedDevice != null && !_config.Current.Devices.Contains(SelectedDevice))
        {
            var replacement = !string.IsNullOrEmpty(_config.Current.SelectedDeviceId)
                ? Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId)
                : null;
            SelectedDevice = replacement ?? Devices.FirstOrDefault();
            if (SelectedDevice == null)
            {
                LoadMappings();
            }
            return;
        }
        LoadMappings();
    }

    public void LoadMappings()
    {
        SyncDevicesCollection();
        CurrentMappings.Clear();

        if (SelectedDevice != null && !_config.Current.Devices.Contains(SelectedDevice))
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId)
                ?? Devices.FirstOrDefault();
            return;
        }

        if (SelectedDevice == null && Devices.Count > 0)
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId)
                ?? Devices[0];
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
                var defaults = RemoteDevice.CreateDefaultMappings(selectedDev.ModelType);
                _config.Update(cfg =>
                {
                    if (cfg.Devices.Contains(selectedDev))
                    {
                        selectedDev.Mappings = defaults;
                    }
                });
            }
            var ownerMappings = selectedDev.Mappings ?? [];
            var buttons = selectedDev.ModelType switch
            {
                RemoteModelType.XiaomiRC001 or RemoteModelType.XiaomiRC003 =>
                    new[] { RemoteButton.Power, RemoteButton.Up, RemoteButton.Left, RemoteButton.Right, RemoteButton.Ok,
                        RemoteButton.Down, RemoteButton.Back, RemoteButton.Home, RemoteButton.Menu,
                        RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.LiveTv },
                RemoteModelType.GoogleGY3LE =>
                    ownerMappings.Select(m => m.Button).Append(RemoteButton.Custom).Distinct().ToArray(),
                _ => ownerMappings.Select(m => m.Button).ToArray()
            };
            var owner = selectedDev;
            foreach (var button in buttons)
            {
                var mapping = ownerMappings.FirstOrDefault(m => m.Button == button)
                    ?? new ButtonMapping { Button = button, ClickAction = ButtonActionType.None };
                CurrentMappings.Add(new MappingItemViewModel(mapping, () => OnMappingItemModified(owner), owner.Id));
            }
            _drafts[owner.Id] = CurrentMappings.ToList();
        }
        SelectedMapping = CurrentMappings.FirstOrDefault();
    }

    private void OnMappingItemModified(RemoteDevice ownerDevice)
    {
        if (!_config.Current.Devices.Contains(ownerDevice))
        {
            _drafts.Remove(ownerDevice.Id);
            return;
        }

        if (!_drafts.TryGetValue(ownerDevice.Id, out var ownerDraft))
        {
            return;
        }

        if (ReferenceEquals(SelectedDevice, ownerDevice))
        {
            MappingRevision++;
            OnPropertyChanged(nameof(MappingRevision));
        }

        var visibleButtons = ownerDraft.Select(i => i.Button).ToHashSet();
        var merged = (ownerDevice.Mappings ?? []).Where(m => !visibleButtons.Contains(m.Button))
            .Concat(ownerDraft.Select(i => i.Source)).ToList();
        _config.Update(cfg =>
        {
            if (cfg.Devices.Contains(ownerDevice))
            {
                ownerDevice.Mappings = merged;
            }
        });
    }

    [RelayCommand]
    public void SaveMappings()
    {
        var selectedDev = SelectedDevice;
        if (selectedDev != null && _config.Current.Devices.Contains(selectedDev))
        {
            var sourceItems = _drafts.TryGetValue(selectedDev.Id, out var draft) ? draft : CurrentMappings.ToList();
            var visibleButtons = sourceItems.Select(i => i.Button).ToHashSet();
            var merged = (selectedDev.Mappings ?? []).Where(m => !visibleButtons.Contains(m.Button))
                .Concat(sourceItems.Select(i => i.Source)).ToList();
            _config.Update(cfg =>
            {
                if (cfg.Devices.Contains(selectedDev))
                {
                    selectedDev.Mappings = merged;
                }
            });
        }
    }
}

public partial class IntegrationsViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private DriverReadinessStatus _driverStatus;
    private CableOutputReadiness _cableOutputStatus = CableOutputReadiness.Unknown;

    public TargetInputTool SelectedTool => _config.Current.InputTool.Tool;

    public string CurrentToolDisplayName => SelectedTool switch
    {
        TargetInputTool.WeChatInput => "微信输入法",
        TargetInputTool.DoubaoInput => "豆包输入法 / 客户端",
        TargetInputTool.Chatterfly => "腾讯 Chatterfly",
        TargetInputTool.Typeless => "Typeless 智能转写",
        TargetInputTool.WindowsDictation => "Windows 语音听写 (Win+H)",
        TargetInputTool.Custom => "其他语音工具",
        _ => "其他语音工具"
    };

    public string CurrentShortcutDisplay => string.IsNullOrWhiteSpace(_config.Current.InputTool.Shortcut) ? "未配置" : _config.Current.InputTool.Shortcut;

    public string CurrentTriggerModeDisplay => _config.Current.InputTool.TriggerMode == VoiceTriggerMode.HoldToTalk ? "按住说话" : "单击切换";

    public string VirtualCableStatusDescription => _driverStatus switch
    {
        DriverReadinessStatus.Ready when _cableOutputStatus == CableOutputReadiness.Disabled =>
            "CABLE Input 已就绪，但录音端 CABLE Output 已被禁用，请在 Windows 声音设置中启用",
        DriverReadinessStatus.Ready when _cableOutputStatus == CableOutputReadiness.Missing =>
            "CABLE Input 已就绪，但未检测到录音端 CABLE Output，请检查系统录音设备或重新安装 VB-CABLE",
        DriverReadinessStatus.Ready => "CABLE Input (VB-Audio Virtual Cable) 驱动已就绪",
        DriverReadinessStatus.NotInstalled => "未检测到 VB-CABLE 驱动，请前往【环境与驱动】安装",
        DriverReadinessStatus.Disabled => "CABLE Input 已被系统禁用，请在 Windows 声音设置中启用",
        DriverReadinessStatus.RebootRequired => "VB-CABLE 需要重启系统完成初始化",
        _ => "虚拟音频设备未就绪"
    };

    public string VirtualCableStatusBrush => _driverStatus switch
    {
        DriverReadinessStatus.Ready when _cableOutputStatus is CableOutputReadiness.Disabled or CableOutputReadiness.Missing => "#FFB347",
        DriverReadinessStatus.Ready => "#00CC66",
        DriverReadinessStatus.Disabled or DriverReadinessStatus.RebootRequired => "#FFB347",
        _ => "#FF6666"
    };

    private readonly Action<WizardMode>? _openWizard;

    internal Action<string> OpenUrlAction { get; set; } = AppLinks.OpenExternalUrl;

    public IntegrationsViewModel(ConfigService config, Action<WizardMode>? openWizard = null)
    {
        _config = config;
        _openWizard = openWizard;
        Reload();
    }

    [RelayCommand]
    public void RunWizard() => _openWizard?.Invoke(WizardMode.Integration);

    [RelayCommand]
    public void OpenFaq() => OpenUrlAction(AppLinks.InputMethodsFaqUrl);

    public void Reload()
    {
        _driverStatus = VirtualAudioAuditor.AuditCableInputDevice();
        _cableOutputStatus = VirtualAudioAuditor.GetCableOutputStatus();
        OnPropertyChanged(nameof(SelectedTool));
        OnPropertyChanged(nameof(CurrentToolDisplayName));
        OnPropertyChanged(nameof(CurrentShortcutDisplay));
        OnPropertyChanged(nameof(CurrentTriggerModeDisplay));
        OnPropertyChanged(nameof(VirtualCableStatusDescription));
        OnPropertyChanged(nameof(VirtualCableStatusBrush));
    }
}

public partial class EnvironmentViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeColor))]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeBgColor))]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeBorderColor))]
    [NotifyPropertyChangedFor(nameof(CanInstallCable))]
    [NotifyCanExecuteChangedFor(nameof(InstallCableCommand))]
    private DriverReadinessStatus _driverStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeColor))]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeBgColor))]
    [NotifyPropertyChangedFor(nameof(DriverStatusBadgeBorderColor))]
    private CableOutputReadiness _cableOutputStatus = CableOutputReadiness.Unknown;

    [ObservableProperty]
    private string _cableInstallMessage = string.Empty;

    public bool CanInstallCable => DriverStatus == DriverReadinessStatus.NotInstalled;

    [RelayCommand(CanExecute = nameof(CanInstallCable))]
    private async Task InstallCableAsync()
    {
        CableInstallMessage = await VbCableInstaller.InstallAsync(new Progress<string>(message => CableInstallMessage = message));
        RefreshStatus();
    }

    public string DriverStatusBadgeText => DriverStatus switch
    {
        DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Disabled => "录音端已禁用",
        DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Missing => "录音端缺失",
        DriverReadinessStatus.Ready => "已就绪",
        DriverReadinessStatus.NotInstalled => "未安装",
        DriverReadinessStatus.Disabled => "已禁用",
        DriverReadinessStatus.RebootRequired => "需重启",
        _ => "初始化失败"
    };

    public string DriverStatusBadgeColor => DriverStatus switch
    {
        DriverReadinessStatus.Ready when CableOutputStatus is CableOutputReadiness.Disabled or CableOutputReadiness.Missing => "#FFB347",
        DriverReadinessStatus.Ready => "#44FF88",
        DriverReadinessStatus.Disabled or DriverReadinessStatus.RebootRequired => "#FFB347",
        _ => "#FF6666"
    };

    public string DriverStatusBadgeBgColor => DriverStatus switch
    {
        DriverReadinessStatus.Ready when CableOutputStatus is CableOutputReadiness.Disabled or CableOutputReadiness.Missing => "#3D2E18",
        DriverReadinessStatus.Ready => "#1C3827",
        DriverReadinessStatus.Disabled or DriverReadinessStatus.RebootRequired => "#3D2E18",
        _ => "#3D1818"
    };

    public string DriverStatusBadgeBorderColor => DriverStatus switch
    {
        DriverReadinessStatus.Ready when CableOutputStatus is CableOutputReadiness.Disabled or CableOutputReadiness.Missing => "#8A5A1A",
        DriverReadinessStatus.Ready => "#2D6A42",
        DriverReadinessStatus.Disabled or DriverReadinessStatus.RebootRequired => "#8A5A1A",
        _ => "#8A2D2D"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EtwStatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(EtwStatusBadgeColor))]
    [NotifyPropertyChangedFor(nameof(EtwStatusBadgeBgColor))]
    [NotifyPropertyChangedFor(nameof(EtwStatusBadgeBorderColor))]
    [NotifyPropertyChangedFor(nameof(EtwStatusDescription))]
    [NotifyPropertyChangedFor(nameof(CanRetryEtwCapture))]
    private EtwCaptureStatus _etwStatus = EtwCaptureStatus.Idle;

    public string EtwStatusBadgeText => EtwStatus switch
    {
        EtwCaptureStatus.RunningInProcess => "运行中 (内置特权)",
        EtwCaptureStatus.RunningElevatedWorker => "运行中 (提权进程)",
        EtwCaptureStatus.Starting => "正在启动",
        EtwCaptureStatus.ElevationDeclined => "已拒绝提权",
        EtwCaptureStatus.Disconnected => "已意外断开",
        EtwCaptureStatus.Failed => "启动失败",
        _ => "待机"
    };

    public string EtwStatusBadgeColor => EtwStatus switch
    {
        EtwCaptureStatus.RunningInProcess or EtwCaptureStatus.RunningElevatedWorker => "#44FF88",
        EtwCaptureStatus.Starting => "#88CCFF",
        EtwCaptureStatus.ElevationDeclined or EtwCaptureStatus.Disconnected => "#FFB347",
        EtwCaptureStatus.Failed => "#FF6666",
        _ => "#AAB1BE"
    };

    public string EtwStatusBadgeBgColor => EtwStatus switch
    {
        EtwCaptureStatus.RunningInProcess or EtwCaptureStatus.RunningElevatedWorker => "#1C3827",
        EtwCaptureStatus.Starting => "#1E2836",
        EtwCaptureStatus.ElevationDeclined or EtwCaptureStatus.Disconnected => "#3D2E18",
        EtwCaptureStatus.Failed => "#3D1818",
        _ => "#25272B"
    };

    public string EtwStatusBadgeBorderColor => EtwStatus switch
    {
        EtwCaptureStatus.RunningInProcess or EtwCaptureStatus.RunningElevatedWorker => "#2D6A42",
        EtwCaptureStatus.Starting => "#385373",
        EtwCaptureStatus.ElevationDeclined or EtwCaptureStatus.Disconnected => "#8A5A1A",
        EtwCaptureStatus.Failed => "#8A2D2D",
        _ => "#3E4249"
    };

    public string EtwStatusDescription => EtwStatus switch
    {
        EtwCaptureStatus.RunningInProcess => "当前进程已具有管理员权限，Chromecast / T1 按键补充 ETW 捕获服务正在内置运行。",
        EtwCaptureStatus.RunningElevatedWorker => "已通过提权工作进程运行 Chromecast / T1 按键补充 ETW 捕获服务。",
        EtwCaptureStatus.Starting => "正在启动 ETW 提权捕获工作进程，如弹出 UAC 权限确认请点击“是”。",
        EtwCaptureStatus.ElevationDeclined => "您取消或拒绝了 UAC 提权请求，Chromecast (G9N9N/GY3LE) 与 T1 部分专用按键可能无法被捕获。",
        EtwCaptureStatus.Disconnected => "ETW 提权工作进程意外断开，请点击下方按钮重新启动捕获服务。",
        EtwCaptureStatus.Failed => "ETW 捕获服务启动失败，请重试启动或尝试以管理员身份重启本程序。",
        _ => "尚未连接需要 ETW 补充捕获的遥控器，或服务当前处于待机状态。"
    };

    public bool CanRetryEtwCapture => EtwStatus is EtwCaptureStatus.ElevationDeclined or EtwCaptureStatus.Disconnected or EtwCaptureStatus.Failed;

    internal Action? RetryEtwCaptureAction { get; set; }

    public void UpdateEtwStatus(EtwCaptureStatus status)
    {
        EtwStatus = status;
    }

    [RelayCommand]
    public void RetryEtwCapture()
    {
        RetryEtwCaptureAction?.Invoke();
    }

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
        if (BluetoothAdapterHelper.RestartAppAsAdministrator())
        {
            if (System.Windows.Application.Current?.MainWindow is Views.MainWindow mw)
            {
                mw.IsExplicitExit = true;
            }
            System.Windows.Application.Current?.Shutdown();
        }
    }

    [RelayCommand]
    public void RefreshStatus()
    {
        DriverStatus = VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true);
        CableOutputStatus = VirtualAudioAuditor.GetCableOutputStatus();
        StatusDescription = DriverStatus switch
        {
            DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Disabled =>
                "CABLE Input 已就绪，但录音端 CABLE Output 已被禁用。请在 Windows 声音设置中启用“CABLE Output”。",
            DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Missing =>
                "CABLE Input 已就绪，但未检测到录音端 CABLE Output。请检查 Windows 声音设置或重新安装 VB-CABLE。",
            DriverReadinessStatus.Ready => "VB-CABLE 虚拟麦克风已就绪，音频服务工作正常。",
            DriverReadinessStatus.NotInstalled => "未检测到 VB-CABLE。可在这里下载并安装。",
            DriverReadinessStatus.Disabled => "检测到 CABLE Input 虚拟扬声器设备已被禁用。请打开 Windows 声音设置启用 “CABLE Input”。",
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

    private bool _launchAtStartup;
    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (SetProperty(ref _launchAtStartup, value))
            {
                StartupService.SetLaunchAtStartup(value);
                _config.Update(cfg => cfg.LaunchAtStartup = value);
                PreferencesChanged?.Invoke();
            }
        }
    }

    private bool _minimizeToTrayOnClose;
    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set
        {
            if (SetProperty(ref _minimizeToTrayOnClose, value))
            {
                _config.Update(cfg => cfg.MinimizeToTrayOnClose = value);
                PreferencesChanged?.Invoke();
            }
        }
    }

    private bool _diagnosticLoggingEnabled;
    public bool DiagnosticLoggingEnabled
    {
        get => _diagnosticLoggingEnabled;
        set
        {
            if (SetProperty(ref _diagnosticLoggingEnabled, value))
            {
                AppLogger.SetEnabled(value);
                _config.Update(cfg => cfg.DiagnosticLoggingEnabled = value);
                PreferencesChanged?.Invoke();
            }
        }
    }

    [ObservableProperty]
    private string _appVersion;

    [ObservableProperty]
    private string _logDirectoryPath;

    public SettingsViewModel(ConfigService config, Action<WizardMode>? openWizard = null)
    {
        _config = config;
        _openWizard = openWizard;
        _launchAtStartup = StartupService.IsLaunchAtStartupEnabled();
        _minimizeToTrayOnClose = _config.Current.MinimizeToTrayOnClose;
        _diagnosticLoggingEnabled = _config.Current.DiagnosticLoggingEnabled;
        LogDirectoryPath = AppLogger.LogDirectory;
        AppVersion = AboutViewModel.ResolveAppVersion();
    }

    public SettingsViewModel(ConfigService config, Action openOnboarding)
        : this(config, mode => openOnboarding())
    {
    }

    public event Action? PreferencesChanged;

    internal void SyncFromConfig()
    {
        bool launch = StartupService.IsLaunchAtStartupEnabled();
        SetProperty(ref _launchAtStartup, launch, nameof(LaunchAtStartup));
        SetProperty(ref _minimizeToTrayOnClose, _config.Current.MinimizeToTrayOnClose, nameof(MinimizeToTrayOnClose));
        SetProperty(ref _diagnosticLoggingEnabled, _config.Current.DiagnosticLoggingEnabled, nameof(DiagnosticLoggingEnabled));
    }

    [RelayCommand]
    public void RerunWizard()
    {
        _openWizard?.Invoke(WizardMode.Full);
    }

    [RelayCommand]
    public void OpenLogFolder()
    {
        OpenLogFolderWithPrompt();
    }

    internal static void OpenLogFolderWithPrompt()
    {
        if (!AppLogger.OpenLogFolder() && System.Windows.Application.Current != null)
        {
            System.Windows.MessageBox.Show(
                "当前已关闭排错日志，尚未生成日志文件。\n如需记录，请先勾选“启用排错日志”。",
                "暂无日志文件",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
    }
}

public partial class GeneralViewModel : ObservableObject, IDisposable
{
    private readonly ConfigService _config;
    private readonly IBluetoothManager _bt;
    private readonly Action<WizardMode> _openWizard;
    private readonly Action<string> _navigate;
    private readonly Action<ulong, bool, string> _connectionChangedHandler;

    private bool _launchAtStartup;
    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (SetProperty(ref _launchAtStartup, value))
            {
                StartupService.SetLaunchAtStartup(value);
                _config.Update(cfg => cfg.LaunchAtStartup = value);
                PreferencesChanged?.Invoke();
            }
        }
    }

    private bool _minimizeToTrayOnClose;
    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set
        {
            if (SetProperty(ref _minimizeToTrayOnClose, value))
            {
                _config.Update(cfg => cfg.MinimizeToTrayOnClose = value);
                PreferencesChanged?.Invoke();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionsReady))]
    [NotifyPropertyChangedFor(nameof(PermissionsStatusText))]
    [NotifyPropertyChangedFor(nameof(PermissionsStatusBrush))]
    [NotifyPropertyChangedFor(nameof(PermissionsBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(PermissionsBorderBrush))]
    [NotifyPropertyChangedFor(nameof(PermissionsIcon))]
    [NotifyPropertyChangedFor(nameof(PermissionsSummary))]
    private DriverReadinessStatus _driverStatus = VirtualAudioAuditor.GetCachedStatus();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionsReady))]
    [NotifyPropertyChangedFor(nameof(PermissionsStatusText))]
    [NotifyPropertyChangedFor(nameof(PermissionsStatusBrush))]
    [NotifyPropertyChangedFor(nameof(PermissionsBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(PermissionsBorderBrush))]
    [NotifyPropertyChangedFor(nameof(PermissionsIcon))]
    [NotifyPropertyChangedFor(nameof(PermissionsSummary))]
    private CableOutputReadiness _cableOutputStatus = VirtualAudioAuditor.GetCableOutputStatus();

    public bool PermissionsReady => DriverStatus == DriverReadinessStatus.Ready &&
        CableOutputStatus is CableOutputReadiness.Active or CableOutputReadiness.Unknown;
    public string PermissionsStatusText => DriverStatus switch
    {
        DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Disabled => "录音端已禁用",
        DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Missing => "录音端缺失",
        DriverReadinessStatus.Ready => "驱动就绪",
        DriverReadinessStatus.Disabled => "设备已禁用",
        DriverReadinessStatus.RebootRequired => "需重启",
        _ => "缺少驱动"
    };
    public string PermissionsStatusBrush => PermissionsReady ? "#44FF88" : "#FFAA33";
    public string PermissionsBadgeBackground => PermissionsReady ? "#1C3827" : "#332200";
    public string PermissionsBorderBrush => PermissionsReady ? "#2D6A42" : "#664400";
    public string PermissionsIcon => PermissionsReady ? "✓" : "⚠";
    public string PermissionsSummary => DriverStatus switch
    {
        DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Disabled =>
            "CABLE Input 已就绪，但录音端 CABLE Output 已被禁用，请在 Windows 声音设置中启用",
        DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Missing =>
            "CABLE Input 已就绪，但未检测到录音端 CABLE Output，请检查系统录音设备或重新安装 VB-CABLE",
        DriverReadinessStatus.Ready => "VB-CABLE 虚拟麦克风已就绪，音频服务工作正常",
        DriverReadinessStatus.Disabled => "CABLE Input 音频设备已被禁用，请在 Windows 声音设置中启用",
        DriverReadinessStatus.RebootRequired => "VB-CABLE 安装尚未完成，请重启系统完成初始化",
        _ => "未检测到 VB-CABLE 虚拟音频驱动，请进入检查并安装"
    };

    public bool RemoteReady => _bt.IsConnected;
    public string RemoteStatusText => RemoteReady ? "已连接" : "未连接";
    public string RemoteStatusBrush => RemoteReady ? "#44FF88" : "#FFAA33";
    public string RemoteBadgeBackground => RemoteReady ? "#1C3827" : "#332200";
    public string RemoteBorderBrush => RemoteReady ? "#2D6A42" : "#664400";
    public string RemoteIcon => RemoteReady ? "✓" : "⚠";
    public string RemoteSummary
    {
        get
        {
            if (!_bt.IsConnected) return "未连接遥控器";
            int count = _bt.ConnectedDeviceAddresses.Count;
            if (count > 1) return $"已连接 {count} 台遥控器，语音服务就绪";
            return !string.IsNullOrEmpty(_bt.ConnectedDeviceName) ? $"已连接 {_bt.ConnectedDeviceName}，语音服务就绪" : "已连接蓝牙遥控器";
        }
    }

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
                TargetInputTool.Chatterfly => "腾讯 Chatterfly",
                TargetInputTool.Typeless => "Typeless",
                TargetInputTool.WindowsDictation => "Windows 听写",
                TargetInputTool.Custom => "其他语音工具",
                _ => "其他语音工具"
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

        _launchAtStartup = StartupService.IsLaunchAtStartupEnabled();
        _minimizeToTrayOnClose = _config.Current.MinimizeToTrayOnClose;

        _connectionChangedHandler = (_, _, _) =>
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
        _bt.ConnectionStatusChanged += _connectionChangedHandler;
    }

    public GeneralViewModel(ConfigService config, IBluetoothManager bt, Action openOnboarding, Action<string> navigate)
        : this(config, bt, mode => openOnboarding(), navigate)
    {
    }

    public event Action? PreferencesChanged;

    public void RefreshState()
    {
        SyncFromConfig();

        DriverStatus = VirtualAudioAuditor.GetCachedStatus();
        CableOutputStatus = VirtualAudioAuditor.GetCableOutputStatus();
        _ = Task.Run(() =>
        {
            var status = VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true);
            var outputStatus = VirtualAudioAuditor.GetCableOutputStatus();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    DriverStatus = status;
                    CableOutputStatus = outputStatus;
                }));
            }
            else
            {
                DriverStatus = status;
                CableOutputStatus = outputStatus;
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

    internal void SyncFromConfig()
    {
        bool launch = StartupService.IsLaunchAtStartupEnabled();
        SetProperty(ref _launchAtStartup, launch, nameof(LaunchAtStartup));
        SetProperty(ref _minimizeToTrayOnClose, _config.Current.MinimizeToTrayOnClose, nameof(MinimizeToTrayOnClose));
    }

    public void Dispose()
    {
        _bt.ConnectionStatusChanged -= _connectionChangedHandler;
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
    internal event Action? SettingsChanged;

    internal Action<string> OpenUrlAction { get; set; } = AppLinks.OpenExternalUrl;

    public AppIntegrationViewModel(ConfigService config)
    {
        _config = config;
        LoadIntegrations();
    }

    [RelayCommand]
    public void OpenFaq() => OpenUrlAction(AppLinks.ApplicationIntegrationFaqUrl);

    public void LoadIntegrations()
    {
        Applications.Clear();
        var map = _config.Current.AppIntegrations ?? new();

        var presets = new (string Id, string Name, string Detail)[]
        {
            ("wechat", "微信 (WeChat)", "打开会话后，尝试聚焦消息输入框"),
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
        _config.Update(cfg =>
        {
            cfg.AppIntegrations ??= new();
            foreach (var app in Applications)
            {
                cfg.AppIntegrations[app.Id] = app.IsEnabled;
            }
        });
        SettingsChanged?.Invoke();
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
    private readonly ConfigService? _config;
    private readonly Func<WindowsDiagnosticSnapshot>? _diagnosticSnapshotProvider;
    private readonly Action<string> _clipboardWriter;

    private bool _diagnosticLoggingEnabled;
    public bool DiagnosticLoggingEnabled
    {
        get => _diagnosticLoggingEnabled;
        set
        {
            if (SetProperty(ref _diagnosticLoggingEnabled, value))
            {
                AppLogger.SetEnabled(value);
                _config?.Update(cfg => cfg.DiagnosticLoggingEnabled = value);
                PreferencesChanged?.Invoke();
            }
        }
    }

    [ObservableProperty]
    private string _appVersion;

    [ObservableProperty]
    private string _architecture = Environment.Is64BitProcess ? "x64 (64位)" : "x86 (32位)";

    [ObservableProperty]
    private string _logDirectoryPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticCopyStatus))]
    private string _diagnosticCopyStatus = string.Empty;

    [ObservableProperty]
    private string _diagnosticCopyStatusColor = "#44FF88";

    public bool HasDiagnosticCopyStatus => !string.IsNullOrEmpty(DiagnosticCopyStatus);

    public AboutViewModel() : this(null)
    {
    }

    public AboutViewModel(ConfigService? config)
        : this(config, WindowsRelease.FetchAsync, null, null)
    {
    }

    internal AboutViewModel(
        ConfigService? config,
        Func<CancellationToken, Task<WindowsRelease>> fetchRelease,
        Func<WindowsDiagnosticSnapshot>? diagnosticSnapshotProvider = null,
        Action<string>? clipboardWriter = null)
    {
        _config = config;
        _fetchRelease = fetchRelease;
        _diagnosticSnapshotProvider = diagnosticSnapshotProvider;
        _clipboardWriter = clipboardWriter ?? WriteTextToClipboard;
        _receiveUpdateNotifications = _config?.Current.ReceiveUpdateNotifications ?? true;
        _diagnosticLoggingEnabled = _config?.Current.DiagnosticLoggingEnabled ?? AppLogger.IsEnabled;
        LogDirectoryPath = AppLogger.LogDirectory;
        AppVersion = ResolveAppVersion();
    }

    public event Action? PreferencesChanged;

    internal void SyncFromConfig()
    {
        if (_config != null)
        {
            SetProperty(ref _diagnosticLoggingEnabled, _config.Current.DiagnosticLoggingEnabled, nameof(DiagnosticLoggingEnabled));
        }
    }

    internal static string ResolveAppVersion()
    {
        var infoVer = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrEmpty(infoVer))
        {
            string version = infoVer.Split('+')[0];
            return WindowsRelease.ParseVersion(version) is { } releaseVersion
                ? WindowsRelease.FormatTag(releaseVersion)
                : "v" + version;
        }

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        return ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";
    }

    [RelayCommand]
    public void OpenLogFolder()
    {
        SettingsViewModel.OpenLogFolderWithPrompt();
    }

    [RelayCommand]
    public void CopyDiagnosticSummary()
    {
        try
        {
            var snapshot = _diagnosticSnapshotProvider?.Invoke() ?? CreateFallbackDiagnosticSnapshot();
            string payload = snapshot.BuildClipboardText();
            _clipboardWriter(payload);
            AppLogger.Info(snapshot.BuildAuditLog(), "AboutViewModel");
            DiagnosticCopyStatusColor = "#44FF88";
            DiagnosticCopyStatus = "✓ 已复制诊断信息到剪贴板，可直接粘贴发送反馈。";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to copy diagnostic summary to clipboard: {ex.Message}", "AboutViewModel");
            DiagnosticCopyStatusColor = "#FF6666";
            DiagnosticCopyStatus = $"复制诊断信息失败：{ex.Message}";
        }
    }

    private WindowsDiagnosticSnapshot CreateFallbackDiagnosticSnapshot()
    {
        string logPath = AppLogger.CurrentLogFilePath;
        bool logExists = System.IO.File.Exists(logPath);
        long? logSize = logExists ? new System.IO.FileInfo(logPath).Length : null;
        var input = _config?.Current.InputTool;
        return new WindowsDiagnosticSnapshot(
            AppVersion: AppVersion,
            OsVersion: Environment.OSVersion.VersionString,
            Architecture: Architecture,
            IsAdministrator: BluetoothAdapterHelper.IsAdministrator(),
            RemoteConnected: false,
            ConnectedRemoteCount: 0,
            RemoteModel: "none",
            VirtualMicrophoneStatus: "unknown",
            CableOutputStatus: "unknown",
            EtwCaptureStatus: "unknown",
            VoiceTool: input?.Tool.ToString() ?? "unselected",
            VoiceTriggerMode: input?.TriggerMode.ToString() ?? "unknown",
            VoiceShortcutName: string.IsNullOrWhiteSpace(input?.Shortcut) ? "unknown" : input.Shortcut,
            PreferredAudioInput: string.IsNullOrWhiteSpace(input?.PreferredAudioInputDevice) ? "default" : input.PreferredAudioInputDevice,
            RuntimeLogEnabled: DiagnosticLoggingEnabled,
            RuntimeLogAvailable: logExists,
            RuntimeLogPath: logPath,
            RuntimeLogSizeBytes: logSize);
    }

    private static void WriteTextToClipboard(string text)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Thread.Sleep(20);
            }
        }
        throw lastError ?? new InvalidOperationException("无法访问系统剪贴板");
    }

    [RelayCommand]
    public void OpenWebsite() => AppLinks.OpenExternalUrl(AppLinks.WebsiteUrl);

    [RelayCommand]
    public void OpenGitHub() => AppLinks.OpenExternalUrl(AppLinks.GitHubUrl);

    [RelayCommand]
    public void OpenUrl(string? url) => AppLinks.OpenExternalUrl(url);
}
