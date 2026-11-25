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
    private record struct ActiveVoiceSession(ulong DeviceAddress, long Token, VoiceTriggerMode TriggerMode, string Shortcut, TargetInputTool Tool, bool ShortcutSent = false);

    private readonly ConfigService _configService;
    private readonly IBluetoothManager _bluetoothManager;
    private readonly RawInputReceiver _rawInputReceiver = new();
    private readonly Dictionary<ulong, HidDeviceReader> _hidReaders = new();
    private readonly ChromecastButtonReceiver _chromecastButtonReceiver = new();
    private readonly RemoteVoiceKeyGuard _voiceKeyGuard;
    private IUsbKeyboardSender _usbKeyboard = new DoubaoUsbKeyboard();
    private readonly object _voiceLifecycleLock = new();
    private ActiveVoiceSession? _activeVoiceSession;
    private long _voiceEventSeq;
    private ulong _liveVoiceAddress;
    private long _liveVoiceToken;
    private ulong _voiceKeyGuardOwnerAddress;

    internal Func<bool> IsAdministratorForTest { get; set; } = BluetoothAdapterHelper.IsAdministrator;
    internal IUsbKeyboardSender UsbKeyboardForTest
    {
        get => _usbKeyboard;
        set
        {
            if (ReferenceEquals(_usbKeyboard, value)) return;
            _usbKeyboard.Dispose();
            _usbKeyboard = value;
        }
    }

    internal enum InputSource { Direct, RawInput, HidReader, Etw }

    private readonly Dictionary<ulong, RemoteButtonGestures> _gestures = new();
    private readonly Dictionary<ulong, Dictionary<(RemoteButton Button, bool Down), (long Time, InputSource Source)>> _dedup = new();
    private readonly Dictionary<(ulong Address, RemoteButton Button), bool> _buttonLogicalDown = new();
    private readonly HashSet<ulong> _readyAddresses = new();
    private readonly Dictionary<IntPtr, ulong> _rawAddresses = new();
    private readonly HashSet<IntPtr> _nonTargetRawHandles = new();
    private readonly Action<ButtonAssignment> _executeMapping;
    private EtwButtonSource[] _etwSources = [];
    private CancellationTokenSource? _etwStartCts;
    private CancellationTokenSource? _probeRetryCts;
    private readonly HashSet<ulong> _unboundEtwAddresses = [];
    private readonly object _etwProbeLock = new();
    private bool _etwElevationDeclined;
    private bool _voiceKeyGuardInstalled;

    private void ResetGestures()
    {
        foreach (var gestures in _gestures.Values) gestures.Reset();
        _gestures.Clear();
        _dedup.Clear();
        _buttonLogicalDown.Clear();
    }

    private void ResetDeviceGestures(ulong address)
    {
        if (_gestures.Remove(address, out var gestures)) gestures.Reset();
        _dedup.Remove(address);
        foreach (var key in _buttonLogicalDown.Keys.Where(k => k.Address == address).ToArray())
            _buttonLogicalDown.Remove(key);
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarningBanner))]
    private string _warningBannerMessage = string.Empty;

    [ObservableProperty]
    private string _warningBannerActionText = string.Empty;

    public bool HasWarningBanner => !string.IsNullOrEmpty(WarningBannerMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EtwIndicatorText))]
    [NotifyPropertyChangedFor(nameof(EtwIndicatorColor))]
    private EtwCaptureStatus _etwCaptureStatus = EtwCaptureStatus.Idle;

    private enum WarningKind { None, Audio, Etw, UsbKeyboard }
    private WarningKind _activeWarningKind;

    public string EtwIndicatorText => EtwCaptureStatus switch
    {
        EtwCaptureStatus.RunningInProcess or EtwCaptureStatus.RunningElevatedWorker => "ETW 按键捕获：运行中",
        EtwCaptureStatus.Starting => "ETW 按键捕获：启动中…",
        EtwCaptureStatus.ElevationDeclined => "ETW 按键捕获：已拒绝提权",
        EtwCaptureStatus.Disconnected => "ETW 按键捕获：已断开",
        EtwCaptureStatus.Failed => "ETW 按键捕获：启动失败",
        _ => "ETW 按键捕获：待机"
    };

    public string EtwIndicatorColor => EtwCaptureStatus switch
    {
        EtwCaptureStatus.RunningInProcess or EtwCaptureStatus.RunningElevatedWorker => "#44FF88",
        EtwCaptureStatus.Starting => "#88CCFF",
        EtwCaptureStatus.ElevationDeclined or EtwCaptureStatus.Disconnected => "#FFB347",
        EtwCaptureStatus.Failed => "#FF6666",
        _ => "#888888"
    };

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
            if (ReferenceEquals(_activeOnboardingVm, value)) return;
            if (_activeOnboardingVm != null)
            {
                _activeOnboardingVm.PropertyChanged -= OnOnboardingPropertyChanged;
            }
            _activeOnboardingVm = value;
            if (_activeOnboardingVm != null)
            {
                _activeOnboardingVm.PropertyChanged += OnOnboardingPropertyChanged;
            }
            ResetGestures();
            RefreshInputSources();
            UpdateVoiceKeyGuard();
        }
    }

    private InputToolConfig GetEffectiveInputToolConfig() =>
        ActiveOnboardingVm?.ActiveVerificationInputTool ?? _configService.Current.InputTool;

    private void OnOnboardingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(OnboardingViewModel.CurrentStep)
            or nameof(OnboardingViewModel.ShortcutText)
            or nameof(OnboardingViewModel.SelectedTool)
            or nameof(OnboardingViewModel.TriggerMode))
        {
            UpdateVoiceKeyGuard();
        }
    }

    public bool IsMainWindowActive { get; set; }

    public bool IsModalConfirmationOpen { get; set; }

    internal bool IsMappingPreviewSuppressed() =>
        ActiveOnboardingVm == null && (IsMainWindowActive || IsModalConfirmationOpen) && ActivePageName == "Mapping";

    private int _buttonSourceGeneration;

    public event Action<WizardMode>? RequestOpenWizard;

    public MainViewModel(ConfigService configService, IBluetoothManager bluetoothManager)
        : this(configService, bluetoothManager, null) { }

    internal MainViewModel(ConfigService configService, IBluetoothManager bluetoothManager, Action<ButtonAssignment>? executeMapping)
    {
        _executeMapping = executeMapping ?? ExecuteButtonAction;
        _configService = configService;
        _bluetoothManager = bluetoothManager;
        _voiceKeyGuard = new RemoteVoiceKeyGuard(() =>
            _bluetoothManager.SessionState is BluetoothSessionState.Opening or BluetoothSessionState.Streaming or BluetoothSessionState.Draining);
        _rawInputReceiver.PhysicalVoiceKeyPressed += OnRawVoiceKey;

        GeneralVm = new GeneralViewModel(_configService, _bluetoothManager, mode => RequestOpenWizard?.Invoke(mode), Navigate);
        DevicesVm = new DevicesViewModel(_configService, _bluetoothManager, mode => RequestOpenWizard?.Invoke(mode));
        MappingVm = new MappingViewModel(_configService, mode => RequestOpenWizard?.Invoke(mode));
        DevicesVm.DevicesChanged += OnDevicesChanged;
        MappingVm.PropertyChanged += OnMappingSettingsChanged;
        IntegrationsVm = new IntegrationsViewModel(_configService, mode => RequestOpenWizard?.Invoke(mode));
        EnvironmentVm = new EnvironmentViewModel();
        EnvironmentVm.RetryEtwCaptureAction = RetryEtwCapture;
        AppIntegrationVm = new AppIntegrationViewModel(_configService);
        AboutVm = new AboutViewModel(
            _configService,
            Services.WindowsRelease.FetchAsync,
            BuildDiagnosticSnapshot);
        SettingsVm = new SettingsViewModel(_configService, mode => RequestOpenWizard?.Invoke(mode));
        GeneralVm.PreferencesChanged += OnGeneralPreferencesChanged;
        SettingsVm.PreferencesChanged += OnSettingsPreferencesChanged;
        AboutVm.PreferencesChanged += OnAboutPreferencesChanged;
        UpdateVoiceKeyGuard();

        _rawInputReceiver.DeviceFilter = IsTargetDevice;
        _rawInputReceiver.DeviceModelResolver = ResolveRawModelType;

        _bluetoothManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        _bluetoothManager.AudioLevelChanged += OnAudioLevelChanged;
        _bluetoothManager.VoiceSessionStarted += OnVoiceSessionStarted;
        _bluetoothManager.VoiceSessionEnded += OnVoiceSessionEnded;
        _bluetoothManager.VoiceButtonPressed += OnVoiceButtonPressed;
        _bluetoothManager.AudioOutputFailed += OnAudioOutputFailed;
        _rawInputReceiver.ButtonEvent += OnRawButtonEvent;
        _chromecastButtonReceiver.ButtonEvent += OnChromecastButtonEvent;
        _chromecastButtonReceiver.TruncatedPacketDetected += OnChromecastTruncatedPacketDetected;
        _chromecastButtonReceiver.DeviceDisconnected += OnEtwDeviceDisconnected;
        _chromecastButtonReceiver.SourceBound += OnEtwSourceBound;
        _chromecastButtonReceiver.UnexpectedDisconnect += OnEtwUnexpectedDisconnect;
        _chromecastButtonReceiver.StatusChanged += OnEtwStatusChanged;
    }

    private void OnDevicesChanged()
    {
        _bluetoothManager.SetAutoReconnectTargets(_configService.Current.Devices.Select(d => d.BluetoothAddress));
        MappingVm.SyncWithDevices();
        RefreshInputSources();
    }

    private void OnAudioOutputFailed(ulong address, string reason) => RunOnUi(() =>
    {
        _activeWarningKind = WarningKind.Audio;
        WarningBannerMessage = string.IsNullOrWhiteSpace(reason)
            ? "虚拟音频输出未就绪，请检查 VB-CABLE 驱动是否已安装并启用。"
            : $"语音音频输出失败：{reason}";
        WarningBannerActionText = "查看驱动";
        EnvironmentVm.RefreshStatus();
    });

    private void OnEtwStatusChanged(EtwCaptureStatus status) => RunOnUi(() =>
    {
        EtwCaptureStatus = status;
        EnvironmentVm.UpdateEtwStatus(status);

        switch (status)
        {
            case EtwCaptureStatus.ElevationDeclined:
                _activeWarningKind = WarningKind.Etw;
                WarningBannerMessage = "未授予管理员权限，Chromecast / T1 按键捕获服务未启动。";
                WarningBannerActionText = "重新提权";
                break;
            case EtwCaptureStatus.Disconnected:
                _activeWarningKind = WarningKind.Etw;
                WarningBannerMessage = "ETW 按键捕获服务已意外断开，部分遥控器实体按键可能暂时无响应。";
                WarningBannerActionText = "重新启动";
                break;
            case EtwCaptureStatus.Failed:
                _activeWarningKind = WarningKind.Etw;
                WarningBannerMessage = "ETW 按键捕获服务启动失败，请重试或检查安全权限设置。";
                WarningBannerActionText = "重试启动";
                break;
            case EtwCaptureStatus.RunningInProcess:
            case EtwCaptureStatus.RunningElevatedWorker:
            case EtwCaptureStatus.Idle:
                if (_activeWarningKind == WarningKind.Etw)
                {
                    _activeWarningKind = WarningKind.None;
                    WarningBannerMessage = string.Empty;
                    WarningBannerActionText = string.Empty;
                }
                break;
        }
    });

    internal void SimulateEtwStatusForTest(EtwCaptureStatus status) => OnEtwStatusChanged(status);

    [RelayCommand]
    public void DismissWarningBanner()
    {
        _activeWarningKind = WarningKind.None;
        WarningBannerMessage = string.Empty;
        WarningBannerActionText = string.Empty;
    }

    [RelayCommand]
    public void ExecuteWarningBannerAction()
    {
        switch (_activeWarningKind)
        {
            case WarningKind.UsbKeyboard:
                if (BluetoothAdapterHelper.RestartAppAsAdministrator())
                {
                    if (System.Windows.Application.Current?.MainWindow is Views.MainWindow mw)
                    {
                        mw.IsExplicitExit = true;
                    }
                    System.Windows.Application.Current?.Shutdown();
                }
                break;
            case WarningKind.Audio:
                Navigate("Permissions");
                break;
            case WarningKind.Etw:
                RetryEtwCapture();
                break;
        }
    }

    [RelayCommand]
    public void RetryEtwCapture()
    {
        RefreshInputSources(forceRetry: true);
    }

    private IEnumerable<RemoteDevice> KnownRemotes()
    {
        if (ActiveOnboardingVm?.SelectedDevice is { BluetoothAddress: not 0 } onboarding)
            yield return onboarding;
        foreach (var device in _configService.Current.Devices)
            if (device.BluetoothAddress != 0)
                yield return device;
    }

    internal void RefreshInputSources() => RefreshInputSources(forceRetry: false);

    internal void RefreshInputSources(bool forceRetry)
    {
        if (forceRetry) _etwElevationDeclined = false;
        _nonTargetRawHandles.Clear();

        var devices = KnownRemotes().DistinctBy(device => device.BluetoothAddress).ToArray();
        var ready = devices.Where(device => _bluetoothManager.IsDeviceReady(device.BluetoothAddress)).ToArray();
        var addresses = ready.Select(device => device.BluetoothAddress).ToHashSet();
        foreach (ulong address in _readyAddresses.Except(addresses).ToArray())
        {
            ResetDeviceGestures(address);
            lock (_etwProbeLock) _unboundEtwAddresses.Remove(address);
            foreach (var handle in _rawAddresses.Where(pair => pair.Value == address).Select(pair => pair.Key).ToArray())
            {
                _rawAddresses.Remove(handle);
                _rawInputReceiver.ResetDeviceState(handle);
            }
            if (_hidReaders.Remove(address, out var reader)) reader.Dispose();
        }

        var currentEtwSources = Volatile.Read(ref _etwSources);
        var existingByAddress = currentEtwSources
            .GroupBy(source => source.Address)
            .ToDictionary(group => group.Key, group => group.First());
        var wanted = devices.Select(device =>
        {
            var protocol = device.IsG9N9N || G9N9NProtocol.IsCandidate(device.Name)
                ? EtwProtocolKind.GoogleG9N9N
                : device.IsT1 ? EtwProtocolKind.T1Remote : EtwProtocolKind.Xiaomi;
            EtwAttributeFilter filter = default;
            if (protocol == EtwProtocolKind.GoogleG9N9N)
            {
                if (_bluetoothManager.IsDeviceReady(device.BluetoothAddress))
                {
                    filter = _bluetoothManager.GetEtwAttributeFilter(device.BluetoothAddress);
                }
                else if (existingByAddress.TryGetValue(device.BluetoothAddress, out var prior) &&
                         prior.Protocol == protocol)
                {
                    // Preserve the cached G9N9N filter while asleep so sibling remotes do not restart ETW.
                    filter = new EtwAttributeFilter(prior.HidHandleStart, prior.HidHandleEnd, prior.ExcludedAttributes);
                }
            }
            return new EtwButtonSource(
                device.BluetoothAddress,
                Guid.NewGuid(),
                protocol,
                filter.HidHandleStart,
                filter.HidHandleEnd,
                filter.ExcludedAttributes);
        }).ToArray();
        bool rebuild = !EtwSourcesMatch(currentEtwSources, wanted) || !_chromecastButtonReceiver.IsRunning;
        if (rebuild && ready.Length > 0)
        {
            int generation = Interlocked.Increment(ref _buttonSourceGeneration);
            CancelEtwTasks();
            _chromecastButtonReceiver.Stop();
            ResetGestures();
            Volatile.Write(ref _etwSources, wanted);
            lock (_etwProbeLock)
            {
                _unboundEtwAddresses.Clear();
                foreach (ulong addr in addresses) _unboundEtwAddresses.Add(addr);
            }

            if (!_etwElevationDeclined)
            {
                var startCts = new CancellationTokenSource();
                _etwStartCts = startCts;
                _ = StartAndProbeEtwAsync(wanted, generation, startCts.Token);
            }
        }
        else if (ready.Length == 0)
        {
            Interlocked.Increment(ref _buttonSourceGeneration);
            CancelEtwTasks();
            lock (_etwProbeLock) _unboundEtwAddresses.Clear();
            if (_chromecastButtonReceiver.IsRunning)
            {
                _chromecastButtonReceiver.Stop();
            }
        }

        foreach (var device in ready)
        {
            ulong address = device.BluetoothAddress;
            if (!rebuild && !_readyAddresses.Contains(address))
            {
                var source = Volatile.Read(ref _etwSources).FirstOrDefault(entry => entry.Address == address);
                if (source.Address != 0 && _chromecastButtonReceiver.IsRunning)
                {
                    lock (_etwProbeLock) _unboundEtwAddresses.Add(address);
                    _ = _bluetoothManager.ProbeButtonSourceAsync(source.Marker, address);
                    StartProbeRetryLoop(Volatile.Read(ref _buttonSourceGeneration));
                }
            }

            if (_hidReaders.ContainsKey(address) || _readyAddresses.Contains(address)) continue;
            var info = HidDeviceReader.FindBestDevice(address);
            if (info == null) continue;
            device.HidDevicePath = info.Path;
            device.HidUsagePage = info.UsagePage;
            device.HidUsage = info.Usage;
            device.HidReportLength = info.InputReportByteLength;
            var reader = new HidDeviceReader();
            if (reader.Start(info.Path, info.InputReportByteLength, device.ModelType))
            {
                _hidReaders[address] = reader;
                reader.ButtonEvent += (button, down) => RunOnUi(() =>
                {
                    if (_hidReaders.GetValueOrDefault(address) == reader)
                        HandleDeviceButton(address, button, down, InputSource.HidReader);
                });
            }
            else
                reader.Dispose();
        }
        _readyAddresses.Clear();
        _readyAddresses.UnionWith(addresses);
    }

    private void CancelEtwTasks()
    {
        var startCts = _etwStartCts;
        _etwStartCts = null;
        try { startCts?.Cancel(); } catch { }

        CancellationTokenSource? retryCts;
        lock (_etwProbeLock)
        {
            retryCts = _probeRetryCts;
            _probeRetryCts = null;
        }
        try { retryCts?.Cancel(); } catch { }
    }

    internal EtwButtonSource[] EtwSourcesForTest => Volatile.Read(ref _etwSources);

    internal static bool EtwSourcesMatch(EtwButtonSource[] existing, EtwButtonSource[] wanted)
    {
        if (existing.Length != wanted.Length) return false;
        for (int i = 0; i < existing.Length; i++)
        {
            ref readonly var a = ref existing[i];
            ref readonly var b = ref wanted[i];
            if (a.Address != b.Address ||
                a.Protocol != b.Protocol ||
                a.HidHandleStart != b.HidHandleStart ||
                a.HidHandleEnd != b.HidHandleEnd)
            {
                return false;
            }
            if (!(a.ExcludedAttributes ?? []).SequenceEqual(b.ExcludedAttributes ?? []))
            {
                return false;
            }
        }
        return true;
    }

    private async Task StartAndProbeEtwAsync(EtwButtonSource[] sources, int generation, CancellationToken ct)
    {
        bool started = await _chromecastButtonReceiver.StartAsync(sources, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || generation != Volatile.Read(ref _buttonSourceGeneration)) return;

        if (!started)
        {
            if (_chromecastButtonReceiver.Status == EtwCaptureStatus.ElevationDeclined)
            {
                RunOnUi(() => _etwElevationDeclined = true);
            }
            return;
        }

        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested) return;
            if (_bluetoothManager.IsDeviceReady(source.Address))
            {
                _ = _bluetoothManager.ProbeButtonSourceAsync(source.Marker, source.Address);
            }
        }

        StartProbeRetryLoop(generation);
    }

    private void StartProbeRetryLoop(int generation)
    {
        CancellationToken token;
        CancellationTokenSource? oldCts;
        lock (_etwProbeLock)
        {
            oldCts = _probeRetryCts;
            _probeRetryCts = new CancellationTokenSource();
            token = _probeRetryCts.Token;
        }
        try { oldCts?.Cancel(); } catch { }

        _ = Task.Run(async () =>
        {
            int[] delaysMs = [1500, 3000, 6000];
            foreach (int delayMs in delaysMs)
            {
                try { await Task.Delay(delayMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                if (token.IsCancellationRequested ||
                    generation != Volatile.Read(ref _buttonSourceGeneration) ||
                    !_chromecastButtonReceiver.IsRunning)
                {
                    return;
                }

                EtwButtonSource[] remaining;
                var sourcesSnapshot = Volatile.Read(ref _etwSources);
                lock (_etwProbeLock)
                {
                    if (_unboundEtwAddresses.Count == 0) return;
                    remaining = sourcesSnapshot
                        .Where(s => _unboundEtwAddresses.Contains(s.Address) && _bluetoothManager.IsDeviceReady(s.Address))
                        .ToArray();
                }

                if (remaining.Length == 0) return;
                foreach (var src in remaining)
                {
                    AppLogger.Info($"Retrying ETW GATT probe for unbound remote 0x{src.Address:X12}", "MainViewModel");
                    _ = _bluetoothManager.ProbeButtonSourceAsync(src.Marker, src.Address);
                }
            }
        }, CancellationToken.None);
    }

    private void OnEtwSourceBound(ulong address)
    {
        lock (_etwProbeLock) _unboundEtwAddresses.Remove(address);
    }

    private void OnEtwUnexpectedDisconnect() => RunOnUi(() =>
    {
        AppLogger.Warn("ETW button capture disconnected unexpectedly; clearing held button gestures", "MainViewModel");
        ResetGestures();
        lock (_etwProbeLock) _unboundEtwAddresses.Clear();
    });

    internal void TriggerEtwUnexpectedDisconnectForTest() => OnEtwUnexpectedDisconnect();

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action);
        else action();
    }

    private void OnChromecastTruncatedPacketDetected(ulong address) => RunOnUi(() =>
    {
        if (ActiveOnboardingVm?.SelectedDevice?.BluetoothAddress == address)
            ActiveOnboardingVm.NotifyTruncatedPacketDetected();
    });

    private void OnEtwDeviceDisconnected(ulong address) => RunOnUi(() =>
    {
        ResetDeviceGestures(address);
        if (_bluetoothManager.IsDeviceReady(address) && _chromecastButtonReceiver.IsRunning)
        {
            var source = _etwSources.FirstOrDefault(s => s.Address == address);
            if (source.Address != 0)
            {
                lock (_etwProbeLock) _unboundEtwAddresses.Add(address);
                _ = _bluetoothManager.ProbeButtonSourceAsync(source.Marker, address);
                StartProbeRetryLoop(Volatile.Read(ref _buttonSourceGeneration));
            }
        }
    });

    private void OnChromecastButtonEvent(ulong address, RemoteButton button, bool isDown)
    {
        int generation = Volatile.Read(ref _buttonSourceGeneration);
        RunOnUi(() =>
        {
            if (generation == Volatile.Read(ref _buttonSourceGeneration))
                HandleDeviceButton(address, button, isDown, InputSource.Etw);
        });
    }

    private void OnConnectionStatusChanged(ulong address, bool connected, string name) => RunOnUi(() =>
    {
        bool anyConnected = _bluetoothManager.IsConnected;
        IsConnected = anyConnected;
        var connectedAddresses = _bluetoothManager.ConnectedDeviceAddresses;
        if (!anyConnected)
        {
            ConnectedDeviceName = "未连接设备";
        }
        else if (connectedAddresses.Count > 1)
        {
            ConnectedDeviceName = $"已连接 {connectedAddresses.Count} 台遥控器";
        }
        else
        {
            ulong activeAddr = connectedAddresses.FirstOrDefault();
            var activeDev = activeAddr != 0 ? KnownRemotes().FirstOrDefault(d => d.BluetoothAddress == activeAddr) : null;
            ConnectedDeviceName = !string.IsNullOrEmpty(activeDev?.Name) ? activeDev.Name :
                (!string.IsNullOrEmpty(_bluetoothManager.ConnectedDeviceName) ? _bluetoothManager.ConnectedDeviceName :
                (connected && !string.IsNullOrEmpty(name) ? name : "已连接遥控器"));
        }

        if (!connected)
        {
            bool matchesActiveVoice = address == 0 || !anyConnected || _activeVoiceSession?.DeviceAddress == address;
            if (matchesActiveVoice)
            {
                lock (_voiceLifecycleLock)
                {
                    if (address == 0 || !anyConnected || _liveVoiceAddress == address)
                    {
                        _liveVoiceAddress = 0;
                        _liveVoiceToken = 0;
                    }
                }
                IsVoiceActive = false;
                CurrentAudioLevel = 0;
                ReleaseHeldVoiceShortcut();
            }

            if (address == 0 || !anyConnected || _voiceKeyGuardOwnerAddress == address)
            {
                _voiceKeyGuard.Clear();
                _voiceKeyGuardOwnerAddress = 0;
            }

            if (!anyConnected || address == 0)
            {
                _rawInputReceiver.ResetDeviceState();
            }
            else
            {
                ResetDeviceGestures(address);
                foreach (var handle in _rawAddresses.Where(pair => pair.Value == address).Select(pair => pair.Key).ToArray())
                {
                    _rawAddresses.Remove(handle);
                    _rawInputReceiver.ResetDeviceState(handle);
                }
            }
        }

        // The selection is the mapping editor's selection, independent of connection state.
        if (_bluetoothManager is not MultiRemoteBluetoothManager && connected)
        {
            ulong resolvedAddr = address != 0 ? address : _bluetoothManager.ConnectedDeviceAddress;
            var device = KnownRemotes().FirstOrDefault(d => d.BluetoothAddress == resolvedAddr)
                ?? _configService.Current.Devices.FirstOrDefault(d => d.Id == _configService.Current.SelectedDeviceId);
            if (device != null)
            {
                _configService.Update(cfg => cfg.SelectedDeviceId = device.Id, save: false);
                _bluetoothManager.VoiceButtonMode = device.VoiceButtonMode;
                _bluetoothManager.CurrentGain = device.Gain;
                MappingVm.SyncSelectedDeviceWithConfig();
            }
        }
        DevicesVm.Reload();
        RefreshInputSources();
    });

    private void OnAudioLevelChanged(ulong address, float level)
    {
        if (address != 0 && _bluetoothManager.VoiceDeviceAddress is { } activeOwner && activeOwner != 0 && activeOwner != address)
            return;

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

    private void OnVoiceSessionStarted(ulong address)
    {
        ulong owner = address != 0 ? address : _bluetoothManager.VoiceDeviceAddress;
        long token;
        lock (_voiceLifecycleLock)
        {
            token = ++_voiceEventSeq;
            _liveVoiceAddress = owner;
            _liveVoiceToken = token;
        }

        void Update()
        {
            lock (_voiceLifecycleLock)
            {
                if (_liveVoiceToken != token) return;
            }

            IsVoiceActive = true;
            if (_activeWarningKind == WarningKind.Audio)
            {
                _activeWarningKind = WarningKind.None;
                WarningBannerMessage = string.Empty;
                WarningBannerActionText = string.Empty;
            }
            if (ActiveOnboardingVm is { } onboarding && (onboarding.CurrentWizardStep != WizardStep.TranscriptionVerify ||
                (owner != 0 && onboarding.SelectedDevice is { BluetoothAddress: not 0 } selected && selected.BluetoothAddress != owner)))
            {
                return;
            }

            // Clean up any orphaned prior session that wasn't closed on the UI thread yet
            if (_activeVoiceSession != null)
            {
                ReleaseHeldVoiceShortcut();
            }

            UpdateVoiceKeyGuard();
            // ATVV is itself a verified remote source. Some Windows BLE HID
            // paths do not deliver a source-bearing F5 Raw Input event, so
            // relying solely on PhysicalVoiceKeyPressed leaves F5 held when
            // the IME chord is injected.
            _voiceKeyGuardOwnerAddress = owner;
            _voiceKeyGuard.Arm();
            var currentConfig = GetEffectiveInputToolConfig();
            var session = new ActiveVoiceSession(owner, token, currentConfig.TriggerMode, currentConfig.Shortcut, currentConfig.Tool);
            _activeVoiceSession = session;
            AppLogger.Info($"Voice session started: Device=0x{owner:X12}, Tool={session.Tool}, Mode={session.TriggerMode}, Shortcut='{session.Shortcut}'", "MainViewModel");
            // Submit all voice tool shortcuts via the USB/IP virtual keyboard so
            // software hooks/apps that ignore LLKHF_INJECTED still respond cleanly.
            _activeVoiceSession = session with { ShortcutSent = SendVoiceShortcut(session) };
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

    private void OnVoiceSessionEnded(ulong address)
    {
        long endedToken = 0;
        lock (_voiceLifecycleLock)
        {
            if (address == 0 || _liveVoiceAddress == address || _liveVoiceAddress == 0)
            {
                endedToken = _liveVoiceToken;
                _liveVoiceAddress = 0;
                _liveVoiceToken = 0;
            }
        }

        void Update()
        {
            if (_activeVoiceSession is { } session)
            {
                bool matchesAddress = address == 0 || session.DeviceAddress == 0 || session.DeviceAddress == address;
                bool matchesToken = endedToken == 0 || session.Token == endedToken;
                if (!matchesAddress || !matchesToken)
                {
                    AppLogger.Debug($"Ignoring stale VoiceSessionEnded for 0x{address:X12} (active=0x{session.DeviceAddress:X12}, token={session.Token}, endedToken={endedToken})", "MainViewModel");
                    return;
                }

                _voiceKeyGuard.Clear();
                _voiceKeyGuardOwnerAddress = 0;
                IsVoiceActive = false;
                CurrentAudioLevel = 0f;
                _activeVoiceSession = null;
                AppLogger.Info($"Voice session ended: Device=0x{session.DeviceAddress:X12}, Mode={session.TriggerMode}, Shortcut='{session.Shortcut}'", "MainViewModel");
                EndVoiceShortcut(session);
            }
            else if (_liveVoiceToken == 0)
            {
                _voiceKeyGuard.Clear();
                _voiceKeyGuardOwnerAddress = 0;
                IsVoiceActive = false;
                CurrentAudioLevel = 0f;
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

    private void OnVoiceButtonPressed(ulong address)
    {
        ulong resolved = address != 0 ? address :
            (_bluetoothManager.VoiceDeviceAddress != 0 ? _bluetoothManager.VoiceDeviceAddress : _bluetoothManager.ConnectedDeviceAddress);
        RunOnUi(() =>
        {
            var device = KnownRemotes().FirstOrDefault(d => d.BluetoothAddress == resolved);
            if (ActiveOnboardingVm is { CurrentWizardStep: WizardStep.ButtonTest } onboarding &&
                (onboarding.SelectedDevice?.BluetoothAddress == resolved || (resolved == 0 && onboarding.SelectedDevice != null)))
                onboarding.NotifyButtonPressed(RemoteButton.Voice);
            else if (ActiveOnboardingVm == null && device != null)
                ShowMappingPress(RemoteButton.Voice, device);
        });
    }

    partial void OnActivePageNameChanged(string value)
    {
        _ = AboutVm.SetUpdatePageActiveAsync(value == "About");
        ResetGestures();
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
        string shortcut = GetEffectiveInputToolConfig().Shortcut ?? string.Empty;
        bool usesF5 = NativeInputSender.TryParseShortcut(shortcut, out _, out var key) && key == 0x74;
        _voiceKeyGuard.IsSuppressionEnabled = !usesF5;
        AppLogger.Debug($"VoiceKeyGuard updated: SuppressionEnabled={_voiceKeyGuard.IsSuppressionEnabled} (Shortcut='{shortcut}')", "MainViewModel");
    }

    private IntPtr _registeredHwnd = IntPtr.Zero;

    public void RegisterRawInput(IntPtr hwnd)
    {
        _registeredHwnd = hwnd;
        if (!_voiceKeyGuardInstalled)
        {
            bool installed = _voiceKeyGuard.Install();
            _voiceKeyGuardInstalled = installed;
            AppLogger.Info($"Remote voice F5 guard installed: {installed}", "MainViewModel");
        }
        _rawInputReceiver.Register(hwnd);
    }
    public void ProcessRawInput(IntPtr lParam) => _rawInputReceiver.ProcessMessage(lParam);
    internal void HandleRawButtonEvent(IntPtr hDevice, RemoteButton button, bool isDown) => OnRawButtonEvent(hDevice, button, isDown);

    private RemoteDevice? ResolveRawSource(IntPtr handle, string path) =>
        KnownRemotes().FirstOrDefault(device => _bluetoothManager.IsDeviceReady(device.BluetoothAddress) &&
            RawInputReceiver.EvaluateDeviceMatch(_bluetoothManager.GetDeviceContainerId(device.BluetoothAddress) ?? device.ContainerId,
                handle, path, device.BluetoothAddress, requireExactIdentity: true));

    private RemoteModelType ResolveRawModelType(IntPtr handle, string path)
    {
        if (_rawAddresses.TryGetValue(handle, out ulong address) &&
            KnownRemotes().FirstOrDefault(device => device.BluetoothAddress == address) is { } known)
            return known.ModelType;
        return ResolveRawSource(handle, path)?.ModelType ?? RemoteModelType.Unknown;
    }

    private bool IsTargetDevice(IntPtr handle, string path)
    {
        if (_rawAddresses.TryGetValue(handle, out ulong address) &&
            _bluetoothManager.IsDeviceReady(address)) return true;
        if (_nonTargetRawHandles.Contains(handle)) return false;
        var device = ResolveRawSource(handle, path);
        if (device == null)
        {
            _nonTargetRawHandles.Add(handle);
            return false;
        }
        _rawAddresses[handle] = device.BluetoothAddress;
        return true;
    }

    private void OnRawVoiceKey(IntPtr handle)
    {
        ulong source = _rawAddresses.GetValueOrDefault(handle);
        ulong owner = _bluetoothManager.VoiceDeviceAddress;
        if (source != 0 && (owner == 0 || source == owner))
        {
            _voiceKeyGuardOwnerAddress = source;
            _voiceKeyGuard.Arm();
        }
    }

    private void OnRawButtonEvent(IntPtr handle, RemoteButton button, bool isDown)
    {
        ulong address = handle == IntPtr.Zero ? _bluetoothManager.ConnectedDeviceAddress :
            _rawAddresses.GetValueOrDefault(handle);
        HandleDeviceButton(address, button, isDown, InputSource.RawInput);
    }

    internal void HandleDeviceButton(ulong address, RemoteButton button, bool isDown, InputSource source = InputSource.Direct)
    {
        if (!_bluetoothManager.IsDeviceReady(address)) return;
        var sourceDevice = KnownRemotes().FirstOrDefault(device => device.BluetoothAddress == address);
        if (sourceDevice == null) return;
        if (ActiveOnboardingVm is { } onboarding)
        {
            if (onboarding.SelectedDevice?.BluetoothAddress != address) return;
            if (isDown) onboarding.NotifyButtonPressed(button);
            if (button != RemoteButton.Voice || onboarding.CurrentWizardStep is not
                (WizardStep.AudioTest or WizardStep.TranscriptionVerify)) return;
        }

        long now = Environment.TickCount64;
        if (!_dedup.TryGetValue(address, out var deviceDedup))
        {
            deviceDedup = new Dictionary<(RemoteButton, bool), (long, InputSource)>();
            _dedup[address] = deviceDedup;
        }
        bool wasDown = _buttonLogicalDown.GetValueOrDefault((address, button), false);
        if (deviceDedup.TryGetValue((button, isDown), out var last))
        {
            long elapsed = now - last.Time;
            if (last.Source != source && elapsed < 150)
            {
                return;
            }
            if (last.Source == source && wasDown == isDown && elapsed < 60)
            {
                return;
            }
        }
        deviceDedup[(button, isDown)] = (now, source);
        _buttonLogicalDown[(address, button)] = isDown;
        if (ActiveOnboardingVm == null && isDown) ShowMappingPress(button, sourceDevice);

        if (button == RemoteButton.Voice)
        {
            ulong owner = _bluetoothManager.VoiceDeviceAddress;
            if (owner != 0 && owner != address) return;
            if (GetEffectiveInputToolConfig().TriggerMode == VoiceTriggerMode.HoldToTalk)
            {
                if (isDown) _ = _bluetoothManager.StartVoiceSessionAsync(address);
                else _ = _bluetoothManager.StopVoiceSessionAsync(address);
            }
            else if (isDown)
            {
                if (_bluetoothManager.IsStreaming || _bluetoothManager.SessionState == BluetoothSessionState.Opening)
                    _ = _bluetoothManager.StopVoiceSessionAsync(address);
                else
                    _ = _bluetoothManager.StartVoiceSessionAsync(address);
            }
            return;
        }

        if (IsMappingPreviewSuppressed())
        {
            if (!isDown && _gestures.TryGetValue(address, out var activeGestures))
            {
                activeGestures.Reset();
            }
            return;
        }

        if (!MappingVm.CustomMappingEnabled) return;
        if (!_gestures.TryGetValue(address, out var gestures))
        {
            if (!isDown) return;
            gestures = new RemoteButtonGestures(ScheduleButtonGesture, (btn, assignment) =>
            {
                if (_bluetoothManager.IsDeviceReady(address))
                    ExecuteMappedButton(address, btn, assignment);
            });
            _gestures[address] = gestures;
        }
        if (!isDown) gestures.HandleUp(button);
        else if (sourceDevice.Mappings?.FirstOrDefault(mapping => mapping.Button == button) is { } mapping)
            gestures.HandleDown(mapping);
    }

    private void ExecuteMappedButton(ulong address, RemoteButton button, ButtonAssignment assignment)
    {
        if (IsMappingPreviewSuppressed()) return;

        var dev = KnownRemotes().FirstOrDefault(d => d.BluetoothAddress == address);
        if (dev != null && IsNativeKeyboardRemote(dev.ModelType) &&
            assignment.Action == ButtonActionType.KeyboardShortcut &&
            IsIdentityKeyboardShortcut(button, assignment.Shortcut))
        {
            AppLogger.Debug($"Skipping duplicate identity shortcut '{assignment.Shortcut}' for native keyboard remote {dev.ModelType} {button}", "MainViewModel");
            return;
        }

        _executeMapping(assignment);
    }

    private static bool IsNativeKeyboardRemote(RemoteModelType type) =>
        type is RemoteModelType.XiaomiRC003 or RemoteModelType.XiaomiRC001 or RemoteModelType.T1Remote;

    private static bool IsIdentityKeyboardShortcut(RemoteButton button, string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        return button switch
        {
            RemoteButton.Up => shortcut.Equals("Up", StringComparison.OrdinalIgnoreCase),
            RemoteButton.Down => shortcut.Equals("Down", StringComparison.OrdinalIgnoreCase),
            RemoteButton.Left => shortcut.Equals("Left", StringComparison.OrdinalIgnoreCase),
            RemoteButton.Right => shortcut.Equals("Right", StringComparison.OrdinalIgnoreCase),
            RemoteButton.Ok => shortcut.Equals("Enter", StringComparison.OrdinalIgnoreCase) ||
                               shortcut.Equals("Return", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
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
            EndVoiceShortcut(session);
        }
        _voiceKeyGuard.EndShortcutHold();
        _voiceKeyGuardOwnerAddress = 0;
        NativeInputSender.ReleaseAllHeldSyntheticKeys();
    }

    private bool SendVoiceShortcut(ActiveVoiceSession session)
    {
        bool administrator = IsAdministratorForTest();
        bool sent = administrator && (session.TriggerMode == VoiceTriggerMode.HoldToTalk
            ? _usbKeyboard.SendDown(session.Shortcut)
            : _usbKeyboard.SendClick(session.Shortcut));
        AppLogger.Info($"USB/IP voice shortcut start: Tool={session.Tool}, Mode={session.TriggerMode}, Shortcut='{session.Shortcut}', Admin={administrator}, Success={sent}", "MainViewModel");
        if (!sent)
        {
            _activeWarningKind = WarningKind.UsbKeyboard;
            WarningBannerMessage = administrator
                ? "USB/IP 虚拟键盘未能发送语音快捷键。请检查驱动状态或快捷键格式，然后重试。"
                : "发送语音快捷键需要管理员权限来运行 USB/IP 虚拟键盘。";
            WarningBannerActionText = administrator ? string.Empty : "以管理员身份重启";
        }
        else if (_activeWarningKind == WarningKind.UsbKeyboard)
        {
            DismissWarningBanner();
        }
        return sent;
    }

    private void EndVoiceShortcut(ActiveVoiceSession session)
    {
        if (!session.ShortcutSent) return;
        bool sent = session.TriggerMode == VoiceTriggerMode.HoldToTalk
            ? _usbKeyboard.Release()
            : _usbKeyboard.SendClick(session.Shortcut);
        AppLogger.Info($"USB/IP voice shortcut end: Tool={session.Tool}, Mode={session.TriggerMode}, Shortcut='{session.Shortcut}', Success={sent}", "MainViewModel");
    }

    internal Func<IntPtr, string?> RawDevicePathResolverForTest { get; set; } = RawInputReceiver.GetDevicePath;

    public void HandleDeviceArrival(IntPtr hDevice)
    {
        AppLogger.Info($"Device arrival notification received for hDevice=0x{hDevice:X}", "MainViewModel");
        _nonTargetRawHandles.Remove(hDevice);
        _rawAddresses.Remove(hDevice);
        _rawInputReceiver.InvalidateDeviceCache(hDevice);
        var (shouldNotify, targetAddress) = ClassifyHidArrival(hDevice, RawDevicePathResolverForTest(hDevice));
        if (shouldNotify)
        {
            _bluetoothManager.NotifyHidDeviceArrival(targetAddress);
        }
    }

    internal (bool ShouldNotify, ulong? TargetAddress) ClassifyHidArrival(IntPtr hDevice, string? devicePath)
    {
        if (hDevice == IntPtr.Zero || string.IsNullOrWhiteSpace(devicePath))
        {
            return (false, null);
        }

        var remotes = KnownRemotes().DistinctBy(d => d.BluetoothAddress).ToArray();
        if (remotes.Length == 0)
        {
            return (false, null);
        }

        foreach (var remote in remotes)
        {
            Guid? containerId = _bluetoothManager.GetDeviceContainerId(remote.BluetoothAddress) ?? remote.ContainerId;
            if (RawInputReceiver.EvaluateDeviceMatch(containerId, hDevice, devicePath, remote.BluetoothAddress, requireExactIdentity: true))
            {
                return (true, remote.BluetoothAddress);
            }
        }

        if (RawInputReceiver.IsKnownRemoteDevice(hDevice, devicePath))
        {
            var inferredModel = RawInputReceiver.InferModelTypeFromDevice(hDevice, devicePath);
            if (inferredModel != RemoteModelType.Unknown)
            {
                var modelMatches = remotes.Where(r => r.ModelType == inferredModel).ToArray();
                if (modelMatches.Length == 1)
                {
                    return (true, modelMatches[0].BluetoothAddress);
                }
            }
            return (true, null);
        }

        return (false, null);
    }

    public void HandleSystemResume()
    {
        AppLogger.Info("System resume notification received; refreshing Bluetooth remote connections", "MainViewModel");
        _bluetoothManager.NotifySystemResume();
    }

    internal WindowsDiagnosticSnapshot BuildDiagnosticSnapshot()
    {
        var connectedAddresses = _bluetoothManager.ConnectedDeviceAddresses;
        var known = KnownRemotes().DistinctBy(d => d.BluetoothAddress).ToDictionary(d => d.BluetoothAddress);
        var modelDescriptions = new List<string>();

        foreach (ulong addr in connectedAddresses)
        {
            var modelType = known.TryGetValue(addr, out var dev) ? dev.ModelType.ToString() : "Unknown";
            var hwInfo = _bluetoothManager.GetDeviceInformation(addr);
            string hwDetail = !string.IsNullOrWhiteSpace(hwInfo?.ModelNumber)
                ? $"/{hwInfo.ModelNumber}"
                : string.Empty;
            string fwDetail = !string.IsNullOrWhiteSpace(hwInfo?.FirmwareRevision)
                ? $" fw={hwInfo.FirmwareRevision}"
                : string.Empty;
            modelDescriptions.Add($"{modelType}{hwDetail}{fwDetail}");
        }

        if (modelDescriptions.Count == 0 &&
            _configService.Current.Devices.FirstOrDefault(d => d.Id == _configService.Current.SelectedDeviceId) is { } selected)
        {
            modelDescriptions.Add($"{selected.ModelType} (selected)");
        }

        string remoteModelSummary = modelDescriptions.Count > 0 ? string.Join(", ", modelDescriptions) : "none";
        string logPath = AppLogger.CurrentLogFilePath;
        bool logExists = System.IO.File.Exists(logPath);
        long? logSize = null;
        if (logExists)
        {
            try { logSize = new System.IO.FileInfo(logPath).Length; } catch { }
        }

        var driverStatus = Platform.Windows.Audio.VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true);
        var cableOutputStatus = Platform.Windows.Audio.VirtualAudioAuditor.GetCableOutputStatus();
        EnvironmentVm.DriverStatus = driverStatus;
        EnvironmentVm.CableOutputStatus = cableOutputStatus;

        var input = _configService.Current.InputTool;
        return new WindowsDiagnosticSnapshot(
            AppVersion: AboutVm.AppVersion,
            OsVersion: Environment.OSVersion.VersionString,
            Architecture: AboutVm.Architecture,
            IsAdministrator: BluetoothAdapterHelper.IsAdministrator(),
            RemoteConnected: _bluetoothManager.IsConnected,
            ConnectedRemoteCount: connectedAddresses.Count,
            RemoteModel: remoteModelSummary,
            VirtualMicrophoneStatus: driverStatus.ToString(),
            CableOutputStatus: cableOutputStatus.ToString(),
            EtwCaptureStatus: EtwCaptureStatus.ToString(),
            VoiceTool: input.Tool.ToString(),
            VoiceTriggerMode: input.TriggerMode.ToString(),
            VoiceShortcutName: string.IsNullOrWhiteSpace(input.Shortcut) ? "unknown" : input.Shortcut,
            PreferredAudioInput: string.IsNullOrWhiteSpace(input.PreferredAudioInputDevice) ? "default" : input.PreferredAudioInputDevice,
            RuntimeLogEnabled: AboutVm.DiagnosticLoggingEnabled,
            RuntimeLogAvailable: logExists,
            RuntimeLogPath: logPath,
            RuntimeLogSizeBytes: logSize);
    }

    public void HandleDeviceRemoval(IntPtr hDevice)
    {
        AppLogger.Info($"Device removal notification received for hDevice=0x{hDevice:X}", "MainViewModel");
        _nonTargetRawHandles.Remove(hDevice);
        _rawInputReceiver.ResetDeviceState(hDevice);
        if (_rawAddresses.Remove(hDevice, out ulong address)) ResetDeviceGestures(address);
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
        if (args.PropertyName == nameof(MappingViewModel.CustomMappingEnabled)) ResetGestures();
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
        _bluetoothManager.AudioOutputFailed -= OnAudioOutputFailed;
        _rawInputReceiver.ButtonEvent -= OnRawButtonEvent;
        _rawInputReceiver.PhysicalVoiceKeyPressed -= OnRawVoiceKey;
        foreach (var reader in _hidReaders.Values) reader.Dispose();
        _hidReaders.Clear();
        _chromecastButtonReceiver.ButtonEvent -= OnChromecastButtonEvent;
        _chromecastButtonReceiver.TruncatedPacketDetected -= OnChromecastTruncatedPacketDetected;
        _chromecastButtonReceiver.DeviceDisconnected -= OnEtwDeviceDisconnected;
        _chromecastButtonReceiver.SourceBound -= OnEtwSourceBound;
        _chromecastButtonReceiver.UnexpectedDisconnect -= OnEtwUnexpectedDisconnect;
        _chromecastButtonReceiver.StatusChanged -= OnEtwStatusChanged;
        CancelEtwTasks();
        _chromecastButtonReceiver.Dispose();

        ReleaseHeldVoiceShortcut();
        _usbKeyboard.Dispose();
        _voiceKeyGuard.Dispose();
        ResetGestures();
        GeneralVm.PreferencesChanged -= OnGeneralPreferencesChanged;
        SettingsVm.PreferencesChanged -= OnSettingsPreferencesChanged;
        AboutVm.ResetUpdateCheck();
        AboutVm.PreferencesChanged -= OnAboutPreferencesChanged;
        if (_activeOnboardingVm != null)
        {
            _activeOnboardingVm.PropertyChanged -= OnOnboardingPropertyChanged;
            _activeOnboardingVm = null;
        }
        GeneralVm.Dispose();
        MappingVm.PropertyChanged -= OnMappingSettingsChanged;
        DevicesVm.DevicesChanged -= OnDevicesChanged;
        DevicesVm.Dispose();
        _rawInputReceiver.Dispose();
    }

    private void OnGeneralPreferencesChanged() => SettingsVm.SyncFromConfig();

    private void OnSettingsPreferencesChanged()
    {
        GeneralVm.SyncFromConfig();
        AboutVm.SyncFromConfig();
    }

    private void OnAboutPreferencesChanged() => SettingsVm.SyncFromConfig();

    internal void ReleaseVoiceKeysOnCrash()
    {
        try { ReleaseHeldVoiceShortcut(); } catch { }
        try { _voiceKeyGuard.Clear(); } catch { }
    }
}
