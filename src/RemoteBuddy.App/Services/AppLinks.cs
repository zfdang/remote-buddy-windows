// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;

namespace RemoteBuddy.App.Services;

internal static class AppLinks
{
    internal const string WebsiteUrl = "https://remote.zfdang.com";
    internal const string GitHubUrl = "https://github.com/zfdang/remote-buddy-windows";
    internal const string DevicesUrl = "https://remote.zfdang.com/device.html";
    internal const string InputMethodsFaqUrl = "https://remote.zfdang.com/faq.html#input-methods";
    internal const string ApplicationIntegrationFaqUrl = "https://remote.zfdang.com/faq.html#integrations";

    internal static void OpenExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
