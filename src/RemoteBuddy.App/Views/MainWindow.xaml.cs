// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.App.Views.Pages;

namespace RemoteBuddy.App.Views;

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint WM_NULL = 0x0000;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 101;
    private const int WM_INPUT = 0x00FF;
    private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int GIDC_ARRIVAL = 1;
    private const int GIDC_REMOVAL = 2;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private NOTIFYICONDATA _trayData;
    private System.Drawing.Icon? _trayIcon;
    private bool _trayIconCreated = false;
    private uint _wmTaskbarCreated = 0;
    private HwndSource? _hwndSource;
    public bool IsExplicitExit { get; set; } = false;

    private GeneralPage? _generalPage;
    private EnvironmentPage? _environmentPage;
    private DevicesPage? _devicesPage;
    private IntegrationsPage? _integrationsPage;
    private MappingPage? _mappingPage;
    private CommonPhrasesPage? _commonPhrasesPage;
    private AppIntegrationPage? _appIntegrationPage;
    private AboutPage? _aboutPage;
    private SettingsPage? _settingsPage;
    private CommonPhraseOverlayWindow? _commonPhraseOverlayWindow;

    private bool _isSystemShutdown = false;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Activated += OnWindowActivatedChanged;
        Deactivated += OnWindowActivatedChanged;
        IsVisibleChanged += OnWindowVisibilityChanged;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        if (Application.Current != null)
        {
            Application.Current.SessionEnding += OnSessionEnding;
        }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _isSystemShutdown = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (DataContext is MainViewModel vm)
        {
            vm.RegisterRawInput(hwnd);
        }
        _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        InitializeTrayIcon(hwnd);
    }

    private void InitializeTrayIcon(IntPtr hwnd)
    {
        if (_trayIconCreated) return;
        try
        {
            if (_trayIcon == null)
            {
                var resource = Application.GetResourceStream(new Uri("/Resources/app.ico", UriKind.Relative));
                if (resource == null) return;
                using (resource.Stream)
                {
                    _trayIcon = new System.Drawing.Icon(resource.Stream, 32, 32);
                }
            }
            _trayData = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = (uint)WM_TRAYICON,
                hIcon = _trayIcon.Handle,
                szTip = "Remote Buddy (Windows)"
            };
            _trayIconCreated = Shell_NotifyIcon(NIM_ADD, ref _trayData);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0011 /* WM_QUERYENDSESSION */)
        {
            _isSystemShutdown = true;
            handled = true;
            return (IntPtr)1; // Allow OS shutdown
        }
        if (msg == 0x0016 /* WM_ENDSESSION */)
        {
            if (wParam != IntPtr.Zero)
            {
                _isSystemShutdown = true;
                IsExplicitExit = true;
                Close();
                Application.Current?.Shutdown();
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (_wmTaskbarCreated != 0 && (uint)msg == _wmTaskbarCreated)
        {
            _trayIconCreated = false;
            InitializeTrayIcon(hwnd);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_INPUT && DataContext is MainViewModel vm)
        {
            vm.ProcessRawInput(lParam);
        }
        else if (msg == WM_INPUT_DEVICE_CHANGE && DataContext is MainViewModel vmDevice)
        {
            int change = wParam.ToInt32();
            if (change == GIDC_ARRIVAL)
            {
                vmDevice.HandleDeviceArrival(lParam);
            }
            else if (change == GIDC_REMOVAL)
            {
                vmDevice.HandleDeviceRemoval(lParam);
            }
            handled = true;
            return IntPtr.Zero;
        }
        else if (msg == WM_POWERBROADCAST && DataContext is MainViewModel vmPower)
        {
            int powerEvent = wParam.ToInt32();
            if (powerEvent == PBT_APMSUSPEND)
                vmPower.CancelPendingAppFocus();
            if (powerEvent == PBT_APMRESUMEAUTOMATIC || powerEvent == PBT_APMRESUMESUSPEND)
            {
                vmPower.HandleSystemResume();
            }
        }
        else if (msg == WM_TRAYICON)
        {
            int eventId = lParam.ToInt32();
            if (eventId == WM_LBUTTONUP || eventId == WM_LBUTTONDBLCLK)
            {
                RestoreWindow();
                handled = true;
            }
            else if (eventId == WM_RBUTTONUP)
            {
                ShowTrayContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }
    }

    private void ShowTrayContextMenu()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var pt)) return;

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            const uint cmdShow = 1;
            const uint cmdExit = 2;
            AppendMenuW(menu, MF_STRING, (UIntPtr)cmdShow, "显示主界面");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)cmdExit, "退出");

            SetForegroundWindow(hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.X, pt.Y, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd == (int)cmdShow)
            {
                RestoreWindow();
            }
            else if (cmd == (int)cmdExit)
            {
                IsExplicitExit = true;
                Close();
                Application.Current?.Shutdown();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (IsExplicitExit || _isSystemShutdown) return;

        if (DataContext is MainViewModel vm && vm.GeneralVm.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Activated -= OnWindowActivatedChanged;
        Deactivated -= OnWindowActivatedChanged;
        IsVisibleChanged -= OnWindowVisibilityChanged;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        if (Application.Current != null)
        {
            Application.Current.SessionEnding -= OnSessionEnding;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.IsMainWindowActive = false;
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.RequestToggleCommonPhraseOverlay -= OnRequestToggleCommonPhraseOverlay;
            vm.RequestCloseCommonPhraseOverlay -= OnRequestCloseCommonPhraseOverlay;
            vm.RequestActivateMainWindow -= RestoreWindow;
        }
        OnRequestCloseCommonPhraseOverlay();

        if (_trayIconCreated)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _trayData);
            _trayIconCreated = false;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            _generalPage = new GeneralPage { DataContext = vm.GeneralVm };
            _environmentPage = new EnvironmentPage { DataContext = vm.EnvironmentVm };
            _devicesPage = new DevicesPage { DataContext = vm.DevicesVm };
            _integrationsPage = new IntegrationsPage { DataContext = vm.IntegrationsVm };
            _mappingPage = new MappingPage { DataContext = vm.MappingVm };
            _commonPhrasesPage = new CommonPhrasesPage { DataContext = vm.CommonPhrasesVm };
            _appIntegrationPage = new AppIntegrationPage { DataContext = vm.AppIntegrationVm };
            _aboutPage = new AboutPage { DataContext = vm.AboutVm };
            _settingsPage = new SettingsPage { DataContext = vm.SettingsVm };

            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.RequestToggleCommonPhraseOverlay += OnRequestToggleCommonPhraseOverlay;
            vm.RequestCloseCommonPhraseOverlay += OnRequestCloseCommonPhraseOverlay;
            vm.RequestActivateMainWindow += RestoreWindow;
            SwitchPage(vm.ActivePageName);
            UpdateWindowActiveState();
        }
    }

    private void OnRequestToggleCommonPhraseOverlay(Core.Models.RemoteModelType? model, Core.Config.CommonPhraseStore store)
    {
        if (DataContext is not MainViewModel vm) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_commonPhraseOverlayWindow == null)
            {
                _commonPhraseOverlayWindow = new CommonPhraseOverlayWindow(
                    model,
                    store,
                    button => vm.InsertCommonPhrase(button),
                    () => vm.CloseCommonPhraseOverlay());
            }
            else
            {
                _commonPhraseOverlayWindow.BindContent(model, store);
            }
            _commonPhraseOverlayWindow.Show();
        });
    }

    private void OnRequestCloseCommonPhraseOverlay()
    {
        if (_commonPhraseOverlayWindow == null) return;
        var win = _commonPhraseOverlayWindow;
        _commonPhraseOverlayWindow = null;
        if (Dispatcher.CheckAccess())
        {
            win.Close();
        }
        else
        {
            Dispatcher.BeginInvoke(win.Close);
        }
    }

    private void OnWindowActivatedChanged(object? sender, EventArgs e)
    {
        UpdateWindowActiveState();
    }

    private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateWindowActiveState();
    }

    private void UpdateWindowActiveState()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsMainWindowActive = IsVisible && IsActive;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActivePageName) && sender is MainViewModel vm)
        {
            SwitchPage(vm.ActivePageName);
        }
    }

    private void SwitchPage(string pageName)
    {
        PageContentContainer.Content = pageName switch
        {
            "General" => _generalPage,
            "Permissions" or "Environment" => _environmentPage,
            "Devices" or "Connection" => _devicesPage,
            "Integrations" => _integrationsPage,
            "Mapping" => _mappingPage,
            "CommonPhrases" => _commonPhrasesPage,
            "AppIntegration" => _appIntegrationPage,
            "About" => _aboutPage,
            "Settings" => _settingsPage,
            _ => _generalPage
        };
    }
}
