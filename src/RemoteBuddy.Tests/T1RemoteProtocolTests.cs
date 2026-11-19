// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;
using Xunit;

namespace RemoteBuddy.Tests;

public class T1RemoteProtocolTests
{
    [Theory]
    [InlineData("t1-remote", true)]
    [InlineData("T1 Remote", true)]
    [InlineData("t1_remote", true)]
    [InlineData("t1", true)]
    [InlineData("T1", true)]
    [InlineData("Xiaomi Remote", false)]
    [InlineData("Chromecast Remote", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCandidate_MatchesExpectedNames(string? name, bool expected)
    {
        Assert.Equal(expected, T1RemoteProtocol.IsCandidate(name));
    }

    [Fact]
    public void Report1_DecodesMenuAndVoiceHold()
    {
        // Report 1 with Menu key (0x65)
        byte[] menuReport = [1, 0, 0, 0x65, 0, 0, 0, 0, 0];
        Assert.True(T1RemoteProtocol.TryParseReport(menuReport, out var buttons, out bool voiceHold));
        Assert.Single(buttons);
        Assert.Equal(RemoteButton.Menu, buttons[0]);
        Assert.False(voiceHold);

        // Report 1 with Voice Hold (0xAA)
        byte[] voiceReport = [1, 0, 0, 0xAA, 0, 0, 0, 0, 0];
        Assert.True(T1RemoteProtocol.TryParseReport(voiceReport, out buttons, out voiceHold));
        Assert.Empty(buttons);
        Assert.True(voiceHold);
    }

    [Fact]
    public void Report1_PassesThroughOkAndArrowKeys_WithoutCustomMapping()
    {
        // Report 1 with OK / Enter (0x28)
        byte[] okReport = [1, 0, 0, 0x28, 0, 0, 0, 0, 0];
        var result = T1RemoteProtocol.DecodeReport(okReport);
        Assert.Equal(T1RemoteProtocol.DecodeStatus.Usages, result.Status);
        Assert.Empty(result.Buttons); // Not intercepted as custom mapped button
        Assert.Contains(RemoteButton.Ok, result.PassthroughButtons); // Observed passthrough
    }

    [Fact]
    public void Report2_DecodesConsumerControlButtons()
    {
        // Back (0x0224)
        byte[] backReport = [2, 0x24, 0x02];
        Assert.True(T1RemoteProtocol.TryParseReport(backReport, out var buttons, out _));
        Assert.Equal([RemoteButton.Back], buttons);

        // Home (0x0223)
        byte[] homeReport = [2, 0x23, 0x02];
        Assert.True(T1RemoteProtocol.TryParseReport(homeReport, out buttons, out _));
        Assert.Equal([RemoteButton.Home], buttons);

        // Mute (0x00E2)
        byte[] muteReport = [2, 0xE2, 0x00];
        Assert.True(T1RemoteProtocol.TryParseReport(muteReport, out buttons, out _));
        Assert.Equal([RemoteButton.Mute], buttons);

        // VolumeUp (0x00E9)
        byte[] volUpReport = [2, 0xE9, 0x00];
        Assert.True(T1RemoteProtocol.TryParseReport(volUpReport, out buttons, out _));
        Assert.Equal([RemoteButton.VolumeUp], buttons);

        // VolumeDown (0x00EA)
        byte[] volDownReport = [2, 0xEA, 0x00];
        Assert.True(T1RemoteProtocol.TryParseReport(volDownReport, out buttons, out _));
        Assert.Equal([RemoteButton.VolumeDown], buttons);

        // Release (0x0000)
        byte[] releaseReport = [2, 0x00, 0x00];
        Assert.True(T1RemoteProtocol.TryParseReport(releaseReport, out buttons, out _));
        Assert.Empty(buttons);
    }

    [Fact]
    public void Report3_DecodesPowerButton()
    {
        // System power (0x01)
        byte[] powerReport = [3, 0x01];
        Assert.True(T1RemoteProtocol.TryParseReport(powerReport, out var buttons, out _));
        Assert.Equal([RemoteButton.Power], buttons);

        // Power release (0x00)
        byte[] powerRelease = [3, 0x00];
        Assert.True(T1RemoteProtocol.TryParseReport(powerRelease, out buttons, out _));
        Assert.Empty(buttons);
    }

    [Fact]
    public void Report4AndVendorReport_AreIgnored_WithoutError()
    {
        byte[] mouseReport = [4, 0x00, 0x10, 0x20];
        var mouseResult = T1RemoteProtocol.DecodeReport(mouseReport);
        Assert.Equal(T1RemoteProtocol.DecodeStatus.Ignore, mouseResult.Status);

        byte[] vendorReport = [0x5A, 0x01, 0x02, 0x03];
        var vendorResult = T1RemoteProtocol.DecodeReport(vendorReport);
        Assert.Equal(T1RemoteProtocol.DecodeStatus.Ignore, vendorResult.Status);
    }
}
