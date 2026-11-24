// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Audio;

public enum DriverReadinessStatus
{
    Ready,
    NotInstalled,
    Disabled,
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
    private static MMDeviceEnumerator? _notificationEnumerator;
    private static EndpointNotificationClient? _notificationClient;

    public static DriverReadinessStatus? StatusOverride { get; set; }
    public static bool IsCableOutputActive { get; private set; }
    public static bool IsCableOutputDisabled { get; private set; }

    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => ResetDeviceCache();
        public void OnDeviceAdded(string pwstrDeviceId) => ResetDeviceCache();
        public void OnDeviceRemoved(string deviceId) => ResetDeviceCache();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            string? cachedId;
            lock (_auditLock)
            {
                cachedId = _cachedCableInputId;
            }
            if (!string.IsNullOrEmpty(cachedId) &&
                string.Equals(pwstrDeviceId, cachedId, StringComparison.OrdinalIgnoreCase))
            {
                ResetDeviceCache();
            }
        }
    }

    private static void EnsureEndpointNotificationRegistered()
    {
        if (_notificationEnumerator != null) return;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var client = new EndpointNotificationClient();
            if (enumerator.RegisterEndpointNotificationCallback(client) == 0)
            {
                _notificationEnumerator = enumerator;
                _notificationClient = client;
            }
            else
            {
                enumerator.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Endpoint notification callback registration skipped: {ex.Message}", "VirtualAudioAuditor");
        }
    }

    public static void Shutdown()
    {
        lock (_auditLock)
        {
            if (_notificationEnumerator != null)
            {
                try
                {
                    if (_notificationClient != null)
                    {
                        _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                    }
                }
                catch { }
                try { _notificationEnumerator.Dispose(); } catch { }
                _notificationEnumerator = null;
                _notificationClient = null;
            }
            _cachedCableInputId = null;
            _lastAuditTime = DateTime.MinValue;
        }
    }

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
            EnsureEndpointNotificationRegistered();
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
            EnsureEndpointNotificationRegistered();
            if (StatusOverride.HasValue)
            {
                return StatusOverride.Value;
            }

            return _lastAuditTime == DateTime.MinValue ? AuditCableInputDevice(forceRefresh: false) : _cachedStatus;
        }
    }

    internal static string DescribeMixFormat(WaveFormat? mixFormat) => mixFormat == null
        ? "unknown"
        : $"{mixFormat.SampleRate}Hz {mixFormat.BitsPerSample}-bit {mixFormat.Channels}ch {mixFormat.Encoding}";

    private static DriverReadinessStatus RunAudit()
    {
        WaveFormat? mixFormat = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var cableInput = GetCableInputDeviceInternal(enumerator);

            if (cableInput == null)
            {
                IsCableOutputActive = false;
                IsCableOutputDisabled = false;
                bool hasDisabled = false;
                bool hasPendingReboot = false;
                string statesSummary = string.Empty;
                var allDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
                foreach (var dev in allDevices)
                {
                    try
                    {
                        if (!dev.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)) continue;
                        statesSummary = string.IsNullOrEmpty(statesSummary) ? dev.State.ToString() : $"{statesSummary}, {dev.State}";
                        if (dev.State == DeviceState.Disabled) hasDisabled = true;
                        else if (dev.State is DeviceState.NotPresent or DeviceState.Unplugged) hasPendingReboot = true;
                    }
                    catch { }
                    finally
                    {
                        dev.Dispose();
                    }
                }

                var status = hasDisabled
                    ? DriverReadinessStatus.Disabled
                    : (hasPendingReboot ? DriverReadinessStatus.RebootRequired : DriverReadinessStatus.NotInstalled);
                AppLogger.Warn($"VB-CABLE audit result: {status} (Active endpoint not found, states=[{statesSummary}])", "VirtualAudioAuditor");
                return status;
            }

            EnsureEndpointUnmutedAndMaxVolume(cableInput);

            bool foundActiveOutput = false;
            bool foundDisabledOutput = false;
            try
            {
                var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All);
                foreach (var dev in captureDevices)
                {
                    try
                    {
                        if (!dev.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)) continue;
                        if (dev.State == DeviceState.Active)
                        {
                            foundActiveOutput = true;
                            EnsureEndpointUnmutedAndMaxVolume(dev);
                        }
                        else if (dev.State == DeviceState.Disabled)
                        {
                            foundDisabledOutput = true;
                        }
                    }
                    catch { }
                    finally
                    {
                        dev.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Failed to check CABLE Output status/volume: {ex.Message}", "VirtualAudioAuditor");
            }

            IsCableOutputActive = foundActiveOutput;
            IsCableOutputDisabled = !foundActiveOutput && foundDisabledOutput;
            if (!foundActiveOutput)
            {
                AppLogger.Warn(
                    foundDisabledOutput
                        ? "VB-CABLE CABLE Output (virtual microphone) is Disabled; speech input tools may receive no audio."
                        : "VB-CABLE CABLE Output (virtual microphone) was not found as an active capture endpoint.",
                    "VirtualAudioAuditor");
            }

            try { mixFormat = cableInput.AudioClient.MixFormat; } catch { }
            if (mixFormat != null && (mixFormat.SampleRate != 16000 || mixFormat.Channels != 1 || mixFormat.BitsPerSample != 16))
            {
                AppLogger.Debug($"VB-CABLE native MixFormat ({DescribeMixFormat(mixFormat)}) differs from 16kHz 16-bit Mono; relying on WASAPI shared-mode resampler", "VirtualAudioAuditor");
            }

            // Real WASAPI test open
            using var testOut = new WasapiOut(cableInput, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
            IWaveProvider dummyProvider = new BufferedWaveProvider(new WaveFormat(16000, 16, 1));
            testOut.Init(dummyProvider);
            AppLogger.Info($"VB-CABLE audit result: Ready (WASAPI test open succeeded, CableOutputActive={foundActiveOutput}, MixFormat: {DescribeMixFormat(mixFormat)})", "VirtualAudioAuditor");
            return DriverReadinessStatus.Ready;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"VB-CABLE audit failed (HResult=0x{ex.HResult:X8}, MixFormat={DescribeMixFormat(mixFormat)}): {ex.Message}", "VirtualAudioAuditor");
            return DriverReadinessStatus.InitializationFailed;
        }
    }

    public static MMDevice? GetCableInputDevice()
    {
        try
        {
            EnsureEndpointNotificationRegistered();
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
                dev?.Dispose();
            }
            catch
            {
                _cachedCableInputId = null;
            }
        }

        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        MMDevice? cableInput = null;
        foreach (var dev in renderDevices)
        {
            try
            {
                if (cableInput == null && dev.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                {
                    cableInput = dev;
                    _cachedCableInputId = dev.ID;
                    continue;
                }
            }
            catch { }
            dev.Dispose();
        }
        return cableInput;
    }
}

/// <summary>
/// Holds output in silence until enough PCM bytes accumulate (~50ms) so WASAPI shared-mode
/// playback starts with a jitter cushion instead of underrunning on initial BLE intervals.
/// </summary>
internal sealed class PreRollWaveProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _inner;
    private readonly int _preRollBytes;
    private readonly object _gateLock = new();
    private bool _released;

    public PreRollWaveProvider(BufferedWaveProvider inner, int preRollBytes = 1600)
    {
        _inner = inner;
        _preRollBytes = Math.Max(0, preRollBytes);
        _released = _preRollBytes == 0;
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public bool IsReleased
    {
        get { lock (_gateLock) return _released; }
    }

    public void ReleasePreRoll()
    {
        lock (_gateLock) _released = true;
    }

    public void Reset()
    {
        lock (_gateLock)
        {
            _inner.ClearBuffer();
            _released = _preRollBytes == 0;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        bool allowRead;
        lock (_gateLock)
        {
            if (!_released && _inner.BufferedBytes >= _preRollBytes)
            {
                _released = true;
            }
            allowRead = _released;
        }

        if (!allowRead)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        return _inner.Read(buffer, offset, count);
    }
}

public sealed class WasapiAudioPlayer : IDisposable
{
    private const int DefaultPreRollBytes = 1600; // 50ms at 16kHz 16-bit mono (32,000 bytes/sec)
    private const int MaxRecoveryAttempts = 2;

    private WasapiOut? _wasapiOut;
    private BufferedWaveProvider? _waveProvider;
    private PreRollWaveProvider? _preRollProvider;
    private readonly WaveFormat _format = new(16000, 16, 1);
    private byte[] _sampleByteBuffer = new byte[4096];
    private readonly object _lock = new();
    private volatile bool _isStopping;
    private volatile bool _disposed;
    private int _recoveryAttempts;

    public event Action<string>? PlaybackRecoveryFailed;

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
        return StartInternal(device, resetRecoveryCount: true);
    }

    private bool StartInternal(MMDevice? device, bool resetRecoveryCount)
    {
        if (_disposed) return false;

        lock (_lock)
        {
            if (resetRecoveryCount)
            {
                _recoveryAttempts = 0;
            }

            device ??= VirtualAudioAuditor.GetCableInputDevice();
            if (device == null)
            {
                AppLogger.Warn("WasapiAudioPlayer.Start failed: CABLE Input device not found", "WasapiAudioPlayer");
                StopInternal(resetRecoveryCount: false);
                return false;
            }

            VirtualAudioAuditor.EnsureEndpointUnmutedAndMaxVolume(device);

            if (_wasapiOut != null && _waveProvider != null && _wasapiOut.PlaybackState == PlaybackState.Playing)
            {
                _preRollProvider?.Reset();
                return true;
            }

            StopInternal(resetRecoveryCount: false);

            WaveFormat? mixFormat = null;
            try { mixFormat = device.AudioClient.MixFormat; } catch { }

            try
            {
                var wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
                var waveProvider = new BufferedWaveProvider(_format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true
                };
                var preRollProvider = new PreRollWaveProvider(waveProvider, DefaultPreRollBytes);

                wasapiOut.PlaybackStopped += OnPlaybackStopped;
                wasapiOut.Init(preRollProvider);
                wasapiOut.Play();

                _isStopping = false;
                _wasapiOut = wasapiOut;
                _waveProvider = waveProvider;
                _preRollProvider = preRollProvider;
                AppLogger.Info($"WasapiAudioPlayer started playback to CABLE Input (Source: 16kHz 16-bit Mono, Native: {VirtualAudioAuditor.DescribeMixFormat(mixFormat)})", "WasapiAudioPlayer");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"WasapiAudioPlayer failed to initialize WASAPI stream (HResult=0x{ex.HResult:X8}, MixFormat={VirtualAudioAuditor.DescribeMixFormat(mixFormat)})", ex, "WasapiAudioPlayer");
                StopInternal(resetRecoveryCount: false);
                return false;
            }
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_isStopping || _disposed) return;

        bool shouldRecover;
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _wasapiOut)) return;
            if (_recoveryAttempts >= MaxRecoveryAttempts)
            {
                shouldRecover = false;
            }
            else
            {
                _recoveryAttempts++;
                shouldRecover = true;
            }
        }

        if (e.Exception != null)
        {
            AppLogger.Warn($"WASAPI playback stopped unexpectedly (HResult=0x{e.Exception.HResult:X8}): {e.Exception.Message}", "WasapiAudioPlayer");
        }
        else
        {
            AppLogger.Warn("WASAPI playback stopped unexpectedly without exception", "WasapiAudioPlayer");
        }

        VirtualAudioAuditor.ResetDeviceCache();

        if (!shouldRecover)
        {
            PlaybackRecoveryFailed?.Invoke("虚拟音频输出流连续中断且恢复次数已耗尽");
            return;
        }

        // Recover off the playback callback thread to avoid Dispose() self-join deadlocks
        _ = Task.Run(() =>
        {
            if (_disposed || _isStopping) return;
            AppLogger.Info("Attempting automatic WASAPI stream recovery...", "WasapiAudioPlayer");
            if (!StartInternal(device: null, resetRecoveryCount: false) && !_disposed && !_isStopping)
            {
                PlaybackRecoveryFailed?.Invoke("虚拟音频输出流自动恢复失败，请检查 VB-CABLE 设备状态");
            }
        });
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

    public async Task DrainAsync(int maxTimeoutMs = 450, CancellationToken ct = default)
    {
        PreRollWaveProvider? preRoll;
        lock (_lock)
        {
            preRoll = _preRollProvider;
        }
        preRoll?.ReleasePreRoll();

        int timeoutMs = Math.Max(maxTimeoutMs, 20);
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            int bytesLeft;
            bool playing;
            lock (_lock)
            {
                playing = _wasapiOut != null && _wasapiOut.PlaybackState == PlaybackState.Playing;
                bytesLeft = _waveProvider?.BufferedBytes ?? 0;
            }

            if (!playing) return;
            if (bytesLeft == 0) break;

            int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0) break;

            try
            {
                await Task.Delay(Math.Min(12, remaining), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        bool stillPlaying;
        lock (_lock)
        {
            stillPlaying = _wasapiOut != null && _wasapiOut.PlaybackState == PlaybackState.Playing;
        }
        if (stillPlaying && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(45, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    public void Drain(int maxTimeoutMs = 450)
    {
        PreRollWaveProvider? preRoll;
        lock (_lock)
        {
            preRoll = _preRollProvider;
        }
        preRoll?.ReleasePreRoll();

        int timeoutMs = Math.Max(maxTimeoutMs, 20);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int bytesLeft;
            bool playing;
            lock (_lock)
            {
                playing = _wasapiOut != null && _wasapiOut.PlaybackState == PlaybackState.Playing;
                bytesLeft = _waveProvider?.BufferedBytes ?? 0;
            }
            if (!playing) return;
            if (bytesLeft == 0) break;
            Thread.Sleep(Math.Min(12, Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds)));
        }
        bool stillPlaying;
        lock (_lock)
        {
            stillPlaying = _wasapiOut != null && _wasapiOut.PlaybackState == PlaybackState.Playing;
        }
        if (stillPlaying)
        {
            Thread.Sleep(45);
        }
    }

    public void Stop() => StopInternal(resetRecoveryCount: true);

    private void StopInternal(bool resetRecoveryCount)
    {
        lock (_lock)
        {
            _isStopping = true;
            if (resetRecoveryCount)
            {
                _recoveryAttempts = 0;
            }
            if (_wasapiOut != null)
            {
                _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
                try
                {
                    _wasapiOut.Stop();
                }
                catch { }
                _wasapiOut.Dispose();
                _wasapiOut = null;
            }
            _waveProvider = null;
            _preRollProvider = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
