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
    private readonly object _remotesLock = new();
    private readonly VoiceSessionArbiter _voice = new();
    private readonly Action<RemoteDevice> _scannerDiscoveredHandler;
    private ulong _lastAddress;
    private int _disposed;

    public MultiRemoteBluetoothManager()
    {
        _scannerDiscoveredHandler = device => DeviceDiscovered?.Invoke(device);
        _scanner.DeviceDiscovered += _scannerDiscoveredHandler;
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
        var remote = GetOrCreate(device.BluetoothAddress);
        remote.CurrentGain = device.Gain;
        remote.VoiceButtonMode = device.VoiceButtonMode;
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
            Action<ulong, bool, string> connHandler = (addr, connected, message) =>
            {
                ulong effective = addr != 0 ? addr : address;
                if (connected)
                    Interlocked.Exchange(ref _lastAddress, effective);
                else
                    Interlocked.CompareExchange(ref _lastAddress, 0, effective);
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

    public Task<bool> ConnectAsync(ulong address) => GetOrCreate(address).ConnectAsync(address);
    public void DisconnectDevice(ulong address) => _remotes.GetValueOrDefault(address)?.Remote.Disconnect();

    public void RemoveDevice(ulong address)
    {
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
