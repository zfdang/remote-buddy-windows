// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;
using Xunit;

namespace RemoteBuddy.Tests;

public sealed class ButtonActionCatalogTests
{
    [Fact]
    public void VisibleCategories_IncludesDirectPassthroughOnlyForDirectionalSingleClick()
    {
        Assert.Contains(ButtonActionCategory.DirectPassthrough,
            ButtonActionCatalog.GetVisibleCategories(RemoteButton.Up, ButtonTrigger.SingleClick));
        Assert.DoesNotContain(ButtonActionCategory.DirectPassthrough,
            ButtonActionCatalog.GetVisibleCategories(RemoteButton.Up, ButtonTrigger.DoubleClick));
        Assert.DoesNotContain(ButtonActionCategory.DirectPassthrough,
            ButtonActionCatalog.GetVisibleCategories(RemoteButton.Up, ButtonTrigger.LongPress));
        Assert.DoesNotContain(ButtonActionCategory.DirectPassthrough,
            ButtonActionCatalog.GetVisibleCategories(RemoteButton.Home, ButtonTrigger.SingleClick));
    }

    [Fact]
    public void PickerActions_FiltersPresetApplicationsByInstallationAndPlacesCustomLast()
    {
        var installed = new HashSet<PresetApplication> { PresetApplication.WeChat, PresetApplication.VSCode };
        var actions = ButtonActionCatalog.GetPickerActions(
            installed,
            currentAction: ButtonAction.OpenCursor,
            button: RemoteButton.Home,
            trigger: ButtonTrigger.SingleClick);

        Assert.Contains(ButtonAction.OpenWeChat, actions);
        Assert.Contains(ButtonAction.OpenVSCode, actions);
        Assert.Contains(ButtonAction.OpenCursor, actions); // Current selection retained even if uninstalled
        Assert.DoesNotContain(ButtonAction.OpenSpotify, actions);
        Assert.DoesNotContain(ButtonAction.DirectPassthrough, actions);
        Assert.DoesNotContain(ButtonAction.Disabled, actions);
        Assert.DoesNotContain(ButtonAction.RepeatSingleClick, actions);
        Assert.Equal(ButtonAction.OpenCustomApplication, actions[^1]);
    }

    [Theory]
    [InlineData("https://example.com/path", true, "example.com")]
    [InlineData("http://localhost:8080", true, "localhost")]
    [InlineData("ms-settings:bluetooth", true, "ms-settings:bluetooth")]
    [InlineData("obsidian://open?vault=notes", true, "obsidian://open?vault=notes")]
    [InlineData("file:///C:/Windows/System32/calc.exe", false, null)]
    [InlineData("javascript:alert(1)", false, null)]
    [InlineData("data:text/html,hi", false, null)]
    [InlineData("vbscript:msgbox(1)", false, null)]
    [InlineData("https://", false, null)]
    [InlineData("not a url", false, null)]
    public void CustomActionUrlPolicy_ValidatesAndFormatsSafely(string raw, bool expectedValid, string? expectedSummary)
    {
        bool valid = CustomActionUrlPolicy.TryValidateUrl(raw, out _);
        Assert.Equal(expectedValid, valid);
        if (expectedValid)
        {
            Assert.Equal(expectedSummary, CustomActionUrlPolicy.SummaryLabel(raw));
        }
    }

    [Fact]
    public void FormatActionSummary_MatchesMacOSCompactAndDetailedRules()
    {
        var passthrough = new ConfiguredButtonAction { Action = ButtonAction.DirectPassthrough };
        Assert.Equal("↑ 直通", ButtonActionCatalog.FormatActionSummary(RemoteButton.Up, ButtonTrigger.SingleClick, passthrough, passthrough));
        Assert.Equal("-", ButtonActionCatalog.FormatActionSummary(RemoteButton.Up, ButtonTrigger.DoubleClick, ConfiguredButtonAction.Disabled, passthrough));

        var copy = new ConfiguredButtonAction { Action = ButtonAction.Copy };
        Assert.Equal("复制", ButtonActionCatalog.FormatActionSummary(RemoteButton.Home, ButtonTrigger.SingleClick, copy, copy));
        Assert.Equal("复制 (Ctrl+C)", ButtonActionCatalog.FormatActionSummary(RemoteButton.Home, ButtonTrigger.SingleClick, copy, copy, detailed: true));

        var customApp = new ConfiguredButtonAction { Action = ButtonAction.OpenCustomApplication, ApplicationProfileId = "p1" };
        Assert.Equal("MyApp", ButtonActionCatalog.FormatActionSummary(RemoteButton.Home, ButtonTrigger.SingleClick, customApp, customApp, _ => "MyApp"));
    }
}
