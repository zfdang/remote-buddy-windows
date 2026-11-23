// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

public class MappingDeviceTests
{
    [Fact]
    public void Rc003Defaults_UseRequestedActions_WithoutChangingOtherModels()
    {
        var mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.XiaomiRC003);
        var tv = mappings.Single(m => m.Button == RemoteButton.LiveTv);
        Assert.Equal(ButtonActionType.MediaControl, tv.ClickAction);
        Assert.Equal(MediaCommand.PlayPause, tv.ClickMedia);
        var home = mappings.Single(m => m.Button == RemoteButton.Home);
        Assert.Equal(ButtonActionType.KeyboardShortcut, home.ClickAction);
        Assert.Equal("Win+Tab", home.ClickShortcut);
        var menu = mappings.Single(m => m.Button == RemoteButton.Menu);
        Assert.Equal(ButtonActionType.TextSnippet, menu.ClickAction);
        Assert.Null(menu.ClickText);
        var power = mappings.Single(m => m.Button == RemoteButton.Power);
        Assert.Equal(ButtonActionType.KeyboardShortcut, power.ClickAction);
        Assert.Equal("Backspace", power.ClickShortcut);

        var google = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleGY3LE);
        Assert.Equal("Alt+Tab", google.Single(m => m.Button == RemoteButton.Home).ClickShortcut);
        Assert.Equal("Apps", google.Single(m => m.Button == RemoteButton.Menu).ClickShortcut);
        Assert.Equal(ButtonActionType.None, google.Single(m => m.Button == RemoteButton.LiveTv).ClickAction);
        Assert.Equal(ButtonActionType.None, google.Single(m => m.Button == RemoteButton.Power).ClickAction);
    }

    [Fact]
    public void G9N9NDefaultsAssignActionsToThePhysicalButtons()
    {
        var mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleG9N9N);
        foreach (var (button, shortcut) in new[] {
            (RemoteButton.Power, "Backspace"), (RemoteButton.Home, "Win+Tab"),
            (RemoteButton.Back, "Escape"), (RemoteButton.YouTube, "Ctrl+R"), (RemoteButton.Netflix, "Ctrl+W")
        })
        {
            var mapping = mappings.Single(m => m.Button == button);
            Assert.Equal(new ButtonAssignment(ButtonActionType.KeyboardShortcut, shortcut),
                mapping.GetAssignment(ButtonTrigger.SingleClick));
        }

        var input = mappings.Single(m => m.Button == RemoteButton.Input);
        Assert.Equal(new ButtonAssignment(ButtonActionType.MediaControl, Media: MediaCommand.PlayPause),
            input.GetAssignment(ButtonTrigger.SingleClick));
    }

    [Fact]
    public void G9N9NDefaultsRepeatOnlyNavigationVolumeAndPower()
    {
        RemoteButton[] expected = [
            RemoteButton.Up, RemoteButton.Down, RemoteButton.Left, RemoteButton.Right, RemoteButton.Ok,
            RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.Power
        ];
        var mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleG9N9N);

        Assert.Equal(expected.Order(), mappings
            .Where(mapping => mapping.LongPressAction != ButtonActionType.None)
            .Select(mapping => mapping.Button).Order());
        Assert.All(mappings.Where(mapping => expected.Contains(mapping.Button)), mapping =>
            Assert.Equal(ButtonActionType.ContinuousRepeat, mapping.LongPressAction));
    }

    [Fact]
    public void T1RemoteDefaults_CreateExpectedFrontButtons_WithoutInterceptingKeyboard()
    {
        var mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.T1Remote);
        Assert.Equal(7, mappings.Count);

        var power = mappings.Single(m => m.Button == RemoteButton.Power);
        Assert.Equal(ButtonActionType.KeyboardShortcut, power.ClickAction);
        Assert.Equal("Backspace", power.ClickShortcut);

        var back = mappings.Single(m => m.Button == RemoteButton.Back);
        Assert.Equal(ButtonActionType.KeyboardShortcut, back.ClickAction);
        Assert.Equal("Escape", back.ClickShortcut);

        var home = mappings.Single(m => m.Button == RemoteButton.Home);
        Assert.Equal(ButtonActionType.KeyboardShortcut, home.ClickAction);
        Assert.Equal("Win+Tab", home.ClickShortcut);

        var menu = mappings.Single(m => m.Button == RemoteButton.Menu);
        Assert.Equal(ButtonActionType.KeyboardShortcut, menu.ClickAction);
        Assert.Equal("Apps", menu.ClickShortcut);

        var volUp = mappings.Single(m => m.Button == RemoteButton.VolumeUp);
        Assert.Equal(ButtonActionType.MediaControl, volUp.ClickAction);
        Assert.Equal(MediaCommand.VolumeUp, volUp.ClickMedia);

        var volDown = mappings.Single(m => m.Button == RemoteButton.VolumeDown);
        Assert.Equal(ButtonActionType.MediaControl, volDown.ClickAction);
        Assert.Equal(MediaCommand.VolumeDown, volDown.ClickMedia);

        var mute = mappings.Single(m => m.Button == RemoteButton.Mute);
        Assert.Equal(ButtonActionType.MediaControl, mute.ClickAction);
        Assert.Equal(MediaCommand.Mute, mute.ClickMedia);

        // Enter and arrow keys must not be present in mapped buttons
        Assert.DoesNotContain(mappings, m => m.Button == RemoteButton.Ok);
        Assert.DoesNotContain(mappings, m => m.Button == RemoteButton.Up);
        Assert.DoesNotContain(mappings, m => m.Button == RemoteButton.Down);
        Assert.DoesNotContain(mappings, m => m.Button == RemoteButton.Left);
        Assert.DoesNotContain(mappings, m => m.Button == RemoteButton.Right);
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

        // Default VoiceButtonMode is SmartToggle
        Assert.Equal(G9N9NVoiceButtonMode.SmartToggle, g9n9n.VoiceButtonMode);
        Assert.True(g9n9n.IsSmartToggleMode);
        Assert.False(g9n9n.IsHoldToTalkMode);

        // Switch to HoldToTalk
        g9n9n.IsHoldToTalkMode = true;
        Assert.Equal(G9N9NVoiceButtonMode.HoldToTalk, g9n9n.VoiceButtonMode);
        Assert.False(g9n9n.IsSmartToggleMode);
        Assert.True(g9n9n.IsHoldToTalkMode);

        var xiaomi = new RemoteDevice { ModelType = RemoteModelType.XiaomiRC003 };
        Assert.False(xiaomi.IsT1);
        Assert.False(xiaomi.IsG9N9N);
        Assert.True(xiaomi.IsXiaomi);
    }

    [Theory]
    [InlineData(RemoteModelType.XiaomiRC001)]
    [InlineData(RemoteModelType.XiaomiRC003)]
    [InlineData(RemoteModelType.XiaomiARN9)]
    [InlineData(RemoteModelType.Unknown)]
    public void XiaomiAndDefaultMappings_UseWinTabAndBackspace(RemoteModelType type)
    {
        var mappings = RemoteDevice.CreateDefaultMappings(type);
        var home = mappings.Single(m => m.Button == RemoteButton.Home);
        Assert.Equal(ButtonActionType.KeyboardShortcut, home.ClickAction);
        Assert.Equal("Win+Tab", home.ClickShortcut);
        var power = mappings.Single(m => m.Button == RemoteButton.Power);
        Assert.Equal(ButtonActionType.KeyboardShortcut, power.ClickAction);
        Assert.Equal("Backspace", power.ClickShortcut);
    }

    [Fact]
    public void SwitchingActionTypesPreservesDraftShortcutAndTextIndependently()
    {
        var mapping = new ButtonMapping { Button = RemoteButton.Menu, ClickAction = ButtonActionType.TextSnippet };
        var vm = new MappingItemViewModel(mapping);
        vm.SingleClick.Text = "你好";
        vm.SingleClick.Shortcut = "Win+D";
        Assert.Equal("你好", mapping.ClickText);
        vm.SingleClick.Action = ButtonActionType.KeyboardShortcut;
        Assert.Equal("Win+D", mapping.ClickShortcut);
    }

    [Fact]
    public void MappingGestureEditorsSaveIndependentActionsAndClearAllThree()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"rb-gestures-{Guid.NewGuid():N}.json");
        var config = new ConfigService(configPath);
        var device = new RemoteDevice { Id = "g9n9n", ModelType = RemoteModelType.GoogleG9N9N,
            Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleG9N9N) };
        config.Current.Devices.Add(device);
        config.Current.SelectedDeviceId = device.Id;
        var vm = new MappingViewModel(config);
        vm.SelectButton("Up");
        var up = vm.SelectedMapping!;
        up.SingleClick.Action = ButtonActionType.KeyboardShortcut;
        up.SingleClick.Shortcut = "Ctrl+C";
        up.LongPress.Action = ButtonActionType.MediaControl;
        up.LongPress.Media = MediaCommand.Mute;
        up.DoubleClick.Action = ButtonActionType.TextSnippet;
        up.DoubleClick.Text = "你好";

        var reloaded = new ConfigService(configPath).Current.Devices.Single().Mappings.Single(mapping => mapping.Button == RemoteButton.Up);
        Assert.Equal("Ctrl+C", reloaded.ClickShortcut);
        Assert.Equal(MediaCommand.Mute, reloaded.LongPressMedia);
        Assert.Equal("你好", reloaded.DoubleClickText);

        vm.ClearSelectedMapping();
        reloaded = new ConfigService(configPath).Current.Devices.Single().Mappings.Single(mapping => mapping.Button == RemoteButton.Up);
        Assert.All(Enum.GetValues<ButtonTrigger>(), trigger => Assert.Equal(ButtonActionType.None, reloaded.GetAssignment(trigger).Action));
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
        var up = vm.CurrentMappings.Single(m => m.Button == RemoteButton.Up);
        up.SingleClick.Shortcut = "F2";
        vm.SelectedDevice = first;
        vm.SelectedDevice = second;
        Assert.Equal("F2", vm.CurrentMappings.Single(m => m.Button == RemoteButton.Up).SingleClick.Shortcut);
        vm.SaveMappings();

        Assert.Equal("F2", second.Mappings.Single(m => m.Button == RemoteButton.Up).ClickShortcut);
        Assert.Equal("Up", first.Mappings.Single(m => m.Button == RemoteButton.Up).ClickShortcut);
        Assert.Equal(first.Id, config.Current.SelectedDeviceId);
        Assert.Contains(vm.CurrentMappings, m => m.Button == RemoteButton.Power);
        Assert.Contains(vm.CurrentMappings, m => m.Button == RemoteButton.Menu);
        Assert.Contains(vm.CurrentMappings, m => m.Button == RemoteButton.LiveTv);
        Assert.DoesNotContain(vm.CurrentMappings, m => m.Button == RemoteButton.Mute);
        Assert.Contains(second.Mappings, m => m.Button == RemoteButton.Mute);
        vm.SelectedDevice = first;
        Assert.Equal("Up", vm.CurrentMappings.Single(m => m.Button == RemoteButton.Up).SingleClick.Shortcut);
        vm.SelectedDevice = second;
        Assert.Equal("F2", vm.CurrentMappings.Single(m => m.Button == RemoteButton.Up).SingleClick.Shortcut);
    }

    [Fact]
    public void MappingGesture_DisplaySummaryAndStatus_ReflectsConfiguration()
    {
        var mapping = new ButtonMapping { Button = RemoteButton.Up };
        var item = new MappingItemViewModel(mapping);

        Assert.Equal("未配置", item.SingleClickSummary);
        Assert.Equal("未配置", item.LongPressSummary);
        Assert.Equal("未配置", item.DoubleClickSummary);
        Assert.False(item.SingleClick.IsConfigured);

        item.SingleClick.Action = ButtonActionType.KeyboardShortcut;
        item.SingleClick.Shortcut = "Ctrl+C";
        Assert.Equal("快捷键: Ctrl+C", item.SingleClickSummary);
        Assert.True(item.SingleClick.IsConfigured);

        item.LongPress.Action = ButtonActionType.ContinuousRepeat;
        Assert.Equal("持续连发", item.LongPressSummary);
        Assert.True(item.LongPress.IsConfigured);

        item.DoubleClick.Action = ButtonActionType.MediaControl;
        item.DoubleClick.Media = MediaCommand.PlayPause;
        Assert.Equal("媒体: 播放/暂停", item.DoubleClickSummary);
        Assert.True(item.DoubleClick.IsConfigured);

        item.Clear();
        Assert.Equal("未配置", item.SingleClickSummary);
        Assert.Equal("未配置", item.LongPressSummary);
        Assert.Equal("未配置", item.DoubleClickSummary);
    }

    [Fact]
    public void MappingViewModel_SelectTrigger_SwitchesActiveGestureAndTitle()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"vib-mapping-trig-{Guid.NewGuid():N}.json"));
        var device = new RemoteDevice { Id = "test_dev", ModelType = RemoteModelType.XiaomiRC003 };
        config.Current.Devices.Add(device);
        config.Current.SelectedDeviceId = device.Id;

        var vm = new MappingViewModel(config);
        Assert.Equal(ButtonTrigger.SingleClick, vm.SelectedTrigger);
        Assert.NotNull(vm.ActiveGesture);
        Assert.Equal("👆 单击设置", vm.ActiveTriggerTitle);

        vm.SelectTrigger("LongPress");
        Assert.Equal(ButtonTrigger.LongPress, vm.SelectedTrigger);
        Assert.Equal(vm.SelectedMapping!.LongPress, vm.ActiveGesture);
        Assert.Equal("⏱️ 长按设置", vm.ActiveTriggerTitle);

        vm.SelectTrigger("DoubleClick");
        Assert.Equal(ButtonTrigger.DoubleClick, vm.SelectedTrigger);
        Assert.Equal(vm.SelectedMapping!.DoubleClick, vm.ActiveGesture);
        Assert.Equal("✌️ 双击设置", vm.ActiveTriggerTitle);
    }

    [Fact]
    public void RemoteDevice_DefaultMicrophoneGain_IsPlus10dB()
    {
        var dev = new RemoteDevice();
        Assert.Equal(RemoteDevice.DefaultMicrophoneGain, dev.Gain);
        double db = Math.Round(20.0 * Math.Log10(dev.Gain));
        Assert.Equal(10.0, db);
    }
}
