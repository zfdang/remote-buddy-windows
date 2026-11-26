// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.Services;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Input;
using Xunit;

namespace RemoteBuddy.Tests;

public class MainViewModelTests
{
    private sealed class FakeUsbKeyboardSender : IUsbKeyboardSender
    {
        public List<string> DownCalls { get; } = [];
        public List<string> ClickCalls { get; } = [];
        public int ReleaseCalls { get; private set; }
        public bool NextResult { get; set; } = true;
        public Action<string>? OnSendDown { get; set; }

        public bool SendDown(string shortcut)
        {
            DownCalls.Add(shortcut);
            OnSendDown?.Invoke(shortcut);
            return NextResult;
        }

        public bool Release()
        {
            ReleaseCalls++;
            return NextResult;
        }

        public bool SendClick(string shortcut)
        {
            ClickCalls.Add(shortcut);
            return NextResult;
        }

        public void Dispose() { }
    }

    private sealed class FakeCommonPhraseInserter : ICommonPhraseInserter
    {
        public CommonPhraseTarget? CapturedTarget { get; set; } = new(new nint(0x1001), 4242, new nint(0x2002), "1.2");
        public bool TargetActive { get; set; } = true;
        public List<string> InsertedTexts { get; } = [];
        public List<CommonPhraseTarget?> InsertedTargets { get; } = [];

        public CommonPhraseTarget? CaptureTarget() => CapturedTarget;
        public bool IsTargetActive(CommonPhraseTarget target) => TargetActive && Equals(CapturedTarget, target);
        public bool InsertText(string text, CommonPhraseTarget? expectedTarget = null)
        {
            if (expectedTarget != null && !IsTargetActive(expectedTarget)) return false;
            InsertedTexts.Add(text);
            InsertedTargets.Add(expectedTarget);
            return true;
        }
    }

    private sealed class FakeAppComposerFocus : IAppComposerFocus
    {
        private static readonly AppFocusTarget Target = new(1, 2, "chatgpt");
        public TaskCompletionSource<AppFocusResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public bool Foreground { get; set; } = true;
        public AppFocusTarget? Capture(IReadOnlyDictionary<string, bool>? enabledIntegrations) =>
            enabledIntegrations?.GetValueOrDefault("chatgpt") == true ? Target : null;
        public bool IsForeground(AppFocusTarget target) => Foreground;
        public Task<AppFocusResult> FocusAsync(AppFocusTarget target, Func<bool> isVoiceLive, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return Completion.Task;
        }
    }

    private sealed class TestBluetoothManager : IBluetoothManager
    {
        public bool IsScanning { get; private set; }
        public bool IsConnected { get; set; } = false;
        public bool IsReady { get; set; } = false;
        public bool IsStreaming => VoiceDeviceAddress != 0;
        public HashSet<ulong> ReadyAddresses { get; } = [];
        public Dictionary<ulong, EtwAttributeFilter> EtwFilters { get; } = [];
        public EtwAttributeFilter GetEtwAttributeFilter(ulong address) => EtwFilters.GetValueOrDefault(address);
        public ulong VoiceDeviceAddress { get; set; }
        public IReadOnlyCollection<ulong> ConnectedDeviceAddresses => ReadyAddresses.Count > 0
            ? ReadyAddresses.ToArray()
            : (IsConnected && ConnectedDeviceAddress != 0 ? [ConnectedDeviceAddress] : []);
        public bool IsDeviceReady(ulong address) => ReadyAddresses.Contains(address) ||
            (IsReady && IsConnected && address != 0 && address == ConnectedDeviceAddress);
        public Task<bool> StartVoiceSessionAsync(ulong address)
        {
            if (VoiceDeviceAddress != 0) return Task.FromResult(false);
            VoiceDeviceAddress = address;
            return StartVoiceSessionAsync();
        }
        public Task<bool> StopVoiceSessionAsync(ulong address)
        {
            if (VoiceDeviceAddress != address) return Task.FromResult(false);
            VoiceDeviceAddress = 0;
            return StopVoiceSessionAsync();
        }
        public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
        public BluetoothSessionState SessionState => BluetoothSessionState.Idle;
        public string ConnectedDeviceName { get; set; } = string.Empty;
        public ulong ConnectedDeviceAddress { get; set; } = 0;
        public Guid? ConnectedDeviceContainerId { get; set; } = null;
        public RemoteDeviceInformation? ConnectedDeviceInformation => null;
        public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;

        public event Action<RemoteDevice>? DeviceDiscovered { add { } remove { } }
        public event Action<ulong, bool, string>? ConnectionStatusChanged;
        public event Action<ulong>? ConnectedDeviceInformationChanged { add { } remove { } }
        public event Action<ulong, float>? AudioLevelChanged { add { } remove { } }
        public event Action<ulong>? VoiceButtonPressed;
        public event Action<ulong>? VoiceSessionStarted;
        public event Action<ulong>? VoiceSessionEnded;
        public event Action<ulong, string>? AudioOutputFailed;

        public int StartVoiceSessionCount { get; private set; }
        public int StopVoiceSessionCount { get; private set; }

        public void RaiseConnectionStatusChanged(bool connected, string name, ulong address = 0) =>
            ConnectionStatusChanged?.Invoke(address != 0 ? address : ConnectedDeviceAddress, connected, name);
        public void RaiseVoiceButtonPressed(ulong address) => VoiceButtonPressed?.Invoke(address);
        public void RaiseVoiceSessionStarted(ulong address)
        {
            VoiceDeviceAddress = address;
            VoiceSessionStarted?.Invoke(address);
        }
        public void RaiseVoiceSessionEnded(ulong address)
        {
            if (VoiceDeviceAddress == address) VoiceDeviceAddress = 0;
            VoiceSessionEnded?.Invoke(address);
        }
        public void RaiseAudioOutputFailed(ulong address, string reason) => AudioOutputFailed?.Invoke(address, reason);

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

    [Theory]
    [InlineData((int)AppFocusResult.ExistingInput)]
    [InlineData((int)AppFocusResult.ComposerFocused)]
    [InlineData((int)AppFocusResult.ComposerUnavailable)]
    public async Task AppIntegration_WaitsForFocusResultBeforeSendingVoiceShortcut(int result)
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"rb-focus-{Guid.NewGuid():N}.json"));
        config.Current.AppIntegrations["chatgpt"] = true;
        var bluetooth = new TestBluetoothManager();
        var focus = new FakeAppComposerFocus();
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyboard = new FakeUsbKeyboardSender { OnSendDown = shortcut => sent.TrySetResult(shortcut) };
        using var vm = new MainViewModel(config, bluetooth)
        {
            AppComposerFocusForTest = focus,
            UsbKeyboardForTest = keyboard,
            IsAdministratorForTest = () => true
        };

        bluetooth.RaiseVoiceSessionStarted(1);
        Assert.True(focus.Token.CanBeCanceled);
        Assert.Empty(keyboard.DownCalls);
        focus.Completion.SetResult((AppFocusResult)result);
        Assert.Equal(InputToolConfig.DefaultWeChatShortcut, await sent.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        bluetooth.RaiseVoiceSessionEnded(1);
    }

    [Fact]
    public void AppIntegration_DoesNotSendShortcutToChangedForeground()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"rb-focus-{Guid.NewGuid():N}.json"));
        config.Current.AppIntegrations["chatgpt"] = true;
        var bluetooth = new TestBluetoothManager();
        var focus = new FakeAppComposerFocus { Foreground = false };
        focus.Completion.SetResult(AppFocusResult.ComposerFocused);
        var keyboard = new FakeUsbKeyboardSender();
        using var vm = new MainViewModel(config, bluetooth)
        {
            AppComposerFocusForTest = focus,
            UsbKeyboardForTest = keyboard,
            IsAdministratorForTest = () => true
        };

        bluetooth.RaiseVoiceSessionStarted(1);
        bluetooth.RaiseVoiceSessionEnded(1);
        Assert.Empty(keyboard.DownCalls);
        Assert.Equal(0, keyboard.ReleaseCalls);
    }

    [Fact]
    public void AppIntegration_VoiceEndCancelsPendingFocusWithoutDispatch()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"rb-focus-{Guid.NewGuid():N}.json"));
        config.Current.AppIntegrations["chatgpt"] = true;
        var bluetooth = new TestBluetoothManager();
        var focus = new FakeAppComposerFocus();
        var keyboard = new FakeUsbKeyboardSender();
        using var vm = new MainViewModel(config, bluetooth)
        {
            AppComposerFocusForTest = focus,
            UsbKeyboardForTest = keyboard,
            IsAdministratorForTest = () => true
        };

        bluetooth.RaiseVoiceSessionStarted(1);
        bluetooth.RaiseVoiceSessionEnded(1);
        Assert.True(focus.Token.IsCancellationRequested);
        focus.Completion.SetResult(AppFocusResult.ComposerFocused);
        Assert.Empty(keyboard.DownCalls);
        Assert.Equal(0, keyboard.ReleaseCalls);
    }

    [Fact]
    public void AppIntegration_ComposerMatchingUsesOnlyExplicitControlMetadata()
    {
        Assert.Equal("wechat", AppComposerFocus.GetIntegrationId("Weixin"));
        Assert.Equal("chatgpt", AppComposerFocus.GetIntegrationId("ChatGPT"));
        Assert.Null(AppComposerFocus.GetIntegrationId("WeType"));
        Assert.Null(AppComposerFocus.GetIntegrationId("msedge"));
        Assert.True(AppComposerFocus.ComposerScore("chatgpt", "prompt-textarea", "Ask anything") > 0);
        Assert.True(AppComposerFocus.ComposerScore("antigravity", "", "Message input") > 0);
        Assert.True(AppComposerFocus.ComposerScore("deepseek", "", "描述你想要构建的内容, / 调用指令, @ 文件或对话") > 0);
        Assert.Equal(0, AppComposerFocus.ComposerScore("deepseek", "", "搜索会话名称"));
        Assert.Equal(0, AppComposerFocus.ComposerScore("wechat", "chat-input-field", "搜索"));
        Assert.Equal(0, AppComposerFocus.ComposerScore("claude", "", "Password"));
        Assert.Equal(0, AppComposerFocus.ComposerScore("chatgpt", "", "Type here"));
    }

    [Fact]
    public void MainViewModel_InitialState_LoadsConfigAndDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager();
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

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
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

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
            const ulong addr = 0x4754514C6FCC;
            var dev = new RemoteDevice
            {
                Id = "g9",
                Name = "Chromecast Remote",
                BluetoothAddress = addr,
                ModelType = RemoteModelType.GoogleG9N9N
            };
            config.Current.Devices.Add(dev);
            bt.IsConnected = true;
            bt.IsReady = true;
            bt.ConnectedDeviceAddress = addr;
            bt.ReadyAddresses.Add(addr);
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

            vm.HandleDeviceRemoval(new IntPtr(0x1234));
            Assert.Equal("General", vm.ActivePageName);
            Assert.Single(vm.MappingVm.Devices);
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

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };
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
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = 0x4754514C6FCC
            };

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

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
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = 0x4754514C6FCC
            };

            var mappedActions = new List<ButtonAction>();
            using var vm = new MainViewModel(config, bt, action => mappedActions.Add(action.Action))
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };
            vm.Navigate("Mapping");
            vm.MappingVm.SelectedDevice = xiaomi; // Viewing Xiaomi in UI

            RemoteButton? pressedButton = null;
            vm.MappingVm.HardwareButtonPressed += btn => pressedButton = btn;

            // Event from G9N9N (active Bluetooth address) arrives
            vm.HandleRawButtonEvent(IntPtr.Zero, RemoteButton.Home, true);
            Assert.Equal([ButtonAction.TaskView], mappedActions);

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
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = 0x4754514C6FCC
            };

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

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

    [Fact]
    public void DeviceSwitching_UpdatesActiveRemoteAndSyncsMapping()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-mvm-switch-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var xiaomi = new RemoteDevice
            {
                Id = "DEV_XIAOMI",
                Name = "Xiaomi Remote",
                BluetoothAddress = 0xA4C138123456,
            };
            var g9n9n = new RemoteDevice
            {
                Id = "DEV_G9N9N",
                Name = "Chromecast Remote",
                BluetoothAddress = 0x4754514C6FCC,
            };
            config.Current.Devices.Add(xiaomi);
            config.Current.Devices.Add(g9n9n);
            config.Current.SelectedDeviceId = xiaomi.Id;

            var bt = new TestBluetoothManager
            {
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = xiaomi.BluetoothAddress,
                ConnectedDeviceName = xiaomi.Name
            };

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

            // Initially Xiaomi is selected
            Assert.Equal(xiaomi.Id, config.Current.SelectedDeviceId);
            Assert.Equal(xiaomi.Id, vm.MappingVm.SelectedDevice?.Id);

            // Now switch to G9N9N
            bt.ConnectedDeviceAddress = g9n9n.BluetoothAddress;
            bt.ConnectedDeviceName = g9n9n.Name;
            bt.RaiseConnectionStatusChanged(true, g9n9n.Name);

            // SelectedDeviceId should automatically update to G9N9N
            Assert.Equal(g9n9n.Id, config.Current.SelectedDeviceId);
            Assert.Equal(g9n9n.Id, vm.MappingVm.SelectedDevice?.Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
    [Fact]
    public void SameModelButtonsMapIndependentlyAndLosingVoiceKeyCannotStopTheOwner()
    {
        string path = Path.Combine(Path.GetTempPath(), $"multi-remote-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bluetooth = new TestBluetoothManager();
            var actions = new List<string?>();
            foreach (ulong id in new ulong[] { 1, 2, 3 })
            {
                var device = new RemoteDevice
                {
                    Id = id.ToString(), BluetoothAddress = id, Name = "Chromecast Remote",
                    ModelType = RemoteModelType.GoogleG9N9N,
                    Mappings = [new ButtonMapping { Button = RemoteButton.Ok, SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = $"remote-{id}" } }]
                };
                config.Current.Devices.Add(device);
                bluetooth.ReadyAddresses.Add(id);
            }
            config.Current.InputTool.TriggerMode = VoiceTriggerMode.HoldToTalk;
            using var vm = new MainViewModel(config, bluetooth, action => actions.Add(action.Text));

            vm.HandleDeviceButton(1, RemoteButton.Voice, true);
            vm.HandleDeviceButton(2, RemoteButton.Voice, true);
            vm.HandleDeviceButton(2, RemoteButton.Voice, false);
            Assert.Equal(1UL, bluetooth.VoiceDeviceAddress);
            foreach (ulong address in bluetooth.ReadyAddresses)
                vm.HandleDeviceButton(address, RemoteButton.Ok, true);
            foreach (ulong address in bluetooth.ReadyAddresses)
                vm.HandleDeviceButton(address, RemoteButton.Ok, false);
            Assert.Equal(new[] { "remote-1", "remote-2", "remote-3" }, actions);

            vm.HandleDeviceButton(1, RemoteButton.Voice, false);
            vm.HandleDeviceButton(3, RemoteButton.Voice, true);
            Assert.Equal(3UL, bluetooth.VoiceDeviceAddress);
            Assert.Equal(2, bluetooth.StartVoiceSessionCount);
            Assert.Equal(1, bluetooth.StopVoiceSessionCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void XiaomiRC003_DirectionalKeyWithIdentityMapping_SkipsDuplicateInjection()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xiaomi-identity-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bluetooth = new TestBluetoothManager();
            var executedShortcuts = new List<string?>();

            var xiaomi = new RemoteDevice
            {
                Id = "xiaomi_rc003",
                BluetoothAddress = 0x112233445566,
                Name = "Xiaomi Bluetooth Remote 2 Pro",
                ModelType = RemoteModelType.XiaomiRC003,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Left,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.ArrowLeft }
                    },
                    new ButtonMapping
                    {
                        Button = RemoteButton.Right,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.DirectPassthrough }
                    },
                    new ButtonMapping
                    {
                        Button = RemoteButton.Ok,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.ReturnKey }
                    }
                ]
            };
            config.Current.Devices.Add(xiaomi);
            bluetooth.ReadyAddresses.Add(xiaomi.BluetoothAddress);

            using var vm = new MainViewModel(config, bluetooth, action => executedShortcuts.Add(action.Action.ToString()));

            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Left, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Left, false);

            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Right, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Right, false);

            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Ok, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Ok, false);

            // Identity shortcuts must NOT be synthesized, preventing double movement in Task View
            Assert.Empty(executedShortcuts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void XiaomiRC003_NativeKeyboardNavigationKeys_SkipOverlappingSyntheticActions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xiaomi-custom-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bluetooth = new TestBluetoothManager();
            var executedActions = new List<ButtonAction>();

            var xiaomi = new RemoteDevice
            {
                Id = "xiaomi_rc003",
                BluetoothAddress = 0x112233445566,
                Name = "Xiaomi Bluetooth Remote 2 Pro",
                ModelType = RemoteModelType.XiaomiRC003,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Left,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.CustomShortcut, Shortcut = "Ctrl+Left" }
                    },
                    new ButtonMapping
                    {
                        Button = RemoteButton.Ok,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.ReturnKey },
                        LongPress = new ConfiguredButtonAction { Action = ButtonAction.CommandReturn }
                    },
                    new ButtonMapping
                    {
                        Button = RemoteButton.Home,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TaskView }
                    }
                ]
            };
            config.Current.Devices.Add(xiaomi);
            bluetooth.ReadyAddresses.Add(xiaomi.BluetoothAddress);

            using var vm = new MainViewModel(config, bluetooth, action => executedActions.Add(action.Action));

            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Left, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Left, false);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Ok, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Ok, false);

            // Native keyboard keys (Left, Ok) must NOT emit overlapping synthetic actions
            Assert.Empty(executedActions);

            // Non-keyboard Xiaomi buttons (Home, Menu, Power, etc.) still execute mapped actions
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Home, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Home, false);
            Assert.Equal(new[] { ButtonAction.TaskView }, executedActions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void GoogleG9N9N_DirectionalKey_ExecutesShortcutNormally()
    {
        string path = Path.Combine(Path.GetTempPath(), $"g9n9n-dpad-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bluetooth = new TestBluetoothManager();
            var executedActions = new List<ButtonAction>();

            var g9n9n = new RemoteDevice
            {
                Id = "g9n9n",
                BluetoothAddress = 0x4754514C6FCC,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Left,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.ArrowLeft }
                    }
                ]
            };
            config.Current.Devices.Add(g9n9n);
            bluetooth.ReadyAddresses.Add(g9n9n.BluetoothAddress);

            using var vm = new MainViewModel(config, bluetooth, action => executedActions.Add(action.Action));

            vm.HandleDeviceButton(g9n9n.BluetoothAddress, RemoteButton.Left, true);
            vm.HandleDeviceButton(g9n9n.BluetoothAddress, RemoteButton.Left, false);

            // G9N9N does not deliver native Windows keyboard arrow keys, so synthetic injection is required
            Assert.Equal(new[] { ButtonAction.ArrowLeft }, executedActions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CrossDevice_StaleVoiceSessionEnded_DoesNotCancelActiveRemoteSession()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cross-voice-end-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong remoteA = 0x111111111111;
            const ulong remoteB = 0x222222222222;
            bt.ReadyAddresses.Add(remoteA);
            bt.ReadyAddresses.Add(remoteB);
            config.Current.Devices.Add(new RemoteDevice { Id = "A", BluetoothAddress = remoteA, Name = "Remote A" });
            config.Current.Devices.Add(new RemoteDevice { Id = "B", BluetoothAddress = remoteB, Name = "Remote B" });

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

            bt.RaiseVoiceSessionStarted(remoteB);
            Assert.True(vm.IsVoiceActive);

            // Spurious or delayed end notification for Remote A must NOT cancel Remote B
            bt.RaiseVoiceSessionEnded(remoteA);
            Assert.True(vm.IsVoiceActive);

            // Matching end for Remote B terminates it cleanly
            bt.RaiseVoiceSessionEnded(remoteB);
            Assert.False(vm.IsVoiceActive);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SingleRemoteDisconnect_WhileAnotherRemainsConnected_PreservesOtherRemoteVoiceSession()
    {
        string path = Path.Combine(Path.GetTempPath(), $"single-disc-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager
            {
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = 0x222222222222,
                ConnectedDeviceName = "Remote B"
            };
            const ulong remoteA = 0x111111111111;
            const ulong remoteB = 0x222222222222;
            bt.ReadyAddresses.Add(remoteA);
            bt.ReadyAddresses.Add(remoteB);
            config.Current.Devices.Add(new RemoteDevice { Id = "A", BluetoothAddress = remoteA, Name = "Remote A" });
            config.Current.Devices.Add(new RemoteDevice { Id = "B", BluetoothAddress = remoteB, Name = "Remote B" });

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };
            bt.RaiseVoiceSessionStarted(remoteB);
            Assert.True(vm.IsVoiceActive);

            bt.ReadyAddresses.Remove(remoteA);
            bt.RaiseConnectionStatusChanged(false, "Remote A", remoteA);

            Assert.True(vm.IsConnected);
            Assert.True(vm.IsVoiceActive);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void VoiceButtonPressed_WithDeviceAddress_ReachesOnboardingWithoutActiveVoiceStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"voice-btn-onb-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true, VoiceDeviceAddress = 0 };
            const ulong remoteB = 0x222222222222;
            var devB = new RemoteDevice { Id = "B", BluetoothAddress = remoteB, Name = "Remote B" };
            config.Current.Devices.Add(devB);

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };
            using var onboarding = new OnboardingViewModel(config, bt, WizardMode.DeviceOnly)
            {
                SelectedDevice = devB,
                CurrentStep = 2
            };
            vm.ActiveOnboardingVm = onboarding;

            bt.RaiseVoiceButtonPressed(remoteB);

            Assert.True(onboarding.HasButtonTested);
            Assert.Contains("语音键", onboarding.LastPressedButtonName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OnboardingConnectionChanged_IgnoresUnrelatedRemoteStatus()
    {
        string path = Path.Combine(Path.GetTempPath(), $"onb-filter-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager();
            var devB = new RemoteDevice { Id = "B", BluetoothAddress = 0x222222222222, Name = "Remote B" };
            using var onboarding = new OnboardingViewModel(config, bt, WizardMode.DeviceOnly)
            {
                SelectedDevice = devB
            };

            bt.RaiseConnectionStatusChanged(false, "Remote A", 0x111111111111);
            Assert.DoesNotContain("Remote A", onboarding.DeviceConnectionStatus);

            bt.ReadyAddresses.Add(devB.BluetoothAddress);
            bt.RaiseConnectionStatusChanged(true, "Remote B", devB.BluetoothAddress);
            Assert.True(onboarding.IsDeviceConnected);
            Assert.Contains("Remote B", onboarding.DeviceConnectionStatus);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MultiRemoteBluetoothManager_DisposeIsIdempotentAndRemoveDeviceSafe()
    {
        var manager = new RemoteBuddy.Platform.Windows.Bluetooth.MultiRemoteBluetoothManager();
        manager.RemoveDevice(0x112233445566);
        manager.Dispose();
        manager.Dispose();
        // SetDeviceVoiceSettings after Dispose must not re-populate state or throw
        manager.SetDeviceVoiceSettings(new RemoteDevice { Id = "X", BluetoothAddress = 0x112233445566 });
        Assert.Empty(manager.ConnectedDeviceAddresses);
    }

    [Fact]
    public void TryRunChromecastWorker_ParsesBothParentPidAndLegacyFormats()
    {
        var sources = new[]
        {
            new RemoteBuddy.Platform.Windows.Input.EtwButtonSource(
                0x112233445566,
                Guid.NewGuid(),
                RemoteBuddy.Platform.Windows.Input.EtwProtocolKind.GoogleG9N9N,
                HidHandleStart: 0x0020,
                HidHandleEnd: 0x004F,
                ExcludedAttributes: [0x0052, 0x0055])
        };
        string encoded = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sources));

        string? capturedPipe = null;
        int capturedPid = -1;
        RemoteBuddy.Platform.Windows.Input.EtwButtonSource[]? capturedSources = null;

        string pipeA = "RemoteBuddy_ChromecastEtw_" + Guid.NewGuid().ToString("N");
        string pipeB = "RemoteBuddy_ChromecastEtw_" + Guid.NewGuid().ToString("N");

        bool handled4Arg = RemoteBuddy.App.App.TryRunChromecastWorker(
            ["--chromecast-worker", pipeA, "12345", encoded],
            (pipe, pid, s) => { capturedPipe = pipe; capturedPid = pid; capturedSources = s; });
        Assert.True(handled4Arg);
        Assert.Equal(pipeA, capturedPipe);
        Assert.Equal(12345, capturedPid);
        Assert.NotNull(capturedSources);
        Assert.Single(capturedSources);
        Assert.Equal((ushort)0x0020, capturedSources[0].HidHandleStart);
        Assert.Equal((ushort)0x004F, capturedSources[0].HidHandleEnd);
        Assert.Equal(new ushort[] { 0x0052, 0x0055 }, capturedSources[0].ExcludedAttributes);

        bool handled3Arg = RemoteBuddy.App.App.TryRunChromecastWorker(
            ["--chromecast-worker", pipeB, encoded],
            (pipe, pid, s) => { capturedPipe = pipe; capturedPid = pid; capturedSources = s; });
        Assert.True(handled3Arg);
        Assert.Equal(pipeB, capturedPipe);
        Assert.Equal(0, capturedPid);
        Assert.Single(capturedSources!);

        // Invalid pipe names must be rejected without invoking workerRunner
        bool invokedInvalid = false;
        Assert.True(RemoteBuddy.App.App.TryRunChromecastWorker(
            ["--chromecast-worker", "invalid-pipe", encoded],
            (_, _, _) => invokedInvalid = true));
        Assert.False(invokedInvalid);
    }

    [Fact]
    public void EtwUnexpectedDisconnect_ResetsHeldGestures()
    {
        string path = Path.Combine(Path.GetTempPath(), $"etw-disc-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0x4754514C6FCC;
            bt.ReadyAddresses.Add(addr);
            var executed = new List<RemoteButton>();
            var g9n9n = new RemoteDevice
            {
                Id = "g9",
                BluetoothAddress = addr,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Home,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "home" },
                        LongPress = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "home-long" }
                    }
                ]
            };
            config.Current.Devices.Add(g9n9n);
            config.Current.SelectedDeviceId = g9n9n.Id;

            using var vm = new MainViewModel(config, bt, _ => executed.Add(RemoteButton.Home));
            vm.HandleDeviceButton(addr, RemoteButton.Home, true);
            vm.TriggerEtwUnexpectedDisconnectForTest();
            vm.HandleDeviceButton(addr, RemoteButton.Home, false);

            // Releasing after UnexpectedDisconnect must NOT fire Click because active gestures were reset
            Assert.Empty(executed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HandleDeviceButton_DualStreamShortClickInterleave_ExecutesOnlyOnce()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dedup-interleave-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0x4754514C6FCC;
            bt.ReadyAddresses.Add(addr);
            var executed = new List<string?>();
            var device = new RemoteDevice
            {
                Id = "g9",
                BluetoothAddress = addr,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Home,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "home" }
                    }
                ]
            };
            config.Current.Devices.Add(device);
            config.Current.SelectedDeviceId = device.Id;

            using var vm = new MainViewModel(config, bt, a => executed.Add(a.Text));
            vm.HandleDeviceButton(addr, RemoteButton.Home, true, MainViewModel.InputSource.RawInput);
            vm.HandleDeviceButton(addr, RemoteButton.Home, false, MainViewModel.InputSource.RawInput);
            vm.HandleDeviceButton(addr, RemoteButton.Home, true, MainViewModel.InputSource.Etw);
            vm.HandleDeviceButton(addr, RemoteButton.Home, false, MainViewModel.InputSource.Etw);

            Assert.Equal(["home"], executed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HandleDeviceButton_CrossButtonInterleave_DedupsBothButtonsWithoutClobbering()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dedup-crossbtn-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0x4754514C6FCC;
            bt.ReadyAddresses.Add(addr);
            var executed = new List<string?>();
            var device = new RemoteDevice
            {
                Id = "g9",
                BluetoothAddress = addr,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Home,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "home" }
                    },
                    new ButtonMapping
                    {
                        Button = RemoteButton.Back,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "back" }
                    }
                ]
            };
            config.Current.Devices.Add(device);
            config.Current.SelectedDeviceId = device.Id;

            using var vm = new MainViewModel(config, bt, a => executed.Add(a.Text));
            vm.HandleDeviceButton(addr, RemoteButton.Home, true, MainViewModel.InputSource.RawInput);
            vm.HandleDeviceButton(addr, RemoteButton.Back, true, MainViewModel.InputSource.RawInput);
            vm.HandleDeviceButton(addr, RemoteButton.Home, true, MainViewModel.InputSource.Etw);

            Assert.Equal(["home", "back"], executed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HandleDeviceButton_FastSameSourceDoubleClick_Preserved()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dedup-dbl-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0x4754514C6FCC;
            bt.ReadyAddresses.Add(addr);
            var executed = new List<string?>();
            var device = new RemoteDevice
            {
                Id = "g9",
                BluetoothAddress = addr,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Home,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "home-single" },
                        DoubleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "home-double" }
                    }
                ]
            };
            config.Current.Devices.Add(device);
            config.Current.SelectedDeviceId = device.Id;

            using var vm = new MainViewModel(config, bt, a => executed.Add(a.Text));
            vm.HandleDeviceButton(addr, RemoteButton.Home, true, MainViewModel.InputSource.Direct);
            vm.HandleDeviceButton(addr, RemoteButton.Home, false, MainViewModel.InputSource.Direct);
            vm.HandleDeviceButton(addr, RemoteButton.Home, true, MainViewModel.InputSource.Direct);
            vm.HandleDeviceButton(addr, RemoteButton.Home, false, MainViewModel.InputSource.Direct);

            Assert.Equal(["home-double"], executed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AudioOutputFailed_RaisesWarningBanner_AndVoiceSessionStartedClearsIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"banner-audio-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true, ConnectedDeviceAddress = 0x112233445566 };
            bt.ReadyAddresses.Add(bt.ConnectedDeviceAddress);
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

            Assert.False(vm.HasWarningBanner);
            bt.RaiseAudioOutputFailed(bt.ConnectedDeviceAddress, "CABLE Input unavailable");

            Assert.True(vm.HasWarningBanner);
            Assert.Contains("CABLE Input unavailable", vm.WarningBannerMessage);
            Assert.Equal("查看驱动", vm.WarningBannerActionText);

            bt.RaiseVoiceSessionStarted(bt.ConnectedDeviceAddress);
            Assert.False(vm.HasWarningBanner);
            Assert.Equal(string.Empty, vm.WarningBannerMessage);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void EtwStatusChanges_UpdateSidebarEnvironmentAndWarningBanner_AndRunningClearsBanner()
    {
        string path = Path.Combine(Path.GetTempPath(), $"banner-etw-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager();
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

            Assert.False(vm.HasWarningBanner);
            Assert.Equal(EtwCaptureStatus.Idle, vm.EtwCaptureStatus);

            vm.SimulateEtwStatusForTest(EtwCaptureStatus.ElevationDeclined);
            Assert.True(vm.HasWarningBanner);
            Assert.Equal("重新提权", vm.WarningBannerActionText);
            Assert.Equal(EtwCaptureStatus.ElevationDeclined, vm.EnvironmentVm.EtwStatus);
            Assert.True(vm.EnvironmentVm.CanRetryEtwCapture);

            vm.SimulateEtwStatusForTest(EtwCaptureStatus.Disconnected);
            Assert.True(vm.HasWarningBanner);
            Assert.Equal("重新启动", vm.WarningBannerActionText);

            vm.SimulateEtwStatusForTest(EtwCaptureStatus.Failed);
            Assert.True(vm.HasWarningBanner);
            Assert.Equal("重试启动", vm.WarningBannerActionText);

            vm.SimulateEtwStatusForTest(EtwCaptureStatus.RunningElevatedWorker);
            Assert.False(vm.HasWarningBanner);
            Assert.Equal(EtwCaptureStatus.RunningElevatedWorker, vm.EtwCaptureStatus);
            Assert.False(vm.EnvironmentVm.CanRetryEtwCapture);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DevicesVmRemoveDevice_SynchronizesMappingViewModelViaDevicesChanged()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sync-rm-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var dev1 = new RemoteDevice { Id = "dev1", Name = "First", BluetoothAddress = 0x1111, ModelType = RemoteModelType.XiaomiRC003 };
            var dev2 = new RemoteDevice { Id = "dev2", Name = "Second", BluetoothAddress = 0x2222, ModelType = RemoteModelType.GoogleG9N9N };
            config.Current.Devices.AddRange([dev1, dev2]);
            config.Current.SelectedDeviceId = dev1.Id;

            var bt = new TestBluetoothManager();
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };
            Assert.Equal(2, vm.MappingVm.Devices.Count);
            Assert.Equal("dev1", vm.MappingVm.SelectedDevice?.Id);

            vm.DevicesVm.RemoveDevice(dev1);

            Assert.Single(vm.MappingVm.Devices);
            Assert.Equal("dev2", vm.MappingVm.SelectedDevice?.Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MappingPageActiveForeground_XiaomiHomePress_ShowsFeedbackWithoutExecutingWinTab()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mapping-suppress-active-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var xiaomi = new RemoteDevice
            {
                Id = "xiaomi_rc003",
                BluetoothAddress = 0xC05D39F86E0C,
                Name = "MI RC",
                ModelType = RemoteModelType.XiaomiRC003,
                Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.XiaomiRC003)
            };
            config.Current.Devices.Add(xiaomi);
            config.Current.SelectedDeviceId = xiaomi.Id;

            var bt = new TestBluetoothManager();
            bt.ReadyAddresses.Add(xiaomi.BluetoothAddress);

            var executed = new List<ButtonAction>();
            using var vm = new MainViewModel(config, bt, action => executed.Add(action.Action));
            vm.IsMainWindowActive = true;
            vm.Navigate("Mapping");
            vm.MappingVm.IsMappingSelectionLocked = false;

            var animatedButtons = new List<RemoteButton>();
            vm.MappingVm.HardwareButtonPressed += button => animatedButtons.Add(button);

            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Home, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Home, false);

            Assert.Equal([RemoteButton.Home], animatedButtons);
            Assert.Equal(RemoteButton.Home, vm.MappingVm.SelectedMapping?.Button);
            Assert.Empty(executed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MappingPageWindowInactive_XiaomiHomePress_ExecutesMappedShortcut()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mapping-suppress-inactive-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var xiaomi = new RemoteDevice
            {
                Id = "xiaomi_rc003",
                BluetoothAddress = 0xC05D39F86E0C,
                Name = "MI RC",
                ModelType = RemoteModelType.XiaomiRC003,
                Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.XiaomiRC003)
            };
            config.Current.Devices.Add(xiaomi);
            config.Current.SelectedDeviceId = xiaomi.Id;

            var bt = new TestBluetoothManager();
            bt.ReadyAddresses.Add(xiaomi.BluetoothAddress);

            var executed = new List<ButtonAction>();
            using var vm = new MainViewModel(config, bt, action => executed.Add(action.Action));
            vm.IsMainWindowActive = false;
            vm.Navigate("Mapping");

            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Home, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Home, false);

            Assert.Equal([ButtonAction.TaskView], executed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void GeneralPageActiveForeground_XiaomiHomePress_ExecutesMappedShortcut()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mapping-suppress-general-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var xiaomi = new RemoteDevice
            {
                Id = "xiaomi_rc003",
                BluetoothAddress = 0xC05D39F86E0C,
                Name = "MI RC",
                ModelType = RemoteModelType.XiaomiRC003,
                Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.XiaomiRC003)
            };
            config.Current.Devices.Add(xiaomi);
            config.Current.SelectedDeviceId = xiaomi.Id;

            var bt = new TestBluetoothManager();
            bt.ReadyAddresses.Add(xiaomi.BluetoothAddress);

            var executed = new List<ButtonAction>();
            using var vm = new MainViewModel(config, bt, action => executed.Add(action.Action));
            vm.IsMainWindowActive = true;
            vm.Navigate("General");

            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Home, true);
            vm.HandleDeviceButton(xiaomi.BluetoothAddress, RemoteButton.Home, false);

            Assert.Equal([ButtonAction.TaskView], executed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CommonPhraseOverlay_ClaimsButtonsAndVoiceKeyDismissesWithoutStartingVoice()
    {
        string path = Path.Combine(Path.GetTempPath(), $"phrase-overlay-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0x4754514C6FCC;
            bt.ReadyAddresses.Add(addr);
            var g9 = new RemoteDevice
            {
                Id = "g9",
                BluetoothAddress = addr,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Back,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "leaked-back" }
                    }
                ]
            };
            config.Current.Devices.Add(g9);
            config.Current.SelectedDeviceId = g9.Id;

            var executed = new List<string?>();
            int overlayOpened = 0;
            int overlayClosed = 0;
            using var vm = new MainViewModel(config, bt, a => executed.Add(a.Text))
            {
                CommonPhraseInserterForTest = new FakeCommonPhraseInserter()
            };
            vm.RequestToggleCommonPhraseOverlay += (_, _) => overlayOpened++;
            vm.RequestCloseCommonPhraseOverlay += () => overlayClosed++;

            vm.ToggleCommonPhraseOverlay(addr);
            Assert.Equal(1, overlayOpened);

            // Pressing Voice dismisses the overlay without starting a voice session (including its paired release)
            vm.HandleDeviceButton(addr, RemoteButton.Voice, true);
            vm.HandleDeviceButton(addr, RemoteButton.Voice, false);
            Assert.Equal(1, overlayClosed);
            Assert.Equal(0, bt.StartVoiceSessionCount);
            Assert.Equal(0, bt.StopVoiceSessionCount);

            // Re-open overlay and press Back: must close overlay without leaking Back's mapped action
            vm.ToggleCommonPhraseOverlay(addr);
            vm.HandleDeviceButton(addr, RemoteButton.Back, true);
            vm.HandleDeviceButton(addr, RemoteButton.Back, false);
            Assert.Equal(2, overlayClosed);
            Assert.Empty(executed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string phrasesPath = CommonPhraseStore.ResolveDefaultPathForConfigDirectory(path);
            if (File.Exists(phrasesPath)) File.Delete(phrasesPath);
        }
    }

    [Fact]
    public void CommonPhraseOverlay_InsertingSameButtonTwice_DoesNotSwallowSecondPress()
    {
        string path = Path.Combine(Path.GetTempPath(), $"phrase-repeat-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0x4754514C6FCC;
            bt.ReadyAddresses.Add(addr);
            config.Current.Devices.Add(new RemoteDevice
            {
                Id = "g9",
                BluetoothAddress = addr,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings =
                [
                    new ButtonMapping
                    {
                        Button = RemoteButton.Up,
                        SingleClick = new ConfiguredButtonAction { Action = ButtonAction.TextSnippet, Text = "leaked-up" }
                    }
                ]
            });

            var inserter = new FakeCommonPhraseInserter();
            var leaked = new List<string?>();
            int overlayOpened = 0;
            int overlayClosed = 0;
            using var vm = new MainViewModel(config, bt, a => leaked.Add(a.Text))
            {
                CommonPhraseInserterForTest = inserter
            };
            vm.RequestToggleCommonPhraseOverlay += (_, _) => overlayOpened++;
            vm.RequestCloseCommonPhraseOverlay += () => overlayClosed++;

            // 1. Open overlay, press Up (closes immediately on keyDown & inserts), then release Up
            vm.ToggleCommonPhraseOverlay(addr);
            vm.HandleDeviceButton(addr, RemoteButton.Up, true);
            vm.HandleDeviceButton(addr, RemoteButton.Up, false);
            Assert.Equal(1, overlayClosed);
            Assert.Empty(leaked);

            // 2. Re-open overlay and press Up again: first press must insert immediately
            vm.ToggleCommonPhraseOverlay(addr);
            vm.HandleDeviceButton(addr, RemoteButton.Up, true);
            vm.HandleDeviceButton(addr, RemoteButton.Up, false);

            Assert.Equal(2, overlayOpened);
            Assert.Equal(2, overlayClosed);
            Assert.Equal(["解释清楚一点", "解释清楚一点"], inserter.InsertedTexts);
            Assert.Empty(leaked);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string phrasesPath = CommonPhraseStore.ResolveDefaultPathForConfigDirectory(path);
            if (File.Exists(phrasesPath)) File.Delete(phrasesPath);
        }
    }

    [Fact]
    public void CommonPhraseOverlay_MultiLinePhrase_InsertsFullTextWithoutSplittingEnter()
    {
        string path = Path.Combine(Path.GetTempPath(), $"phrase-multiline-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0x4754514C6FCC;
            bt.ReadyAddresses.Add(addr);
            config.Current.Devices.Add(new RemoteDevice
            {
                Id = "g9",
                BluetoothAddress = addr,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N
            });

            var inserter = new FakeCommonPhraseInserter();
            using var vm = new MainViewModel(config, bt)
            {
                CommonPhraseInserterForTest = inserter
            };

            string multiLineId = vm.CommonPhraseStore.SavePhrase(
                new CommonPhrase { Label = "两行", Text = "第一行\n第二行" },
                null);
            vm.CommonPhraseStore.Assign(RemoteButton.Up, multiLineId);

            vm.ToggleCommonPhraseOverlay(addr);
            vm.HandleDeviceButton(addr, RemoteButton.Up, true);
            vm.HandleDeviceButton(addr, RemoteButton.Up, false);

            Assert.Equal(["第一行\n第二行"], inserter.InsertedTexts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string phrasesPath = CommonPhraseStore.ResolveDefaultPathForConfigDirectory(path);
            if (File.Exists(phrasesPath)) File.Delete(phrasesPath);
        }
    }

    [Fact]
    public void CommonPhraseOverlay_TargetWindowChanges_ClosesOverlayAndCancelsInsertion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"phrase-target-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0x4754514C6FCC;
            bt.ReadyAddresses.Add(addr);
            config.Current.Devices.Add(new RemoteDevice
            {
                Id = "g9",
                BluetoothAddress = addr,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N
            });

            var inserter = new FakeCommonPhraseInserter();
            int overlayOpened = 0;
            int overlayClosed = 0;
            using var vm = new MainViewModel(config, bt)
            {
                CommonPhraseInserterForTest = inserter
            };
            vm.RequestToggleCommonPhraseOverlay += (_, _) => overlayOpened++;
            vm.RequestCloseCommonPhraseOverlay += () => overlayClosed++;

            // With no valid external target, opening overlay is skipped
            inserter.CapturedTarget = null;
            vm.ToggleCommonPhraseOverlay(addr);
            Assert.Equal(0, overlayOpened);

            // Open targeting editor window A, then switch foreground to chat window B before pressing phrase key
            inserter.CapturedTarget = new CommonPhraseTarget(new nint(0x1111), 101, new nint(0x2222), "1");
            inserter.TargetActive = true;
            vm.ToggleCommonPhraseOverlay(addr);
            Assert.Equal(1, overlayOpened);

            inserter.TargetActive = false;
            vm.HandleDeviceButton(addr, RemoteButton.Up, true);
            vm.HandleDeviceButton(addr, RemoteButton.Up, false);

            Assert.Equal(1, overlayClosed);
            Assert.Empty(inserter.InsertedTexts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string phrasesPath = CommonPhraseStore.ResolveDefaultPathForConfigDirectory(path);
            if (File.Exists(phrasesPath)) File.Delete(phrasesPath);
        }
    }

    [Fact]
    public void CommonPhraseOverlay_XiaomiRemote_DoesNotClaimNativeOkOrArrows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"phrase-xiaomi-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            const ulong addr = 0xC05D39F86E0C;
            bt.ReadyAddresses.Add(addr);
            config.Current.Devices.Add(new RemoteDevice
            {
                Id = "mi",
                BluetoothAddress = addr,
                Name = "MI RC",
                ModelType = RemoteModelType.XiaomiRC003
            });

            var inserter = new FakeCommonPhraseInserter();
            int overlayClosed = 0;
            using var vm = new MainViewModel(config, bt)
            {
                CommonPhraseInserterForTest = inserter
            };
            vm.RequestCloseCommonPhraseOverlay += () => overlayClosed++;

            vm.ToggleCommonPhraseOverlay(addr);

            // OK emits native Windows Enter on Xiaomi; overlay must leave it alone
            vm.HandleDeviceButton(addr, RemoteButton.Ok, true);
            vm.HandleDeviceButton(addr, RemoteButton.Ok, false);
            Assert.Equal(0, overlayClosed);
            Assert.Empty(inserter.InsertedTexts);

            // Extra non-keyboard button (Menu) inserts normally and closes overlay
            vm.HandleDeviceButton(addr, RemoteButton.Menu, true);
            vm.HandleDeviceButton(addr, RemoteButton.Menu, false);
            Assert.Equal(1, overlayClosed);
            Assert.Equal(["总结一下"], inserter.InsertedTexts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string phrasesPath = CommonPhraseStore.ResolveDefaultPathForConfigDirectory(path);
            if (File.Exists(phrasesPath)) File.Delete(phrasesPath);
        }
    }

    [Fact]
    public void RefreshInputSources_PreservesG9N9NCachedFilterWhenG9N9NSleepsWithActiveXiaomiRemote()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dual-remote-etw-filter-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var xiaomi = new RemoteDevice
            {
                Id = "xiaomi_rc003",
                BluetoothAddress = 0xC05D39F86E0C,
                Name = "MI RC",
                ModelType = RemoteModelType.XiaomiRC003,
                Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.XiaomiRC003)
            };
            var g9n9n = new RemoteDevice
            {
                Id = "g9n9n",
                BluetoothAddress = 0x4754514C6FCC,
                Name = "Chromecast Remote",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleG9N9N)
            };
            config.Current.Devices.Add(xiaomi);
            config.Current.Devices.Add(g9n9n);
            config.Current.SelectedDeviceId = xiaomi.Id;

            var bt = new TestBluetoothManager { IsConnected = true, IsReady = true };
            bt.ReadyAddresses.Add(xiaomi.BluetoothAddress);
            bt.ReadyAddresses.Add(g9n9n.BluetoothAddress);
            ushort[] excluded = [0x0038, 0x0039, 0x003A, 0x003B, 0x003C, 0x003D, 0x003E, 0x003F, 0x0040];
            bt.EtwFilters[g9n9n.BluetoothAddress] = new EtwAttributeFilter(0x001F, 0x002B, excluded);

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };
            vm.RefreshInputSources();
            var initialSources = vm.EtwSourcesForTest;
            var initialG9 = Assert.Single(initialSources, s => s.Address == g9n9n.BluetoothAddress);
            Assert.Equal((ushort)0x001F, initialG9.HidHandleStart);
            Assert.Equal((ushort)0x002B, initialG9.HidHandleEnd);
            Assert.NotNull(initialG9.ExcludedAttributes);
            Assert.Equal(excluded, initialG9.ExcludedAttributes);

            // G9N9N goes to sleep / disconnects while Xiaomi stays ready; live BT filter becomes default
            bt.ReadyAddresses.Remove(g9n9n.BluetoothAddress);
            bt.EtwFilters.Remove(g9n9n.BluetoothAddress);
            vm.RefreshInputSources();
            var afterSleepSources = vm.EtwSourcesForTest;

            Assert.True(MainViewModel.EtwSourcesMatch(initialSources, afterSleepSources));
            var preservedG9 = Assert.Single(afterSleepSources, s => s.Address == g9n9n.BluetoothAddress);
            Assert.Equal((ushort)0x001F, preservedG9.HidHandleStart);
            Assert.Equal((ushort)0x002B, preservedG9.HidHandleEnd);
            Assert.NotNull(preservedG9.ExcludedAttributes);
            Assert.Equal(excluded, preservedG9.ExcludedAttributes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void GeneralAndSettingsViewModels_DoNotWriteConfigOnConstruction_AndStayInSync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vm-sync-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var bt = new TestBluetoothManager();
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };

            Assert.False(File.Exists(path));
            bool initialTray = vm.GeneralVm.MinimizeToTrayOnClose;
            Assert.Equal(initialTray, vm.SettingsVm.MinimizeToTrayOnClose);

            vm.GeneralVm.MinimizeToTrayOnClose = !initialTray;
            Assert.Equal(!initialTray, vm.SettingsVm.MinimizeToTrayOnClose);
            Assert.Equal(!initialTray, config.Current.MinimizeToTrayOnClose);

            vm.SettingsVm.MinimizeToTrayOnClose = initialTray;
            Assert.Equal(initialTray, vm.GeneralVm.MinimizeToTrayOnClose);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(@"USB\VID_8087&PID_0032\5&1A2B3C4D&0&14", true)]
    [InlineData(@"PCI\VEN_8086&DEV_51F0&SUBSYS_00908086&REV_01\3&11583659&0&A3", true)]
    [InlineData(@"USB\VID_0A12&PID_0001\'; Restart-Computer #", false)]
    [InlineData(@"USB\VID_0A12&PID_0001\$(calc)", false)]
    [InlineData(@"USB\VID_0A12\", false)]
    [InlineData("", false)]
    public void BluetoothAdapterHelper_IsValidPnpInstanceId_FiltersMetacharacters(string instanceId, bool expected)
    {
        Assert.Equal(expected, RemoteBuddy.Platform.Windows.Bluetooth.BluetoothAdapterHelper.IsValidPnpInstanceId(instanceId));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Remote Buddy\\RemoteBuddy.App.exe\" --minimized", @"C:\Program Files\Remote Buddy\RemoteBuddy.App.exe")]
    [InlineData("C:\\Tools\\RemoteBuddy.App.exe --minimized", @"C:\Tools\RemoteBuddy.App.exe")]
    [InlineData("C:\\Program Files\\Remote Buddy\\RemoteBuddy.App.exe --minimized", @"C:\Program Files\Remote Buddy\RemoteBuddy.App.exe")]
    [InlineData("   ", null)]
    public void StartupService_ExtractExecutablePath_ParsesQuotedAndUnquotedPaths(string regValue, string? expectedPath)
    {
        Assert.Equal(expectedPath, RemoteBuddy.Platform.Windows.Services.StartupService.ExtractExecutablePath(regValue));
    }

    [Fact]
    public void MainViewModel_UsesOnboardingVerificationTriggerMode_WithoutOverwritingSavedConfig()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-onb-temp-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            config.Update(cfg =>
            {
                cfg.InputTool.Tool = TargetInputTool.WeChatInput;
                cfg.InputTool.TriggerMode = VoiceTriggerMode.HoldToTalk;
                cfg.InputTool.Shortcut = "Ctrl+F5";
                cfg.Devices.Add(new RemoteDevice { Id = "dev1", BluetoothAddress = 0x112233445566, Name = "Xiaomi Remote" });
                cfg.SelectedDeviceId = "dev1";
            });

            var bt = new TestBluetoothManager
            {
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = 0x112233445566
            };

            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = new FakeUsbKeyboardSender()
            };
            var dev = config.Current.Devices[0];
            using var onboarding = new OnboardingViewModel(config, bt, WizardMode.Integration)
            {
                SelectedDevice = dev
            };
            onboarding.ApplyPreset("WindowsDictation"); // ClickToToggle + Win+H
            onboarding.NextStep();
            Assert.Equal(WizardStep.TranscriptionVerify, onboarding.CurrentWizardStep);

            vm.ActiveOnboardingVm = onboarding;

            // Raw voice release must NOT stop voice session when staged verification mode is ClickToToggle
            vm.HandleRawButtonEvent(IntPtr.Zero, RemoteButton.Voice, true);
            vm.HandleRawButtonEvent(IntPtr.Zero, RemoteButton.Voice, false);
            Assert.Equal(1, bt.StartVoiceSessionCount);
            Assert.Equal(0, bt.StopVoiceSessionCount);

            // Saved config remains untouched before completion
            Assert.Equal(TargetInputTool.WeChatInput, config.Current.InputTool.Tool);
            Assert.Equal(VoiceTriggerMode.HoldToTalk, config.Current.InputTool.TriggerMode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(TargetInputTool.WeChatInput, VoiceTriggerMode.HoldToTalk, "Ctrl+F5")]
    [InlineData(TargetInputTool.DoubaoInput, VoiceTriggerMode.HoldToTalk, "RAlt")]
    [InlineData(TargetInputTool.Chatterfly, VoiceTriggerMode.HoldToTalk, "Ctrl+Shift+V")]
    [InlineData(TargetInputTool.Typeless, VoiceTriggerMode.HoldToTalk, "Ctrl+Alt+V")]
    [InlineData(TargetInputTool.WindowsDictation, VoiceTriggerMode.ClickToToggle, "Win+H")]
    [InlineData(TargetInputTool.Custom, VoiceTriggerMode.HoldToTalk, "Ctrl+Alt+F9")]
    public void VoiceSession_AllInputTools_SendAndReleaseViaUsbIpKeyboard(
        TargetInputTool tool,
        VoiceTriggerMode triggerMode,
        string shortcut)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-usbip-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            config.Update(cfg =>
            {
                cfg.InputTool.Tool = tool;
                cfg.InputTool.TriggerMode = triggerMode;
                cfg.InputTool.Shortcut = shortcut;
            });

            var bt = new TestBluetoothManager
            {
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = 0x112233445566
            };
            bt.ReadyAddresses.Add(bt.ConnectedDeviceAddress);

            var fakeUsb = new FakeUsbKeyboardSender();
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => true,
                UsbKeyboardForTest = fakeUsb
            };

            bt.RaiseVoiceSessionStarted(bt.ConnectedDeviceAddress);
            bt.RaiseVoiceSessionEnded(bt.ConnectedDeviceAddress);

            if (triggerMode == VoiceTriggerMode.HoldToTalk)
            {
                Assert.Equal([shortcut], fakeUsb.DownCalls);
                Assert.Equal(1, fakeUsb.ReleaseCalls);
                Assert.Empty(fakeUsb.ClickCalls);
            }
            else
            {
                Assert.Equal([shortcut, shortcut], fakeUsb.ClickCalls);
                Assert.Empty(fakeUsb.DownCalls);
                Assert.Equal(0, fakeUsb.ReleaseCalls);
            }
            Assert.False(vm.HasWarningBanner);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void VoiceSession_WithoutAdministrator_ShowsUsbIpElevationBanner()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-usbip-noadmin-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            config.Update(cfg =>
            {
                cfg.InputTool.Tool = TargetInputTool.Chatterfly;
                cfg.InputTool.TriggerMode = VoiceTriggerMode.HoldToTalk;
                cfg.InputTool.Shortcut = "Ctrl+Shift+V";
            });

            var bt = new TestBluetoothManager
            {
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = 0x112233445566
            };
            bt.ReadyAddresses.Add(bt.ConnectedDeviceAddress);

            var fakeUsb = new FakeUsbKeyboardSender();
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => false,
                UsbKeyboardForTest = fakeUsb
            };

            bt.RaiseVoiceSessionStarted(bt.ConnectedDeviceAddress);
            bt.RaiseVoiceSessionEnded(bt.ConnectedDeviceAddress);

            Assert.Empty(fakeUsb.DownCalls);
            Assert.Equal(0, fakeUsb.ReleaseCalls);
            Assert.True(vm.HasWarningBanner);
            Assert.Contains("管理员权限", vm.WarningBannerMessage);
            Assert.Equal("以管理员身份重启", vm.WarningBannerActionText);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OnboardingVerification_WhenUsbIpShortcutFails_BlocksProgressionAndShowsError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vib-onb-fail-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var dev = new RemoteDevice { Id = "dev1", BluetoothAddress = 0x112233445566, Name = "Xiaomi Remote" };
            config.Current.Devices.Add(dev);
            config.Current.SelectedDeviceId = dev.Id;

            var bt = new TestBluetoothManager
            {
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = dev.BluetoothAddress
            };
            bt.ReadyAddresses.Add(dev.BluetoothAddress);

            var fakeUsb = new FakeUsbKeyboardSender();
            using var vm = new MainViewModel(config, bt)
            {
                IsAdministratorForTest = () => false,
                UsbKeyboardForTest = fakeUsb
            };
            using var onboarding = new OnboardingViewModel(config, bt, WizardMode.Integration)
            {
                SelectedDevice = dev
            };
            onboarding.ApplyPreset("Chatterfly");
            onboarding.NextStep();
            Assert.Equal(WizardStep.TranscriptionVerify, onboarding.CurrentWizardStep);

            vm.ActiveOnboardingVm = onboarding;
            bt.RaiseVoiceSessionStarted(dev.BluetoothAddress);
            bt.RaiseVoiceSessionEnded(dev.BluetoothAddress);

            Assert.Contains("管理员权限", onboarding.ValidationError);

            onboarding.TranscriptionTestText = "手动输入";
            onboarding.IsTranscriptionVerified = true;
            onboarding.NextStep();

            Assert.Equal(WizardStep.TranscriptionVerify, onboarding.CurrentWizardStep);
            Assert.Contains("管理员权限", onboarding.ValidationError);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
