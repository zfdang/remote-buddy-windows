// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using System.Windows.Controls;

namespace RemoteBuddy.App.Views.Pages;

public partial class AppIntegrationPage : UserControl
{
    public AppIntegrationPage() : this(null)
    {
    }

    internal AppIntegrationPage(ResourceDictionary? testResources)
    {
        if (testResources != null)
        {
            Resources.MergedDictionaries.Add(testResources);
        }
        InitializeComponent();
    }
}
