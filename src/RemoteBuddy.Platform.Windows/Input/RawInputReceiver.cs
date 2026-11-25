// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;

namespace RemoteBuddy.Platform.Windows.Input;

public sealed class RawInputReceiver : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int RID_INPUT = 0x10000003;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int RIDEV_DEVNOTIFY = 0x00002000;
    private const int RIDEV_REMOVE = 0x00000001;
    private const byte KeyboardReportId = 0xFF;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_DEVICEINFO = 0x2000000b;

    private static readonly ConcurrentDictionary<string, Guid?> _containerIdCache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIM_TYPEHID = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize);

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

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", SetLastError = true)]
    private static extern uint CM_Get_Device_Interface_Property(
        [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceInterface,
        ref DEVPROPKEY PropertyKey,
        out uint PropertyType,
        [Out] byte[] PropertyBuffer,
        ref uint PropertyBufferSize,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint devInst, ref DEVPROPKEY key,
        out uint propertyType, [Out] byte[] buffer, ref uint bufferSize, uint flags);

    public event Action<IntPtr, RemoteButton, bool>? ButtonEvent;
    public event Action<IntPtr>? PhysicalVoiceKeyPressed;

    public Func<IntPtr, string, bool>? DeviceFilter { get; set; }
    public Func<IntPtr, string, RemoteModelType>? DeviceModelResolver { get; set; }

    private IntPtr _hwnd;
    private bool _isRegistered;
    private readonly Dictionary<IntPtr, string> _devicePathCache = new();
    private readonly Dictionary<IntPtr, IntPtr> _preparsedDataCache = new();
    private readonly Dictionary<IntPtr, Dictionary<byte, HashSet<RemoteButton>>> _deviceReportButtons = new();
    private readonly HashSet<(IntPtr Device, string Outcome)> _loggedHidReports = new();
    private readonly HashSet<IntPtr> _devicesHoldingF5 = new();
    private readonly object _lock = new();

    public static Guid? GetDeviceContainerId(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;

        if (_containerIdCache.TryGetValue(devicePath, out var cached))
        {
            return cached;
        }

        var resolved = ResolveDeviceContainerIdInternal(devicePath);
        if (resolved.HasValue)
        {
            _containerIdCache[devicePath] = resolved;
        }

        return resolved;
    }

    private static Guid? ResolveDeviceContainerIdInternal(string devicePath)
    {
        try
        {
            // ContainerId belongs to the device node, not its interface property store.
            var instanceKey = new DEVPROPKEY
            {
                fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
                pid = 256 // DEVPKEY_Device_InstanceId
            };
            uint instanceSize = 0;
            CM_Get_Device_Interface_Property(devicePath, ref instanceKey, out _, [], ref instanceSize, 0);
            if (instanceSize == 0 || instanceSize > 65536) return null;
            byte[] instanceBuffer = new byte[instanceSize];
            if (CM_Get_Device_Interface_Property(devicePath, ref instanceKey, out uint instanceType,
                    instanceBuffer, ref instanceSize, 0) != 0 || instanceType != 0x12)
                return null;
            string instanceId = Encoding.Unicode.GetString(instanceBuffer, 0, (int)instanceSize).TrimEnd('\0');
            if (string.IsNullOrEmpty(instanceId) || CM_Locate_DevNode(out uint devInst, instanceId, 0) != 0)
                return null;

            var key = new DEVPROPKEY
            {
                fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
                pid = 2 // DEVPKEY_Device_ContainerId
            };

            uint bufferSize = 16;
            byte[] buffer = new byte[16];
            uint propType = 0;
            uint cr = CM_Get_DevNode_Property(devInst, ref key, out propType, buffer, ref bufferSize, 0);
            if (cr == 0 && propType == 0x0D && bufferSize == 16)
            {
                var containerId = new Guid(buffer);
                return containerId == Guid.Empty ? null : containerId;
            }
        }
        catch
        {
            // Graceful fallback for non-Windows test environments
        }

        return null;
    }

    public void ResetDeviceState(IntPtr? hDevice = null)
    {
        var releasesToDispatch = new List<(IntPtr Device, RemoteButton Button)>();
        lock (_lock)
        {
            if (hDevice.HasValue)
            {
                if (_deviceReportButtons.TryGetValue(hDevice.Value, out var reportMap))
                {
                    foreach (var (_, btns) in reportMap)
                    {
                        foreach (var btn in btns) releasesToDispatch.Add((hDevice.Value, btn));
                        btns.Clear();
                    }
                    _deviceReportButtons.Remove(hDevice.Value);
                }
                if (_devicePathCache.Remove(hDevice.Value, out var oldPath) && !string.IsNullOrEmpty(oldPath))
                {
                    _containerIdCache.TryRemove(oldPath, out _);
                }
                if (_preparsedDataCache.Remove(hDevice.Value, out var preparsed) && preparsed != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(preparsed);
                }
                _devicesHoldingF5.Remove(hDevice.Value);
                _loggedHidReports.RemoveWhere(entry => entry.Device == hDevice.Value);
            }
            else
            {
                foreach (var (dev, reportMap) in _deviceReportButtons)
                {
                    foreach (var (_, btns) in reportMap)
                    {
                        foreach (var btn in btns) releasesToDispatch.Add((dev, btn));
                    }
                }
                foreach (var oldPath in _devicePathCache.Values)
                {
                    if (!string.IsNullOrEmpty(oldPath)) _containerIdCache.TryRemove(oldPath, out _);
                }
                foreach (var preparsed in _preparsedDataCache.Values)
                {
                    if (preparsed != IntPtr.Zero) Marshal.FreeHGlobal(preparsed);
                }
                _preparsedDataCache.Clear();
                _deviceReportButtons.Clear();
                _devicePathCache.Clear();
                _devicesHoldingF5.Clear();
                _loggedHidReports.Clear();
            }
        }

        foreach (var (dev, btn) in releasesToDispatch)
        {
            ButtonEvent?.Invoke(dev, btn, false);
        }
    }

    public void InvalidateDeviceCache(IntPtr hDevice)
    {
        string? cachedPath = null;
        lock (_lock)
        {
            _devicePathCache.Remove(hDevice, out cachedPath);
            _loggedHidReports.RemoveWhere(entry => entry.Device == hDevice);
            if (_preparsedDataCache.Remove(hDevice, out var preparsed) && preparsed != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(preparsed);
            }
        }
        if (!string.IsNullOrEmpty(cachedPath))
        {
            _containerIdCache.TryRemove(cachedPath, out _);
        }
        else if (hDevice != IntPtr.Zero)
        {
            string? path = GetDevicePath(hDevice);
            if (!string.IsNullOrEmpty(path)) _containerIdCache.TryRemove(path, out _);
        }
    }

    public static RemoteModelType InferModelTypeFromDevice(IntPtr hDevice, string devicePath)
    {
        if (GetHidDeviceInfo(hDevice, out uint vid, out _))
        {
            if (vid == G9N9NProtocol.VendorId) return RemoteModelType.GoogleG9N9N;
            if (vid == T1RemoteProtocol.VendorId) return RemoteModelType.T1Remote;
            if (vid == 0x2717) return RemoteModelType.XiaomiRC003;
        }

        if (!string.IsNullOrEmpty(devicePath))
        {
            string upper = devicePath.ToUpperInvariant();
            if (upper.Contains("VID_18D1") || upper.Contains("VID&000218D1") || upper.Contains("VID&0118D1") || upper.Contains("PID_9450") || upper.Contains("PID&9450"))
                return RemoteModelType.GoogleG9N9N;
            if (upper.Contains("VID_620A") || upper.Contains("VID&0002620A") || upper.Contains("VID&01620A") || upper.Contains("T1-REMOTE"))
                return RemoteModelType.T1Remote;
            if (upper.Contains("VID_2717") || upper.Contains("VID&00022717") || upper.Contains("VID&012717") || upper.Contains("PID_32B8") || upper.Contains("PID&32B8"))
                return RemoteModelType.XiaomiRC003;
        }

        return RemoteModelType.Unknown;
    }

    public static bool GetHidDeviceInfo(IntPtr hDevice, out uint vendorId, out uint productId)
    {
        vendorId = 0;
        productId = 0;
        if (hDevice == IntPtr.Zero) return false;

        try
        {
            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, IntPtr.Zero, ref size);
            if (size == 0) return false;

            IntPtr pData = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.WriteInt32(pData, (int)size); // cbSize
                if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, pData, ref size) == size)
                {
                    uint dwType = (uint)Marshal.ReadInt32(pData, 4);
                    if (dwType == 2) // RIM_TYPEHID
                    {
                        // In RID_DEVICE_INFO, the union starts at offset 8
                        vendorId = (uint)Marshal.ReadInt32(pData, 8);
                        productId = (uint)Marshal.ReadInt32(pData, 12);
                        return true;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
            }
        }
        catch
        {
            // Graceful fallback
        }

        return false;
    }

    public static bool IsKnownRemoteDevice(IntPtr hDevice, string devicePath)
    {
        if (GetHidDeviceInfo(hDevice, out uint vid, out _))
        {
            if (vid == G9N9NProtocol.VendorId || vid == T1RemoteProtocol.VendorId || vid == 0x2717)
            {
                return true;
            }
            if (vid != 0)
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(devicePath))
        {
            string upper = devicePath.ToUpperInvariant();
            if (upper.Contains("VID_18D1") || upper.Contains("VID&000218D1") || upper.Contains("VID&0118D1") || upper.Contains("VID&0218D1") ||
                upper.Contains("VID_620A") || upper.Contains("VID&0002620A") || upper.Contains("VID&01620A") || upper.Contains("VID&02620A") ||
                upper.Contains("VID_2717") || upper.Contains("VID&00022717") || upper.Contains("VID&012717") || upper.Contains("VID&022717") ||
                upper.Contains("PID_9450") || upper.Contains("PID&9450") ||
                upper.Contains("PID_32B8") || upper.Contains("PID&32B8") ||
                upper.Contains("T1-REMOTE"))
            {
                return true;
            }
            // Standard PC keyboards / other USB devices with explicit vendor IDs or ACPI paths
            if (upper.Contains("VID_") || upper.Contains("VID&") || upper.Contains("ACPI#PNP0303"))
            {
                return false;
            }
        }

        return false;
    }

    public static bool EvaluateDeviceMatch(Guid? targetContainerId, IntPtr hDevice, string devicePath, ulong targetBluetoothAddress = 0, bool requireExactIdentity = false)
    {
        // SendInput events have no physical source. Admitting them feeds our
        // shortcut output back into the mapping pipeline indefinitely.
        if (hDevice == IntPtr.Zero) return false;

        // 1. Direct match by zero-padded 12-char Bluetooth MAC address in device path
        if (targetBluetoothAddress != 0)
        {
            string macHex = targetBluetoothAddress.ToString("X12");
            if (devicePath.Contains(macHex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        Guid? deviceContainerId = GetDeviceContainerId(devicePath);
        if (requireExactIdentity)
            return targetContainerId.HasValue && targetContainerId == deviceContainerId;

        if (targetContainerId.HasValue)
        {
            if (deviceContainerId.HasValue)
            {
                // Container ID is known for both target and incoming device: require exact container match.
                return deviceContainerId.Value == targetContainerId.Value;
            }

            // Target remote is known, but incoming devnode does not report a container ID property.
            // Require verified remote identity (VID / device path); unverified devices (standard keyboards) must be rejected.
            return IsKnownRemoteDevice(hDevice, devicePath);
        }

        // 2. If target remote Container ID is not yet known (e.g. before pairing), only admit verified remotes
        if (IsKnownRemoteDevice(hDevice, devicePath))
        {
            return true;
        }

        // All other unverified devices (standard keyboards, mice, other USB peripherals) are rejected
        return false;
    }

    public static string? GetDevicePath(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return null;
        uint charCount = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref charCount);
        if (charCount == 0) return null;

        IntPtr pData = Marshal.AllocHGlobal((int)(charCount * sizeof(char)));
        try
        {
            uint res = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, pData, ref charCount);
            if (res > 0 && res != uint.MaxValue)
            {
                return Marshal.PtrToStringUni(pData);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
        }
    }

    public bool Register(IntPtr hwnd)
    {
        _hwnd = hwnd;
        var devices = new RAWINPUTDEVICE[]
        {
            new()
            {
                usUsagePage = 0x0C, // Consumer Control
                usUsage = 0x01,     // Consumer Control Device
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = hwnd
            },
            new()
            {
                usUsagePage = 0x01, // Generic Desktop Controls
                usUsage = 0x06,     // Keyboard
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = hwnd
            }
        };

        _isRegistered = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (_isRegistered)
        {
            AppLogger.Info($"RawInput devices successfully registered for HWND 0x{hwnd:X}", "RawInputReceiver");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            AppLogger.Error($"Failed to register RawInput devices for HWND 0x{hwnd:X}. Win32Error={err}", component: "RawInputReceiver");
        }
        return _isRegistered;
    }

    public void ProcessMessage(IntPtr lParam)
    {
        uint dwSize = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (dwSize == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != dwSize)
            {
                return;
            }

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            IntPtr hDevice = header.hDevice;
            if (hDevice == IntPtr.Zero)
            {
                return; // Discard synthetic SendInput loops immediately
            }

            string devicePath;
            lock (_lock)
            {
                if (!_devicePathCache.TryGetValue(hDevice, out var path) || string.IsNullOrEmpty(path))
                {
                    path = GetDevicePath(hDevice) ?? string.Empty;
                    if (!string.IsNullOrEmpty(path))
                    {
                        _devicePathCache[hDevice] = path;
                    }
                }
                devicePath = path;
            }

            bool filterRejected = DeviceFilter != null && !DeviceFilter(hDevice, devicePath);
            if (filterRejected)
            {
                bool hasActiveState;
                lock (_lock)
                {
                    hasActiveState = _devicesHoldingF5.Contains(hDevice) || _deviceReportButtons.ContainsKey(hDevice);
                }
                if (!hasActiveState)
                {
                    return;
                }
            }

            int headerSize = Marshal.SizeOf<RAWINPUTHEADER>();

            if (header.dwType == RIM_TYPEKEYBOARD)
            {
                if ((int)dwSize < headerSize + Marshal.SizeOf<RAWKEYBOARD>()) return;

                ushort flags = (ushort)Marshal.ReadInt16(buffer, headerSize + 2);
                ushort vkey = (ushort)Marshal.ReadInt16(buffer, headerSize + 6);
                bool isDown = (flags & 1) == 0;
                if (filterRejected && isDown)
                {
                    return;
                }

                if (vkey == 0x74)
                {
                    bool shouldArm = false;
                    lock (_lock)
                    {
                        if (isDown)
                        {
                            shouldArm = _devicesHoldingF5.Add(hDevice);
                        }
                        else
                        {
                            _devicesHoldingF5.Remove(hDevice);
                        }
                    }

                    if (shouldArm)
                    {
                        PhysicalVoiceKeyPressed?.Invoke(hDevice);
                    }
                    return; // ATVV owns voice session start/stop.
                }

                ushort makeCode = (ushort)Marshal.ReadInt16(buffer, headerSize);
                var btn = MapVirtualKeyToRemoteButton(vkey, makeCode);
                if (btn == RemoteButton.None)
                {
                    AppLogger.Info($"Unmapped remote keyboard event: VKey=0x{vkey:X2}, MakeCode=0x{(ushort)Marshal.ReadInt16(buffer, headerSize):X2}, Down={isDown}", "RawInputReceiver");
                    return;
                }

                bool dispatchKeyboard = false;
                lock (_lock)
                {
                    if (!_deviceReportButtons.TryGetValue(hDevice, out var reportMap))
                    {
                        if (filterRejected) return;
                        reportMap = new Dictionary<byte, HashSet<RemoteButton>>();
                        _deviceReportButtons[hDevice] = reportMap;
                    }

                    if (!reportMap.TryGetValue(KeyboardReportId, out var pressedButtons))
                    {
                        if (filterRejected) return;
                        pressedButtons = new HashSet<RemoteButton>();
                        reportMap[KeyboardReportId] = pressedButtons;
                    }

                    if (isDown)
                    {
                        dispatchKeyboard = pressedButtons.Add(btn);
                    }
                    else
                    {
                        dispatchKeyboard = pressedButtons.Remove(btn);
                    }
                }

                if (dispatchKeyboard)
                {
                    AppLogger.Info($"RawInput Keyboard event: {btn} {(isDown ? "Down" : "Up")} (vkey=0x{vkey:X2}, hDevice=0x{hDevice:X})", "RawInputReceiver");
                    ButtonEvent?.Invoke(hDevice, btn, isDown);
                }
                return;
            }

            if (header.dwType != RIM_TYPEHID) return;

            int hidHeaderSize = Marshal.SizeOf<RAWHID>();
            int dataOffset = headerSize + hidHeaderSize;

            if ((int)dwSize < dataOffset) return;

            uint dwSizeHid = (uint)Marshal.ReadInt32(buffer, headerSize);
            uint dwCount = (uint)Marshal.ReadInt32(buffer, headerSize + 4);

            ulong totalRawBytesU = (ulong)dwSizeHid * dwCount;
            if (dwSizeHid == 0 || totalRawBytesU == 0 || totalRawBytesU > int.MaxValue ||
                (ulong)dataOffset + totalRawBytesU > dwSize) return;
            int totalRawBytes = (int)totalRawBytesU;

            byte[] rawBytes = new byte[totalRawBytes];
            Marshal.Copy(IntPtr.Add(buffer, dataOffset), rawBytes, 0, totalRawBytes);

            int reportSize = (int)dwSizeHid;
            var hidEvents = new List<(RemoteButton Button, bool IsDown, byte ReportId)>();
            bool notifyVoiceHold = false;

            var modelType = DeviceModelResolver?.Invoke(hDevice, devicePath) ?? RemoteModelType.Unknown;
            if (modelType == RemoteModelType.Unknown)
            {
                modelType = InferModelTypeFromDevice(hDevice, devicePath);
            }

            lock (_lock)
            {
                if (!_deviceReportButtons.TryGetValue(hDevice, out var reportMap))
                {
                    if (filterRejected) return;
                    reportMap = new Dictionary<byte, HashSet<RemoteButton>>();
                    _deviceReportButtons[hDevice] = reportMap;
                }

                try
                {
                    if (!_preparsedDataCache.TryGetValue(hDevice, out IntPtr preparsed))
                {
                    uint preparsedSize = 0;
                    GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize);
                    if (preparsedSize > 0 && preparsedSize <= 65536)
                    {
                        IntPtr alloc = Marshal.AllocHGlobal((int)preparsedSize);
                        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, alloc, ref preparsedSize) == preparsedSize)
                        {
                            preparsed = alloc;
                            _preparsedDataCache[hDevice] = preparsed;
                        }
                        else
                        {
                            Marshal.FreeHGlobal(alloc);
                        }
                    }
                }

                for (int i = 0; i < (int)dwCount; i++)
                {
                    var slice = rawBytes.AsSpan(i * reportSize, reportSize);
                    bool recognized = false;
                    List<RemoteButton> currentButtons = new();

                    // 1. First try parsing through Windows HID API (HidP_GetUsages)
                    if (preparsed != IntPtr.Zero)
                    {
                        uint usageLength = 16;
                        ushort[] usages = new ushort[usageLength];
                        IntPtr pReport = IntPtr.Add(buffer, dataOffset + (i * reportSize));
                        if (HidP_GetUsages(0, 0x0C, 0, usages, ref usageLength, preparsed, pReport, (uint)reportSize) >= 0 && usageLength > 0)
                        {
                            for (int u = 0; u < usageLength; u++)
                            {
                                if (modelType == RemoteModelType.T1Remote && usages[u] == 0x0221) continue;
                                if (G9N9NProtocol.ConsumerButtons.TryGetValue(usages[u], out var btn))
                                {
                                    currentButtons.Add(btn);
                                }
                            }
                            recognized = true;
                        }
                    }

                    // 2. Fallback to model-appropriate protocol report parser
                    if (!recognized || currentButtons.Count == 0)
                    {
                        if (modelType is RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE)
                        {
                            if (G9N9NProtocol.TryParseReport(slice, out currentButtons))
                            {
                                recognized = true;
                            }
                        }
                        else if (modelType == RemoteModelType.T1Remote)
                        {
                            if (T1RemoteProtocol.TryParseReport(slice, out currentButtons, out bool voiceHoldPressed))
                            {
                                recognized = true;
                                if (voiceHoldPressed) notifyVoiceHold = true;
                            }
                        }
                        else if (modelType is RemoteModelType.XiaomiRC003 or RemoteModelType.XiaomiRC001 or RemoteModelType.XiaomiARN9)
                        {
                            if (XiaomiProtocol.TryParseReport(slice, out currentButtons))
                            {
                                recognized = true;
                            }
                        }
                        else
                        {
                            if (G9N9NProtocol.TryParseReport(slice, out currentButtons))
                            {
                                recognized = true;
                            }
                            else if (T1RemoteProtocol.TryParseReport(slice, out currentButtons, out bool voiceHoldPressed))
                            {
                                recognized = true;
                                if (voiceHoldPressed) notifyVoiceHold = true;
                            }
                            else if (XiaomiProtocol.TryParseReport(slice, out currentButtons))
                            {
                                recognized = true;
                            }
                        }
                    }

                    byte reportId = GetReportId(slice);
                    if (!filterRejected)
                    {
                        string outcome = !recognized ? "unrecognized" : currentButtons.Count == 0 ? "no mapped buttons" : "decoded";
                        if (_loggedHidReports.Add((hDevice, outcome)))
                            AppLogger.Info($"RawInput HID report: hDevice=0x{hDevice:X}, model={modelType}, reportId=0x{reportId:X2}, len={reportSize}, outcome={outcome}, buttons={currentButtons.Count}", "RawInputReceiver");
                    }

                    if (!recognized)
                    {
                        continue;
                    }

                    if (currentButtons.Count == 0 && reportId == 0)
                    {
                        // All-zero generic release frame: release all non-keyboard HID buttons
                        foreach (var (rId, btns) in reportMap)
                        {
                            if (rId == KeyboardReportId) continue;
                            foreach (var btn in btns)
                            {
                                hidEvents.Add((btn, false, reportId));
                            }
                            btns.Clear();
                        }
                        continue;
                    }

                    if (currentButtons.Count > 1 && modelType is RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE or RemoteModelType.Unknown)
                    {
                        // G9N9N multi-key chords drop release frames; dispatch as momentary taps
                        foreach (var btn in currentButtons)
                        {
                            hidEvents.Add((btn, true, reportId));
                            hidEvents.Add((btn, false, reportId));
                        }
                        continue;
                    }

                    if (!reportMap.TryGetValue(reportId, out var pressedButtons))
                    {
                        pressedButtons = new HashSet<RemoteButton>();
                        reportMap[reportId] = pressedButtons;
                    }

                    var currentReportButtons = new HashSet<RemoteButton>(currentButtons);

                    // 1. Released buttons for this specific report ID -> KeyUp (false)
                    var released = new List<RemoteButton>();
                    foreach (var btn in pressedButtons)
                    {
                        if (!currentReportButtons.Contains(btn))
                        {
                            released.Add(btn);
                        }
                    }

                    foreach (var btn in released)
                    {
                        pressedButtons.Remove(btn);
                        hidEvents.Add((btn, false, reportId));
                    }

                    // 2. Newly pressed buttons -> KeyDown (true)
                    foreach (var btn in currentReportButtons)
                    {
                        if (pressedButtons.Add(btn))
                        {
                            hidEvents.Add((btn, true, reportId));
                        }
                    }
                }
                }
                finally
                {
                    if (filterRejected && reportMap.Values.All(btns => btns.Count == 0))
                    {
                        _deviceReportButtons.Remove(hDevice);
                    }
                }
            }

            if (notifyVoiceHold && !filterRejected)
            {
                PhysicalVoiceKeyPressed?.Invoke(hDevice);
            }

            foreach (var (btn, isDown, reportId) in hidEvents)
            {
                AppLogger.Info($"RawInput HID event: {btn} {(isDown ? "Down" : "Up")} (hDevice=0x{hDevice:X}, reportId=0x{reportId:X2}, len={reportSize})", "RawInputReceiver");
                ButtonEvent?.Invoke(hDevice, btn, isDown);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte GetReportId(ReadOnlySpan<byte> slice)
    {
        if (slice.Length % 2 != 0 && slice.Length > 1)
        {
            return slice[0];
        }
        if (slice.Length >= 3 && (slice[0] == 1 || slice[0] == 2 || slice[0] == 3))
        {
            return slice[0];
        }
        return 0;
    }

    public static RemoteButton MapVirtualKeyToRemoteButton(ushort vkey, ushort makeCode = 0)
    {
        // Captured Xiaomi RC003 Windows reports: power has no assigned VKey.
        if (vkey == 0xFF && makeCode == 0x5E) return RemoteButton.Power;
        return vkey switch
        {
            0xC0 => RemoteButton.LiveTv,       // Xiaomi TV key (HID keyboard usage 0x35)
            0x26 => RemoteButton.Up,           // VK_UP
            0x28 => RemoteButton.Down,         // VK_DOWN
            0x25 => RemoteButton.Left,         // VK_LEFT
            0x27 => RemoteButton.Right,        // VK_RIGHT
            0x0D => RemoteButton.Ok,           // VK_RETURN
            0x1B => RemoteButton.Back,         // VK_ESCAPE
            0xA6 => RemoteButton.Back,         // VK_BROWSER_BACK
            0x24 => RemoteButton.Home,         // VK_HOME
            0xAC => RemoteButton.Home,         // VK_BROWSER_HOME
            0x12 => RemoteButton.Menu,         // VK_MENU
            0x5D => RemoteButton.Menu,         // VK_APPS
            0xAF => RemoteButton.VolumeUp,     // VK_VOLUME_UP
            0xAE => RemoteButton.VolumeDown,   // VK_VOLUME_DOWN
            0xAD => RemoteButton.Mute,         // VK_VOLUME_MUTE
            0xB3 => RemoteButton.PlayPause,    // VK_MEDIA_PLAY_PAUSE
            _ => RemoteButton.None
        };
    }

    public void Dispose()
    {
        if (_isRegistered && _hwnd != IntPtr.Zero)
        {
            var devices = new RAWINPUTDEVICE[]
            {
                new()
                {
                    usUsagePage = 0x0C,
                    usUsage = 0x01,
                    dwFlags = RIDEV_REMOVE,
                    hwndTarget = IntPtr.Zero
                },
                new()
                {
                    usUsagePage = 0x01,
                    usUsage = 0x06,
                    dwFlags = RIDEV_REMOVE,
                    hwndTarget = IntPtr.Zero
                }
            };
            RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            _isRegistered = false;
        }

        lock (_lock)
        {
            foreach (var preparsed in _preparsedDataCache.Values)
            {
                if (preparsed != IntPtr.Zero) Marshal.FreeHGlobal(preparsed);
            }
            _preparsedDataCache.Clear();
            _deviceReportButtons.Clear();
            _devicePathCache.Clear();
            _devicesHoldingF5.Clear();
            _loggedHidReports.Clear();
        }
    }
}
