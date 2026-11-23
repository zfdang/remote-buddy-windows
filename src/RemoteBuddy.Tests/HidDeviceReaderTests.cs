// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Audio;
using RemoteBuddy.Platform.Windows.Input;
using Xunit;

namespace RemoteBuddy.Tests;

public class HidDeviceReaderTests
{
    private sealed class TestBluetoothManager : IBluetoothManager
    {
        public bool IsScanning { get; private set; }
        public bool IsConnected { get; set; } = true;
        public bool IsReady => IsConnected;
        public bool IsStreaming => false;
        public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
        public BluetoothSessionState SessionState => BluetoothSessionState.Idle;
        public string ConnectedDeviceName { get; set; } = "Test Remote";
        public ulong ConnectedDeviceAddress => 0x4754514C6FCC;
        public Guid? ConnectedDeviceContainerId => null;
        public RemoteDeviceInformation? ConnectedDeviceInformation => null;
        public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;

        public event Action<RemoteDevice>? DeviceDiscovered;
        public event Action<bool, string>? ConnectionStatusChanged;
        public event Action? ConnectedDeviceInformationChanged { add { } remove { } }
        public event Action<float>? AudioLevelChanged { add { } remove { } }
        public event Action? VoiceButtonPressed;
        public event Action? VoiceSessionStarted { add { } remove { } }
        public event Action? VoiceSessionEnded { add { } remove { } }

        public void RaiseDeviceDiscovered(RemoteDevice dev) => DeviceDiscovered?.Invoke(dev);
        public void RaiseConnectionStatusChanged(bool connected, string name) => ConnectionStatusChanged?.Invoke(connected, name);
        public void RaiseVoiceButtonPressed() => VoiceButtonPressed?.Invoke();

        public void StartScanning() => IsScanning = true;
        public void StopScanning() => IsScanning = false;
        public Task DiscoverPairedRemotesAsync() => Task.CompletedTask;
        public void StartScan() => StartScanning();
        public void StopScan() => StopScanning();
        public Task<bool> ConnectAsync(ulong bluetoothAddress) => Task.FromResult(true);
        public void Disconnect() { IsConnected = false; }
        public Task<bool> StartVoiceSessionAsync() => Task.FromResult(true);
        public Task<bool> StopVoiceSessionAsync() => Task.FromResult(true);
        public void Dispose() { }
    }

    [Fact]
    public void RemoteDevice_HidProperties_FormatExpectedSummary()
    {
        var device = new RemoteDevice
        {
            Id = "4754514C6FCC",
            Name = "Chromecast Remote",
            ModelType = RemoteModelType.GoogleG9N9N
        };

        Assert.False(device.HasHidInfo);
        Assert.Equal("标准 Consumer Control (待检测)", device.HidSpecSummary);

        device.HidDevicePath = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}#4754514c6fcc#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        device.HidUsagePage = 0x000C;
        device.HidUsage = 0x0001;
        device.HidReportLength = 3;
        device.HidCaptureStatus = "已就绪 (RawInput & Direct Stream)";

        Assert.True(device.HasHidInfo);
        Assert.Contains("0x000C", device.HidSpecSummary);
        Assert.Contains("0x0001", device.HidSpecSummary);
        Assert.Contains("3 字节", device.HidSpecSummary);
        Assert.Equal("已就绪 (RawInput & Direct Stream)", device.HidCaptureStatus);
    }

    [Fact]
    public void VirtualAudioAuditor_CacheTtl_PreventsSynchronousWasapiThrashing()
    {
        // First call populates cache
        var status1 = VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: false);
        var cached = VirtualAudioAuditor.GetCachedStatus();
        Assert.Equal(status1, cached);

        // Subsequent call within 5s returns cached value immediately
        var status2 = VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: false);
        Assert.Equal(status1, status2);
    }

    [Fact]
    public void VirtualAudioAuditor_ResetDeviceCache_ClearsCachedEndpointId()
    {
        VirtualAudioAuditor.ResetDeviceCache();
        // Should not throw on null device
        VirtualAudioAuditor.EnsureEndpointUnmutedAndMaxVolume(null);
    }

    [Fact]
    public void WasapiAudioPlayer_CanInstantiateAndDisposeSafely()
    {
        using var player = new WasapiAudioPlayer();
        Assert.False(player.IsActive);
        player.Stop();
        Assert.False(player.IsActive);
    }

    [Fact]
    public void ScopedWizard_ButtonTest_WorksInBothDeviceOnlyAndFullModes()
    {
        var config = new ConfigService();
        var mockBt = new TestBluetoothManager();

        // 1. DeviceOnly mode: ButtonTest is Step 2
        var vmDeviceOnly = new OnboardingViewModel(config, mockBt, WizardMode.DeviceOnly);
        Assert.Equal(WizardStep.DeviceConnect, vmDeviceOnly.CurrentWizardStep);
        Assert.Equal(1, vmDeviceOnly.CurrentStep);

        // Move to Step 2
        vmDeviceOnly.SelectedDevice = new RemoteDevice { Id = "TEST", Name = "Test Remote" };
        mockBt.RaiseConnectionStatusChanged(true, "Test Remote");
        vmDeviceOnly.NextStep();

        Assert.Equal(WizardStep.ButtonTest, vmDeviceOnly.CurrentWizardStep);
        Assert.Equal(2, vmDeviceOnly.CurrentStep);
        Assert.False(vmDeviceOnly.HasButtonTested);

        vmDeviceOnly.NotifyButtonPressed(RemoteButton.Ok);
        Assert.True(vmDeviceOnly.HasButtonTested);
        Assert.Contains("确认键", vmDeviceOnly.LastPressedButtonName);

        // 2. Full mode: ButtonTest is Step 4
        var vmFull = new OnboardingViewModel(config, mockBt, WizardMode.Full);
        Assert.Equal(WizardStep.Welcome, vmFull.CurrentWizardStep);

        // Advance to Step 4
        vmFull.NextStep(); // Step 2: DriverCheck
        Assert.Equal(WizardStep.DriverCheck, vmFull.CurrentWizardStep);

        try
        {
            // Verification gate: if driver is NotInstalled, NextStep() blocks
            VirtualAudioAuditor.StatusOverride = DriverReadinessStatus.NotInstalled;
            vmFull.DriverStatus = DriverReadinessStatus.NotInstalled;
            vmFull.NextStep();
            Assert.Equal(WizardStep.DriverCheck, vmFull.CurrentWizardStep);
            Assert.False(string.IsNullOrEmpty(vmFull.ValidationError));

            // When driver becomes ready, NextStep() advances to Step 3 (DeviceConnect)
            VirtualAudioAuditor.StatusOverride = DriverReadinessStatus.Ready;
            vmFull.DriverStatus = DriverReadinessStatus.Ready;
            vmFull.NextStep(); // Step 3: DeviceConnect
            Assert.Equal(WizardStep.DeviceConnect, vmFull.CurrentWizardStep);
            Assert.Empty(vmFull.ValidationError);
        }
        finally
        {
            VirtualAudioAuditor.StatusOverride = null;
        }

        vmFull.SelectedDevice = new RemoteDevice { Id = "TEST2", Name = "Test Remote 2" };
        mockBt.RaiseConnectionStatusChanged(true, "Test Remote 2");
        vmFull.NextStep(); // Step 4: ButtonTest

        Assert.Equal(WizardStep.ButtonTest, vmFull.CurrentWizardStep);
        Assert.Equal(4, vmFull.CurrentStep);
        Assert.False(vmFull.HasButtonTested);

        vmFull.NotifyButtonPressed(RemoteButton.Up);
        Assert.True(vmFull.HasButtonTested);
        Assert.Contains("方向上键", vmFull.LastPressedButtonName);
    }

    [Fact]
    public void VirtualAudioAuditor_StatusOverride_OverridesAuditResult()
    {
        try
        {
            VirtualAudioAuditor.StatusOverride = DriverReadinessStatus.Ready;
            Assert.Equal(DriverReadinessStatus.Ready, VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true));
            Assert.Equal(DriverReadinessStatus.Ready, VirtualAudioAuditor.GetCachedStatus());

            VirtualAudioAuditor.StatusOverride = DriverReadinessStatus.RebootRequired;
            Assert.Equal(DriverReadinessStatus.RebootRequired, VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true));
        }
        finally
        {
            VirtualAudioAuditor.StatusOverride = null;
        }
    }
}
