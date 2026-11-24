// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RemoteBuddy.Core.Protocols;

public record ATVVCapabilities(
    ushort Version,
    byte Codecs,
    byte Interaction,
    int? ReportedFrameSize,
    byte SelectedCodec,
    double SampleRate
)
{
    public int FrameSize => ReportedFrameSize ?? 120;
}

public static class ATVVProtocol
{
    public static readonly Guid ServiceUuid = new("AB5E0001-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid TransmitUuid = new("AB5E0002-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid AudioUuid = new("AB5E0003-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid ControlUuid = new("AB5E0004-5A21-4F05-BC7D-AF01F617B664");

    // Opcodes
    public const byte OpcodeAudioStop = 0x00;
    public const byte OpcodeAudioStart = 0x04;
    public const byte OpcodeStartSearch = 0x08;
    public const byte OpcodeGetCapabilities = 0x0A;
    public const byte OpcodeCapabilitiesResp = 0x0B;
    public const byte OpcodeMicrophoneOpen = 0x0C;
    public const byte OpcodeMicrophoneRefused = 0x0C;
    public const byte OpcodeAudioSync = 0x0A;
    public const byte OpcodeMicrophoneClose = 0x0D;
    public const byte OpcodeMicrophoneExtend = 0x0E;

    public static byte[] BuildGetCapabilities(ushort version = 0x0100)
    {
        return new byte[] { OpcodeGetCapabilities, 0x01, 0x00, 0x00, 0x03, 0x03 };
    }

    public static byte[] BuildMicrophoneOpen(ushort version = 0x0100, byte codec = 0x02)
    {
        if (version >= 0x0100)
        {
            return new byte[] { OpcodeMicrophoneOpen, 0x00 };
        }
        return new byte[] { OpcodeMicrophoneOpen, 0x00, codec };
    }

    public static byte[] BuildMicrophoneClose(byte sessionId, ushort version = 0x0100)
    {
        if (version >= 0x0100)
        {
            return new byte[] { OpcodeMicrophoneClose, sessionId };
        }
        return new byte[] { OpcodeMicrophoneClose };
    }

    public static byte[] BuildMicrophoneExtend(byte sessionId)
    {
        return new byte[] { OpcodeMicrophoneExtend, sessionId };
    }

    public static ATVVCapabilities? ParseCapabilities(ReadOnlySpan<byte> bytes, bool allowingLegacyLayout = true)
    {
        if (bytes.Length < 7 || bytes[0] != OpcodeCapabilitiesResp)
            return null;

        ushort version = (ushort)((bytes[1] << 8) | bytes[2]);
        byte codecs;
        byte interaction;

        if (version >= 0x0100)
        {
            codecs = bytes[3];
            interaction = bytes[4];
            if (allowingLegacyLayout && codecs == 0 && bytes.Length >= 9 && (bytes[4] & 0x03) != 0)
            {
                codecs = bytes[4];
                interaction = 0x03;
            }
        }
        else
        {
            if (bytes.Length < 9) return null;
            codecs = bytes[4];
            interaction = 0;
        }

        int rawFrameSize = (bytes[5] << 8) | bytes[6];
        int? reportedFrameSize = rawFrameSize is > 0 and <= 512 ? rawFrameSize : null;
        byte selectedCodec = (codecs & 0x02) != 0 ? (byte)0x02 : (byte)0x01;
        double sampleRate = (selectedCodec == 0x02) ? 16000.0 : 8000.0;

        return new ATVVCapabilities(
            Version: version,
            Codecs: codecs,
            Interaction: interaction,
            ReportedFrameSize: reportedFrameSize,
            SelectedCodec: selectedCodec,
            SampleRate: sampleRate
        );
    }

    public static bool ParseAudioStart(ReadOnlySpan<byte> bytes, out byte interaction, out byte codec, out byte sessionId)
        => ParseAudioStart(bytes, out interaction, out codec, out sessionId, defaultCodec: 0x02);

    public static bool ParseAudioStart(ReadOnlySpan<byte> bytes, out byte interaction, out byte codec, out byte sessionId, byte defaultCodec)
    {
        interaction = 0;
        codec = 0;
        sessionId = 0;

        if (bytes.Length == 0 || bytes[0] != OpcodeAudioStart)
            return false;

        if (bytes.Length == 1)
        {
            // ATVV v0.4 sends 1-byte [0x04] with no interaction, codec, or sessionId fields
            codec = defaultCodec;
            return true;
        }

        if (bytes.Length < 3)
            return false;

        interaction = bytes[1];
        codec = bytes[2];
        if (bytes.Length >= 4)
        {
            sessionId = bytes[3];
        }

        return true;
    }

    public static bool ParseAudioSync(ReadOnlySpan<byte> bytes, out short predictor, out byte stepIndex)
        => ParseAudioSync(bytes, out predictor, out stepIndex, out _);

    public static bool ParseAudioSync(ReadOnlySpan<byte> bytes, out short predictor, out byte stepIndex, out ushort frameNumber)
    {
        predictor = 0;
        stepIndex = 0;
        frameNumber = 0;

        if (bytes.Length < 7 || bytes[0] != OpcodeAudioSync)
            return false;

        frameNumber = (ushort)((bytes[2] << 8) | bytes[3]);
        ushort predictorBits = (ushort)((bytes[4] << 8) | bytes[5]);
        predictor = unchecked((short)predictorBits);
        stepIndex = bytes[6];

        return true;
    }
}

public sealed class ATVVFrameAccumulator
{
    public const int MaxBufferBytes = 64 * 1024;

    public delegate void SpanAction(ReadOnlySpan<byte> frame);

    private byte[] _buffer = new byte[1024];
    private int _offset;
    private int _count;
    private (short Predictor, byte StepIndex)? _pendingSync;
    private int _framesBeforeSync = 0;
    private ushort _decodedFrameIndex;
    private bool _sawNonZeroSyncFrame;

    public int PendingCount => _count;

    public void Synchronize(short predictor, byte stepIndex)
    {
        _pendingSync = (predictor, stepIndex);
        _framesBeforeSync = _count == 0 ? 0 : 1;
    }

    public void Synchronize(short predictor, byte stepIndex, ushort frameNumber)
    {
        if (frameNumber != 0 || _sawNonZeroSyncFrame)
        {
            _sawNonZeroSyncFrame = true;
            short delta = unchecked((short)(frameNumber - _decodedFrameIndex));
            if (delta is < 0 and >= -16)
            {
                // Stale AudioSync for an already-decoded frame; discard instead of corrupting a later frame
                return;
            }
            if (delta is >= 0 and <= 16)
            {
                _pendingSync = (predictor, stepIndex);
                _framesBeforeSync = delta;
                return;
            }
        }

        // Remotes that send constant 0 or non-resetting/packet-based frame counters fall back to boundary heuristic
        Synchronize(predictor, stepIndex);
    }

    public void Reset()
    {
        _offset = 0;
        _count = 0;
        _pendingSync = null;
        _framesBeforeSync = 0;
        _decodedFrameIndex = 0;
        _sawNonZeroSyncFrame = false;
    }

    public void Append(ReadOnlySpan<byte> data, int frameSize, SpanAction onFrame)
    {
        if (frameSize <= 0 || data.IsEmpty) return;
        if (data.Length > MaxBufferBytes)
        {
            Reset();
            return;
        }
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_offset + _count));
        _count += data.Length;

        while (_count >= frameSize)
        {
            var frame = _buffer.AsSpan(_offset, frameSize);
            _offset += frameSize;
            _count -= frameSize;
            _decodedFrameIndex++;
            onFrame(frame);
        }

        if (_count == 0)
        {
            _offset = 0;
        }
    }

    public void Flush(int frameSize, SpanAction onPartialFrame)
    {
        if (_count <= 0)
        {
            _offset = 0;
            return;
        }

        if (frameSize <= 0)
        {
            _offset = 0;
            _count = 0;
            return;
        }

        var partial = _buffer.AsSpan(_offset, _count);
        _offset = 0;
        _count = 0;
        _decodedFrameIndex++;
        onPartialFrame(partial);
    }

    public List<byte[]> Append(ReadOnlySpan<byte> data, int frameSize)
    {
        var frames = new List<byte[]>();
        Append(data, frameSize, frame => frames.Add(frame.ToArray()));
        return frames;
    }

    private void EnsureCapacity(int additionalBytes)
    {
        if (_offset + _count + additionalBytes <= _buffer.Length) return;

        if (_count + additionalBytes <= _buffer.Length)
        {
            Buffer.BlockCopy(_buffer, _offset, _buffer, 0, _count);
            _offset = 0;
            return;
        }

        if (_count + additionalBytes > MaxBufferBytes)
        {
            // Bound memory growth if partial frames accumulate without matching frameSize.
            Reset();
            return;
        }

        int newSize = Math.Min(MaxBufferBytes, Math.Max(_buffer.Length * 2, _count + additionalBytes));
        var next = new byte[newSize];
        if (_count > 0)
        {
            Buffer.BlockCopy(_buffer, _offset, next, 0, _count);
        }
        _buffer = next;
        _offset = 0;
    }

    public bool TryConsumeSyncForNextFrame(out short predictor, out byte stepIndex)
    {
        if (_pendingSync.HasValue && _framesBeforeSync == 0)
        {
            var sync = _pendingSync.Value;
            _pendingSync = null;
            predictor = sync.Predictor;
            stepIndex = sync.StepIndex;
            return true;
        }

        if (_framesBeforeSync > 0)
        {
            _framesBeforeSync--;
        }

        predictor = 0;
        stepIndex = 0;
        return false;
    }
}
