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

    private readonly object _lock = new();

    private Phase _currentPhase = Phase.Idle;
    public Phase CurrentPhase
    {
        get { lock (_lock) return _currentPhase; }
        private set { lock (_lock) _currentPhase = value; }
    }

    private ulong _generation = 0;
    public ulong Generation
    {
        get { lock (_lock) return _generation; }
        private set { lock (_lock) _generation = value; }
    }

    private long _startedAtMs = 0;
    private long? _physicalReleasedAtMs = null;

    public bool IsActive { get { lock (_lock) return _currentPhase != Phase.Idle; } }
    public bool IsAwaitingReopen { get { lock (_lock) return _currentPhase == Phase.AwaitingReopen; } }
    public bool IsHandsFreeActive { get { lock (_lock) return _currentPhase == Phase.HandsFreeActive; } }
    public bool IsClosing { get { lock (_lock) return _currentPhase == Phase.Closing; } }

    public bool IsAwaitingReopenWithPendingPreparationRelease
    {
        get { lock (_lock) return _currentPhase == Phase.PhysicalPressed && _physicalReleasedAtMs.HasValue; }
    }

    public void BeginPhysicalPress(long nowMs)
    {
        lock (_lock)
        {
            _generation++;
            _currentPhase = Phase.PhysicalPressed;
            _startedAtMs = nowMs;
            _physicalReleasedAtMs = null;
        }
    }

    public bool RecordShortReleaseDuringPreparation(bool isRemoteStop, bool localCloseRequested, long nowMs)
    {
        lock (_lock)
        {
            if (_currentPhase != Phase.PhysicalPressed || !isRemoteStop || localCloseRequested)
                return false;

            long releaseTime = _physicalReleasedAtMs ?? nowMs;
            long elapsed = releaseTime - _startedAtMs;
            if (elapsed >= 0 && elapsed < HoldThresholdMs)
            {
                _physicalReleasedAtMs = releaseTime;
                return true;
            }

            return false;
        }
    }

    public bool ShouldReopenOnPhysicalStop(bool isRemoteStop, bool localCloseRequested, long nowMs)
    {
        lock (_lock)
        {
            if (_currentPhase != Phase.PhysicalPressed || !isRemoteStop || localCloseRequested)
                return false;

            long releaseTime = _physicalReleasedAtMs ?? nowMs;
            long elapsed = releaseTime - _startedAtMs;
            return elapsed >= 0 && elapsed < HoldThresholdMs;
        }
    }

    public bool TryBeginReopenOnPhysicalStop(bool isRemoteStop, bool localCloseRequested, long nowMs, out ulong generation)
    {
        lock (_lock)
        {
            generation = _generation;
            if (_currentPhase != Phase.PhysicalPressed || !isRemoteStop || localCloseRequested)
                return false;

            long releaseTime = _physicalReleasedAtMs ?? nowMs;
            long elapsed = releaseTime - _startedAtMs;
            if (elapsed < 0 || elapsed >= HoldThresholdMs)
                return false;

            _generation++;
            _currentPhase = Phase.AwaitingReopen;
            generation = _generation;
            return true;
        }
    }

    public ulong EnterAwaitingReopen()
    {
        lock (_lock)
        {
            if (_currentPhase != Phase.PhysicalPressed) return 0;
            _generation++;
            _currentPhase = Phase.AwaitingReopen;
            return _generation;
        }
    }

    public ulong? EnterHandsFreeActive()
    {
        lock (_lock)
        {
            if (_currentPhase != Phase.AwaitingReopen) return null;
            _generation++;
            _currentPhase = Phase.HandsFreeActive;
            return _generation;
        }
    }

    public bool ShouldStopOnRemoteOpenRequest()
    {
        lock (_lock)
        {
            return _currentPhase switch
            {
                Phase.PhysicalPressed => _physicalReleasedAtMs.HasValue,
                Phase.AwaitingReopen or Phase.HandsFreeActive or Phase.Closing => true,
                _ => false
            };
        }
    }

    public bool ShouldStopOnRemoteAudioStart(byte? interaction, byte? activeSessionId, byte? incomingSessionId)
    {
        lock (_lock)
        {
            return _currentPhase switch
            {
                Phase.Closing => true,
                Phase.HandsFreeActive =>
                    interaction == 0x03 || (activeSessionId.HasValue && incomingSessionId.HasValue && incomingSessionId.Value != activeSessionId.Value),
                Phase.AwaitingReopen => interaction == 0x03,
                Phase.PhysicalPressed => _physicalReleasedAtMs.HasValue && interaction == 0x03,
                _ => false
            };
        }
    }

    public ulong? EnterClosing()
    {
        lock (_lock)
        {
            switch (_currentPhase)
            {
                case Phase.PhysicalPressed when _physicalReleasedAtMs.HasValue:
                case Phase.AwaitingReopen:
                case Phase.HandsFreeActive:
                    _generation++;
                    _currentPhase = Phase.Closing;
                    return _generation;

                case Phase.Closing:
                    return _generation;

                default:
                    return null;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _generation++;
            _currentPhase = Phase.Idle;
            _startedAtMs = 0;
            _physicalReleasedAtMs = null;
        }
    }
}
