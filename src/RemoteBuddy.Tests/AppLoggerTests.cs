// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Diagnostics;
using Xunit;

namespace RemoteBuddy.Tests;

[Collection("AppLogger")]
public class AppLoggerTests : IDisposable
{
    private readonly string _tempLogDir;

    public AppLoggerTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "VIB_Tests_" + Guid.NewGuid().ToString("N"));
        AppLogger.ResetForTesting();
        AppLogger.Initialize(_tempLogDir);
    }

    public void Dispose()
    {
        AppLogger.ResetForTesting();
        try
        {
            if (Directory.Exists(_tempLogDir))
            {
                Directory.Delete(_tempLogDir, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup errors
        }
    }

    [Fact]
    public void AppLogger_WritesFormattedLogEntry_ToFile()
    {
        AppLogger.Info("Bluetooth connection established", "BluetoothManager");

        string logFile = AppLogger.CurrentLogFilePath;
        Assert.True(File.Exists(logFile));

        string content = File.ReadAllText(logFile);
        Assert.Contains("[INFO ]", content);
        Assert.Contains("[BluetoothManager]", content);
        Assert.Contains("Bluetooth connection established", content);
    }

    [Fact]
    public void AppLogger_WritesExceptionDetails_OnError()
    {
        var ex = new InvalidOperationException("GATT discovery failed");
        AppLogger.Error("Operation failed", ex, "BluetoothManager");

        string content = File.ReadAllText(AppLogger.CurrentLogFilePath);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("[BluetoothManager]", content);
        Assert.Contains("Operation failed", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("GATT discovery failed", content);
    }

    [Fact]
    public void AppLogger_RotatesLogs_WhenExceedingMaxSize()
    {
        // Re-initialize with small limit: 1200 bytes
        AppLogger.ResetForTesting();
        AppLogger.Initialize(_tempLogDir, maxSizeBytes: 1200, maxArchives: 2);

        // Write enough lines to exceed 1200 bytes
        for (int i = 0; i < 30; i++)
        {
            AppLogger.Info($"Repetitive test log message number {i:D3} for rotation testing", "TestComp");
        }

        string archive1 = Path.Combine(_tempLogDir, "app.1.log");
        Assert.True(File.Exists(archive1), "Expected app.1.log to exist after rotation");

        string current = File.ReadAllText(AppLogger.CurrentLogFilePath);
        Assert.Contains("=== Remote Buddy Diagnostic Log Rotated", current);
    }

    [Fact]
    public void AppLogger_Disabled_DoesNotAppend()
    {
        AppLogger.Info("First entry", "Comp");
        string before = File.ReadAllText(AppLogger.CurrentLogFilePath);

        AppLogger.IsEnabled = false;
        AppLogger.Info("Second entry should be ignored", "Comp");

        string after = File.ReadAllText(AppLogger.CurrentLogFilePath);
        Assert.Equal(before, after);
    }

    [Fact]
    public void AppLogger_InitializedDisabled_DoesNotCreateLogDirectoryOrFile()
    {
        string disabledDir = Path.Combine(Path.GetTempPath(), "VIB_DisabledLogs_" + Guid.NewGuid().ToString("N"));
        try
        {
            AppLogger.ResetForTesting();
            AppLogger.Initialize(disabledDir, isEnabled: false);

            AppLogger.Info("Should not be written", "Test");
            AppLogger.Warn("Warning should not be written", "Test");
            AppLogger.Error("Error should not be written", new InvalidOperationException("ignored"), "Test");

            Assert.Equal(disabledDir, AppLogger.LogDirectory);
            Assert.False(AppLogger.OpenLogFolder());
            Assert.False(Directory.Exists(disabledDir));
            Assert.False(File.Exists(AppLogger.CurrentLogFilePath));
        }
        finally
        {
            AppLogger.ResetForTesting();
            if (Directory.Exists(disabledDir))
            {
                Directory.Delete(disabledDir, recursive: true);
            }
        }
    }

    [Fact]
    public void AppLogger_SetEnabled_TogglesLoggingAndCreatesFileOnlyWhenEnabled()
    {
        string toggleDir = Path.Combine(Path.GetTempPath(), "VIB_ToggleLogs_" + Guid.NewGuid().ToString("N"));
        try
        {
            AppLogger.ResetForTesting();
            AppLogger.Initialize(toggleDir, isEnabled: false);

            AppLogger.Info("Before enable", "Test");
            Assert.False(File.Exists(AppLogger.CurrentLogFilePath));

            AppLogger.SetEnabled(true);
            Assert.True(AppLogger.IsEnabled);
            Assert.True(File.Exists(AppLogger.CurrentLogFilePath));
            string afterEnable = File.ReadAllText(AppLogger.CurrentLogFilePath);
            Assert.Contains("Diagnostic logging enabled by user", afterEnable);

            AppLogger.SetEnabled(false);
            AppLogger.Info("After disable", "Test");
            string afterDisable = File.ReadAllText(AppLogger.CurrentLogFilePath);
            Assert.Equal(afterEnable, afterDisable);
        }
        finally
        {
            AppLogger.ResetForTesting();
            if (Directory.Exists(toggleDir))
            {
                Directory.Delete(toggleDir, recursive: true);
            }
        }
    }

    [Fact]
    public void AppLogger_ResetForTesting_PreservesTestDefaultDirectoryIsolation()
    {
        AppLogger.ResetForTesting();
        string defaultTestDir = AppLogger.LogDirectory;
        string realAppDataLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteBuddy",
            "logs");

        Assert.StartsWith(Path.GetTempPath(), defaultTestDir, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(realAppDataLogDir, defaultTestDir);
    }

    [Fact]
    public void ConfigService_ReadDiagnosticLoggingEnabled_DefaultsTrueAndRespectsSavedFlag()
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"vib-logcfg-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(ConfigService.ReadDiagnosticLoggingEnabled(configPath));

            var config = new ConfigService(configPath);
            Assert.True(config.Current.DiagnosticLoggingEnabled);

            config.Update(cfg => cfg.DiagnosticLoggingEnabled = false);
            Assert.False(ConfigService.ReadDiagnosticLoggingEnabled(configPath));

            config.Update(cfg => cfg.DiagnosticLoggingEnabled = true);
            Assert.True(ConfigService.ReadDiagnosticLoggingEnabled(configPath));
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
            if (File.Exists(configPath + ".bak")) File.Delete(configPath + ".bak");
        }
    }

    [Fact]
    public void AboutAndSettingsViewModel_DiagnosticLoggingToggle_PersistsSyncsAndControlsAppLogger()
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"vib-logvm-{Guid.NewGuid():N}.json");
        string vmLogDir = Path.Combine(Path.GetTempPath(), "VIB_VmLogs_" + Guid.NewGuid().ToString("N"));
        try
        {
            AppLogger.ResetForTesting();
            AppLogger.Initialize(vmLogDir, isEnabled: true);

            var config = new ConfigService(configPath);
            var settingsVm = new SettingsViewModel(config);
            var aboutVm = new AboutViewModel(config);
            settingsVm.PreferencesChanged += aboutVm.SyncFromConfig;
            aboutVm.PreferencesChanged += settingsVm.SyncFromConfig;

            Assert.True(settingsVm.DiagnosticLoggingEnabled);
            Assert.True(aboutVm.DiagnosticLoggingEnabled);

            // Turning off from About page stops logging, persists config, and syncs Settings page
            aboutVm.DiagnosticLoggingEnabled = false;
            Assert.False(AppLogger.IsEnabled);
            Assert.False(settingsVm.DiagnosticLoggingEnabled);
            Assert.False(ConfigService.ReadDiagnosticLoggingEnabled(configPath));

            string beforeDisabledWrite = File.ReadAllText(AppLogger.CurrentLogFilePath);
            AppLogger.Info("Must not be logged while disabled via UI", "Test");
            Assert.Equal(beforeDisabledWrite, File.ReadAllText(AppLogger.CurrentLogFilePath));

            // Turning back on from Settings page resumes logging and syncs About page
            settingsVm.DiagnosticLoggingEnabled = true;
            Assert.True(AppLogger.IsEnabled);
            Assert.True(aboutVm.DiagnosticLoggingEnabled);
            Assert.True(ConfigService.ReadDiagnosticLoggingEnabled(configPath));
            Assert.Contains("Diagnostic logging enabled by user", File.ReadAllText(AppLogger.CurrentLogFilePath));
        }
        finally
        {
            AppLogger.ResetForTesting();
            if (File.Exists(configPath)) File.Delete(configPath);
            if (File.Exists(configPath + ".bak")) File.Delete(configPath + ".bak");
            if (Directory.Exists(vmLogDir)) Directory.Delete(vmLogDir, recursive: true);
        }
    }
}
