// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using RemoteBuddy.App.Converters;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Audio;
using Xunit;

namespace RemoteBuddy.Tests;

public class ConvertersAndViewModelTests
{
    private sealed class DummyBluetoothManager : IBluetoothManager
    {
        public bool IsScanning { get; private set; }
        public bool IsConnected { get; set; } = false;
        public bool IsReady => false;
        public bool IsStreaming => false;
        public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
        public BluetoothSessionState SessionState => BluetoothSessionState.Idle;
        public string ConnectedDeviceName { get; set; } = string.Empty;
        public ulong ConnectedDeviceAddress { get; set; } = 0;
        public Guid? ConnectedDeviceContainerId { get; set; } = null;
        public RemoteDeviceInformation? ConnectedDeviceInformation { get; private set; }
        public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;

        public event Action<RemoteDevice>? DeviceDiscovered;
        public event Action<bool, string>? ConnectionStatusChanged;
        public event Action? ConnectedDeviceInformationChanged;
        public event Action<float>? AudioLevelChanged { add { } remove { } }
        public event Action? VoiceButtonPressed { add { } remove { } }
        public event Action? VoiceSessionStarted { add { } remove { } }
        public event Action? VoiceSessionEnded;

        public void RaiseDeviceDiscovered(RemoteDevice dev) => DeviceDiscovered?.Invoke(dev);
        public void RaiseConnectionStatusChanged(bool connected, string name) => ConnectionStatusChanged?.Invoke(connected, name);
        public void SetConnectedDeviceInformation(RemoteDeviceInformation? information)
        {
            ConnectedDeviceInformation = information;
            ConnectedDeviceInformationChanged?.Invoke();
        }
        public void RaiseVoiceSessionEnded() => VoiceSessionEnded?.Invoke();

        public void StartScanning() => IsScanning = true;
        public void StopScanning() => IsScanning = false;
        public Task DiscoverPairedRemotesAsync() => Task.CompletedTask;
        public void StartScan() => StartScanning();
        public void StopScan() => StopScanning();
        public Task<bool> ConnectAsync(ulong bluetoothAddress)
        {
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
        var config = new ConfigService(path);
        var dev1 = new RemoteDevice { Id = "dev1", BluetoothAddress = 1, Name = "Remote 1" };
        var dev2 = new RemoteDevice { Id = "dev2", BluetoothAddress = 2, Name = "Remote 2" };
        config.Current.Devices.AddRange([dev1, dev2]);
        config.Current.SelectedDeviceId = dev1.Id;

        var dummyBt = new DummyBluetoothManager();
        DevicesViewModel.ConfirmDeviceRemovalHandler = _ => true;
        var vm = new DevicesViewModel(config, dummyBt);

        Assert.Equal(2, vm.DiscoveredDevices.Count);

        vm.RemoveDevice(dev1);

        Assert.Single(vm.DiscoveredDevices);
        Assert.Equal("dev2", vm.DiscoveredDevices[0].Id);
        Assert.Single(config.Current.Devices);
        Assert.Null(config.Current.SelectedDeviceId);
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
        Assert.False(string.IsNullOrEmpty(vm.Runtime));
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
}
