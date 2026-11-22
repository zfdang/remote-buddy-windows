// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;

namespace RemoteBuddy.Platform.Windows.Input;

/// <summary>
/// Captures Chromecast with Google TV Voice Remote (G9N9N) button reports in real time
/// directly from the Windows Bluetooth driver (BthPort) via an ETW trace session.
/// </summary>
public sealed class ChromecastEtwReceiver : IDisposable
{
    private static readonly Guid BthPortProviderGuid = new("8a1f9517-3a8c-4a9e-a018-4f17a200f277");
    private const string SessionNamePrefix = "RemoteBuddy_BthEtw_";

    public event Action<RemoteButton, bool>? ButtonEvent;

    private readonly object _lock = new();
    private string? _sessionName;
    private ulong _sessionHandle;
    private ulong _traceHandle = 0xFFFFFFFFFFFFFFFF;
    private Thread? _processThread;
    private EventRecordCallback? _callbackHolder;
    private IntPtr _logfileBuffer = IntPtr.Zero;
    private IntPtr _propertiesBuffer = IntPtr.Zero;
    private bool _isRunning;
    private RemoteButton _lastHeldButton = RemoteButton.None;

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _isRunning;
        }
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            // Ensure BthPort registry parameters are configured
            if (!BthPortRegistryHelper.IsConfigured())
            {
                BthPortRegistryHelper.TryConfigure();
            }

            _sessionName = SessionNamePrefix + Process.GetCurrentProcess().Id;
            int propSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 1024;
            _propertiesBuffer = Marshal.AllocHGlobal(propSize);

            unsafe
            {
                byte* ptr = (byte*)_propertiesBuffer.ToPointer();
                for (int i = 0; i < propSize; i++) ptr[i] = 0;
            }

            var prop = new EVENT_TRACE_PROPERTIES
            {
                Wnode = new WNODE_HEADER
                {
                    BufferSize = (uint)propSize,
                    Flags = 0x00020000, // WNODE_FLAG_TRACED_GUID
                    ClientContext = 2   // PerfCounter
                },
                BufferSize = 64,
                MinimumBuffers = 4,
                MaximumBuffers = 16,
                LogFileMode = 0x00000100, // EVENT_TRACE_REAL_TIME_MODE
                FlushTimer = 1,
                LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()
            };

            Marshal.StructureToPtr(prop, _propertiesBuffer, false);
            IntPtr nameDest = IntPtr.Add(_propertiesBuffer, (int)prop.LoggerNameOffset);
            char[] nameChars = (_sessionName + "\0").ToCharArray();
            Marshal.Copy(nameChars, 0, nameDest, nameChars.Length);

            uint startResult = StartTraceW(out _sessionHandle, _sessionName, _propertiesBuffer);
            if (startResult == 183 /* ERROR_ALREADY_EXISTS */)
            {
                ControlTraceW(0, _sessionName, _propertiesBuffer, 1 /* EVENT_TRACE_CONTROL_STOP */);
                startResult = StartTraceW(out _sessionHandle, _sessionName, _propertiesBuffer);
            }

            if (startResult != 0)
            {
                AppLogger.Warn($"Failed to start ETW trace session for BthPort. Win32Error={startResult}", "ChromecastEtwReceiver");
                CleanupUnmanaged();
                return false;
            }

            Guid providerGuid = BthPortProviderGuid;
            uint enableResult = EnableTraceEx2(
                _sessionHandle,
                ref providerGuid,
                1,                      // EVENT_CONTROL_CODE_ENABLE_PROVIDER
                5,                      // TRACE_LEVEL_VERBOSE
                0x8000000000000000UL,   // matchAnyKeyword (BthPort Data Events)
                0, 0, IntPtr.Zero);

            if (enableResult != 0)
            {
                AppLogger.Warn($"EnableTraceEx2 failed for BthPort provider. Win32Error={enableResult}", "ChromecastEtwReceiver");
                Stop();
                return false;
            }

            _callbackHolder = ProcessEventRecord;
            IntPtr pCallback = Marshal.GetFunctionPointerForDelegate(_callbackHolder);

            var logfile = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = Marshal.StringToHGlobalUni(_sessionName),
                LogFileMode = 0x10000000 | 0x00000100, // PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_REAL_TIME
                CurrentEvent = new byte[368],
                EventRecordCallback = pCallback
            };

            _logfileBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<EVENT_TRACE_LOGFILEW>());
            Marshal.StructureToPtr(logfile, _logfileBuffer, false);

            _traceHandle = OpenTraceW(_logfileBuffer);
            if (_traceHandle == 0xFFFFFFFFFFFFFFFF)
            {
                int err = Marshal.GetLastWin32Error();
                AppLogger.Warn($"OpenTraceW failed. Win32Error={err}", "ChromecastEtwReceiver");
                Stop();
                return false;
            }

            _isRunning = true;
            _processThread = new Thread(ProcessTraceThread)
            {
                Name = "Chromecast-ETW-Worker",
                IsBackground = true
            };
            _processThread.Start();

            AppLogger.Info($"Chromecast ETW receiver successfully started (Session={_sessionName})", "ChromecastEtwReceiver");
            return true;
        }
    }

    private void ProcessTraceThread()
    {
        ulong handle = _traceHandle;
        if (handle == 0 || handle == 0xFFFFFFFFFFFFFFFF) return;

        ulong[] handles = [handle];
        uint result = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        if (result != 0 && result != 1223 /* ERROR_CANCELLED */)
        {
            AppLogger.Debug($"ProcessTrace exited with code {result}", "ChromecastEtwReceiver");
        }
    }

    private void ProcessEventRecord(IntPtr pEventRecord)
    {
        if (pEventRecord == IntPtr.Zero || !_isRunning) return;

        try
        {
            var header = Marshal.PtrToStructure<EVENT_HEADER>(pEventRecord);
            if (header.ProviderId != BthPortProviderGuid) return;
            if (header.EventDescriptor.Id != 402 /* BthPort_Event_BipAclData */) return;

            byte[]? typeBytes = GetEventProperty(pEventRecord, "BIP_Type");
            if (typeBytes == null || typeBytes.Length < 1 || typeBytes[0] != 3 /* ACL RX */) return;

            byte[]? dataBytes = GetEventProperty(pEventRecord, "BIP_Data");
            if (dataBytes == null || dataBytes.Length < 12) return;

            if (TryParseAclPacket(dataBytes, out RemoteButton button, out bool isDown))
            {
                DispatchButtonEvent(button, isDown);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Exception processing ETW event record: {ex.Message}", "ChromecastEtwReceiver");
        }
    }

    public static bool TryParseAclPacket(ReadOnlySpan<byte> aclPacket, out RemoteButton button, out bool isDown)
    {
        button = RemoteButton.None;
        isDown = false;

        if (aclPacket.Length < 12) return false;

        ushort l2capLen = BinaryPrimitives.ReadUInt16LittleEndian(aclPacket.Slice(4, 2));
        ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(aclPacket.Slice(6, 2));

        if (cid != 4) return false; // ATT CID is 4
        if (aclPacket.Length < 8 + l2capLen) return false;

        byte attOpcode = aclPacket[8];
        if (attOpcode != 0x1B && attOpcode != 0x1D) return false; // Handle Value Notification or Indication

        ushort attrHandle = BinaryPrimitives.ReadUInt16LittleEndian(aclPacket.Slice(9, 2));
        // G9N9N Chromecast Remote HID input report handle is 0x0029
        if (attrHandle != 0x0029) return false;

        ReadOnlySpan<byte> report = aclPacket.Slice(11, l2capLen - 3);
        if (report.IsEmpty) return false;

        if (report.Length >= 8)
        {
            byte code = report[0];
            if (code == 0)
            {
                button = RemoteButton.None;
                isDown = false;
                return true;
            }

            button = code switch
            {
                0x01 => RemoteButton.Power,
                0x03 => RemoteButton.Up,
                0x04 => RemoteButton.Down,
                0x05 => RemoteButton.Left,
                0x06 => RemoteButton.Right,
                0x07 => RemoteButton.Ok,
                0x08 => RemoteButton.Mute,
                0x0A => RemoteButton.Home,
                0x0B => RemoteButton.Back,
                0x0C => RemoteButton.VolumeUp,
                0x0D => RemoteButton.VolumeDown,
                0x0E => RemoteButton.YouTube,
                0x0F => RemoteButton.Netflix,
                0x11 => RemoteButton.Input,
                _ => RemoteButton.None
            };

            if (button != RemoteButton.None)
            {
                isDown = true;
                return true;
            }
        }
        else if (report.Length >= 2)
        {
            ushort usage = BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(0, 2));
            if (usage == 0)
            {
                button = RemoteButton.None;
                isDown = false;
                return true;
            }

            if (G9N9NProtocol.ConsumerButtons.TryGetValue(usage, out var consumerBtn))
            {
                button = consumerBtn;
                isDown = true;
                return true;
            }
        }

        return false;
    }

    private void DispatchButtonEvent(RemoteButton button, bool isDown)
    {
        RemoteButton buttonToRelease = RemoteButton.None;
        RemoteButton buttonToPress = RemoteButton.None;

        lock (_lock)
        {
            if (isDown)
            {
                if (_lastHeldButton != RemoteButton.None && _lastHeldButton != button)
                {
                    buttonToRelease = _lastHeldButton;
                }
                _lastHeldButton = button;
                buttonToPress = button;
            }
            else
            {
                if (_lastHeldButton != RemoteButton.None)
                {
                    buttonToRelease = _lastHeldButton;
                    _lastHeldButton = RemoteButton.None;
                }
            }
        }

        if (buttonToRelease != RemoteButton.None)
        {
            AppLogger.Info($"Chromecast ETW button: {buttonToRelease} Up", "ChromecastEtwReceiver");
            ButtonEvent?.Invoke(buttonToRelease, false);
        }

        if (buttonToPress != RemoteButton.None)
        {
            AppLogger.Info($"Chromecast ETW button: {buttonToPress} Down", "ChromecastEtwReceiver");
            ButtonEvent?.Invoke(buttonToPress, true);
        }
    }

    private static byte[]? GetEventProperty(IntPtr pEventRecord, string propertyName)
    {
        IntPtr pName = Marshal.StringToHGlobalUni(propertyName);
        try
        {
            var desc = new PROPERTY_DATA_DESCRIPTOR
            {
                PropertyName = (ulong)pName.ToInt64(),
                ArrayIndex = 0xFFFFFFFF,
                Reserved = 0
            };

            if (TdhGetPropertySize(pEventRecord, 0, IntPtr.Zero, 1, ref desc, out uint size) == 0 && size > 0 && size <= 65536)
            {
                byte[] buffer = new byte[size];
                IntPtr pBuffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (TdhGetProperty(pEventRecord, 0, IntPtr.Zero, 1, ref desc, size, pBuffer) == 0)
                    {
                        Marshal.Copy(pBuffer, buffer, 0, (int)size);
                        return buffer;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pBuffer);
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pName);
        }
        return null;
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                CleanupUnmanaged();
                return;
            }

            _isRunning = false;

            if (_traceHandle != 0 && _traceHandle != 0xFFFFFFFFFFFFFFFF)
            {
                CloseTrace(_traceHandle);
                _traceHandle = 0xFFFFFFFFFFFFFFFF;
            }

            if (_sessionHandle != 0)
            {
                if (_propertiesBuffer != IntPtr.Zero && !string.IsNullOrEmpty(_sessionName))
                {
                    ControlTraceW(_sessionHandle, _sessionName, _propertiesBuffer, 1 /* STOP */);
                }
                _sessionHandle = 0;
            }

            if (_lastHeldButton != RemoteButton.None)
            {
                var held = _lastHeldButton;
                _lastHeldButton = RemoteButton.None;
                ButtonEvent?.Invoke(held, false);
            }

            CleanupUnmanaged();
        }

        if (_processThread != null && _processThread.IsAlive)
        {
            _processThread.Join(500);
            _processThread = null;
        }

        AppLogger.Info("Chromecast ETW receiver stopped", "ChromecastEtwReceiver");
    }

    private void CleanupUnmanaged()
    {
        if (_logfileBuffer != IntPtr.Zero)
        {
            var logfile = Marshal.PtrToStructure<EVENT_TRACE_LOGFILEW>(_logfileBuffer);
            if (logfile.LoggerName != IntPtr.Zero) Marshal.FreeHGlobal(logfile.LoggerName);
            Marshal.FreeHGlobal(_logfileBuffer);
            _logfileBuffer = IntPtr.Zero;
        }

        if (_propertiesBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_propertiesBuffer);
            _propertiesBuffer = IntPtr.Zero;
        }

        _callbackHolder = null;
    }

    public void Dispose()
    {
        Stop();
    }

    #region Win32 ETW P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public uint KernelTime;
        public uint UserTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EVENT_TRACE_LOGFILEW
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint LogFileMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 368)]
        public byte[] CurrentEvent;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTY_DATA_DESCRIPTOR
    {
        public ulong PropertyName;
        public uint ArrayIndex;
        public uint Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr pEventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartTraceW(out ulong pSessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint EnableTraceEx2(
        ulong traceHandle,
        ref Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr pLogFile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint CloseTrace(ulong traceHandle);

    [DllImport("tdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint TdhGetPropertySize(
        IntPtr pEvent,
        uint TdhContextCount,
        IntPtr pTdhContext,
        uint PropertyDataCount,
        ref PROPERTY_DATA_DESCRIPTOR pPropertyDataDescriptor,
        out uint pPropertySize);

    [DllImport("tdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint TdhGetProperty(
        IntPtr pEvent,
        uint TdhContextCount,
        IntPtr pTdhContext,
        uint PropertyDataCount,
        ref PROPERTY_DATA_DESCRIPTOR pPropertyDataDescriptor,
        uint PropertySize,
        IntPtr pBuffer);

    #endregion
}
