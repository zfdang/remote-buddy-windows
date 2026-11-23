// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;
using RemoteBuddy.Platform.Windows.Bluetooth;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private record struct ActiveVoiceSession(VoiceTriggerMode TriggerMode, string Shortcut, TargetInputTool Tool);

    private readonly ConfigService _configService;
    private readonly IBluetoothManager _bluetoothManager;
    private readonly RawInputReceiver _rawInputReceiver = new();
    private readonly HidDeviceReader _hidDeviceReader = new();
    private readonly ChromecastButtonReceiver _chromecastButtonReceiver = new();
    private readonly RemoteVoiceKeyGuard _voiceKeyGuard;
    private readonly Dictionary<IntPtr, (Guid? Target, bool Accepted)> _deviceAdmissionLog = new();
    private ActiveVoiceSession? _activeVoiceSession;

    private readonly RemoteButtonGestures _buttonGestures;

    [ObservableProperty]
    private string _currentTitle = "Remote Buddy (Windows)";

    [ObservableProperty]
    private string _activePageName = "General";

    [ObservableProperty]
    private bool _isConnected = false;

    [ObservableProperty]
    private string _connectedDeviceName = "未连接设备";

    [ObservableProperty]
    private float _currentAudioLevel = 0f;

    [ObservableProperty]
    private bool _isVoiceActive = false;

    public GeneralViewModel GeneralVm { get; }
    public DevicesViewModel DevicesVm { get; }
    public MappingViewModel MappingVm { get; }
    public IntegrationsViewModel IntegrationsVm { get; }
    public EnvironmentViewModel EnvironmentVm { get; }
    public AppIntegrationViewModel AppIntegrationVm { get; }
    public AboutViewModel AboutVm { get; }
    public SettingsViewModel SettingsVm { get; }

    private OnboardingViewModel? _activeOnboardingVm;
    public OnboardingViewModel? ActiveOnboardingVm
    {
        get => _activeOnboardingVm;
        set
        {
            _activeOnboardingVm = value;
            _buttonGestures.Reset();
            if (value != null && IsConnected)
            {
                bool needsEtw = G9N9NProtocol.IsCandidate(ConnectedDeviceName) ||
                    XiaomiProtocol.IsCandidate(ConnectedDeviceName) ||
                    T1RemoteProtocol.IsCandidate(ConnectedDeviceName) ||
                    (value.SelectedDevice != null && (value.SelectedDevice.IsG9N9N || value.SelectedDevice.IsXiaomi || value.SelectedDevice.IsT1));
                if (needsEtw && !_chromecastButtonReceiver.IsRunning)
                {
                    AppLogger.Info("Activating ETW button receiver for active onboarding session", "MainViewModel");
                    StartChromecastButtons();
                }
            }
        }
    }

    private readonly object _buttonDedupLock = new();
    private RemoteButton _lastDedupButton = RemoteButton.None;
    private bool _lastDedupIsDown = false;
    private long _lastDedupTimeMs = 0;

    public event Action<WizardMode>? RequestOpenWizard;

    public MainViewModel(ConfigService configService, IBluetoothManager bluetoothManager)
    {
        _configService = configService;
        _bluetoothManager = bluetoothManager;
        _buttonGestures = new RemoteButtonGestures(ScheduleButtonGesture, ExecuteButtonAction);
        _voiceKeyGuard = new RemoteVoiceKeyGuard(() =>
            _bluetoothManager.SessionState is BluetoothSessionState.Opening or BluetoothSessionState.Streaming or BluetoothSessionState.Draining);
        _rawInputReceiver.PhysicalVoiceKeyPressed += _voiceKeyGuard.Arm;

        GeneralVm = new GeneralViewModel(_configService, _bluetoothManager, mode => RequestOpenWizard?.Invoke(mode), Navigate);
        DevicesVm = new DevicesViewModel(_configService, _bluetoothManager, mode => RequestOpenWizard?.Invoke(mode));
        MappingVm = new MappingViewModel(_configService);
        MappingVm.PropertyChanged += OnMappingSettingsChanged;
        IntegrationsVm = new IntegrationsViewModel(_configService, mode => RequestOpenWizard?.Invoke(mode));
        EnvironmentVm = new EnvironmentViewModel();
        AppIntegrationVm = new AppIntegrationViewModel(_configService);
        AboutVm = new AboutViewModel();
        SettingsVm = new SettingsViewModel(_configService, mode => RequestOpenWizard?.Invoke(mode));
        UpdateVoiceKeyGuard();

        _rawInputReceiver.DeviceFilter = IsTargetDevice;

        _bluetoothManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        _bluetoothManager.AudioLevelChanged += OnAudioLevelChanged;
        _bluetoothManager.VoiceSessionStarted += OnVoiceSessionStarted;
        _bluetoothManager.VoiceSessionEnded += OnVoiceSessionEnded;
        _bluetoothManager.VoiceButtonPressed += OnVoiceButtonPressed;
        _rawInputReceiver.ButtonEvent += OnRawButtonEvent;
        _hidDeviceReader.ButtonEvent += OnHidButtonEvent;
        _chromecastButtonReceiver.ButtonEvent += OnChromecastButtonEvent;
        _chromecastButtonReceiver.TruncatedPacketDetected += OnChromecastTruncatedPacketDetected;
    }

    private void StartChromecastButtons()
    {
        ulong addr = _bluetoothManager.ConnectedDeviceAddress;
        if (addr == 0 && ActiveOnboardingVm?.SelectedDevice != null)
        {
            addr = ActiveOnboardingVm.SelectedDevice.BluetoothAddress;
        }
        if (addr == 0) return;

        var name = ConnectedDeviceName;
        var dev = _configService.Current.Devices.FirstOrDefault(d =>
            (addr != 0 && d.BluetoothAddress == addr) ||
            d.Id == _configService.Current.SelectedDeviceId);

        var protocolKind = (XiaomiProtocol.IsCandidate(name) || (dev != null && dev.IsXiaomi) || (ActiveOnboardingVm?.SelectedDevice != null && ActiveOnboardingVm.SelectedDevice.IsXiaomi))
            ? EtwProtocolKind.Xiaomi
            : ((T1RemoteProtocol.IsCandidate(name) || (dev != null && dev.IsT1) || (ActiveOnboardingVm?.SelectedDevice != null && ActiveOnboardingVm.SelectedDevice.IsT1))
                ? EtwProtocolKind.T1Remote
                : EtwProtocolKind.GoogleG9N9N);

        var marker = Guid.NewGuid();
        if (_chromecastButtonReceiver.IsRunning)
        {
            _chromecastButtonReceiver.ResetBinding(marker, protocolKind);
            _ = _bluetoothManager.ProbeButtonSourceAsync(marker);
            return;
        }

        if (_chromecastButtonReceiver.Start(marker, protocolKind))
            _ = _bluetoothManager.ProbeButtonSourceAsync(marker);
    }

    private void OnChromecastTruncatedPacketDetected()
    {
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ActiveOnboardingVm?.NotifyTruncatedPacketDetected();
            }));
        }
        else
        {
            ActiveOnboardingVm?.NotifyTruncatedPacketDetected();
        }
    }

    private void OnChromecastButtonEvent(RemoteButton button, bool isDown)
    {
        AppLogger.Info($"MainViewModel: OnChromecastButtonEvent: button={button}, isDown={isDown}", "MainViewModel");
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => OnRawButtonEvent(IntPtr.Zero, button, isDown)));
        }
        else
        {
            OnRawButtonEvent(IntPtr.Zero, button, isDown);
        }
    }

    private void OnHidButtonEvent(RemoteButton button, bool isDown)
    {
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => OnRawButtonEvent(IntPtr.Zero, button, isDown)));
        }
        else
        {
            OnRawButtonEvent(IntPtr.Zero, button, isDown);
        }
    }

    private void OnConnectionStatusChanged(bool connected, string name)
    {
        void Update()
        {
            IsConnected = connected;
            ConnectedDeviceName = connected ? name : "未连接设备";
            if (!connected)
            {
                IsVoiceActive = false;
                CurrentAudioLevel = 0f;
                ReleaseHeldVoiceShortcut();
                _voiceKeyGuard.Clear();
                _buttonGestures.Reset();
                _rawInputReceiver.ResetDeviceState();
                _hidDeviceReader.Stop();
                _chromecastButtonReceiver.Stop();
            }
            else
            {
                if (_registeredHwnd != IntPtr.Zero)
                {
                    _rawInputReceiver.Register(_registeredHwnd);
                }

                ulong addr = _bluetoothManager.ConnectedDeviceAddress;
                var dev = _configService.Current.Devices.FirstOrDefault(d => 
                    (addr != 0 && d.BluetoothAddress == addr) || 
                    d.Id == _configService.Current.SelectedDeviceId);
                if (dev != null)
                {
                    _bluetoothManager.VoiceButtonMode = dev.VoiceButtonMode;
                }
                MappingVm.SyncSelectedDeviceWithConfig();

                bool needsEtw = G9N9NProtocol.IsCandidate(name) ||
                    XiaomiProtocol.IsCandidate(name) ||
                    T1RemoteProtocol.IsCandidate(name) ||
                    (dev != null && (dev.IsG9N9N || dev.IsXiaomi || dev.IsT1)) ||
                    (ActiveOnboardingVm?.SelectedDevice != null && (ActiveOnboardingVm.SelectedDevice.IsG9N9N || ActiveOnboardingVm.SelectedDevice.IsXiaomi || ActiveOnboardingVm.SelectedDevice.IsT1));
                if (needsEtw)
                {
                    AppLogger.Info($"Activating ETW button receiver for device '{name}'", "MainViewModel");
                    StartChromecastButtons();
                }

                if (addr != 0)
                {
                    var hidInfo = HidDeviceReader.FindBestDevice(addr);
                    if (hidInfo != null)
                    {
                        if (dev != null)
                        {
                            dev.HidDevicePath = hidInfo.Path;
                            dev.HidUsagePage = hidInfo.UsagePage;
                            dev.HidUsage = hidInfo.Usage;
                            dev.HidReportLength = hidInfo.InputReportByteLength;
                            dev.HidCaptureStatus = "已就绪 (RawInput & Direct Stream)";
                        }
                        _hidDeviceReader.Start(hidInfo.Path, hidInfo.InputReportByteLength);
                    }
                    else
                    {
                        _hidDeviceReader.StartForRemote(addr);
                    }
                }
            }
        }

        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(Update);
        }
        else
        {
            Update();
        }
    }

    private void OnAudioLevelChanged(float level)
    {
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
            {
                CurrentAudioLevel = level;
            });
        }
        else
        {
            CurrentAudioLevel = level;
        }
    }

    private void OnVoiceSessionStarted()
    {
        void Update()
        {
            IsVoiceActive = true;
            if (ActiveOnboardingVm != null && ActiveOnboardingVm.CurrentWizardStep != WizardStep.TranscriptionVerify) return;
            UpdateVoiceKeyGuard();
            // ATVV is itself a verified remote source. Some Windows BLE HID
            // paths do not deliver a source-bearing F5 Raw Input event, so
            // relying solely on PhysicalVoiceKeyPressed leaves F5 held when
            // the IME chord is injected.
            _voiceKeyGuard.Arm();
            var currentConfig = _configService.Current.InputTool;
            var session = new ActiveVoiceSession(currentConfig.TriggerMode, currentConfig.Shortcut, currentConfig.Tool);
            _activeVoiceSession = session;
            AppLogger.Info($"Voice session started: Tool={session.Tool}, Mode={session.TriggerMode}, Shortcut='{session.Shortcut}'", "MainViewModel");
            if (session.TriggerMode == VoiceTriggerMode.HoldToTalk)
            {
                _voiceKeyGuard.BeginShortcutHold(session.Shortcut);
                bool sent = NativeInputSender.SendShortcutDown(session.Shortcut);
                if (!sent) _voiceKeyGuard.EndShortcutHold();
                AppLogger.Info($"Input tool shortcut down: Success={sent}", "MainViewModel");
            }
            else
            {
                bool sent = NativeInputSender.SendShortcutClick(session.Shortcut);
                AppLogger.Info($"Input tool shortcut click: Success={sent}", "MainViewModel");
            }
        }

        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(Update);
        }
        else
        {
            Update();
        }
    }

    private void OnVoiceSessionEnded()
    {
        void Update()
        {
            _voiceKeyGuard.Clear();
            IsVoiceActive = false;
            CurrentAudioLevel = 0f;
            if (_activeVoiceSession is { } session)
            {
                _activeVoiceSession = null;
                AppLogger.Info($"Voice session ended: Mode={session.TriggerMode}, Shortcut='{session.Shortcut}'", "MainViewModel");
                if (session.TriggerMode == VoiceTriggerMode.HoldToTalk)
                {
                    NativeInputSender.SendShortcutUp(session.Shortcut);
                }
                else if (session.TriggerMode == VoiceTriggerMode.ClickToToggle)
                {
                    NativeInputSender.SendShortcutClick(session.Shortcut);
                }
            }
        }

        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(Update);
        }
        else
        {
            Update();
        }
    }

    private void OnVoiceButtonPressed()
    {
        App.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            var connectedDev = _configService.Current.Devices.FirstOrDefault(d =>
                (d.BluetoothAddress != 0 && d.BluetoothAddress == _bluetoothManager.ConnectedDeviceAddress) ||
                (d.ContainerId.HasValue && d.ContainerId == _bluetoothManager.ConnectedDeviceContainerId));

            if (ActiveOnboardingVm?.CurrentWizardStep == WizardStep.ButtonTest)
                ActiveOnboardingVm.NotifyButtonPressed(RemoteButton.Voice);
            else if (ActiveOnboardingVm == null)
                ShowMappingPress(RemoteButton.Voice, connectedDev);
        }));
    }

    partial void OnActivePageNameChanged(string value)
    {
        _buttonGestures.Reset();
        if (value == "General")
        {
            GeneralVm.RefreshState();
        }
        else if (value is "Permissions" or "Environment")
        {
            EnvironmentVm.RefreshStatus();
        }
        else if (value is "Devices" or "Connection")
        {
            // Device view active
        }
        else if (value == "Mapping")
        {
            MappingVm.LoadMappings();
        }
        else if (value == "Integrations")
        {
            IntegrationsVm.Reload();
            UpdateVoiceKeyGuard();
        }
        else if (value == "AppIntegration")
        {
            AppIntegrationVm.LoadIntegrations();
        }
    }

    public void UpdateVoiceKeyGuard()
    {
        string shortcut = _configService.Current.InputTool.Shortcut ?? string.Empty;
        bool usesF5 = NativeInputSender.TryParseShortcut(shortcut, out _, out var key) && key == 0x74;
        _voiceKeyGuard.IsSuppressionEnabled = !usesF5;
        AppLogger.Debug($"VoiceKeyGuard updated: SuppressionEnabled={_voiceKeyGuard.IsSuppressionEnabled} (Shortcut='{shortcut}')", "MainViewModel");
    }

    private IntPtr _registeredHwnd = IntPtr.Zero;

    public void RegisterRawInput(IntPtr hwnd)
    {
        _registeredHwnd = hwnd;
        AppLogger.Info($"Remote voice F5 guard installed: {_voiceKeyGuard.Install()}", "MainViewModel");
        _rawInputReceiver.Register(hwnd);
    }
    public void ProcessRawInput(IntPtr lParam) => _rawInputReceiver.ProcessMessage(lParam);
    internal void HandleRawButtonEvent(IntPtr hDevice, RemoteButton button, bool isDown) => OnRawButtonEvent(hDevice, button, isDown);

    private bool IsTargetDevice(IntPtr hDevice, string devicePath)
    {
        // 1. If currently onboarding a device, evaluate match strictly against the onboarding target
        if (ActiveOnboardingVm?.SelectedDevice is { } onboardingDev)
        {
            bool match = RawInputReceiver.EvaluateDeviceMatch(onboardingDev.ContainerId, hDevice, devicePath, onboardingDev.BluetoothAddress);
            LogAdmission(hDevice, onboardingDev.ContainerId, onboardingDev.BluetoothAddress, match, devicePath);
            return match;
        }

        // 2. Normal operation: when a remote is actively connected, ONLY admit that connected remote
        Guid? connectedContainerId = _bluetoothManager.ConnectedDeviceContainerId;
        ulong connectedAddress = _bluetoothManager.ConnectedDeviceAddress;
        if (_bluetoothManager.IsConnected && (connectedContainerId.HasValue || connectedAddress != 0))
        {
            bool match = RawInputReceiver.EvaluateDeviceMatch(connectedContainerId, hDevice, devicePath, connectedAddress);
            LogAdmission(hDevice, connectedContainerId, connectedAddress, match, devicePath);
            return match;
        }

        // 3. Fallback: only if no remote is actively connected, check the user's selected remote
        var selectedDev = _configService.Current.Devices.FirstOrDefault(d => d.Id == _configService.Current.SelectedDeviceId);
        if (selectedDev != null && (selectedDev.ContainerId.HasValue || selectedDev.BluetoothAddress != 0))
        {
            bool match = RawInputReceiver.EvaluateDeviceMatch(selectedDev.ContainerId, hDevice, devicePath, selectedDev.BluetoothAddress);
            LogAdmission(hDevice, selectedDev.ContainerId, selectedDev.BluetoothAddress, match, devicePath);
            return match;
        }

        return false;
    }

    private void LogAdmission(IntPtr hDevice, Guid? targetContainerId, ulong targetAddress, bool accepted, string devicePath)
    {
        if (!_deviceAdmissionLog.TryGetValue(hDevice, out var previous) ||
            previous != (targetContainerId, accepted))
        {
            if (_deviceAdmissionLog.Count > 100) _deviceAdmissionLog.Clear();
            _deviceAdmissionLog[hDevice] = (targetContainerId, accepted);
            AppLogger.Info($"RawInput device admission: Accepted={accepted}, TargetContainer={targetContainerId}, " +
                $"TargetAddress=0x{targetAddress:X12}, InputContainer={RawInputReceiver.GetDeviceContainerId(devicePath)}, Path='{devicePath}'", "MainViewModel");
        }
    }

    private void OnRawButtonEvent(IntPtr hDevice, RemoteButton button, bool isDown)
    {
        lock (_buttonDedupLock)
        {
            long now = Environment.TickCount64;
            if (button == _lastDedupButton && isDown == _lastDedupIsDown && (now - _lastDedupTimeMs) < 60)
            {
                AppLogger.Debug($"Suppressing duplicate button event: {button} isDown={isDown} within {now - _lastDedupTimeMs}ms", "MainViewModel");
                return;
            }
            _lastDedupButton = button;
            _lastDedupIsDown = isDown;
            _lastDedupTimeMs = now;
        }

        // 1. Resolve source device strictly
        RemoteDevice? sourceDevice = null;
        if (hDevice != IntPtr.Zero)
        {
            string? devPath = RawInputReceiver.GetDevicePath(hDevice);
            if (!string.IsNullOrEmpty(devPath))
            {
                var inputContainerId = RawInputReceiver.GetDeviceContainerId(devPath);
                sourceDevice = _configService.Current.Devices.FirstOrDefault(d =>
                    (d.ContainerId.HasValue && inputContainerId.HasValue && d.ContainerId.Value == inputContainerId.Value) ||
                    (d.BluetoothAddress != 0 && devPath.ToUpperInvariant().Contains(d.BluetoothAddress.ToString("X").ToUpperInvariant())));
            }
        }
        else
        {
            // Event from ETW or HID direct stream: comes from the active connected BLE remote (or onboarding target)
            ulong connectedAddr = _bluetoothManager.ConnectedDeviceAddress;
            Guid? connectedContainer = _bluetoothManager.ConnectedDeviceContainerId;
            sourceDevice = _configService.Current.Devices.FirstOrDefault(d =>
                (connectedAddr != 0 && d.BluetoothAddress == connectedAddr) ||
                (connectedContainer.HasValue && d.ContainerId == connectedContainer.Value));
            if (sourceDevice == null && ActiveOnboardingVm?.SelectedDevice != null)
            {
                sourceDevice = ActiveOnboardingVm.SelectedDevice;
            }
        }
        sourceDevice ??= _configService.Current.Devices.FirstOrDefault(d => d.Id == _configService.Current.SelectedDeviceId)
            ?? _configService.Current.Devices.FirstOrDefault();

        // 2. Onboarding Isolation: if onboarding, strictly enforce device match
        if (ActiveOnboardingVm != null)
        {
            if (ActiveOnboardingVm.SelectedDevice != null && sourceDevice != null &&
                sourceDevice.BluetoothAddress != 0 && ActiveOnboardingVm.SelectedDevice.BluetoothAddress != 0 &&
                sourceDevice.BluetoothAddress != ActiveOnboardingVm.SelectedDevice.BluetoothAddress)
            {
                AppLogger.Info($"Ignoring button {button} from device '{sourceDevice.Name}' (0x{sourceDevice.BluetoothAddress:X}) during onboarding of '{ActiveOnboardingVm.SelectedDevice.Name}' (0x{ActiveOnboardingVm.SelectedDevice.BluetoothAddress:X})", "MainViewModel");
                return;
            }

            _buttonGestures.Reset();
            if (isDown)
            {
                ActiveOnboardingVm.NotifyButtonPressed(button);
            }
            if (button != RemoteButton.Voice || (ActiveOnboardingVm.CurrentWizardStep is not (WizardStep.AudioTest or WizardStep.TranscriptionVerify)))
                return;
        }

        // 3. Absolute device isolation: when a remote is actively connected, completely drop buttons from any other remote
        ulong activeAddress = _bluetoothManager.ConnectedDeviceAddress;
        if (_bluetoothManager.IsConnected && activeAddress != 0 && sourceDevice != null &&
            sourceDevice.BluetoothAddress != 0 && sourceDevice.BluetoothAddress != activeAddress)
        {
            AppLogger.Debug($"Ignoring button {button} from inactive device '{sourceDevice.Name}' (0x{sourceDevice.BluetoothAddress:X}). Active device is 0x{activeAddress:X}", "MainViewModel");
            return;
        }

        // 4. Mapping page visual feedback (isolated to the remote currently viewed in Mapping UI)
        if (isDown && ActiveOnboardingVm == null)
            ShowMappingPress(button, sourceDevice);

        // 5. Voice key handling
        if (button == RemoteButton.Voice)
        {

            var currentMode = _configService.Current.InputTool.TriggerMode;
            if (currentMode == VoiceTriggerMode.HoldToTalk)
            {
                if (isDown)
                {
                    _ = _bluetoothManager.StartVoiceSessionAsync();
                }
                else
                {
                    _ = _bluetoothManager.StopVoiceSessionAsync();
                }
            }
            else // ClickToToggle
            {
                if (isDown)
                {
                    if (_bluetoothManager.IsStreaming || _bluetoothManager.SessionState == BluetoothSessionState.Opening)
                    {
                        _ = _bluetoothManager.StopVoiceSessionAsync();
                    }
                    else
                    {
                        _ = _bluetoothManager.StartVoiceSessionAsync();
                    }
                }
            }
            return;
        }

        // 5. Custom button mappings: apply mappings belonging to sourceDevice
        if (!MappingVm.CustomMappingEnabled) return;
        if (!isDown)
        {
            _buttonGestures.HandleUp(button);
            return;
        }

        var mapping = sourceDevice?.Mappings?.FirstOrDefault(m => m.Button == button);
        if (mapping != null) _buttonGestures.HandleDown(mapping);
    }

    private void ShowMappingPress(RemoteButton button, RemoteDevice? sourceDevice = null)
    {
        if (ActivePageName == "Mapping" && MappingVm.SelectedDevice != null)
        {
            if (sourceDevice != null &&
                sourceDevice.Id != MappingVm.SelectedDevice.Id &&
                sourceDevice.BluetoothAddress != MappingVm.SelectedDevice.BluetoothAddress)
            {
                // Physical press was on a different remote than the one currently selected in the Mapping page
                return;
            }
            MappingVm.NotifyHardwarePress(button);
        }
    }

    private void ReleaseHeldVoiceShortcut()
    {
        if (_activeVoiceSession is { } session)
        {
            _activeVoiceSession = null;
            if (session.TriggerMode == VoiceTriggerMode.HoldToTalk)
            {
                AppLogger.Info($"Releasing held voice shortcut: Shortcut='{session.Shortcut}'", "MainViewModel");
                NativeInputSender.SendShortcutUp(session.Shortcut);
            }
            else if (session.TriggerMode == VoiceTriggerMode.ClickToToggle)
            {
                AppLogger.Info($"Toggling off voice shortcut on session teardown: Shortcut='{session.Shortcut}'", "MainViewModel");
                NativeInputSender.SendShortcutClick(session.Shortcut);
            }
        }
        _voiceKeyGuard.EndShortcutHold();
        NativeInputSender.ReleaseAllHeldSyntheticKeys();
    }

    public void HandleDeviceRemoval(IntPtr hDevice)
    {
        AppLogger.Info($"Device removal notification received for hDevice=0x{hDevice:X}", "MainViewModel");
        _rawInputReceiver.ResetDeviceState(hDevice);
        _buttonGestures.Reset();
    }

    private static Action ScheduleButtonGesture(int delayMilliseconds, Action callback)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(delayMilliseconds)
        };
        timer.Tick += OnTick;
        timer.Start();
        return Cancel;

        void OnTick(object? sender, EventArgs args)
        {
            Cancel();
            callback();
        }

        void Cancel()
        {
            timer.Stop();
            timer.Tick -= OnTick;
        }
    }

    private void OnMappingSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MappingViewModel.CustomMappingEnabled)) _buttonGestures.Reset();
    }

    private void ExecuteButtonAction(ButtonAssignment assignment)
    {
        if (!_bluetoothManager.IsConnected || ActiveOnboardingVm != null || !MappingVm.CustomMappingEnabled) return;

        switch (assignment.Action)
        {
            case ButtonActionType.KeyboardShortcut when !string.IsNullOrEmpty(assignment.Shortcut):
                NativeInputSender.SendShortcutClick(assignment.Shortcut);
                break;
            case ButtonActionType.MediaControl when assignment.Media != MediaCommand.None:
                NativeInputSender.SendMedia(assignment.Media);
                break;
            case ButtonActionType.TextSnippet when !string.IsNullOrEmpty(assignment.Text):
                NativeInputSender.SendUnicodeText(assignment.Text);
                break;
        }
    }

    [RelayCommand]
    public void Navigate(string pageName)
    {
        ActivePageName = pageName;
    }

    public void Dispose()
    {
        _bluetoothManager.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _bluetoothManager.AudioLevelChanged -= OnAudioLevelChanged;
        _bluetoothManager.VoiceSessionStarted -= OnVoiceSessionStarted;
        _bluetoothManager.VoiceSessionEnded -= OnVoiceSessionEnded;
        _bluetoothManager.VoiceButtonPressed -= OnVoiceButtonPressed;
        _rawInputReceiver.ButtonEvent -= OnRawButtonEvent;
        _rawInputReceiver.PhysicalVoiceKeyPressed -= _voiceKeyGuard.Arm;
        _hidDeviceReader.ButtonEvent -= OnHidButtonEvent;
        _hidDeviceReader.Dispose();
        _chromecastButtonReceiver.ButtonEvent -= OnChromecastButtonEvent;
        _chromecastButtonReceiver.TruncatedPacketDetected -= OnChromecastTruncatedPacketDetected;
        _chromecastButtonReceiver.Dispose();

        ReleaseHeldVoiceShortcut();
        _voiceKeyGuard.Dispose();
        _buttonGestures.Reset();
        MappingVm.PropertyChanged -= OnMappingSettingsChanged;
        DevicesVm.Dispose();
        _rawInputReceiver.Dispose();
    }
}
