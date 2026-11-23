// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Core.Config;

public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly string _configFilePath;
    private readonly string _backupFilePath;

    public AppConfig Current { get; private set; }

    public ConfigService(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            _configFilePath = customPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "RemoteBuddy");
            Directory.CreateDirectory(dir);
            _configFilePath = Path.Combine(dir, "config.json");

            // Migrate legacy config if present
            if (!File.Exists(_configFilePath))
            {
                string legacyConfig = Path.Combine(appData, "VoiceInputBuddy", "config.json");
                if (File.Exists(legacyConfig))
                {
                    try
                    {
                        File.Copy(legacyConfig, _configFilePath, overwrite: false);
                    }
                    catch
                    {
                        // Ignore migration copy failures and continue with defaults
                    }
                }
            }
        }

        _backupFilePath = _configFilePath + ".bak";
        Current = Load();
    }

    public AppConfig Load()
    {
        lock (_lock)
        {
            bool hasMain = File.Exists(_configFilePath);
            if (hasMain && TryLoadFile(_configFilePath, out var config))
            {
                Current = config;
                AppLogger.Info($"Configuration loaded from {_configFilePath}. Devices: {Current.Devices.Count}, Selected: {Current.SelectedDeviceId ?? "none"}", "ConfigService");
                return Current;
            }

            // If main file exists but failed to load, quarantine it immediately
            // so subsequent saves or backups do not overwrite valid backups with corrupted data
            if (hasMain)
            {
                string corruptPath = _configFilePath + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
                try
                {
                    File.Move(_configFilePath, corruptPath, overwrite: true);
                    AppLogger.Warn($"Preserved corrupt config file as {corruptPath}", "ConfigService");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Could not move corrupt config file: {ex.Message}", "ConfigService");
                }
            }

            // Fallback to backup if main file was corrupt or missing
            if (File.Exists(_backupFilePath))
            {
                if (TryLoadFile(_backupFilePath, out var backupConfig))
                {
                    AppLogger.Warn($"Configuration restored from backup {_backupFilePath}", "ConfigService");
                    Current = backupConfig;
                    // Re-persist clean state to restore main config.json
                    Save();
                    return Current;
                }
                else
                {
                    string corruptBackup = _backupFilePath + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
                    try
                    {
                        File.Move(_backupFilePath, corruptBackup, overwrite: true);
                        AppLogger.Warn($"Preserved corrupt backup file as {corruptBackup}", "ConfigService");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Could not move corrupt backup file: {ex.Message}", "ConfigService");
                    }
                }
            }

            Current = new AppConfig();
            AppLogger.Info($"Initialized default configuration for {_configFilePath}", "ConfigService");
            return Current;
        }
    }

    private static bool TryLoadFile(string path, out AppConfig config)
    {
        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                config = null!;
                return false;
            }

            var parsed = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (parsed != null)
            {
                parsed.InputTool ??= new();
                parsed.Devices = DeduplicateDevices(parsed.Devices ?? new(), parsed.SelectedDeviceId);
                foreach (var dev in parsed.Devices)
                {
                    dev.Mappings ??= RemoteDevice.CreateDefaultMappings(dev.ModelType);
                }
                config = parsed;
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to parse config file at {path}", ex, "ConfigService");
        }

        config = null!;
        return false;
    }

    public static List<RemoteDevice> DeduplicateDevices(IEnumerable<RemoteDevice> devices, string? preferredSelectedId = null)
    {
        var result = new List<RemoteDevice>();
        var byAddress = new Dictionary<ulong, int>();
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var dev in devices)
        {
            if (dev == null) continue;

            int existingIndex = -1;
            if (dev.BluetoothAddress != 0 && byAddress.TryGetValue(dev.BluetoothAddress, out int addrIdx))
            {
                existingIndex = addrIdx;
            }
            else if (dev.BluetoothAddress == 0 && !string.IsNullOrEmpty(dev.Id) && byId.TryGetValue(dev.Id, out int idIdx))
            {
                existingIndex = idIdx;
            }
            else if (dev.BluetoothAddress == 0 && string.IsNullOrEmpty(dev.Id) && result.Contains(dev))
            {
                continue;
            }

            if (existingIndex < 0)
            {
                int idx = result.Count;
                result.Add(dev);
                if (dev.BluetoothAddress != 0) byAddress[dev.BluetoothAddress] = idx;
                else if (!string.IsNullOrEmpty(dev.Id)) byId[dev.Id] = idx;
                continue;
            }

            var existing = result[existingIndex];
            bool preferIncoming = !string.IsNullOrEmpty(preferredSelectedId) &&
                string.Equals(dev.Id, preferredSelectedId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(existing.Id, preferredSelectedId, StringComparison.OrdinalIgnoreCase);

            if (preferIncoming)
            {
                if ((dev.Mappings == null || dev.Mappings.Count == 0) && existing.Mappings is { Count: > 0 })
                {
                    dev.Mappings = existing.Mappings;
                }
                dev.ContainerId ??= existing.ContainerId;
                result[existingIndex] = dev;
            }
            else
            {
                if ((existing.Mappings == null || existing.Mappings.Count == 0) && dev.Mappings is { Count: > 0 })
                {
                    existing.Mappings = dev.Mappings;
                }
                existing.ContainerId ??= dev.ContainerId;
            }
        }

        return result;
    }

    public RemoteDevice UpsertDevice(RemoteDevice device, bool select = true, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lock)
        {
            if (string.IsNullOrEmpty(device.Id))
            {
                device.Id = device.BluetoothAddress != 0
                    ? device.BluetoothAddress.ToString("X12")
                    : Guid.NewGuid().ToString("N");
            }

            var existing = Current.Devices.FirstOrDefault(d => d.MatchesPhysicalDevice(device));
            RemoteDevice canonical;
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(device.Name)) existing.Name = device.Name;
                if (device.ModelType != RemoteModelType.Unknown) existing.ModelType = device.ModelType;
                if (device.ContainerId.HasValue) existing.ContainerId = device.ContainerId;
                if (!string.IsNullOrEmpty(device.HidDevicePath))
                {
                    existing.HidDevicePath = device.HidDevicePath;
                    existing.HidUsagePage = device.HidUsagePage;
                    existing.HidUsage = device.HidUsage;
                    existing.HidReportLength = device.HidReportLength;
                    existing.HidCaptureStatus = device.HidCaptureStatus;
                }
                if (existing.Mappings == null || existing.Mappings.Count == 0)
                {
                    existing.Mappings = device.Mappings is { Count: > 0 }
                        ? device.Mappings
                        : RemoteDevice.CreateDefaultMappings(existing.ModelType);
                }

                Current.Devices.RemoveAll(d => !ReferenceEquals(d, existing) && d.MatchesPhysicalDevice(device));
                canonical = existing;
            }
            else
            {
                if (device.Mappings == null || device.Mappings.Count == 0)
                {
                    device.Mappings = RemoteDevice.CreateDefaultMappings(device.ModelType);
                }
                Current.Devices.Add(device);
                canonical = device;
            }

            if (select)
            {
                Current.SelectedDeviceId = canonical.Id;
            }
            if (save)
            {
                SaveLocked();
            }
            return canonical;
        }
    }

    public void Update(Action<AppConfig> mutator, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        lock (_lock)
        {
            mutator(Current);
            if (save)
            {
                SaveLocked();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        var snapshot = CreateSnapshot(Current);
        string tempFile = _configFilePath + $".{Guid.NewGuid():N}.tmp";
        bool cleanupNeeded = true;
        try
        {
            string? dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tempFile, json);

            if (File.Exists(_configFilePath))
            {
                try
                {
                    File.Replace(tempFile, _configFilePath, _backupFilePath, ignoreMetadataErrors: true);
                    cleanupNeeded = false;
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                {
                    AppLogger.Warn($"File.Replace failed ({ex.Message}), falling back to direct copy", "ConfigService");
                    File.Copy(_configFilePath, _backupFilePath, overwrite: true);
                    File.Move(tempFile, _configFilePath, overwrite: true);
                    cleanupNeeded = false;
                }
            }
            else
            {
                File.Move(tempFile, _configFilePath, overwrite: true);
                cleanupNeeded = false;
                try
                {
                    File.Copy(_configFilePath, _backupFilePath, overwrite: true);
                }
                catch { }
            }

            AppLogger.Info($"Configuration safely saved to {_configFilePath}", "ConfigService");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to save configuration to {_configFilePath}", ex, "ConfigService");
        }
        finally
        {
            if (cleanupNeeded && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    private static AppConfig CreateSnapshot(AppConfig source)
    {
        var input = source.InputTool ?? new InputToolConfig();
        return new AppConfig
        {
            Version = source.Version,
            Language = source.Language,
            LaunchAtStartup = source.LaunchAtStartup,
            MinimizeToTrayOnClose = source.MinimizeToTrayOnClose,
            HasCompletedOnboarding = source.HasCompletedOnboarding,
            SelectedDeviceId = source.SelectedDeviceId,
            InputTool = new InputToolConfig
            {
                Tool = input.Tool,
                TriggerMode = input.TriggerMode,
                Shortcut = input.Shortcut,
                PreferredAudioInputDevice = input.PreferredAudioInputDevice
            },
            AppIntegrations = source.AppIntegrations != null
                ? new Dictionary<string, bool>(source.AppIntegrations)
                : new(),
            Devices = DeduplicateDevices(source.Devices ?? [], source.SelectedDeviceId)
                .Select(d => new RemoteDevice
                {
                    Id = d.Id,
                    BluetoothAddress = d.BluetoothAddress,
                    ContainerId = d.ContainerId,
                    Name = d.Name,
                    ModelType = d.ModelType,
                    Gain = d.Gain,
                    VoiceButtonMode = d.VoiceButtonMode,
                    Mappings = (d.Mappings ?? [])
                        .Select(m => new ButtonMapping
                        {
                            Button = m.Button,
                            ClickAction = m.ClickAction,
                            ClickShortcut = m.ClickShortcut,
                            ClickText = m.ClickText,
                            ClickMedia = m.ClickMedia,
                            DoubleClickAction = m.DoubleClickAction,
                            DoubleClickShortcut = m.DoubleClickShortcut,
                            DoubleClickText = m.DoubleClickText,
                            DoubleClickMedia = m.DoubleClickMedia,
                            LongPressAction = m.LongPressAction,
                            LongPressShortcut = m.LongPressShortcut,
                            LongPressText = m.LongPressText,
                            LongPressMedia = m.LongPressMedia
                        })
                        .ToList()
                })
                .ToList()
        };
    }
}
