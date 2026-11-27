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

internal sealed record ClipboardFormatEntry(uint Format, byte[] Data);

internal sealed record ClipboardSnapshot(IReadOnlyList<ClipboardFormatEntry> Entries);

internal interface IClipboardAccess
{
    bool TryWriteTextWithSnapshot(string text, out ClipboardSnapshot? snapshot, out uint leaseSequence);
    bool TryRestoreSnapshotIfUnchanged(ClipboardSnapshot? snapshot, uint expectedSequence);
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
            if (!_clipboard.TryWriteTextWithSnapshot(normalized, out ClipboardSnapshot? snapshot, out uint leaseSequence))
            {
                AppLogger.Warn("Common phrase insertion aborted: failed to write text to clipboard", "CommonPhraseInserter");
                return false;
            }

            if (expectedTarget != null && !IsTargetActive(expectedTarget))
            {
                _clipboard.TryRestoreSnapshotIfUnchanged(snapshot, leaseSequence);
                AppLogger.Info("Common phrase insertion cancelled: foreground target changed before paste", "CommonPhraseInserter");
                return false;
            }

            bool pasted = _sendPasteShortcut();
            if (!pasted)
            {
                _clipboard.TryRestoreSnapshotIfUnchanged(snapshot, leaseSequence);
                AppLogger.Warn("Common phrase insertion aborted: Ctrl+V dispatch failed", "CommonPhraseInserter");
                return false;
            }

            long generation = Interlocked.Increment(ref _restoreGeneration);
            _scheduleRestore(_restoreDelay, () =>
            {
                lock (_pasteLock)
                {
                    if (Volatile.Read(ref _restoreGeneration) != generation) return;
                    _clipboard.TryRestoreSnapshotIfUnchanged(snapshot, leaseSequence);
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
    private const uint CF_TEXT = 1;
    private const uint CF_BITMAP = 2;
    private const uint CF_METAFILEPICT = 3;
    private const uint CF_PALETTE = 9;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_ENHMETAFILE = 14;
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const uint CF_OWNERDISPLAY = 0x0080;
    private const uint CF_DSPTEXT = 0x0081;
    private const uint CF_DSPBITMAP = 0x0082;
    private const uint CF_DSPMETAFILEPICT = 0x0083;
    private const uint CF_DSPENHMETAFILE = 0x008E;
    private const uint CF_PRIVATEFIRST = 0x0200;
    private const uint CF_GDIOBJLAST = 0x03FF;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const long MaxSnapshotBytes = 32L * 1024 * 1024;

    private static readonly uint ExcludeMonitorFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ClipboardHistoryFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint CloudClipboardFormat = RegisterClipboardFormatW("CanUploadToCloudClipboard");
    private static readonly uint DataObjectFormat = RegisterClipboardFormatW("DataObject");
    private static readonly uint OlePrivateDataFormat = RegisterClipboardFormatW("Ole Private Data");

    private readonly Func<nint> _resolveOwnerHwnd;
    private nint _cachedOwnerHwnd;

    public Win32ClipboardAccess()
        : this(ResolveApplicationOwnerHwnd)
    {
    }

    internal Win32ClipboardAccess(Func<nint> resolveOwnerHwnd)
    {
        _resolveOwnerHwnd = resolveOwnerHwnd;
    }

    public bool TryWriteTextWithSnapshot(string text, out ClipboardSnapshot? snapshot, out uint leaseSequence)
    {
        snapshot = null;
        leaseSequence = 0;

        nint ownerHwnd = GetValidOwnerHwnd();
        if (ownerHwnd == 0)
        {
            AppLogger.Warn("Clipboard write aborted: no valid application owner window handle", "CommonPhraseInserter");
            return false;
        }

        if (!TryOpenClipboardWithRetry(ownerHwnd)) return false;
        try
        {
            if (!TryCaptureSnapshotLocked(out snapshot))
            {
                // Refuse to clear clipboard formats that cannot be completely preserved.
                return false;
            }

            if (!EmptyClipboard()) return false;
            if (!TrySetUnicodeText(text))
            {
                if (snapshot is { Entries.Count: > 0 } && EmptyClipboard())
                {
                    TryRestoreEntriesLocked(snapshot);
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

    public bool TryRestoreSnapshotIfUnchanged(ClipboardSnapshot? snapshot, uint expectedSequence)
    {
        if (GetClipboardSequenceNumber() != expectedSequence) return false;

        nint ownerHwnd = GetValidOwnerHwnd();
        if (ownerHwnd == 0) return false;

        if (!TryOpenClipboardWithRetry(ownerHwnd)) return false;
        try
        {
            if (GetClipboardSequenceNumber() != expectedSequence) return false;
            if (!EmptyClipboard()) return false;

            if (snapshot is { Entries.Count: > 0 })
            {
                return TryRestoreEntriesLocked(snapshot);
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private nint GetValidOwnerHwnd()
    {
        nint cached = Volatile.Read(ref _cachedOwnerHwnd);
        if (cached != 0 && IsWindow(cached)) return cached;

        nint resolved = _resolveOwnerHwnd();
        if (resolved != 0 && IsWindow(resolved))
        {
            Volatile.Write(ref _cachedOwnerHwnd, resolved);
            return resolved;
        }

        Volatile.Write(ref _cachedOwnerHwnd, 0);
        return 0;
    }

    private static nint ResolveApplicationOwnerHwnd()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return 0;

        nint ReadMainWindowHandle()
        {
            if (app.MainWindow is not { } mainWindow) return 0;
            var helper = new System.Windows.Interop.WindowInteropHelper(mainWindow);
            return helper.Handle != 0 ? helper.Handle : helper.EnsureHandle();
        }

        if (app.Dispatcher.CheckAccess())
        {
            return ReadMainWindowHandle();
        }

        try
        {
            return app.Dispatcher.Invoke(ReadMainWindowHandle);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryOpenClipboardWithRetry(nint ownerHwnd)
    {
        if (ownerHwnd == 0) return false;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(ownerHwnd)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    internal static bool TryCaptureSnapshotFromFormats(
        IReadOnlyList<uint> formats,
        Func<uint, byte[]?> readFormatBytes,
        out ClipboardSnapshot? snapshot)
    {
        snapshot = null;
        if (formats.Count == 0)
        {
            snapshot = new ClipboardSnapshot([]);
            return true;
        }

        bool hasDib = formats.Contains(CF_DIB) || formats.Contains(CF_DIBV5);
        long totalBytes = 0;
        var entries = new List<ClipboardFormatEntry>(formats.Count);

        foreach (uint fmt in formats)
        {
            if (fmt is CF_BITMAP or CF_PALETTE)
            {
                if (hasDib) continue; // Windows synthesizes CF_BITMAP/CF_PALETTE from CF_DIB/CF_DIBV5.
                return false;
            }

            if (IsUnsupportedHandleFormat(fmt))
            {
                return false;
            }

            if ((DataObjectFormat != 0 && fmt == DataObjectFormat) ||
                (OlePrivateDataFormat != 0 && fmt == OlePrivateDataFormat))
            {
                continue;
            }

            byte[]? data = readFormatBytes(fmt);
            if (data == null || data.Length == 0 || totalBytes + data.Length > MaxSnapshotBytes)
            {
                return false;
            }

            totalBytes += data.Length;
            entries.Add(new ClipboardFormatEntry(fmt, data));
        }

        if (entries.Count == 0)
        {
            return false;
        }

        snapshot = new ClipboardSnapshot(entries);
        return true;
    }

    private static bool TryCaptureSnapshotLocked(out ClipboardSnapshot? snapshot)
    {
        snapshot = null;
        var formats = new List<uint>();
        uint fmt = 0;
        while (true)
        {
            fmt = EnumClipboardFormats(fmt);
            if (fmt == 0)
            {
                if (Marshal.GetLastWin32Error() != 0) return false;
                break;
            }
            formats.Add(fmt);
        }

        return TryCaptureSnapshotFromFormats(formats, ReadClipboardFormatBytesLocked, out snapshot);
    }

    private static bool IsUnsupportedHandleFormat(uint format) =>
        format is CF_METAFILEPICT or CF_ENHMETAFILE or CF_OWNERDISPLAY
            or CF_DSPTEXT or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE ||
        format is >= CF_PRIVATEFIRST and <= CF_GDIOBJLAST;

    private static byte[]? ReadClipboardFormatBytesLocked(uint format)
    {
        IntPtr handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;

        nuint size = GlobalSize(handle);
        if (size == 0 || size > (nuint)MaxSnapshotBytes) return null;

        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            byte[] data = new byte[(int)size];
            Marshal.Copy(ptr, data, 0, data.Length);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool TryRestoreEntriesLocked(ClipboardSnapshot snapshot)
    {
        bool allRestored = true;
        foreach (var entry in snapshot.Entries)
        {
            if (!TrySetClipboardBytes(entry.Format, entry.Data))
            {
                allRestored = false;
            }
        }
        return allRestored;
    }

    private static bool TrySetUnicodeText(string text)
    {
        byte[] bytes = new byte[checked((text.Length + 1) * sizeof(char))];
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return TrySetClipboardBytes(CF_UNICODETEXT, bytes);
    }

    private static bool TrySetClipboardBytes(uint format, byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (hGlobal == IntPtr.Zero) return false;

        IntPtr ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }

        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
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

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

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
