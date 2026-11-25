// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Platform.Windows.Bluetooth;
using RemoteBuddy.Platform.Windows.Input;
using Xunit;

namespace RemoteBuddy.Tests;

public class ChromecastEtwTests
{
    private static byte[] CreateAclPacket(byte opcode, ushort attrHandle, byte[] payload, ushort connectionHandle = 0x0040)
    {
        ushort l2capLen = (ushort)(3 + payload.Length);
        ushort aclDataLen = (ushort)(4 + l2capLen);

        byte[] packet = new byte[4 + 4 + 3 + payload.Length];
        // ACL Header: handle | PB=2 (first non-flushable) = 0x2000 | handle
        packet[0] = (byte)(connectionHandle & 0xFF);
        packet[1] = (byte)(0x20 | ((connectionHandle >> 8) & 0x0F));
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
    [InlineData(0x03, RemoteButton.Up)]
    [InlineData(0x04, RemoteButton.Down)]
    [InlineData(0x05, RemoteButton.Left)]
    [InlineData(0x06, RemoteButton.Right)]
    [InlineData(0x07, RemoteButton.Ok)]
    [InlineData(0x0A, RemoteButton.Home)]
    [InlineData(0x0B, RemoteButton.Back)]
    public void TryParseAclPacket_G9N9N3ByteReport_ReturnsCorrectButton(byte code, RemoteButton expected)
    {
        // G9N9N Report ID 1: [0x01, code, 0x00]
        byte[] payload = [0x01, code, 0x00];
        byte[] packet = CreateAclPacket(0x1B, 0x0029, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.GoogleG9N9N);

        Assert.True(success);
        Assert.True(isDown);
        Assert.Equal(expected, button);
    }

    [Theory]
    [InlineData(0x0029)]
    [InlineData(0x0031)]
    public void TryParseAclPacket_G9N9NDoesNotTreatAudioStopAsPlayPause(ushort attribute)
    {
        byte[] packet = CreateAclPacket(0x1B, attribute, [0x00, 0x02]);

        Assert.False(ChromecastEtwReceiver.TryParseAclPacket(packet, out _, out _));
    }

    [Fact]
    public void TryParseAclPacket_G9N9N3ByteRelease_ReturnsNoneAndNotDown()
    {
        byte[] payload = [0x01, 0x00, 0x00];
        byte[] packet = CreateAclPacket(0x1B, 0x0029, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.GoogleG9N9N);

        Assert.True(success);
        Assert.False(isDown);
        Assert.Equal(RemoteButton.None, button);
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

    [Theory]
    [InlineData(0x0224, RemoteButton.Back)]
    [InlineData(0x00E9, RemoteButton.VolumeUp)]
    [InlineData(0x00EA, RemoteButton.VolumeDown)]
    public void TryParseAclPacket_Xiaomi2ByteReport_ReturnsCorrectButton(ushort usage, RemoteButton expected)
    {
        byte[] payload = [(byte)(usage & 0xFF), (byte)((usage >> 8) & 0xFF)];
        byte[] packet = CreateAclPacket(0x1B, 0x0058, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.Xiaomi);

        Assert.True(success);
        Assert.True(isDown);
        Assert.Equal(expected, button);
    }

    [Theory]
    [InlineData(0xF1, RemoteButton.Back, true, true)]
    [InlineData(0x80, RemoteButton.VolumeUp, true, true)]
    [InlineData(0x81, RemoteButton.VolumeDown, true, true)]
    [InlineData(0x00, RemoteButton.None, false, true)]
    [InlineData(0x28, RemoteButton.None, false, false)]
    public void TryParseAclPacket_XiaomiRc003KeyboardReportRecoversOnlyDroppedKeys(
        byte usage, RemoteButton expected, bool expectedDown, bool expectedSuccess)
    {
        byte[] packet = CreateAclPacket(0x1B, 0x0064, [0, 0, usage, 0, 0, 0, 0, 0]);
        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.Xiaomi);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expected, button);
        Assert.Equal(expectedDown, isDown);
    }

    [Fact]
    public void TryParseAclPacket_Xiaomi2ByteRelease_ReturnsNoneAndNotDown()
    {
        byte[] payload = [0x00, 0x00];
        byte[] packet = CreateAclPacket(0x1B, 0x0058, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.Xiaomi);

        Assert.True(success);
        Assert.False(isDown);
        Assert.Equal(RemoteButton.None, button);
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
    public void TryParseAclPacket_Xiaomi_RejectsAtvvAudioSyncPayload()
    {
        // Valid ATVV audio-sync payload: 0A 00 00 00 00 52 00
        byte[] payload = [0x0A, 0x00, 0x00, 0x00, 0x00, 0x52, 0x00];

        // On verified button attribute
        byte[] packet1 = CreateAclPacket(0x1B, 0x002B, payload);
        bool success1 = ChromecastEtwReceiver.TryParseAclPacket(packet1, out var button1, out bool isDown1, EtwProtocolKind.Xiaomi);
        Assert.False(success1);
        Assert.Equal(RemoteButton.None, button1);
        Assert.False(isDown1);

        // On non-HID attribute (e.g. 0x0012)
        byte[] packet2 = CreateAclPacket(0x1B, 0x0012, payload);
        bool success2 = ChromecastEtwReceiver.TryParseAclPacket(packet2, out var button2, out bool isDown2, EtwProtocolKind.Xiaomi);
        Assert.False(success2);
        Assert.Equal(RemoteButton.None, button2);
        Assert.False(isDown2);
    }

    [Theory]
    [InlineData(0x0025)]
    [InlineData(0x0028)]
    [InlineData(0x002D)]
    public void TryParseAclPacket_G9N9N_RejectsNonHidAttributes(ushort attribute)
    {
        // 0x0025, 0x0028, 0x002D are non-HID characteristics (e.g. ATVV or control)
        // Even if payload resembles a button or status (e.g. [0x01, 0x0A, 0x00] or [0x23, 0x02]), it must be rejected
        byte[] payload1 = [0x01, 0x0A, 0x00];
        byte[] packet1 = CreateAclPacket(0x1B, attribute, payload1);
        Assert.False(ChromecastEtwReceiver.TryParseAclPacket(packet1, out _, out _, EtwProtocolKind.GoogleG9N9N));

        byte[] payload2 = [0x23, 0x02];
        byte[] packet2 = CreateAclPacket(0x1B, attribute, payload2);
        Assert.False(ChromecastEtwReceiver.TryParseAclPacket(packet2, out _, out _, EtwProtocolKind.GoogleG9N9N));
    }

    [Fact]
    public void TryParseAclPacket_G9N9N_RejectsAtvvAudioSyncPayloadOnInputAttribute()
    {
        // Valid ATVV audio-sync payload: 0A 00 00 00 00 52 00
        byte[] payload = [0x0A, 0x00, 0x00, 0x00, 0x00, 0x52, 0x00];
        byte[] packet = CreateAclPacket(0x1B, 0x0029, payload);

        bool success = ChromecastEtwReceiver.TryParseAclPacket(packet, out var button, out bool isDown, EtwProtocolKind.GoogleG9N9N);
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
        receiver.SetSources([new EtwButtonSource(1, new Guid("00112233-4455-6677-8899-aabbccddeeff"), EtwProtocolKind.GoogleG9N9N)]);
        var edges = new List<(RemoteButton, bool)>();
        int redacted = 0;
        receiver.ButtonEvent += (_, button, isDown) => edges.Add((button, isDown));
        receiver.TruncatedPacketDetected += _ => redacted++;

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
    public void ProcessPacket_ProbeIsolation_RejectsOtherConnections()
    {
        using var receiver = new ChromecastEtwReceiver();
        receiver.SetSources([new EtwButtonSource(1, new Guid("00112233-4455-6677-8899-aabbccddeeff"), EtwProtocolKind.GoogleG9N9N)]);
        var edges = new List<(RemoteButton, bool)>();
        receiver.ButtonEvent += (_, button, isDown) => edges.Add((button, isDown));

        // Probing target device on connection 0x0040
        byte[] request = CreateAclPacket(0x06, 0x0001,
            [0xFF, 0xFF, 0x00, 0x28, 0xFF, 0xEE, 0xDD, 0xCC, 0xBB, 0xAA, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00],
            connectionHandle: 0x0040);
        byte[] response = CreateAclPacket(0x01, 0x0106, [0x00, 0x0A], connectionHandle: 0x0040);

        receiver.ProcessPacket(4, request);
        receiver.ProcessPacket(3, response);

        // A button press arrives from a DIFFERENT connection (0x0041)
        byte[] otherPress = CreateAclPacket(0x1B, 0x0029, [0x07, 0, 0, 0, 0, 0, 0, 0], connectionHandle: 0x0041);
        receiver.ProcessPacket(3, otherPress);

        // Must NOT bind or dispatch from connection 0x0041
        Assert.Empty(edges);

        // A button press arrives from the PROBED connection (0x0040)
        byte[] matchingPress = CreateAclPacket(0x1B, 0x0029, [0x07, 0, 0, 0, 0, 0, 0, 0], connectionHandle: 0x0040);
        receiver.ProcessPacket(3, matchingPress);

        // Must bind and dispatch from connection 0x0040
        Assert.Equal(new[] { (RemoteButton.Ok, true) }, edges);
    }

    [Fact]
    public void ProcessPacket_ThreeSourcesMaintainSeparateHeldButtonsAndHandleLifetimes()
    {
        using var receiver = new ChromecastEtwReceiver();
        var sources = new[] {
            new EtwButtonSource(0x100, Guid.NewGuid(), EtwProtocolKind.GoogleG9N9N),
            new EtwButtonSource(0x200, Guid.NewGuid(), EtwProtocolKind.GoogleG9N9N),
            new EtwButtonSource(0x300, Guid.NewGuid(), EtwProtocolKind.Xiaomi)
        };
        receiver.SetSources(sources);
        var edges = new List<(ulong, RemoteButton, bool)>();
        var disconnected = new List<ulong>();
        receiver.ButtonEvent += (address, button, down) => edges.Add((address, button, down));
        receiver.DeviceDisconnected += disconnected.Add;
        for (int i = 0; i < sources.Length; i++)
        {
            ushort handle = (ushort)(0x40 + i);
            byte[] request = new byte[20];
            new byte[] {0xFF, 0xFF, 0x00, 0x28}.CopyTo(request, 0);
            var marker = request.AsSpan(4);
            sources[i].Marker.TryWriteBytes(marker, bigEndian: true, out _);
            marker.Reverse();
            receiver.ProcessPacket(4, CreateAclPacket(0x06, 0x0001, request, connectionHandle: handle));
            receiver.ProcessPacket(3, CreateAclPacket(0x01, 0x0106, [0, 0x0A], connectionHandle: handle));
        }

        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, [7, 0, 0, 0, 0, 0, 0, 0], connectionHandle: 0x40));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, [7, 0, 0, 0, 0, 0, 0, 0], connectionHandle: 0x41));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0064, [0, 0, 0xF1, 0, 0, 0, 0, 0], connectionHandle: 0x42));
        receiver.ProcessPacket(2, [5, 4, 0, 0x40, 0, 0x13]);
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, new byte[8], connectionHandle: 0x40));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, new byte[8], connectionHandle: 0x41));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0064, new byte[8], connectionHandle: 0x42));

        Assert.Equal(new (ulong, RemoteButton, bool)[] {
            (0x100, RemoteButton.Ok, true), (0x200, RemoteButton.Ok, true), (0x300, RemoteButton.Back, true),
            (0x200, RemoteButton.Ok, false), (0x300, RemoteButton.Back, false)
        }, edges);
        Assert.Equal(new ulong[] { 0x100 }, disconnected);
    }

    [Fact]
    public void ProcessPacket_WithoutProbe_RejectsButtonReports()
    {
        using var receiver = new ChromecastEtwReceiver();
        var edges = new List<(RemoteButton, bool)>();
        receiver.ButtonEvent += (_, button, isDown) => edges.Add((button, isDown));

        byte[] press = CreateAclPacket(0x1B, 0x0029, [0x01, 0x07, 0x00]); // G9N9N Ok
        byte[] release = CreateAclPacket(0x1B, 0x0029, [0x01, 0x00, 0x00]);

        receiver.ProcessPacket(3, press);
        receiver.ProcessPacket(3, release);

        Assert.Empty(edges);
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

    [Fact]
    public void ProcessPacket_HandlesTrailingPaddingInAclAndHciDisconnectWithMaskedHandle()
    {
        using var receiver = new ChromecastEtwReceiver();
        const ulong addr = 0x112233445566;
        Guid marker = new("00112233-4455-6677-8899-aabbccddeeff");
        receiver.SetSources([new EtwButtonSource(addr, marker, EtwProtocolKind.GoogleG9N9N)]);

        var bound = new List<ulong>();
        var disconnected = new List<ulong>();
        var edges = new List<(RemoteButton, bool)>();
        receiver.SourceBound += bound.Add;
        receiver.DeviceDisconnected += disconnected.Add;
        receiver.ButtonEvent += (_, btn, down) => edges.Add((btn, down));

        byte[] baseRequest = CreateAclPacket(0x06, 0x0001,
            [0xFF, 0xFF, 0x00, 0x28, 0xFF, 0xEE, 0xDD, 0xCC, 0xBB, 0xAA, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00],
            connectionHandle: 0x0040);
        byte[] paddedRequest = [.. baseRequest, 0x00, 0x00, 0x00, 0x00];
        byte[] baseResponse = CreateAclPacket(0x01, 0x0106, [0x00, 0x0A], connectionHandle: 0x0040);
        byte[] paddedResponse = [.. baseResponse, 0xAA, 0xBB];
        byte[] basePress = CreateAclPacket(0x1B, 0x0029, [0x07, 0, 0, 0, 0, 0, 0, 0], connectionHandle: 0x0040);
        byte[] paddedPress = [.. basePress, 0x00, 0x00];

        receiver.ProcessPacket(4, paddedRequest);
        receiver.ProcessPacket(3, paddedResponse);
        Assert.Equal(new[] { addr }, bound);

        receiver.ProcessPacket(3, paddedPress);
        Assert.Equal(new[] { (RemoteButton.Ok, true) }, edges);

        // Padded 8-byte HCI Disconnection Complete with upper reserved handle bits set (0x2040 -> 0x0040)
        receiver.ProcessPacket(2, [0x05, 0x04, 0x00, 0x40, 0x20, 0x13, 0x00, 0x00]);
        Assert.Equal(new[] { addr }, disconnected);

        // Subsequent packets on unbound handle must be ignored
        receiver.ProcessPacket(3, paddedPress);
        Assert.Single(edges);
    }

    [Fact]
    public void TryParseAclPacket_WithTrailingAlignmentPadding_StillDecodesCleanly()
    {
        byte[] acl = CreateAclPacket(0x1B, 0x0029, [0x07, 0, 0, 0, 0, 0, 0, 0]);
        byte[] padded = [.. acl, 0x00, 0x00, 0x00, 0x00];

        Assert.True(ChromecastEtwReceiver.TryParseAclPacket(padded, out var btn, out bool down, EtwProtocolKind.GoogleG9N9N));
        Assert.Equal(RemoteButton.Ok, btn);
        Assert.True(down);
    }

    private static (byte[] Request, byte[] Response) CreateProbeHandshake(Guid marker, ushort connectionHandle = 0x0040)
    {
        byte[] markerPayload = new byte[20];
        new byte[] { 0xFF, 0xFF, 0x00, 0x28 }.CopyTo(markerPayload, 0);
        var uuid = markerPayload.AsSpan(4);
        marker.TryWriteBytes(uuid, bigEndian: true, out _);
        uuid.Reverse();
        return (
            CreateAclPacket(0x06, 0x0001, markerPayload, connectionHandle),
            CreateAclPacket(0x01, 0x0106, [0x00, 0x0A], connectionHandle));
    }

    private static void BindSource(ChromecastEtwReceiver receiver, EtwButtonSource source, ushort connectionHandle = 0x0040)
    {
        var (request, response) = CreateProbeHandshake(source.Marker, connectionHandle);
        receiver.ProcessPacket(4, request);
        receiver.ProcessPacket(3, response);
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
    public void G9N9N_1ByteShortReport_PressAndRelease_DecodesOn0046AndInHidRange(byte code, RemoteButton expected)
    {
        IReadOnlySet<ushort> customAttrs = new HashSet<ushort> { 0x0046 };
        Assert.True(ChromecastEtwReceiver.TryParseAclPacket(
            CreateAclPacket(0x1B, 0x0046, [code]),
            out var staticBtn, out bool staticDown, out _, EtwProtocolKind.GoogleG9N9N, customAttrs));
        Assert.Equal(expected, staticBtn);
        Assert.True(staticDown);

        Assert.True(ChromecastEtwReceiver.TryParseAclPacket(
            CreateAclPacket(0x1B, 0x0046, [0x00]),
            out var releaseBtn, out bool releaseDown, out _, EtwProtocolKind.GoogleG9N9N, customAttrs));
        Assert.Equal(RemoteButton.None, releaseBtn);
        Assert.False(releaseDown);

        // Stateful ProcessPacket: both fallback (no HID range) and discovered HID range
        foreach (var range in new (ushort Start, ushort End)[] { (0, 0), (0x0030, 0x0050) })
        {
            using var receiver = new ChromecastEtwReceiver();
            var source = new EtwButtonSource(0xA02607, Guid.NewGuid(), EtwProtocolKind.GoogleG9N9N, range.Start, range.End);
            receiver.SetSources([source]);
            var edges = new List<(RemoteButton, bool)>();
            receiver.ButtonEvent += (_, btn, down) => edges.Add((btn, down));
            BindSource(receiver, source);

            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0046, [code]));
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0046, [0x00]));

            Assert.Equal([(expected, true), (expected, false)], edges);
        }
    }

    [Theory]
    [InlineData(0x01, 0x00, RemoteButton.Power)]
    [InlineData(0x03, 0x00, RemoteButton.Up)]
    [InlineData(0x11, 0x00, RemoteButton.Input)]
    [InlineData(0x03, 0x0B, RemoteButton.Up)]
    public void G9N9N_2ByteIndexedReport_ParsesPowerWithoutReportIdMisparseAndChords(byte b0, byte b1, RemoteButton expected)
    {
        using var receiver = new ChromecastEtwReceiver();
        var source = new EtwButtonSource(0x01102602, Guid.NewGuid(), EtwProtocolKind.GoogleG9N9N, 0x0020, 0x0050);
        receiver.SetSources([source]);
        var edges = new List<(RemoteButton, bool)>();
        receiver.ButtonEvent += (_, btn, down) => edges.Add((btn, down));
        BindSource(receiver, source);

        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0035, [b0, b1]));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0035, [0x00, 0x00]));

        Assert.Equal([(expected, true), (expected, false)], edges);
    }

    [Theory]
    [InlineData(0x0042, RemoteButton.Up)]
    [InlineData(0x0043, RemoteButton.Down)]
    [InlineData(0x0044, RemoteButton.Left)]
    [InlineData(0x0045, RemoteButton.Right)]
    [InlineData(0x0041, RemoteButton.Ok)]
    [InlineData(0x0224, RemoteButton.Back)]
    [InlineData(0x0223, RemoteButton.Home)]
    [InlineData(0x00E2, RemoteButton.Mute)]
    [InlineData(0x00E9, RemoteButton.VolumeUp)]
    [InlineData(0x00EA, RemoteButton.VolumeDown)]
    [InlineData(0x0077, RemoteButton.YouTube)]
    [InlineData(0x0078, RemoteButton.Netflix)]
    [InlineData(0x019E, RemoteButton.Power)]
    [InlineData(0x0189, RemoteButton.Input)]
    public void G9N9N_011B_2ByteAnd3ByteConsumerUsage_All14KeysWorkInHidRange(ushort usage, RemoteButton expected)
    {
        byte lo = (byte)(usage & 0xFF);
        byte hi = (byte)((usage >> 8) & 0xFF);

        using var receiver = new ChromecastEtwReceiver();
        var source = new EtwButtonSource(0x011B0001, Guid.NewGuid(), EtwProtocolKind.GoogleG9N9N, 0x0025, 0x0050, [0x0015, 0x0028]);
        receiver.SetSources([source]);
        var edges = new List<(RemoteButton, bool)>();
        receiver.ButtonEvent += (_, btn, down) => edges.Add((btn, down));
        BindSource(receiver, source);

        // 2-byte truncated Consumer Usage on handle 0x004A inside HID range
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x004A, [lo, hi]));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x004A, [0x00, 0x00]));
        // Compliant 3-byte Consumer Usage payload [lo, hi, 0x00] / [0, 0, 0]
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x004A, [lo, hi, 0x00]));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x004A, [0x00, 0x00, 0x00]));

        Assert.Equal([(expected, true), (expected, false), (expected, true), (expected, false)], edges);
    }

    [Fact]
    public void G9N9N_ZeroRegression_Locks0029AndRejectsAtvvAndOutsideHidRange()
    {
        using var receiver = new ChromecastEtwReceiver();
        var source = new EtwButtonSource(
            0x4754514C6FCC,
            Guid.NewGuid(),
            EtwProtocolKind.GoogleG9N9N,
            HidHandleStart: 0x0020,
            HidHandleEnd: 0x0050,
            ExcludedAttributes: [0x0025, 0x0028, 0x002D]);
        receiver.SetSources([source]);
        var edges = new List<(RemoteButton, bool)>();
        receiver.ButtonEvent += (_, btn, down) => edges.Add((btn, down));
        BindSource(receiver, source);

        // Excluded ATVV handles cannot spoof Mute (0x08) or PlayPause ([0x00, 0x02]) even inside HID range
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0025, [0x08]));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0028, [0x00, 0x02]));
        // [0x00, 0x02] must never parse even on 0x0029 or 0x0046
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, [0x00, 0x02]));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0046, [0x00, 0x02]));
        // Battery / DIS handle outside HID range (0x0055) sending 10% (0x0A) must not trigger Home
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0055, [0x0A]));
        Assert.Empty(edges);

        // Valid press on 0x0029 locks the connection onto 0x0029
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, [0x07, 0, 0, 0, 0, 0, 0, 0]));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, new byte[8]));
        // After lock-in, another handle inside HID range (e.g. 0x0031 / 0x0046) must be ignored
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0031, [0x0A]));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0046, [0x42, 0x00]));
        Assert.Equal([(RemoteButton.Ok, true), (RemoteButton.Ok, false)], edges);

        // Disconnect clears lock so rebinding works cleanly on next connection
        receiver.ProcessPacket(2, [0x05, 0x04, 0x00, 0x40, 0x00, 0x13]);
        BindSource(receiver, source);
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0046, [0x03]));
        receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0046, [0x00]));
        Assert.Equal([(RemoteButton.Ok, true), (RemoteButton.Ok, false), (RemoteButton.Up, true), (RemoteButton.Up, false)], edges);
    }

    [Theory]
    [InlineData(1, 0x0046)]
    [InlineData(2, 0x0046)]
    [InlineData(3, 0x0029)]
    [InlineData(8, 0x0029)]
    public void G9N9N_RedactedHeaderOnlyPackets_AreDetectedFor1_2_3_And8BytePayloads(int payloadLength, ushort handle)
    {
        IReadOnlySet<ushort> customAttrs = new HashSet<ushort> { 0x0029, 0x0046 };
        byte[] redacted = CreateAclPacket(0x1B, handle, new byte[payloadLength])[..11];

        bool ok = ChromecastEtwReceiver.TryParseAclPacket(redacted, out _, out _, out bool isTruncated, EtwProtocolKind.GoogleG9N9N, customAttrs);
        Assert.False(ok);
        Assert.True(isTruncated);

        using var receiver = new ChromecastEtwReceiver();
        var source = new EtwButtonSource(0x1234, Guid.NewGuid(), EtwProtocolKind.GoogleG9N9N, 0x0020, 0x0050);
        receiver.SetSources([source]);
        var truncatedAddrs = new List<ulong>();
        receiver.TruncatedPacketDetected += truncatedAddrs.Add;
        BindSource(receiver, source);

        receiver.ProcessPacket(3, redacted);
        Assert.Equal([0x1234UL], truncatedAddrs);
    }

    [Fact]
    public void ProbeDiagnostics_DistinguishRejectedMarkerFromSuccessfulBinding()
    {
        string tempLogDir = Path.Combine(Path.GetTempPath(), "RB_ProbeDiag_" + Guid.NewGuid().ToString("N"));
        AppLogger.ResetForTesting();
        AppLogger.Initialize(tempLogDir);
        try
        {
            using var receiver = new ChromecastEtwReceiver();
            var source = new EtwButtonSource(0x1234, Guid.NewGuid(), EtwProtocolKind.GoogleG9N9N);
            receiver.SetSources([source]);
            var bound = new List<ulong>();
            receiver.SourceBound += bound.Add;
            var (request, response) = CreateProbeHandshake(source.Marker);
            byte[] rejected = (byte[])request.Clone();
            rejected[6] = 5;

            receiver.ProcessPacket(4, rejected);
            receiver.ProcessPacket(4, rejected);
            receiver.ProcessPacket(3, response);
            Assert.Empty(bound);

            receiver.ProcessPacket(4, request);
            receiver.ProcessPacket(3, response);
            Assert.Equal([source.Address], bound);

            string log = File.ReadAllText(AppLogger.CurrentLogFilePath);
            Assert.Single(log.Split('\n'), line => line.Contains("rejected outgoing ACL: reason=NonAttChannel"));
            Assert.Single(log.Split('\n'), line => line.Contains("marker seen in rejected packet: addr=0x000000001234"));
            Assert.Contains("BthPort observed GATT query for 0x000000001234", log);
            Assert.Contains("ETW bound 0x000000001234 to ACL 0x0040 (via=ATT response 0x01", log);
            Assert.DoesNotContain(Convert.ToHexString(request.AsSpan(15)), log);
        }
        finally
        {
            AppLogger.ResetForTesting();
            if (Directory.Exists(tempLogDir)) Directory.Delete(tempLogDir, recursive: true);
        }
    }

    [Fact]
    public void G9N9N_DiagnosticLogging_LogsObservedFormat_IgnoresExcluded_AndCapsUnknownsAt32()
    {
        string tempLogDir = Path.Combine(Path.GetTempPath(), "RB_G9N9N_Diag_" + Guid.NewGuid().ToString("N"));
        AppLogger.ResetForTesting();
        AppLogger.Initialize(tempLogDir);
        try
        {
            using var receiver = new ChromecastEtwReceiver();
            var source = new EtwButtonSource(
                0xABCDEF123456,
                Guid.NewGuid(),
                EtwProtocolKind.GoogleG9N9N,
                HidHandleStart: 0x0020,
                HidHandleEnd: 0x0050,
                ExcludedAttributes: [0x0025, 0x0028]);
            receiver.SetSources([source]);
            BindSource(receiver, source);

            // Observed format on valid button press
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0046, [0x42, 0x00]));
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0046, [0x00, 0x00]));

            // Excluded attribute notifications must stay silent
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0025, [0x08]));
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0028, [0xDE, 0xAD, 0xBE, 0xEF]));

            // Duplicate unknown payload inside HID range -> logged once
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0048, [0xDE, 0xAD, 0xBE, 0xEF]));
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0048, [0xDE, 0xAD, 0xBE, 0xEF]));

            // Outside HID range non-excluded notification -> logged once (#2)
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0055, [0x0A]));

            // Send 32 additional distinct unrecognized payloads (#3..#32 logged, #33 triggers cap, #34 suppressed)
            for (byte i = 1; i <= 32; i++)
            {
                receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0048, [0x5A, i, 0x01, 0x02]));
            }

            string logText = File.ReadAllText(AppLogger.CurrentLogFilePath);
            Assert.Contains("G9N9N ETW report format observed: addr=0xABCDEF123456, attr=0x0046, format=ConsumerUsage2Byte, len=2, sample=4200, button=Up, down=True", logText);
            Assert.Contains("G9N9N ETW unrecognized notification [UnknownPayload]: addr=0xABCDEF123456, attr=0x0048, opcode=0x1B, len=4, hex=DEADBEEF (hidRange=0x0020..0x0050)", logText);
            Assert.Contains("G9N9N ETW unrecognized notification [OutsideHidRange]: addr=0xABCDEF123456, attr=0x0055, opcode=0x1B, len=1, hex=0A (hidRange=0x0020..0x0050)", logText);
            Assert.Contains("log cap (32) reached", logText);
            Assert.DoesNotContain("attr=0x0025", logText);
            Assert.DoesNotContain("attr=0x0028", logText);
            Assert.DoesNotContain("hex=5A200102", logText); // 32nd loop iteration = 34th unique item -> suppressed

            int unrecognizedLines = logText.Split('\n').Count(line => line.Contains("G9N9N ETW unrecognized notification ["));
            Assert.Equal(32, unrecognizedLines);
        }
        finally
        {
            AppLogger.ResetForTesting();
            try
            {
                if (Directory.Exists(tempLogDir)) Directory.Delete(tempLogDir, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public void BuildEtwAttributeFilter_ComputesHidRangeAndExclusionsCorrectly()
    {
        Guid gap = new("00001800-0000-1000-8000-00805F9B34FB");
        Guid gatt = new("00001801-0000-1000-8000-00805F9B34FB");
        Guid hid = new("00001812-0000-1000-8000-00805F9B34FB");
        Guid atvv = new("AB5E0001-5A21-4F05-BC7D-AF01F617B664");
        Guid battery = new("0000180F-0000-1000-8000-00805F9B34FB");

        // 0x1812 in middle: expands ATVV service range (0x0050..0x005F) and priority pairs
        var midFilter = WinRtBluetoothManager.BuildEtwAttributeFilter(
            [(battery, 0x0060), (hid, 0x0020), (gap, 0x0001), (atvv, 0x0050), (gatt, 0x0010)],
            [0x0052, 0x0054, 0x0056],
            [0x0062, 0x0012, 0x0052],
            out string midSummary);
        Assert.Equal(0x0020, midFilter.HidHandleStart);
        Assert.Equal(0x004F, midFilter.HidHandleEnd);
        Assert.Equal(
            Enumerable.Range(0x0050, 16).Select(h => (ushort)h).ToArray(),
            midFilter.ExcludedAttributes);
        Assert.Contains("hidRange=0x0020..0x004F", midSummary);
        Assert.Contains("0x1812@0x0020..0x004F", midSummary);

        // 0x1812 as last service: priority declaration handle also expands to value handle (h + 1)
        var lastFilter = WinRtBluetoothManager.BuildEtwAttributeFilter(
            [(gap, 0x0001), (hid, 0x0050)],
            [0x0025],
            [],
            out _);
        Assert.Equal(0x0050, lastFilter.HidHandleStart);
        Assert.Equal(ushort.MaxValue, lastFilter.HidHandleEnd);
        Assert.NotNull(lastFilter.ExcludedAttributes);
        Assert.Equal(new ushort[] { 0x0025, 0x0026 }, lastFilter.ExcludedAttributes);

        // 0x1812 missing -> fallback includes sorted distinct declaration+value pairs
        var missingFilter = WinRtBluetoothManager.BuildEtwAttributeFilter(
            [(gap, 0x0001), (atvv, 0x0050)],
            [0x0054, 0x0052],
            [0x0062, 0x0012],
            out _);
        Assert.False(missingFilter.HasHidRange);
        Assert.NotNull(missingFilter.ExcludedAttributes);
        Assert.Equal(
            new ushort[] { 0x0012, 0x0013, 0x0052, 0x0053, 0x0054, 0x0055, 0x0062, 0x0063 },
            missingFilter.ExcludedAttributes);
    }

    [Fact]
    public void BuildEtwAttributeFilter_Real0x4754514C6FCCLayout_ExcludesAllAtvvAudioAndControlNotifications()
    {
        Guid gap = new("00001800-0000-1000-8000-00805F9B34FB");
        Guid dis = new("0000180A-0000-1000-8000-00805F9B34FB");
        Guid battery = new("0000180F-0000-1000-8000-00805F9B34FB");
        Guid hid = new("00001812-0000-1000-8000-00805F9B34FB");
        Guid vendor1 = new("d343bfc0-5a21-4f05-bc7d-af01f617b664");
        Guid atvv = new("AB5E0001-5A21-4F05-BC7D-AF01F617B664");
        Guid vendor2 = new("54534542-5157-4854-2053-492049415447");

        var filter = WinRtBluetoothManager.BuildEtwAttributeFilter(
            [
                (gap, 0x0001),
                (dis, 0x0008),
                (battery, 0x001B),
                (hid, 0x001F),
                (vendor1, 0x002C),
                (atvv, 0x0038),
                (vendor2, 0x0041)
            ],
            [0x0039, 0x003B, 0x003E],
            [],
            out _);

        Assert.Equal((ushort)0x001F, filter.HidHandleStart);
        Assert.Equal((ushort)0x002B, filter.HidHandleEnd);
        Assert.NotNull(filter.ExcludedAttributes);
        for (ushort h = 0x0038; h <= 0x0040; h++)
        {
            Assert.Contains(h, filter.ExcludedAttributes);
        }
        Assert.Contains((ushort)0x003C, filter.ExcludedAttributes);
        Assert.Contains((ushort)0x003F, filter.ExcludedAttributes);

        string tempLogDir = Path.Combine(Path.GetTempPath(), "RB_G9N9N_Real_" + Guid.NewGuid().ToString("N"));
        AppLogger.ResetForTesting();
        AppLogger.Initialize(tempLogDir);
        try
        {
            using var receiver = new ChromecastEtwReceiver();
            var source = new EtwButtonSource(
                0x4754514C6FCC,
                Guid.NewGuid(),
                EtwProtocolKind.GoogleG9N9N,
                filter.HidHandleStart,
                filter.HidHandleEnd,
                filter.ExcludedAttributes);
            receiver.SetSources([source]);
            var edges = new List<(RemoteButton, bool)>();
            receiver.ButtonEvent += (_, btn, down) => edges.Add((btn, down));
            BindSource(receiver, source);

            // Real ATVV control & 160-byte ADPCM audio frames captured from 0x4754514C6FCC
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x003F, [0x04, 0x03, 0x02, 0x44]));
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x003F, [0x08]));
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x003F, [0x00, 0x02]));
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x003F, [0x0A, 0x02, 0x00, 0x10, 0x00, 0x00, 0x00]));
            byte[] audioFrame = Enumerable.Range(0, 160).Select(i => (byte)(0xA0 + (i % 16))).ToArray();
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x003C, audioFrame));

            Assert.Empty(edges);

            // Existing 0x0029 button traffic works normally
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, [0x07, 0, 0, 0, 0, 0, 0, 0]));
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x0029, new byte[8]));
            Assert.Equal([(RemoteButton.Ok, true), (RemoteButton.Ok, false)], edges);

            string logText = File.ReadAllText(AppLogger.CurrentLogFilePath);
            Assert.DoesNotContain("G9N9N ETW unrecognized notification [", logText);
        }
        finally
        {
            AppLogger.ResetForTesting();
            try
            {
                if (Directory.Exists(tempLogDir)) Directory.Delete(tempLogDir, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public void G9N9N_DiagnosticLogging_OmitsOversizedPayloads()
    {
        string tempLogDir = Path.Combine(Path.GetTempPath(), "RB_G9N9N_Privacy_" + Guid.NewGuid().ToString("N"));
        AppLogger.ResetForTesting();
        AppLogger.Initialize(tempLogDir);
        try
        {
            using var receiver = new ChromecastEtwReceiver();
            var source = new EtwButtonSource(
                0xABCDEF123456,
                Guid.NewGuid(),
                EtwProtocolKind.GoogleG9N9N,
                HidHandleStart: 0x001F,
                HidHandleEnd: 0x002B,
                ExcludedAttributes: [0x003C, 0x003F]);
            receiver.SetSources([source]);
            BindSource(receiver, source);

            byte[] oversized = Enumerable.Range(0, 160).Select(i => (byte)(0xD0 + (i % 16))).ToArray();
            receiver.ProcessPacket(3, CreateAclPacket(0x1B, 0x002A, oversized));

            string logText = File.ReadAllText(AppLogger.CurrentLogFilePath);
            Assert.Contains("G9N9N ETW unrecognized notification [UnknownPayload]: addr=0xABCDEF123456, attr=0x002A, opcode=0x1B, len=160, hex=<omitted> (hidRange=0x001F..0x002B)", logText);
            Assert.DoesNotContain("D0D1D2D3D4D5", logText);
        }
        finally
        {
            AppLogger.ResetForTesting();
            try
            {
                if (Directory.Exists(tempLogDir)) Directory.Delete(tempLogDir, recursive: true);
            }
            catch { }
        }
    }
}
