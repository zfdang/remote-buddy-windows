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
    private MultiRemoteBluetoothManager? _bluetoothManager;
    private MainViewModel? _mainViewModel;
    private Mutex? _instanceMutex;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppLogger.Initialize();
        AppLogger.Info($"=== Application Starting (PID: {Environment.ProcessId}) ===", "App");
        AppLogger.Info($"OS: {Environment.OSVersion}, .NET Runtime: {Environment.Version}, Args: [{string.Join(", ", e.Args)}]", "App");

        if (TryRunChromecastWorker(e.Args))
        {
            Shutdown();
            return;
        }

        var mutex = new Mutex(false, @"Local\RemoteBuddy.Windows.Main");
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                AppLogger.Info("Remote Buddy is already running in this Windows session.", "App");
                Shutdown(0);
                return;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous instance stopped without releasing ownership.
        }
        _instanceMutex = mutex;

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
        _bluetoothManager = new MultiRemoteBluetoothManager();
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

        if (_configService.Current.HasCompletedOnboarding)
        {
            foreach (var device in _configService.Current.Devices.Where(d => d.BluetoothAddress != 0).DistinctBy(d => d.BluetoothAddress))
            {
                _bluetoothManager.SetDeviceVoiceSettings(device);
                _ = _bluetoothManager.ConnectAsync(device.BluetoothAddress);
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
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    internal static bool TryRunChromecastWorker(string[] args, Action<string, int, EtwButtonSource[]>? workerRunner = null)
    {
        if (args.Length < 3 || args[0] != "--chromecast-worker") return false;

        string pipeName = args[1];
        int parentPid = 0;
        string encodedSources;
        if (args.Length >= 4 && int.TryParse(args[2], out int parsedPid))
        {
            parentPid = parsedPid;
            encodedSources = args[3];
        }
        else
        {
            encodedSources = args[2];
        }

        try
        {
            var sources = System.Text.Json.JsonSerializer.Deserialize<EtwButtonSource[]>(
                Convert.FromBase64String(encodedSources));
            if (sources is { Length: > 0 })
            {
                if (workerRunner != null)
                    workerRunner(pipeName, parentPid, sources);
                else
                    ChromecastWorkerHost.RunWorker(pipeName, parentPid, sources);
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            AppLogger.Warn("Invalid ETW worker arguments", "App");
        }
        return true;
    }
}
