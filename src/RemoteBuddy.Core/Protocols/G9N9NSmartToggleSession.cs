// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RemoteBuddy.Core.Protocols;

/// <summary>
/// State machine for Chromecast G9N9N physical voice button smart-toggle mode:
/// - Short press & release (&lt; 0.7s) keeps the voice session alive and triggers an ATVV MIC_OPEN (0x0C)
///   reopen so the user can speak hands-free until the next press.
/// - Long press (&gt;= 0.7s) behaves as standard hold-to-talk and stops immediately on release.
/// </summary>
public sealed class G9N9NSmartToggleSession
{
    public const double HoldThresholdSeconds = 0.7;
    public const long HoldThresholdMs = 700;
    public const double ReopenTimeoutSeconds = 5.0;
    public const long ReopenTimeoutMs = 5000;
    public const double MaximumHandsFreeDurationSeconds = 300.0;
    public const long MaximumHandsFreeDurationMs = 300000;

    public enum Phase
    {
        Idle,
        PhysicalPressed,
        AwaitingReopen,
        HandsFreeActive,
        Closing
    }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public ulong Generation { get; private set; } = 0;

    private long _startedAtMs = 0;
    private long? _physicalReleasedAtMs = null;

    public bool IsActive => CurrentPhase != Phase.Idle;
    public bool IsAwaitingReopen => CurrentPhase == Phase.AwaitingReopen;
    public bool IsHandsFreeActive => CurrentPhase == Phase.HandsFreeActive;
    public bool IsClosing => CurrentPhase == Phase.Closing;

    public bool IsAwaitingReopenWithPendingPreparationRelease =>
        CurrentPhase == Phase.PhysicalPressed && _physicalReleasedAtMs.HasValue;

    public void BeginPhysicalPress(long nowMs)
    {
        Generation++;
        CurrentPhase = Phase.PhysicalPressed;
        _startedAtMs = nowMs;
        _physicalReleasedAtMs = null;
    }

    public bool RecordShortReleaseDuringPreparation(bool isRemoteStop, bool localCloseRequested, long nowMs)
    {
        if (CurrentPhase != Phase.PhysicalPressed || !isRemoteStop || localCloseRequested)
            return false;

        long releaseTime = _physicalReleasedAtMs ?? nowMs;
        if (releaseTime - _startedAtMs < HoldThresholdMs)
        {
            _physicalReleasedAtMs = releaseTime;
            return true;
        }

        return false;
    }

    public bool ShouldReopenOnPhysicalStop(bool isRemoteStop, bool localCloseRequested, long nowMs)
    {
        if (CurrentPhase != Phase.PhysicalPressed || !isRemoteStop || localCloseRequested)
            return false;

        long releaseTime = _physicalReleasedAtMs ?? nowMs;
        return (releaseTime - _startedAtMs) < HoldThresholdMs;
    }

    public ulong EnterAwaitingReopen()
    {
        Generation++;
        CurrentPhase = Phase.AwaitingReopen;
        return Generation;
    }

    public ulong? EnterHandsFreeActive()
    {
        if (CurrentPhase != Phase.AwaitingReopen) return null;
        Generation++;
        CurrentPhase = Phase.HandsFreeActive;
        return Generation;
    }

    public bool ShouldStopOnRemoteOpenRequest()
    {
        return CurrentPhase switch
        {
            Phase.PhysicalPressed => _physicalReleasedAtMs.HasValue,
            Phase.AwaitingReopen or Phase.HandsFreeActive or Phase.Closing => true,
            _ => false
        };
    }

    public bool ShouldStopOnRemoteAudioStart(byte? interaction, byte? activeSessionId, byte? incomingSessionId)
    {
        return CurrentPhase switch
        {
            Phase.Closing => true,
            Phase.HandsFreeActive =>
                interaction == 0x03 || (incomingSessionId.HasValue && incomingSessionId != activeSessionId),
            Phase.AwaitingReopen => interaction == 0x03,
            Phase.PhysicalPressed => _physicalReleasedAtMs.HasValue && interaction == 0x03,
            _ => false
        };
    }

    public ulong? EnterClosing()
    {
        switch (CurrentPhase)
        {
            case Phase.PhysicalPressed when _physicalReleasedAtMs.HasValue:
            case Phase.AwaitingReopen:
            case Phase.HandsFreeActive:
                Generation++;
                CurrentPhase = Phase.Closing;
                return Generation;

            case Phase.Closing:
                return Generation;

            default:
                return null;
        }
    }

    public void Reset()
    {
        Generation++;
        CurrentPhase = Phase.Idle;
        _startedAtMs = 0;
        _physicalReleasedAtMs = null;
    }
}
