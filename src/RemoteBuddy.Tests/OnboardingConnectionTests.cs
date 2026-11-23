// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Bluetooth;
using Xunit;

namespace RemoteBuddy.Tests;

public class OnboardingConnectionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Unknown")]
    public void ToolConfiguration_InvalidShortcut_DoesNotAdvanceOrOverwriteSavedShortcut(string shortcut)
    {
        using var manager = new WinRtBluetoothManager();
        var config = CreateConfig();
        using var vm = new OnboardingViewModel(config, manager)
        {
            CurrentStep = 6,
            ShortcutText = shortcut
        };

        vm.NextStep();

        Assert.Equal(6, vm.CurrentStep);
        Assert.Equal("Ctrl+F5", config.Current.InputTool.Shortcut);
        Assert.Contains("快捷键", vm.ValidationError);
        vm.ApplyPreset("WeChatInput");
        Assert.Equal("Ctrl+F5", vm.ShortcutText);
        vm.NextStep();
        Assert.Equal(7, vm.CurrentStep);
        Assert.Empty(vm.ValidationError);
    }

    [Fact]
    public void ReopeningWizard_WithReadyService_RestoresDeviceAndPassesConnectionStep()
    {
        using var manager = new WinRtBluetoothManager();
        typeof(WinRtBluetoothManager).GetProperty(nameof(WinRtBluetoothManager.IsReady))!
            .SetValue(manager, true);
        var config = CreateConfig();
        var vm = new OnboardingViewModel(config, manager) { CurrentStep = 3 };

        Assert.Same(config.Current.Devices[0], vm.SelectedDevice);
        Assert.Single(vm.DiscoveredDevices);
        Assert.True(vm.IsDeviceConnected);
        vm.NextStep();
        Assert.Equal(4, vm.CurrentStep);
        Assert.Empty(vm.ValidationError);
    }

    [Fact]
    public void SavedConnectedFlag_WithoutReadyService_DoesNotPassConnectionStep()
    {
        using var manager = new WinRtBluetoothManager();
        var vm = new OnboardingViewModel(CreateConfig(), manager) { CurrentStep = 3 };

        Assert.False(vm.IsDeviceConnected);
        vm.NextStep();
        Assert.Equal(3, vm.CurrentStep);
        Assert.Contains("语音服务", vm.ValidationError);
    }

    [Fact]
    public void Disconnect_ClearsServiceReadiness()
    {
        using var manager = new WinRtBluetoothManager();
        typeof(WinRtBluetoothManager).GetProperty(nameof(WinRtBluetoothManager.IsReady))!
            .SetValue(manager, true);
        manager.Disconnect();
        Assert.False(manager.IsReady);
    }

    [Fact]
    public void ChromecastButtonTest_DistinguishesVoiceFromOrdinaryButtonActivity()
    {
        using var manager = new WinRtBluetoothManager();
        var config = CreateConfig();
        config.Current.Devices[0].ModelType = RemoteModelType.GoogleG9N9N;
        using var vm = new OnboardingViewModel(config, manager) { CurrentStep = 4, IsDeviceConnected = true };

        vm.NotifyButtonPressed(RemoteButton.Voice);
        Assert.Equal("已按下: 语音键", vm.LastPressedButtonName);
        Assert.True(vm.HasButtonTested);

        vm.NotifyTruncatedPacketDetected();
        Assert.True(vm.HasButtonTested);
        Assert.Equal("检测到普通按键活动", vm.LastPressedButtonName);
        Assert.Contains("未提供键码", vm.TruncationWarningMessage);

        vm.NotifyButtonPressed(RemoteButton.Voice);
        Assert.Equal("已按下: 语音键", vm.LastPressedButtonName);
        Assert.Contains("未提供键码", vm.TruncationWarningMessage);

        vm.NotifyButtonPressed(RemoteButton.Left);
        Assert.Contains("方向左键", vm.LastPressedButtonName);
        Assert.Empty(vm.TruncationWarningMessage);

        vm.NotifyButtonPressed(RemoteButton.Voice);
        Assert.Equal("已按下: 语音键", vm.LastPressedButtonName);
        Assert.True(vm.HasButtonTested);
        vm.NextStep();
        Assert.Equal(WizardStep.AudioTest, vm.CurrentWizardStep);

        vm.PreviousStep();
        vm.NotifyButtonPressed(RemoteButton.Voice);
        vm.NextStep();
        Assert.Equal(WizardStep.AudioTest, vm.CurrentWizardStep);
    }

    private static ConfigService CreateConfig()
    {
        var config = new ConfigService(Path.Combine(Path.GetTempPath(), $"vib-test-{Guid.NewGuid():N}.json"));
        config.Current.SelectedDeviceId = "1234";
        config.Current.Devices.Add(new RemoteDevice
        {
            Id = "1234", BluetoothAddress = 0x1234, Name = "Test remote", IsConnected = true
        });
        return config;
    }

    [Fact]
    public void ButtonTest_RequiresFreshPressDuringStepFour()
    {
        using var manager = new WinRtBluetoothManager();
        var vm = new OnboardingViewModel(CreateConfig(), manager);
        vm.NotifyButtonPressed(RemoteButton.Up);
        Assert.False(vm.HasButtonTested);
        vm.CurrentStep = 4;
        vm.NextStep();
        Assert.Equal(4, vm.CurrentStep);
        vm.NotifyButtonPressed(RemoteButton.Ok);
        vm.NextStep();
        Assert.Equal(5, vm.CurrentStep);
        vm.PreviousStep();
        Assert.False(vm.HasButtonTested);
    }
    [Fact]
    public void VoiceArbiterAllowsOnlyOneContendingTransportAndReleasesForNextPress()
    {
        var arbiter = new VoiceSessionArbiter();
        using var first = new WinRtBluetoothManager(arbiter);
        using var second = new WinRtBluetoothManager(arbiter);
        bool a = false, b = false;
        Parallel.Invoke(() => a = arbiter.TryAcquire(first), () => b = arbiter.TryAcquire(second));

        Assert.NotEqual(a, b);
        var winner = a ? first : second;
        var loser = a ? second : first;
        Assert.Same(winner, arbiter.Owner);
        arbiter.Release(loser);
        Assert.Same(winner, arbiter.Owner);
        arbiter.Release(winner);
        Assert.True(arbiter.TryAcquire(loser));
        Assert.Same(loser, arbiter.Owner);
    }

}
