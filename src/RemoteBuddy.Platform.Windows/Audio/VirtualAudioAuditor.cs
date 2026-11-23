// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Audio;

public enum DriverReadinessStatus
{
    Ready,
    NotInstalled,
    RebootRequired,
    InitializationFailed
}

public static class VirtualAudioAuditor
{
    private static DriverReadinessStatus _cachedStatus = DriverReadinessStatus.NotInstalled;
    private static DateTime _lastAuditTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly object _auditLock = new();
    private static string? _cachedCableInputId;

    public static DriverReadinessStatus? StatusOverride { get; set; }

    public static void ResetDeviceCache()
    {
        lock (_auditLock)
        {
            _cachedCableInputId = null;
            _lastAuditTime = DateTime.MinValue;
        }
    }

    public static void EnsureEndpointUnmutedAndMaxVolume(MMDevice? device)
    {
        if (device == null) return;
        try
        {
            if (device.AudioEndpointVolume != null)
            {
                if (device.AudioEndpointVolume.Mute)
                {
                    device.AudioEndpointVolume.Mute = false;
                    AppLogger.Info($"Unmuted audio endpoint: {device.FriendlyName}", "VirtualAudioAuditor");
                }
                if (device.AudioEndpointVolume.MasterVolumeLevelScalar < 0.99f)
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                    AppLogger.Info($"Set audio endpoint volume to 100%: {device.FriendlyName}", "VirtualAudioAuditor");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Failed to adjust endpoint volume for {device.FriendlyName}: {ex.Message}", "VirtualAudioAuditor");
        }
    }

    public static DriverReadinessStatus AuditCableInputDevice(bool forceRefresh = false)
    {
        lock (_auditLock)
        {
            if (StatusOverride.HasValue)
            {
                return StatusOverride.Value;
            }

            if (!forceRefresh && DateTime.UtcNow - _lastAuditTime < CacheTtl)
            {
                return _cachedStatus;
            }

            _cachedStatus = RunAudit();
            _lastAuditTime = DateTime.UtcNow;
            return _cachedStatus;
        }
    }

    public static DriverReadinessStatus GetCachedStatus()
    {
        lock (_auditLock)
        {
            if (StatusOverride.HasValue)
            {
                return StatusOverride.Value;
            }

            return _lastAuditTime == DateTime.MinValue ? AuditCableInputDevice(forceRefresh: false) : _cachedStatus;
        }
    }

    private static DriverReadinessStatus RunAudit()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var cableInput = GetCableInputDeviceInternal(enumerator);

            if (cableInput == null)
            {
                var allDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
                bool pending = allDevices.Any(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
                var status = pending ? DriverReadinessStatus.RebootRequired : DriverReadinessStatus.NotInstalled;
                AppLogger.Warn($"VB-CABLE audit result: {status} (Active endpoint not found, pending={pending})", "VirtualAudioAuditor");
                return status;
            }

            EnsureEndpointUnmutedAndMaxVolume(cableInput);

            try
            {
                var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                var cableOutput = captureDevices.FirstOrDefault(d =>
                    d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
                if (cableOutput != null)
                {
                    EnsureEndpointUnmutedAndMaxVolume(cableOutput);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Failed to check CABLE Output volume: {ex.Message}", "VirtualAudioAuditor");
            }

            // Real WASAPI test open
            using var testOut = new WasapiOut(cableInput, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
            IWaveProvider dummyProvider = new BufferedWaveProvider(new WaveFormat(16000, 16, 1));
            testOut.Init(dummyProvider);
            AppLogger.Info("VB-CABLE audit result: Ready (WASAPI test open succeeded)", "VirtualAudioAuditor");
            return DriverReadinessStatus.Ready;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"VB-CABLE audit failed with exception: {ex.Message}", "VirtualAudioAuditor");
            return DriverReadinessStatus.InitializationFailed;
        }
    }

    public static MMDevice? GetCableInputDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return GetCableInputDeviceInternal(enumerator);
        }
        catch
        {
            return null;
        }
    }

    private static MMDevice? GetCableInputDeviceInternal(MMDeviceEnumerator enumerator)
    {
        if (!string.IsNullOrEmpty(_cachedCableInputId))
        {
            try
            {
                var dev = enumerator.GetDevice(_cachedCableInputId);
                if (dev != null && dev.State == DeviceState.Active)
                {
                    return dev;
                }
            }
            catch
            {
                _cachedCableInputId = null;
            }
        }

        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var cableInput = renderDevices.FirstOrDefault(d =>
            d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        if (cableInput != null)
        {
            _cachedCableInputId = cableInput.ID;
        }
        return cableInput;
    }
}

public sealed class WasapiAudioPlayer : IDisposable
{
    private WasapiOut? _wasapiOut;
    private BufferedWaveProvider? _waveProvider;
    private readonly WaveFormat _format = new(16000, 16, 1);
    private byte[] _sampleByteBuffer = new byte[4096];
    private readonly object _lock = new();

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _wasapiOut != null && _wasapiOut.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public bool Start(MMDevice? device = null)
    {
        lock (_lock)
        {
            device ??= VirtualAudioAuditor.GetCableInputDevice();
            if (device == null)
            {
                AppLogger.Warn("WasapiAudioPlayer.Start failed: CABLE Input device not found", "WasapiAudioPlayer");
                Stop();
                return false;
            }

            VirtualAudioAuditor.EnsureEndpointUnmutedAndMaxVolume(device);

            if (_wasapiOut != null && _waveProvider != null && _wasapiOut.PlaybackState == PlaybackState.Playing)
            {
                _waveProvider.ClearBuffer();
                return true;
            }

            Stop();

            try
            {
                _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
                _waveProvider = new BufferedWaveProvider(_format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true
                };

                var mixFormat = device.AudioClient.MixFormat;
                _wasapiOut.Init(_waveProvider);
                _wasapiOut.Play();
                AppLogger.Info($"WasapiAudioPlayer started playback to CABLE Input (Source: 16kHz 16-bit Mono, Native: {mixFormat.SampleRate}Hz {mixFormat.Channels}ch)", "WasapiAudioPlayer");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("WasapiAudioPlayer failed to initialize WASAPI stream", ex, "WasapiAudioPlayer");
                Stop();
                return false;
            }
        }
    }

    public void WriteSamples(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;

        lock (_lock)
        {
            if (_waveProvider == null) return;

            int byteCount = samples.Length * 2;
            if (_sampleByteBuffer.Length < byteCount)
            {
                Array.Resize(ref _sampleByteBuffer, Math.Max(byteCount, _sampleByteBuffer.Length * 2));
            }

            MemoryMarshal.AsBytes(samples).CopyTo(_sampleByteBuffer);
            _waveProvider.AddSamples(_sampleByteBuffer, 0, byteCount);
        }
    }

    public async Task DrainAsync(int timeoutMs = 60, CancellationToken ct = default)
    {
        int waitTime = CalculateDrainWaitTime(timeoutMs);
        try
        {
            await Task.Delay(waitTime, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public void Drain(int timeoutMs = 60)
    {
        int waitTime = CalculateDrainWaitTime(timeoutMs);
        Thread.Sleep(waitTime);
    }

    private int CalculateDrainWaitTime(int timeoutMs)
    {
        lock (_lock)
        {
            if (_waveProvider != null)
            {
                int bytesLeft = _waveProvider.BufferedBytes;
                // 16kHz 16-bit mono = 32,000 bytes/sec
                int msLeft = (int)((bytesLeft / 32000.0) * 1000.0);
                int neededMs = msLeft + 20;
                int maxTimeout = Math.Max(timeoutMs, 20);
                return Math.Clamp(neededMs, 20, maxTimeout);
            }
            return Math.Max(timeoutMs, 0);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_wasapiOut != null)
            {
                try
                {
                    _wasapiOut.Stop();
                }
                catch { }
                _wasapiOut.Dispose();
                _wasapiOut = null;
            }
            _waveProvider = null;
        }
    }

    public void Dispose() => Stop();
}
