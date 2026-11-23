// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.App.Views;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Platform.Windows.Bluetooth;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App;

public partial class App : Application
{
    private ConfigService? _configService;
    private WinRtBluetoothManager? _bluetoothManager;
    private MainViewModel? _mainViewModel;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppLogger.Initialize();
        AppLogger.Info($"=== Application Starting (PID: {Environment.ProcessId}) ===", "App");
        AppLogger.Info($"OS: {Environment.OSVersion}, .NET Runtime: {Environment.Version}, Args: [{string.Join(", ", e.Args)}]", "App");

        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--chromecast-worker", StringComparison.OrdinalIgnoreCase) &&
                i + 2 < e.Args.Length && Guid.TryParse(e.Args[i + 2], out var marker))
            {
                string pipeName = e.Args[i + 1];
                var protocolKind = EtwProtocolKind.GoogleG9N9N;
                if (i + 3 < e.Args.Length && Enum.TryParse<EtwProtocolKind>(e.Args[i + 3], out var parsedKind))
                {
                    protocolKind = parsedKind;
                }
                ChromecastWorkerHost.RunWorker(pipeName, marker, protocolKind);
                Shutdown(0);
                return;
            }
        }

        // Global unhandled exception handlers for diagnostic logging
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLogger.Error("AppDomain UnhandledException", ex, "App");
            }
            else
            {
                AppLogger.Error($"AppDomain UnhandledException object: {args.ExceptionObject}", component: "App");
            }
            AppLogger.Flush();
        };

        DispatcherUnhandledException += (s, args) =>
        {
            AppLogger.Error("DispatcherUnhandledException encountered", args.Exception, "App");
            AppLogger.Flush();
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            AppLogger.Error("TaskScheduler UnobservedTaskException", args.Exception, "App");
            args.SetObserved();
        };

        bool startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        // Prevent shutdown when modal onboarding dialog closes
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _configService = new ConfigService();
        _bluetoothManager = new WinRtBluetoothManager();
        _mainViewModel = new MainViewModel(_configService, _bluetoothManager);

        var mainWindow = new MainWindow { DataContext = _mainViewModel };
        MainWindow = mainWindow;

        // Ensure HWND is initialized so RawInputReceiver is active for onboarding
        new System.Windows.Interop.WindowInteropHelper(mainWindow).EnsureHandle();

        _mainViewModel.RequestOpenWizard += ShowWizardWindow;

        if (!_configService.Current.HasCompletedOnboarding)
        {
            AppLogger.Info("First-time setup detected: opening Onboarding wizard", "App");
            ShowWizardWindow(WizardMode.Full);
            if (!_configService.Current.HasCompletedOnboarding)
            {
                AppLogger.Info("First-time setup was not completed, shutting down application.", "App");
                mainWindow.IsExplicitExit = true;
                mainWindow.Close();
                Shutdown();
                return;
            }
        }

        // Auto-reconnect to previously saved remote device
        if (_configService.Current.HasCompletedOnboarding && !string.IsNullOrEmpty(_configService.Current.SelectedDeviceId))
        {
            var savedDevice = _configService.Current.Devices.FirstOrDefault(d => d.Id == _configService.Current.SelectedDeviceId);
            if (savedDevice != null && savedDevice.BluetoothAddress != 0)
            {
                AppLogger.Info($"Attempting auto-reconnect to saved device: '{savedDevice.Name}' (0x{savedDevice.BluetoothAddress:X12})", "App");
                _ = _bluetoothManager.ConnectAsync(savedDevice.BluetoothAddress);
            }
        }

        if (!startMinimized || !_configService.Current.HasCompletedOnboarding)
        {
            mainWindow.Show();
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        AppLogger.Info("Application startup sequence completed successfully", "App");
    }

    private void ShowWizardWindow(WizardMode mode = WizardMode.Full)
    {
        if (_configService == null || _bluetoothManager == null || _mainViewModel == null) return;

        AppLogger.Info($"Showing wizard dialog window: Mode={mode}", "App");
        var onboardingVm = new OnboardingViewModel(_configService, _bluetoothManager, mode);
        _mainViewModel.ActiveOnboardingVm = onboardingVm;

        var onboardingWin = new OnboardingWindow { DataContext = onboardingVm };
        if (MainWindow is { IsVisible: true })
        {
            onboardingWin.Owner = MainWindow;
        }

        onboardingVm.RequestClose += () => onboardingWin.Close();
        try
        {
            onboardingWin.ShowDialog();
            AppLogger.Info($"Wizard dialog closed. Mode={mode}, HasCompletedOnboarding={_configService.Current.HasCompletedOnboarding}", "App");
        }
        finally
        {
            onboardingVm.Dispose();
            _mainViewModel.ActiveOnboardingVm = null;
            _mainViewModel.DevicesVm.Reload();
            _mainViewModel.IntegrationsVm.Reload();
            _mainViewModel.MappingVm.LoadMappings();
            _mainViewModel.GeneralVm.RefreshState();
            _mainViewModel.UpdateVoiceKeyGuard();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info($"=== Application Exiting (ExitCode: {e.ApplicationExitCode}) ===", "App");
        _mainViewModel?.Dispose();
        _bluetoothManager?.Dispose();
        AppLogger.Flush();
        base.OnExit(e);
    }
}
