// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

public class ConfigServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "VIB_ConfigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Load_InitializesDefault_WhenFileDoesNotExist()
    {
        string path = Path.Combine(_tempDir, "config.json");
        var service = new ConfigService(path);

        Assert.NotNull(service.Current);
        Assert.False(service.Current.HasCompletedOnboarding);
        Assert.Empty(service.Current.Devices);
    }

    [Fact]
    public void SaveAndLoad_PersistsAllProperties()
    {
        string path = Path.Combine(_tempDir, "config.json");
        var service = new ConfigService(path);

        service.Current.HasCompletedOnboarding = true;
        service.Current.SelectedDeviceId = "DEV_001";
        service.Current.Devices.Add(new RemoteDevice
        {
            Id = "DEV_001",
            Name = "Living Room Remote",
            BluetoothAddress = 0xAABBCCDDEEFF,
            ModelType = RemoteModelType.XiaomiARN9
        });
        service.Current.InputTool.Tool = TargetInputTool.WeChatInput;
        service.Current.InputTool.Shortcut = "Ctrl + Win";

        service.Save();

        var reloaded = new ConfigService(path);
        Assert.True(reloaded.Current.HasCompletedOnboarding);
        Assert.Equal("DEV_001", reloaded.Current.SelectedDeviceId);
        Assert.Single(reloaded.Current.Devices);
        Assert.Equal(RemoteModelType.XiaomiARN9, reloaded.Current.Devices[0].ModelType);
        Assert.Equal(TargetInputTool.WeChatInput, reloaded.Current.InputTool.Tool);
        Assert.Equal("Ctrl + Win", reloaded.Current.InputTool.Shortcut);
    }

    [Fact]
    public void Load_RestoresFromBackup_WhenMainConfigIsCorrupted()
    {
        string path = Path.Combine(_tempDir, "config.json");
        var service = new ConfigService(path);

        service.Current.HasCompletedOnboarding = true;
        service.Current.SelectedDeviceId = "ORIGINAL_VALID_ID";
        service.Save(); // Generates config.json and backup

        // Corrupt main config.json
        File.WriteAllText(path, "{ corrupt_json: [ incomplete");

        var recoveredService = new ConfigService(path);
        Assert.True(recoveredService.Current.HasCompletedOnboarding);
        Assert.Equal("ORIGINAL_VALID_ID", recoveredService.Current.SelectedDeviceId);
    }

    [Fact]
    public void Load_PreservesCorruptFileAsTimestampedBackup_WhenNoValidBackupExists()
    {
        string path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, "THIS IS INVALID NOT JSON");

        var service = new ConfigService(path);

        // Fallback to default
        Assert.NotNull(service.Current);
        Assert.False(service.Current.HasCompletedOnboarding);

        // The corrupt file must have been preserved for diagnostics
        var corruptFiles = Directory.GetFiles(_tempDir, "config.json.corrupt.*");
        Assert.Single(corruptFiles);
        Assert.Equal("THIS IS INVALID NOT JSON", File.ReadAllText(corruptFiles[0]));
    }

    [Fact]
    public void Save_OmitsRuntimeAndComputedPropertiesFromJson()
    {
        string path = Path.Combine(_tempDir, "config.json");
        var service = new ConfigService(path);
        var device = new RemoteDevice
        {
            Id = "DEV_SANITIZED",
            Name = "Chromecast",
            BluetoothAddress = 0x112233445566,
            ModelType = RemoteModelType.GoogleG9N9N,
            IsConnected = true,
            BatteryLevel = 92,
            HidDevicePath = @"\\?\hid#test",
            HidUsagePage = 0x0C,
            HidUsage = 0x01,
            HidReportLength = 3,
            HidCaptureStatus = "Ready",
            HardwareInformation = new RemoteDeviceInformation(0x112233445566, null, "Chromecast", "G9N9N", null)
        };
        service.Current.Devices.Add(device);
        service.Save();

        string json = File.ReadAllText(path);
        foreach (string forbidden in new[]
        {
            "IsConnected", "BatteryLevel", "HardwareInformation",
            "HidDevicePath", "HidUsagePage", "HidUsage", "HidReportLength", "HidCaptureStatus",
            "HasHidInfo", "HidSpecSummary", "IsG9N9N", "IsT1", "IsXiaomi",
            "FormattedAddress", "ModelDisplayName", "IllustrationSource",
            "VoiceModeHelp", "IsSmartToggleMode", "IsHoldToTalkMode"
        })
        {
            Assert.DoesNotContain($"\"{forbidden}\"", json);
        }

        foreach (string expected in new[]
        {
            "\"Id\"", "\"BluetoothAddress\"", "\"Name\"", "\"ModelType\"", "\"Gain\"", "\"VoiceButtonMode\"", "\"Mappings\""
        })
        {
            Assert.Contains(expected, json);
        }
    }

    [Fact]
    public async Task Save_WithConcurrentUpdates_DoesNotThrowCollectionModified()
    {
        string path = Path.Combine(_tempDir, "config.json");
        var service = new ConfigService(path);

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                int idx = i;
                service.Update(cfg =>
                {
                    if (cfg.Devices.Count > 5) cfg.Devices.RemoveAt(0);
                    cfg.Devices.Add(new RemoteDevice
                    {
                        Id = $"dev_{idx}",
                        Name = $"Remote {idx}",
                        BluetoothAddress = (ulong)(0x1000 + idx),
                        ModelType = RemoteModelType.GoogleG9N9N,
                        Mappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleG9N9N)
                    });
                    cfg.SelectedDeviceId = $"dev_{idx}";
                }, save: false);
            }
        });

        var saver = Task.Run(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                service.Save();
            }
        });

        await Task.WhenAll(writer, saver);
        service.Save();

        var reloaded = new ConfigService(path);
        Assert.NotNull(reloaded.Current);
        Assert.NotEmpty(reloaded.Current.Devices);
    }

    [Fact]
    public void UpsertDevice_DeduplicatesByPhysicalBluetoothAddress_AndPreservesMappings()
    {
        string path = Path.Combine(_tempDir, "config.json");
        var service = new ConfigService(path);

        var customMappings = RemoteDevice.CreateDefaultMappings(RemoteModelType.GoogleG9N9N);
        customMappings.Single(m => m.Button == RemoteButton.Home).SetConfiguredAction(
            ButtonTrigger.SingleClick,
            new ConfiguredButtonAction { Action = ButtonAction.CustomShortcut, Shortcut = "Ctrl+Alt+H" });

        var first = new RemoteDevice
        {
            Id = "4754514C6FCC",
            Name = "Chromecast",
            BluetoothAddress = 0x4754514C6FCC,
            ModelType = RemoteModelType.GoogleG9N9N,
            Mappings = customMappings
        };
        service.UpsertDevice(first);

        var duplicateIdEntry = new RemoteDevice
        {
            Id = "bt_4754514C6FCC",
            Name = "Chromecast Remote",
            BluetoothAddress = 0x4754514C6FCC,
            ModelType = RemoteModelType.GoogleG9N9N,
            Mappings = []
        };
        var canonical = service.UpsertDevice(duplicateIdEntry);

        Assert.Single(service.Current.Devices);
        Assert.Equal("4754514C6FCC", canonical.Id);
        Assert.Equal("Chromecast Remote", canonical.Name);
        Assert.Equal("Ctrl+Alt+H", canonical.Mappings.Single(m => m.Button == RemoteButton.Home).GetConfiguredAction(ButtonTrigger.SingleClick).Shortcut);

        var reloaded = new ConfigService(path);
        Assert.Single(reloaded.Current.Devices);
        Assert.Equal("Ctrl+Alt+H", reloaded.Current.Devices[0].Mappings.Single(m => m.Button == RemoteButton.Home).GetConfiguredAction(ButtonTrigger.SingleClick).Shortcut);
    }

    [Fact]
    public void Load_MigratesLegacyV1ButtonMappings_ToConfiguredButtonActions()
    {
        string path = Path.Combine(_tempDir, "legacy-config.json");
        File.WriteAllText(path, """
        {
          "Version": 1,
          "SelectedDeviceId": "DEV1",
          "Devices": [
            {
              "Id": "DEV1",
              "BluetoothAddress": 123456,
              "Name": "Legacy Remote",
              "ModelType": "GoogleG9N9N",
              "Mappings": [
                {
                  "Button": "Home",
                  "ClickAction": "KeyboardShortcut",
                  "ClickShortcut": "Win+Tab",
                  "DoubleClickAction": "TextSnippet",
                  "DoubleClickText": "你好",
                  "LongPressAction": "MediaControl",
                  "LongPressMedia": "PlayPause"
                },
                {
                  "Button": "Power",
                  "ClickAction": "KeyboardShortcut",
                  "ClickShortcut": "Ctrl+Alt+P",
                  "LongPressAction": "ContinuousRepeat"
                }
              ]
            }
          ]
        }
        """);

        var service = new ConfigService(path);
        var dev = Assert.Single(service.Current.Devices);
        var home = dev.Mappings.Single(m => m.Button == RemoteButton.Home);
        Assert.Equal(ButtonAction.TaskView, home.GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal(ButtonAction.TextSnippet, home.GetConfiguredAction(ButtonTrigger.DoubleClick).Action);
        Assert.Equal("你好", home.GetConfiguredAction(ButtonTrigger.DoubleClick).Text);
        Assert.Equal(ButtonAction.PlayPause, home.GetConfiguredAction(ButtonTrigger.LongPress).Action);

        var power = dev.Mappings.Single(m => m.Button == RemoteButton.Power);
        Assert.Equal(ButtonAction.CustomShortcut, power.GetConfiguredAction(ButtonTrigger.SingleClick).Action);
        Assert.Equal("Ctrl+Alt+P", power.GetConfiguredAction(ButtonTrigger.SingleClick).Shortcut);
        Assert.Equal(ButtonAction.RepeatSingleClick, power.GetConfiguredAction(ButtonTrigger.LongPress).Action);

        service.Save();
        string savedJson = File.ReadAllText(path);
        Assert.DoesNotContain("ClickAction", savedJson);
        Assert.Contains("SingleClick", savedJson);
    }

    [Fact]
    public void Load_DeduplicatesExistingDuplicatePhysicalAddressesInConfigJson()
    {
        string path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """
        {
          "Version": 1,
          "SelectedDeviceId": "bt_4754514C6FCC",
          "Devices": [
            {
              "Id": "4754514C6FCC",
              "BluetoothAddress": 78427466788812,
              "Name": "Chromecast Remote",
              "ModelType": "GoogleG9N9N"
            },
            {
              "Id": "bt_4754514C6FCC",
              "BluetoothAddress": 78427466788812,
              "Name": "Chromecast Remote",
              "ModelType": "GoogleG9N9N"
            },
            {
              "Id": "C05D39F86E0C",
              "BluetoothAddress": 211506637073932,
              "Name": "MI RC",
              "ModelType": "XiaomiRC003"
            }
          ]
        }
        """);

        var service = new ConfigService(path);
        Assert.Equal(2, service.Current.Devices.Count);
        Assert.Contains(service.Current.Devices, d => d.Id == "bt_4754514C6FCC");
        Assert.Contains(service.Current.Devices, d => d.Id == "C05D39F86E0C");
    }
}
