// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;
using RemoteBuddy.Platform.Windows.Bluetooth;
using Xunit;

namespace RemoteBuddy.Tests;

public class G9N9NSmartToggleTests
{
    [Fact]
    public void ShortPress_TriggersReopen_AndEntersHandsFreeActive()
    {
        var session = new G9N9NSmartToggleSession();
        Assert.Equal(G9N9NSmartToggleSession.Phase.Idle, session.CurrentPhase);

        long startMs = 1000;
        session.BeginPhysicalPress(startMs);
        Assert.Equal(G9N9NSmartToggleSession.Phase.PhysicalPressed, session.CurrentPhase);
        Assert.True(session.IsActive);

        // Released at 300ms (< 700ms hold threshold)
        long releaseMs = startMs + 300;
        bool shouldReopen = session.ShouldReopenOnPhysicalStop(isRemoteStop: true, localCloseRequested: false, releaseMs);
        Assert.True(shouldReopen);

        ulong reopenGen = session.EnterAwaitingReopen();
        Assert.Equal(G9N9NSmartToggleSession.Phase.AwaitingReopen, session.CurrentPhase);
        Assert.True(session.IsAwaitingReopen);
        Assert.Equal(session.Generation, reopenGen);

        // Remote responds with AudioStart
        var activeGen = session.EnterHandsFreeActive();
        Assert.NotNull(activeGen);
        Assert.Equal(G9N9NSmartToggleSession.Phase.HandsFreeActive, session.CurrentPhase);
        Assert.True(session.IsHandsFreeActive);
    }

    [Fact]
    public void LongPress_BehavesAsHoldToTalk_DoesNotReopen()
    {
        var session = new G9N9NSmartToggleSession();
        long startMs = 1000;
        session.BeginPhysicalPress(startMs);

        // Released at 1200ms (> 700ms hold threshold)
        long releaseMs = startMs + 1200;
        bool shouldReopen = session.ShouldReopenOnPhysicalStop(isRemoteStop: true, localCloseRequested: false, releaseMs);
        Assert.False(shouldReopen);

        session.Reset();
        Assert.Equal(G9N9NSmartToggleSession.Phase.Idle, session.CurrentPhase);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void SecondPressDuringHandsFree_RequestsClose()
    {
        var session = new G9N9NSmartToggleSession();
        session.BeginPhysicalPress(1000);
        session.EnterAwaitingReopen();
        session.EnterHandsFreeActive();

        Assert.True(session.IsHandsFreeActive);
        Assert.True(session.ShouldStopOnRemoteOpenRequest());

        var closeGen = session.EnterClosing();
        Assert.NotNull(closeGen);
        Assert.Equal(G9N9NSmartToggleSession.Phase.Closing, session.CurrentPhase);
        Assert.True(session.IsClosing);
    }

    [Fact]
    public void PreparationRelease_PreservesShortPressState()
    {
        var session = new G9N9NSmartToggleSession();
        long start = 5000;
        session.BeginPhysicalPress(start);

        // Record early release during audio pipeline preparation (200ms)
        bool recorded = session.RecordShortReleaseDuringPreparation(isRemoteStop: true, localCloseRequested: false, start + 200);
        Assert.True(recorded);
        Assert.True(session.IsAwaitingReopenWithPendingPreparationRelease);

        // When physical stop later arrives, should still reopen
        bool shouldReopen = session.ShouldReopenOnPhysicalStop(isRemoteStop: true, localCloseRequested: false, start + 250);
        Assert.True(shouldReopen);
    }

    [Fact]
    public void VoiceButtonMode_HoldToTalk_DisablesSmartToggleEligibility()
    {
        using var manager = new WinRtBluetoothManager();
        manager.ConnectedDeviceNameOverride = "Chromecast Remote";

        manager.VoiceButtonMode = G9N9NVoiceButtonMode.SmartToggle;
        Assert.True(manager.IsSmartToggleEligible());

        manager.VoiceButtonMode = G9N9NVoiceButtonMode.HoldToTalk;
        Assert.False(manager.IsSmartToggleEligible());
    }

    [Fact]
    public void G9N9NSmartToggleSession_ConcurrentTransitions_MaintainsPhaseConsistency()
    {
        var session = new G9N9NSmartToggleSession();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 500, i =>
        {
            try
            {
                long now = i * 10;
                session.BeginPhysicalPress(now);
                _ = session.RecordShortReleaseDuringPreparation(true, false, now + 100);
                _ = session.ShouldReopenOnPhysicalStop(true, false, now + 200);
                _ = session.EnterAwaitingReopen();
                _ = session.EnterHandsFreeActive();
                _ = session.ShouldStopOnRemoteOpenRequest();
                _ = session.ShouldStopOnRemoteAudioStart(0x03, 1, 2);
                _ = session.EnterClosing();
                session.Reset();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
    }
}
