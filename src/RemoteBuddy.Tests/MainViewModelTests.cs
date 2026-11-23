// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

public class MainViewModelTests
{
    private sealed class TestBluetoothManager : IBluetoothManager
    {
        public bool IsScanning { get; private set; }
        public bool IsConnected => false;
        public bool IsReady => false;
        public bool IsStreaming => false;
        public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
        public BluetoothSessionState SessionState => BluetoothSessionState.Idle;
        public string ConnectedDeviceName { get; set; } = string.Empty;
        public ulong ConnectedDeviceAddress { get; set; } = 0;
        public Guid? ConnectedDeviceContainerId { get; set; } = null;
        public RemoteDeviceInformation? ConnectedDeviceInformation => null;
        public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;

        public event Action<RemoteDevice>? DeviceDiscovered { add { } remove { } }
        public event Action<bool, string>? ConnectionStatusChanged;
        public event Action? ConnectedDeviceInformationChanged { add { } remove { } }
        public event Action<float>? AudioLevelChanged { add { } remove { } }
        public event Action? VoiceButtonPressed { add { } remove { } }
        public event Action? VoiceSessionStarted { add { } remove { } }
        public event Action? VoiceSessionEnded { add { } remove { } }

        public int StartVoiceSessionCount { get; private set; }
        public int StopVoiceSessionCount { get; private set; }

        public void RaiseConnectionStatusChanged(bool connected, string name) => ConnectionStatusChanged?.Invoke(connected, name);

        public void StartScanning() => IsScanning = true;
        public void StopScanning() => IsScanning = false;
        public Task DiscoverPairedRemotesAsync() => Task.CompletedTask;
        public void StartScan() => StartScanning();
        public void StopScan() => StopScanning();
        public Task<bool> ConnectAsync(ulong bluetoothAddress) => Task.FromResult(true);
        public void Disconnect() { }
        public Task<bool> StartVoiceSessionAsync()
        {
            StartVoiceSessionCount++;
            return Task.FromResult(true);
        }
        public Task<bool> StopVoiceSessionAsync()
        {
            StopVoiceSessionCount++;
            return Task.FromResult(true);
        }
        public void Dispose() { }
    }

    [Fact]
    public void MainViewModel_InitialState_LoadsConfigAndDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager();
            using var vm = new MainViewModel(config, bt);

            Assert.Equal("General", vm.ActivePageName);
            Assert.False(vm.IsConnected);
            Assert.Equal("未连接设备", vm.ConnectedDeviceName);
            Assert.False(vm.IsVoiceActive);
            Assert.Equal(0f, vm.CurrentAudioLevel);
            Assert.NotNull(vm.GeneralVm);
            Assert.NotNull(vm.EnvironmentVm);
            Assert.NotNull(vm.DevicesVm);
            Assert.NotNull(vm.IntegrationsVm);
            Assert.NotNull(vm.MappingVm);
            Assert.NotNull(vm.AppIntegrationVm);
            Assert.NotNull(vm.AboutVm);
            Assert.NotNull(vm.SettingsVm);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MainViewModel_Navigate_UpdatesActivePageName()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager();
            using var vm = new MainViewModel(config, bt);

            vm.Navigate("General");
            Assert.Equal("General", vm.ActivePageName);

            vm.Navigate("Permissions");
            Assert.Equal("Permissions", vm.ActivePageName);

            vm.Navigate("Devices");
            Assert.Equal("Devices", vm.ActivePageName);

            vm.Navigate("Integrations");
            Assert.Equal("Integrations", vm.ActivePageName);

            vm.Navigate("Mapping");
            Assert.Equal("Mapping", vm.ActivePageName);

            vm.Navigate("AppIntegration");
            Assert.Equal("AppIntegration", vm.ActivePageName);

            vm.Navigate("About");
            Assert.Equal("About", vm.ActivePageName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MainViewModel_HandleDeviceRemoval_DoesNotThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager();
            using var vm = new MainViewModel(config, bt);

            vm.HandleDeviceRemoval(new IntPtr(0x1234));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MainViewModel_Connection_SynchronizesVoiceButtonModeToBluetoothManager()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-vbm-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var device = new RemoteDevice
            {
                Id = "DEV_G9N9N",
                Name = "Chromecast Remote",
                BluetoothAddress = 0x4754514C6FCC,
                VoiceButtonMode = G9N9NVoiceButtonMode.HoldToTalk
            };
            config.Current.Devices.Add(device);
            config.Current.SelectedDeviceId = device.Id;

            var bt = new TestBluetoothManager
            {
                VoiceButtonMode = G9N9NVoiceButtonMode.SmartToggle
            };

            using var vm = new MainViewModel(config, bt);
            bt.RaiseConnectionStatusChanged(true, "Chromecast Remote");

            Assert.Equal(G9N9NVoiceButtonMode.HoldToTalk, bt.VoiceButtonMode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MainViewModel_VoiceButtonFromActiveRemote_TriggersVoiceSession()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-voice-active-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var g9n9n = new RemoteDevice
            {
                Id = "DEV_G9N9N",
                Name = "Chromecast Remote",
                BluetoothAddress = 0x4754514C6FCC,
            };
            config.Current.Devices.Add(g9n9n);
            config.Current.SelectedDeviceId = g9n9n.Id;
            config.Current.InputTool.TriggerMode = VoiceTriggerMode.ClickToToggle;

            var bt = new TestBluetoothManager
            {
                ConnectedDeviceAddress = 0x4754514C6FCC
            };

            using var vm = new MainViewModel(config, bt);

            // Press voice button from active remote (ETW / direct stream)
            vm.HandleRawButtonEvent(IntPtr.Zero, RemoteButton.Voice, true);

            Assert.Equal(1, bt.StartVoiceSessionCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MainViewModel_MappingPress_IsolatedToSelectedDevice()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-map-iso-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var g9n9n = new RemoteDevice
            {
                Id = "DEV_G9N9N",
                Name = "Chromecast Remote",
                BluetoothAddress = 0x4754514C6FCC,
            };
            var xiaomi = new RemoteDevice
            {
                Id = "DEV_XIAOMI",
                Name = "Xiaomi Remote",
                BluetoothAddress = 0xA4C138123456,
            };
            config.Current.Devices.Add(g9n9n);
            config.Current.Devices.Add(xiaomi);
            config.Current.SelectedDeviceId = g9n9n.Id;

            // Connect to G9N9N
            var bt = new TestBluetoothManager
            {
                ConnectedDeviceAddress = 0x4754514C6FCC
            };

            using var vm = new MainViewModel(config, bt);
            vm.Navigate("Mapping");
            vm.MappingVm.SelectedDevice = xiaomi; // Viewing Xiaomi in UI

            RemoteButton? pressedButton = null;
            vm.MappingVm.HardwareButtonPressed += btn => pressedButton = btn;

            // Event from G9N9N (active Bluetooth address) arrives
            vm.HandleRawButtonEvent(IntPtr.Zero, RemoteButton.Home, true);

            // Since Xiaomi is selected in Mapping UI but event was from G9N9N, MappingVm should NOT receive press
            Assert.Null(pressedButton);

            // Now switch Mapping UI to view G9N9N
            vm.MappingVm.SelectedDevice = g9n9n;
            vm.HandleRawButtonEvent(IntPtr.Zero, RemoteButton.Back, true);

            // Now it matches, so MappingVm should receive press
            Assert.Equal(RemoteButton.Back, pressedButton);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MainViewModel_Onboarding_IgnoresButtonFromOtherRemote()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-onb-iso-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var g9n9n = new RemoteDevice
            {
                Id = "DEV_G9N9N",
                Name = "Chromecast Remote",
                BluetoothAddress = 0x4754514C6FCC,
            };
            var xiaomi = new RemoteDevice
            {
                Id = "DEV_XIAOMI",
                Name = "Xiaomi Remote",
                BluetoothAddress = 0xA4C138123456,
            };
            config.Current.Devices.Add(g9n9n);
            config.Current.Devices.Add(xiaomi);

            // System currently connected to G9N9N
            var bt = new TestBluetoothManager
            {
                ConnectedDeviceAddress = 0x4754514C6FCC
            };

            using var vm = new MainViewModel(config, bt);

            // Start onboarding for Xiaomi
            var onboardingVm = new OnboardingViewModel(config, bt, WizardMode.DeviceOnly);
            onboardingVm.SelectedDevice = xiaomi;
            onboardingVm.CurrentStep = 2; // Step 2 is ButtonTest in DeviceOnly mode
            vm.ActiveOnboardingVm = onboardingVm;

            // Button event arrives from connected G9N9N (not Xiaomi)
            vm.HandleRawButtonEvent(IntPtr.Zero, RemoteButton.Back, true);

            // Xiaomi onboarding must NOT receive G9N9N's button press
            Assert.False(onboardingVm.HasButtonTested);
            Assert.NotEqual("已按下: 返回键", onboardingVm.LastPressedButtonName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
