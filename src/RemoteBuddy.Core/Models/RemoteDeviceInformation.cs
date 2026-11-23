// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Text;
using RemoteBuddy.Core.Protocols;

namespace RemoteBuddy.Core.Models;

public sealed record RemoteDeviceInformation(
    ulong BluetoothAddress,
    ATVVCapabilities? VoiceCapabilities = null,
    string? ModelNumber = null,
    string? FirmwareRevision = null,
    (ushort VendorId, ushort ProductId)? PnpId = null,
    ushort? ProductVersion = null,
    string? HardwareRevision = null)
{
    private static readonly UTF8Encoding DeviceEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string? VendorProductId => PnpId is { } id ? $"{id.VendorId:X4} / {id.ProductId:X4}" : null;
    public string? HidVersionText => ProductVersion is { } ver ? $"0x{ver:X4}" : null;
    public string? VoiceProtocol => VoiceCapabilities is { } caps ? $"ATVV {caps.Version >> 8}.{caps.Version & 0xFF}" : null;
    public string? AudioFormat => VoiceCapabilities is { } caps ? $"{caps.SampleRate / 1000:g} kHz · IMA ADPCM" : null;
    public string? AudioFrameSize => VoiceCapabilities?.ReportedFrameSize is { } size ? $"{size} 字节" : null;

    public static string? DecodeDeviceString(ReadOnlySpan<byte> data)
    {
        try
        {
            var value = DeviceEncoding.GetString(data).Trim('\0', ' ', '\t', '\r', '\n');
            return value.Length == 0 ? null : value;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public static (ushort VendorId, ushort ProductId)? DecodePnpId(ReadOnlySpan<byte> data)
    {
        if (data.Length != 7 || data[0] is not (1 or 2)) return null;

        ushort vendorId = BinaryPrimitives.ReadUInt16LittleEndian(data[1..3]);
        ushort productId = BinaryPrimitives.ReadUInt16LittleEndian(data[3..5]);
        return vendorId == 0 ? null : (vendorId, productId);
    }

    public static ushort? DecodePnpProductVersion(ReadOnlySpan<byte> data)
    {
        if (data.Length != 7 || data[0] is not (1 or 2)) return null;

        ushort vendorId = BinaryPrimitives.ReadUInt16LittleEndian(data[1..3]);
        if (vendorId == 0) return null;

        return BinaryPrimitives.ReadUInt16LittleEndian(data[5..7]);
    }
}
