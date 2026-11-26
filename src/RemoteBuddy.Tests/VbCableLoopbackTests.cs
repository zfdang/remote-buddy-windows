// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using RemoteBuddy.Platform.Windows.Audio;
using Xunit;

namespace RemoteBuddy.Tests;

public class VbCableLoopbackTests
{
    [Fact]
    public void CorrelationFindsDelayedAndAttenuatedTestTone()
    {
        short[] tone = VbCableLoopbackProbe.CreateTestTone();
        var captured = new short[12000];
        var noise = new Random(100);
        for (int i = 0; i < captured.Length; i++)
        {
            captured[i] = (short)noise.Next(-32, 33);
        }
        for (int i = 0; i < tone.Length; i++)
        {
            captured[2891 + i] += (short)(tone[i] / 4);
        }

        Assert.InRange(VbCableLoopbackProbe.FindCorrelation(tone, captured), 0.95, 1.001);
    }

    [Fact]
    public void CorrelationRejectsSilenceAndUnrelatedAudio()
    {
        short[] tone = VbCableLoopbackProbe.CreateTestTone();
        var captured = new short[12000];
        Assert.Equal(0, VbCableLoopbackProbe.FindCorrelation(tone, captured));

        for (int i = 0; i < captured.Length; i++)
        {
            captured[i] = (short)(10000 * Math.Sin(2 * Math.PI * 800 * i / 16000));
        }

        Assert.InRange(VbCableLoopbackProbe.FindCorrelation(tone, captured), 0, 0.5);
        Assert.Equal(0, VbCableLoopbackProbe.FindCorrelation(tone, captured.AsSpan(0, tone.Length - 1)));
    }

    [Fact]
    public async Task ProbeDefersWhileVoicePlaybackOwnsTheCable()
    {
        VbCableLoopbackProbe.BeginVoicePlayback();
        try
        {
            Assert.Equal(CableLoopbackResult.VoiceBusy, await VbCableLoopbackProbe.RunAsync());
        }
        finally
        {
            VbCableLoopbackProbe.EndVoicePlayback();
        }
    }
}
