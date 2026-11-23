// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using RemoteBuddy.Core.Audio;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;
using RemoteBuddy.Platform.Windows.Audio;

namespace RemoteBuddy.Platform.Windows.Bluetooth;

public sealed class WinRtBluetoothManager : IBluetoothManager
{
    private static readonly Guid DeviceInformationServiceUuid = new("0000180A-0000-1000-8000-00805F9B34FB");
    private static readonly Guid ModelNumberUuid = new("00002A24-0000-1000-8000-00805F9B34FB");
    private static readonly Guid FirmwareRevisionUuid = new("00002A26-0000-1000-8000-00805F9B34FB");
    private static readonly Guid PnpIdUuid = new("00002A50-0000-1000-8000-00805F9B34FB");

    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private BluetoothLEDevice? _connectedDevice;
    private GattDeviceService? _gattService;
    private GattCharacteristic? _txCharacteristic;
    private GattCharacteristic? _audioCharacteristic;
    private GattCharacteristic? _controlCharacteristic;

    private readonly ImaAdpcmDecoder _decoder = new();
    private readonly WasapiAudioPlayer _audioPlayer = new();
    private readonly ATVVFrameAccumulator _frameAccumulator = new();
    private readonly object _audioLock = new();
    private readonly PcmPostprocessor _postprocessor = new();
    private short[] _decodedBuffer = new short[1024];

    private byte _currentSessionId = 0;
    private byte? _lastEndedSessionId;
    private long _lastEndedSessionTimeMs;
    private int _frameSize = 120;
    private volatile BluetoothSessionState _sessionState = BluetoothSessionState.Idle;
    private bool _cancelOpenPending = false;
    private bool _voiceSessionStartedEmitted = false;
    private bool _audioStopReceived = false;
    private int _connectionGeneration = 0;
    private int _sessionGeneration = 0;
    private int _audioGeneration = 0;
    private int _activeOpeningOperationId = 0;
    private long _deviceAddress = 0;
    private int _disposed = 0;
    private TaskCompletionSource<ATVVCapabilities?>? _capabilitiesTcs;
    private TaskCompletionSource<bool>? _audioStopTcs;
    private System.Threading.Timer? _keepaliveTimer;
    private readonly G9N9NSmartToggleSession _smartToggle = new();
    private System.Threading.Timer? _smartToggleReopenTimer;
    private System.Threading.Timer? _smartToggleDeadlineTimer;
    public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;
    private readonly object _stateLock = new();

    public event Action<RemoteDevice>? DeviceDiscovered;
    public event Action<ulong, bool, string>? ConnectionStatusChanged;
    public event Action<ulong>? ConnectedDeviceInformationChanged;
    public event Action<ulong, float>? AudioLevelChanged;
    public event Action<ulong>? VoiceSessionStarted;
    public event Action<ulong>? VoiceSessionEnded;
    public event Action<ulong>? VoiceButtonPressed;
    public event Action<ulong, string>? AudioOutputFailed;

    private ulong CurrentEventAddress => _connectedDevice?.BluetoothAddress ?? (ulong)Volatile.Read(ref _deviceAddress);

    public bool IsScanning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public bool IsConnected => _connectedDevice != null && _connectedDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public bool IsReady { get; private set; }
    public bool IsStreaming => _sessionState == BluetoothSessionState.Streaming;
    public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
    public BluetoothSessionState SessionState => _sessionState;
    internal string? ConnectedDeviceNameOverride { get; set; }
    public string ConnectedDeviceName => ConnectedDeviceNameOverride ?? _connectedDevice?.Name ?? string.Empty;
    public ulong ConnectedDeviceAddress => _connectedDevice?.BluetoothAddress ?? 0;
    public Guid? ConnectedDeviceContainerId { get; private set; }
    public RemoteDeviceInformation? ConnectedDeviceInformation { get; private set; }

    internal async Task ProbeButtonSourceAsync(Guid marker)
    {
        BluetoothLEDevice? device;
        lock (_stateLock) device = _connectedDevice;
        if (device == null) return;

        try
        {
            // Give ETW trace session 150ms to attach to driver buffers
            await Task.Delay(150).ConfigureAwait(false);
            var result = await device.GetGattServicesForUuidAsync(marker, BluetoothCacheMode.Uncached);
            foreach (var service in result.Services) service.Dispose();
            AppLogger.Info($"Chromecast button source query finished: {result.Status}", "BluetoothManager");

            if (result.Status != GattCommunicationStatus.Success)
            {
                await Task.Delay(250).ConfigureAwait(false);
                var retryResult = await device.GetGattServicesForUuidAsync(marker, BluetoothCacheMode.Uncached);
                foreach (var service in retryResult.Services) service.Dispose();
                AppLogger.Info($"Chromecast button source query retry: {retryResult.Status}", "BluetoothManager");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info($"Chromecast button source query failed: {ex.Message}", "BluetoothManager");
        }
    }

    public void StartScan() => StartScanning();
    public void StopScan() => StopScanning();

    private readonly VoiceSessionArbiter? _voiceArbiter;

    public WinRtBluetoothManager() : this(null) { }

    internal WinRtBluetoothManager(VoiceSessionArbiter? voiceArbiter)
    {
        _voiceArbiter = voiceArbiter;
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += OnAdvertisementReceived;
    }

    public void StartScanning()
    {
        try
        {
            AppLogger.Info("Starting Bluetooth LE advertisement scan...", "BluetoothManager");
            _watcher.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to start Bluetooth LE scan", ex, "BluetoothManager");
        }
    }

    public async Task DiscoverPairedRemotesAsync()
    {
        try
        {
            var devices = await global::Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
            foreach (var info in devices)
            {
                if (!XiaomiProtocol.IsCandidate(info.Name) && !G9N9NProtocol.IsCandidate(info.Name) && !T1RemoteProtocol.IsCandidate(info.Name)) continue;
                using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device == null) continue;
                AppLogger.Info($"Found paired remote: '{info.Name}' (0x{device.BluetoothAddress:X12}), OS status={device.ConnectionStatus}", "BluetoothManager");
                var modelType = XiaomiProtocol.DetectModelType(info.Name);
                DeviceDiscovered?.Invoke(new RemoteDevice
                {
                    Id = device.BluetoothAddress.ToString("X"),
                    BluetoothAddress = device.BluetoothAddress,
                    Name = info.Name,
                    ModelType = modelType,
                    Mappings = RemoteDevice.CreateDefaultMappings(modelType)
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to enumerate paired remotes", ex, "BluetoothManager");
        }
    }

    public void StopScanning()
    {
        try
        {
            AppLogger.Info("Stopping Bluetooth LE advertisement scan...", "BluetoothManager");
            _watcher.Stop();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to stop Bluetooth LE scan", ex, "BluetoothManager");
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        string name = args.Advertisement.LocalName;
        if (string.IsNullOrWhiteSpace(name)) return;

        if (XiaomiProtocol.IsCandidate(name) || G9N9NProtocol.IsCandidate(name) || T1RemoteProtocol.IsCandidate(name))
        {
            AppLogger.Info($"Discovered candidate remote: '{name}' (Address: 0x{args.BluetoothAddress:X12}, RSSI: {args.RawSignalStrengthInDBm} dBm)", "BluetoothManager");
            var modelType = XiaomiProtocol.DetectModelType(name);
            var device = new RemoteDevice
            {
                Id = args.BluetoothAddress.ToString("X"),
                BluetoothAddress = args.BluetoothAddress,
                Name = name,
                ModelType = modelType,
                Mappings = RemoteDevice.CreateDefaultMappings(modelType),
                IsConnected = false
            };
            DeviceDiscovered?.Invoke(device);
        }
    }

    public async Task<bool> ConnectAsync(ulong bluetoothAddress)
    {
        AppLogger.Info($"ConnectAsync initiated for BluetoothAddress: 0x{bluetoothAddress:X12}", "BluetoothManager");
        Disconnect();

        int targetConnGen;
        lock (_stateLock)
        {
            targetConnGen = ++_connectionGeneration;
            _activeOpeningOperationId++;
            Volatile.Write(ref _deviceAddress, (long)bluetoothAddress);
        }

        void CleanupAttempt(BluetoothLEDevice? localDev)
        {
            localDev?.Dispose();
            bool shouldDisconnect = false;
            lock (_stateLock)
            {
                if (targetConnGen == _connectionGeneration)
                {
                    if (_gattService != null)
                    {
                        try { _gattService.Dispose(); } catch { }
                        _gattService = null;
                    }
                    shouldDisconnect = true;
                }
            }

            if (shouldDisconnect)
            {
                Disconnect();
            }
        }

        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    device?.Dispose();
                    return false;
                }

                if (device == null)
                {
                    AppLogger.Error($"BluetoothLEDevice.FromBluetoothAddressAsync returned null for 0x{bluetoothAddress:X12}", component: "BluetoothManager");
                    CleanupAttempt(null);
                    ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "无法获取蓝牙设备");
                    return false;
                }

                _connectedDevice = device;
                _connectedDevice.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;
            }

            AppLogger.Info($"Device object obtained: '{device.Name}' ({device.DeviceId}), status: {device.ConnectionStatus}", "BluetoothManager");

            // Discover Service
            var servicesResult = await device.GetGattServicesForUuidAsync(ATVVProtocol.ServiceUuid);
            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    device.Dispose();
                    return false;
                }

                if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                {
                    AppLogger.Error($"Failed to discover ATVV GATT service {ATVVProtocol.ServiceUuid}: Status={servicesResult.Status}, Count={servicesResult.Services.Count}", component: "BluetoothManager");
                    CleanupAttempt(device);
                    ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "未找到语音服务 (ATVV)");
                    return false;
                }
            }

            var service = servicesResult.Services[0];
            var charsResult = await service.GetCharacteristicsAsync();
            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    device.Dispose();
                    return false;
                }

                if (charsResult.Status != GattCommunicationStatus.Success)
                {
                    AppLogger.Error($"Failed to discover ATVV characteristics: Status={charsResult.Status}", component: "BluetoothManager");
                    CleanupAttempt(device);
                    ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "获取服务特征失败");
                    return false;
                }
            }

            GattCharacteristic? tx = null;
            GattCharacteristic? audio = null;
            GattCharacteristic? control = null;

            foreach (var ch in charsResult.Characteristics)
            {
                if (ch.Uuid == ATVVProtocol.TransmitUuid) tx = ch;
                else if (ch.Uuid == ATVVProtocol.AudioUuid) audio = ch;
                else if (ch.Uuid == ATVVProtocol.ControlUuid) control = ch;
            }

            if (tx == null || audio == null || control == null)
            {
                AppLogger.Error($"Missing required ATVV characteristics: TX={(tx != null)}, Audio={(audio != null)}, Control={(control != null)}", component: "BluetoothManager");
                lock (_stateLock)
                {
                    if (targetConnGen == _connectionGeneration)
                    {
                        CleanupAttempt(device);
                        ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "缺少必要的 ATVV 特征");
                    }
                    else
                    {
                        device.Dispose();
                    }
                }
                return false;
            }

            bool isChromecast = G9N9NProtocol.IsCandidate(device.Name);
            var capabilitiesTcs = new TaskCompletionSource<ATVVCapabilities?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    service.Dispose();
                    device.Dispose();
                    return false;
                }

                _gattService = service;
                _txCharacteristic = tx;
                _audioCharacteristic = audio;
                _controlCharacteristic = control;
                _capabilitiesTcs = capabilitiesTcs;
                _decoder.LowNibbleFirst = device.Name.Contains("arn9", StringComparison.OrdinalIgnoreCase);
            }

            // Keep the DIS model check before audio setup for Xiaomi ARN9 decoding.
            var information = new RemoteDeviceInformation(bluetoothAddress);
            if (!isChromecast)
            {
                var deviceInformation = await ReadDeviceInformationAsync(device, information, includeHardwareDetails: false);
                lock (_stateLock)
                {
                    if (targetConnGen != _connectionGeneration) return false;
                    if (deviceInformation.ModelNumber?.Contains("ARN9", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _decoder.LowNibbleFirst = true;
                        AppLogger.Info("Detected ARN9 hardware model via DIS (LowNibbleFirst=true)", "BluetoothManager");
                    }
                }
            }

            // Listen before CCCD activation so the initial capabilities notification is captured.
            audio.ValueChanged += OnAudioValueChanged;
            control.ValueChanged += OnControlValueChanged;

            // Subscribe notifications
            var audioStatus = await audio.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    try { audio.ValueChanged -= OnAudioValueChanged; } catch { }
                    try { control.ValueChanged -= OnControlValueChanged; } catch { }
                    device.Dispose();
                    return false;
                }

                if (audioStatus != GattCommunicationStatus.Success)
                {
                    AppLogger.Error($"Failed to subscribe to Audio notifications: Status={audioStatus}", component: "BluetoothManager");
                    try { audio.ValueChanged -= OnAudioValueChanged; } catch { }
                    try { control.ValueChanged -= OnControlValueChanged; } catch { }
                    CleanupAttempt(device);
                    ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "订阅音频通知失败");
                    return false;
                }
            }

            var controlStatus = await control.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    try { audio.ValueChanged -= OnAudioValueChanged; } catch { }
                    try { control.ValueChanged -= OnControlValueChanged; } catch { }
                    device.Dispose();
                    return false;
                }

                if (controlStatus != GattCommunicationStatus.Success)
                {
                    AppLogger.Error($"Failed to subscribe to Control notifications: Status={controlStatus}", component: "BluetoothManager");
                    try { audio.ValueChanged -= OnAudioValueChanged; } catch { }
                    try { control.ValueChanged -= OnControlValueChanged; } catch { }
                    CleanupAttempt(device);
                    ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "订阅控制通知失败");
                    return false;
                }
            }

            // Negotiate capabilities before declaring ready
            byte[] getCaps = ATVVProtocol.BuildGetCapabilities();
            var buffer = CryptographicBuffer.CreateFromByteArray(getCaps);
            var writeStatus = await tx.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    device.Dispose();
                    return false;
                }

                if (writeStatus != GattCommunicationStatus.Success)
                {
                    AppLogger.Error($"Failed to write ATVV GetCapabilities command: Status={writeStatus}", component: "BluetoothManager");
                    CleanupAttempt(device);
                    ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "下发能力协商命令失败");
                    return false;
                }
            }

            AppLogger.Info("ATVV GetCapabilities sent, awaiting remote response...", "BluetoothManager");
            var completedTask = await Task.WhenAny(capabilitiesTcs.Task, Task.Delay(5000));
            ATVVCapabilities? capabilities;
            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    device.Dispose();
                    return false;
                }

                capabilities = completedTask == capabilitiesTcs.Task ? capabilitiesTcs.Task.Result : null;
                if (capabilities == null)
                {
                    AppLogger.Warn("ATVV capabilities negotiation timed out or failed 16kHz validation", "BluetoothManager");
                    CleanupAttempt(device);
                    ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "能力协商超时或不支持 16kHz 编码");
                    return false;
                }
            }

            // Retrieve ContainerId for device matching
            Guid? foundContainerId = null;
            try
            {
                var devInfo = await global::Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(
                    device.DeviceId,
                    new[] { "System.Devices.Aep.ContainerId" },
                    global::Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);
                if (devInfo != null && devInfo.Properties.TryGetValue("System.Devices.Aep.ContainerId", out object? cObj) && cObj is Guid guidVal && guidVal != Guid.Empty)
                {
                    foundContainerId = guidVal;
                }
            }
            catch { }

            if (!foundContainerId.HasValue)
            {
                foundContainerId = Input.RawInputReceiver.GetDeviceContainerId(device.DeviceId);
            }

            lock (_stateLock)
            {
                if (targetConnGen != _connectionGeneration)
                {
                    // Stale connection attempt superseded while querying ContainerId
                    device.Dispose();
                    return false;
                }

                ConnectedDeviceContainerId = foundContainerId;
                if (isChromecast)
                {
                    information = information with { VoiceCapabilities = capabilities };
                    ConnectedDeviceInformation = information;
                }
                IsReady = true;
            }

            AppLogger.Info($"Bluetooth remote connected and ready: '{device.Name}', FrameSize={_frameSize}, ContainerId={ConnectedDeviceContainerId}", "BluetoothManager");
            ConnectionStatusChanged?.Invoke(bluetoothAddress, true, device.Name);
            _ = Task.Run(() => VirtualAudioAuditor.GetCachedStatus());
            if (isChromecast)
            {
                _ = LoadChromecastHardwareDetailsAsync(device, targetConnGen, information);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Exception during ConnectAsync for 0x{bluetoothAddress:X12}", ex, "BluetoothManager");
            lock (_stateLock)
            {
                if (targetConnGen == _connectionGeneration)
                {
                    CleanupAttempt(null);
                    ConnectionStatusChanged?.Invoke(bluetoothAddress, false, "连接异常: " + ex.Message);
                }
            }
            return false;
        }
    }

    private async Task LoadChromecastHardwareDetailsAsync(BluetoothLEDevice device, int generation, RemoteDeviceInformation information)
    {
        var updated = await ReadDeviceInformationAsync(device, information, includeHardwareDetails: true).ConfigureAwait(false);
        if (updated == information) return;

        lock (_stateLock)
        {
            if (generation != _connectionGeneration) return;
            ConnectedDeviceInformation = updated;
        }
        ConnectedDeviceInformationChanged?.Invoke(device.BluetoothAddress);
    }

    private static async Task<RemoteDeviceInformation> ReadDeviceInformationAsync(
        BluetoothLEDevice device, RemoteDeviceInformation information, bool includeHardwareDetails)
    {
        try
        {
            var result = await device.GetGattServicesForUuidAsync(DeviceInformationServiceUuid).AsTask().ConfigureAwait(false);
            if (result.Services.Count == 0) return information;

            try
            {
                if (result.Status != GattCommunicationStatus.Success) return information;
                var service = result.Services[0];
                var model = await ReadDeviceInformationValueAsync(service, ModelNumberUuid).ConfigureAwait(false);
                information = information with { ModelNumber = RemoteDeviceInformation.DecodeDeviceString(model) };
                if (!includeHardwareDetails) return information;

                var firmware = await ReadDeviceInformationValueAsync(service, FirmwareRevisionUuid).ConfigureAwait(false);
                var pnpId = await ReadDeviceInformationValueAsync(service, PnpIdUuid).ConfigureAwait(false);
                return information with
                {
                    FirmwareRevision = RemoteDeviceInformation.DecodeDeviceString(firmware),
                    PnpId = RemoteDeviceInformation.DecodePnpId(pnpId),
                };
            }
            finally
            {
                foreach (var service in result.Services) service.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"DIS query failed: {ex.Message}", "BluetoothManager");
            return information;
        }
    }

    private static async Task<byte[]?> ReadDeviceInformationValueAsync(GattDeviceService service, Guid uuid)
    {
        try
        {
            var characteristics = await service.GetCharacteristicsForUuidAsync(uuid).AsTask().ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                return null;

            var value = await characteristics.Characteristics[0].ReadValueAsync().AsTask().ConfigureAwait(false);
            if (value.Status != GattCommunicationStatus.Success || value.Value == null) return null;
            CryptographicBuffer.CopyToByteArray(value.Value, out var bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"DIS characteristic {uuid} read failed: {ex.Message}", "BluetoothManager");
            return null;
        }
    }

    private void OnDeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        lock (_stateLock)
        {
            if (_connectedDevice != sender) return;
        }
        bool connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        AppLogger.Info($"Device connection status changed: {sender.Name}, connected={connected}", "BluetoothManager");
        if (!connected)
        {
            ulong addr = sender.BluetoothAddress;
            Disconnect();
            ConnectionStatusChanged?.Invoke(addr, false, sender.Name);
        }
    }

    public async Task<bool> StartVoiceSessionAsync()
    {
        int currentConnGen;
        int currentSessGen;
        int opId;
        lock (_stateLock)
        {
            if (_sessionState != BluetoothSessionState.Idle || _txCharacteristic == null)
            {
                AppLogger.Warn($"Cannot start voice session: State={_sessionState}, TxNull={(_txCharacteristic == null)}", "BluetoothManager");
                return false;
            }

            // Output must be ready before opening mic
            if (!TryStartAudio())
            {
                AppLogger.Error("Audio player failed to start output device before opening mic", component: "BluetoothManager");
                return false;
            }

            _sessionState = BluetoothSessionState.Opening;
            _cancelOpenPending = false;
            _audioStopReceived = false;
            _lastEndedSessionId = null;
            currentConnGen = _connectionGeneration;
            currentSessGen = ++_sessionGeneration;
            opId = ++_activeOpeningOperationId;
        }

        AppLogger.Info($"Starting voice session: SessionGen={currentSessGen}, OpId={opId}. State -> Opening", "BluetoothManager");

        // Reset audio pipeline outside _stateLock to prevent AB-BA deadlock
        lock (_audioLock)
        {
            _audioGeneration++;
            _frameAccumulator.Reset();
            _decoder.Reset();
            _postprocessor.Reset();
        }

        try
        {
            byte[] micOpen = ATVVProtocol.BuildMicrophoneOpen();
            var buffer = CryptographicBuffer.CreateFromByteArray(micOpen);
            var writeStatus = await _txCharacteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse)
                .AsTask().ConfigureAwait(false);

            if (writeStatus != GattCommunicationStatus.Success)
            {
                AppLogger.Error($"Failed to send ATVV MicrophoneOpen packet: Status={writeStatus}", component: "BluetoothManager");
                lock (_stateLock)
                {
                    if (currentConnGen == _connectionGeneration && currentSessGen == _sessionGeneration)
                    {
                        _audioPlayer.Stop();
                        _sessionState = BluetoothSessionState.Idle;
                        if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                    }
                }
                return false;
            }

            lock (_stateLock)
            {
                if (currentConnGen != _connectionGeneration || currentSessGen != _sessionGeneration || opId != _activeOpeningOperationId)
                {
                    AppLogger.Warn("MicrophoneOpen completed but operation was cancelled or superseded", "BluetoothManager");
                    if (currentConnGen == _connectionGeneration && currentSessGen == _sessionGeneration)
                    {
                        _audioPlayer.Stop();
                        _sessionState = BluetoothSessionState.Idle;
                        if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                        _ = SendClosePacketAsync(_currentSessionId);
                    }
                    return false;
                }

                if (_sessionState != BluetoothSessionState.Opening || _cancelOpenPending)
                {
                    AppLogger.Info("Fast release detected during Opening, closing mic immediately", "BluetoothManager");
                    _lastEndedSessionId = _currentSessionId;
                    _lastEndedSessionTimeMs = Environment.TickCount64;
                    _ = SendClosePacketAsync(_currentSessionId);
                    _audioPlayer.Stop();
                    _sessionState = BluetoothSessionState.Idle;
                    if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                    return false;
                }

                _sessionState = BluetoothSessionState.Streaming;
                _voiceSessionStartedEmitted = true;
                StartKeepaliveTimer();
            }

            AppLogger.Info("Microphone opened successfully. State -> Streaming", "BluetoothManager");
            VoiceSessionStarted?.Invoke(CurrentEventAddress);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exception while opening microphone", ex, "BluetoothManager");
            lock (_stateLock)
            {
                if (currentConnGen == _connectionGeneration && currentSessGen == _sessionGeneration)
                {
                    _audioPlayer.Stop();
                    _sessionState = BluetoothSessionState.Idle;
                    if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                }
            }
            return false;
        }
    }

    public async Task<bool> StopVoiceSessionAsync()
    {
        byte sessionIdToClose;
        int currentConnGen;
        int currentSessGen;
        bool needsAwaitStop = false;
        TaskCompletionSource<bool>? stopTcs = null;

        lock (_stateLock)
        {
            if (_sessionState == BluetoothSessionState.Opening)
            {
                AppLogger.Info("StopVoiceSession requested during Opening, cancelling opening session immediately", "BluetoothManager");
                _sessionGeneration++;
                _cancelOpenPending = true;
                _activeOpeningOperationId++;
                _audioPlayer.Stop();
                _sessionState = BluetoothSessionState.Idle;
                if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                _ = SendClosePacketAsync(_currentSessionId);
                return true;
            }

            if (_sessionState != BluetoothSessionState.Streaming)
            {
                AppLogger.Debug($"StopVoiceSession ignored, current state is {_sessionState}", "BluetoothManager");
                return false;
            }

            _sessionState = BluetoothSessionState.Draining;
            sessionIdToClose = _currentSessionId;
            _lastEndedSessionId = sessionIdToClose;
            _lastEndedSessionTimeMs = Environment.TickCount64;
            currentConnGen = _connectionGeneration;
            currentSessGen = _sessionGeneration;
            StopKeepaliveTimer();
            _smartToggle.Reset();
            CancelSmartToggleTimers();

            if (!_audioStopReceived)
            {
                _audioStopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                stopTcs = _audioStopTcs;
                needsAwaitStop = true;
            }
        }

        AppLogger.Info($"StopVoiceSession initiated for SessionId={sessionIdToClose}. State -> Draining", "BluetoothManager");

        // Send 0x0D MicClose
        await SendClosePacketAsync(sessionIdToClose);

        // Await AUDIO_STOP (0x00) or bounded tail timeout (250ms)
        if (needsAwaitStop && stopTcs != null)
        {
            await Task.WhenAny(stopTcs.Task, Task.Delay(250)).ConfigureAwait(false);
        }

        // Drain audio player asynchronously OUTSIDE _stateLock to prevent blocking other threads
        await _audioPlayer.DrainAsync(50).ConfigureAwait(false);

        bool emitEnded = false;
        lock (_stateLock)
        {
            if (currentConnGen != _connectionGeneration || currentSessGen != _sessionGeneration)
            {
                AppLogger.Warn("StopVoiceSession completed but session was superseded", "BluetoothManager");
                return false;
            }

            _audioPlayer.Stop();
            _sessionState = BluetoothSessionState.Idle;
            if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
            if (_voiceSessionStartedEmitted)
            {
                emitEnded = true;
                _voiceSessionStartedEmitted = false;
            }
        }

        AppLogger.Info("Voice session closed and audio stopped. State -> Idle", "BluetoothManager");
        if (emitEnded)
        {
            EndVoiceSession(CurrentEventAddress);
        }
        return true;
    }

    private async Task SendClosePacketAsync(byte sessionId)
    {
        if (_txCharacteristic == null) return;
        try
        {
            byte[] micClose = ATVVProtocol.BuildMicrophoneClose(sessionId);
            var buffer = CryptographicBuffer.CreateFromByteArray(micClose);
            await _txCharacteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
        }
        catch { }
    }

    private void StartKeepaliveTimer()
    {
        StopKeepaliveTimer();
        // Xiaomi remotes do not support 0x0E MIC_EXTEND and may disconnect; send to Google & T1 candidate remotes
        if (!G9N9NProtocol.IsCandidate(ConnectedDeviceName) && !T1RemoteProtocol.IsCandidate(ConnectedDeviceName)) return;

        // Bind keepalive callbacks to this session and do not overlap slow GATT writes.
        int connectionGeneration = _connectionGeneration;
        int sessionGeneration = _sessionGeneration;
        int writePending = 0;
        _keepaliveTimer = new System.Threading.Timer(async _ =>
        {
            if (Interlocked.Exchange(ref writePending, 1) != 0) return;
            try
            {
                GattCharacteristic tx;
                byte sessId;
                lock (_stateLock)
                {
                    if (connectionGeneration != _connectionGeneration || sessionGeneration != _sessionGeneration ||
                        _sessionState != BluetoothSessionState.Streaming || _txCharacteristic == null) return;
                    tx = _txCharacteristic;
                    sessId = _currentSessionId;
                }
                byte[] extend = ATVVProtocol.BuildMicrophoneExtend(sessId);
                var buffer = CryptographicBuffer.CreateFromByteArray(extend);
                var status = await tx.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
                AppLogger.Debug($"MIC_EXTEND: SessionId={sessId}, Status={status}", "BluetoothManager");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Keepalive extend failed: {ex.Message}", "BluetoothManager");
            }
            finally
            {
                Volatile.Write(ref writePending, 0);
            }
        }, null, 8000, 8000);
    }

    private void StopKeepaliveTimer()
    {
        _keepaliveTimer?.Dispose();
        _keepaliveTimer = null;
    }

    internal bool IsSmartToggleEligible()
    {
        return G9N9NProtocol.IsCandidate(ConnectedDeviceName) && VoiceButtonMode == G9N9NVoiceButtonMode.SmartToggle;
    }

    private void CancelSmartToggleTimers()
    {
        CancelSmartToggleReopenTimer();
        CancelSmartToggleDeadlineTimer();
    }

    private void CancelSmartToggleReopenTimer()
    {
        _smartToggleReopenTimer?.Dispose();
        _smartToggleReopenTimer = null;
    }

    private void CancelSmartToggleDeadlineTimer()
    {
        _smartToggleDeadlineTimer?.Dispose();
        _smartToggleDeadlineTimer = null;
    }

    private void ScheduleSmartToggleReopenTimeout(ulong generation)
    {
        CancelSmartToggleReopenTimer();
        _smartToggleReopenTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_smartToggle.IsAwaitingReopen && _smartToggle.Generation == generation)
                {
                    AppLogger.Warn("G9N9N smart-toggle reopen timed out", "BluetoothManager");
                    _smartToggle.Reset();
                    _ = StopVoiceSessionAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error in smart-toggle reopen timer", ex, "BluetoothManager");
            }
        }, null, (int)G9N9NSmartToggleSession.ReopenTimeoutMs, Timeout.Infinite);
    }

    private void ScheduleSmartToggleDeadlineTimer(ulong generation)
    {
        CancelSmartToggleDeadlineTimer();
        _smartToggleDeadlineTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_smartToggle.IsHandsFreeActive && _smartToggle.Generation == generation)
                {
                    AppLogger.Info("G9N9N smart-toggle maximum hands-free duration reached (5 min safety limit); stopping", "BluetoothManager");
                    _smartToggle.EnterClosing();
                    CancelSmartToggleTimers();
                    _ = StopVoiceSessionAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error in smart-toggle deadline timer", ex, "BluetoothManager");
            }
        }, null, (int)G9N9NSmartToggleSession.MaximumHandsFreeDurationMs, Timeout.Infinite);
    }

    private async Task ReopenMicrophoneAsync(ulong generation)
    {
        if (_txCharacteristic == null) return;
        try
        {
            byte[] micOpen = ATVVProtocol.BuildMicrophoneOpen();
            var buffer = CryptographicBuffer.CreateFromByteArray(micOpen);
            var writeStatus = await _txCharacteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse)
                .AsTask().ConfigureAwait(false);
            AppLogger.Info($"G9N9N smart-toggle MicrophoneOpen packet sent. Status={writeStatus}", "BluetoothManager");
            if (writeStatus != GattCommunicationStatus.Success)
            {
                _smartToggle.Reset();
                CancelSmartToggleTimers();
                _ = StopVoiceSessionAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exception while reopening microphone for smart-toggle", ex, "BluetoothManager");
            _smartToggle.Reset();
            CancelSmartToggleTimers();
            _ = StopVoiceSessionAsync();
        }
    }

    private void OnAudioValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        int currentAudioGen = Volatile.Read(ref _audioGeneration);
        int frameSize;
        float gain;
        lock (_stateLock)
        {
            if (sender != _audioCharacteristic) return;
            if (_sessionState != BluetoothSessionState.Streaming && _sessionState != BluetoothSessionState.Draining)
            {
                return;
            }
            frameSize = _frameSize;
            gain = CurrentGain;
        }

        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] data);
        if (data.Length == 0) return;

        float? latestLevel = null;

        // Process audio in _audioLock ONLY (never nest _stateLock inside to prevent deadlock)
        lock (_audioLock)
        {
            if (currentAudioGen != Volatile.Read(ref _audioGeneration))
            {
                return;
            }

            var frames = _frameAccumulator.Append(data, frameSize);
            foreach (var frame in frames)
            {
                if (_frameAccumulator.TryConsumeSyncForNextFrame(out short predictor, out byte stepIndex))
                {
                    _decoder.Synchronize(predictor, stepIndex);
                }

                int requiredSamples = frame.Length * 2;
                if (_decodedBuffer.Length < requiredSamples)
                {
                    Array.Resize(ref _decodedBuffer, Math.Max(requiredSamples, _decodedBuffer.Length * 2));
                }

                int count = _decoder.Decode(frame, _decodedBuffer);
                var sampleSpan = _decodedBuffer.AsSpan(0, count);

                _postprocessor.Process(sampleSpan, gain);

                _audioPlayer.WriteSamples(sampleSpan);

                latestLevel = AudioLevelMeter.CalculateRms(sampleSpan);
            }
        }

        if (latestLevel.HasValue)
        {
            AudioLevelChanged?.Invoke(CurrentEventAddress, latestLevel.Value);
        }
    }

    private void OnControlValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        int capturedConnGen;
        int capturedAudioGen;
        lock (_stateLock)
        {
            if (sender != _controlCharacteristic) return;
            capturedConnGen = _connectionGeneration;
            capturedAudioGen = _audioGeneration;
        }

        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] data);
        if (data.Length == 0) return;

        byte opcode = data[0];
        switch (opcode)
        {
            case ATVVProtocol.OpcodeCapabilitiesResp: // 0x0B
                var caps = ATVVProtocol.ParseCapabilities(data);
                AppLogger.Info($"ATVV response CapabilitiesResp (0x0B): Version=0x{caps?.Version:X4}, Codecs=0x{caps?.Codecs:X2}, FrameSize={caps?.FrameSize}", "BluetoothManager");
                lock (_stateLock)
                {
                    if (capturedConnGen != _connectionGeneration) return;
                    if (caps != null && caps.SampleRate == 16000 && (caps.Codecs & 0x02) != 0)
                    {
                        _frameSize = caps.FrameSize;
                        _capabilitiesTcs?.TrySetResult(caps);
                    }
                    else
                    {
                        _capabilitiesTcs?.TrySetResult(null);
                    }
                }
                break;

            case ATVVProtocol.OpcodeAudioStart: // 0x04
                if (ATVVProtocol.ParseAudioStart(data, out _, out byte codec, out byte sessionId))
                {
                    AppLogger.Info($"ATVV packet AudioStart (0x04): Codec=0x{codec:X2}, SessionId={sessionId}", "BluetoothManager");
                    lock (_stateLock)
                    {
                        if (capturedConnGen != _connectionGeneration) return;
                    }

                    if (IsSmartToggleEligible())
                    {
                        if (_smartToggle.IsAwaitingReopen)
                        {
                            var gen = _smartToggle.EnterHandsFreeActive();
                            CancelSmartToggleReopenTimer();
                            if (gen.HasValue) ScheduleSmartToggleDeadlineTimer(gen.Value);
                            AppLogger.Info("G9N9N smart-toggle reopened; hands-free speech active", "BluetoothManager");
                        }
                        else if (_smartToggle.ShouldStopOnRemoteAudioStart(null, _currentSessionId, sessionId))
                        {
                            _smartToggle.EnterClosing();
                            CancelSmartToggleTimers();
                            _ = StopVoiceSessionAsync();
                            return;
                        }
                    }

                    // Reject non-16kHz codec (e.g. 8kHz)
                    if (codec != 0x02)
                    {
                        AppLogger.Warn($"Rejecting non-16kHz codec (0x{codec:X2}), sending MicClose", "BluetoothManager");
                        _ = SendClosePacketAsync(sessionId);
                        bool shouldEmitEnded = false;
                        lock (_stateLock)
                        {
                            if (capturedConnGen != _connectionGeneration) return;
                            _audioPlayer.Stop();
                            _sessionState = BluetoothSessionState.Idle;
                            if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                            StopKeepaliveTimer();
                            if (_voiceSessionStartedEmitted)
                            {
                                shouldEmitEnded = true;
                                _voiceSessionStartedEmitted = false;
                            }
                        }
                        if (shouldEmitEnded)
                        {
                            EndVoiceSession(CurrentEventAddress);
                        }
                        return;
                    }

                    // Xiaomi remotes can send AudioStart directly, without a HID
                    // voice key or StartSearch. Adopt that stream instead of closing it.
                    bool wasCancelledOrEnded = false;
                    bool remoteInitiated = false;
                    int remoteSessionGen = 0;
                    lock (_stateLock)
                    {
                        if (capturedConnGen != _connectionGeneration) return;
                        bool isStaleDuplicateSession = _lastEndedSessionId.HasValue &&
                            _lastEndedSessionId.Value == sessionId &&
                            (Environment.TickCount64 - _lastEndedSessionTimeMs) < 500;

                        if (_cancelOpenPending || _sessionState == BluetoothSessionState.Draining ||
                            (_sessionState == BluetoothSessionState.Idle && (!IsReady || isStaleDuplicateSession)))
                        {
                            wasCancelledOrEnded = true;
                            _lastEndedSessionId = sessionId;
                            _lastEndedSessionTimeMs = Environment.TickCount64;
                        }
                        else if (_sessionState == BluetoothSessionState.Idle)
                        {
                            if (!TryStartAudio())
                            {
                                wasCancelledOrEnded = true;
                                AppLogger.Error("Cannot accept remote AudioStart: audio output failed", component: "BluetoothManager");
                            }
                            else
                            {
                                _currentSessionId = sessionId;
                                _lastEndedSessionId = null;
                                _audioStopReceived = false;
                                _sessionState = BluetoothSessionState.Opening;
                                remoteSessionGen = ++_sessionGeneration;
                                _activeOpeningOperationId++;
                                remoteInitiated = true;
                            }
                        }
                        else if (_sessionState == BluetoothSessionState.Opening)
                        {
                            _currentSessionId = sessionId;
                        }
                    }

                    if (wasCancelledOrEnded)
                    {
                        AppLogger.Info($"AudioStart received while state={_sessionState}/cancelPending={_cancelOpenPending}, sending close", "BluetoothManager");
                        _ = SendClosePacketAsync(sessionId);
                    }
                    else if (remoteInitiated)
                    {
                        // Reset without nesting the state and audio locks.
                        lock (_audioLock)
                        {
                            if (capturedConnGen != Volatile.Read(ref _connectionGeneration) ||
                                remoteSessionGen != Volatile.Read(ref _sessionGeneration)) return;
                            _audioGeneration++;
                            _frameAccumulator.Reset();
                            _decoder.Reset();
                            _postprocessor.Reset();
                        }
                        lock (_stateLock)
                        {
                            if (capturedConnGen != _connectionGeneration || remoteSessionGen != _sessionGeneration ||
                                _cancelOpenPending || _sessionState != BluetoothSessionState.Opening)
                                return;
                            _sessionState = BluetoothSessionState.Streaming;
                            _voiceSessionStartedEmitted = true;
                            StartKeepaliveTimer();
                        }
                        AppLogger.Info($"Accepted remote-initiated voice session {sessionId}. State -> Streaming", "BluetoothManager");
                        ulong eventAddr = CurrentEventAddress;
                        VoiceButtonPressed?.Invoke(eventAddr);
                        VoiceSessionStarted?.Invoke(eventAddr);
                    }
                }
                break;

            case ATVVProtocol.OpcodeMicrophoneRefused: // 0x0C
                AppLogger.Warn("ATVV packet MicrophoneRefused (0x0C) received from remote", "BluetoothManager");
                bool emitRefusedEnded = false;
                lock (_stateLock)
                {
                    if (capturedConnGen != _connectionGeneration) return;
                    _activeOpeningOperationId++;
                    _smartToggle.Reset();
                    CancelSmartToggleTimers();
                    if (_sessionState == BluetoothSessionState.Opening || _sessionState == BluetoothSessionState.Streaming)
                    {
                        StopKeepaliveTimer();
                        _audioPlayer.Stop();
                        _sessionState = BluetoothSessionState.Idle;
                        if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                        if (_voiceSessionStartedEmitted)
                        {
                            emitRefusedEnded = true;
                            _voiceSessionStartedEmitted = false;
                        }
                    }
                }
                if (emitRefusedEnded)
                {
                    EndVoiceSession(CurrentEventAddress);
                }
                break;

            case ATVVProtocol.OpcodeAudioSync: // 0x0A
                if (ATVVProtocol.ParseAudioSync(data, out short predictor, out byte stepIndex))
                {
                    AppLogger.Debug($"ATVV packet AudioSync (0x0A): Predictor={predictor}, StepIndex={stepIndex}", "BluetoothManager");
                    lock (_audioLock)
                    {
                        if (capturedAudioGen != _audioGeneration) return;
                        _frameAccumulator.Synchronize(predictor, stepIndex);
                    }
                }
                break;

            case ATVVProtocol.OpcodeStartSearch: // 0x08
                AppLogger.Info("ATVV packet StartSearch (0x08) received from remote (voice key pressed)", "BluetoothManager");
                lock (_stateLock)
                {
                    if (capturedConnGen != _connectionGeneration) return;
                }

                VoiceButtonPressed?.Invoke(CurrentEventAddress);
                if (_voiceArbiter?.Owner is { } owner && owner != this) return;
                if (IsSmartToggleEligible())
                {
                    if (_smartToggle.IsActive && _smartToggle.ShouldStopOnRemoteOpenRequest())
                    {
                        AppLogger.Info("G9N9N smart-toggle voice key pressed second time; stopping hands-free session", "BluetoothManager");
                        _smartToggle.EnterClosing();
                        CancelSmartToggleTimers();
                        _ = StopVoiceSessionAsync();
                        return;
                    }

                    _smartToggle.BeginPhysicalPress(Environment.TickCount64);
                }

                _ = StartVoiceSessionAsync();
                break;

            case ATVVProtocol.OpcodeAudioStop: // 0x00
                AppLogger.Info($"ATVV AudioStop received: Reason={(data.Length > 1 ? $"0x{data[1]:X2}" : "missing")}, " +
                    $"State={_sessionState}, SessionId={_currentSessionId}", "BluetoothManager");
                lock (_stateLock)
                {
                    if (capturedConnGen != _connectionGeneration) return;
                    if (_sessionState == BluetoothSessionState.Idle)
                    {
                        _cancelOpenPending = false;
                        return;
                    }
                    _audioStopReceived = true;
                }
                _audioStopTcs?.TrySetResult(true);

                if (IsSmartToggleEligible() && _smartToggle.IsActive)
                {
                    bool shouldReopen = _smartToggle.ShouldReopenOnPhysicalStop(isRemoteStop: true, localCloseRequested: false, Environment.TickCount64);
                    if (shouldReopen)
                    {
                        AppLogger.Info("G9N9N physical voice key released before 0.7s threshold. Initiating hands-free smart-toggle reopen!", "BluetoothManager");
                        ulong gen = _smartToggle.EnterAwaitingReopen();
                        ScheduleSmartToggleReopenTimeout(gen);
                        _ = ReopenMicrophoneAsync(gen);
                        return;
                    }
                    else
                    {
                        AppLogger.Info("G9N9N physical voice key released after 0.7s hold. Stopping hold-to-talk session", "BluetoothManager");
                        _smartToggle.Reset();
                        CancelSmartToggleTimers();
                    }
                }

                if (_sessionState == BluetoothSessionState.Streaming || _sessionState == BluetoothSessionState.Opening)
                {
                    _ = StopVoiceSessionAsync();
                }
                break;
        }
    }

    public void Disconnect()
    {
        AppLogger.Info("Disconnect initiated: cleaning up Bluetooth connection and audio resources", "BluetoothManager");
        bool emitEnded = false;
        bool hadConnection = false;
        ulong disconnectedAddress = 0;
        BluetoothLEDevice? devToDispose = null;
        lock (_stateLock)
        {
            disconnectedAddress = _connectedDevice?.BluetoothAddress ?? (ulong)Volatile.Read(ref _deviceAddress);
            _connectionGeneration++;
            _sessionGeneration++;
            _activeOpeningOperationId++;
            if (_voiceSessionStartedEmitted)
            {
                emitEnded = true;
                _voiceSessionStartedEmitted = false;
            }
            _sessionState = BluetoothSessionState.Idle;
            _cancelOpenPending = false;
            StopKeepaliveTimer();
            _smartToggle.Reset();
            CancelSmartToggleTimers();
            _audioPlayer.Stop();
            if (!emitEnded) _voiceArbiter?.Release(this);

            if (_connectedDevice != null)
            {
                hadConnection = true;
                devToDispose = _connectedDevice;
                _connectedDevice.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _connectedDevice = null;
            }

            if (_audioCharacteristic != null)
            {
                _audioCharacteristic.ValueChanged -= OnAudioValueChanged;
                _audioCharacteristic = null;
            }

            if (_controlCharacteristic != null)
            {
                _controlCharacteristic.ValueChanged -= OnControlValueChanged;
                _controlCharacteristic = null;
            }

            if (_gattService != null)
            {
                try { _gattService.Dispose(); } catch { }
                _gattService = null;
            }

            _txCharacteristic = null;
            IsReady = false;
            _lastEndedSessionId = null;
            _audioStopReceived = false;
            ConnectedDeviceContainerId = null;
            ConnectedDeviceInformation = null;
            _capabilitiesTcs?.TrySetCanceled();
            _audioStopTcs?.TrySetCanceled();
        }

        // Reset audio pipeline outside _stateLock to prevent AB-BA deadlock
        lock (_audioLock)
        {
            _audioGeneration++;
            _frameAccumulator.Reset();
            _decoder.Reset();
            _postprocessor.Reset();
        }

        if (emitEnded)
        {
            EndVoiceSession(disconnectedAddress);
        }

        if (hadConnection)
        {
            ConnectionStatusChanged?.Invoke(disconnectedAddress, false, string.Empty);
        }

        devToDispose?.Dispose();
    }

    private bool TryStartAudio()
    {
        if (_voiceArbiter?.TryAcquire(this) == false)
        {
            AppLogger.Info($"Ignoring voice from '{ConnectedDeviceName}': another session owns the microphone", "BluetoothManager");
            return false;
        }
        bool started = false;
        try
        {
            started = _audioPlayer.Start();
            if (!started)
            {
                AudioOutputFailed?.Invoke(CurrentEventAddress, "虚拟音频驱动 VB-CABLE (CABLE Input) 未就绪或被占用");
            }
            return started;
        }
        finally
        {
            if (!started) _voiceArbiter?.Release(this);
        }
    }

    private void EndVoiceSession(ulong bluetoothAddress)
    {
        try { VoiceSessionEnded?.Invoke(bluetoothAddress); }
        finally { _voiceArbiter?.Release(this); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopScanning();
        _watcher.Received -= OnAdvertisementReceived;
        Disconnect();
        _audioPlayer.Dispose();
    }
}
