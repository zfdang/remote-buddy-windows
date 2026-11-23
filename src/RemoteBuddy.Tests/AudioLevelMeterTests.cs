// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using NAudio.Wave;
using RemoteBuddy.Core.Audio;
using RemoteBuddy.Platform.Windows.Audio;
using Xunit;

namespace RemoteBuddy.Tests;

public class AudioLevelMeterTests
{
    [Fact]
    public void AudioLevelMeter_CalculateRms_ReturnsZeroOnSilence()
    {
        short[] silence = new short[160];
        float level = AudioLevelMeter.CalculateRms(silence);
        Assert.Equal(0f, level);
    }

    [Fact]
    public void AudioLevelMeter_CalculateRms_ScalesCorrectlyWithSignal()
    {
        short[] signal = new short[160];
        Array.Fill(signal, (short)16384); // 50% amplitude
        float level = AudioLevelMeter.CalculateRms(signal);
        Assert.True(level > 0.5f);
        Assert.True(level <= 1.0f);
    }

    [Fact]
    public void PreRollWaveProvider_HoldsSilenceUntilThreshold_ThenReleasesRealSamples()
    {
        var inner = new BufferedWaveProvider(new WaveFormat(16000, 16, 1));
        var preRoll = new PreRollWaveProvider(inner, preRollBytes: 1600);

        byte[] firstChunk = new byte[640];
        Array.Fill(firstChunk, (byte)0x55);
        inner.AddSamples(firstChunk, 0, firstChunk.Length);

        byte[] readBuf = new byte[320];
        int read1 = preRoll.Read(readBuf, 0, readBuf.Length);
        Assert.Equal(320, read1);
        Assert.False(preRoll.IsReleased);
        Assert.All(readBuf, b => Assert.Equal((byte)0, b));
        Assert.Equal(640, inner.BufferedBytes); // Initial samples preserved until pre-roll completes

        byte[] secondChunk = new byte[960];
        Array.Fill(secondChunk, (byte)0x55);
        inner.AddSamples(secondChunk, 0, secondChunk.Length); // Total 1600 bytes >= preRollBytes

        int read2 = preRoll.Read(readBuf, 0, readBuf.Length);
        Assert.Equal(320, read2);
        Assert.True(preRoll.IsReleased);
        Assert.All(readBuf, b => Assert.Equal((byte)0x55, b));

        // Reset re-arms pre-roll cleanly
        preRoll.Reset();
        Assert.False(preRoll.IsReleased);
        Assert.Equal(0, inner.BufferedBytes);
    }

    [Fact]
    public void PreRollWaveProvider_ReleasePreRoll_FlushesShortUtterances()
    {
        var inner = new BufferedWaveProvider(new WaveFormat(16000, 16, 1));
        var preRoll = new PreRollWaveProvider(inner, preRollBytes: 1600);

        byte[] shortSpeech = new byte[320];
        Array.Fill(shortSpeech, (byte)0x7A);
        inner.AddSamples(shortSpeech, 0, shortSpeech.Length);

        preRoll.ReleasePreRoll();

        byte[] outBuf = new byte[320];
        int count = preRoll.Read(outBuf, 0, outBuf.Length);
        Assert.Equal(320, count);
        Assert.True(preRoll.IsReleased);
        Assert.All(outBuf, b => Assert.Equal((byte)0x7A, b));
    }

    [Fact]
    public async Task WasapiAudioPlayer_DrainAsync_ReturnsImmediatelyWhenNotPlaying()
    {
        using var player = new WasapiAudioPlayer();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await player.DrainAsync(500);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 250);
    }
}
