// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Platform.Windows.Bluetooth;

/// <summary>Owns one BLE transport per remote and shares a single voice lease.</summary>
public sealed class MultiRemoteBluetoothManager : IBluetoothManager
{
    private sealed record ManagedRemote(
        WinRtBluetoothManager Remote,
        Action<ulong, bool, string> ConnectionStatusHandler,
        Action<ulong> DeviceInfoHandler,
        Action<ulong, float> AudioLevelHandler,
        Action<ulong> VoiceButtonHandler,
        Action<ulong> VoiceStartedHandler,
        Action<ulong> VoiceEndedHandler,
        Action<ulong, string> AudioOutputFailedHandler);

    private readonly WinRtBluetoothManager _scanner = new();
    private readonly ConcurrentDictionary<ulong, ManagedRemote> _remotes = new();
    private readonly ConcurrentDictionary<ulong, (float Gain, G9N9NVoiceButtonMode VoiceButtonMode)> _voiceSettings = new();
    private readonly object _remotesLock = new();
    private readonly VoiceSessionArbiter _voice = new();
    private readonly Action<RemoteDevice> _scannerDiscoveredHandler;
    private readonly WinRtBluetoothConnectionWatcher _connectionWatcher = new();
    private readonly BluetoothAutoReconnectCoordinator _autoReconnect;
    private ulong _lastAddress;
    private int _disposed;

    public MultiRemoteBluetoothManager()
    {
        _scannerDiscoveredHandler = device => DeviceDiscovered?.Invoke(device);
        _scanner.DeviceDiscovered += _scannerDiscoveredHandler;
        _autoReconnect = new BluetoothAutoReconnectCoordinator(
            isDeviceReady: IsDeviceReady,
            isDeviceOsConnectedAsync: WinRtBluetoothConnectionWatcher.IsDeviceOsConnectedAsync,
            connectImplAsync: ConnectDirectAsync);
        _connectionWatcher.DeviceOsConnected += OnWatcherDeviceOsConnected;
        _connectionWatcher.DeviceOsDisconnected += OnWatcherDeviceOsDisconnected;
        _connectionWatcher.BluetoothRadioRestored += OnBluetoothRadioRestored;
    }

    private void OnWatcherDeviceOsConnected(ulong address, string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _autoReconnect.OnOsDeviceConnected(address, reason);
    }

    private void OnWatcherDeviceOsDisconnected(ulong address)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _autoReconnect.OnOsDeviceDisconnected(address);
    }

    private void OnBluetoothRadioRestored()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _autoReconnect.NotifySystemResume();
    }

    private WinRtBluetoothManager? Current
    {
        get
        {
            if (_voice.Owner is { IsReady: true, IsConnected: true } owner)
                return owner;
            ulong lastAddr = Interlocked.Read(ref _lastAddress);
            if (lastAddr != 0 && _remotes.GetValueOrDefault(lastAddr)?.Remote is { IsReady: true, IsConnected: true } last)
                return last;
            return _remotes.Values.Select(e => e.Remote).FirstOrDefault(remote => remote.IsReady && remote.IsConnected);
        }
    }

    public bool IsScanning => _scanner.IsScanning;
    public bool IsConnected => Current != null;
    public bool IsReady => IsConnected;
    public bool IsStreaming => _voice.Owner?.IsStreaming == true;
    public BluetoothSessionState SessionState => _voice.Owner?.SessionState ?? BluetoothSessionState.Idle;
    public ulong VoiceDeviceAddress => _voice.Owner is { } owner
        ? _remotes.FirstOrDefault(pair => pair.Value.Remote == owner).Key : 0;
    public IReadOnlyCollection<ulong> ConnectedDeviceAddresses => _remotes
        .Where(pair => pair.Value.Remote is { IsReady: true, IsConnected: true })
        .Select(pair => pair.Key)
        .ToArray();
    public string ConnectedDeviceName => Current?.ConnectedDeviceName ?? string.Empty;
    public ulong ConnectedDeviceAddress => Current?.ConnectedDeviceAddress ?? 0;
    public Guid? ConnectedDeviceContainerId => Current?.ConnectedDeviceContainerId;
    public RemoteDeviceInformation? ConnectedDeviceInformation => Current?.ConnectedDeviceInformation;
    public float CurrentGain { get => Current?.CurrentGain ?? RemoteDevice.DefaultMicrophoneGain; set { if (Current is { } remote) remote.CurrentGain = value; } }
    public G9N9NVoiceButtonMode VoiceButtonMode { get => Current?.VoiceButtonMode ?? G9N9NVoiceButtonMode.SmartToggle; set { if (Current is { } remote) remote.VoiceButtonMode = value; } }
    public bool IsDeviceReady(ulong address) => _remotes.GetValueOrDefault(address)?.Remote is { IsReady: true, IsConnected: true };
    public Guid? GetDeviceContainerId(ulong address) => _remotes.GetValueOrDefault(address)?.Remote.ConnectedDeviceContainerId;
    public RemoteDeviceInformation? GetDeviceInformation(ulong address) => _remotes.GetValueOrDefault(address)?.Remote.ConnectedDeviceInformation;
    public void SetDeviceVoiceSettings(RemoteDevice device)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _voiceSettings[device.BluetoothAddress] = (device.Gain, device.VoiceButtonMode);
        if (_remotes.TryGetValue(device.BluetoothAddress, out var existing))
        {
            existing.Remote.CurrentGain = device.Gain;
            existing.Remote.VoiceButtonMode = device.VoiceButtonMode;
        }
    }

    public event Action<RemoteDevice>? DeviceDiscovered;
    public event Action<ulong, bool, string>? ConnectionStatusChanged;
    public event Action<ulong>? ConnectedDeviceInformationChanged;
    public event Action<ulong, float>? AudioLevelChanged;
    public event Action<ulong>? VoiceButtonPressed;
    public event Action<ulong>? VoiceSessionStarted;
    public event Action<ulong>? VoiceSessionEnded;
    public event Action<ulong, string>? AudioOutputFailed;

    public void StartScanning() => _scanner.StartScanning();
    public void StopScanning() => _scanner.StopScanning();
    public Task DiscoverPairedRemotesAsync() => _scanner.DiscoverPairedRemotesAsync();
    public void StartScan() => StartScanning();
    public void StopScan() => StopScanning();

    private WinRtBluetoothManager GetOrCreate(ulong address)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_remotes.TryGetValue(address, out var fastExisting)) return fastExisting.Remote;

        lock (_remotesLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_remotes.TryGetValue(address, out var existing)) return existing.Remote;

            var remote = new WinRtBluetoothManager(_voice);
            if (_voiceSettings.TryGetValue(address, out var savedSettings))
            {
                remote.CurrentGain = savedSettings.Gain;
                remote.VoiceButtonMode = savedSettings.VoiceButtonMode;
            }
            Action<ulong, bool, string> connHandler = (addr, connected, message) =>
            {
                ulong effective = addr != 0 ? addr : address;
                if (connected)
                    Interlocked.Exchange(ref _lastAddress, effective);
                else
                    Interlocked.CompareExchange(ref _lastAddress, 0, effective);
                _autoReconnect.OnConnectionStatusChanged(effective, connected);
                ConnectionStatusChanged?.Invoke(effective, connected, message);
            };
            Action<ulong> infoHandler = addr => ConnectedDeviceInformationChanged?.Invoke(addr != 0 ? addr : address);
            Action<ulong, float> levelHandler = (addr, level) =>
            {
                if (_voice.Owner == remote) AudioLevelChanged?.Invoke(addr != 0 ? addr : address, level);
            };
            Action<ulong> buttonHandler = addr => VoiceButtonPressed?.Invoke(addr != 0 ? addr : address);
            Action<ulong> startedHandler = addr => VoiceSessionStarted?.Invoke(addr != 0 ? addr : address);
            Action<ulong> endedHandler = addr => VoiceSessionEnded?.Invoke(addr != 0 ? addr : address);
            Action<ulong, string> audioFailedHandler = (addr, reason) => AudioOutputFailed?.Invoke(addr != 0 ? addr : address, reason);

            remote.ConnectionStatusChanged += connHandler;
            remote.ConnectedDeviceInformationChanged += infoHandler;
            remote.AudioLevelChanged += levelHandler;
            remote.VoiceButtonPressed += buttonHandler;
            remote.VoiceSessionStarted += startedHandler;
            remote.VoiceSessionEnded += endedHandler;
            remote.AudioOutputFailed += audioFailedHandler;

            _remotes[address] = new ManagedRemote(
                remote, connHandler, infoHandler, levelHandler,
                buttonHandler, startedHandler, endedHandler, audioFailedHandler);
            return remote;
        }
    }

    private static void DetachAndDispose(ManagedRemote entry)
    {
        try
        {
            entry.Remote.Dispose();
        }
        finally
        {
            entry.Remote.ConnectionStatusChanged -= entry.ConnectionStatusHandler;
            entry.Remote.ConnectedDeviceInformationChanged -= entry.DeviceInfoHandler;
            entry.Remote.AudioLevelChanged -= entry.AudioLevelHandler;
            entry.Remote.VoiceButtonPressed -= entry.VoiceButtonHandler;
            entry.Remote.VoiceSessionStarted -= entry.VoiceStartedHandler;
            entry.Remote.VoiceSessionEnded -= entry.VoiceEndedHandler;
            entry.Remote.AudioOutputFailed -= entry.AudioOutputFailedHandler;
        }
    }

    private Task<bool> ConnectDirectAsync(ulong address)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.FromResult(false);
        return GetOrCreate(address).ConnectAsync(address);
    }

    public Task<bool> ConnectAsync(ulong address)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.FromResult(false);
        return _autoReconnect.ConnectAsync(address);
    }

    public void DisconnectDevice(ulong address)
    {
        _autoReconnect.MarkUserDisconnected(address);
        _remotes.GetValueOrDefault(address)?.Remote.Disconnect();
    }

    public void SetAutoReconnectTargets(IEnumerable<ulong> addresses)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _autoReconnect.SetTargets(addresses);
        if (_autoReconnect.HasTargets)
        {
            _connectionWatcher.EnsureStarted();
        }
    }

    public void NotifySystemResume()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_autoReconnect.HasTargets)
        {
            _connectionWatcher.EnsureStarted();
        }
        _autoReconnect.NotifySystemResume();
    }

    public void NotifyHidDeviceArrival()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _autoReconnect.NotifyHidDeviceArrival();
    }

    public void RemoveDevice(ulong address)
    {
        _autoReconnect.RemoveTarget(address);
        _voiceSettings.TryRemove(address, out _);
        ManagedRemote? removed;
        lock (_remotesLock)
        {
            _remotes.TryRemove(address, out removed);
            Interlocked.CompareExchange(ref _lastAddress, 0, address);
        }
        if (removed != null)
        {
            DetachAndDispose(removed);
        }
    }

    public void Disconnect()
    {
        foreach (var entry in _remotes.Values) entry.Remote.Disconnect();
        Interlocked.Exchange(ref _lastAddress, 0);
    }

    public Task<bool> StartVoiceSessionAsync() => Current?.StartVoiceSessionAsync() ?? Task.FromResult(false);
    public Task<bool> StopVoiceSessionAsync() => _voice.Owner?.StopVoiceSessionAsync() ?? Task.FromResult(false);
    public Task<bool> StartVoiceSessionAsync(ulong address) => _remotes.GetValueOrDefault(address)?.Remote.StartVoiceSessionAsync() ?? Task.FromResult(false);
    public Task<bool> StopVoiceSessionAsync(ulong address) => _voice.Owner == _remotes.GetValueOrDefault(address)?.Remote && _voice.Owner is { } owner
        ? owner.StopVoiceSessionAsync() : Task.FromResult(false);
    public Task ProbeButtonSourceAsync(Guid marker, ulong address) => _remotes.GetValueOrDefault(address)?.Remote.ProbeButtonSourceAsync(marker) ?? Task.CompletedTask;
    public EtwAttributeFilter GetEtwAttributeFilter(ulong address) => _remotes.GetValueOrDefault(address)?.Remote.ConnectedEtwAttributeFilter ?? default;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _connectionWatcher.DeviceOsConnected -= OnWatcherDeviceOsConnected;
        _connectionWatcher.DeviceOsDisconnected -= OnWatcherDeviceOsDisconnected;
        _connectionWatcher.BluetoothRadioRestored -= OnBluetoothRadioRestored;
        _connectionWatcher.Dispose();
        _autoReconnect.Dispose();

        ManagedRemote[] snapshot;
        lock (_remotesLock)
        {
            snapshot = _remotes.Values.ToArray();
            _remotes.Clear();
            Interlocked.Exchange(ref _lastAddress, 0);
        }

        _scanner.DeviceDiscovered -= _scannerDiscoveredHandler;
        _scanner.Dispose();
        foreach (var entry in snapshot) DetachAndDispose(entry);
    }
}

internal sealed class VoiceSessionArbiter
{
    private WinRtBluetoothManager? _owner;

    public WinRtBluetoothManager? Owner => Volatile.Read(ref _owner);

    public bool TryAcquire(WinRtBluetoothManager remote)
    {
        var prior = Interlocked.CompareExchange(ref _owner, remote, null);
        return prior == null;
    }

    public void Release(WinRtBluetoothManager remote) => Interlocked.CompareExchange(ref _owner, null, remote);
}
