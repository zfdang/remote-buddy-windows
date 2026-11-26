// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.App.Views.Pages;

public partial class MappingPage : UserControl
{
    private MappingViewModel? _mappingViewModel;
    private readonly VoiceShortcutRecorder _shortcutRecorder = new();

    public MappingPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        AttachViewModel(DataContext as MappingViewModel);
        MappingCanvasControl.DataContext = DataContext;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as MappingViewModel);
        MappingCanvasControl.DataContext = DataContext;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void AttachViewModel(MappingViewModel? vm)
    {
        if (vm == null || ReferenceEquals(_mappingViewModel, vm)) return;
        _mappingViewModel = vm;
        _mappingViewModel.RequestOpenShortcutPicker += OnRequestOpenShortcutPicker;
        _mappingViewModel.RequestScrollEditorIntoView += OnRequestScrollEditorIntoView;
    }

    private void DetachViewModel()
    {
        if (_mappingViewModel == null) return;
        _mappingViewModel.RequestOpenShortcutPicker -= OnRequestOpenShortcutPicker;
        _mappingViewModel.RequestScrollEditorIntoView -= OnRequestScrollEditorIntoView;
        _mappingViewModel = null;
    }

    private void OnRequestScrollEditorIntoView()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ActionEditorCard.IsVisible)
            {
                ActionEditorCard.BringIntoView();
            }
        });
    }

    private void OnRequestOpenShortcutPicker(string? currentShortcut)
    {
        if (_mappingViewModel == null) return;
        var owner = Window.GetWindow(this);
        var dialog = new ShortcutPickerWindow(currentShortcut) { Owner = owner };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedShortcut))
        {
            _mappingViewModel.ApplyCustomShortcut(dialog.SelectedShortcut);
        }
    }

    private void OpenShortcutPickerDialog(object sender, RoutedEventArgs e)
    {
        OnRequestOpenShortcutPicker(_mappingViewModel?.EditingConfiguredAction.Shortcut);
    }

    private static void FlushFocusedTextBoxBinding()
    {
        if (Keyboard.FocusedElement is TextBox tb)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
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
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PageScrollViewer?.ScrollToTop());
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

    private void OnCategoryButtonClick(object sender, RoutedEventArgs e)
    {
        if (_mappingViewModel != null && sender is Button { Tag: ButtonActionCategory category })
        {
            _mappingViewModel.SelectCategory(category);
        }
    }

    private void OnActionOptionClick(object sender, RoutedEventArgs e)
    {
        if (_mappingViewModel != null && sender is Button { Tag: ButtonAction action })
        {
            _mappingViewModel.SelectAction(action);
        }
    }

    private void ChooseCustomApplication(object sender, RoutedEventArgs e)
    {
        if (_mappingViewModel == null) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择应用程序",
            Filter = "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        var owner = Window.GetWindow(this);
        bool? picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (picked == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            _mappingViewModel.AddCustomApplication(dialog.FileName);
        }
    }

    private void OnSelectCustomAppClick(object sender, RoutedEventArgs e)
    {
        if (_mappingViewModel != null && sender is Button { Tag: string id })
        {
            _mappingViewModel.SelectCustomApplication(id);
        }
    }

    private void OnDeleteCustomAppClick(object sender, RoutedEventArgs e)
    {
        if (_mappingViewModel != null && sender is Button { Tag: string id })
        {
            _mappingViewModel.DeleteCustomApplication(id);
        }
    }

    private void OnSelectCustomAppStrategyNone(object sender, RoutedEventArgs e)
    {
        _mappingViewModel?.SetCustomAppFocusStrategy(CustomApplicationFocusStrategy.None);
    }

    private void OnSelectCustomAppStrategyShortcut(object sender, RoutedEventArgs e)
    {
        _mappingViewModel?.SetCustomAppFocusStrategy(CustomApplicationFocusStrategy.KeyboardShortcut);
    }

    private void ConfirmResetAllMappings(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MappingViewModel { SelectedDevice: { } device } vm) return;
        string message = $"此操作将把「{device.Name}」的所有按键动作恢复为出厂默认设置，已录入的自定义快捷键将被清空。此操作无法撤销。";
        var owner = Window.GetWindow(this);
        const string title = "确认恢复所有默认设置？";
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
        {
            vm.ResetToDefaults();
        }
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
        if (sender is not ToggleButton { IsChecked: true } recorder || _mappingViewModel == null) return;
        e.Handled = true;

        if (_shortcutRecorder.OnKeyDown(e.Key, e.SystemKey, Keyboard.Modifiers, out var captured, out bool cancelled))
        {
            if (cancelled)
            {
                recorder.IsChecked = false;
            }
            else if (!string.IsNullOrEmpty(captured))
            {
                _mappingViewModel.ApplyCustomShortcut(captured);
                recorder.IsChecked = false;
            }
        }
    }

    private void RecordShortcutKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not ToggleButton { IsChecked: true } recorder || _mappingViewModel == null) return;
        e.Handled = true;

        if (_shortcutRecorder.OnKeyUp(e.Key, e.SystemKey, out var captured) && !string.IsNullOrEmpty(captured))
        {
            _mappingViewModel.ApplyCustomShortcut(captured);
            recorder.IsChecked = false;
        }
    }

    private void StopRecordingShortcut(object sender, KeyboardFocusChangedEventArgs e)
    {
        _shortcutRecorder.Reset();
        if (sender is ToggleButton recorder) recorder.IsChecked = false;
    }
}
