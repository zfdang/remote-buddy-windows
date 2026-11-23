// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Models;

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

    private void SelectRemoteButton(object sender, RoutedEventArgs e)
    {
        if (DataContext is MappingViewModel vm && sender is Button { Tag: string tag } &&
            Enum.TryParse<RemoteButton>(tag, out var button))
            vm.SelectedMapping = vm.CurrentMappings.FirstOrDefault(item => item.Button == button);
    }
}
