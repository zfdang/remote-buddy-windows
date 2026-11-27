// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Specialized;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

public class MappingDeviceTests
{
    [Fact]
    public void XiaomiAndGoogleDefaults_AlignWithMacOSButtonMappings()
    {
        var rc003 = RemoteDevice.CreateDefaultMappings(RemoteModelType.XiaomiRC003);
        Assert.Equal(12, rc003.Count);
        Assert.Equal(ButtonAction.DeleteBackward, rc003.Single(m => m.Button == RemoteButton.Power).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.RepeatSingleClick, rc003.Single(m => m.Button == RemoteButton.Power).GetConfiguredAction(ButtonTrigger.LongPress).Action);
        // Xiaomi OK and directional keys keep Windows native keyboard behavior (no overlapping Enter / Ctrl+Enter actions)
        foreach (var nativeBtn in new[] { RemoteButton.Ok, RemoteButton.Up, RemoteButton.Down, RemoteButton.Left, RemoteButton.Right })
        {
            var mapping = rc003.Single(m => m.Button == nativeBtn);
            Assert.All(Enum.GetValues<ButtonTrigger>(), t =>
                Assert.Equal(ButtonAction.Disabled, mapping.GetConfiguredAction(t).Action));
        }
        Assert.Equal(ButtonAction.Escape, rc003.Single(m => m.Button == RemoteButton.Back).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.TaskView, rc003.Single(m => m.Button == RemoteButton.Home).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.ContextMenu, rc003.Single(m => m.Button == RemoteButton.Menu).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.PlayPause, rc003.Single(m => m.Button == RemoteButton.LiveTv).GetConfiguredAction(ButtonTrigger.SingleClick).Action);

        var g9n9n = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleG9N9N);
        Assert.Equal(14, g9n9n.Count);
        Assert.Equal(ButtonAction.PreviousTrack, g9n9n.Single(m => m.Button == RemoteButton.YouTube).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.NextTrack, g9n9n.Single(m => m.Button == RemoteButton.Netflix).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.PlayPause, g9n9n.Single(m => m.Button == RemoteButton.Input).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.VolumeMute, g9n9n.Single(m => m.Button == RemoteButton.Mute).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.CommandReturn, g9n9n.Single(m => m.Button == RemoteButton.Ok).GetConfiguredAction(ButtonTrigger.LongPress).Action);

        var gy3le = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleGY3LE);
        Assert.Equal(14, gy3le.Count);
        Assert.Equal(ButtonAction.Disabled, gy3le.Single(m => m.Button == RemoteButton.Custom).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
    }

    [Theory]
    [InlineData(RemoteModelType.GoogleG9N9N)]
    [InlineData(RemoteModelType.GoogleGY3LE)]
    public void GoogleDefaults_RepeatDirectionsVolumeAndPower_WhileOkLongPressSendsCommandReturn(RemoteModelType model)
    {
        var mappings = RemoteDevice.CreateDefaultMappings(model);
        var repeatButtons = mappings
            .Where(m => m.GetConfiguredAction(ButtonTrigger.LongPress).Action == ButtonAction.RepeatSingleClick)
            .Select(m => m.Button)
            .Order()
            .ToArray();

        RemoteButton[] expected =
        [
            RemoteButton.Up, RemoteButton.Down, RemoteButton.Left, RemoteButton.Right,
            RemoteButton.Power, RemoteButton.VolumeUp, RemoteButton.VolumeDown
        ];
        Assert.Equal(expected.Order().ToArray(), repeatButtons);
        Assert.Equal(ButtonAction.CommandReturn, mappings.Single(m => m.Button == RemoteButton.Ok).GetConfiguredAction(ButtonTrigger.LongPress).Action);
    }

    [Theory]
    [InlineData(RemoteModelType.Unknown)]
    [InlineData(RemoteModelType.XiaomiRC001)]
    [InlineData(RemoteModelType.XiaomiRC003)]
    [InlineData(RemoteModelType.XiaomiARN9)]
    public void XiaomiDefaults_RepeatOnlyPowerAndVolume_WhileOkAndArrowsStayDisabled(RemoteModelType model)
    {
        var mappings = RemoteDevice.CreateDefaultMappings(model);
        var repeatButtons = mappings
            .Where(m => m.GetConfiguredAction(ButtonTrigger.LongPress).Action == ButtonAction.RepeatSingleClick)
            .Select(m => m.Button)
            .Order()
            .ToArray();

        RemoteButton[] expected = [RemoteButton.Power, RemoteButton.VolumeUp, RemoteButton.VolumeDown];
        Assert.Equal(expected.Order().ToArray(), repeatButtons);
        Assert.Equal(ButtonAction.Disabled, mappings.Single(m => m.Button == RemoteButton.Ok).GetConfiguredAction(ButtonTrigger.LongPress).Action);
    }

    [Fact]
    public void T1RemoteDefaults_CreateExpectedFrontButtons_WithoutInterceptingKeyboard()
    {
        var mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.T1Remote);
        Assert.Equal(7, mappings.Count);

        Assert.Equal(ButtonAction.DeleteBackward, mappings.Single(m => m.Button == RemoteButton.Power).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.Escape, mappings.Single(m => m.Button == RemoteButton.Back).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.TaskView, mappings.Single(m => m.Button == RemoteButton.Home).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.ContextMenu, mappings.Single(m => m.Button == RemoteButton.Menu).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.VolumeUp, mappings.Single(m => m.Button == RemoteButton.VolumeUp).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.VolumeDown, mappings.Single(m => m.Button == RemoteButton.VolumeDown).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.VolumeMute, mappings.Single(m => m.Button == RemoteButton.Mute).GetConfiguredAction(ButtonTrigger.SingleClick).Action);

        Assert.DoesNotContain(mappings, m => m.Button is RemoteButton.Ok or RemoteButton.Up or RemoteButton.Down or RemoteButton.Left or RemoteButton.Right);
    }

    [Fact]
    public void RemoteDevice_ModelFlagsAndVoiceButtonMode_WorkAsExpected()
    {
        var t1 = new RemoteDevice { ModelType = RemoteModelType.T1Remote };
        Assert.True(t1.IsT1);
        Assert.False(t1.IsG9N9N);
        Assert.False(t1.IsXiaomi);

        var g9n9n = new RemoteDevice { ModelType = RemoteModelType.GoogleG9N9N };
        Assert.False(g9n9n.IsT1);
        Assert.True(g9n9n.IsG9N9N);
        Assert.False(g9n9n.IsXiaomi);
        Assert.Equal("/Resources/ChromecastG9N9NPhoto.png", g9n9n.IllustrationSource);

        var gy3le = new RemoteDevice { ModelType = RemoteModelType.GoogleGY3LE };
        Assert.Equal("/Resources/GoogleStreamerGY3LEIllustration.png", gy3le.IllustrationSource);

        Assert.Equal(G9N9NVoiceButtonMode.SmartToggle, g9n9n.VoiceButtonMode);
        Assert.True(g9n9n.IsSmartToggleMode);
        g9n9n.IsHoldToTalkMode = true;
        Assert.Equal(G9N9NVoiceButtonMode.HoldToTalk, g9n9n.VoiceButtonMode);
    }

    [Fact]
    public void MappingGestureEditorsSaveIndependentActionsAndClearAllThree()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"rb-gestures-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(configPath);
            var device = new RemoteDevice
            {
                Id = "g9n9n",
                ModelType = RemoteModelType.GoogleG9N9N,
                Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleG9N9N)
            };
            config.Current.Devices.Add(device);
            config.Current.SelectedDeviceId = device.Id;

            var vm = new MappingViewModel(config);
            vm.SelectButton("Up");
            var up = vm.SelectedMapping!;
            up.SingleClick.Action = ButtonAction.CustomShortcut;
            up.SingleClick.Shortcut = "Ctrl+C";
            up.LongPress.Action = ButtonAction.VolumeMute;
            up.DoubleClick.Action = ButtonAction.TextSnippet;
            up.DoubleClick.Text = "你好";

            var reloaded = new ConfigService(configPath).Current.Devices.Single().Mappings.Single(m => m.Button == RemoteButton.Up);
            Assert.Equal(ButtonAction.CustomShortcut, reloaded.GetConfiguredAction(ButtonTrigger.SingleClick).Action);
            Assert.Equal("Ctrl+C", reloaded.GetConfiguredAction(ButtonTrigger.SingleClick).Shortcut);
            Assert.Equal(ButtonAction.VolumeMute, reloaded.GetConfiguredAction(ButtonTrigger.LongPress).Action);
            Assert.Equal("你好", reloaded.GetConfiguredAction(ButtonTrigger.DoubleClick).Text);

            vm.ClearSelectedMapping();
            reloaded = new ConfigService(configPath).Current.Devices.Single().Mappings.Single(m => m.Button == RemoteButton.Up);
            Assert.All(Enum.GetValues<ButtonTrigger>(), trigger =>
                Assert.Equal(ButtonAction.Disabled, reloaded.GetConfiguredAction(trigger).Action));
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public void EditingOneDevicePreservesOtherDeviceAndActiveSelection()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"vib-mapping-{Guid.NewGuid():N}.json"));
        var first = new RemoteDevice { Id = "first", ModelType = RemoteModelType.XiaomiRC003 };
        var second = new RemoteDevice { Id = "second", ModelType = RemoteModelType.XiaomiRC003 };
        config.Current.Devices.AddRange([first, second]);
        config.Current.SelectedDeviceId = first.Id;

        var vm = new MappingViewModel(config) { SelectedDevice = second };
        var home = vm.CurrentMappings.Single(m => m.Button == RemoteButton.Home);
        home.SingleClick.Action = ButtonAction.CustomShortcut;
        home.SingleClick.Shortcut = "F2";
        vm.SelectedDevice = first;
        vm.SelectedDevice = second;
        Assert.Equal("F2", vm.CurrentMappings.Single(m => m.Button == RemoteButton.Home).SingleClick.Shortcut);
        vm.SaveMappings();

        Assert.Equal("F2", second.Mappings.Single(m => m.Button == RemoteButton.Home).GetConfiguredAction(ButtonTrigger.SingleClick).Shortcut);
        Assert.Equal(ButtonAction.TaskView, first.Mappings.Single(m => m.Button == RemoteButton.Home).GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(first.Id, config.Current.SelectedDeviceId);
    }

    [Fact]
    public void MappingViewModel_XiaomiNativeNavigationButtons_AreLockedAgainstOverlappingActions()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"vib-xiaomi-lock-{Guid.NewGuid():N}.json"));
        var xiaomi = new RemoteDevice { Id = "mi", Name = "MI RC", ModelType = RemoteModelType.XiaomiRC003 };
        config.Current.Devices.Add(xiaomi);
        config.Current.SelectedDeviceId = xiaomi.Id;

        var vm = new MappingViewModel(config);
        Assert.Equal("原生 Enter", vm.GetActionSummary(RemoteButton.Ok, ButtonTrigger.SingleClick));
        Assert.Equal("-", vm.GetActionSummary(RemoteButton.Ok, ButtonTrigger.LongPress));
        Assert.Equal("原生 ↑", vm.GetActionSummary(RemoteButton.Up, ButtonTrigger.SingleClick));

        vm.BeginEdit(RemoteButton.Ok, ButtonTrigger.LongPress);
        Assert.True(vm.IsNativeKeyboardLocked);
        Assert.False(vm.ShowCategoryGrid);
        Assert.False(vm.CanClearEditingAction);
        Assert.False(vm.CanRestoreEditingDefault);

        vm.SelectAction(ButtonAction.CommandReturn);
        Assert.Equal(ButtonAction.Disabled, vm.GetConfiguredAction(RemoteButton.Ok, ButtonTrigger.LongPress).Action);
    }

    [Fact]
    public void MappingViewModel_InlineEditor_SupportsCategoriesRestoreClearUrlAndCustomApps()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rb-editor-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var dev = new RemoteDevice { Id = "g9", Name = "G9N9N", ModelType = RemoteModelType.GoogleG9N9N };
            config.Current.Devices.Add(dev);
            config.Current.SelectedDeviceId = dev.Id;

            var vm = new MappingViewModel(config);
            vm.SetInstalledApplicationsForTest(new HashSet<PresetApplication> { PresetApplication.RemoteBuddy, PresetApplication.Explorer });

            // Directional SingleClick exposes DirectPassthrough category
            vm.BeginEdit(RemoteButton.Right, ButtonTrigger.SingleClick);
            Assert.True(vm.IsEditorOpen);
            Assert.Contains(vm.VisibleCategories, c => c.Category == ButtonActionCategory.DirectPassthrough);
            vm.SelectAction(ButtonAction.DirectPassthrough);
            Assert.Equal("→ 直通", vm.GetActionSummary(RemoteButton.Right, ButtonTrigger.SingleClick));

            // Secondary triggers are locked while SingleClick is DirectPassthrough
            vm.BeginEdit(RemoteButton.Right, ButtonTrigger.LongPress);
            Assert.True(vm.IsDirectPassthroughLockActive);
            Assert.Equal("-", vm.GetActionSummary(RemoteButton.Right, ButtonTrigger.LongPress));

            // Restore SingleClick default restores ArrowRight
            vm.BeginEdit(RemoteButton.Right, ButtonTrigger.SingleClick);
            vm.RestoreDefaultAction();
            Assert.Equal(ButtonAction.ArrowRight, vm.GetConfiguredAction(RemoteButton.Right, ButtonTrigger.SingleClick).Action);

            // Open URL validation & persistence
            vm.BeginEdit(RemoteButton.YouTube, ButtonTrigger.SingleClick);
            vm.SelectCategory(ButtonActionCategory.Custom);
            vm.SelectAction(ButtonAction.OpenUrl);
            vm.UrlInput = "https://github.com/zfdang";
            Assert.True(vm.IsUrlValid);
            Assert.Equal("github.com", vm.GetActionSummary(RemoteButton.YouTube, ButtonTrigger.SingleClick));

            // Custom Application profile add + strategy + delete
            vm.SelectCategory(ButtonActionCategory.Applications);
            vm.SelectAction(ButtonAction.OpenCustomApplication);
            vm.AddCustomApplication(@"C:\Tools\MyEditor.exe");
            Assert.Single(vm.CustomApplications);
            vm.SetCustomAppFocusStrategy(CustomApplicationFocusStrategy.KeyboardShortcut);
            vm.SetCustomAppFocusShortcut("Ctrl+L");
            Assert.Equal("MyEditor", vm.GetActionSummary(RemoteButton.YouTube, ButtonTrigger.SingleClick));
            vm.DeleteCustomApplication(vm.CustomApplications[0].Id);
            Assert.Empty(vm.CustomApplications);

            // Clear editing action
            vm.ClearEditingAction();
            Assert.True(vm.IsEditingDisabled);
            Assert.Equal("未设置", vm.GetActionSummary(RemoteButton.YouTube, ButtonTrigger.SingleClick));

            // Reset all defaults
            vm.ResetToDefaults();
            Assert.False(vm.IsEditorOpen);
            Assert.Equal(ButtonAction.PreviousTrack, vm.GetConfiguredAction(RemoteButton.YouTube, ButtonTrigger.SingleClick).Action);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SelectingCustomShortcut_AndCancellingPicker_PreservesExistingMappingInMemoryAndDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rb-cancel-shortcut-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var dev = new RemoteDevice
            {
                Id = "mi",
                Name = "MI RC",
                ModelType = RemoteModelType.XiaomiRC003,
                Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.XiaomiRC003)
            };
            config.Current.Devices.Add(dev);
            config.Current.SelectedDeviceId = dev.Id;
            config.Save();

            var vm = new MappingViewModel(config);
            int pickerRequests = 0;
            vm.RequestOpenShortcutPicker += _ => pickerRequests++;

            vm.BeginEdit(RemoteButton.Back, ButtonTrigger.SingleClick);
            Assert.Equal(ButtonAction.Escape, vm.GetConfiguredAction(RemoteButton.Back, ButtonTrigger.SingleClick).Action);

            // Simulate clicking CustomShortcut and then cancelling the picker (ApplyCustomShortcut not called)
            vm.SelectAction(ButtonAction.CustomShortcut);
            Assert.Equal(1, pickerRequests);
            Assert.Equal(ButtonAction.Escape, vm.GetConfiguredAction(RemoteButton.Back, ButtonTrigger.SingleClick).Action);

            var reloaded = new ConfigService(path).Current.Devices.Single().Mappings
                .Single(m => m.Button == RemoteButton.Back).GetConfiguredAction(ButtonTrigger.SingleClick);
            Assert.Equal(ButtonAction.Escape, reloaded.Action);
            Assert.Null(reloaded.Shortcut);

            // Confirming a valid shortcut updates and persists CustomShortcut
            vm.ApplyCustomShortcut("Ctrl+K");
            var updated = new ConfigService(path).Current.Devices.Single().Mappings
                .Single(m => m.Button == RemoteButton.Back).GetConfiguredAction(ButtonTrigger.SingleClick);
            Assert.Equal(ButtonAction.CustomShortcut, updated.Action);
            Assert.Equal("Ctrl+K", updated.Shortcut);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MappingGesture_DisplaySummaryAndStatus_ReflectsConfiguration()
    {
        var mapping = new ButtonMapping { Button = RemoteButton.Up };
        var item = new MappingItemViewModel(mapping);

        Assert.Equal("未设置", item.SingleClickSummary);
        Assert.Equal("未设置", item.LongPressSummary);
        Assert.Equal("未设置", item.DoubleClickSummary);
        Assert.False(item.SingleClick.IsConfigured);

        item.SingleClick.Action = ButtonAction.CustomShortcut;
        item.SingleClick.Shortcut = "Ctrl+C";
        Assert.Equal("Ctrl+C", item.SingleClickSummary);
        Assert.True(item.SingleClick.IsConfigured);

        item.LongPress.Action = ButtonAction.RepeatSingleClick;
        Assert.Equal("持续连发", item.LongPressSummary);
        Assert.True(item.LongPress.IsConfigured);

        item.DoubleClick.Action = ButtonAction.PlayPause;
        Assert.Equal("播放 / 暂停", item.DoubleClickSummary);
        Assert.True(item.DoubleClick.IsConfigured);

        item.Clear();
        Assert.Equal("未设置", item.SingleClickSummary);
        Assert.Equal("未设置", item.LongPressSummary);
        Assert.Equal("未设置", item.DoubleClickSummary);
    }

    [Fact]
    public void InvalidShortcut_SetsValidationErrorWithoutPersisting_AndValidShortcutClearsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vib-val-shortcut-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var dev = new RemoteDevice { Id = "g9", Name = "G9N9N", ModelType = RemoteModelType.GoogleG9N9N };
            config.Current.Devices.Add(dev);
            config.Current.SelectedDeviceId = dev.Id;

            var vm = new MappingViewModel(config);
            vm.SelectButton("Home");
            var gesture = vm.SelectedMapping!.SingleClick;
            gesture.Action = ButtonAction.CustomShortcut;
            gesture.Shortcut = "Win+Tab";

            gesture.Shortcut = "NotAValidKey!!!";
            Assert.True(gesture.HasShortcutError);
            Assert.Equal("Win+Tab", dev.Mappings.Single(m => m.Button == RemoteButton.Home).GetConfiguredAction(ButtonTrigger.SingleClick).Shortcut);

            gesture.Shortcut = "Ctrl+,";
            Assert.False(gesture.HasShortcutError);
            Assert.Equal("Ctrl+,", dev.Mappings.Single(m => m.Button == RemoteButton.Home).GetConfiguredAction(ButtonTrigger.SingleClick).Shortcut);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ContinuousRepeatWithoutSingleClick_SetsWarning_WhileStayingConfigured()
    {
        var mapping = new ButtonMapping { Button = RemoteButton.Up };
        var item = new MappingItemViewModel(mapping);

        Assert.False(item.LongPress.ShowRepeatNeedsClickWarning);
        item.LongPress.Action = ButtonAction.RepeatSingleClick;

        Assert.True(item.LongPress.ShowRepeatNeedsClickWarning);
        Assert.True(item.LongPress.IsConfigured);
        Assert.Equal("持续连发", item.LongPressSummary);

        item.SingleClick.Action = ButtonAction.ArrowUp;
        Assert.False(item.LongPress.ShowRepeatNeedsClickWarning);
    }

    [Fact]
    public void EmptyConfig_SetsHasDevicesFalseAndIsXiaomiFalse()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"vib-empty-{Guid.NewGuid():N}.json"));
        var vm = new MappingViewModel(config);

        Assert.False(vm.HasDevices);
        Assert.Null(vm.SelectedDevice);
        Assert.False(vm.IsXiaomi);
        Assert.False(vm.IsG9N9N);
        Assert.False(vm.IsT1);
    }

    [Fact]
    public void Gy3leLayoutFlags_AndCustomButton_ConfiguredDistinctlyFromG9N9N()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"vib-gy3le-{Guid.NewGuid():N}.json"));
        var gy3le = new RemoteDevice { Id = "gy", Name = "Google TV Streamer", ModelType = RemoteModelType.GoogleGY3LE };
        config.Current.Devices.Add(gy3le);
        config.Current.SelectedDeviceId = gy3le.Id;

        Assert.True(gy3le.IsG9N9N);
        Assert.True(gy3le.IsGY3LELayout);
        Assert.False(gy3le.IsG9N9NLayout);

        var vm = new MappingViewModel(config);
        Assert.True(vm.IsG9N9N);
        Assert.True(vm.IsGY3LELayout);
        Assert.False(vm.IsG9N9NLayout);

        var custom = Assert.Single(vm.CurrentMappings, m => m.Button == RemoteButton.Custom);
        Assert.Equal("自定义键 (★)", custom.ButtonName);
    }

    [Fact]
    public void SyncDevicesCollection_DoesNotDuplicateWhenComboBoxClearsSelectionOnCollectionChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vib-reentrant-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigService(path);
            var g9 = new RemoteDevice { Id = "4754514C6FCC", Name = "Chromecast Remote", BluetoothAddress = 0x4754514C6FCC, ModelType = RemoteModelType.GoogleG9N9N };
            var mi = new RemoteDevice { Id = "C05D39F86E0C", Name = "MI RC", BluetoothAddress = 0xC05D39F86E0C, ModelType = RemoteModelType.XiaomiRC003 };
            config.UpsertDevice(g9);
            config.UpsertDevice(mi);

            using var devicesVm = new DevicesViewModel(config, new MinimalBluetoothManager());
            var mappingVm = new MappingViewModel(config);

            ((INotifyCollectionChanged)mappingVm.Devices).CollectionChanged += (_, args) =>
            {
                if (args.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                {
                    mappingVm.SelectedDevice = null;
                }
            };

            mappingVm.LoadMappings();
            mappingVm.SyncWithDevices();
            Assert.Equal(2, mappingVm.Devices.Count);

            devicesVm.RemoveDevice(g9);
            mappingVm.SyncWithDevices();
            Assert.Single(mappingVm.Devices);

            config.UpsertDevice(new RemoteDevice
            {
                Id = "bt_4754514C6FCC",
                Name = "Chromecast Remote",
                BluetoothAddress = 0x4754514C6FCC,
                ModelType = RemoteModelType.GoogleG9N9N
            });
            mappingVm.SyncWithDevices();
            mappingVm.LoadMappings();

            Assert.Equal(2, mappingVm.Devices.Count);
            Assert.Equal(2, mappingVm.Devices.Select(d => d.BluetoothAddress).Distinct().Count());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SyncWithDevices_DeduplicatesByPhysicalBluetoothAddress()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"vib-dedup-map-{Guid.NewGuid():N}.json"));
        config.Current.Devices.Add(new RemoteDevice
        {
            Id = "id-one",
            Name = "Chromecast A",
            BluetoothAddress = 0x4754514C6FCC,
            ModelType = RemoteModelType.GoogleG9N9N
        });
        config.Current.Devices.Add(new RemoteDevice
        {
            Id = "id-two",
            Name = "Chromecast B",
            BluetoothAddress = 0x4754514C6FCC,
            ModelType = RemoteModelType.GoogleG9N9N
        });

        var vm = new MappingViewModel(config);
        vm.SyncWithDevices();

        Assert.Single(vm.Devices);
        Assert.Equal(0x4754514C6FCCUL, vm.Devices[0].BluetoothAddress);
    }

    private sealed class MinimalBluetoothManager : IBluetoothManager
    {
        public bool IsScanning => false;
        public bool IsConnected => false;
        public bool IsReady => false;
        public bool IsStreaming => false;
        public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
        public BluetoothSessionState SessionState => BluetoothSessionState.Idle;
        public string ConnectedDeviceName => string.Empty;
        public ulong ConnectedDeviceAddress => 0;
        public Guid? ConnectedDeviceContainerId => null;
        public RemoteDeviceInformation? ConnectedDeviceInformation => null;
        public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;

        public event Action<RemoteDevice>? DeviceDiscovered { add { } remove { } }
        public event Action<ulong, bool, string>? ConnectionStatusChanged { add { } remove { } }
        public event Action<ulong>? ConnectedDeviceInformationChanged { add { } remove { } }
        public event Action<ulong, float>? AudioLevelChanged { add { } remove { } }
        public event Action<ulong>? VoiceButtonPressed { add { } remove { } }
        public event Action<ulong>? VoiceSessionStarted { add { } remove { } }
        public event Action<ulong>? VoiceSessionEnded { add { } remove { } }

        public void StartScanning() { }
        public void StopScanning() { }
        public Task DiscoverPairedRemotesAsync() => Task.CompletedTask;
        public void StartScan() { }
        public void StopScan() { }
        public Task<bool> ConnectAsync(ulong bluetoothAddress) => Task.FromResult(false);
        public void Disconnect() { }
        public Task<bool> StartVoiceSessionAsync() => Task.FromResult(false);
        public Task<bool> StopVoiceSessionAsync() => Task.FromResult(false);
        public void Dispose() { }
    }
}
