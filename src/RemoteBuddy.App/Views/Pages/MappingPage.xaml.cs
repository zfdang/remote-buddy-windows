// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.Views.Pages;

public partial class MappingPage : UserControl
{
    private MappingViewModel? _mappingViewModel;
    private readonly VoiceShortcutRecorder _shortcutRecorder = new();

    public MappingPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _mappingViewModel = DataContext as MappingViewModel;
        if (_mappingViewModel != null)
            _mappingViewModel.HardwareButtonPressed += AnimateHardwareButton;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mappingViewModel != null)
            _mappingViewModel.HardwareButtonPressed -= AnimateHardwareButton;
        _mappingViewModel = null;
    }

    private void AnimateHardwareButton(RemoteButton button)
    {
        var control = FindVisibleButton(RemoteButtons, button.ToString());
        if (control != null)
        {
            var press = (Storyboard)FindResource("RemoteButtonPress");
            press.Begin(control, HandoffBehavior.SnapshotAndReplace, isControllable: false);
        }
    }

    private static Button? FindVisibleButton(FrameworkElement element, string tag)
    {
        if (!element.IsVisible) return null;
        if (element is Button button && Equals(button.Tag, tag)) return button;

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<FrameworkElement>())
        {
            var match = FindVisibleButton(child, tag);
            if (match != null) return match;
        }
        return null;
    }

    private static void FlushFocusedTextBoxBinding()
    {
        if (Keyboard.FocusedElement is TextBox tb)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }

    private void FlushFocusedInput(object sender, MouseButtonEventArgs e)
    {
        FlushFocusedTextBoxBinding();
    }

    private void OnDeviceSelectorPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        FlushFocusedTextBoxBinding();
        Keyboard.ClearFocus();
    }

    private void OnDeviceSelectorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is ComboBox selector && e.Key == Key.Escape && selector.IsDropDownOpen)
        {
            selector.IsDropDownOpen = false;
        }

        // Prevent keyboard/remote arrow or character keys from switching remotes via the selector.
        e.Handled = true;
    }

    private void OnDeviceSelectorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearDeviceSelectorFocus(sender as ComboBox);
    }

    private void OnDeviceSelectorDropDownClosed(object sender, EventArgs e)
    {
        ClearDeviceSelectorFocus(sender as ComboBox);
    }

    private void ClearDeviceSelectorFocus(ComboBox? selector)
    {
        if (selector == null) return;

        void ReleaseFocus()
        {
            if (FocusManager.GetFocusScope(selector) is { } scope &&
                (ReferenceEquals(FocusManager.GetFocusedElement(scope), selector) || selector.IsKeyboardFocusWithin))
            {
                FocusManager.SetFocusedElement(scope, null);
            }

            if (selector.IsKeyboardFocusWithin || ReferenceEquals(Keyboard.FocusedElement, selector))
            {
                Keyboard.ClearFocus();
            }
        }

        ReleaseFocus();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)ReleaseFocus);
    }

    private void SelectRemoteButton(object sender, RoutedEventArgs e)
    {
        FlushFocusedTextBoxBinding();
        if (DataContext is MappingViewModel vm && sender is Button { Tag: string tag })
            vm.SelectButton(tag);
    }

    private void ConfirmResetAllMappings(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MappingViewModel { SelectedDevice: { } device } vm) return;
        string message = $"将「{device.Name}」的所有按键恢复为默认映射？\n\n此遥控器的单击、长按和双击自定义映射都会被覆盖，且无法撤销。";
        var owner = Window.GetWindow(this);
        const string title = "恢复所有按键的默认映射";
        var mainVm = (owner?.DataContext ?? Application.Current?.MainWindow?.DataContext) as MainViewModel;
        if (mainVm != null) mainVm.IsModalConfirmationOpen = true;
        MessageBoxResult result;
        try
        {
            result = owner == null
                ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        }
        finally
        {
            if (mainVm != null) mainVm.IsModalConfirmationOpen = false;
        }
        if (result == MessageBoxResult.Yes && ReferenceEquals(vm.SelectedDevice, device))
            vm.ResetToDefaults();
    }

    private void OnRecordShortcutChecked(object sender, RoutedEventArgs e)
    {
        _shortcutRecorder.Reset();
        if (sender is ToggleButton recorder)
        {
            recorder.Focus();
        }
    }

    private void RecordShortcut(object sender, KeyEventArgs e)
    {
        if (sender is not ToggleButton { IsChecked: true, DataContext: MappingGestureViewModel gesture } recorder) return;
        e.Handled = true;

        if (_shortcutRecorder.OnKeyDown(e.Key, e.SystemKey, Keyboard.Modifiers, out var captured, out bool cancelled))
        {
            if (cancelled)
            {
                recorder.IsChecked = false;
            }
            else if (!string.IsNullOrEmpty(captured))
            {
                gesture.Shortcut = captured;
                recorder.IsChecked = false;
            }
        }
    }

    private void RecordShortcutKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not ToggleButton { IsChecked: true, DataContext: MappingGestureViewModel gesture } recorder) return;
        e.Handled = true;

        if (_shortcutRecorder.OnKeyUp(e.Key, e.SystemKey, out var captured) && !string.IsNullOrEmpty(captured))
        {
            gesture.Shortcut = captured;
            recorder.IsChecked = false;
        }
    }

    private void StopRecordingShortcut(object sender, KeyboardFocusChangedEventArgs e)
    {
        _shortcutRecorder.Reset();
        if (sender is ToggleButton recorder) recorder.IsChecked = false;
    }

    private void ApplyShortcutPreset(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: KeyValuePair<string, string> preset, DataContext: MappingGestureViewModel gesture } picker)
        {
            gesture.Shortcut = preset.Value;
            picker.SelectedIndex = -1;
        }
    }

    private void SelectSingleClickFromStatus(object sender, MouseButtonEventArgs e)
    {
        FlushFocusedTextBoxBinding();
        if (DataContext is MappingViewModel vm) vm.SelectedTrigger = ButtonTrigger.SingleClick;
    }

    private void SelectLongPressFromStatus(object sender, MouseButtonEventArgs e)
    {
        FlushFocusedTextBoxBinding();
        if (DataContext is MappingViewModel vm) vm.SelectedTrigger = ButtonTrigger.LongPress;
    }

    private void SelectDoubleClickFromStatus(object sender, MouseButtonEventArgs e)
    {
        FlushFocusedTextBoxBinding();
        if (DataContext is MappingViewModel vm) vm.SelectedTrigger = ButtonTrigger.DoubleClick;
    }
}
