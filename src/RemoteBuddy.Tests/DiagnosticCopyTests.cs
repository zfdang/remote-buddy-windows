// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Bluetooth;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

[Collection("AppLogger")]
public class DiagnosticCopyTests : IDisposable
{
    private readonly string _tempLogDir;

    public DiagnosticCopyTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "VIB_DiagTests_" + Guid.NewGuid().ToString("N"));
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
        }
    }

    private sealed class StubBluetoothManager : IBluetoothManager
    {
        public bool IsScanning => false;
        public bool IsConnected { get; set; }
        public bool IsReady { get; set; }
        public bool IsStreaming => false;
        public float CurrentGain { get; set; } = RemoteDevice.DefaultMicrophoneGain;
        public BluetoothSessionState SessionState => BluetoothSessionState.Idle;
        public string ConnectedDeviceName { get; set; } = string.Empty;
        public ulong ConnectedDeviceAddress { get; set; }
        public Guid? ConnectedDeviceContainerId => null;
        public RemoteDeviceInformation? ConnectedDeviceInformation { get; set; }
        public G9N9NVoiceButtonMode VoiceButtonMode { get; set; } = G9N9NVoiceButtonMode.SmartToggle;

        public IReadOnlyCollection<ulong> ConnectedDeviceAddresses =>
            IsConnected && ConnectedDeviceAddress != 0 ? [ConnectedDeviceAddress] : [];

        public RemoteDeviceInformation? GetDeviceInformation(ulong address) =>
            ConnectedDeviceInformation?.BluetoothAddress == address ? ConnectedDeviceInformation : null;

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

    [Fact]
    public void SanitizePath_ReplacesUserProfilePrefixCaseInsensitively()
    {
        string input = @"C:\Users\alice\AppData\Roaming\RemoteBuddy\logs\app.log";
        string sanitized = WindowsDiagnosticSnapshot.SanitizePath(input, @"c:\users\Alice\");

        Assert.Equal(@"%USERPROFILE%\AppData\Roaming\RemoteBuddy\logs\app.log", sanitized);
    }

    [Fact]
    public void AppLogger_GetRecentLogLines_CapsAt300LinesInChronologicalOrder()
    {
        for (int i = 1; i <= 350; i++)
        {
            AppLogger.Info($"DiagEntry-{i:D3}", "TestComp");
        }

        var lines = AppLogger.GetRecentLogLines();
        Assert.Equal(AppLogger.DiagnosticLogLineLimit, lines.Count);
        Assert.Contains("DiagEntry-051", lines[0]);
        Assert.Contains("DiagEntry-350", lines[^1]);
    }

    [Fact]
    public void AboutViewModel_CopyDiagnosticSummary_WritesSummaryAndRecentLogsAndAuditLog()
    {
        AppLogger.Info(@"Initialized path C:\Users\tester\AppData\Roaming\RemoteBuddy\logs\app.log", "Startup");
        var snapshot = new WindowsDiagnosticSnapshot(
            AppVersion: "v26.11.25.100",
            OsVersion: "Microsoft Windows NT 10.0.26100.0",
            Architecture: "x64 (64位)",
            IsAdministrator: false,
            RemoteConnected: true,
            ConnectedRemoteCount: 1,
            RemoteModel: "XiaomiRC003/ARN9",
            VirtualMicrophoneStatus: "Ready",
            CableOutputStatus: "Ready",
            EtwCaptureStatus: "RunningInProcess",
            VoiceTool: "WeChatInput",
            VoiceTriggerMode: "HoldToTalk",
            VoiceShortcutName: "Ctrl+F5",
            PreferredAudioInput: "CABLE Output",
            RuntimeLogEnabled: true,
            RuntimeLogAvailable: true,
            RuntimeLogPath: @"C:\Users\tester\AppData\Roaming\RemoteBuddy\logs\app.log",
            RuntimeLogSizeBytes: 2048,
            GeneratedAt: new DateTimeOffset(2026, 11, 25, 0, 47, 0, TimeSpan.Zero));

        string? copiedPayload = null;
        var vm = new AboutViewModel(
            config: null,
            fetchRelease: _ => Task.FromCanceled<App.Services.WindowsRelease>(new CancellationToken(true)),
            diagnosticSnapshotProvider: () => snapshot,
            clipboardWriter: text => copiedPayload = text);

        vm.CopyDiagnosticSummaryCommand.Execute(null);

        Assert.NotNull(copiedPayload);
        Assert.Contains("### Remote Buddy 运行环境与诊断信息 / Runtime Diagnostics", copiedPayload);
        Assert.Contains("v26.11.25.100", copiedPayload);
        Assert.Contains("XiaomiRC003/ARN9", copiedPayload);
        Assert.Contains("2 KB", copiedPayload);
        Assert.Contains("### 最近运行日志 / Recent Runtime Log", copiedPayload);
        Assert.Contains("Initialized path", copiedPayload);
        Assert.True(vm.HasDiagnosticCopyStatus);
        Assert.Contains("已复制诊断信息", vm.DiagnosticCopyStatus);

        string logContent = File.ReadAllText(AppLogger.CurrentLogFilePath);
        Assert.Contains("ABOUT DIAGNOSTICS copied app_version=v26.11.25.100", logContent);
    }

    [Fact]
    public void AboutViewModel_CopyDiagnosticSummary_SurfacesClipboardErrorGracefully()
    {
        var vm = new AboutViewModel(
            config: null,
            fetchRelease: _ => Task.FromCanceled<App.Services.WindowsRelease>(new CancellationToken(true)),
            diagnosticSnapshotProvider: null,
            clipboardWriter: _ => throw new InvalidOperationException("clipboard busy"));

        vm.CopyDiagnosticSummaryCommand.Execute(null);

        Assert.True(vm.HasDiagnosticCopyStatus);
        Assert.Contains("复制诊断信息失败", vm.DiagnosticCopyStatus);
        Assert.Contains("clipboard busy", vm.DiagnosticCopyStatus);
    }

    [Fact]
    public void MainViewModel_BuildDiagnosticSnapshot_OmitsPersonalizedDeviceNameAndCapturesHardwareModel()
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"diag-mvm-{Guid.NewGuid():N}.json");
        try
        {
            const ulong addr = 0xC05D39F86E0C;
            var config = new ConfigService(configPath);
            config.Current.SelectedDeviceId = "dev1";
            config.Current.Devices.Add(new RemoteDevice
            {
                Id = "dev1",
                BluetoothAddress = addr,
                Name = "Living Room Personal Secret Name",
                ModelType = RemoteModelType.XiaomiRC003
            });

            var bt = new StubBluetoothManager
            {
                IsConnected = true,
                IsReady = true,
                ConnectedDeviceAddress = addr,
                ConnectedDeviceName = "Living Room Personal Secret Name",
                ConnectedDeviceInformation = new RemoteDeviceInformation(addr)
                {
                    ModelNumber = "ARN9",
                    FirmwareRevision = "1.2.3"
                }
            };

            using var mvm = new MainViewModel(config, bt);
            var snapshot = mvm.BuildDiagnosticSnapshot();
            string text = snapshot.BuildSummary();

            Assert.True(snapshot.RemoteConnected);
            Assert.Contains("XiaomiRC003/ARN9 fw=1.2.3", snapshot.RemoteModel);
            Assert.DoesNotContain("Living Room Personal Secret Name", text);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }
}
