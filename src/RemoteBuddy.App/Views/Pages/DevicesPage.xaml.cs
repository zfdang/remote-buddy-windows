// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Windows;
using System.Windows.Controls;

namespace RemoteBuddy.App.Views.Pages;

public partial class DevicesPage : UserControl
{
    public DevicesPage() : this(null)
    {
    }

    internal DevicesPage(ResourceDictionary? testResources)
    {
        if (testResources != null)
        {
            Resources.MergedDictionaries.Add(testResources);
        }
        InitializeComponent();
    }
}
