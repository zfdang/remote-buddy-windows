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
    private record struct ActiveVoiceSession(ulong DeviceAddress, long Token, VoiceTriggerMode TriggerMode, string Shortcut, TargetInputTool Tool);

    private readonly ConfigService _configService;
    private readonly IBluetoothManager _bluetoothManager;
    private readonly RawInputReceiver _rawInputReceiver = new();
    private readonly Dictionary<ulong, HidDeviceReader> _hidReaders = new();
    private readonly ChromecastButtonReceiver _chromecastButtonReceiver = new();
    private readonly RemoteVoiceKeyGuard _voiceKeyGuard;
    private readonly object _voiceLifecycleLock = new();
    private ActiveVoiceSession? _activeVoiceSession;
    private long _voiceEventSeq;
    private ulong _liveVoiceAddress;
    private long _liveVoiceToken;
    private ulong _voiceKeyGuardOwnerAddress;

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
            ResetGestures();
            RefreshInputSources();
        }
    }

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
        MappingVm = new MappingViewModel(_configService);
        DevicesVm.DevicesChanged += RefreshInputSources;
        MappingVm.PropertyChanged += OnMappingSettingsChanged;
        IntegrationsVm = new IntegrationsViewModel(_configService, mode => RequestOpenWizard?.Invoke(mode));
        EnvironmentVm = new EnvironmentViewModel();
        AppIntegrationVm = new AppIntegrationViewModel(_configService);
        AboutVm = new AboutViewModel();
        SettingsVm = new SettingsViewModel(_configService, mode => RequestOpenWizard?.Invoke(mode));
        UpdateVoiceKeyGuard();

        _rawInputReceiver.DeviceFilter = IsTargetDevice;
        _rawInputReceiver.DeviceModelResolver = ResolveRawModelType;

        _bluetoothManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        _bluetoothManager.AudioLevelChanged += OnAudioLevelChanged;
        _bluetoothManager.VoiceSessionStarted += OnVoiceSessionStarted;
        _bluetoothManager.VoiceSessionEnded += OnVoiceSessionEnded;
        _bluetoothManager.VoiceButtonPressed += OnVoiceButtonPressed;
        _rawInputReceiver.ButtonEvent += OnRawButtonEvent;
        _chromecastButtonReceiver.ButtonEvent += OnChromecastButtonEvent;
        _chromecastButtonReceiver.TruncatedPacketDetected += OnChromecastTruncatedPacketDetected;
        _chromecastButtonReceiver.DeviceDisconnected += OnEtwDeviceDisconnected;
        _chromecastButtonReceiver.SourceBound += OnEtwSourceBound;
        _chromecastButtonReceiver.UnexpectedDisconnect += OnEtwUnexpectedDisconnect;
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

        var wanted = devices.Select(device => (device.BluetoothAddress, Protocol: device.IsG9N9N ||
            G9N9NProtocol.IsCandidate(device.Name) ? EtwProtocolKind.GoogleG9N9N :
            device.IsT1 ? EtwProtocolKind.T1Remote : EtwProtocolKind.Xiaomi)).ToArray();
        bool rebuild = !_etwSources.Select(source => (source.Address, source.Protocol)).SequenceEqual(wanted) ||
            !_chromecastButtonReceiver.IsRunning;
        if (rebuild && ready.Length > 0)
        {
            int generation = Interlocked.Increment(ref _buttonSourceGeneration);
            CancelEtwTasks();
            _chromecastButtonReceiver.Stop();
            ResetGestures();
            _etwSources = wanted.Select(source => new EtwButtonSource(source.BluetoothAddress, Guid.NewGuid(), source.Protocol)).ToArray();
            lock (_etwProbeLock)
            {
                _unboundEtwAddresses.Clear();
                foreach (ulong addr in addresses) _unboundEtwAddresses.Add(addr);
            }

            if (!_etwElevationDeclined)
            {
                _etwStartCts = new CancellationTokenSource();
                var startToken = _etwStartCts.Token;
                _ = StartAndProbeEtwAsync(_etwSources, generation, startToken);
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
                var source = _etwSources.FirstOrDefault(entry => entry.Address == address);
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
        try { _etwStartCts?.Cancel(); } catch { }
        _etwStartCts?.Dispose();
        _etwStartCts = null;

        lock (_etwProbeLock)
        {
            try { _probeRetryCts?.Cancel(); } catch { }
            _probeRetryCts?.Dispose();
            _probeRetryCts = null;
        }
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
        lock (_etwProbeLock)
        {
            try { _probeRetryCts?.Cancel(); } catch { }
            _probeRetryCts?.Dispose();
            _probeRetryCts = new CancellationTokenSource();
            token = _probeRetryCts.Token;
        }

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
                lock (_etwProbeLock)
                {
                    if (_unboundEtwAddresses.Count == 0) return;
                    remaining = _etwSources
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
                _configService.Current.SelectedDeviceId = device.Id;
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
            var currentConfig = _configService.Current.InputTool;
            var session = new ActiveVoiceSession(owner, token, currentConfig.TriggerMode, currentConfig.Shortcut, currentConfig.Tool);
            _activeVoiceSession = session;
            AppLogger.Info($"Voice session started: Device=0x{owner:X12}, Tool={session.Tool}, Mode={session.TriggerMode}, Shortcut='{session.Shortcut}'", "MainViewModel");
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
                if (session.TriggerMode == VoiceTriggerMode.HoldToTalk)
                {
                    NativeInputSender.SendShortcutUp(session.Shortcut);
                }
                else if (session.TriggerMode == VoiceTriggerMode.ClickToToggle)
                {
                    NativeInputSender.SendShortcutClick(session.Shortcut);
                }
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
            if (_configService.Current.InputTool.TriggerMode == VoiceTriggerMode.HoldToTalk)
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
        _voiceKeyGuardOwnerAddress = 0;
        NativeInputSender.ReleaseAllHeldSyntheticKeys();
    }

    public void HandleDeviceArrival(IntPtr hDevice)
    {
        AppLogger.Info($"Device arrival notification received for hDevice=0x{hDevice:X}", "MainViewModel");
        _nonTargetRawHandles.Remove(hDevice);
        _rawAddresses.Remove(hDevice);
        _rawInputReceiver.InvalidateDeviceCache(hDevice);
    }

    public void HandleDeviceRemoval(IntPtr hDevice)
    {
        AppLogger.Info($"Device removal notification received for hDevice=0x{hDevice:X}", "MainViewModel");
        _nonTargetRawHandles.Remove(hDevice);
        _rawInputReceiver.InvalidateDeviceCache(hDevice);
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
        _rawInputReceiver.ButtonEvent -= OnRawButtonEvent;
        _rawInputReceiver.PhysicalVoiceKeyPressed -= OnRawVoiceKey;
        foreach (var reader in _hidReaders.Values) reader.Dispose();
        _hidReaders.Clear();
        _chromecastButtonReceiver.ButtonEvent -= OnChromecastButtonEvent;
        _chromecastButtonReceiver.TruncatedPacketDetected -= OnChromecastTruncatedPacketDetected;
        _chromecastButtonReceiver.DeviceDisconnected -= OnEtwDeviceDisconnected;
        _chromecastButtonReceiver.SourceBound -= OnEtwSourceBound;
        _chromecastButtonReceiver.UnexpectedDisconnect -= OnEtwUnexpectedDisconnect;
        CancelEtwTasks();
        _chromecastButtonReceiver.Dispose();

        ReleaseHeldVoiceShortcut();
        _voiceKeyGuard.Dispose();
        ResetGestures();
        MappingVm.PropertyChanged -= OnMappingSettingsChanged;
        DevicesVm.DevicesChanged -= RefreshInputSources;
        DevicesVm.Dispose();
        _rawInputReceiver.Dispose();
    }
}
