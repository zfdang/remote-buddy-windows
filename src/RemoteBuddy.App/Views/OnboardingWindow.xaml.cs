// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.App.Views;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        if (Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.RegisterRawInput(hwnd);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x00FF /* WM_INPUT */)
        {
            if (Application.Current?.MainWindow?.DataContext is MainViewModel vm)
            {
                vm.ProcessRawInput(lParam);
            }
        }
        else if (msg == 0x0319 /* WM_APPCOMMAND */)
        {
            if (DataContext is OnboardingViewModel onboardingVm && onboardingVm.CurrentWizardStep == WizardStep.ButtonTest &&
                !onboardingVm.IsTestingChromecast)
            {
                int cmd = ((int)lParam >> 16) & ~0xF000;
                var appBtn = cmd switch
                {
                    1 => RemoteButton.Back,        // APPCOMMAND_BROWSER_BACK
                    8 => RemoteButton.Mute,        // APPCOMMAND_VOLUME_MUTE
                    9 => RemoteButton.VolumeDown,  // APPCOMMAND_VOLUME_DOWN
                    10 => RemoteButton.VolumeUp,   // APPCOMMAND_VOLUME_UP
                    14 => RemoteButton.PlayPause,  // APPCOMMAND_MEDIA_PLAY_PAUSE
                    _ => RemoteButton.None
                };
                if (appBtn != RemoteButton.None)
                {
                    onboardingVm.NotifyButtonPressed(appBtn);
                    handled = true;
                    return (IntPtr)1;
                }
            }
        }
        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Application.Current?.MainWindow is { } mw)
        {
            var hwnd = new WindowInteropHelper(mw).Handle;
            if (mw.DataContext is MainViewModel mainVm)
            {
                mainVm.RegisterRawInput(hwnd);
            }
        }
        if (DataContext is OnboardingViewModel vm)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.Dispose();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm && vm.CurrentWizardStep == WizardStep.ButtonTest &&
            !vm.IsTestingChromecast)
        {
            // Do not swallow Escape or Tab so dialog cancellation and navigation still work.
            if (e.Key == Key.Escape || e.Key == Key.Tab)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            var button = e.Key switch
            {
                Key.Up => RemoteButton.Up,
                Key.Down => RemoteButton.Down,
                Key.Left => RemoteButton.Left,
                Key.Right => RemoteButton.Right,
                Key.Return or Key.Enter or Key.Select or Key.Execute => RemoteButton.Ok,
                Key.Back or Key.BrowserBack => RemoteButton.Back,
                Key.Home or Key.BrowserHome => RemoteButton.Home,
                Key.VolumeUp => RemoteButton.VolumeUp,
                Key.VolumeDown => RemoteButton.VolumeDown,
                Key.VolumeMute => RemoteButton.Mute,
                Key.MediaPlayPause => RemoteButton.PlayPause,
                _ => RemoteButton.None
            };

            if (button != RemoteButton.None)
            {
                vm.NotifyButtonPressed(button);
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            UpdatePanels(vm.CurrentWizardStep);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingViewModel.CurrentStep) && sender is OnboardingViewModel vm)
        {
            UpdatePanels(vm.CurrentWizardStep);
        }
    }

    private void UpdatePanels(WizardStep step)
    {
        Step1Panel.Visibility = step == WizardStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == WizardStep.DriverCheck ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == WizardStep.DeviceConnect ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = step == WizardStep.ButtonTest ? Visibility.Visible : Visibility.Collapsed;
        Step5Panel.Visibility = step == WizardStep.AudioTest ? Visibility.Visible : Visibility.Collapsed;
        Step6Panel.Visibility = step == WizardStep.ImeConfig ? Visibility.Visible : Visibility.Collapsed;
        Step7Panel.Visibility = step == WizardStep.TranscriptionVerify ? Visibility.Visible : Visibility.Collapsed;
        Step8Panel.Visibility = step == WizardStep.Complete ? Visibility.Visible : Visibility.Collapsed;
    }
}
