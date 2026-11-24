// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;
using Xunit;

namespace RemoteBuddy.Tests;

public class G9N9NProtocolTests
{
    [Fact]
    public void TryParseReport1_ParsesCustomButton_AtKnownIndex()
    {
        // Report ID 1, index 16 (0x10) = Custom button (0x0079)
        byte[] payload = [0x01, 0x10, 0x00];
        bool success = G9N9NProtocol.TryParseReport(payload, out var buttons);

        Assert.True(success);
        Assert.Single(buttons);
        Assert.Equal(RemoteButton.Custom, buttons[0]);
    }

    [Fact]
    public void TryParseReport1_ParsesDirectionKeys()
    {
        // Report ID 1, index 3 = Up (0x0042)
        byte[] payloadUp = [0x01, 0x03, 0x00];
        Assert.True(G9N9NProtocol.TryParseReport(payloadUp, out var upButtons));
        Assert.Single(upButtons);
        Assert.Equal(RemoteButton.Up, upButtons[0]);

        // Report ID 1, index 4 = Down (0x0043)
        byte[] payloadDown = [0x01, 0x04, 0x00];
        Assert.True(G9N9NProtocol.TryParseReport(payloadDown, out var downButtons));
        Assert.Single(downButtons);
        Assert.Equal(RemoteButton.Down, downButtons[0]);
    }

    [Fact]
    public void TryParseReport_ReturnsEmptyListOnReleaseReport_InsteadOfFailing()
    {
        // Key release with Report ID 1: [0x01, 0x00, 0x00]
        byte[] releaseReport1 = [0x01, 0x00, 0x00];
        Assert.True(G9N9NProtocol.TryParseReport(releaseReport1, out var buttons1));
        Assert.Empty(buttons1);

        // Key release with Report ID 2: [0x02, 0x00, 0x00]
        byte[] releaseReport2 = [0x02, 0x00, 0x00];
        Assert.True(G9N9NProtocol.TryParseReport(releaseReport2, out var buttons2));
        Assert.Empty(buttons2);

        // Key release without Report ID (2 bytes zero): [0x00, 0x00]
        byte[] releaseNoId = [0x00, 0x00];
        Assert.True(G9N9NProtocol.TryParseReport(releaseNoId, out var buttonsNoId));
        Assert.Empty(buttonsNoId);
    }

    [Fact]
    public void TryParseReport_RejectsXiaomiNineByteAndMousePackets()
    {
        byte[] xiaomiNineByte = [0x01, 0x00, 0x00, 0x52, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.False(G9N9NProtocol.TryParseReport(xiaomiNineByte, out _));

        byte[] xiaomiMouse = [0x03, 0x01, 0x00, 0x00];
        Assert.False(G9N9NProtocol.TryParseReport(xiaomiMouse, out _));

        byte[] t1Mouse = [0x04, 0x01, 0x00, 0x00, 0x00];
        Assert.False(G9N9NProtocol.TryParseReport(t1Mouse, out _));

        // T1 SystemControl Power [0x03, 0x01] is parsed cleanly when dispatched to T1RemoteProtocol
        byte[] t1SystemPower = [0x03, 0x01];
        Assert.True(T1RemoteProtocol.TryParseReport(t1SystemPower, out var t1Buttons, out _));
        Assert.Equal([RemoteButton.Power], t1Buttons);
    }

    [Fact]
    public void XiaomiTryParseReport_ParsesMultiKeyAndOddSlotBootKeyboardAndIgnoresModifiersAndF8ToF12()
    {
        // 8-byte boot keyboard: key in odd slot slice[3] = 0x52 (Up)
        byte[] oddSlot8 = [0x00, 0x00, 0x00, 0x52, 0x00, 0x00, 0x00, 0x00];
        Assert.True(XiaomiProtocol.TryParseReport(oddSlot8, out var oddButtons));
        Assert.Equal([RemoteButton.Up], oddButtons);

        // 9-byte boot keyboard with Report ID 1 + multi-key combo: 0x52 (Up) + 0x28 (Ok)
        byte[] combo9 = [0x01, 0x00, 0x00, 0x52, 0x28, 0x00, 0x00, 0x00, 0x00];
        Assert.True(XiaomiProtocol.TryParseReport(combo9, out var comboButtons));
        Assert.Equal([RemoteButton.Up, RemoteButton.Ok], comboButtons);

        // All-zero release (9-byte Report ID 1)
        byte[] release9 = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.True(XiaomiProtocol.TryParseReport(release9, out var releaseButtons));
        Assert.Empty(releaseButtons);

        // Modifier-only (slice[0] = LeftCtrl) does not falsely trigger buttons
        byte[] modifierOnly = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.False(XiaomiProtocol.TryParseReport(modifierOnly, out _));

        // Keyboard F8..F12 (0x41..0x45) in boot keyboard must NOT map to Consumer Ok/Up/Down/Left/Right
        for (byte fKey = 0x41; fKey <= 0x45; fKey++)
        {
            byte[] fReport = [0x01, 0x00, 0x00, fKey, 0x00, 0x00, 0x00, 0x00, 0x00];
            Assert.False(XiaomiProtocol.TryParseReport(fReport, out _));
        }

        // 8-byte boot keyboard with Ctrl+Shift (0x03) modifier + Up (0x52) must not be misrejected as mouse Report ID 3
        byte[] ctrlShiftUp8 = [0x03, 0x00, 0x52, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.True(XiaomiProtocol.TryParseReport(ctrlShiftUp8, out var ctrlShiftUpButtons));
        Assert.Equal([RemoteButton.Up], ctrlShiftUpButtons);
    }

    [Fact]
    public void TryParseReport_TwoByteNoIdPowerAndNameBoundaryRegression()
    {
        // 2-byte no-Report-ID Power [0x01, 0x00] must decode to Power, not be stripped as Report ID 1
        byte[] twoBytePower = [0x01, 0x00];
        Assert.True(G9N9NProtocol.TryParseReport(twoBytePower, out var powerButtons));
        Assert.Equal([RemoteButton.Power], powerButtons);

        // Even >=6 byte packet starting with 0x02 must not strip 0x02 as Report ID 2
        byte[] evenSix = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.False(G9N9NProtocol.TryParseReport(evenSix, out _));

        // Word-boundary Xiaomi matching: "HDMI Audio" and "Origami" must not match "mi "
        Assert.False(XiaomiProtocol.IsCandidate("HDMI Audio"));
        Assert.False(XiaomiProtocol.IsCandidate("Origami BT"));
        Assert.True(XiaomiProtocol.IsCandidate("MI Remote"));
        Assert.True(XiaomiProtocol.IsCandidate("MI"));
    }
}
