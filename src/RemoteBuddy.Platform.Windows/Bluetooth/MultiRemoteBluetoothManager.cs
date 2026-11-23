// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Platform.Windows.Bluetooth;

/// <summary>Owns one BLE transport per remote and shares a single voice lease.</summary>
public sealed class MultiRemoteBluetoothManager : IBluetoothManager
{
    private readonly WinRtBluetoothManager _scanner = new();
    private readonly ConcurrentDictionary<ulong, WinRtBluetoothManager> _remotes = new();
    private readonly VoiceSessionArbiter _voice = new();
    private ulong _lastAddress;

    public MultiRemoteBluetoothManager() => _scanner.DeviceDiscovered += device => DeviceDiscovered?.Invoke(device);

    private WinRtBluetoothManager? Current => _remotes.GetValueOrDefault(_lastAddress) is { IsReady: true } last
        ? last : _remotes.Values.FirstOrDefault(remote => remote.IsReady);

    public bool IsScanning => _scanner.IsScanning;
    public bool IsConnected => Current != null;
    public bool IsReady => IsConnected;
    public bool IsStreaming => _voice.Owner?.IsStreaming == true;
    public BluetoothSessionState SessionState => _voice.Owner?.SessionState ?? BluetoothSessionState.Idle;
    public ulong VoiceDeviceAddress => _voice.Owner is { } owner ? _remotes.FirstOrDefault(pair => pair.Value == owner).Key : 0;
    public string ConnectedDeviceName => Current?.ConnectedDeviceName ?? string.Empty;
    public ulong ConnectedDeviceAddress => Current?.ConnectedDeviceAddress ?? 0;
    public Guid? ConnectedDeviceContainerId => Current?.ConnectedDeviceContainerId;
    public RemoteDeviceInformation? ConnectedDeviceInformation => Current?.ConnectedDeviceInformation;
    public float CurrentGain { get => Current?.CurrentGain ?? RemoteDevice.DefaultMicrophoneGain; set { if (Current is { } remote) remote.CurrentGain = value; } }
    public G9N9NVoiceButtonMode VoiceButtonMode { get => Current?.VoiceButtonMode ?? G9N9NVoiceButtonMode.SmartToggle; set { if (Current is { } remote) remote.VoiceButtonMode = value; } }
    public bool IsDeviceReady(ulong address) => _remotes.GetValueOrDefault(address) is { IsReady: true, IsConnected: true };
    public Guid? GetDeviceContainerId(ulong address) => _remotes.GetValueOrDefault(address)?.ConnectedDeviceContainerId;
    public RemoteDeviceInformation? GetDeviceInformation(ulong address) => _remotes.GetValueOrDefault(address)?.ConnectedDeviceInformation;
    public void SetDeviceVoiceSettings(RemoteDevice device)
    {
        var remote = GetOrCreate(device.BluetoothAddress);
        remote.CurrentGain = device.Gain;
        remote.VoiceButtonMode = device.VoiceButtonMode;
    }

    public event Action<RemoteDevice>? DeviceDiscovered;
    public event Action<bool, string>? ConnectionStatusChanged;
    public event Action? ConnectedDeviceInformationChanged;
    public event Action<float>? AudioLevelChanged;
    public event Action? VoiceButtonPressed;
    public event Action? VoiceSessionStarted;
    public event Action? VoiceSessionEnded;

    public void StartScanning() => _scanner.StartScanning();
    public void StopScanning() => _scanner.StopScanning();
    public Task DiscoverPairedRemotesAsync() => _scanner.DiscoverPairedRemotesAsync();
    public void StartScan() => StartScanning();
    public void StopScan() => StopScanning();

    private WinRtBluetoothManager GetOrCreate(ulong address)
    {
        lock (_remotes)
        {
            if (_remotes.TryGetValue(address, out var existing)) return existing;
            var remote = new WinRtBluetoothManager(_voice);
            remote.ConnectionStatusChanged += (connected, _) =>
            {
                if (connected) _lastAddress = address;
                ConnectionStatusChanged?.Invoke(IsReady, ConnectedDeviceName);
            };
            remote.ConnectedDeviceInformationChanged += () => ConnectedDeviceInformationChanged?.Invoke();
            remote.AudioLevelChanged += level => { if (_voice.Owner == remote) AudioLevelChanged?.Invoke(level); };
            remote.VoiceButtonPressed += () => { if (_voice.Owner == remote) VoiceButtonPressed?.Invoke(); };
            remote.VoiceSessionStarted += () => VoiceSessionStarted?.Invoke();
            remote.VoiceSessionEnded += () => VoiceSessionEnded?.Invoke();
            _remotes[address] = remote;
            return remote;
        }
    }

    public Task<bool> ConnectAsync(ulong address) => GetOrCreate(address).ConnectAsync(address);
    public void DisconnectDevice(ulong address) => _remotes.GetValueOrDefault(address)?.Disconnect();
    public void Disconnect()
    {
        foreach (var remote in _remotes.Values) remote.Disconnect();
    }

    public Task<bool> StartVoiceSessionAsync() => Current?.StartVoiceSessionAsync() ?? Task.FromResult(false);
    public Task<bool> StopVoiceSessionAsync() => _voice.Owner?.StopVoiceSessionAsync() ?? Task.FromResult(false);
    public Task<bool> StartVoiceSessionAsync(ulong address) => _remotes.GetValueOrDefault(address)?.StartVoiceSessionAsync() ?? Task.FromResult(false);
    public Task<bool> StopVoiceSessionAsync(ulong address) => _voice.Owner == _remotes.GetValueOrDefault(address) && _voice.Owner is { } owner
        ? owner.StopVoiceSessionAsync() : Task.FromResult(false);
    public Task ProbeButtonSourceAsync(Guid marker, ulong address) => _remotes.GetValueOrDefault(address)?.ProbeButtonSourceAsync(marker) ?? Task.CompletedTask;

    public void Dispose()
    {
        foreach (var remote in _remotes.Values) remote.Dispose();
        _scanner.Dispose();
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
