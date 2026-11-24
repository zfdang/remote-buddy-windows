// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Core.Protocols;

/// <summary>
/// Protocol, identity, and HID report definitions for the T1-Remote (ATVV v1.0 + multi-report BLE HID air-mouse/keyboard remote).
/// </summary>
public static class T1RemoteProtocol
{
    public const ushort VendorId = 0x620A;
    public const ushort ProductId = 0x0407;
    public const ushort VoiceHoldUsage = 0x00AA;
    public const ushort VoiceSearchConsumerUsage = 0x0221;
    public const ushort SystemPowerDownUsage = 0x0081;

    public const byte KeyboardReportId = 1;
    public const byte ConsumerReportId = 2;
    public const byte SystemControlReportId = 3;
    public const byte MouseReportId = 4;
    public const byte VendorReportId = 0x5A;

    private static readonly HashSet<string> CandidateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "t1-remote",
        "t1 remote",
        "t1_remote",
        "t1remote"
    };

    public static bool IsCandidate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string clean = name.Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        if (clean == "t1" || clean.Contains("t1 remote") || clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("t1"))
        {
            return true;
        }
        foreach (var cand in CandidateNames)
        {
            if (name.Contains(cand, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public enum DecodeStatus
    {
        Usages,
        Ignore,
        Invalid
    }

    public readonly record struct DecodeResult(
        DecodeStatus Status,
        List<RemoteButton> Buttons,
        bool VoiceHoldPressed,
        List<RemoteButton> PassthroughButtons
    );

    /// <summary>
    /// Decodes a T1 HID input report without intercepting rear QWERTY typing or air-mouse movements.
    /// </summary>
    public static DecodeResult DecodeReport(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return new DecodeResult(DecodeStatus.Invalid, new(), false, new());
        }

        byte reportId = data[0];
        ReadOnlySpan<byte> payload = data;

        // Strip leading report ID only when length matches a report-ID-prefixed frame,
        // so 8-byte no-ID keyboard reports with modifier bits (0x01..0x04) are not truncated.
        bool hasReportId =
            (reportId == KeyboardReportId && data.Length == 9) ||
            (reportId == ConsumerReportId && data.Length is 3 or 5) ||
            (reportId == SystemControlReportId && data.Length == 2) ||
            (reportId == MouseReportId && data.Length is >= 2 and not 8) ||
            (reportId == VendorReportId && data.Length is >= 2 and not 8);

        if (hasReportId)
        {
            payload = data[1..];
        }
        else
        {
            // If data starts without report ID, infer by length
            if (data.Length == 8) reportId = KeyboardReportId;
            else if (data.Length == 2) reportId = ConsumerReportId;
            else if (data.Length == 1) reportId = SystemControlReportId;
            else return new DecodeResult(DecodeStatus.Invalid, new(), false, new());
        }

        switch (reportId)
        {
            case KeyboardReportId:
                if (payload.Length < 8)
                {
                    return new DecodeResult(DecodeStatus.Invalid, new(), false, new());
                }

                byte modifier = payload[0];
                var buttons = new List<RemoteButton>();
                var passthrough = new List<RemoteButton>();
                bool voiceHold = false;

                for (int i = 2; i < 8 && i < payload.Length; i++)
                {
                    byte b = payload[i];
                    if (b == 0) continue;

                    if (b == (byte)VoiceHoldUsage)
                    {
                        voiceHold = true;
                    }
                    else if (b == 0x65) // Menu
                    {
                        buttons.Add(RemoteButton.Menu);
                    }
                    else if (modifier == 0)
                    {
                        // Observed passthrough navigation usages
                        switch (b)
                        {
                            case 0x28: passthrough.Add(RemoteButton.Ok); break;
                            case 0x4F: passthrough.Add(RemoteButton.Right); break;
                            case 0x50: passthrough.Add(RemoteButton.Left); break;
                            case 0x51: passthrough.Add(RemoteButton.Down); break;
                            case 0x52: passthrough.Add(RemoteButton.Up); break;
                        }
                    }
                }

                return new DecodeResult(DecodeStatus.Usages, buttons, voiceHold, passthrough);

            case ConsumerReportId:
                if (payload.Length < 2)
                {
                    return new DecodeResult(DecodeStatus.Invalid, new(), false, new());
                }

                ushort usage = (ushort)(payload[0] | (payload[1] << 8));
                if (usage == 0)
                {
                    return new DecodeResult(DecodeStatus.Usages, new(), false, new());
                }

                var consumerButtons = new List<RemoteButton>();
                switch (usage)
                {
                    case 0x0224:
                        consumerButtons.Add(RemoteButton.Back);
                        break;
                    case 0x0223:
                        consumerButtons.Add(RemoteButton.Home);
                        break;
                    case 0x00E2:
                        consumerButtons.Add(RemoteButton.Mute);
                        break;
                    case 0x00E9:
                        consumerButtons.Add(RemoteButton.VolumeUp);
                        break;
                    case 0x00EA:
                        consumerButtons.Add(RemoteButton.VolumeDown);
                        break;
                    default:
                        // Ignore non-front consumer usages (such as AC Search or back-keyboard media keys)
                        return new DecodeResult(DecodeStatus.Ignore, new(), false, new());
                }

                return new DecodeResult(DecodeStatus.Usages, consumerButtons, false, new());

            case SystemControlReportId:
                if (payload.Length < 1)
                {
                    return new DecodeResult(DecodeStatus.Invalid, new(), false, new());
                }

                var systemButtons = new List<RemoteButton>();
                if ((payload[0] & 0x01) != 0 || payload[0] == 0x81)
                {
                    systemButtons.Add(RemoteButton.Power);
                }

                return new DecodeResult(DecodeStatus.Usages, systemButtons, false, new());

            case MouseReportId:
            case VendorReportId:
                // Air mouse cursor motion & vendor packets must be ignored without error
                return new DecodeResult(DecodeStatus.Ignore, new(), false, new());

            default:
                return new DecodeResult(DecodeStatus.Invalid, new(), false, new());
        }
    }

    public static bool TryParseReport(ReadOnlySpan<byte> data, out List<RemoteButton> buttons, out bool voiceHoldPressed)
    {
        var result = DecodeReport(data);
        if (result.Status == DecodeStatus.Usages)
        {
            buttons = result.Buttons;
            voiceHoldPressed = result.VoiceHoldPressed;
            return true;
        }

        buttons = new();
        voiceHoldPressed = false;
        return false;
    }
}
