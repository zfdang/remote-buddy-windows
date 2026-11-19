// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows.Controls;
using System.Windows;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.App.Views.Pages;

public partial class MappingPage : UserControl
{
    public MappingPage()
    {
        InitializeComponent();
    }

    private void SelectRemoteButton(object sender, RoutedEventArgs e)
    {
        if (DataContext is MappingViewModel vm && sender is Button { Tag: string tag } &&
            Enum.TryParse<RemoteButton>(tag, out var button))
            vm.SelectedMapping = vm.CurrentMappings.FirstOrDefault(item => item.Button == button);
    }
}
