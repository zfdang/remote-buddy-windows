// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Bluetooth;
using RemoteBuddy.Platform.Windows.Input;
using Xunit;

namespace RemoteBuddy.Tests;

public class ChromecastEtwTests
{
    private static byte[] CreateAclPacket(byte opcode, ushort attrHandle, byte[] payload)
    {
        ushort l2capLen = (ushort)(3 + payload.Length);
        ushort aclDataLen = (ushort)(4 + l2capLen);

        byte[] packet = new byte[4 + 4 + 3 + payload.Length];
        // ACL Header: handle (0x0040) | PB=2 (first non-flushable) = 0x2040
        packet[0] = 0x40;
        packet[1] = 0x20;
        // Total ACL Data Length
        packet[2] = (byte)(aclDataLen & 0xFF);
        packet[3] = (byte)((aclDataLen >> 8) & 0xFF);

        // L2CAP Header: Length and CID
        packet[4] = (byte)(l2capLen & 0xFF);
        packet[5] = (byte)((l2capLen >> 8) & 0xFF);
        packet[6] = 0x04; // CID 4 = ATT
        packet[7] = 0x00;

        // ATT Header: Opcode and Attribute Handle
        packet[8] = opcode; // 0x1B = Notification
        packet[9] = (byte)(attrHandle & 0xFF);
        packet[10] = (byte)((attrHandle >> 8) & 0xFF);

        // Payload
        Array.Copy(payload, 0, packet, 11, payload.Length);
        return packet;
    }

    [Theory]
    [InlineData(0x01, RemoteButton.Power)]
    [InlineData(0x03, RemoteButton.Up)]
    [InlineData(0x04, RemoteButton.Down)]
    [InlineData(0x05, RemoteButton.Left)]
    [InlineData(0x06, RemoteButton.Right)]
    [InlineData(0x07, RemoteButton.Ok)]
    [InlineData(0x08, RemoteButton.Mute)]
    [InlineData(0x0A, RemoteButton.Home)]
    [InlineData(0x0B, RemoteButton.Back)]
    [InlineData(0x0C, RemoteButton.VolumeUp)]
    [InlineData(0x0D, RemoteButton.VolumeDown)]
    [InlineData(0x0E, RemoteButton.YouTube)]
    [InlineData(0x0F, RemoteButton.Netflix)]
    [InlineData(0x11, RemoteButton.Input)]
    public void TryParseAclPacket_8ByteButtonPress_ReturnsCorrectButton(byte code, RemoteButton expected)
    {
        byte[] payload = [code, 0, 0, 0, 0, 0, 0, 0];
        byte[] packet = CreateAclPacket(0x1B, 0x0029, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown);

        Assert.True(success);
        Assert.True(isDown);
        Assert.Equal(expected, button);
    }

    [Theory]
    [InlineData(0x00F1, RemoteButton.Back)]
    [InlineData(0x0080, RemoteButton.VolumeUp)]
    [InlineData(0x0081, RemoteButton.VolumeDown)]
    [InlineData(0x004A, RemoteButton.Home)]
    [InlineData(0x0065, RemoteButton.Menu)]
    [InlineData(0x0028, RemoteButton.Ok)]
    [InlineData(0x0052, RemoteButton.Up)]
    [InlineData(0x0051, RemoteButton.Down)]
    [InlineData(0x0050, RemoteButton.Left)]
    [InlineData(0x004F, RemoteButton.Right)]
    public void TryParseAclPacket_Xiaomi3ByteReport_ReturnsCorrectButton(ushort usage, RemoteButton expected)
    {
        byte[] payload = [0x01, (byte)(usage & 0xFF), (byte)((usage >> 8) & 0xFF)];
        byte[] packet = CreateAclPacket(0x1B, 0x002B, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.Xiaomi);

        Assert.True(success);
        Assert.True(isDown);
        Assert.Equal(expected, button);
    }

    [Fact]
    public void TryParseAclPacket_XiaomiRelease_ReturnsNoneAndNotDown()
    {
        byte[] payload = [0x01, 0x00, 0x00];
        byte[] packet = CreateAclPacket(0x1B, 0x002B, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.Xiaomi);

        Assert.True(success);
        Assert.False(isDown);
        Assert.Equal(RemoteButton.None, button);
    }

    [Fact]
    public void TryParseAclPacket_AtvvAudioSyncOnNonHidAttribute_DoesNotDecodeAsButton()
    {
        // Reviewer reproduction: ATVV audio-sync payload 0A 00 00 00 00 52 00 on non-HID attribute (e.g. 0x003D)
        byte[] payload = [0x0A, 0x00, 0x00, 0x00, 0x00, 0x52, 0x00];
        byte[] packet = CreateAclPacket(0x1B, 0x003D, payload);

        // When G9N9N protocol is active
        bool g9Success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var g9Button, out bool g9Down, EtwProtocolKind.GoogleG9N9N);
        Assert.False(g9Success);
        Assert.Equal(RemoteButton.None, g9Button);
        Assert.False(g9Down);

        // When Xiaomi protocol is active
        bool xSuccess = ChromecastEtwReceiver.TryParseAclPacket(packet, out var xButton, out bool xDown, EtwProtocolKind.Xiaomi);
        Assert.False(xSuccess);
        Assert.Equal(RemoteButton.None, xButton);
        Assert.False(xDown);
    }

    [Fact]
    public void TryParseAclPacket_MouseMovement_DoesNotDecodeAsButton()
    {
        // Mouse report: Report ID 0x03, buttons=0, dX=0x52, dY=0x00
        byte[] payload = [0x03, 0x00, 0x52, 0x00];
        byte[] packet = CreateAclPacket(0x1B, 0x002B, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.Xiaomi);
        Assert.False(success);
        Assert.Equal(RemoteButton.None, button);
        Assert.False(isDown);
    }

    [Fact]
    public void TryParseAclPacket_8ByteRelease_ReturnsNoneAndNotDown()
    {
        byte[] payload = [0x00, 0, 0, 0, 0, 0, 0, 0];
        byte[] packet = CreateAclPacket(0x1B, 0x0029, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown);

        Assert.True(success);
        Assert.False(isDown);
        Assert.Equal(RemoteButton.None, button);
    }

    [Theory]
    [InlineData(0x0042, RemoteButton.Up)]
    [InlineData(0x0041, RemoteButton.Ok)]
    [InlineData(0x0224, RemoteButton.Back)]
    [InlineData(0x0223, RemoteButton.Home)]
    [InlineData(0x00E9, RemoteButton.VolumeUp)]
    [InlineData(0x00EA, RemoteButton.VolumeDown)]
    [InlineData(0x00E2, RemoteButton.Mute)]
    public void TryParseAclPacket_2ByteConsumerReport_ReturnsCorrectButton(ushort usage, RemoteButton expected)
    {
        byte[] payload = [(byte)(usage & 0xFF), (byte)((usage >> 8) & 0xFF)];
        byte[] packet = CreateAclPacket(0x1B, 0x0029, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown);

        Assert.True(success);
        Assert.True(isDown);
        Assert.Equal(expected, button);
    }

    [Fact]
    public void TryParseAclPacket_2ByteRelease_ReturnsNoneAndNotDown()
    {
        byte[] payload = [0x00, 0x00];
        byte[] packet = CreateAclPacket(0x1B, 0x0029, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown);

        Assert.True(success);
        Assert.False(isDown);
        Assert.Equal(RemoteButton.None, button);
    }

    [Fact]
    public void TryParseAclPacket_InvalidCid_ReturnsFalse()
    {
        byte[] payload = [0x03, 0, 0, 0, 0, 0, 0, 0];
        byte[] packet = CreateAclPacket(0x1B, 0x0029, payload);
        packet[6] = 0x05; // Change CID from 4 (ATT) to 5

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out _, out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParseAclPacket_WrongAttrHandle_ReturnsFalse()
    {
        byte[] payload = [0x03, 0, 0, 0, 0, 0, 0, 0];
        byte[] packet = CreateAclPacket(0x1B, 0x003A, payload); // 0x003A instead of 0x0029

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out _, out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParseAclPacket_NonNotificationOpcode_ReturnsFalse()
    {
        byte[] payload = [0x03, 0, 0, 0, 0, 0, 0, 0];
        byte[] packet = CreateAclPacket(0x12, 0x0029, payload); // Opcode 0x12 = Write Request, not 0x1B or 0x1D

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out _, out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParseAclPacket_TruncatedPacket_ReturnsFalse()
    {
        byte[] packet = [0x40, 0x20, 0x05, 0x00]; // Only 4 bytes

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out _, out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParseAclPacket_EmptyPayloadHeaderOnly_ReturnsFalseAndNeverReportsOk()
    {
        byte[] packet = CreateAclPacket(0x1B, 0x0029, new byte[8])[..11];

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, out bool isTruncated);

        Assert.False(success);
        Assert.True(isTruncated);
        Assert.Equal(RemoteButton.None, button);
        Assert.False(isDown);
    }

    [Fact]
    public void ProcessPacket_RequiresMatchingProbe_AndSeparatesButtonEventsFromRedaction()
    {
        using var receiver = new ChromecastEtwReceiver();
        receiver.SetSourceProbe(new Guid("00112233-4455-6677-8899-aabbccddeeff"));
        var edges = new List<(RemoteButton, bool)>();
        int redacted = 0;
        receiver.ButtonEvent += (button, isDown) => edges.Add((button, isDown));
        receiver.TruncatedPacketDetected += () => redacted++;

        byte[] request = CreateAclPacket(0x06, 0x0001,
            [0xFF, 0xFF, 0x00, 0x28, 0xFF, 0xEE, 0xDD, 0xCC, 0xBB, 0xAA, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00]);
        byte[] response = CreateAclPacket(0x01, 0x0106, [0x00, 0x0A]);
        byte[] press = CreateAclPacket(0x1B, 0x0029, [0x07, 0, 0, 0, 0, 0, 0, 0]);
        byte[] release = CreateAclPacket(0x1B, 0x0029, new byte[8]);

        receiver.ProcessPacket(2, request);
        receiver.ProcessPacket(3, response);
        receiver.ProcessPacket(3, press);
        receiver.ProcessPacket(3, press.AsSpan(0, 11));
        Assert.Empty(edges);
        Assert.Equal(0, redacted);

        receiver.ProcessPacket(4, request);
        receiver.ProcessPacket(3, response);
        receiver.ProcessPacket(3, press);
        receiver.ProcessPacket(3, press);
        receiver.ProcessPacket(3, release);
        Assert.Equal(new[] { (RemoteButton.Ok, true), (RemoteButton.Ok, false) }, edges);

        receiver.ProcessPacket(3, press.AsSpan(0, 12));
        Assert.Equal(0, redacted);
        receiver.ProcessPacket(3, press.AsSpan(0, 11));
        Assert.Equal(1, redacted);
        Assert.Equal(2, edges.Count);

        press[0]++;
        receiver.ProcessPacket(3, press);
        receiver.ProcessPacket(3, press.AsSpan(0, 11));
        Assert.Equal(2, edges.Count);
        Assert.Equal(1, redacted);

        press[0]--;
        receiver.ProcessPacket(2, [0x05, 0x04, 0x00, 0x40, 0x00, 0x13]);
        receiver.ProcessPacket(3, press);
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public void ChromecastButtonReceiver_CanInstantiateAndDispose()
    {
        using var receiver = new ChromecastButtonReceiver();
        Assert.False(receiver.IsRunning);
        receiver.Stop();
        Assert.False(receiver.IsRunning);
    }

    [Fact]
    public async Task BluetoothAdapterHelper_Restart_CustomHandler_ReturnsExpectedResult()
    {
        BluetoothAdapterHelper.CustomRestartHandler = () => Task.FromResult((true, "Test success"));
        try
        {
            var (success, msg) = await BluetoothAdapterHelper.RestartBluetoothAdapterAsync();
            Assert.True(success);
            Assert.Equal("Test success", msg);
        }
        finally
        {
            BluetoothAdapterHelper.CustomRestartHandler = null;
        }
    }
}
