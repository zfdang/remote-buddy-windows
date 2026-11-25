// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Input;

/// <summary>Stops a verified remote's held F5 from contaminating its IME chord.</summary>
public sealed class RemoteVoiceKeyGuard(Func<bool> voiceSessionActive) : IDisposable
{
    private const long MaxHoldDurationMs = 5000; // Expire a stale hold once the voice session is no longer active.
    private const long AbsoluteMaxHoldDurationMs = 310_000; // Hard safety cap (>5 min) prevents permanent key lock if session status ever wedged.

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    private IntPtr _hook;
    private volatile bool _armed;
    private long _armedAt;
    private volatile bool _ownsF5;
    private long _holdStartedAt;
    private int _blockedDowns;
    private int _blockedUps;
    private volatile bool _isSuppressionEnabled = true;
    private ushort[] _requiredModifierVks = [];
    private int _allowedUsbF5Downs;
    private int _allowedUsbF5Ups;

    internal Func<ushort, bool> IsKeyDownForTest { get; set; } = NativeInputSender.IsKeyPhysicallyDown;

    public void BeginShortcutHold(string shortcut)
    {
        BeginShortcutHoldCore(shortcut, allowedUsbDowns: 0, allowedUsbUps: 0);
    }

    public void BeginUsbShortcutHold(string shortcut, bool isHoldToTalk)
    {
        BeginShortcutHoldCore(shortcut, allowedUsbDowns: 1, allowedUsbUps: isHoldToTalk ? 0 : 1);
    }

    private void BeginShortcutHoldCore(string shortcut, int allowedUsbDowns, int allowedUsbUps)
    {
        if (NativeInputSender.TryParseShortcut(shortcut, out var modifiers, out var key) && key == 0x74)
        {
            _requiredModifierVks = modifiers.ToArray();
            Volatile.Write(ref _allowedUsbF5Downs, allowedUsbDowns);
            Volatile.Write(ref _allowedUsbF5Ups, allowedUsbUps);
            _ownsF5 = true;
        }
        else
        {
            _requiredModifierVks = [];
            Volatile.Write(ref _allowedUsbF5Downs, 0);
            Volatile.Write(ref _allowedUsbF5Ups, 0);
            _ownsF5 = false;
        }
        Volatile.Write(ref _holdStartedAt, Environment.TickCount64);
        Interlocked.Exchange(ref _blockedDowns, 0);
        Interlocked.Exchange(ref _blockedUps, 0);
    }

    public void EndShortcutHold()
    {
        if (_ownsF5)
            AppLogger.Info($"F5 hold finished: PhysicalDownsBlocked={Volatile.Read(ref _blockedDowns)}, PhysicalUpsBlocked={Volatile.Read(ref _blockedUps)}", "RemoteVoiceKeyGuard");
        _ownsF5 = false;
        _requiredModifierVks = [];
        Volatile.Write(ref _allowedUsbF5Downs, 0);
        Volatile.Write(ref _allowedUsbF5Ups, 0);
    }

    private bool AreRequiredModifiersDown()
    {
        var modifiers = _requiredModifierVks;
        for (int i = 0; i < modifiers.Length; i++)
        {
            if (!IsKeyDownForTest(modifiers[i])) return false;
        }
        return true;
    }

    public bool IsSuppressionEnabled
    {
        get => _isSuppressionEnabled;
        set => _isSuppressionEnabled = value;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? module);

    private HookProc? _rootedCallback;

    // Install on the UI thread, which owns the hook's message pump.
    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _rootedCallback = OnKeyboard;
        _hook = SetWindowsHookExW(13, _rootedCallback, GetModuleHandleW(null), 0);
        return _hook != IntPtr.Zero;
    }

    // Called only after Raw Input has verified the physical remote's identity.
    public void Arm()
    {
        if (!IsSuppressionEnabled) return;
        Volatile.Write(ref _armedAt, Environment.TickCount64);
        _armed = true;
        bool released = NativeInputSender.SendKey(0x74, true);
        AppLogger.Info($"Remote F5 guard armed; cleanup key-up accepted={released}", "RemoteVoiceKeyGuard");
    }

    public void Clear()
    {
        _armed = false;
        EndShortcutHold();
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && Marshal.ReadInt32(data) == 0x74)
            {
                uint flags = (uint)Marshal.ReadInt32(data, 8);
                // Let injected cleanup events through. Observe no other keys.
                if ((flags & 0x10) == 0)
                {
                    // The app owns the injected F5 until audio drain completes.
                    // Physical repeats can re-trigger the IME; physical key-up
                    // can release the injected hold before our balanced key-up.
                    if (_ownsF5)
                    {
                        long heldMs = Environment.TickCount64 - Volatile.Read(ref _holdStartedAt);
                        if (heldMs > AbsoluteMaxHoldDurationMs || (heldMs > MaxHoldDurationMs && !voiceSessionActive()))
                        {
                            _ownsF5 = false;
                        }
                        else
                        {
                            bool isKeyUp = message.ToInt64() is 0x101 or 0x105;
                            if (isKeyUp)
                            {
                                if (Interlocked.Decrement(ref _allowedUsbF5Ups) >= 0)
                                {
                                    return CallNextHookEx(_hook, code, message, data);
                                }
                                Interlocked.Increment(ref _allowedUsbF5Ups);
                                Interlocked.Increment(ref _blockedUps);
                            }
                            else
                            {
                                if (Volatile.Read(ref _allowedUsbF5Downs) > 0 &&
                                    AreRequiredModifiersDown() &&
                                    Interlocked.Decrement(ref _allowedUsbF5Downs) >= 0)
                                {
                                    return CallNextHookEx(_hook, code, message, data);
                                }
                                Interlocked.Increment(ref _blockedDowns);
                            }
                            return (IntPtr)1;
                        }
                    }
                    if (!IsSuppressionEnabled) return CallNextHookEx(_hook, code, message, data);
                    bool sessionActive = voiceSessionActive();
                    if (!sessionActive && _armed && Environment.TickCount64 - Volatile.Read(ref _armedAt) > 2000)
                        _armed = false;
                    if (sessionActive || _armed)
                    {
                        if (message.ToInt64() is 0x101 or 0x105) _armed = false;
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch { }

        return CallNextHookEx(_hook, code, message, data);
    }

    ~RemoteVoiceKeyGuard()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        EndShortcutHold();
        _armed = false;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        if (disposing)
        {
            GC.KeepAlive(_rootedCallback);
        }
    }
}
