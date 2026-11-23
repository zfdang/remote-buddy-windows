// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;
using RemoteBuddy.Platform.Windows.Audio;
using RemoteBuddy.Platform.Windows.Bluetooth;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.ViewModels;

public enum WizardMode
{
    Full,
    DeviceOnly,
    Integration
}

public enum WizardStep
{
    Welcome,
    DriverCheck,
    DeviceConnect,
    ButtonTest,
    AudioTest,
    ImeConfig,
    TranscriptionVerify,
    Complete
}

public partial class OnboardingViewModel : ObservableObject, IDisposable
{
    private readonly ConfigService _config;
    private readonly IBluetoothManager _bt;

    private readonly WizardStep[] _activeSteps;

    private readonly Action<RemoteDevice> _onDeviceDiscovered;
    private readonly Action<bool, string> _onConnectionChanged;
    private readonly Action<float> _onAudioLevelChanged;
    private readonly Action _onVoiceSessionEnded;
    private readonly Action _onVoiceSessionStarted;

    public WizardMode Mode { get; }

    public WizardStep CurrentWizardStep => _activeSteps[Math.Clamp(CurrentStep - 1, 0, _activeSteps.Length - 1)];

    public string WindowTitle => Mode switch
    {
        WizardMode.DeviceOnly => "添加遥控器向导 - Remote Buddy",
        WizardMode.Integration => "输入法集成向导 - Remote Buddy",
        _ => "设置向导 - Remote Buddy"
    };

    public string StepProgressText => $"第 {CurrentStep} / {TotalSteps} 步";

    public bool IsCompleted { get; private set; } = false;

    public string CompleteSummaryTitle => Mode switch
    {
        WizardMode.DeviceOnly => "🎉 遥控器添加完成！",
        WizardMode.Integration => "🎉 输入法集成配置完成！",
        _ => "🎉 配置全部完成！"
    };

    public string CompleteSummaryDescription => Mode switch
    {
        WizardMode.DeviceOnly => SelectedDevice != null
            ? $"遥控器「{SelectedDevice.Name}」已成功连接并完成测试。点击“完成”即可保存至您的遥控器列表并开始使用。"
            : "遥控器已成功连接并完成测试。点击“完成”即可保存至您的遥控器列表并开始使用。",
        WizardMode.Integration => $"已成功配置「{SelectedTool}」语音输入法（快捷键 {ShortcutText}）。语音键已设为{(IsHoldToTalk ? "按住说话" : "单击切换")}。",
        _ => "所有音频链路与输入法快捷键已就绪。在任意应用程序或文档中，拿起遥控器按住说话即可畅享语音输入。"
    };

    [ObservableProperty]
    private int _currentStep = 1;

    [ObservableProperty]
    private int _totalSteps = 8;

    [ObservableProperty]
    private string _stepTitle = "欢迎使用 Remote Buddy";

    [ObservableProperty]
    private DriverReadinessStatus _driverStatus;

    partial void OnDriverStatusChanged(DriverReadinessStatus value)
    {
        DriverStatusMessage = value switch
        {
            DriverReadinessStatus.Ready => "✅ VB-CABLE 虚拟麦克风已就绪，音频服务工作正常。",
            DriverReadinessStatus.NotInstalled => "❌ 未检测到 VB-CABLE。请点击下方按钮前往官网下载安装。",
            DriverReadinessStatus.RebootRequired => "⚠️ 检测到虚拟声卡安装未完全生效，请重启电脑完成系统音频服务初始化。",
            _ => "❌ 虚拟麦克风初始化失败，请在设备管理器中检查设备状态。"
        };
    }

    [ObservableProperty]
    private string _driverStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isScanning = false;

    [ObservableProperty]
    private ObservableCollection<RemoteDevice> _discoveredDevices = new();

    [ObservableProperty]
    private RemoteDevice? _selectedDevice;

    [ObservableProperty]
    private bool _isDeviceConnected = false;

    [ObservableProperty]
    private string _deviceConnectionStatus = "尚未连接语音服务。Windows 显示已配对或已连接后，仍需点击下方设备的“连接”。";

    [ObservableProperty]
    private string _lastPressedButtonName = "尚未检测到按键";

    [ObservableProperty]
    private bool _hasButtonTested = false;

    [ObservableProperty]
    private string _truncationWarningMessage = string.Empty;

    public Visibility TruncationWarningVisibility => string.IsNullOrEmpty(TruncationWarningMessage) ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestartBluetooth))]
    private bool _isRestartingBluetooth = false;

    public bool CanRestartBluetooth => !IsRestartingBluetooth;

    [ObservableProperty]
    private string _restartBluetoothStatusText = string.Empty;

    public Visibility RestartBluetoothStatusVisibility => string.IsNullOrEmpty(RestartBluetoothStatusText) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsTestingChromecast => SelectedDevice is { } device &&
        (device.IsG9N9N || G9N9NProtocol.IsCandidate(device.Name));

    public void NotifyTruncatedPacketDetected()
    {
        if (CurrentWizardStep != WizardStep.ButtonTest || !IsDeviceConnected ||
            !IsTestingChromecast || TruncationWarningMessage.Length > 0) return;

        LastPressedButtonName = "检测到普通按键活动";
        HasButtonTested = true;
        ValidationError = string.Empty;
        TruncationWarningMessage = "已确认选中的遥控器发来了普通按键报文。Windows 未提供键码，暂时无法显示键名或执行该键的映射。如果刚启用蓝牙详细日志，请重启 Windows，重新连接遥控器后再测试。";
        OnPropertyChanged(nameof(TruncationWarningVisibility));
    }

    [RelayCommand]
    public async Task RestartBluetoothAdapterAsync()
    {
        if (IsRestartingBluetooth) return;

        IsRestartingBluetooth = true;
        RestartBluetoothStatusText = "正在重启系统蓝牙适配器，请稍候...";
        OnPropertyChanged(nameof(RestartBluetoothStatusVisibility));

        try
        {
            var (success, msg) = await BluetoothAdapterHelper.RestartBluetoothAdapterAsync();
            RestartBluetoothStatusText = msg;
            OnPropertyChanged(nameof(RestartBluetoothStatusVisibility));
        }
        catch (Exception ex)
        {
            RestartBluetoothStatusText = $"重启发生异常: {ex.Message}";
            OnPropertyChanged(nameof(RestartBluetoothStatusVisibility));
        }
        finally
        {
            IsRestartingBluetooth = false;
        }
    }

    [RelayCommand]
    public void OpenDeviceManager() => BluetoothAdapterHelper.OpenDeviceManager();

    [ObservableProperty]
    private float _liveAudioLevel = 0f;

    [ObservableProperty]
    private bool _hasAudioTested = false;

    [ObservableProperty]
    private TargetInputTool _selectedTool;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHoldToTalk))]
    [NotifyPropertyChangedFor(nameof(IsClickToToggle))]
    [NotifyPropertyChangedFor(nameof(TranscriptionInstructions))]
    private VoiceTriggerMode _triggerMode;

    public bool IsHoldToTalk
    {
        get => TriggerMode == VoiceTriggerMode.HoldToTalk;
        set { if (value) TriggerMode = VoiceTriggerMode.HoldToTalk; }
    }

    public bool IsClickToToggle
    {
        get => TriggerMode == VoiceTriggerMode.ClickToToggle;
        set { if (value) TriggerMode = VoiceTriggerMode.ClickToToggle; }
    }

    public string TranscriptionInstructions => IsHoldToTalk
        ? "点击下方输入框，按住语音键说一句话，松开后核对转写结果。"
        : "点击下方输入框，按一下语音键开始说话，再按一下结束，然后核对转写结果。";

    [ObservableProperty]
    private string _shortcutText = string.Empty;

    [ObservableProperty]
    private string _transcriptionTestText = string.Empty;

    [ObservableProperty]
    private bool _isTranscriptionVerified = false;

    [ObservableProperty]
    private string _validationError = string.Empty;

    private bool _hasVoiceTranscriptionSessionOccurred = false;

    public event Action? RequestClose;

    public OnboardingViewModel(ConfigService config, IBluetoothManager bt, WizardMode mode = WizardMode.Full)
    {
        _config = config;
        _bt = bt;
        Mode = mode;

        if (Mode == WizardMode.DeviceOnly)
        {
            _activeSteps =
            [
                WizardStep.DeviceConnect,
                WizardStep.ButtonTest,
                WizardStep.AudioTest,
                WizardStep.Complete
            ];
            DeviceConnectionStatus = "点击“扫描设备”查找遥控器，然后点击“连接”。";
        }
        else if (Mode == WizardMode.Integration)
        {
            _activeSteps =
            [
                WizardStep.ImeConfig,
                WizardStep.TranscriptionVerify,
                WizardStep.Complete
            ];
            SelectedTool = _config.Current.InputTool.Tool;
            TriggerMode = _config.Current.InputTool.TriggerMode;
            ShortcutText = _config.Current.InputTool.Shortcut;
        }
        else
        {
            _activeSteps =
            [
                WizardStep.Welcome,
                WizardStep.DriverCheck,
                WizardStep.DeviceConnect,
                WizardStep.ButtonTest,
                WizardStep.AudioTest,
                WizardStep.ImeConfig,
                WizardStep.TranscriptionVerify,
                WizardStep.Complete
            ];
            SelectedTool = _config.Current.InputTool.Tool;
            TriggerMode = _config.Current.InputTool.TriggerMode;
            ShortcutText = _config.Current.InputTool.Shortcut;
            IsDeviceConnected = _bt.IsReady;
            SelectedDevice = _config.Current.Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId);
            foreach (var device in _config.Current.Devices)
                DiscoveredDevices.Add(device);
            if (IsDeviceConnected) DeviceConnectionStatus = "语音服务已就绪，可以继续。";
        }

        TotalSteps = _activeSteps.Length;

        _onDeviceDiscovered = (dev) =>
        {
            App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (!DiscoveredDevices.Any(d => d.BluetoothAddress == dev.BluetoothAddress))
                {
                    DiscoveredDevices.Add(dev);
                }
            }));
        };

        _onConnectionChanged = (connected, name) =>
        {
            void Update()
            {
                IsDeviceConnected = connected;
                DeviceConnectionStatus = connected ? $"语音服务已就绪：{name}" : $"语音服务未就绪：{name}";
                if (!connected)
                {
                    HasButtonTested = false;
                    HasAudioTested = false;
                    LiveAudioLevel = 0f;
                    _hasVoiceTranscriptionSessionOccurred = false;
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

        _onAudioLevelChanged = (level) =>
        {
            void Update()
            {
                LiveAudioLevel = level;
                if (CurrentWizardStep == WizardStep.AudioTest && level > 0.05f)
                {
                    HasAudioTested = true;
                }
            }

            if (App.Current?.Dispatcher != null && !App.Current.Dispatcher.CheckAccess())
            {
                App.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, Update);
            }
            else
            {
                Update();
            }
        };

        _onVoiceSessionEnded = () =>
        {
            void Update()
            {
                LiveAudioLevel = 0f;
                if (CurrentWizardStep == WizardStep.TranscriptionVerify)
                {
                    _hasVoiceTranscriptionSessionOccurred = true;
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

        _onVoiceSessionStarted = () =>
        {
            void Update()
            {
                if (CurrentWizardStep == WizardStep.ButtonTest)
                {
                    NotifyButtonPressed(RemoteButton.Voice);
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
        _bt.ConnectionStatusChanged += _onConnectionChanged;
        _bt.AudioLevelChanged += _onAudioLevelChanged;
        _bt.VoiceSessionEnded += _onVoiceSessionEnded;
        _bt.VoiceSessionStarted += _onVoiceSessionStarted;

        RefreshDriverStatus();
        UpdateStepMetadata();
    }

    private bool _isDisposed = false;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (IsScanning)
        {
            _bt.StopScanning();
            IsScanning = false;
        }
        _bt.DeviceDiscovered -= _onDeviceDiscovered;
        _bt.ConnectionStatusChanged -= _onConnectionChanged;
        _bt.AudioLevelChanged -= _onAudioLevelChanged;
        _bt.VoiceSessionEnded -= _onVoiceSessionEnded;
        _bt.VoiceSessionStarted -= _onVoiceSessionStarted;

        if (!IsCompleted && Mode == WizardMode.DeviceOnly)
        {
            AppLogger.Info("AddDevice wizard closed without completion: discarding unverified device connection", "OnboardingViewModel");
            if (_bt.IsConnected)
            {
                _bt.Disconnect();
            }
            if (!string.IsNullOrEmpty(_config.Current.SelectedDeviceId))
            {
                var prevDev = _config.Current.Devices.FirstOrDefault(d => d.Id == _config.Current.SelectedDeviceId);
                if (prevDev != null && prevDev.BluetoothAddress != 0)
                {
                    _ = _bt.ConnectAsync(prevDev.BluetoothAddress);
                }
            }
        }
    }

    [RelayCommand]
    public void RefreshDriverStatus()
    {
        DriverStatus = VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true);
    }

    [RelayCommand]
    public void OpenOfficialWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://vb-audio.com/Cable/",
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    public void ToggleScan()
    {
        IsScanning = !IsScanning;
        if (IsScanning)
        {
            _bt.StartScanning();
            _ = _bt.DiscoverPairedRemotesAsync();
        }
        else
        {
            _bt.StopScanning();
        }
    }

    [RelayCommand]
    public async Task ConnectDeviceAsync(RemoteDevice? device)
    {
        if (device == null) return;
        ValidationError = string.Empty;
        IsDeviceConnected = false;
        DeviceConnectionStatus = $"正在连接 {device.Name} 并协商语音服务，请稍候……";
        AppLogger.Info($"Wizard connect requested: '{device.Name}' (0x{device.BluetoothAddress:X12})", "OnboardingViewModel");
        SelectedDevice = device;
        bool ok = await _bt.ConnectAsync(device.BluetoothAddress);
        if (ok)
        {
            device.IsConnected = true;
            IsDeviceConnected = true;
            device.ContainerId = _bt.ConnectedDeviceContainerId;
            if (device.Mappings == null || device.Mappings.Count == 0)
            {
                device.Mappings = RemoteDevice.CreateDefaultMappings(device.ModelType);
            }
            DeviceConnectionStatus = $"语音服务已就绪：{device.Name}";
        }
        else
        {
            DeviceConnectionStatus = $"与 {device.Name} 连接失败。";
            ValidationError = $"无法与 {device.Name} 建立语音服务。请确保遥控器处于配对状态、电量充足且未被其他设备独占连接。";
            AppLogger.Warn($"Wizard connection failed: {ValidationError}", "OnboardingViewModel");
        }
    }

    public void NotifyButtonPressed(RemoteButton button)
    {
        AppLogger.Info($"OnboardingViewModel: NotifyButtonPressed: button={button}, step={CurrentWizardStep}", "OnboardingViewModel");
        if (CurrentWizardStep != WizardStep.ButtonTest || button == RemoteButton.None) return;
        if (IsTestingChromecast && !IsDeviceConnected) return;

        LastPressedButtonName = $"已按下: {GetButtonFriendlyName(button)}";
        HasButtonTested = true;
        ValidationError = string.Empty;

        if (IsTestingChromecast && button == RemoteButton.Voice) return;

        TruncationWarningMessage = string.Empty;
        RestartBluetoothStatusText = string.Empty;
        OnPropertyChanged(nameof(TruncationWarningVisibility));
        OnPropertyChanged(nameof(RestartBluetoothStatusVisibility));
    }

    private static string GetButtonFriendlyName(RemoteButton button) => button switch
    {
        RemoteButton.Up => "方向上键",
        RemoteButton.Down => "方向下键",
        RemoteButton.Left => "方向左键",
        RemoteButton.Right => "方向右键",
        RemoteButton.Ok => "确认键 (OK)",
        RemoteButton.Back => "返回键",
        RemoteButton.Home => "主页键 (Home)",
        RemoteButton.Voice => "语音键",
        RemoteButton.VolumeUp => "音量加 (+)",
        RemoteButton.VolumeDown => "音量减 (-)",
        RemoteButton.Mute => "静音键",
        RemoteButton.Power => "电源键",
        RemoteButton.LiveTv => "电视直播键",
        RemoteButton.Menu => "菜单键",
        RemoteButton.YouTube => "YouTube 键",
        RemoteButton.Netflix => "Netflix 键",
        RemoteButton.Input => "输入源切换键 (Input)",
        _ => button.ToString()
    };

    partial void OnCurrentStepChanged(int value)
    {
        if (CurrentWizardStep == WizardStep.ButtonTest)
        {
            HasButtonTested = false;
            LastPressedButtonName = "尚未检测到按键";
            TruncationWarningMessage = string.Empty;
            RestartBluetoothStatusText = string.Empty;
            OnPropertyChanged(nameof(TruncationWarningVisibility));
            OnPropertyChanged(nameof(RestartBluetoothStatusVisibility));
        }
        if (CurrentWizardStep == WizardStep.AudioTest)
        {
            HasAudioTested = false;
        }
        UpdateStepMetadata();
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
            ValidationError = string.Empty;
        }
    }

    public bool SaveToolConfig()
    {
        if (!NativeInputSender.TryParseShortcut(ShortcutText, out _, out _))
        {
            ValidationError = "请输入有效的语音快捷键，例如 Ctrl+Win；快捷键不能为空。";
            return false;
        }
        ValidationError = string.Empty;
        _config.Current.InputTool.Tool = SelectedTool;
        _config.Current.InputTool.TriggerMode = TriggerMode;
        _config.Current.InputTool.Shortcut = ShortcutText;
        _config.Save();
        return true;
    }

    public string NextButtonText => CurrentStep == TotalSteps ? "完成" : "下一步";
    public bool CanGoPrevious => CurrentStep > 1;

    [RelayCommand]
    public void NextStep()
    {
        ValidationError = string.Empty;
        if (CurrentWizardStep == WizardStep.DeviceConnect && IsScanning)
        {
            _bt.StopScanning();
            IsScanning = false;
        }

        // Validation gates for each step
        switch (CurrentWizardStep)
        {
            case WizardStep.DriverCheck:
                if (DriverStatus != DriverReadinessStatus.Ready)
                {
                    RefreshDriverStatus();
                }
                if (DriverStatus != DriverReadinessStatus.Ready)
                {
                    ValidationError = "请先安装 VB-CABLE 并确保音频端点就绪（如需重启请先重启电脑）。";
                    return;
                }
                break;

            case WizardStep.DeviceConnect:
                if (!IsDeviceConnected)
                {
                    ValidationError = "请先点击设备的“连接”，等待语音服务就绪。Windows 蓝牙连接状态不能代替应用内语音服务连接。";
                    AppLogger.Warn($"DeviceConnect blocked: IsDeviceConnected={IsDeviceConnected}, ManagerReady={_bt.IsReady}, Status={DeviceConnectionStatus}", "OnboardingViewModel");
                    return;
                }
                break;

            case WizardStep.ButtonTest:
                if (!HasButtonTested)
                {
                    ValidationError = "请在遥控器上按一个方向键、OK 或语音键，确认收到响应。";
                    return;
                }
                break;

            case WizardStep.AudioTest:
                if (!HasAudioTested)
                {
                    ValidationError = "请按住遥控器语音键说话，确保电平条产生跳动反馈。";
                    return;
                }
                break;

            case WizardStep.ImeConfig:
                if (!SaveToolConfig()) return;
                break;

            case WizardStep.TranscriptionVerify:
                if (!_hasVoiceTranscriptionSessionOccurred)
                {
                    ValidationError = "请使用遥控器语音键在下方输入框中完成一次实际语音转写。";
                    return;
                }
                if (string.IsNullOrWhiteSpace(TranscriptionTestText))
                {
                    ValidationError = "转写文本框内容不能为空，请先通过遥控器转写文字。";
                    return;
                }
                if (!IsTranscriptionVerified)
                {
                    ValidationError = "请勾选确认输入法转写结果正确无误。";
                    return;
                }
                break;
        }

        if (CurrentStep < TotalSteps)
        {
            AppLogger.Info($"Wizard progressing from Step {CurrentStep} to {CurrentStep + 1} ({CurrentWizardStep})", "OnboardingViewModel");
            CurrentStep++;
            UpdateStepMetadata();
        }
        else
        {
            Complete();
        }
    }

    [RelayCommand]
    public void PreviousStep()
    {
        ValidationError = string.Empty;
        if (CurrentStep > 1)
        {
            CurrentStep--;
            UpdateStepMetadata();
        }
    }

    [RelayCommand]
    public void Complete()
    {
        if (Mode == WizardMode.Full)
        {
            if (DriverStatus != DriverReadinessStatus.Ready)
            {
                RefreshDriverStatus();
            }
            if (DriverStatus != DriverReadinessStatus.Ready)
            {
                ValidationError = "虚拟麦克风驱动未就绪，无法完成设置。";
                return;
            }
            if (!IsDeviceConnected)
            {
                ValidationError = "遥控器未连接，无法完成设置。";
                return;
            }
            if (!SaveToolConfig()) return;
            _config.Current.HasCompletedOnboarding = true;
        }
        else if (Mode == WizardMode.DeviceOnly)
        {
            if (!IsDeviceConnected)
            {
                ValidationError = "遥控器未连接，无法完成添加。";
                return;
            }
        }
        else if (Mode == WizardMode.Integration)
        {
            if (!SaveToolConfig()) return;
        }

        if (SelectedDevice != null)
        {
            if (string.IsNullOrEmpty(SelectedDevice.HidDevicePath) && SelectedDevice.BluetoothAddress != 0)
            {
                var info = HidDeviceReader.FindBestDevice(SelectedDevice.BluetoothAddress);
                if (info != null)
                {
                    SelectedDevice.HidDevicePath = info.Path;
                    SelectedDevice.HidUsagePage = info.UsagePage;
                    SelectedDevice.HidUsage = info.Usage;
                    SelectedDevice.HidReportLength = info.InputReportByteLength;
                    SelectedDevice.HidCaptureStatus = "已就绪 (RawInput & Direct Stream)";
                }
            }

            if (!_config.Current.Devices.Any(d => d.Id == SelectedDevice.Id))
            {
                _config.Current.Devices.Add(SelectedDevice);
            }
            _config.Current.SelectedDeviceId = SelectedDevice.Id;
            AppLogger.Info($"Wizard completed: device '{SelectedDevice.Name}' (0x{SelectedDevice.BluetoothAddress:X12}) added to configuration", "OnboardingViewModel");
        }
        _config.Save();
        IsCompleted = true;
        AppLogger.Info($"Wizard completed (Mode={Mode}) and configuration saved", "OnboardingViewModel");
        RequestClose?.Invoke();
    }

    private void UpdateStepMetadata()
    {
        StepTitle = Mode switch
        {
            WizardMode.Full => CurrentWizardStep switch
            {
                WizardStep.Welcome => "第 1 步：欢迎与界面语言",
                WizardStep.DriverCheck => "第 2 步：虚拟麦克风驱动检查 (VB-CABLE)",
                WizardStep.DeviceConnect => "第 3 步：选择并连接遥控器",
                WizardStep.ButtonTest => "第 4 步：遥控器按键响应测试",
                WizardStep.AudioTest => "第 5 步：麦克风拾音与动态波形测试",
                WizardStep.ImeConfig => "第 6 步：配置目标语音输入工具",
                WizardStep.TranscriptionVerify => "第 7 步：真实输入法转写验收",
                WizardStep.Complete => "第 8 步：设置完成与使用说明",
                _ => "首次设置向导"
            },
            WizardMode.Integration => CurrentWizardStep switch
            {
                WizardStep.ImeConfig => "第 1 步：配置目标语音输入工具",
                WizardStep.TranscriptionVerify => "第 2 步：真实输入法转写验收",
                WizardStep.Complete => "第 3 步：输入法集成配置完成",
                _ => "输入法集成向导"
            },
            _ => CurrentWizardStep switch
            {
                WizardStep.DeviceConnect => "第 1 步：选择并连接遥控器",
                WizardStep.ButtonTest => "第 2 步：遥控器按键响应测试",
                WizardStep.AudioTest => "第 3 步：麦克风拾音与动态波形测试",
                WizardStep.Complete => "第 4 步：遥控器添加完成",
                _ => "添加遥控器向导"
            }
        };

        OnPropertyChanged(nameof(CurrentWizardStep));
        OnPropertyChanged(nameof(StepProgressText));
        OnPropertyChanged(nameof(CompleteSummaryTitle));
        OnPropertyChanged(nameof(CompleteSummaryDescription));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(CanGoPrevious));
    }
}
