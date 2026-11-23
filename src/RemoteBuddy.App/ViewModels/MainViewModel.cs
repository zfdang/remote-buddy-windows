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

    private readonly object _repeatLock = new();
    private System.Threading.Timer? _repeatTimer;
    private int _repeatGeneration = 0;
    private RemoteButton _heldButton = RemoteButton.None;
    private ButtonMapping? _activeRepeatMapping;

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
            StopRepeatTimer();
            if (value != null && IsConnected)
            {
                bool isChromecast = G9N9NProtocol.IsCandidate(ConnectedDeviceName) ||
                    (value.SelectedDevice != null && value.SelectedDevice.IsG9N9N);
                if (isChromecast && !_chromecastButtonReceiver.IsRunning)
                {
                    AppLogger.Info("Activating Chromecast ETW receiver for active onboarding session", "MainViewModel");
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
        _voiceKeyGuard = new RemoteVoiceKeyGuard(() =>
            _bluetoothManager.SessionState is BluetoothSessionState.Opening or BluetoothSessionState.Streaming or BluetoothSessionState.Draining);
        _rawInputReceiver.PhysicalVoiceKeyPressed += _voiceKeyGuard.Arm;

        GeneralVm = new GeneralViewModel(_configService, _bluetoothManager, mode => RequestOpenWizard?.Invoke(mode), Navigate);
        DevicesVm = new DevicesViewModel(_configService, _bluetoothManager, mode => RequestOpenWizard?.Invoke(mode));
        MappingVm = new MappingViewModel(_configService);
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
        if (_bluetoothManager.ConnectedDeviceAddress == 0 || _chromecastButtonReceiver.IsRunning) return;
        var marker = Guid.NewGuid();
        if (_chromecastButtonReceiver.Start(marker))
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
                StopRepeatTimer();
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

                bool isChromecast = G9N9NProtocol.IsCandidate(name) ||
                    (dev != null && dev.IsG9N9N) ||
                    (ActiveOnboardingVm?.SelectedDevice != null && ActiveOnboardingVm.SelectedDevice.IsG9N9N);
                if (isChromecast)
                {
                    AppLogger.Info($"Activating Chromecast ETW receiver for device '{name}'", "MainViewModel");
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
            if (ActiveOnboardingVm?.CurrentWizardStep == WizardStep.ButtonTest)
                ActiveOnboardingVm.NotifyButtonPressed(RemoteButton.Voice);
            else if (ActiveOnboardingVm == null)
                ShowMappingPress(RemoteButton.Voice);
        }));
    }

    partial void OnActivePageNameChanged(string value)
    {
        StopRepeatTimer();
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

    private bool IsTargetDevice(IntPtr hDevice, string devicePath)
    {
        // 1. Establish HID vs BLE correspondence through official Windows Container ID or Bluetooth MAC
        Guid? targetContainerId = _bluetoothManager.ConnectedDeviceContainerId
            ?? ActiveOnboardingVm?.SelectedDevice?.ContainerId
            ?? _configService.Current.Devices.FirstOrDefault(d => d.Id == _configService.Current.SelectedDeviceId)?.ContainerId;

        ulong targetAddress = _bluetoothManager.ConnectedDeviceAddress;
        if (targetAddress == 0 && ActiveOnboardingVm?.SelectedDevice != null)
        {
            targetAddress = ActiveOnboardingVm.SelectedDevice.BluetoothAddress;
        }

        bool accepted = RawInputReceiver.EvaluateDeviceMatch(targetContainerId, hDevice, devicePath, targetAddress);
        if (!_deviceAdmissionLog.TryGetValue(hDevice, out var previous) ||
            previous != (targetContainerId, accepted))
        {
            if (_deviceAdmissionLog.Count > 100) _deviceAdmissionLog.Clear();
            _deviceAdmissionLog[hDevice] = (targetContainerId, accepted);
            AppLogger.Info($"RawInput device admission: Accepted={accepted}, TargetContainer={targetContainerId}, " +
                $"TargetAddress=0x{targetAddress:X12}, InputContainer={RawInputReceiver.GetDeviceContainerId(devicePath)}, Path='{devicePath}'", "MainViewModel");
        }
        return accepted;
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

        if (ActiveOnboardingVm != null)
        {
            StopRepeatTimer();
            if (isDown)
            {
                ActiveOnboardingVm.NotifyButtonPressed(button);
            }
            if (button != RemoteButton.Voice || (ActiveOnboardingVm.CurrentWizardStep is not (WizardStep.AudioTest or WizardStep.TranscriptionVerify)))
                return;
        }

        if (isDown && ActiveOnboardingVm == null)
            ShowMappingPress(button);

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

        if (!MappingVm.CustomMappingEnabled) return;

        var selectedDev = _configService.Current.Devices.FirstOrDefault(d => d.Id == _configService.Current.SelectedDeviceId);
        var mapping = selectedDev?.Mappings?.FirstOrDefault(m => m.Button == button);
        if (mapping == null) return;

        AppLogger.Info($"Button mapping event: {button} isDown={isDown}, Action={mapping.ClickAction}", "MainViewModel");

        if (isDown)
        {
            StopRepeatTimer();
            ExecuteButtonAction(mapping.ClickAction, mapping.ClickShortcut, mapping.ClickMedia, mapping.ClickText);

            if (mapping.LongPressAction == ButtonActionType.ContinuousRepeat)
            {
                lock (_repeatLock)
                {
                    _heldButton = button;
                    _activeRepeatMapping = mapping;
                    var capturedMapping = mapping;
                    int currentGen = _repeatGeneration;
                    _repeatTimer = new System.Threading.Timer(_ =>
                    {
                        try
                        {
                            if (Volatile.Read(ref _repeatGeneration) != currentGen) return;
                            if (!_bluetoothManager.IsConnected || ActiveOnboardingVm != null)
                            {
                                StopRepeatTimer();
                                return;
                            }

                            lock (_repeatLock)
                            {
                                if (_repeatGeneration != currentGen || _heldButton != capturedMapping.Button) return;
                            }

                            AppLogger.Debug($"Repeating button action: {capturedMapping.Button} ({capturedMapping.ClickAction})", "MainViewModel");
                            ExecuteButtonAction(capturedMapping.ClickAction, capturedMapping.ClickShortcut, capturedMapping.ClickMedia, capturedMapping.ClickText);

                            lock (_repeatLock)
                            {
                                if (_repeatGeneration == currentGen && _heldButton == capturedMapping.Button)
                                {
                                    _repeatTimer?.Change(100, Timeout.Infinite);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("Exception in button repeat timer", ex, "MainViewModel");
                        }
                    }, null, 400, Timeout.Infinite);
                }
            }
        }
        else
        {
            lock (_repeatLock)
            {
                if (button == _heldButton)
                {
                    StopRepeatTimer();
                }
            }
        }
    }

    private void ShowMappingPress(RemoteButton button)
    {
        if (ActivePageName == "Mapping" && MappingVm.SelectedDevice is { BluetoothAddress: not 0 } device &&
            device.BluetoothAddress == _bluetoothManager.ConnectedDeviceAddress)
        {
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
        StopRepeatTimer();
    }

    private void StopRepeatTimer()
    {
        lock (_repeatLock)
        {
            Interlocked.Increment(ref _repeatGeneration);
            _repeatTimer?.Dispose();
            _repeatTimer = null;
            _heldButton = RemoteButton.None;
            _activeRepeatMapping = null;
        }
    }

    private static void ExecuteButtonAction(ButtonActionType actionType, string? shortcut, MediaCommand media, string? text)
    {
        switch (actionType)
        {
            case ButtonActionType.KeyboardShortcut:
                if (!string.IsNullOrEmpty(shortcut))
                {
                    NativeInputSender.SendShortcutClick(shortcut);
                }
                break;
            case ButtonActionType.MediaControl:
                if (media != MediaCommand.None)
                {
                    NativeInputSender.SendMedia(media);
                }
                break;
            case ButtonActionType.TextSnippet:
                if (!string.IsNullOrEmpty(text))
                {
                    NativeInputSender.SendUnicodeText(text);
                }
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
        StopRepeatTimer();
        DevicesVm.Dispose();
        _rawInputReceiver.Dispose();
    }
}
