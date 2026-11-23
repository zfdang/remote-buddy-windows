// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.Views.Pages;

public partial class MappingPage : UserControl
{
    private MappingViewModel? _mappingViewModel;

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
        var result = owner == null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes && ReferenceEquals(vm.SelectedDevice, device))
            vm.ResetToDefaults();
    }

    private void RecordShortcut(object sender, KeyEventArgs e)
    {
        if (sender is not ToggleButton { IsChecked: true, DataContext: MappingGestureViewModel gesture } recorder) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            recorder.IsChecked = false;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(FormatRecordedKey(key));

        var shortcut = string.Join("+", parts);
        if (!NativeInputSender.TryParseShortcut(shortcut, out _, out _)) return;
        gesture.Shortcut = shortcut;
        recorder.IsChecked = false;
    }

    private static string FormatRecordedKey(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString();
        return key switch
        {
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            _ => key.ToString()
        };
    }

    private void StopRecordingShortcut(object sender, KeyboardFocusChangedEventArgs e)
    {
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
