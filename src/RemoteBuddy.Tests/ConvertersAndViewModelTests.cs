// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Windows.Input;
using RemoteBuddy.App.Converters;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Audio;
using Xunit;

namespace RemoteBuddy.Tests;

[Collection("VirtualAudioAuditor")]
public class ConvertersAndViewModelTests
{
    private sealed class DummyBluetoothManager : IBluetoothManager
    {
        public bool IsScanning { get; private set; }
        public bool IsConnected { get; set; } = false;
        public bool IsReady => IsConnected;
        public bool IsStreaming => false;
        public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
        public BluetoothSessionState SessionState => BluetoothSessionState.Idle;
        public string ConnectedDeviceName { get; set; } = string.Empty;
        public ulong ConnectedDeviceAddress { get; set; } = 0;
        public Guid? ConnectedDeviceContainerId { get; set; } = null;
        public RemoteDeviceInformation? ConnectedDeviceInformation { get; private set; }
        public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;
        public bool ConnectResult { get; set; } = true;

        public event Action<RemoteDevice>? DeviceDiscovered;
        public event Action<ulong, bool, string>? ConnectionStatusChanged;
        public event Action<ulong>? ConnectedDeviceInformationChanged;
        public event Action<ulong, float>? AudioLevelChanged { add { } remove { } }
        public event Action<ulong>? VoiceButtonPressed;
        public event Action<ulong>? VoiceSessionStarted;
        public event Action<ulong>? VoiceSessionEnded;

        public void RaiseDeviceDiscovered(RemoteDevice dev) => DeviceDiscovered?.Invoke(dev);
        public void RaiseConnectionStatusChanged(bool connected, string name, ulong address = 0) =>
            ConnectionStatusChanged?.Invoke(address != 0 ? address : ConnectedDeviceAddress, connected, name);
        public void SetConnectedDeviceInformation(RemoteDeviceInformation? information)
        {
            ConnectedDeviceInformation = information;
            ConnectedDeviceInformationChanged?.Invoke(information?.BluetoothAddress ?? ConnectedDeviceAddress);
        }
        public void RaiseVoiceButtonPressed(ulong address = 0) => VoiceButtonPressed?.Invoke(address);
        public void RaiseVoiceSessionStarted(ulong address = 0) => VoiceSessionStarted?.Invoke(address);
        public void RaiseVoiceSessionEnded(ulong address = 0) => VoiceSessionEnded?.Invoke(address);

        public void StartScanning() => IsScanning = true;
        public void StopScanning() => IsScanning = false;
        public Task DiscoverPairedRemotesAsync() => Task.CompletedTask;
        public void StartScan() => StartScanning();
        public void StopScan() => StopScanning();
        public Task<bool> ConnectAsync(ulong bluetoothAddress)
        {
            if (!ConnectResult) return Task.FromResult(false);
            IsConnected = true;
            ConnectedDeviceAddress = bluetoothAddress;
            return Task.FromResult(true);
        }
        public void Disconnect()
        {
            IsConnected = false;
            ConnectedDeviceAddress = 0;
        }
        public Task<bool> StartVoiceSessionAsync() => Task.FromResult(true);
        public Task<bool> StopVoiceSessionAsync() => Task.FromResult(true);
        public void Dispose() { }
    }

    [Theory]
    [InlineData(1.0f, "+0 dB")]
    [InlineData(2.0f, "+6 dB")]
    [InlineData(4.0f, "+12 dB")]
    [InlineData(10.0f, "+20 dB")]
    [InlineData(0.5f, "-6 dB")]
    public void GainToDbTextConverter_FormatsDbCorrectly(float gain, string expected)
    {
        var converter = new GainToDbTextConverter();
        var result = converter.Convert(gain, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IntegrationWizard_UpdatesTriggerModeAndShortcut_WhenContinuingToVerification()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-int-{Guid.NewGuid():N}.json");
        var config = new ConfigService(path);
        using var wizard = new OnboardingViewModel(config, new DummyBluetoothManager(), WizardMode.Integration);
        var overview = new IntegrationsViewModel(config);

        wizard.ApplyPreset("WindowsDictation");
        Assert.Equal("Win+H", wizard.ShortcutText);
        Assert.True(wizard.IsClickToToggle);
        Assert.NotEqual(TargetInputTool.WindowsDictation, config.Current.InputTool.Tool);

        wizard.IsHoldToTalk = true;
        wizard.ShortcutText = "Ctrl+F5";
        wizard.NextStep();

        Assert.Equal(WizardStep.TranscriptionVerify, wizard.CurrentWizardStep);
        Assert.Equal("按住说话", overview.CurrentTriggerModeDisplay);
        Assert.Equal("Ctrl+F5", overview.CurrentShortcutDisplay);

        wizard.PreviousStep();
        wizard.IsClickToToggle = true;
        wizard.NextStep();
        overview.Reload();

        var saved = new ConfigService(path).Current.InputTool;
        Assert.Equal(TargetInputTool.WindowsDictation, saved.Tool);
        Assert.Equal(VoiceTriggerMode.ClickToToggle, saved.TriggerMode);
        Assert.Equal("Ctrl+F5", saved.Shortcut);
        Assert.Equal("单击切换", overview.CurrentTriggerModeDisplay);
    }

    [Fact]
    public void MappingViewModel_SelectButtonAndClear_OperatesCorrectly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-map-{Guid.NewGuid():N}.json");
        var config = new ConfigService(path);
        var dev = new RemoteDevice { Id = "test_dev", ModelType = RemoteModelType.XiaomiRC003 };
        config.Current.Devices.Add(dev);
        config.Current.SelectedDeviceId = dev.Id;

        var vm = new MappingViewModel(config);
        Assert.NotNull(vm.SelectedDevice);

        // Select Up button
        vm.SelectButton("Up");
        Assert.NotNull(vm.SelectedMapping);
        Assert.Equal(RemoteButton.Up, vm.SelectedMapping.Button);

        // Select Home button
        vm.SelectButton("Home");
        Assert.NotNull(vm.SelectedMapping);
        Assert.Equal(RemoteButton.Home, vm.SelectedMapping.Button);

        // Clear Home button
        vm.ClearSelectedMapping();
        Assert.Equal(ButtonActionType.None, vm.SelectedMapping.SingleClick.Action);
        Assert.Empty(vm.SelectedMapping.SingleClick.Shortcut);

        // Reset to Defaults
        vm.ResetToDefaults();
        vm.SelectButton("Home");
        Assert.Equal(ButtonActionType.KeyboardShortcut, vm.SelectedMapping.SingleClick.Action);
        Assert.Equal("Win+Tab", vm.SelectedMapping.SingleClick.Shortcut);
    }

    [Fact]
    public void DevicesViewModel_AssignsLiveHardwareReadingsOnlyToTheMatchingAddress()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"rb-device-info-{Guid.NewGuid():N}.json"));
        var first = new RemoteDevice { Id = "first", BluetoothAddress = 1, Name = "Chromecast Remote" };
        var second = new RemoteDevice { Id = "second", BluetoothAddress = 2, Name = "Chromecast Remote" };
        config.Current.Devices.AddRange([first, second]);
        var bluetooth = new DummyBluetoothManager();
        using var vm = new DevicesViewModel(config, bluetooth);

        var readings = new RemoteDeviceInformation(2, FirmwareRevision: "1.0");
        bluetooth.SetConnectedDeviceInformation(readings);
        Assert.Null(first.HardwareInformation);
        Assert.Equal(readings, second.HardwareInformation);

        vm.Reload();
        Assert.Equal(readings, second.HardwareInformation);
        bluetooth.SetConnectedDeviceInformation(null);
        Assert.Null(second.HardwareInformation);
    }

    [Fact]
    public void DevicesViewModel_RemoveDevice_RemovesFromConfigAndList()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-dev-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var dev1 = new RemoteDevice { Id = "dev1", BluetoothAddress = 1, Name = "Remote 1" };
            var dev2 = new RemoteDevice { Id = "dev2", BluetoothAddress = 2, Name = "Remote 2" };
            config.Current.Devices.AddRange([dev1, dev2]);
            config.Current.SelectedDeviceId = dev1.Id;

            var dummyBt = new DummyBluetoothManager();
            DevicesViewModel.ConfirmDeviceRemovalHandler = _ => true;
            using var vm = new DevicesViewModel(config, dummyBt);

            Assert.Equal(2, vm.DiscoveredDevices.Count);

            vm.RemoveDevice(dev1);

            Assert.Single(vm.DiscoveredDevices);
            Assert.Equal("dev2", vm.DiscoveredDevices[0].Id);
            Assert.Single(config.Current.Devices);
            Assert.Null(config.Current.SelectedDeviceId);
        }
        finally
        {
            DevicesViewModel.ConfirmDeviceRemovalHandler = null;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void GeneralViewModel_ReflectsState_And_Navigates()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-gen-{Guid.NewGuid():N}.json");
        var config = new ConfigService(path);
        var dummyBt = new DummyBluetoothManager();
        string navigatedTo = "";
        bool wizardOpened = false;

        var vm = new GeneralViewModel(config, dummyBt, () => wizardOpened = true, target => navigatedTo = target);

        Assert.NotNull(vm.PermissionsSummary);
        Assert.Equal("未连接遥控器", vm.RemoteSummary);
        Assert.False(vm.RemoteReady);
        Assert.NotNull(vm.IntegrationSummary);

        vm.RerunWizard();
        Assert.True(wizardOpened);

        vm.GoToPermissions();
        Assert.Equal("Permissions", navigatedTo);

        vm.GoToDevices();
        Assert.Equal("Devices", navigatedTo);

        vm.GoToIntegrations();
        Assert.Equal("Integrations", navigatedTo);
    }

    [Fact]
    public void AppIntegrationViewModel_EnableDisableAll_PersistsState()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-appint-{Guid.NewGuid():N}.json");
        var config = new ConfigService(path);

        var vm = new AppIntegrationViewModel(config);
        Assert.NotEmpty(vm.Applications);
        Assert.All(vm.Applications, a => Assert.False(a.IsEnabled));

        vm.EnableAll();
        Assert.All(vm.Applications, a => Assert.True(a.IsEnabled));
        Assert.True(config.Current.AppIntegrations["wechat"]);
        Assert.True(config.Current.AppIntegrations["antigravity"]);

        vm.DisableAll();
        Assert.All(vm.Applications, a => Assert.False(a.IsEnabled));
        Assert.False(config.Current.AppIntegrations["wechat"]);
    }

    [Fact]
    public void AboutViewModel_ExposesAppMetadata()
    {
        var vm = new AboutViewModel();
        Assert.False(string.IsNullOrEmpty(vm.AppVersion));
        Assert.False(string.IsNullOrEmpty(vm.Architecture));
        Assert.False(string.IsNullOrEmpty(vm.LogDirectoryPath));
    }

    [Fact]
    public void OnboardingViewModel_ScopedWizards_FollowExpectedPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-wizard-{Guid.NewGuid():N}.json");
        var config = new ConfigService(path);
        var dummyBt = new DummyBluetoothManager();

        // 1. DeviceOnly wizard test: 4 steps (Connect -> Buttons -> Audio -> Complete)
        var deviceWizard = new OnboardingViewModel(config, dummyBt, WizardMode.DeviceOnly);
        Assert.Equal(WizardMode.DeviceOnly, deviceWizard.Mode);
        Assert.Equal(1, deviceWizard.CurrentStep);
        Assert.Equal(WizardStep.DeviceConnect, deviceWizard.CurrentWizardStep);

        deviceWizard.IsDeviceConnected = true;
        deviceWizard.NextStep();
        Assert.Equal(2, deviceWizard.CurrentStep);
        Assert.Equal(WizardStep.ButtonTest, deviceWizard.CurrentWizardStep);

        deviceWizard.NotifyButtonPressed(RemoteButton.Ok);
        deviceWizard.NextStep();
        Assert.Equal(3, deviceWizard.CurrentStep);
        Assert.Equal(WizardStep.AudioTest, deviceWizard.CurrentWizardStep);

        deviceWizard.HasAudioTested = true;
        deviceWizard.NextStep();
        Assert.Equal(4, deviceWizard.CurrentStep);
        Assert.Equal(WizardStep.Complete, deviceWizard.CurrentWizardStep);
        deviceWizard.Dispose();
        // Disposed without Complete(): device list must remain empty
        Assert.Empty(config.Current.Devices);

        // 2. Integration wizard test: 3 steps (Tool selection -> Transcription test -> Complete)
        var intWizard = new OnboardingViewModel(config, dummyBt, WizardMode.Integration);
        Assert.Equal(WizardMode.Integration, intWizard.Mode);
        Assert.Equal(1, intWizard.CurrentStep);
        Assert.Equal(WizardStep.ImeConfig, intWizard.CurrentWizardStep);
        Assert.Equal("第 1 步：配置目标语音输入工具", intWizard.StepTitle);

        intWizard.SelectedTool = TargetInputTool.WeChatInput;
        intWizard.ShortcutText = "Ctrl+F5";
        intWizard.NextStep();
        Assert.Equal(2, intWizard.CurrentStep);
        Assert.Equal(WizardStep.TranscriptionVerify, intWizard.CurrentWizardStep);
        Assert.Equal("第 2 步：真实输入法转写验收", intWizard.StepTitle);

        dummyBt.RaiseVoiceSessionEnded();
        intWizard.TranscriptionTestText = "语音转写测试";
        intWizard.IsTranscriptionVerified = true;
        intWizard.NextStep();
        Assert.Equal(3, intWizard.CurrentStep);
        Assert.Equal(WizardStep.Complete, intWizard.CurrentWizardStep);
        Assert.Equal("第 3 步：输入法集成配置完成", intWizard.StepTitle);
        intWizard.Complete();
        Assert.True(intWizard.IsCompleted);
        intWizard.Dispose();
    }

    [Fact]
    public void DevicesViewModel_OnlyShowsConfiguredDevices_AndIgnoresRawDiscoveredDevices()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-devvm-{Guid.NewGuid():N}.json");
        var config = new ConfigService(path);
        var dummyBt = new DummyBluetoothManager();

        var vm = new DevicesViewModel(config, dummyBt);
        Assert.False(vm.HasDevices);
        Assert.Empty(vm.DiscoveredDevices);

        // Raw device discovered over Bluetooth should NOT be added to DiscoveredDevices
        dummyBt.RaiseDeviceDiscovered(new RemoteDevice
        {
            Id = "random-unpaired",
            Name = "Air Device",
            BluetoothAddress = 0xAABBCCDDEEFF,
            ModelType = RemoteModelType.GoogleG9N9N
        });

        Assert.False(vm.HasDevices);
        Assert.Empty(vm.DiscoveredDevices);

        // When a configured device is added and reloaded
        config.Current.Devices.Add(new RemoteDevice
        {
            Id = "configured-device",
            Name = "My G9N9N",
            BluetoothAddress = 0x112233445566,
            ModelType = RemoteModelType.GoogleG9N9N
        });
        vm.Reload();

        Assert.True(vm.HasDevices);
        Assert.Single(vm.DiscoveredDevices);
        Assert.Equal("My G9N9N", vm.DiscoveredDevices[0].Name);

        // Discovered update for existing configured device updates its properties
        dummyBt.RaiseDeviceDiscovered(new RemoteDevice
        {
            Id = "configured-device",
            Name = "My G9N9N Updated",
            BluetoothAddress = 0x112233445566,
            ModelType = RemoteModelType.GoogleG9N9N
        });

        Assert.Equal("My G9N9N Updated", vm.DiscoveredDevices[0].Name);
        Assert.Single(vm.DiscoveredDevices);
    }

    [Fact]
    public void EnvironmentViewModel_DriverStatusBadgeText_TranslatesAccurately()
    {
        var vm = new EnvironmentViewModel();
        vm.DriverStatus = DriverReadinessStatus.Ready;
        Assert.Equal("已就绪", vm.DriverStatusBadgeText);

        vm.DriverStatus = DriverReadinessStatus.NotInstalled;
        Assert.Equal("未安装", vm.DriverStatusBadgeText);

        vm.DriverStatus = DriverReadinessStatus.Disabled;
        Assert.Equal("已禁用", vm.DriverStatusBadgeText);

        vm.DriverStatus = DriverReadinessStatus.RebootRequired;
        Assert.Equal("需重启", vm.DriverStatusBadgeText);

        vm.DriverStatus = DriverReadinessStatus.InitializationFailed;
        Assert.Equal("初始化失败", vm.DriverStatusBadgeText);
    }

    [Fact]
    public async Task DevicesViewModel_OnlyConnectedDeviceIsActive_WhenMultipleDevicesConfigured()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-dev-iso-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var g9n9n = new RemoteDevice { Id = "dev-g9n9n", BluetoothAddress = 0x4754514C6FCC, Name = "Chromecast Remote" };
            var xiaomi = new RemoteDevice { Id = "dev-xiaomi", BluetoothAddress = 0xA4C138123456, Name = "Xiaomi Remote" };
            config.Current.Devices.AddRange([g9n9n, xiaomi]);
            config.Current.SelectedDeviceId = g9n9n.Id;

            var dummyBt = new DummyBluetoothManager
            {
                IsConnected = true,
                ConnectedDeviceAddress = g9n9n.BluetoothAddress,
                ConnectedDeviceName = g9n9n.Name
            };

            using var vm = new DevicesViewModel(config, dummyBt);

            // Initially G9N9N is active, Xiaomi is inactive
            var dev1 = vm.DiscoveredDevices.First(d => d.Id == g9n9n.Id);
            var dev2 = vm.DiscoveredDevices.First(d => d.Id == xiaomi.Id);
            Assert.True(dev1.IsConnected);
            Assert.False(dev2.IsConnected);

            // Now connect Xiaomi
            await vm.ConnectDeviceAsync(dev2);

            // Only Xiaomi must be active; G9N9N must be disconnected
            Assert.False(dev1.IsConnected);
            Assert.True(dev2.IsConnected);
            Assert.Equal(xiaomi.Id, config.Current.SelectedDeviceId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DevicesViewModel_DiscoveredUnknownDevice_IsIgnoredWithoutPollutingConfiguredDevices()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-nearby-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var configured = new RemoteDevice
            {
                Id = "cfg-1",
                Name = "Original Name",
                BluetoothAddress = 0xAABBCCDDEEFF,
                ModelType = RemoteModelType.XiaomiRC001
            };
            config.Current.Devices.Add(configured);

            var bt = new DummyBluetoothManager();
            using var vm = new DevicesViewModel(config, bt);

            bt.RaiseDeviceDiscovered(new RemoteDevice
            {
                Id = "nearby-1",
                Name = "Nearby Chromecast",
                BluetoothAddress = 0x112233445566,
                ModelType = RemoteModelType.GoogleG9N9N
            });

            Assert.Single(vm.DiscoveredDevices);
            Assert.Equal("cfg-1", vm.DiscoveredDevices[0].Id);

            bt.RaiseDeviceDiscovered(new RemoteDevice
            {
                Id = "cfg-1",
                Name = "Updated Remote Name",
                BluetoothAddress = 0xAABBCCDDEEFF,
                ModelType = RemoteModelType.XiaomiRC003
            });

            Assert.Single(vm.DiscoveredDevices);
            Assert.Equal("Updated Remote Name", vm.DiscoveredDevices[0].Name);
            Assert.Equal(RemoteModelType.XiaomiRC003, vm.DiscoveredDevices[0].ModelType);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DevicesViewModel_GainChangesDebounce_AndFlushOnDispose()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-debounce-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var dev = new RemoteDevice
            {
                Id = "dev1",
                Name = "Test Remote",
                BluetoothAddress = 0x112233445566,
                ModelType = RemoteModelType.GoogleG9N9N,
                Gain = 2.0f
            };
            config.Current.Devices.Add(dev);
            config.Current.SelectedDeviceId = dev.Id;
            config.Save();

            var bt = new DummyBluetoothManager
            {
                IsConnected = true,
                ConnectedDeviceAddress = dev.BluetoothAddress
            };
            var vm = new DevicesViewModel(config, bt);

            // Rapid slider changes must update live BT settings immediately without synchronous disk save
            dev.Gain = 4.0f;
            dev.Gain = 6.5f;
            Assert.Equal(6.5f, bt.CurrentGain);
            Assert.Equal(2.0f, new ConfigService(path).Current.Devices.Single().Gain);

            // Disposing must flush the debounced gain write synchronously
            vm.Dispose();
            Assert.Equal(6.5f, new ConfigService(path).Current.Devices.Single().Gain);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DevicesViewModel_ConnectionStatusChanged_PreservesUserSelectedDevice_AndReloadSkipsSave()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-sel-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var dev1 = new RemoteDevice
            {
                Id = "dev1",
                Name = "Remote 1",
                BluetoothAddress = 0x111111111111,
                ModelType = RemoteModelType.GoogleG9N9N
            };
            var dev2 = new RemoteDevice
            {
                Id = "dev2",
                Name = "Remote 2",
                BluetoothAddress = 0x222222222222,
                ModelType = RemoteModelType.XiaomiRC003
            };
            config.Current.Devices.AddRange([dev1, dev2]);
            config.Current.SelectedDeviceId = dev2.Id;
            config.Save();

            var bt = new DummyBluetoothManager();
            using var vm = new DevicesViewModel(config, bt);
            Assert.Same(dev2, vm.SelectedDevice);

            // Connecting dev1 must not overwrite the user's valid dev2 selection
            bt.IsConnected = true;
            bt.ConnectedDeviceAddress = dev1.BluetoothAddress;
            bt.RaiseConnectionStatusChanged(true, dev1.Name, dev1.BluetoothAddress);

            Assert.True(dev1.IsConnected);
            Assert.Same(dev2, vm.SelectedDevice);

            // Reload must refresh UI collections without writing config.json
            var pastWrite = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(path, pastWrite);
            vm.Reload();
            Assert.Equal(pastWrite, File.GetLastWriteTimeUtc(path), TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OnboardingViewModel_WhenDriverAlreadyReady_PopulatesDriverStatusMessage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-onb-ready-{Guid.NewGuid():N}.json");
        var previousOverride = VirtualAudioAuditor.StatusOverride;
        try
        {
            VirtualAudioAuditor.StatusOverride = DriverReadinessStatus.Ready;
            var config = new ConfigService(path);
            using var vm = new OnboardingViewModel(config, new DummyBluetoothManager(), WizardMode.Full);

            Assert.Equal(DriverReadinessStatus.Ready, vm.DriverStatus);
            Assert.Contains("VB-CABLE 虚拟麦克风已就绪", vm.DriverStatusMessage);
        }
        finally
        {
            VirtualAudioAuditor.StatusOverride = previousOverride;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OnboardingViewModel_ApplyPresetDoubaoInput_SetsRightAltAndAllowsSwitchingOrCustomCapture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-onb-shortcut-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            using var vm = new OnboardingViewModel(config, new DummyBluetoothManager(), WizardMode.Integration);

            vm.ApplyPreset("DoubaoInput");
            Assert.Equal(TargetInputTool.DoubaoInput, vm.SelectedTool);
            Assert.Equal("RAlt", vm.ShortcutText);
            Assert.Equal(VoiceTriggerMode.HoldToTalk, vm.TriggerMode);
            Assert.NotNull(vm.SelectedShortcutOption);
            Assert.Equal("RAlt", vm.SelectedShortcutOption!.Value);
            Assert.False(vm.IsCustomShortcutSelected);

            // Selecting another preset updates ShortcutText while keeping SelectedTool
            var ctrlWinOption = OnboardingViewModel.VoiceShortcutOptions.Single(o => !o.IsCustom && o.Value == "Ctrl+Win");
            vm.SelectedShortcutOption = ctrlWinOption;
            Assert.Equal(TargetInputTool.DoubaoInput, vm.SelectedTool);
            Assert.Equal("Ctrl+Win", vm.ShortcutText);
            Assert.False(vm.IsCustomShortcutSelected);

            // Selecting Custom reveals the custom recorder without wiping existing ShortcutText
            var customOption = OnboardingViewModel.VoiceShortcutOptions.Single(o => o.IsCustom);
            vm.SelectedShortcutOption = customOption;
            Assert.True(vm.IsCustomShortcutSelected);
            Assert.Equal("Ctrl+Win", vm.ShortcutText);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OnboardingViewModel_UnlistedSavedShortcut_SelectsCustomOptionAutomatically()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-onb-custom-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            config.Current.InputTool.Tool = TargetInputTool.Custom;
            config.Current.InputTool.Shortcut = "Ctrl+Shift+F9";
            using var vm = new OnboardingViewModel(config, new DummyBluetoothManager(), WizardMode.Integration);

            Assert.True(vm.IsCustomShortcutSelected);
            Assert.Equal("Ctrl+Shift+F9", vm.ShortcutText);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void VoiceShortcutRecorder_CapturesStandaloneModifiersCombosAndCancelEscape()
    {
        var recorder = new VoiceShortcutRecorder();

        // 1. Standalone Right Alt (arrives as Key.System + SystemKey=Key.RightAlt in WPF)
        Assert.True(recorder.OnKeyDown(Key.System, Key.RightAlt, ModifierKeys.Alt, out var captured, out bool cancelled));
        Assert.Null(captured);
        Assert.False(cancelled);
        Assert.True(recorder.OnKeyUp(Key.System, Key.RightAlt, out captured));
        Assert.Equal("RAlt", captured);

        // 2. Standalone Right Ctrl
        Assert.True(recorder.OnKeyDown(Key.RightCtrl, Key.None, ModifierKeys.Control, out captured, out cancelled));
        Assert.Null(captured);
        Assert.True(recorder.OnKeyUp(Key.RightCtrl, Key.None, out captured));
        Assert.Equal("RCtrl", captured);

        // 3. Regular modifier + main key completes on KeyDown immediately
        Assert.True(recorder.OnKeyDown(Key.LeftCtrl, Key.None, ModifierKeys.Control, out captured, out cancelled));
        Assert.Null(captured);
        Assert.True(recorder.OnKeyDown(Key.F5, Key.None, ModifierKeys.Control, out captured, out cancelled));
        Assert.Equal("Ctrl+F5", captured);
        Assert.False(cancelled);

        // 4. Multi-modifier-only combo (Ctrl + Win) captures on modifier release
        Assert.True(recorder.OnKeyDown(Key.LeftCtrl, Key.None, ModifierKeys.Control, out _, out _));
        Assert.True(recorder.OnKeyDown(Key.LWin, Key.None, ModifierKeys.Control | ModifierKeys.Windows, out _, out _));
        Assert.True(recorder.OnKeyUp(Key.LWin, Key.None, out captured));
        Assert.Equal("Ctrl+Win", captured);

        // 5. Standalone Escape cancels recording
        Assert.True(recorder.OnKeyDown(Key.Escape, Key.None, ModifierKeys.None, out captured, out cancelled));
        Assert.Null(captured);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task DevicesViewModel_ConnectFailure_SurfacesReadableFeedback()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-devfail-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new DummyBluetoothManager { ConnectResult = false };
            using var vm = new DevicesViewModel(config, bt);
            var dev = new RemoteDevice { Id = "d1", Name = "Chromecast", BluetoothAddress = 0x112233445566 };

            await vm.ConnectDeviceAsync(dev);

            Assert.True(vm.HasConnectionFeedback);
            Assert.Contains("Chromecast", vm.ConnectionFeedbackMessage);
            vm.DismissConnectionFeedback();
            Assert.False(vm.HasConnectionFeedback);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(CableOutputReadiness.Missing, "录音端缺失")]
    [InlineData(CableOutputReadiness.Disabled, "录音端已禁用")]
    public void CableOutputMissingOrDisabled_UpdatesEnvironmentIntegrationsGeneralAndOnboardingViews(
        CableOutputReadiness outputState,
        string expectedBadgeText)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-cableout-{Guid.NewGuid():N}.json");
        var prevDriverOverride = VirtualAudioAuditor.StatusOverride;
        var prevOutputOverride = VirtualAudioAuditor.CableOutputOverride;
        try
        {
            VirtualAudioAuditor.StatusOverride = DriverReadinessStatus.Ready;
            VirtualAudioAuditor.CableOutputOverride = outputState;
            var config = new ConfigService(path);
            var bt = new DummyBluetoothManager();

            var envVm = new EnvironmentViewModel();
            Assert.Equal(expectedBadgeText, envVm.DriverStatusBadgeText);
            Assert.Equal("#FFB347", envVm.DriverStatusBadgeColor);
            Assert.Contains("CABLE Output", envVm.StatusDescription);

            var intVm = new IntegrationsViewModel(config);
            Assert.Equal("#FFB347", intVm.VirtualCableStatusBrush);
            Assert.Contains("CABLE Output", intVm.VirtualCableStatusDescription);

            using var genVm = new GeneralViewModel(config, bt, () => { }, _ => { });
            Assert.False(genVm.PermissionsReady);
            Assert.Equal(expectedBadgeText, genVm.PermissionsStatusText);
            Assert.Equal("#FFAA33", genVm.PermissionsStatusBrush);
            Assert.Equal("⚠", genVm.PermissionsIcon);
            Assert.Contains("CABLE Output", genVm.PermissionsSummary);

            using var onbVm = new OnboardingViewModel(config, bt, WizardMode.Full);
            Assert.DoesNotContain("✅ VB-CABLE 虚拟麦克风已就绪", onbVm.DriverStatusMessage);
            Assert.Contains("CABLE Output", onbVm.DriverStatusMessage);
        }
        finally
        {
            VirtualAudioAuditor.StatusOverride = prevDriverOverride;
            VirtualAudioAuditor.CableOutputOverride = prevOutputOverride;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
