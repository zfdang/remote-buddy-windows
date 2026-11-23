// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
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
    private enum AudioWorkKind { Audio, Sync, Reset, Flush }

    private readonly record struct AudioWorkItem(
        AudioWorkKind Kind,
        int AudioGeneration,
        byte[]? PooledBuffer,
        int Length,
        int FrameSize,
        float Gain,
        short Predictor,
        byte StepIndex,
        ushort FrameNumber,
        bool ResetLowNibbleFirst,
        TaskCompletionSource<bool>? Completion);

    private static readonly TimeSpan MicOpenWriteTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AudioStartWaitTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan MicCloseWriteTimeout = TimeSpan.FromMilliseconds(1500);
    private const long NoAudioPacketTimeoutMs = 4000;
    private const long GlobalMaxSessionDurationMs = 300_000;

    private static readonly Guid DeviceInformationServiceUuid = new("0000180A-0000-1000-8000-00805F9B34FB");
    private static readonly Guid ModelNumberUuid = new("00002A24-0000-1000-8000-00805F9B34FB");
    private static readonly Guid FirmwareRevisionUuid = new("00002A26-0000-1000-8000-00805F9B34FB");
    private static readonly Guid HardwareRevisionUuid = new("00002A27-0000-1000-8000-00805F9B34FB");
    private static readonly Guid PnpIdUuid = new("00002A50-0000-1000-8000-00805F9B34FB");
    private static readonly Guid HidServiceUuid = new("00001812-0000-1000-8000-00805F9B34FB");

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
    private readonly Channel<AudioWorkItem> _audioChannel = Channel.CreateUnbounded<AudioWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _audioWorkerTask;

    private byte _currentSessionId = 0;
    private byte? _lastEndedSessionId;
    private long _lastEndedSessionTimeMs;
    private int _frameSize = 120;
    private ATVVCapabilities? _capabilities;
    private volatile BluetoothSessionState _sessionState = BluetoothSessionState.Idle;
    private bool _cancelOpenPending = false;
    private bool _voiceSessionStartedEmitted = false;
    private bool _audioStopReceived = false;
    private int _connectionGeneration = 0;
    private int _sessionGeneration = 0;
    private int _audioGeneration = 0;
    private int _activeOpeningOperationId = 0;
    private long _deviceAddress = 0;
    private long _lastAudioPacketTimeMs = 0;
    private long _sessionStartedTimeMs = 0;
    private int _disposed = 0;
    private CancellationTokenSource? _connectionCts;
    private TaskCompletionSource<ATVVCapabilities?>? _capabilitiesTcs;
    private TaskCompletionSource<bool>? _audioStartTcs;
    private TaskCompletionSource<bool>? _audioStopTcs;
    private System.Threading.Timer? _keepaliveTimer;
    private System.Threading.Timer? _sessionWatchdogTimer;
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
    public EtwAttributeFilter ConnectedEtwAttributeFilter { get; private set; }

    internal async Task ProbeButtonSourceAsync(Guid marker)
    {
        BluetoothLEDevice? device;
        int capturedConnGen;
        CancellationToken connToken;
        lock (_stateLock)
        {
            device = _connectedDevice;
            capturedConnGen = _connectionGeneration;
            connToken = _connectionCts?.Token ?? CancellationToken.None;
        }
        if (device == null) return;

        bool IsStillCurrent()
        {
            lock (_stateLock)
            {
                return capturedConnGen == _connectionGeneration && ReferenceEquals(_connectedDevice, device);
            }
        }

        try
        {
            // Give ETW trace session 150ms to attach to driver buffers
            await Task.Delay(150, connToken).ConfigureAwait(false);
            if (!IsStillCurrent()) return;

            var result = await device.GetGattServicesForUuidAsync(marker, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            foreach (var service in result.Services) service.Dispose();
            AppLogger.Info($"Chromecast button source query finished: {result.Status}", "BluetoothManager");

            if (result.Status != GattCommunicationStatus.Success)
            {
                await Task.Delay(250, connToken).ConfigureAwait(false);
                if (!IsStillCurrent()) return;

                var retryResult = await device.GetGattServicesForUuidAsync(marker, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                foreach (var service in retryResult.Services) service.Dispose();
                AppLogger.Info($"Chromecast button source query retry: {retryResult.Status}", "BluetoothManager");
            }
        }
        catch (OperationCanceledException)
        {
            AppLogger.Debug("Chromecast button source query cancelled on disconnect", "BluetoothManager");
        }
        catch (ObjectDisposedException)
        {
            AppLogger.Debug("Chromecast button source query skipped on disposed device", "BluetoothManager");
        }
        catch (Exception ex) when (!IsStillCurrent())
        {
            AppLogger.Debug($"Chromecast button source query stopped after disconnect: {ex.Message}", "BluetoothManager");
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
        _audioWorkerTask = Task.Run(ProcessAudioChannelAsync);
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
        CancellationToken connToken;
        lock (_stateLock)
        {
            targetConnGen = ++_connectionGeneration;
            _activeOpeningOperationId++;
            _capabilities = null;
            _connectionCts?.Dispose();
            _connectionCts = new CancellationTokenSource();
            connToken = _connectionCts.Token;
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
            var writeStatus = await WriteCharacteristicWithTimeoutAsync(tx, getCaps, TimeSpan.FromSeconds(3), connToken).ConfigureAwait(false);
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

            EtwAttributeFilter discoveredFilter = default;
            if (isChromecast)
            {
                discoveredFilter = await DiscoverChromecastAttributeFilterAsync(
                    device,
                    service,
                    tx.AttributeHandle,
                    audio.AttributeHandle,
                    control.AttributeHandle).ConfigureAwait(false);
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
                    ConnectedEtwAttributeFilter = discoveredFilter;
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

    private static async Task<EtwAttributeFilter> DiscoverChromecastAttributeFilterAsync(
        BluetoothLEDevice device,
        GattDeviceService activeAtvvService,
        ushort txHandle,
        ushort audioHandle,
        ushort controlHandle)
    {
        ushort[] rawPriority = new ushort[] { txHandle, audioHandle, controlHandle }
            .Where(h => h != 0)
            .Distinct()
            .OrderBy(h => h)
            .ToArray();
        ushort[] fallbackExcluded = ExpandHandlePairs(rawPriority);

        try
        {
            var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
            try
            {
                if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                {
                    AppLogger.Info(
                        $"G9N9N GATT layout discovery unavailable for 0x{device.BluetoothAddress:X12}: Status={servicesResult.Status}, Count={servicesResult.Services.Count}; falling back to priority exclusions [{string.Join(", ", fallbackExcluded.Select(h => $"0x{h:X4}"))}]",
                        "BluetoothManager");
                    return new EtwAttributeFilter(0, 0, fallbackExcluded);
                }

                var services = new List<(Guid ServiceUuid, ushort StartHandle)>(servicesResult.Services.Count);
                var nonHidHandles = new List<ushort>();
                foreach (var svc in servicesResult.Services)
                {
                    services.Add((svc.Uuid, svc.AttributeHandle));
                    if (ReferenceEquals(svc, activeAtvvService) || svc.Uuid == ATVVProtocol.ServiceUuid || svc.Uuid == HidServiceUuid)
                    {
                        continue;
                    }

                    try
                    {
                        var charsResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
                        if (charsResult.Status == GattCommunicationStatus.Success)
                        {
                            foreach (var ch in charsResult.Characteristics)
                            {
                                if (ch.AttributeHandle != 0)
                                {
                                    nonHidHandles.Add(ch.AttributeHandle);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"Non-HID characteristic scan skipped for service {svc.Uuid}: {ex.Message}", "BluetoothManager");
                    }
                }

                var filter = BuildEtwAttributeFilter(services, rawPriority, nonHidHandles, out string summary);
                AppLogger.Info($"G9N9N GATT layout for 0x{device.BluetoothAddress:X12}: {summary}", "BluetoothManager");
                return filter;
            }
            finally
            {
                foreach (var svc in servicesResult.Services)
                {
                    // Never dispose the cached WinRT instance backing the active ATVV subscriptions.
                    if (!ReferenceEquals(svc, activeAtvvService) && svc.Uuid != ATVVProtocol.ServiceUuid)
                    {
                        try { svc.Dispose(); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info(
                $"G9N9N GATT layout discovery failed for 0x{device.BluetoothAddress:X12}: {ex.Message}; falling back to priority exclusions [{string.Join(", ", fallbackExcluded.Select(h => $"0x{h:X4}"))}]",
                "BluetoothManager");
            return new EtwAttributeFilter(0, 0, fallbackExcluded);
        }
    }

    private static ushort[] ExpandHandlePairs(IEnumerable<ushort> handles)
    {
        var expanded = new SortedSet<ushort>();
        foreach (ushort h in handles)
        {
            if (h == 0) continue;
            expanded.Add(h);
            if (h < ushort.MaxValue) expanded.Add((ushort)(h + 1));
        }
        return expanded.ToArray();
    }

    internal static EtwAttributeFilter BuildEtwAttributeFilter(
        IReadOnlyList<(Guid ServiceUuid, ushort StartHandle)> services,
        IReadOnlyCollection<ushort> priorityExcludedHandles,
        IReadOnlyCollection<ushort> nonHidCharacteristicHandles,
        out string summary)
    {
        var sorted = services
            .Where(s => s.StartHandle > 0)
            .Distinct()
            .OrderBy(s => s.StartHandle)
            .ToArray();

        ushort hidStart = 0;
        ushort hidEnd = 0;
        ushort atvvStart = 0;
        ushort atvvEnd = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            ushort end = (i + 1 < sorted.Length && sorted[i + 1].StartHandle > sorted[i].StartHandle)
                ? (ushort)(sorted[i + 1].StartHandle - 1)
                : ushort.MaxValue;
            if (sorted[i].ServiceUuid == HidServiceUuid && hidStart == 0)
            {
                hidStart = sorted[i].StartHandle;
                hidEnd = end;
            }
            else if (sorted[i].ServiceUuid == ATVVProtocol.ServiceUuid && atvvStart == 0)
            {
                atvvStart = sorted[i].StartHandle;
                atvvEnd = end;
            }
        }

        bool hasHidRange = hidStart > 0 && hidEnd >= hidStart;
        var excluded = new SortedSet<ushort>();
        foreach (ushort handle in priorityExcludedHandles)
        {
            if (handle == 0) continue;
            excluded.Add(handle);
            if (handle < ushort.MaxValue) excluded.Add((ushort)(handle + 1));
        }

        if (atvvStart > 0 && atvvEnd >= atvvStart && (atvvEnd - atvvStart) < 32)
        {
            for (uint h = atvvStart; h <= atvvEnd; h++)
            {
                excluded.Add((ushort)h);
            }
        }

        foreach (ushort handle in nonHidCharacteristicHandles.Where(h => h != 0).Distinct().OrderBy(h => h))
        {
            foreach (ushort candidate in handle < ushort.MaxValue
                ? new ushort[] { handle, (ushort)(handle + 1) }
                : new ushort[] { handle })
            {
                if (hasHidRange && (candidate < hidStart || candidate > hidEnd)) continue;
                if (excluded.Count >= 32) break;
                excluded.Add(candidate);
            }
            if (excluded.Count >= 32) break;
        }

        var serviceSummaries = new List<string>(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            ushort end = (i + 1 < sorted.Length && sorted[i + 1].StartHandle > sorted[i].StartHandle)
                ? (ushort)(sorted[i + 1].StartHandle - 1)
                : ushort.MaxValue;
            serviceSummaries.Add($"{FormatServiceUuid(sorted[i].ServiceUuid)}@0x{sorted[i].StartHandle:X4}..0x{end:X4}");
        }

        ushort[] excludedArray = excluded.ToArray();
        summary =
            $"hidRange={(hasHidRange ? $"0x{hidStart:X4}..0x{hidEnd:X4}" : "none")}, " +
            $"excluded=[{string.Join(", ", excludedArray.Select(h => $"0x{h:X4}"))}], " +
            $"services=[{string.Join(", ", serviceSummaries)}]";

        return new EtwAttributeFilter(hidStart, hidEnd, excludedArray);
    }

    private static string FormatServiceUuid(Guid uuid)
    {
        string formatted = uuid.ToString("D");
        if (formatted.StartsWith("0000", StringComparison.OrdinalIgnoreCase) &&
            formatted.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase))
        {
            return "0x" + formatted.Substring(4, 4).ToUpperInvariant();
        }
        return formatted;
    }

    private async Task LoadChromecastHardwareDetailsAsync(BluetoothLEDevice device, int generation, RemoteDeviceInformation information)
    {
        var updated = await ReadDeviceInformationAsync(device, information, includeHardwareDetails: true).ConfigureAwait(false);
        AppLogger.Info(
            $"G9N9N hardware details for 0x{device.BluetoothAddress:X12}: model='{updated.ModelNumber ?? "-"}', firmware='{updated.FirmwareRevision ?? "-"}', hardwareRev='{updated.HardwareRevision ?? "-"}', pnp='{updated.VendorProductId ?? "-"}', hidVersion='{updated.HidVersionText ?? "-"}'",
            "BluetoothManager");
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
                var hardware = await ReadDeviceInformationValueAsync(service, HardwareRevisionUuid).ConfigureAwait(false);
                var pnpId = await ReadDeviceInformationValueAsync(service, PnpIdUuid).ConfigureAwait(false);
                return information with
                {
                    FirmwareRevision = RemoteDeviceInformation.DecodeDeviceString(firmware),
                    HardwareRevision = RemoteDeviceInformation.DecodeDeviceString(hardware),
                    PnpId = RemoteDeviceInformation.DecodePnpId(pnpId),
                    ProductVersion = RemoteDeviceInformation.DecodePnpProductVersion(pnpId),
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

    private static async Task<GattCommunicationStatus> WriteCharacteristicWithTimeoutAsync(
        GattCharacteristic tx,
        byte[] payload,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var buffer = CryptographicBuffer.CreateFromByteArray(payload);
        var writeOp = tx.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
        var writeTask = writeOp.AsTask();
        _ = writeTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await writeTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { writeOp.Cancel(); } catch { }
            string reason = cancellationToken.IsCancellationRequested ? "cancelled" : $"timed out after {timeout.TotalMilliseconds:0}ms";
            AppLogger.Warn($"GATT WriteValueAsync {reason} (Opcode=0x{(payload.Length > 0 ? payload[0] : 0):X2})", "BluetoothManager");
            return GattCommunicationStatus.Unreachable;
        }
    }

    private void EnqueueAudioReset(int audioGen, bool resetLowNibbleFirst = false)
    {
        _audioChannel.Writer.TryWrite(new AudioWorkItem(
            AudioWorkKind.Reset, audioGen, null, 0, 0, 0, 0, 0, 0, resetLowNibbleFirst, null));
    }

    private async Task FlushAudioPipelineAsync(int audioGen, int frameSize, float gain)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_audioChannel.Writer.TryWrite(new AudioWorkItem(
            AudioWorkKind.Flush, audioGen, null, 0, frameSize, gain, 0, 0, 0, false, tcs)))
        {
            await Task.WhenAny(tcs.Task, Task.Delay(150)).ConfigureAwait(false);
        }
    }

    public async Task<bool> StartVoiceSessionAsync()
    {
        int currentConnGen;
        int currentSessGen;
        int currentAudioGen;
        int opId;
        GattCharacteristic tx;
        ATVVCapabilities? caps;
        CancellationToken connToken;
        TaskCompletionSource<bool> startTcs;

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

            tx = _txCharacteristic;
            caps = _capabilities;
            connToken = _connectionCts?.Token ?? CancellationToken.None;
            _sessionState = BluetoothSessionState.Opening;
            _cancelOpenPending = false;
            _audioStopReceived = false;
            _lastEndedSessionId = null;
            _currentSessionId = 0;
            currentConnGen = _connectionGeneration;
            currentSessGen = ++_sessionGeneration;
            currentAudioGen = Interlocked.Increment(ref _audioGeneration);
            opId = ++_activeOpeningOperationId;
            startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _audioStartTcs = startTcs;

            long now = Environment.TickCount64;
            Volatile.Write(ref _lastAudioPacketTimeMs, now);
            Volatile.Write(ref _sessionStartedTimeMs, now);
        }

        AppLogger.Info($"Starting voice session: SessionGen={currentSessGen}, OpId={opId}. State -> Opening", "BluetoothManager");
        EnqueueAudioReset(currentAudioGen, resetLowNibbleFirst: false);

        try
        {
            ushort version = caps?.Version ?? 0x0100;
            byte selectedCodec = caps?.SelectedCodec ?? 0x02;
            byte[] micOpen = ATVVProtocol.BuildMicrophoneOpen(version, selectedCodec);
            var writeStatus = await WriteCharacteristicWithTimeoutAsync(tx, micOpen, MicOpenWriteTimeout, connToken)
                .ConfigureAwait(false);

            if (writeStatus != GattCommunicationStatus.Success)
            {
                AppLogger.Error($"Failed to send ATVV MicrophoneOpen packet: Status={writeStatus}", component: "BluetoothManager");
                lock (_stateLock)
                {
                    if (currentConnGen == _connectionGeneration && currentSessGen == _sessionGeneration)
                    {
                        if (ReferenceEquals(_audioStartTcs, startTcs)) _audioStartTcs = null;
                        _audioPlayer.Stop();
                        _sessionState = BluetoothSessionState.Idle;
                        if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                    }
                }
                startTcs.TrySetResult(false);
                return false;
            }

            // Hold Opening until remote confirms with AUDIO_START (0x04) carrying SessionID
            Task completed = await Task.WhenAny(startTcs.Task, Task.Delay(AudioStartWaitTimeout, connToken))
                .ConfigureAwait(false);

            if (completed == startTcs.Task && await startTcs.Task.ConfigureAwait(false))
            {
                return true;
            }

            byte sessionIdToClose = 0;
            bool timedOut = completed != startTcs.Task;
            lock (_stateLock)
            {
                if (ReferenceEquals(_audioStartTcs, startTcs)) _audioStartTcs = null;
                if (currentConnGen == _connectionGeneration && currentSessGen == _sessionGeneration &&
                    _sessionState == BluetoothSessionState.Opening)
                {
                    if (timedOut)
                    {
                        AppLogger.Warn($"Timed out waiting for AUDIO_START (0x04) after {AudioStartWaitTimeout.TotalMilliseconds:0}ms; aborting voice session", "BluetoothManager");
                    }
                    _sessionGeneration++;
                    _activeOpeningOperationId++;
                    sessionIdToClose = _currentSessionId;
                    _lastEndedSessionId = sessionIdToClose;
                    _lastEndedSessionTimeMs = Environment.TickCount64;
                    _audioPlayer.Stop();
                    _sessionState = BluetoothSessionState.Idle;
                    if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                }
                else
                {
                    timedOut = false;
                }
            }
            if (timedOut)
            {
                _ = SendClosePacketAsync(sessionIdToClose);
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            startTcs.TrySetResult(false);
            lock (_stateLock)
            {
                if (ReferenceEquals(_audioStartTcs, startTcs)) _audioStartTcs = null;
                if (currentConnGen == _connectionGeneration && currentSessGen == _sessionGeneration &&
                    _sessionState == BluetoothSessionState.Opening)
                {
                    _audioPlayer.Stop();
                    _sessionState = BluetoothSessionState.Idle;
                    if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exception while opening microphone", ex, "BluetoothManager");
            startTcs.TrySetResult(false);
            lock (_stateLock)
            {
                if (ReferenceEquals(_audioStartTcs, startTcs)) _audioStartTcs = null;
                if (currentConnGen == _connectionGeneration && currentSessGen == _sessionGeneration &&
                    _sessionState == BluetoothSessionState.Opening)
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
        int currentAudioGen;
        int frameSize;
        float gain;
        bool needsAwaitStop = false;
        TaskCompletionSource<bool>? stopTcs = null;
        TaskCompletionSource<bool>? openTcs = null;

        lock (_stateLock)
        {
            if (_sessionState == BluetoothSessionState.Opening)
            {
                AppLogger.Info("StopVoiceSession requested during Opening, cancelling opening session immediately", "BluetoothManager");
                _sessionGeneration++;
                _cancelOpenPending = true;
                _activeOpeningOperationId++;
                openTcs = _audioStartTcs;
                _audioStartTcs = null;
                _smartToggle.Reset();
                CancelSmartToggleTimers();
                StopSessionWatchdogTimer();
                _audioPlayer.Stop();
                _sessionState = BluetoothSessionState.Idle;
                if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                sessionIdToClose = _currentSessionId;
                _ = SendClosePacketAsync(sessionIdToClose);
                openTcs?.TrySetResult(false);
                return true;
            }

            if (_sessionState != BluetoothSessionState.Streaming)
            {
                if (_sessionState == BluetoothSessionState.Idle && _smartToggle.IsActive)
                {
                    _smartToggle.Reset();
                    CancelSmartToggleTimers();
                }
                AppLogger.Debug($"StopVoiceSession ignored, current state is {_sessionState}", "BluetoothManager");
                return false;
            }

            _sessionState = BluetoothSessionState.Draining;
            sessionIdToClose = _currentSessionId;
            _lastEndedSessionId = sessionIdToClose;
            _lastEndedSessionTimeMs = Environment.TickCount64;
            currentConnGen = _connectionGeneration;
            currentSessGen = _sessionGeneration;
            currentAudioGen = Volatile.Read(ref _audioGeneration);
            frameSize = _frameSize;
            gain = CurrentGain;
            StopKeepaliveTimer();
            StopSessionWatchdogTimer();
            CancelSmartToggleTimers();
            if (_smartToggle.IsActive)
            {
                _smartToggle.EnterClosing();
            }

            if (!_audioStopReceived)
            {
                _audioStopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                stopTcs = _audioStopTcs;
                needsAwaitStop = true;
            }
        }

        AppLogger.Info($"StopVoiceSession initiated for SessionId={sessionIdToClose}. State -> Draining", "BluetoothManager");

        // Send 0x0D MicClose
        await SendClosePacketAsync(sessionIdToClose).ConfigureAwait(false);

        // Await AUDIO_STOP (0x00) or bounded tail timeout (250ms)
        if (needsAwaitStop && stopTcs != null)
        {
            await Task.WhenAny(stopTcs.Task, Task.Delay(250)).ConfigureAwait(false);
        }

        // Flush ordered audio channel & any trailing partial ADPCM bytes before draining output
        await FlushAudioPipelineAsync(currentAudioGen, frameSize, gain).ConfigureAwait(false);

        // Drain audio player asynchronously OUTSIDE _stateLock to prevent blocking other threads
        await _audioPlayer.DrainAsync(450).ConfigureAwait(false);

        bool emitEnded = false;
        lock (_stateLock)
        {
            if (currentConnGen != _connectionGeneration || currentSessGen != _sessionGeneration)
            {
                if (_sessionState == BluetoothSessionState.Idle && _smartToggle.IsActive)
                {
                    _smartToggle.Reset();
                    CancelSmartToggleTimers();
                }
                AppLogger.Warn("StopVoiceSession completed but session was superseded", "BluetoothManager");
                return false;
            }

            StopKeepaliveTimer();
            StopSessionWatchdogTimer();
            _smartToggle.Reset();
            CancelSmartToggleTimers();
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
        GattCharacteristic? tx;
        ushort version;
        CancellationToken connToken;
        lock (_stateLock)
        {
            tx = _txCharacteristic;
            version = _capabilities?.Version ?? 0x0100;
            connToken = _connectionCts?.Token ?? CancellationToken.None;
        }
        if (tx == null) return;
        try
        {
            byte[] micClose = ATVVProtocol.BuildMicrophoneClose(sessionId, version);
            await WriteCharacteristicWithTimeoutAsync(tx, micClose, MicCloseWriteTimeout, connToken).ConfigureAwait(false);
        }
        catch { }
    }

    private void StartKeepaliveTimer()
    {
        StopKeepaliveTimer();
        // Xiaomi remotes & ATVV v0.4 do not support 0x0E MIC_EXTEND
        if ((_capabilities?.Version ?? 0x0100) < 0x0100) return;
        if (!G9N9NProtocol.IsCandidate(ConnectedDeviceName) && !T1RemoteProtocol.IsCandidate(ConnectedDeviceName)) return;

        // Bind keepalive callbacks to this session and do not overlap slow GATT writes.
        int connectionGeneration = _connectionGeneration;
        int sessionGeneration = _sessionGeneration;
        int writePending = 0;
        int consecutiveFailures = 0;
        _keepaliveTimer = new System.Threading.Timer(async _ =>
        {
            if (Interlocked.Exchange(ref writePending, 1) != 0) return;
            try
            {
                GattCharacteristic tx;
                byte sessId;
                CancellationToken connToken;
                lock (_stateLock)
                {
                    if (connectionGeneration != _connectionGeneration || sessionGeneration != _sessionGeneration ||
                        _sessionState != BluetoothSessionState.Streaming || _txCharacteristic == null) return;
                    tx = _txCharacteristic;
                    sessId = _currentSessionId;
                    connToken = _connectionCts?.Token ?? CancellationToken.None;
                }
                byte[] extend = ATVVProtocol.BuildMicrophoneExtend(sessId);
                var status = await WriteCharacteristicWithTimeoutAsync(tx, extend, MicCloseWriteTimeout, connToken)
                    .ConfigureAwait(false);
                AppLogger.Debug($"MIC_EXTEND: SessionId={sessId}, Status={status}", "BluetoothManager");
                if (status == GattCommunicationStatus.Success)
                {
                    consecutiveFailures = 0;
                }
                else if (++consecutiveFailures >= 2)
                {
                    AppLogger.Warn($"MIC_EXTEND failed {consecutiveFailures} consecutive times (Status={status}); stopping session", "BluetoothManager");
                    _ = StopVoiceSessionAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Keepalive extend failed: {ex.Message}", "BluetoothManager");
                if (++consecutiveFailures >= 2)
                {
                    AppLogger.Warn("Keepalive write failed consecutively; stopping voice session", "BluetoothManager");
                    _ = StopVoiceSessionAsync();
                }
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

    private void StartSessionWatchdogTimer()
    {
        StopSessionWatchdogTimer();
        int connGen = _connectionGeneration;
        int sessGen = _sessionGeneration;
        int triggering = 0;
        _sessionWatchdogTimer = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref triggering) != 0) return;
            try
            {
                bool isAwaitingReopen = _smartToggle.IsAwaitingReopen;
                lock (_stateLock)
                {
                    if (connGen != _connectionGeneration || sessGen != _sessionGeneration ||
                        _sessionState != BluetoothSessionState.Streaming)
                    {
                        return;
                    }
                }

                long now = Environment.TickCount64;
                long idleMs = now - Volatile.Read(ref _lastAudioPacketTimeMs);
                long elapsedMs = now - Volatile.Read(ref _sessionStartedTimeMs);
                if (elapsedMs >= GlobalMaxSessionDurationMs)
                {
                    if (Interlocked.Exchange(ref triggering, 1) == 0)
                    {
                        AppLogger.Warn($"Global voice session limit ({GlobalMaxSessionDurationMs}ms) reached; forcing session stop", "BluetoothManager");
                        _ = StopVoiceSessionAsync();
                    }
                }
                else if (!isAwaitingReopen && idleMs >= NoAudioPacketTimeoutMs)
                {
                    if (Interlocked.Exchange(ref triggering, 1) == 0)
                    {
                        AppLogger.Warn($"Voice audio packet inactivity exceeded {idleMs}ms; forcing session stop", "BluetoothManager");
                        _ = StopVoiceSessionAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error in session watchdog timer", ex, "BluetoothManager");
            }
        }, null, 1000, 1000);
    }

    private void StopSessionWatchdogTimer()
    {
        _sessionWatchdogTimer?.Dispose();
        _sessionWatchdogTimer = null;
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
        GattCharacteristic? tx;
        ushort version;
        byte codec;
        CancellationToken connToken;
        lock (_stateLock)
        {
            if (_sessionState != BluetoothSessionState.Streaming ||
                !_smartToggle.IsAwaitingReopen ||
                _smartToggle.Generation != generation)
            {
                return;
            }
            tx = _txCharacteristic;
            version = _capabilities?.Version ?? 0x0100;
            codec = _capabilities?.SelectedCodec ?? 0x02;
            connToken = _connectionCts?.Token ?? CancellationToken.None;
        }
        if (tx == null) return;

        try
        {
            byte[] micOpen = ATVVProtocol.BuildMicrophoneOpen(version, codec);
            var writeStatus = await WriteCharacteristicWithTimeoutAsync(tx, micOpen, MicOpenWriteTimeout, connToken)
                .ConfigureAwait(false);
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
        int currentAudioGen;
        int frameSize;
        float gain;
        lock (_stateLock)
        {
            if (sender != _audioCharacteristic) return;
            if (_sessionState is not (BluetoothSessionState.Opening or BluetoothSessionState.Streaming or BluetoothSessionState.Draining))
            {
                return;
            }
            currentAudioGen = Volatile.Read(ref _audioGeneration);
            frameSize = _frameSize;
            gain = CurrentGain;
        }

        uint lenU = args.CharacteristicValue.Length;
        if (lenU == 0) return;
        int len = (int)lenU;

        Volatile.Write(ref _lastAudioPacketTimeMs, Environment.TickCount64);

        byte[] rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            args.CharacteristicValue.CopyTo(0, rented, 0, len);
            if (!_audioChannel.Writer.TryWrite(new AudioWorkItem(
                AudioWorkKind.Audio, currentAudioGen, rented, len, frameSize, gain, 0, 0, 0, false, null)))
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    private async Task ProcessAudioChannelAsync()
    {
        try
        {
            await foreach (var item in _audioChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    switch (item.Kind)
                    {
                        case AudioWorkKind.Reset:
                            lock (_audioLock)
                            {
                                if (item.AudioGeneration != Volatile.Read(ref _audioGeneration)) break;
                                _frameAccumulator.Reset();
                                _decoder.Reset();
                                if (item.ResetLowNibbleFirst) _decoder.LowNibbleFirst = false;
                                _postprocessor.Reset();
                            }
                            item.Completion?.TrySetResult(true);
                            break;

                        case AudioWorkKind.Audio:
                            float? latestLevel = null;
                            lock (_audioLock)
                            {
                                if (item.AudioGeneration != Volatile.Read(ref _audioGeneration) || item.PooledBuffer == null)
                                    break;
                                _frameAccumulator.Append(item.PooledBuffer.AsSpan(0, item.Length), item.FrameSize, frame =>
                                {
                                    latestLevel = DecodeAndPlayFrameLocked(frame, item.Gain);
                                });
                            }
                            if (latestLevel.HasValue)
                            {
                                AudioLevelChanged?.Invoke(CurrentEventAddress, latestLevel.Value);
                            }
                            break;

                        case AudioWorkKind.Sync:
                            lock (_audioLock)
                            {
                                if (item.AudioGeneration != Volatile.Read(ref _audioGeneration)) break;
                                _frameAccumulator.Synchronize(item.Predictor, item.StepIndex, item.FrameNumber);
                            }
                            break;

                        case AudioWorkKind.Flush:
                            float? flushLevel = null;
                            lock (_audioLock)
                            {
                                if (item.AudioGeneration == Volatile.Read(ref _audioGeneration))
                                {
                                    _frameAccumulator.Flush(item.FrameSize, partial =>
                                    {
                                        flushLevel = DecodeAndPlayFrameLocked(partial, item.Gain);
                                    });
                                }
                            }
                            if (flushLevel.HasValue)
                            {
                                AudioLevelChanged?.Invoke(CurrentEventAddress, flushLevel.Value);
                            }
                            item.Completion?.TrySetResult(true);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Audio pipeline error processing {item.Kind}", ex, "BluetoothManager");
                    item.Completion?.TrySetResult(false);
                }
                finally
                {
                    if (item.PooledBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(item.PooledBuffer);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Audio channel consumer exited unexpectedly", ex, "BluetoothManager");
        }
    }

    private float DecodeAndPlayFrameLocked(ReadOnlySpan<byte> frame, float gain)
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
        return AudioLevelMeter.CalculateRms(sampleSpan);
    }

    internal G9N9NSmartToggleSession.Phase SmartTogglePhaseForTest => _smartToggle.CurrentPhase;
    internal BluetoothSessionState SessionStateForTest => _sessionState;

    internal void SetSessionStateForTest(BluetoothSessionState state, byte sessionId = 0, G9N9NSmartToggleSession.Phase? smartTogglePhase = null)
    {
        lock (_stateLock)
        {
            _sessionState = state;
            _currentSessionId = sessionId;
        }
        if (smartTogglePhase.HasValue)
        {
            _smartToggle.Reset();
            switch (smartTogglePhase.Value)
            {
                case G9N9NSmartToggleSession.Phase.PhysicalPressed:
                    _smartToggle.BeginPhysicalPress(Environment.TickCount64);
                    break;
                case G9N9NSmartToggleSession.Phase.AwaitingReopen:
                    _smartToggle.BeginPhysicalPress(Environment.TickCount64);
                    _smartToggle.EnterAwaitingReopen();
                    break;
                case G9N9NSmartToggleSession.Phase.HandsFreeActive:
                    _smartToggle.BeginPhysicalPress(Environment.TickCount64);
                    _smartToggle.EnterAwaitingReopen();
                    _smartToggle.EnterHandsFreeActive();
                    break;
                case G9N9NSmartToggleSession.Phase.Closing:
                    _smartToggle.BeginPhysicalPress(Environment.TickCount64);
                    _smartToggle.EnterClosing();
                    break;
            }
        }
    }

    internal void HandleControlPacketForTest(byte[] data)
    {
        int capturedConnGen;
        int capturedAudioGen;
        byte defaultCodec;
        lock (_stateLock)
        {
            capturedConnGen = _connectionGeneration;
            capturedAudioGen = Volatile.Read(ref _audioGeneration);
            defaultCodec = _capabilities?.SelectedCodec ?? 0x02;
        }
        if (data.Length == 0) return;
        ProcessControlPacket(data, capturedConnGen, capturedAudioGen, defaultCodec);
    }

    private void OnControlValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        int capturedConnGen;
        int capturedAudioGen;
        byte defaultCodec;
        lock (_stateLock)
        {
            if (sender != _controlCharacteristic) return;
            capturedConnGen = _connectionGeneration;
            capturedAudioGen = Volatile.Read(ref _audioGeneration);
            defaultCodec = _capabilities?.SelectedCodec ?? 0x02;
        }

        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] data);
        if (data.Length == 0) return;
        ProcessControlPacket(data, capturedConnGen, capturedAudioGen, defaultCodec);
    }

    private void ProcessControlPacket(byte[] data, int capturedConnGen, int capturedAudioGen, byte defaultCodec)
    {
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
                        _capabilities = caps;
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
                if (ATVVProtocol.ParseAudioStart(data, out byte interaction, out byte codec, out byte sessionId, defaultCodec))
                {
                    AppLogger.Info($"ATVV packet AudioStart (0x04): Interaction=0x{interaction:X2}, Codec=0x{codec:X2}, SessionId={sessionId}", "BluetoothManager");
                    BluetoothSessionState lockedState;
                    byte lockedSessionId;
                    lock (_stateLock)
                    {
                        if (capturedConnGen != _connectionGeneration) return;
                        lockedState = _sessionState;
                        lockedSessionId = _currentSessionId;
                        if (lockedState == BluetoothSessionState.Idle && _smartToggle.IsActive)
                        {
                            _smartToggle.Reset();
                            CancelSmartToggleTimers();
                        }
                    }

                    if (lockedState is BluetoothSessionState.Streaming or BluetoothSessionState.Opening && IsSmartToggleEligible())
                    {
                        if (_smartToggle.IsAwaitingReopen)
                        {
                            var gen = _smartToggle.EnterHandsFreeActive();
                            CancelSmartToggleReopenTimer();
                            if (gen.HasValue) ScheduleSmartToggleDeadlineTimer(gen.Value);
                            Volatile.Write(ref _lastAudioPacketTimeMs, Environment.TickCount64);
                            int reopenAudioGen;
                            bool alreadyStreaming;
                            lock (_stateLock)
                            {
                                if (capturedConnGen != _connectionGeneration) return;
                                _currentSessionId = sessionId;
                                reopenAudioGen = Interlocked.Increment(ref _audioGeneration);
                                alreadyStreaming = _sessionState == BluetoothSessionState.Streaming;
                            }
                            EnqueueAudioReset(reopenAudioGen, resetLowNibbleFirst: false);
                            AppLogger.Info($"G9N9N smart-toggle reopened (SessionId={sessionId}); hands-free speech active", "BluetoothManager");
                            if (alreadyStreaming) return;
                        }
                        else if (_smartToggle.ShouldStopOnRemoteAudioStart(interaction, lockedSessionId, sessionId))
                        {
                            _smartToggle.EnterClosing();
                            CancelSmartToggleTimers();
                            if (sessionId != lockedSessionId)
                            {
                                _ = SendClosePacketAsync(sessionId);
                            }
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
                        TaskCompletionSource<bool>? openTcs = null;
                        lock (_stateLock)
                        {
                            if (capturedConnGen != _connectionGeneration) return;
                            openTcs = _audioStartTcs;
                            _audioStartTcs = null;
                            _audioPlayer.Stop();
                            _sessionState = BluetoothSessionState.Idle;
                            if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                            StopKeepaliveTimer();
                            StopSessionWatchdogTimer();
                            if (_voiceSessionStartedEmitted)
                            {
                                shouldEmitEnded = true;
                                _voiceSessionStartedEmitted = false;
                            }
                        }
                        openTcs?.TrySetResult(false);
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
                    bool hostConfirmed = false;
                    TaskCompletionSource<bool>? confirmedOpenTcs = null;
                    int remoteSessionGen = 0;
                    int remoteAudioGen = 0;
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
                                remoteAudioGen = Interlocked.Increment(ref _audioGeneration);
                                _activeOpeningOperationId++;
                                remoteInitiated = true;
                                long now = Environment.TickCount64;
                                Volatile.Write(ref _lastAudioPacketTimeMs, now);
                                Volatile.Write(ref _sessionStartedTimeMs, now);
                            }
                        }
                        else if (_sessionState == BluetoothSessionState.Opening)
                        {
                            _currentSessionId = sessionId;
                            if (_audioStartTcs != null)
                            {
                                _sessionState = BluetoothSessionState.Streaming;
                                _voiceSessionStartedEmitted = true;
                                Volatile.Write(ref _lastAudioPacketTimeMs, Environment.TickCount64);
                                StartKeepaliveTimer();
                                StartSessionWatchdogTimer();
                                confirmedOpenTcs = _audioStartTcs;
                                _audioStartTcs = null;
                                hostConfirmed = true;
                            }
                        }
                    }

                    if (wasCancelledOrEnded)
                    {
                        AppLogger.Info($"AudioStart received while state={_sessionState}/cancelPending={_cancelOpenPending}, sending close", "BluetoothManager");
                        _ = SendClosePacketAsync(sessionId);
                    }
                    else if (hostConfirmed)
                    {
                        AppLogger.Info($"Host-initiated voice session {sessionId} confirmed by AudioStart. State -> Streaming", "BluetoothManager");
                        VoiceSessionStarted?.Invoke(CurrentEventAddress);
                        confirmedOpenTcs?.TrySetResult(true);
                    }
                    else if (remoteInitiated)
                    {
                        try
                        {
                            EnqueueAudioReset(remoteAudioGen, resetLowNibbleFirst: false);
                            bool rollbackSendClose = false;
                            lock (_stateLock)
                            {
                                if (capturedConnGen != _connectionGeneration || remoteSessionGen != _sessionGeneration)
                                {
                                    return;
                                }
                                if (_cancelOpenPending || _sessionState != BluetoothSessionState.Opening)
                                {
                                    _audioPlayer.Stop();
                                    _sessionState = BluetoothSessionState.Idle;
                                    if (!_voiceSessionStartedEmitted) _voiceArbiter?.Release(this);
                                    rollbackSendClose = true;
                                }
                                else
                                {
                                    _sessionState = BluetoothSessionState.Streaming;
                                    _voiceSessionStartedEmitted = true;
                                    StartKeepaliveTimer();
                                    StartSessionWatchdogTimer();
                                }
                            }
                            if (rollbackSendClose)
                            {
                                _ = SendClosePacketAsync(sessionId);
                                return;
                            }
                            AppLogger.Info($"Accepted remote-initiated voice session {sessionId}. State -> Streaming", "BluetoothManager");
                            ulong eventAddr = CurrentEventAddress;
                            VoiceButtonPressed?.Invoke(eventAddr);
                            VoiceSessionStarted?.Invoke(eventAddr);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("Exception while starting remote-initiated voice session", ex, "BluetoothManager");
                            bool shouldClose = false;
                            bool shouldEmitEnded = false;
                            lock (_stateLock)
                            {
                                if (capturedConnGen == _connectionGeneration && remoteSessionGen == _sessionGeneration)
                                {
                                    StopKeepaliveTimer();
                                    StopSessionWatchdogTimer();
                                    _audioPlayer.Stop();
                                    _sessionState = BluetoothSessionState.Idle;
                                    if (_voiceSessionStartedEmitted)
                                    {
                                        shouldEmitEnded = true;
                                        _voiceSessionStartedEmitted = false;
                                    }
                                    else
                                    {
                                        _voiceArbiter?.Release(this);
                                    }
                                    shouldClose = true;
                                }
                            }
                            if (shouldEmitEnded) EndVoiceSession(CurrentEventAddress);
                            if (shouldClose) _ = SendClosePacketAsync(sessionId);
                        }
                    }
                }
                break;

            case ATVVProtocol.OpcodeMicrophoneRefused: // 0x0C
                AppLogger.Warn("ATVV packet MicrophoneRefused (0x0C) received from remote", "BluetoothManager");
                bool emitRefusedEnded = false;
                TaskCompletionSource<bool>? refusedOpenTcs = null;
                lock (_stateLock)
                {
                    if (capturedConnGen != _connectionGeneration) return;
                    _activeOpeningOperationId++;
                    _smartToggle.Reset();
                    CancelSmartToggleTimers();
                    refusedOpenTcs = _audioStartTcs;
                    _audioStartTcs = null;
                    if (_sessionState == BluetoothSessionState.Opening || _sessionState == BluetoothSessionState.Streaming)
                    {
                        StopKeepaliveTimer();
                        StopSessionWatchdogTimer();
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
                refusedOpenTcs?.TrySetResult(false);
                if (emitRefusedEnded)
                {
                    EndVoiceSession(CurrentEventAddress);
                }
                break;

            case ATVVProtocol.OpcodeAudioSync: // 0x0A
                if (ATVVProtocol.ParseAudioSync(data, out short predictor, out byte stepIndex, out ushort frameNumber))
                {
                    AppLogger.Debug($"ATVV packet AudioSync (0x0A): Frame={frameNumber}, Predictor={predictor}, StepIndex={stepIndex}", "BluetoothManager");
                    _audioChannel.Writer.TryWrite(new AudioWorkItem(
                        AudioWorkKind.Sync, capturedAudioGen, null, 0, 0, 0, predictor, stepIndex, frameNumber, false, null));
                }
                break;

            case ATVVProtocol.OpcodeStartSearch: // 0x08
                AppLogger.Info("ATVV packet StartSearch (0x08) received from remote (voice key pressed)", "BluetoothManager");
                BluetoothSessionState searchState;
                lock (_stateLock)
                {
                    if (capturedConnGen != _connectionGeneration) return;
                    searchState = _sessionState;
                    if (searchState == BluetoothSessionState.Idle && _smartToggle.IsActive)
                    {
                        _smartToggle.Reset();
                        CancelSmartToggleTimers();
                    }
                }

                VoiceButtonPressed?.Invoke(CurrentEventAddress);
                if (_voiceArbiter?.Owner is { } owner && owner != this) return;

                if (searchState == BluetoothSessionState.Draining)
                {
                    AppLogger.Debug("Ignoring trailing StartSearch (0x08) while voice session is draining", "BluetoothManager");
                    return;
                }

                if (searchState == BluetoothSessionState.Streaming)
                {
                    if (IsSmartToggleEligible())
                    {
                        if (_smartToggle.IsActive && _smartToggle.ShouldStopOnRemoteOpenRequest())
                        {
                            AppLogger.Info("G9N9N smart-toggle voice key pressed second time; stopping hands-free session", "BluetoothManager");
                            _smartToggle.EnterClosing();
                            CancelSmartToggleTimers();
                            _ = StopVoiceSessionAsync();
                        }
                        else if (!_smartToggle.IsActive)
                        {
                            _smartToggle.BeginPhysicalPress(Environment.TickCount64);
                        }
                    }
                    return;
                }

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
                byte stopReason = data.Length > 1 ? data[1] : (byte)0xFF;
                BluetoothSessionState stopObservedState;
                byte stopObservedSessionId;
                lock (_stateLock)
                {
                    if (capturedConnGen != _connectionGeneration) return;
                    stopObservedState = _sessionState;
                    stopObservedSessionId = _currentSessionId;
                    if (_sessionState == BluetoothSessionState.Idle)
                    {
                        _cancelOpenPending = false;
                        if (_smartToggle.IsActive)
                        {
                            _smartToggle.Reset();
                            CancelSmartToggleTimers();
                        }
                        AppLogger.Info($"ATVV AudioStop received: Reason={(data.Length > 1 ? $"0x{data[1]:X2}" : "missing")}, State={stopObservedState}, SessionId={stopObservedSessionId}", "BluetoothManager");
                        return;
                    }
                    _audioStopReceived = true;
                }
                AppLogger.Info($"ATVV AudioStop received: Reason={(data.Length > 1 ? $"0x{data[1]:X2}" : "missing")}, State={stopObservedState}, SessionId={stopObservedSessionId}", "BluetoothManager");
                _audioStopTcs?.TrySetResult(true);

                if (stopObservedState is BluetoothSessionState.Streaming or BluetoothSessionState.Opening &&
                    IsSmartToggleEligible() && _smartToggle.IsActive)
                {
                    bool isRemotePhysicalStop = stopReason != 0x00;
                    bool shouldReopen = _smartToggle.ShouldReopenOnPhysicalStop(isRemoteStop: isRemotePhysicalStop, localCloseRequested: false, Environment.TickCount64);
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

                if (stopObservedState == BluetoothSessionState.Streaming || stopObservedState == BluetoothSessionState.Opening)
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
        int nextAudioGen;
        BluetoothLEDevice? devToDispose = null;
        CancellationTokenSource? ctsToCancel = null;
        TaskCompletionSource<bool>? openTcsToCancel = null;

        lock (_stateLock)
        {
            disconnectedAddress = _connectedDevice?.BluetoothAddress ?? (ulong)Volatile.Read(ref _deviceAddress);
            _connectionGeneration++;
            _sessionGeneration++;
            _activeOpeningOperationId++;
            nextAudioGen = Interlocked.Increment(ref _audioGeneration);
            ctsToCancel = _connectionCts;
            _connectionCts = null;
            openTcsToCancel = _audioStartTcs;
            _audioStartTcs = null;

            if (_voiceSessionStartedEmitted)
            {
                emitEnded = true;
                _voiceSessionStartedEmitted = false;
            }
            _sessionState = BluetoothSessionState.Idle;
            _cancelOpenPending = false;
            StopKeepaliveTimer();
            StopSessionWatchdogTimer();
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
            _capabilities = null;
            _currentSessionId = 0;
            IsReady = false;
            _lastEndedSessionId = null;
            _audioStopReceived = false;
            ConnectedDeviceContainerId = null;
            ConnectedDeviceInformation = null;
            ConnectedEtwAttributeFilter = default;
            _capabilitiesTcs?.TrySetCanceled();
            _audioStopTcs?.TrySetCanceled();
        }

        try { ctsToCancel?.Cancel(); } catch { }
        ctsToCancel?.Dispose();
        openTcsToCancel?.TrySetResult(false);

        // Reset audio pipeline outside _stateLock to prevent AB-BA deadlock
        lock (_audioLock)
        {
            _frameAccumulator.Reset();
            _decoder.Reset();
            _decoder.LowNibbleFirst = false;
            _postprocessor.Reset();
        }
        EnqueueAudioReset(nextAudioGen, resetLowNibbleFirst: true);

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
        _audioChannel.Writer.TryComplete();
        try { _audioWorkerTask.Wait(250); } catch { }
        _audioPlayer.Dispose();
    }
}
