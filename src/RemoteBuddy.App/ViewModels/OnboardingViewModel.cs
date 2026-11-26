// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;
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

public sealed record VoiceShortcutOption(string Label, string Value, bool IsCustom = false);

public partial class OnboardingViewModel : ObservableObject, IDisposable
{
    public const string WeChatVoiceSetup = "在微信输入法设置的「语音输入」页打开「快捷键全局生效」，让语音快捷键在使用其他输入法时也生效。开启「按住说话」，让快捷键与 Remote Buddy 一致，并将麦克风选为 CABLE Output (VB-Audio Virtual Cable)。在 Remote Buddy 中选择「按住说话」。";

    public const string DoubaoVoiceSetup = "在豆包输入法设置的「语音输入」页将「全局唤起语音」设为「持续模式」，让「长按模式」的快捷键与 Remote Buddy 一致，并将麦克风选为 CABLE Output (VB-Audio Virtual Cable)。以管理员身份运行 Remote Buddy，并选择「按住说话」。首次使用时会准备已签名的 USB/IP 虚拟键盘驱动。";

    public const string ChatterflyVoiceSetup = "安装并启动腾讯 Chatterfly，在「设置 → 语音设置」中将语音输入快捷键设为与 Remote Buddy 一致并开启「快捷键全局可用」，在「音频设置 → 麦克风」中选择 CABLE Output (VB-Audio Virtual Cable)。以管理员身份运行 Remote Buddy，并选择「按住说话」。首次使用时会通过已签名的 USB/IP 虚拟键盘发送快捷键。";

    public const string CustomVoiceSetup = "不限定具体语音输入工具：先在上方设定触发方式（按住说话 / 单击切换）与全局快捷键，确认目标工具的麦克风已设为 CABLE Output (VB-Audio Virtual Cable)，并以管理员身份运行 Remote Buddy；下一步将通过 USB/IP 虚拟键盘实测唤起工具并将语音转写文字写入检测框。";

    public static IReadOnlyList<VoiceShortcutOption> VoiceShortcutOptions { get; } =
    [
        new("右 Alt (RAlt) — 豆包输入法默认", "RAlt"),
        new("Ctrl+F5 — 微信输入法默认", "Ctrl+F5"),
        new("Win+H — Windows 语音听写", "Win+H"),
        new("Ctrl+Alt+V — Typeless 默认", "Ctrl+Alt+V"),
        new("Ctrl+Win — 常见输入法快捷键", "Ctrl+Win"),
        new("右 Ctrl (RCtrl)", "RCtrl"),
        new("右 Shift (RShift)", "RShift"),
        new("左 Alt (LAlt)", "LAlt"),
        new("F1", "F1"),
        new("F2", "F2"),
        new("Alt+Space", "Alt+Space"),
        new("Ctrl+Shift+V", "Ctrl+Shift+V"),
        new("自定义快捷键...", string.Empty, true)
    ];

    private readonly ConfigService _config;
    private readonly IBluetoothManager _bt;

    private readonly WizardStep[] _activeSteps;

    private readonly Action<RemoteDevice> _onDeviceDiscovered;
    private readonly Action<ulong, bool, string> _onConnectionChanged;
    private readonly Action<ulong, float> _onAudioLevelChanged;
    private readonly Action<ulong> _onVoiceSessionEnded;
    private readonly Action<ulong> _onVoiceSessionStarted;

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
        WizardMode.Integration => $"已成功配置「{GetToolDisplayName(SelectedTool)}」语音输入法（快捷键 {ShortcutText}）。语音键已设为{(IsHoldToTalk ? "按住说话" : "单击切换")}。",
        _ => "所有音频链路与输入法快捷键已就绪。在任意应用程序或文档中，拿起遥控器按住说话即可畅享语音输入。"
    };

    private static string GetToolDisplayName(TargetInputTool tool) => tool switch
    {
        TargetInputTool.WeChatInput => "微信输入法",
        TargetInputTool.DoubaoInput => "豆包输入法 / 客户端",
        TargetInputTool.Chatterfly => "腾讯 Chatterfly",
        TargetInputTool.Typeless => "Typeless 智能转写",
        TargetInputTool.WindowsDictation => "Windows 语音听写",
        TargetInputTool.Custom => "其他语音工具",
        _ => "其他语音工具"
    };

    [ObservableProperty]
    private int _currentStep = 1;

    [ObservableProperty]
    private int _totalSteps = 8;

    [ObservableProperty]
    private string _stepTitle = "欢迎使用 Remote Buddy";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriverStatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanInstallCable))]
    [NotifyCanExecuteChangedFor(nameof(InstallCableCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestCableLoopbackCommand))]
    private DriverReadinessStatus _driverStatus = DriverReadinessStatus.NotInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriverStatusMessage))]
    [NotifyCanExecuteChangedFor(nameof(TestCableLoopbackCommand))]
    private CableOutputReadiness _cableOutputStatus = CableOutputReadiness.Unknown;

    [ObservableProperty]
    private string _cableInstallMessage = string.Empty;

    [ObservableProperty]
    private string _cableLoopbackMessage = string.Empty;

    public bool CanInstallCable => DriverStatus == DriverReadinessStatus.NotInstalled;

    public string DriverStatusMessage => DriverStatus switch
    {
        DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Disabled =>
            "⚠️ 检测到 CABLE Input 已就绪，但录音端 CABLE Output 已被禁用，请在 Windows 声音设置中启用后再继续。",
        DriverReadinessStatus.Ready when CableOutputStatus == CableOutputReadiness.Missing =>
            "⚠️ 检测到 CABLE Input 已就绪，但未检测到录音端 CABLE Output，请检查系统录音设备或重新安装 VB-CABLE。",
        DriverReadinessStatus.Ready => "✅ VB-CABLE 虚拟麦克风已就绪，音频服务工作正常。",
        DriverReadinessStatus.NotInstalled => "未检测到 VB-CABLE。可在这里下载并安装。",
        DriverReadinessStatus.Disabled => "⚠️ 检测到 CABLE Input 音频设备已被禁用，请在 Windows 声音设置中启用后再继续。",
        DriverReadinessStatus.RebootRequired => "⚠️ 检测到虚拟声卡安装未完全生效，请重启电脑完成系统音频服务初始化。",
        _ => "❌ 虚拟麦克风初始化失败，请在设备管理器中检查设备状态。"
    };

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
    [NotifyPropertyChangedFor(nameof(InputToolSetupInstructions))]
    [NotifyPropertyChangedFor(nameof(ShowShortcutEditor))]
    [NotifyPropertyChangedFor(nameof(TranscriptionInstructions))]
    private TargetInputTool _selectedTool;

    public string InputToolSetupInstructions => SelectedTool switch
    {
        TargetInputTool.WeChatInput => WeChatVoiceSetup,
        TargetInputTool.DoubaoInput => DoubaoVoiceSetup,
        TargetInputTool.Chatterfly => ChatterflyVoiceSetup,
        TargetInputTool.Typeless => "在 Typeless 中启用全局听写快捷键并保持与 Remote Buddy 一致，麦克风选为 CABLE Output (VB-Audio Virtual Cable)。默认快捷键 Ctrl+Alt+V。",
        TargetInputTool.WindowsDictation => "请将 CABLE Output 设为 Windows 默认麦克风，并选择「单击切换」配合快捷键 Win+H。",
        TargetInputTool.Custom => CustomVoiceSetup,
        _ => "请在目标输入工具中将麦克风选为 CABLE Output。"
    };

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

    public string TranscriptionInstructions
    {
        get
        {
            if (SelectedTool == TargetInputTool.Custom)
            {
                string shortcut = string.IsNullOrWhiteSpace(ShortcutText) ? "已配置的快捷键" : ShortcutText;
                return IsHoldToTalk
                    ? $"请先确认第三方语音工具已在后台就绪并监听快捷键（{shortcut}）。点击下方检测输入框，按住遥控器语音键说话，松开后确认识别文本已写入输入框。"
                    : $"请先确认第三方语音工具已在后台就绪并监听快捷键（{shortcut}）。点击下方检测输入框，按一下遥控器语音键开始说话、再按一下结束，确认识别文本已写入输入框。";
            }

            return IsHoldToTalk
                ? "点击下方输入框，按住语音键说一句话，松开后核对转写结果。"
                : "点击下方输入框，按一下语音键开始说话，再按一下结束，然后核对转写结果。";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptionInstructions))]
    private string _shortcutText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomShortcutSelected))]
    [NotifyPropertyChangedFor(nameof(ShowShortcutEditor))]
    private VoiceShortcutOption? _selectedShortcutOption;

    public bool IsCustomShortcutSelected => SelectedShortcutOption?.IsCustom == true;

    public bool ShowShortcutEditor => IsCustomShortcutSelected || SelectedTool == TargetInputTool.Custom;

    private bool _syncingShortcutSelection;

    partial void OnSelectedShortcutOptionChanged(VoiceShortcutOption? value)
    {
        if (_syncingShortcutSelection) return;

        if (value is { IsCustom: false })
        {
            _syncingShortcutSelection = true;
            try
            {
                ShortcutText = value.Value;
                ValidationError = string.Empty;
            }
            finally
            {
                _syncingShortcutSelection = false;
            }
        }
        else
        {
            ValidationError = string.Empty;
        }
    }

    private void SyncShortcutOptionFromText(string? shortcut)
    {
        _syncingShortcutSelection = true;
        try
        {
            string normalized = shortcut?.Trim() ?? string.Empty;
            var match = VoiceShortcutOptions.FirstOrDefault(opt =>
                !opt.IsCustom &&
                string.Equals(opt.Value, normalized, StringComparison.OrdinalIgnoreCase));
            SelectedShortcutOption = match ?? VoiceShortcutOptions.First(opt => opt.IsCustom);
        }
        finally
        {
            _syncingShortcutSelection = false;
        }
    }

    [ObservableProperty]
    private string _transcriptionTestText = string.Empty;

    [ObservableProperty]
    private bool _isTranscriptionVerified = false;

    [ObservableProperty]
    private string _validationError = string.Empty;

    private bool _hasVoiceTranscriptionSessionOccurred = false;
    private bool _hasTranscriptionTextChangedThisRound = false;
    private bool _loggedVerificationAudio;
    private string? _lastShortcutDispatchError;

    internal void RecordVoiceShortcutDispatch(bool succeeded, string? failureReason = null)
    {
        if (CurrentWizardStep != WizardStep.TranscriptionVerify) return;

        AppLogger.Info($"Transcription verification shortcut: addr=0x{SelectedDevice?.BluetoothAddress ?? 0:X12}, dispatched={succeeded}", "OnboardingViewModel");
        if (succeeded)
        {
            _lastShortcutDispatchError = null;
            ValidationError = string.Empty;
        }
        else
        {
            _hasVoiceTranscriptionSessionOccurred = false;
            _lastShortcutDispatchError = string.IsNullOrWhiteSpace(failureReason)
                ? "语音快捷键发送失败，请检查管理员权限与 USB/IP 虚拟键盘驱动状态后重试。"
                : failureReason;
            ValidationError = _lastShortcutDispatchError;
        }
    }

    internal InputToolConfig? ActiveVerificationInputTool =>
        CurrentWizardStep == WizardStep.TranscriptionVerify
            ? new InputToolConfig
            {
                Tool = SelectedTool,
                TriggerMode = TriggerMode,
                Shortcut = ShortcutText
            }
            : null;

    partial void OnTranscriptionTestTextChanged(string value)
    {
        if (CurrentWizardStep != WizardStep.TranscriptionVerify) return;
        bool hasText = !string.IsNullOrWhiteSpace(value);
        if (_hasTranscriptionTextChangedThisRound != hasText)
            AppLogger.Info($"Transcription verification field: hasText={hasText}", "OnboardingViewModel");
        _hasTranscriptionTextChangedThisRound = hasText;
    }

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
            SyncShortcutOptionFromText(ShortcutText);
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
            SyncShortcutOptionFromText(ShortcutText);
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

        _onConnectionChanged = (addr, connected, name) =>
        {
            if (SelectedDevice is { BluetoothAddress: not 0 } selected && addr != 0 && addr != selected.BluetoothAddress) return;
            void Update()
            {
                bool targetReady = SelectedDevice is { BluetoothAddress: not 0 } device
                    ? (_bt.IsDeviceReady(device.BluetoothAddress) || (connected && (addr == 0 || addr == device.BluetoothAddress)))
                    : connected;
                IsDeviceConnected = targetReady;
                DeviceConnectionStatus = targetReady ? $"语音服务已就绪：{name}" : $"语音服务未就绪：{name}";
                if (!targetReady)
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

        _onAudioLevelChanged = (addr, level) =>
        {
            if (SelectedDevice is { BluetoothAddress: not 0 } selected && addr != 0 && addr != selected.BluetoothAddress) return;
            void Update()
            {
                LiveAudioLevel = level;
                if (CurrentWizardStep == WizardStep.AudioTest && level > 0.05f)
                {
                    HasAudioTested = true;
                }
                if (CurrentWizardStep == WizardStep.TranscriptionVerify && level > 0.05f && !_loggedVerificationAudio)
                {
                    _loggedVerificationAudio = true;
                    AppLogger.Info($"Transcription verification received audio signal: addr=0x{addr:X12}", "OnboardingViewModel");
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

        _onVoiceSessionEnded = (addr) =>
        {
            if (SelectedDevice is { BluetoothAddress: not 0 } selected && addr != 0 && addr != selected.BluetoothAddress) return;
            void Update()
            {
                LiveAudioLevel = 0f;
                _loggedVerificationAudio = false;
                if (CurrentWizardStep == WizardStep.TranscriptionVerify &&
                    string.IsNullOrEmpty(_lastShortcutDispatchError))
                {
                    _hasVoiceTranscriptionSessionOccurred = true;
                    AppLogger.Info($"Transcription verification voice session ended: addr=0x{addr:X12}", "OnboardingViewModel");
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

        _onVoiceSessionStarted = (addr) =>
        {
            if (SelectedDevice is { BluetoothAddress: not 0 } selected && addr != 0 && addr != selected.BluetoothAddress) return;
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
    private int _connectAttemptId;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        TestCableLoopbackCommand.Cancel();

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
            if (SelectedDevice is { BluetoothAddress: not 0 } target &&
                _config.Current.Devices.All(d => d.BluetoothAddress != target.BluetoothAddress))
            {
                _bt.RemoveDevice(target.BluetoothAddress);
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
        CableOutputStatus = VirtualAudioAuditor.GetCableOutputStatus();
        OnPropertyChanged(nameof(DriverStatusMessage));
    }

    private bool CanTestCableLoopback() =>
        DriverStatus == DriverReadinessStatus.Ready && CableOutputStatus == CableOutputReadiness.Active;

    [RelayCommand(CanExecute = nameof(CanTestCableLoopback))]
    private async Task TestCableLoopbackAsync(CancellationToken cancellationToken)
    {
        CableLoopbackMessage = "正在通过 VB-CABLE 播放并检测测试音…";
        try
        {
            var result = await VbCableLoopbackProbe.RunAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CableLoopbackMessage = result switch
            {
                CableLoopbackResult.Passed => "环回通过，CABLE Output 收到了测试音。如果输入工具仍无声，请确认它选用的麦克风是 CABLE Output。",
                CableLoopbackResult.NoSignal => "CABLE Output 未检测到测试音。请检查 Windows 音量混音器中 Remote Buddy 和 VB-CABLE 的静音及音量设置。",
                CableLoopbackResult.EndpointUnavailable => "未找到可用的 CABLE Input 和 CABLE Output。请在 Windows 声音设置中启用这两个设备并重新检测。",
                CableLoopbackResult.VoiceBusy => "正在使用遥控器语音。请结束语音后再测。",
                _ => "测试音播放或录音失败。请确认 VB-CABLE 可用，详细信息见应用日志。"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    [RelayCommand(CanExecute = nameof(CanInstallCable))]
    private async Task InstallCableAsync()
    {
        CableInstallMessage = await VbCableInstaller.InstallAsync(new Progress<string>(message => CableInstallMessage = message));
        RefreshDriverStatus();
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
        int attemptId = Interlocked.Increment(ref _connectAttemptId);
        ValidationError = string.Empty;
        IsDeviceConnected = false;
        DeviceConnectionStatus = $"正在连接 {device.Name} 并协商语音服务，请稍候……";
        AppLogger.Info($"Wizard connect requested: '{device.Name}' (0x{device.BluetoothAddress:X12})", "OnboardingViewModel");
        SelectedDevice = device;
        try
        {
            _bt.SetDeviceVoiceSettings(device);
            bool ok = await _bt.ConnectAsync(device.BluetoothAddress);
            if (attemptId != Volatile.Read(ref _connectAttemptId) || _isDisposed) return;

            if (ok)
            {
                device.IsConnected = true;
                IsDeviceConnected = true;
                device.ContainerId = _bt.GetDeviceContainerId(device.BluetoothAddress);
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
        catch (Exception ex)
        {
            if (attemptId != Volatile.Read(ref _connectAttemptId) || _isDisposed) return;
            AppLogger.Error($"Wizard connection exception for '{device.Name}' (0x{device.BluetoothAddress:X12})", ex, "OnboardingViewModel");
            DeviceConnectionStatus = $"与 {device.Name} 连接异常。";
            ValidationError = $"连接 {device.Name} 时出现异常：{ex.Message}";
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
        if (CurrentWizardStep != WizardStep.DriverCheck && TestCableLoopbackCommand.IsRunning)
        {
            TestCableLoopbackCommand.Cancel();
            CableLoopbackMessage = string.Empty;
        }
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
        if (CurrentWizardStep == WizardStep.TranscriptionVerify)
        {
            AppLogger.Info($"Transcription verification entered: mode={Mode}, addr=0x{SelectedDevice?.BluetoothAddress ?? 0:X12}, tool={SelectedTool}, trigger={TriggerMode}", "OnboardingViewModel");
            TranscriptionTestText = string.Empty;
            _hasVoiceTranscriptionSessionOccurred = false;
            _hasTranscriptionTextChangedThisRound = false;
            _loggedVerificationAudio = false;
            _lastShortcutDispatchError = null;
            IsTranscriptionVerified = false;
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
                TargetInputTool.DoubaoInput => "RAlt",
                TargetInputTool.Chatterfly => "Ctrl+Shift+V",
                TargetInputTool.Typeless => "Ctrl+Alt+V",
                TargetInputTool.WindowsDictation => "Win+H",
                TargetInputTool.Custom => "Ctrl+Shift+V",
                _ => "Ctrl+Shift+V"
            };
            SyncShortcutOptionFromText(ShortcutText);
            TriggerMode = (tool == TargetInputTool.WindowsDictation)
                ? VoiceTriggerMode.ClickToToggle
                : VoiceTriggerMode.HoldToTalk;
            ValidationError = string.Empty;
        }
    }

    public bool ValidateToolConfig()
    {
        if (!NativeInputSender.TryParseShortcut(ShortcutText, out _, out _))
        {
            ValidationError = "请输入或录制有效的语音快捷键（如 RAlt、Ctrl+F5、Win+H）；快捷键不能为空。";
            return false;
        }
        ValidationError = string.Empty;
        return true;
    }

    public bool SaveToolConfig()
    {
        if (!ValidateToolConfig()) return false;
        _config.Update(cfg =>
        {
            cfg.InputTool.Tool = SelectedTool;
            cfg.InputTool.TriggerMode = TriggerMode;
            cfg.InputTool.Shortcut = ShortcutText;
        });
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
                    ValidationError = DriverStatus == DriverReadinessStatus.Disabled
                        ? "CABLE Input 音频设备当前处于禁用状态，请在 Windows 声音设置中启用。"
                        : "请先安装 VB-CABLE 并确保音频端点就绪（如需重启请先重启电脑）。";
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
                if (!ValidateToolConfig()) return;
                break;

            case WizardStep.TranscriptionVerify:
                AppLogger.Info($"Transcription verification submitted: shortcutError={_lastShortcutDispatchError != null}, sessionEnded={_hasVoiceTranscriptionSessionOccurred}, hasText={_hasTranscriptionTextChangedThisRound}, confirmed={IsTranscriptionVerified}", "OnboardingViewModel");
                if (!string.IsNullOrEmpty(_lastShortcutDispatchError))
                {
                    ValidationError = _lastShortcutDispatchError;
                    return;
                }
                if (!_hasVoiceTranscriptionSessionOccurred)
                {
                    ValidationError = "请使用遥控器语音键在下方输入框中完成一次实际语音转写。";
                    return;
                }
                if (string.IsNullOrWhiteSpace(TranscriptionTestText) || !_hasTranscriptionTextChangedThisRound)
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
            _config.Update(cfg => cfg.HasCompletedOnboarding = true, save: SelectedDevice == null);
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

            SelectedDevice = _config.UpsertDevice(SelectedDevice, select: true, save: true);
            AppLogger.Info($"Wizard completed: device '{SelectedDevice.Name}' (0x{SelectedDevice.BluetoothAddress:X12}) added to configuration", "OnboardingViewModel");
        }
        else if (Mode != WizardMode.Full)
        {
            _config.Save();
        }
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
