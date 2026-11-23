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
    G9N9NVoiceButtonMode VoiceButtonMode { get; set; }

    event Action<RemoteDevice>? DeviceDiscovered;
    event Action<bool, string>? ConnectionStatusChanged;
    event Action<float>? AudioLevelChanged;
    event Action? VoiceButtonPressed;
    event Action? VoiceSessionStarted;
    event Action? VoiceSessionEnded;

    void StartScanning();
    void StopScanning();
    Task DiscoverPairedRemotesAsync();
    void StartScan();
    void StopScan();
    Task<bool> ConnectAsync(ulong bluetoothAddress);
    void Disconnect();
    Task<bool> StartVoiceSessionAsync();
    Task<bool> StopVoiceSessionAsync();
    Task ProbeButtonSourceAsync(Guid marker) => Task.CompletedTask;
}
