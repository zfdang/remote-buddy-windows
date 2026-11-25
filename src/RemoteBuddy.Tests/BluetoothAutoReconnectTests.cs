// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Bluetooth;
using Xunit;

namespace RemoteBuddy.Tests;

public class BluetoothAutoReconnectTests
{
    private sealed class RecordingBluetoothManager : IBluetoothManager
    {
        public bool IsScanning { get; private set; }
        public bool IsConnected { get; set; }
        public bool IsReady { get; set; }
        public bool IsStreaming => false;
        public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
        public BluetoothSessionState SessionState => BluetoothSessionState.Idle;
        public string ConnectedDeviceName => string.Empty;
        public ulong ConnectedDeviceAddress => 0;
        public Guid? ConnectedDeviceContainerId => null;
        public RemoteDeviceInformation? ConnectedDeviceInformation => null;
        public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;

        public List<ulong[]> TargetUpdates { get; } = [];
        public int SystemResumeCount { get; private set; }
        public int HidArrivalCount { get; private set; }

        public event Action<RemoteDevice>? DeviceDiscovered { add { } remove { } }
        public event Action<ulong, bool, string>? ConnectionStatusChanged { add { } remove { } }
        public event Action<ulong>? ConnectedDeviceInformationChanged { add { } remove { } }
        public event Action<ulong, float>? AudioLevelChanged { add { } remove { } }
        public event Action<ulong>? VoiceButtonPressed { add { } remove { } }
        public event Action<ulong>? VoiceSessionStarted { add { } remove { } }
        public event Action<ulong>? VoiceSessionEnded { add { } remove { } }

        public void StartScanning() => IsScanning = true;
        public void StopScanning() => IsScanning = false;
        public Task DiscoverPairedRemotesAsync() => Task.CompletedTask;
        public void StartScan() => StartScanning();
        public void StopScan() => StopScanning();
        public Task<bool> ConnectAsync(ulong bluetoothAddress) => Task.FromResult(true);
        public void Disconnect() { }
        public Task<bool> StartVoiceSessionAsync() => Task.FromResult(true);
        public Task<bool> StopVoiceSessionAsync() => Task.FromResult(true);

        public void SetAutoReconnectTargets(IEnumerable<ulong> addresses) =>
            TargetUpdates.Add(addresses.ToArray());

        public void NotifySystemResume() => SystemResumeCount++;
        public void NotifyHidDeviceArrival() => HidArrivalCount++;
        public void Dispose() { }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Condition was not met within {timeoutMs}ms.");
            }
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData("BluetoothLE#BluetoothLE00:93:37:a5:2d:ea-c0:5d:39:f8:6e:0c", null, 0xC05D39F86E0CUL)]
    [InlineData("BluetoothLE#BluetoothLE00:93:37:a5:2d:ea-47:54:51:4c:6f:cc", "47:54:51:4c:6f:cc", 0x4754514C6FCCUL)]
    [InlineData("unrelated-id", "c0-5d-39-f8-6e-0c", 0xC05D39F86E0CUL)]
    [InlineData("unrelated-id", "c05d39f86e0c", 0xC05D39F86E0CUL)]
    public void TryParseBluetoothAddress_ParsesAepPropertiesAndDeviceIds(string deviceId, string? propAddr, ulong expected)
    {
        var props = new Dictionary<string, object>();
        if (propAddr != null)
        {
            props[WinRtBluetoothConnectionWatcher.AepDeviceAddressKey] = propAddr;
        }

        bool ok = WinRtBluetoothConnectionWatcher.TryParseBluetoothAddress(deviceId, props, out ulong actual);

        Assert.True(ok);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryParseBluetoothAddress_AndIsConnected_RejectInvalidAndExtractState()
    {
        Assert.False(WinRtBluetoothConnectionWatcher.TryParseBluetoothAddress("invalid", null, out _));
        Assert.False(WinRtBluetoothConnectionWatcher.TryParseBluetoothAddress("bt-00:00:00:00:00:00", null, out _));

        var connectedProps = new Dictionary<string, object>
        {
            [WinRtBluetoothConnectionWatcher.AepIsConnectedKey] = true
        };
        Assert.True(WinRtBluetoothConnectionWatcher.TryParseIsConnected(connectedProps, out bool isConnected));
        Assert.True(isConnected);

        var disconnectedProps = new Dictionary<string, object>
        {
            [WinRtBluetoothConnectionWatcher.AepIsConnectedKey] = false
        };
        Assert.True(WinRtBluetoothConnectionWatcher.TryParseIsConnected(disconnectedProps, out bool isDisconnected));
        Assert.False(isDisconnected);

        Assert.False(WinRtBluetoothConnectionWatcher.TryParseIsConnected(new Dictionary<string, object>(), out _));
    }

    [Fact]
    public async Task OnOsDeviceConnected_ConnectsTrackedDisconnectedTarget_AndIgnoresUntrackedOrReady()
    {
        const ulong tracked = 0xC05D39F86E0C;
        const ulong readyAddr = 0x4754514C6FCC;
        const ulong untracked = 0x112233445566;

        var readySet = new HashSet<ulong> { readyAddr };
        var connectCalls = new ConcurrentQueue<ulong>();

        using var coordinator = new BluetoothAutoReconnectCoordinator(
            isDeviceReady: addr => readySet.Contains(addr),
            isDeviceOsConnectedAsync: _ => Task.FromResult(true),
            connectImplAsync: addr =>
            {
                connectCalls.Enqueue(addr);
                readySet.Add(addr);
                return Task.FromResult(true);
            });

        coordinator.SetTargets([tracked, readyAddr]);

        coordinator.OnOsDeviceConnected(untracked, "aep_updated");
        coordinator.OnOsDeviceConnected(readyAddr, "aep_updated");
        coordinator.OnOsDeviceConnected(tracked, "aep_updated");

        await WaitUntilAsync(() => connectCalls.Count == 1);
        await Task.Delay(50);

        Assert.Single(connectCalls);
        Assert.True(connectCalls.TryDequeue(out ulong connected));
        Assert.Equal(tracked, connected);
    }

    [Fact]
    public async Task ConnectAsync_AndOsConnected_DeduplicateInFlightAttempt()
    {
        const ulong addr = 0xC05D39F86E0C;
        int connectAttempts = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var coordinator = new BluetoothAutoReconnectCoordinator(
            isDeviceReady: _ => false,
            isDeviceOsConnectedAsync: _ => Task.FromResult(true),
            connectImplAsync: _ =>
            {
                Interlocked.Increment(ref connectAttempts);
                return gate.Task;
            });

        coordinator.SetTargets([addr]);

        var first = coordinator.ConnectAsync(addr);
        coordinator.OnOsDeviceConnected(addr, "aep_updated");
        var second = coordinator.ConnectAsync(addr);

        Assert.Equal(1, Volatile.Read(ref connectAttempts));

        gate.SetResult(true);
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, Volatile.Read(ref connectAttempts));
    }

    [Fact]
    public async Task MarkUserDisconnected_SuppressesAutoReconnect_UntilExplicitConnect()
    {
        const ulong addr = 0xC05D39F86E0C;
        int connectAttempts = 0;
        bool ready = false;

        using var coordinator = new BluetoothAutoReconnectCoordinator(
            isDeviceReady: _ => ready,
            isDeviceOsConnectedAsync: _ => Task.FromResult(true),
            connectImplAsync: _ =>
            {
                Interlocked.Increment(ref connectAttempts);
                ready = true;
                return Task.FromResult(true);
            },
            delayAsync: (_, ct) => Task.Delay(1, ct));

        coordinator.SetTargets([addr]);
        coordinator.MarkUserDisconnected(addr);

        coordinator.OnOsDeviceConnected(addr, "aep_updated");
        coordinator.OnConnectionStatusChanged(addr, false);
        coordinator.NotifySystemResume();
        coordinator.NotifyHidDeviceArrival();
        await Task.Delay(60);

        Assert.Equal(0, Volatile.Read(ref connectAttempts));

        Assert.True(await coordinator.ConnectAsync(addr));
        Assert.Equal(1, Volatile.Read(ref connectAttempts));

        // After explicit reconnect and subsequent unexpected disconnect, auto-reconnect works again.
        ready = false;
        coordinator.OnOsDeviceConnected(addr, "aep_updated");
        await WaitUntilAsync(() => Volatile.Read(ref connectAttempts) == 2);
    }

    [Fact]
    public async Task ConnectAsync_AfterInvalidatedInFlight_WaitsAndStartsFreshAttempt()
    {
        const ulong addr = 0xC05D39F86E0C;
        int attempts = 0;
        var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var coordinator = new BluetoothAutoReconnectCoordinator(
            isDeviceReady: _ => false,
            isDeviceOsConnectedAsync: _ => Task.FromResult(false),
            connectImplAsync: _ =>
            {
                int n = Interlocked.Increment(ref attempts);
                return n == 1 ? firstGate.Task : Task.FromResult(true);
            });

        coordinator.SetTargets([addr]);
        var staleTask = coordinator.ConnectAsync(addr);
        coordinator.MarkUserDisconnected(addr);

        var freshTask = coordinator.ConnectAsync(addr);
        Assert.False(freshTask.IsCompleted);

        firstGate.SetResult(false);
        Assert.False(await staleTask);
        Assert.True(await freshTask);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task TransientFailureWhileOsConnected_RetriesBoundedTimes_AndStopsWhenOsDisconnects()
    {
        const ulong addr = 0xC05D39F86E0C;
        int attempts = 0;
        bool osConnected = true;
        bool ready = false;
        var recordedDelays = new ConcurrentQueue<int>();

        using var coordinator = new BluetoothAutoReconnectCoordinator(
            isDeviceReady: _ => ready,
            isDeviceOsConnectedAsync: _ => Task.FromResult(osConnected),
            connectImplAsync: _ =>
            {
                int n = Interlocked.Increment(ref attempts);
                if (n == 3)
                {
                    ready = true;
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            },
            delayAsync: (ms, ct) =>
            {
                recordedDelays.Enqueue(ms);
                return Task.Delay(1, ct);
            });

        coordinator.SetTargets([addr]);
        coordinator.OnOsDeviceConnected(addr, "aep_updated");

        await WaitUntilAsync(() => ready);
        Assert.Equal(3, Volatile.Read(ref attempts));
        Assert.Equal([1000, 2000], recordedDelays.ToArray());

        // Now verify no retries occur when OS is disconnected.
        ready = false;
        osConnected = false;
        int baseline = Volatile.Read(ref attempts);
        coordinator.OnConnectionStatusChanged(addr, false);
        await Task.Delay(50);
        Assert.Equal(baseline, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task NotifySystemResume_AndHidArrival_ReconnectOnlyOsConnectedTargets()
    {
        const ulong onlineAddr = 0xC05D39F86E0C;
        const ulong sleepingAddr = 0x4754514C6FCC;

        var readySet = new HashSet<ulong>();
        var osConnectedSet = new HashSet<ulong> { onlineAddr };
        var connectCalls = new ConcurrentQueue<ulong>();
        long clockMs = 5000;

        using var coordinator = new BluetoothAutoReconnectCoordinator(
            isDeviceReady: addr => readySet.Contains(addr),
            isDeviceOsConnectedAsync: addr => Task.FromResult(osConnectedSet.Contains(addr)),
            connectImplAsync: addr =>
            {
                connectCalls.Enqueue(addr);
                readySet.Add(addr);
                return Task.FromResult(true);
            },
            delayAsync: (_, ct) => Task.Delay(1, ct),
            nowMs: () => clockMs);

        coordinator.SetTargets([onlineAddr, sleepingAddr]);
        coordinator.NotifySystemResume();

        await WaitUntilAsync(() => readySet.Contains(onlineAddr));
        await Task.Delay(40);
        Assert.DoesNotContain(sleepingAddr, connectCalls);

        // Now second remote wakes up and emits bursty HID arrivals.
        osConnectedSet.Add(sleepingAddr);
        clockMs += 5000;
        coordinator.NotifyHidDeviceArrival();
        coordinator.NotifyHidDeviceArrival();
        coordinator.NotifyHidDeviceArrival();

        await WaitUntilAsync(() => readySet.Contains(sleepingAddr));
        Assert.Equal(2, connectCalls.Count);
    }

    [Fact]
    public void MainViewModel_ForwardsResumeHidArrivalAndDeviceListChanges_ToBluetoothManager()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bt-reconnect-mvm-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var devA = new RemoteDevice { Id = "A", BluetoothAddress = 0xC05D39F86E0C, Name = "MI RC" };
            var devB = new RemoteDevice { Id = "B", BluetoothAddress = 0x4754514C6FCC, Name = "Chromecast Remote" };
            config.Current.Devices.Add(devA);
            config.Current.Devices.Add(devB);

            var bt = new RecordingBluetoothManager();
            using var vm = new MainViewModel(config, bt);

            vm.HandleDeviceArrival(new IntPtr(0x1234));
            vm.HandleSystemResume();
            Assert.Equal(1, bt.HidArrivalCount);
            Assert.Equal(1, bt.SystemResumeCount);

            vm.DevicesVm.RemoveDevice(devA);
            Assert.NotEmpty(bt.TargetUpdates);
            Assert.Equal([devB.BluetoothAddress], bt.TargetUpdates[^1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
