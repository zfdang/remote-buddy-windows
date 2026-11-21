// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;

namespace RemoteBuddy.Platform.Windows.Input;

public sealed record HidDeviceInfo(string Path, ushort UsagePage, ushort Usage, int InputReportByteLength);

public sealed class HidDeviceReader : IDisposable
{
    private static readonly Guid HidClassGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle hFile, ref OVERLAPPED lpOverlapped,
        out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIo(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, ref uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetUsages(
        int ReportType,
        ushort UsagePage,
        ushort LinkCollection,
        [Out] ushort[] UsageList,
        ref uint UsageLength,
        IntPtr PreparsedData,
        IntPtr Report,
        uint ReportLength);

    public event Action<RemoteButton, bool>? ButtonEvent;

    private SafeFileHandle? _handle;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private readonly HashSet<RemoteButton> _pressedButtons = new();
    private readonly object _lock = new();

    public bool IsRunning => _handle != null && !_handle.IsClosed && !_handle.IsInvalid && _readTask != null && !_readTask.IsCompleted;
    public string? ActivePath { get; private set; }
    public HidDeviceInfo? ActiveDeviceInfo { get; private set; }

    public static List<string> EnumerateHidDevicePaths()
    {
        var result = new List<string>();
        Guid hidGuid = HidClassGuid;
        IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12 /* DIGCF_PRESENT | DIGCF_DEVICEINTERFACE */);
        if (hDevInfo == (IntPtr)(-1)) return result;

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = (uint)Marshal.SizeOf(ifData);

            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index++, ref ifData))
            {
                uint reqSize = 0;
                SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
                if (reqSize > 0)
                {
                    IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                        if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, detail, reqSize, ref reqSize, IntPtr.Zero))
                        {
                            IntPtr pPath = IntPtr.Add(detail, 4);
                            string? path = Marshal.PtrToStringAuto(pPath);
                            if (!string.IsNullOrEmpty(path))
                            {
                                result.Add(path);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }

        return result;
    }

    public static HidDeviceInfo? QueryDeviceInfo(string path)
    {
        try
        {
            using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            if (HidD_GetPreparsedData(handle, out IntPtr pData))
            {
                try
                {
                    if (HidP_GetCaps(pData, out HIDP_CAPS caps) >= 0)
                    {
                        return new HidDeviceInfo(path, caps.UsagePage, caps.Usage, caps.InputReportByteLength);
                    }
                }
                finally
                {
                    HidD_FreePreparsedData(pData);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Failed to query HID device info for '{path}': {ex.Message}", "HidDeviceReader");
        }
        return null;
    }

    public static bool PathMatchesVendorId(string pathUpper, ushort vendorId)
    {
        if (vendorId == 0) return false;
        string v = vendorId.ToString("X4");
        return pathUpper.Contains($"VID&0001{v}") ||
               pathUpper.Contains($"VID&0002{v}") ||
               pathUpper.Contains($"VID&01{v}") ||
               pathUpper.Contains($"VID&02{v}") ||
               pathUpper.Contains($"VID_{v}");
    }

    public static HidDeviceInfo? FindBestDevice(ulong bluetoothAddress, ushort vendorId = 0)
    {
        string addrHex = bluetoothAddress != 0 ? bluetoothAddress.ToString("X").ToUpperInvariant() : string.Empty;

        var paths = EnumerateHidDevicePaths();
        var matching = new List<HidDeviceInfo>();
        foreach (var p in paths)
        {
            string upper = p.ToUpperInvariant();
            bool match = (!string.IsNullOrEmpty(addrHex) && addrHex.Length >= 6 && upper.Contains(addrHex))
                || PathMatchesVendorId(upper, vendorId);

            if (match)
            {
                var info = QueryDeviceInfo(p);
                if (info != null)
                {
                    matching.Add(info);
                }
            }
        }

        if (matching.Count == 0) return null;

        // Prioritize Consumer Control (0x000C, 0x0001) over mouse or generic TLCs
        var consumer = matching.FirstOrDefault(m => m.UsagePage == 0x000C && m.Usage == 0x0001);
        if (consumer != null) return consumer;

        // Then Keyboard (0x0001, 0x0006)
        var keyboard = matching.FirstOrDefault(m => m.UsagePage == 0x0001 && m.Usage == 0x0006);
        if (keyboard != null) return keyboard;

        return matching[0];
    }

    public static string? FindDevicePath(ulong bluetoothAddress, ushort vendorId = 0)
    {
        return FindBestDevice(bluetoothAddress, vendorId)?.Path;
    }

    public bool Start(string path, int reportLength = 0)
    {
        Stop();

        try
        {
            var info = QueryDeviceInfo(path);
            ActiveDeviceInfo = info;
            int effectiveReportLen = reportLength > 0
                ? reportLength
                : (info != null && info.InputReportByteLength > 0 ? info.InputReportByteLength : 64);

            var handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                AppLogger.Warn($"HidDeviceReader: Failed to open '{path}', Win32Error={err}", "HidDeviceReader");
                return false;
            }

            _handle = handle;
            _cts = new CancellationTokenSource();
            ActivePath = path;

            var ct = _cts.Token;
            _readTask = Task.Run(() => ReadLoopNative(handle, effectiveReportLen, ct), ct);
            AppLogger.Info($"HidDeviceReader started successfully for: {path} (ReportLen={effectiveReportLen})", "HidDeviceReader");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"HidDeviceReader exception starting for '{path}'", ex, "HidDeviceReader");
            Stop();
            return false;
        }
    }

    public bool StartForRemote(ulong bluetoothAddress, ushort vendorId = 0)
    {
        var best = FindBestDevice(bluetoothAddress, vendorId);
        if (best == null)
        {
            AppLogger.Warn($"HidDeviceReader: No matching HID device path found for address 0x{bluetoothAddress:X12}", "HidDeviceReader");
            return false;
        }
        return Start(best.Path, best.InputReportByteLength);
    }

    private void ReadLoopNative(SafeFileHandle handle, int reportLen, CancellationToken ct)
    {
        IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
        if (hEvent == IntPtr.Zero)
        {
            AppLogger.Error("HidDeviceReader: Failed to create event for overlapped I/O", component: "HidDeviceReader");
            return;
        }

        try
        {
            byte[] buffer = new byte[reportLen];
            var ov = new OVERLAPPED { hEvent = hEvent };

            while (!ct.IsCancellationRequested && !handle.IsClosed && !handle.IsInvalid)
            {
                uint bytesRead = 0;
                bool ok = ReadFile(handle, buffer, (uint)reportLen, out bytesRead, ref ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 997 /* ERROR_IO_PENDING */)
                    {
                        // Wait for read or cancellation in 100ms chunks to check ct
                        bool completed = false;
                        while (!ct.IsCancellationRequested && !handle.IsClosed)
                        {
                            uint waitRes = WaitForSingleObject(hEvent, 100);
                            if (waitRes == 0 /* WAIT_OBJECT_0 */)
                            {
                                completed = true;
                                break;
                            }
                        }

                        if (!completed || ct.IsCancellationRequested)
                        {
                            CancelIo(handle);
                            break;
                        }

                        if (!GetOverlappedResult(handle, ref ov, out bytesRead, false))
                        {
                            int gerr = Marshal.GetLastWin32Error();
                            if (gerr != 995 /* ERROR_OPERATION_ABORTED */ && !ct.IsCancellationRequested)
                            {
                                AppLogger.Warn($"HidDeviceReader: GetOverlappedResult error {gerr}", "HidDeviceReader");
                            }
                            break;
                        }
                    }
                    else
                    {
                        if (err != 995 /* ERROR_OPERATION_ABORTED */ && !ct.IsCancellationRequested)
                        {
                            AppLogger.Warn($"HidDeviceReader: ReadFile error {err}", "HidDeviceReader");
                        }
                        break;
                    }
                }

                if (bytesRead > 0)
                {
                    var slice = buffer.AsSpan(0, (int)bytesRead);
                    List<RemoteButton> currentButtons;
                    if (G9N9NProtocol.TryParseReport(slice, out currentButtons) ||
                        T1RemoteProtocol.TryParseReport(slice, out currentButtons, out _) ||
                        XiaomiProtocol.TryParseReport(slice, out currentButtons))
                    {
                        DispatchButtons(currentButtons);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                AppLogger.Warn($"HidDeviceReader read loop exception: {ex.Message}", "HidDeviceReader");
            }
        }
        finally
        {
            CloseHandle(hEvent);
        }
    }

    private void DispatchButtons(List<RemoteButton> currentButtons)
    {
        var currentSet = new HashSet<RemoteButton>(currentButtons);
        var released = new List<RemoteButton>();
        var pressed = new List<RemoteButton>();

        lock (_lock)
        {
            foreach (var btn in _pressedButtons)
            {
                if (!currentSet.Contains(btn)) released.Add(btn);
            }
            foreach (var btn in released) _pressedButtons.Remove(btn);

            foreach (var btn in currentSet)
            {
                if (_pressedButtons.Add(btn)) pressed.Add(btn);
            }
        }

        foreach (var btn in released)
        {
            AppLogger.Info($"HidDeviceReader event: {btn} Up", "HidDeviceReader");
            ButtonEvent?.Invoke(btn, false);
        }
        foreach (var btn in pressed)
        {
            AppLogger.Info($"HidDeviceReader event: {btn} Down", "HidDeviceReader");
            ButtonEvent?.Invoke(btn, true);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_handle != null && !_handle.IsClosed && !_handle.IsInvalid)
        {
            try
            {
                CancelIo(_handle);
            }
            catch { }
            try
            {
                _handle.Dispose();
            }
            catch { }
        }

        _handle = null;
        _cts?.Dispose();
        _cts = null;
        ActivePath = null;
        ActiveDeviceInfo = null;

        lock (_lock)
        {
            _pressedButtons.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
