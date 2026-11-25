// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using System.Windows.Controls;

namespace RemoteBuddy.App.Views.Pages;

public partial class IntegrationsPage : UserControl
{
    public IntegrationsPage() : this(null)
    {
    }

    internal IntegrationsPage(ResourceDictionary? testResources)
    {
        if (testResources != null)
        {
            Resources.MergedDictionaries.Add(testResources);
        }
        InitializeComponent();
    }
}
