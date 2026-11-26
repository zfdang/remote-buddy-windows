// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using System.Windows.Automation;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Platform.Windows.Input;

namespace RemoteBuddy.App.Services;

public sealed record CommonPhraseTarget(
    nint WindowHandle,
    uint ProcessId,
    nint FocusedControlHandle,
    string? FocusedElementRuntimeId);

public interface ICommonPhraseInserter
{
    CommonPhraseTarget? CaptureTarget();
    bool IsTargetActive(CommonPhraseTarget target);
    bool InsertText(string text, CommonPhraseTarget? expectedTarget = null);
}

internal interface IClipboardAccess
{
    bool TryWriteTextWithSnapshot(string text, out string? previousText, out bool hadPreviousText, out uint leaseSequence);
    bool TryRestoreTextIfUnchanged(string? previousText, bool hadPreviousText, uint expectedSequence);
}

/// <summary>
/// Inserts phrases using Unicode clipboard paste (Ctrl+V) with lease-checked restoration
/// so multi-line text preserves newlines instead of emitting physical Enter keys.
/// </summary>
public sealed class WindowsCommonPhraseInserter : ICommonPhraseInserter
{
    internal static readonly TimeSpan DefaultRestoreDelay = TimeSpan.FromMilliseconds(250);

    private readonly IClipboardAccess _clipboard;
    private readonly Func<bool> _sendPasteShortcut;
    private readonly Func<CommonPhraseTarget?> _captureTarget;
    private readonly Func<CommonPhraseTarget, bool> _isTargetActive;
    private readonly Action<TimeSpan, Action> _scheduleRestore;
    private readonly TimeSpan _restoreDelay;
    private readonly object _pasteLock = new();
    private long _restoreGeneration;

    public WindowsCommonPhraseInserter()
        : this(
            new Win32ClipboardAccess(),
            () => NativeInputSender.SendShortcutClick("Ctrl+V"),
            CaptureTargetImpl,
            IsTargetActiveImpl,
            ScheduleRestoreDefault,
            DefaultRestoreDelay)
    {
    }

    internal WindowsCommonPhraseInserter(
        IClipboardAccess clipboard,
        Func<bool> sendPasteShortcut,
        Func<CommonPhraseTarget?> captureTarget,
        Func<CommonPhraseTarget, bool> isTargetActive,
        Action<TimeSpan, Action> scheduleRestore,
        TimeSpan? restoreDelay = null)
    {
        _clipboard = clipboard;
        _sendPasteShortcut = sendPasteShortcut;
        _captureTarget = captureTarget;
        _isTargetActive = isTargetActive;
        _scheduleRestore = scheduleRestore;
        _restoreDelay = restoreDelay ?? DefaultRestoreDelay;
    }

    public static string NormalizeClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
    }

    public CommonPhraseTarget? CaptureTarget() => _captureTarget();

    public bool IsTargetActive(CommonPhraseTarget target) => _isTargetActive(target);

    public bool InsertText(string text, CommonPhraseTarget? expectedTarget = null)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (expectedTarget != null && !IsTargetActive(expectedTarget)) return false;

        string normalized = NormalizeClipboardText(text);
        lock (_pasteLock)
        {
            if (!_clipboard.TryWriteTextWithSnapshot(normalized, out string? previousText, out bool hadPreviousText, out uint leaseSequence))
            {
                AppLogger.Warn("Common phrase insertion aborted: failed to write text to clipboard", "CommonPhraseInserter");
                return false;
            }

            if (expectedTarget != null && !IsTargetActive(expectedTarget))
            {
                _clipboard.TryRestoreTextIfUnchanged(previousText, hadPreviousText, leaseSequence);
                AppLogger.Info("Common phrase insertion cancelled: foreground target changed before paste", "CommonPhraseInserter");
                return false;
            }

            bool pasted = _sendPasteShortcut();
            if (!pasted)
            {
                _clipboard.TryRestoreTextIfUnchanged(previousText, hadPreviousText, leaseSequence);
                AppLogger.Warn("Common phrase insertion aborted: Ctrl+V dispatch failed", "CommonPhraseInserter");
                return false;
            }

            long generation = Interlocked.Increment(ref _restoreGeneration);
            _scheduleRestore(_restoreDelay, () =>
            {
                lock (_pasteLock)
                {
                    if (Volatile.Read(ref _restoreGeneration) != generation) return;
                    _clipboard.TryRestoreTextIfUnchanged(previousText, hadPreviousText, leaseSequence);
                }
            });
            return true;
        }
    }

    private static void ScheduleRestoreDefault(TimeSpan delay, Action callback)
    {
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            try
            {
                callback();
            }
            catch
            {
            }
        }, TaskScheduler.Default);
    }

    private static CommonPhraseTarget? CaptureTargetImpl()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;

        uint threadId = GetWindowThreadProcessId(hwnd, out uint pid);
        if (threadId == 0 || pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return null;
        }

        nint focusHwnd = 0;
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(threadId, ref info))
        {
            focusHwnd = info.hwndFocus;
        }

        (bool available, string? runtimeId) = TryGetFocusedElementRuntimeId(pid);
        return new CommonPhraseTarget(
            WindowHandle: hwnd,
            ProcessId: pid,
            FocusedControlHandle: focusHwnd,
            FocusedElementRuntimeId: available && !string.IsNullOrEmpty(runtimeId) ? runtimeId : null);
    }

    private static bool IsTargetActiveImpl(CommonPhraseTarget target)
    {
        nint currentHwnd = GetForegroundWindow();
        if (currentHwnd == 0 || currentHwnd != target.WindowHandle) return false;

        uint threadId = GetWindowThreadProcessId(currentHwnd, out uint currentPid);
        if (threadId == 0 || currentPid != target.ProcessId || currentPid == (uint)Environment.ProcessId)
        {
            return false;
        }

        if (target.FocusedControlHandle != 0)
        {
            var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != target.FocusedControlHandle)
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(target.FocusedElementRuntimeId))
        {
            (bool available, string? runtimeId) = TryGetFocusedElementRuntimeId(currentPid);
            if (available && !string.Equals(runtimeId, target.FocusedElementRuntimeId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static (bool Available, string? RuntimeId) TryGetFocusedElementRuntimeId(uint expectedProcessId)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return (false, null);

            if ((uint)focused.Current.ProcessId != expectedProcessId)
            {
                return (true, string.Empty);
            }

            int[]? runtimeId = focused.GetRuntimeId();
            if (runtimeId == null || runtimeId.Length == 0)
            {
                return (false, null);
            }

            return (true, string.Join('.', runtimeId));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return (false, null);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);
}

internal sealed class Win32ClipboardAccess : IClipboardAccess
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const nuint MaxRestoreBytes = 16 * 1024 * 1024;

    private static readonly uint ExcludeMonitorFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ClipboardHistoryFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint CloudClipboardFormat = RegisterClipboardFormatW("CanUploadToCloudClipboard");

    public bool TryWriteTextWithSnapshot(string text, out string? previousText, out bool hadPreviousText, out uint leaseSequence)
    {
        previousText = null;
        hadPreviousText = false;
        leaseSequence = 0;

        if (!TryOpenClipboardWithRetry()) return false;
        try
        {
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                if (!TryReadUnicodeText(out previousText))
                {
                    // Avoid silently destroying unreadable/oversized clipboard contents.
                    return false;
                }
                hadPreviousText = true;
            }

            if (!EmptyClipboard()) return false;
            if (!TrySetUnicodeText(text))
            {
                if (hadPreviousText && previousText != null)
                {
                    EmptyClipboard();
                    TrySetUnicodeText(previousText);
                }
                return false;
            }

            SetExclusionMetadata();
            leaseSequence = GetClipboardSequenceNumber();
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public bool TryRestoreTextIfUnchanged(string? previousText, bool hadPreviousText, uint expectedSequence)
    {
        if (GetClipboardSequenceNumber() != expectedSequence) return false;
        if (!TryOpenClipboardWithRetry()) return false;
        try
        {
            if (GetClipboardSequenceNumber() != expectedSequence) return false;
            if (!EmptyClipboard()) return false;

            if (hadPreviousText && previousText != null)
            {
                if (!TrySetUnicodeText(previousText)) return false;
                SetExclusionMetadata();
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpenClipboardWithRetry()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private static bool TryReadUnicodeText(out string? text)
    {
        text = null;
        IntPtr handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return false;

        nuint size = GlobalSize(handle);
        if (size == 0 || size > MaxRestoreBytes) return false;

        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return false;
        try
        {
            text = Marshal.PtrToStringUni(ptr) ?? string.Empty;
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool TrySetUnicodeText(string text)
    {
        int byteCount = checked((text.Length + 1) * sizeof(char));
        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
        if (hGlobal == IntPtr.Zero) return false;

        IntPtr ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }

        try
        {
            char[] chars = text.ToCharArray();
            Marshal.Copy(chars, 0, ptr, chars.Length);
            Marshal.WriteInt16(ptr, chars.Length * sizeof(char), 0);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }

        return true;
    }

    private static void SetExclusionMetadata()
    {
        TrySetDwordFormat(ExcludeMonitorFormat, 1);
        TrySetDwordFormat(ClipboardHistoryFormat, 0);
        TrySetDwordFormat(CloudClipboardFormat, 0);
    }

    private static void TrySetDwordFormat(uint format, int value)
    {
        if (format == 0) return;
        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)sizeof(int));
        if (hGlobal == IntPtr.Zero) return;

        IntPtr ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return;
        }

        try
        {
            Marshal.WriteInt32(ptr, value);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr hMem);
}
