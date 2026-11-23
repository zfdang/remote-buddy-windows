// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.App.Views.Pages;

public partial class EnvironmentPage : UserControl
{
    public EnvironmentPage()
    {
        InitializeComponent();
    }

    private void OnDownloadLinkRequested(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            AppLogger.Warn($"Could not open the VB-Audio download page: {ex.Message}", nameof(EnvironmentPage));
        }
    }
}
