// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Audio;

public enum CableLoopbackResult
{
    Passed,
    EndpointUnavailable,
    NoSignal,
    AudioError,
    VoiceBusy
}

public static class VbCableLoopbackProbe
{
    private const int SampleRate = 16000;
    private const int CaptureMilliseconds = 1200;
    private const double MinimumCorrelation = 0.75;
    private static readonly object PlaybackLock = new();
    private static TaskCompletionSource? _activeProbe;
    private static WasapiOut? _probePlayback;
    private static int _voicePlaybackCount;

    public static Task<CableLoopbackResult> RunAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => RunCoreAsync(cancellationToken), cancellationToken);

    internal static void BeginVoicePlayback()
    {
        lock (PlaybackLock)
        {
            _voicePlaybackCount++;
            _activeProbe?.TrySetResult();
            StopProbePlaybackLocked();
        }
    }

    internal static void EndVoicePlayback()
    {
        lock (PlaybackLock) _voicePlaybackCount--;
    }

    private static void StopProbePlaybackLocked()
    {
        var playback = _probePlayback;
        _probePlayback = null;
        if (playback == null) return;
        try { playback.Stop(); }
        catch (Exception ex)
        {
            AppLogger.Debug($"Could not stop VB-CABLE test tone: {ex.Message}", nameof(VbCableLoopbackProbe));
        }
    }

    private static async Task<CableLoopbackResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (PlaybackLock)
        {
            if (_activeProbe != null || _voicePlaybackCount > 0) return CableLoopbackResult.VoiceBusy;
            _activeProbe = interrupted;
        }

        CableLoopbackResult result;
        try
        {
            result = await CaptureTestToneAsync(interrupted.Task, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn($"VB-CABLE loopback failed (HResult=0x{ex.HResult:X8}): {ex.Message}", nameof(VbCableLoopbackProbe));
            result = CableLoopbackResult.AudioError;
        }
        finally
        {
            lock (PlaybackLock) _activeProbe = null;
        }

        AppLogger.Info($"VB-CABLE loopback result: {result}", nameof(VbCableLoopbackProbe));
        return result;
    }

    private static async Task<CableLoopbackResult> CaptureTestToneAsync(Task interrupted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = VirtualAudioAuditor.GetCableInputDevice();
        using var output = input == null ? null : VirtualAudioAuditor.GetCableOutputDevice(input);
        if (input == null || output == null) return CableLoopbackResult.EndpointUnavailable;

        var format = new WaveFormat(SampleRate, 16, 1);
        short[] reference = CreateTestTone();
        byte[] toneWithLeadIn = new byte[(SampleRate / 10 + reference.Length) * sizeof(short)];
        MemoryMarshal.AsBytes(reference.AsSpan()).CopyTo(toneWithLeadIn.AsSpan(SampleRate / 10 * sizeof(short)));
        byte[] captured = new byte[(CaptureMilliseconds + 100) * format.AverageBytesPerSecond / 1000];
        int captureLength = 0;
        var audioError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var capture = new WasapiCapture(output, useEventSync: true, audioBufferMillisecondsLength: 30) { WaveFormat = format })
        using (var tone = new RawSourceWaveStream(new MemoryStream(toneWithLeadIn, writable: false), format))
        using (var playback = new WasapiOut(input, AudioClientShareMode.Shared, useEventSync: true, latency: 20))
        {
            capture.DataAvailable += (_, args) =>
            {
                int offset = captureLength;
                int count = Math.Min(args.BytesRecorded, captured.Length - offset);
                args.Buffer.AsSpan(0, count).CopyTo(captured.AsSpan(offset));
                Volatile.Write(ref captureLength, offset + count);
            };
            capture.RecordingStopped += (_, args) =>
            {
                if (args.Exception != null) audioError.TrySetResult(args.Exception);
            };
            playback.PlaybackStopped += (_, args) =>
            {
                if (args.Exception != null) audioError.TrySetResult(args.Exception);
            };

            playback.Init(tone);
            capture.StartRecording();
            try
            {
                lock (PlaybackLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (interrupted.IsCompleted) return CableLoopbackResult.VoiceBusy;
                    _probePlayback = playback;
                    playback.Play();
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    var elapsed = Task.Delay(CaptureMilliseconds, deadline.Token);
                    await Task.WhenAny(elapsed, audioError.Task, interrupted).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (interrupted.IsCompleted) return CableLoopbackResult.VoiceBusy;
                    if (audioError.Task.IsCompletedSuccessfully) throw await audioError.Task.ConfigureAwait(false);
                }
                finally
                {
                    deadline.Cancel();
                }
            }
            finally
            {
                lock (PlaybackLock) StopProbePlaybackLocked();
                capture.StopRecording();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (interrupted.IsCompleted) return CableLoopbackResult.VoiceBusy;
        if (audioError.Task.IsCompletedSuccessfully) throw await audioError.Task.ConfigureAwait(false);
        int bytesCaptured = Volatile.Read(ref captureLength);
        double correlation = FindCorrelation(reference, MemoryMarshal.Cast<byte, short>(captured.AsSpan(0, bytesCaptured)));
        AppLogger.Debug($"VB-CABLE loopback correlation={correlation:F3}, frames={bytesCaptured / sizeof(short)}",
            nameof(VbCableLoopbackProbe));
        return correlation >= MinimumCorrelation ? CableLoopbackResult.Passed : CableLoopbackResult.NoSignal;
    }

    internal static short[] CreateTestTone()
    {
        const double duration = 0.22;
        const double startHz = 450;
        const double endHz = 1450;
        var samples = new short[(int)(SampleRate * duration)];
        for (int i = 0; i < samples.Length; i++)
        {
            double time = (double)i / SampleRate;
            double envelope = Math.Sin(Math.PI * time / duration);
            double phase = 2 * Math.PI * (startHz * time + (endHz - startHz) * time * time / (2 * duration));
            samples[i] = (short)(5000 * envelope * envelope * Math.Sin(phase));
        }
        return samples;
    }

    internal static double FindCorrelation(ReadOnlySpan<short> reference, ReadOnlySpan<short> captured)
    {
        // Evaluate every possible delay while sampling the reference at 4 kHz.
        const int stride = 4;
        const int minimumRms = 32;
        double referenceEnergy = 0;
        for (int i = 0; i < reference.Length; i += stride)
        {
            referenceEnergy += (double)reference[i] * reference[i];
        }

        double best = 0;
        int points = (reference.Length + stride - 1) / stride;
        for (int offset = 0; offset <= captured.Length - reference.Length; offset++)
        {
            double dot = 0;
            double energy = 0;
            for (int i = 0; i < reference.Length; i += stride)
            {
                int sample = captured[offset + i];
                dot += (double)reference[i] * sample;
                energy += (double)sample * sample;
            }
            if (energy >= points * minimumRms * minimumRms && dot > 0)
            {
                best = Math.Max(best, dot / Math.Sqrt(referenceEnergy * energy));
            }
        }
        return best;
    }
}
