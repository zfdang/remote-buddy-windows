// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Bluetooth;

/// <summary>
/// Monitors Windows Bluetooth LE paired-device AEP connection state (<c>System.Devices.Aep.IsConnected</c>)
/// and Bluetooth radio power state so remotes are reconnected automatically when they come back online.
/// </summary>
internal sealed class WinRtBluetoothConnectionWatcher : IDisposable
{
    internal const string AepIsConnectedKey = "System.Devices.Aep.IsConnected";
    internal const string AepDeviceAddressKey = "System.Devices.Aep.DeviceAddress";

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, ulong> _idToAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Radio> _monitoredRadios = [];
    private DeviceWatcher? _watcher;
    private bool _radioMonitorStarted;
    private bool _disposed;

    public event Action<ulong, string>? DeviceOsConnected;
    public event Action<ulong>? DeviceOsDisconnected;
    public event Action? BluetoothRadioRestored;

    public void EnsureStarted()
    {
        lock (_lock)
        {
            if (_disposed) return;

            if (_watcher != null &&
                _watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted or DeviceWatcherStatus.Created)
            {
                return;
            }

            StopWatcherLocked();

            try
            {
                var watcher = DeviceInformation.CreateWatcher(
                    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                    new[] { AepIsConnectedKey, AepDeviceAddressKey },
                    DeviceInformationKind.AssociationEndpoint);

                watcher.Added += OnDeviceAdded;
                watcher.Updated += OnDeviceUpdated;
                watcher.Removed += OnDeviceRemoved;
                watcher.Stopped += OnWatcherStopped;
                watcher.Start();
                _watcher = watcher;
                AppLogger.Info("Started WinRT AEP connection watcher for paired BLE remotes", "BluetoothManager");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to start WinRT AEP connection watcher: {ex.Message}", "BluetoothManager");
            }

            if (!_radioMonitorStarted)
            {
                _radioMonitorStarted = true;
                _ = StartRadioMonitorAsync();
            }
        }
    }

    public static async Task<bool> IsDeviceOsConnectedAsync(ulong address)
    {
        if (address == 0) return false;
        try
        {
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask().ConfigureAwait(false);
            return device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParseIsConnected(IReadOnlyDictionary<string, object>? properties, out bool isConnected)
    {
        if (properties != null && properties.TryGetValue(AepIsConnectedKey, out object? raw) && raw is bool value)
        {
            isConnected = value;
            return true;
        }

        isConnected = false;
        return false;
    }

    internal static bool TryParseBluetoothAddress(
        string? deviceId,
        IReadOnlyDictionary<string, object>? properties,
        out ulong address)
    {
        if (properties != null && properties.TryGetValue(AepDeviceAddressKey, out object? rawAddr))
        {
            if (rawAddr is string addrStr && TryParseMacAddress(addrStr, out address))
            {
                return true;
            }
            if (rawAddr is ulong ulongAddr && ulongAddr != 0)
            {
                address = ulongAddr;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            int dashIdx = deviceId.LastIndexOf('-');
            if (dashIdx >= 0 && dashIdx + 1 < deviceId.Length)
            {
                string tail = deviceId[(dashIdx + 1)..];
                if (TryParseMacAddress(tail, out address))
                {
                    return true;
                }
            }
        }

        address = 0;
        return false;
    }

    internal static bool TryParseMacAddress(string? value, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        Span<char> hex = stackalloc char[12];
        int count = 0;
        foreach (char ch in value.Trim())
        {
            if (ch is ':' or '-' or ' ') continue;
            if (!Uri.IsHexDigit(ch) || count >= 12) return false;
            hex[count++] = ch;
        }

        if (count != 12) return false;
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) && address != 0;
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
    {
        if (_disposed) return;
        if (!TryParseBluetoothAddress(args.Id, args.Properties, out ulong address)) return;

        _idToAddress[args.Id] = address;
        if (TryParseIsConnected(args.Properties, out bool isConnected) && isConnected)
        {
            AppLogger.Info($"AEP watcher observed paired remote already/now connected: 0x{address:X12}", "BluetoothManager");
            DeviceOsConnected?.Invoke(address, "aep_added");
        }
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        if (_disposed) return;
        if (!TryParseIsConnected(args.Properties, out bool isConnected)) return;

        if (!_idToAddress.TryGetValue(args.Id, out ulong address))
        {
            if (!TryParseBluetoothAddress(args.Id, args.Properties, out address)) return;
            _idToAddress[args.Id] = address;
        }

        if (isConnected)
        {
            AppLogger.Info($"AEP watcher observed remote online: 0x{address:X12}", "BluetoothManager");
            DeviceOsConnected?.Invoke(address, "aep_updated");
        }
        else
        {
            AppLogger.Debug($"AEP watcher observed remote offline: 0x{address:X12}", "BluetoothManager");
            DeviceOsDisconnected?.Invoke(address);
        }
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        _idToAddress.TryRemove(args.Id, out _);
    }

    private void OnWatcherStopped(DeviceWatcher sender, object args)
    {
        AppLogger.Debug($"WinRT AEP connection watcher stopped (Status={sender.Status})", "BluetoothManager");
    }

    private async Task StartRadioMonitorAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
            foreach (var radio in radios)
            {
                if (radio.Kind != RadioKind.Bluetooth) continue;

                lock (_lock)
                {
                    if (_disposed) return;
                    radio.StateChanged += OnRadioStateChanged;
                    _monitoredRadios.Add(radio);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Bluetooth radio monitor unavailable: {ex.Message}", "BluetoothManager");
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        if (_disposed) return;
        if (sender.State == RadioState.On)
        {
            AppLogger.Info("Bluetooth radio transitioned to On; refreshing paired remote connections", "BluetoothManager");
            EnsureStarted();
            BluetoothRadioRestored?.Invoke();
        }
    }

    private void StopWatcherLocked()
    {
        if (_watcher == null) return;
        var watcher = _watcher;
        _watcher = null;

        watcher.Added -= OnDeviceAdded;
        watcher.Updated -= OnDeviceUpdated;
        watcher.Removed -= OnDeviceRemoved;
        watcher.Stopped -= OnWatcherStopped;

        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Radio[] radios;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            StopWatcherLocked();
            radios = _monitoredRadios.ToArray();
            _monitoredRadios.Clear();
        }

        foreach (var radio in radios)
        {
            try { radio.StateChanged -= OnRadioStateChanged; } catch { }
        }
        _idToAddress.Clear();
    }
}
