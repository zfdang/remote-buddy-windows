// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Core.Bluetooth;

public enum BluetoothSessionState
{
    Idle = 0,
    Opening,
    Streaming,
    Draining
}

public readonly record struct EtwAttributeFilter(
    ushort HidHandleStart = 0,
    ushort HidHandleEnd = 0,
    ushort[]? ExcludedAttributes = null)
{
    public bool HasHidRange => HidHandleStart > 0 && HidHandleEnd >= HidHandleStart;
}

public interface IBluetoothManager : IDisposable
{
    bool IsScanning { get; }
    bool IsConnected { get; }
    bool IsReady { get; }
    bool IsStreaming { get; }
    float CurrentGain { get; set; }
    BluetoothSessionState SessionState { get; }
    string ConnectedDeviceName { get; }
    ulong ConnectedDeviceAddress { get; }
    Guid? ConnectedDeviceContainerId { get; }
    RemoteDeviceInformation? ConnectedDeviceInformation { get; }
    G9N9NVoiceButtonMode VoiceButtonMode { get; set; }

    event Action<RemoteDevice>? DeviceDiscovered;
    event Action<ulong, bool, string>? ConnectionStatusChanged;
    event Action<ulong>? ConnectedDeviceInformationChanged;
    event Action<ulong, float>? AudioLevelChanged;
    event Action<ulong>? VoiceButtonPressed;
    event Action<ulong>? VoiceSessionStarted;
    event Action<ulong>? VoiceSessionEnded;
    event Action<ulong, string>? AudioOutputFailed { add { } remove { } }

    void StartScanning();
    void StopScanning();
    Task DiscoverPairedRemotesAsync();
    void StartScan();
    void StopScan();
    Task<bool> ConnectAsync(ulong bluetoothAddress);
    void Disconnect();
    Task<bool> StartVoiceSessionAsync();
    Task<bool> StopVoiceSessionAsync();
    ulong VoiceDeviceAddress => IsStreaming ? ConnectedDeviceAddress : 0;
    IReadOnlyCollection<ulong> ConnectedDeviceAddresses => IsConnected && ConnectedDeviceAddress != 0 ? [ConnectedDeviceAddress] : [];
    bool IsDeviceReady(ulong address) => IsReady && IsConnected && address != 0 && address == ConnectedDeviceAddress;
    Guid? GetDeviceContainerId(ulong address) => address == ConnectedDeviceAddress ? ConnectedDeviceContainerId : null;
    RemoteDeviceInformation? GetDeviceInformation(ulong address) => ConnectedDeviceInformation?.BluetoothAddress == address ? ConnectedDeviceInformation : null;
    void SetDeviceVoiceSettings(RemoteDevice device)
    {
        if (device.BluetoothAddress != ConnectedDeviceAddress) return;
        CurrentGain = device.Gain;
        VoiceButtonMode = device.VoiceButtonMode;
    }
    void DisconnectDevice(ulong address)
    {
        if (address == ConnectedDeviceAddress) Disconnect();
    }
    void RemoveDevice(ulong address) => DisconnectDevice(address);
    void SetAutoReconnectTargets(IEnumerable<ulong> addresses) { }
    void NotifySystemResume() { }
    void NotifyHidDeviceArrival(ulong? address = null) { }
    Task<bool> StartVoiceSessionAsync(ulong address) => address == ConnectedDeviceAddress ? StartVoiceSessionAsync() : Task.FromResult(false);
    Task<bool> StopVoiceSessionAsync(ulong address) => address == ConnectedDeviceAddress ? StopVoiceSessionAsync() : Task.FromResult(false);
    Task ProbeButtonSourceAsync(Guid marker, ulong address) => Task.CompletedTask;
    byte[]? GetProbeReadPayload(ulong address) => null;
    EtwAttributeFilter GetEtwAttributeFilter(ulong address) => default;
}
