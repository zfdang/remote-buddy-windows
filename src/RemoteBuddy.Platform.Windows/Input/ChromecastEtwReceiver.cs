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
    public event Action? TruncatedPacketDetected;

    private const byte HciEvent = 2;
    private const byte AclReceived = 3;
    private const byte AclSent = 4;
    private const ushort G9N9NInputAttribute = 0x0029;
    private readonly object _lock = new();
    private byte[] _sourceRequest = [];
    private ushort? _probeConnection;
    private ushort? _boundConnection;
    private bool _sawPacketEvent;
    private bool _sawRedaction;

    internal void SetSourceProbe(Guid marker)
    {
        // ATT Find By Type Value: full range, Primary Service, 128-bit UUID in wire order.
        _sourceRequest = new byte[23];
        byte[] header = [0x06, 0x01, 0x00, 0xFF, 0xFF, 0x00, 0x28];
        header.CopyTo(_sourceRequest, 0);
        var uuid = _sourceRequest.AsSpan(header.Length);
        marker.TryWriteBytes(uuid, bigEndian: true, out _);
        uuid.Reverse();
        _probeConnection = null;
        _boundConnection = null;
        _sawPacketEvent = _sawRedaction = false;
    }

    private string? _sessionName;
    private ulong _sessionHandle;
    private ulong _traceHandle = 0xFFFFFFFFFFFFFFFF;
    private Thread? _processThread;
    private CancellationTokenSource? _flushCancellation;
    private EventRecordCallback? _callbackHolder;
    private IntPtr _logfileBuffer = IntPtr.Zero;
    private IntPtr _propertiesBuffer = IntPtr.Zero;
    private volatile bool _isRunning;
    private RemoteButton _lastHeldButton = RemoteButton.None;

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _isRunning;
        }
    }

    public bool Start(Guid marker)
    {
        lock (_lock)
        {
            if (_isRunning) return true;
            SetSourceProbe(marker);

            // Ensure BthPort registry parameters are configured
            if (!BthPortRegistryHelper.IsConfigured())
            {
                BthPortRegistryHelper.TryConfigure();
            }

            _sessionName = SessionNamePrefix + Process.GetCurrentProcess().Id;
            int propSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 1024;
            _propertiesBuffer = Marshal.AllocHGlobal(propSize);

            byte[] zero = new byte[propSize];
            Marshal.Copy(zero, 0, _propertiesBuffer, propSize);

            var prop = new EVENT_TRACE_PROPERTIES
            {
                Wnode = new WNODE_HEADER
                {
                    BufferSize = (uint)propSize,
                    Flags = 0x00020000, // WNODE_FLAG_TRACED_GUID
                    Guid = Guid.NewGuid(),
                    ClientContext = 2   // System time
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
                Marshal.StructureToPtr(prop, _propertiesBuffer, false);
                Marshal.Copy(nameChars, 0, nameDest, nameChars.Length);
                startResult = StartTraceW(out _sessionHandle, _sessionName, _propertiesBuffer);
            }

            if (startResult != 0)
            {
                AppLogger.Warn($"Failed to start ETW trace session for BthPort. Win32Error={startResult}", "ChromecastEtwReceiver");
                _sessionHandle = 0;
                CleanupUnmanaged();
                return false;
            }

            Guid providerGuid = BthPortProviderGuid;
            uint enableResult = EnableTraceEx2(
                _sessionHandle,
                ref providerGuid,
                1,                      // EVENT_CONTROL_CODE_ENABLE_PROVIDER
                5,                      // TRACE_LEVEL_VERBOSE
                ulong.MaxValue,         // Include BthPort packet events across driver keyword layouts
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
            _flushCancellation = new CancellationTokenSource();
            _ = FlushBuffersAsync(_sessionHandle, _sessionName, prop.Wnode.Guid, _flushCancellation.Token);

            AppLogger.Info($"Chromecast ETW receiver successfully started (Session={_sessionName})", "ChromecastEtwReceiver");
            return true;
        }
    }

    private static async Task FlushBuffersAsync(ulong sessionHandle, string sessionName, Guid sessionGuid, CancellationToken cancellationToken)
    {
        // ETW's automatic real-time flush uses whole seconds. Flush this session
        // more often so a single press doesn't wait for the next driver batch.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        int structureSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        int bufferSize = structureSize + (sessionName.Length + 1) * sizeof(char);
        var properties = new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER { BufferSize = (uint)bufferSize, Guid = sessionGuid },
            LoggerNameOffset = (uint)structureSize
        };
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Marshal.StructureToPtr(properties, buffer, false);
                uint result = ControlTraceW(sessionHandle, null, buffer, 3 /* EVENT_TRACE_CONTROL_FLUSH */);
                if (result == 0) continue;

                if (!cancellationToken.IsCancellationRequested)
                    AppLogger.Warn($"Could not flush the Chromecast ETW session (Win32Error={result})", "ChromecastEtwReceiver");
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ProcessTraceThread()
    {
        ulong handle = _traceHandle;
        if (handle == 0 || handle == 0xFFFFFFFFFFFFFFFF) return;

        ulong[] handles = [handle];
        var callback = _callbackHolder;
        uint result = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        GC.KeepAlive(callback);
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

            if (header.EventDescriptor.Id != 402) return;
            if (!_sawPacketEvent)
            {
                _sawPacketEvent = true;
                AppLogger.Info($"BthPort packet event received (version {header.EventDescriptor.Version})", "ChromecastEtwReceiver");
            }

            var kind = GetEventProperty(pEventRecord, "BIP_Type");
            if (kind is { Length: 1 })
            {
                if (!IsPacketEventKind(kind[0])) return;
                var packet = GetEventProperty(pEventRecord, "BIP_Data");
                var length = GetEventProperty(pEventRecord, "BIP_DataLen");
                if (packet != null && length is { Length: 4 } &&
                    BinaryPrimitives.ReadUInt32LittleEndian(length) == packet.Length)
                {
                    ProcessPacket(kind[0], packet);
                    return;
                }
            }

            // The raw fallback applies only to the version 0 layout.
            if (header.EventDescriptor.Version != 0) return;
            var record = Marshal.PtrToStructure<EVENT_RECORD>(pEventRecord);
            if (record.UserDataLength <= 8 || record.UserData == IntPtr.Zero) return;
            byte[] data = new byte[record.UserDataLength];
            Marshal.Copy(record.UserData, data, 0, data.Length);
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) == data.Length - 8)
                ProcessPacket(data[3], data.AsSpan(8));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Exception processing ETW event record: {ex.Message}", "ChromecastEtwReceiver");
        }
    }

    private static bool IsPacketEventKind(byte kind) => kind is HciEvent or AclReceived or AclSent;

    internal void ProcessPacket(byte kind, ReadOnlySpan<byte> packet)
    {
        if (kind == HciEvent)
        {
            // Disconnection Complete. A controller can now reuse the handle for another device.
            if (packet is [0x05, 0x04, 0x00, _, _, _])
            {
                ushort disconnected = BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]);
                if (_boundConnection == disconnected)
                {
                    _boundConnection = null;
                    DispatchButtonEvent(RemoteButton.None, false);
                }
                if (_probeConnection == disconnected) _probeConnection = null;
            }
            return;
        }

        if (kind is not (AclSent or AclReceived) || !TryGetAttPacket(packet, out ushort connection, out var att, out bool incomplete)) return;

        if (kind == AclSent)
        {
            if (!incomplete && att.SequenceEqual(_sourceRequest))
            {
                _probeConnection = connection;
                AppLogger.Info("BthPort observed the selected-device GATT query", "ChromecastEtwReceiver");
            }
            return;
        }

        // The unique request was sent via WinRT on the selected device. Confirm its ATT response
        // on the same ACL handle before accepting any input from that connection.
        if (_probeConnection == connection && !incomplete && (att is [0x01, 0x06, _, _, _] || att is [0x07, ..]))
        {
            _boundConnection = connection;
            _probeConnection = null;
            AppLogger.Info("Chromecast ETW confirmed the selected Bluetooth connection", "ChromecastEtwReceiver");
            return;
        }

        if (_boundConnection != connection) return;
        if (TryParseButtonReport(att, incomplete, out var button, out bool isDown, out bool truncated))
            DispatchButtonEvent(button, isDown);
        else if (truncated)
        {
            if (!_sawRedaction)
            {
                _sawRedaction = true;
                AppLogger.Warn("Windows omitted the key code from a verified Chromecast button report", "ChromecastEtwReceiver");
            }
            DispatchButtonEvent(RemoteButton.None, false);
            TruncatedPacketDetected?.Invoke();
        }
    }

    private static bool TryGetAttPacket(ReadOnlySpan<byte> packet, out ushort connection, out ReadOnlySpan<byte> att, out bool incomplete)
    {
        connection = 0;
        att = default;
        incomplete = false;
        if (packet.Length < 8) return false;

        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        connection = (ushort)(header & 0x0FFF);
        if (connection > 0x0EFF || (header & 0xD000) != 0) return false;

        int aclLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        int l2capLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
        if (aclLength != l2capLength + 4 || packet.Length > aclLength + 4 ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]) != 4) return false;

        att = packet[8..];
        incomplete = att.Length < l2capLength;
        // The observed redaction keeps the ATT header of the eight-byte G9N9N report.
        return incomplete ? l2capLength == 11 && att.Length == 3 : !att.IsEmpty;
    }

    public static bool TryParseAclPacket(ReadOnlySpan<byte> packet, out RemoteButton button, out bool isDown) =>
        TryParseAclPacket(packet, out button, out isDown, out _);

    public static bool TryParseAclPacket(ReadOnlySpan<byte> packet, out RemoteButton button, out bool isDown, out bool truncated)
    {
        button = RemoteButton.None;
        isDown = truncated = false;
        return TryGetAttPacket(packet, out _, out var att, out bool incomplete) &&
            TryParseButtonReport(att, incomplete, out button, out isDown, out truncated);
    }

    private static bool TryParseButtonReport(ReadOnlySpan<byte> att, bool incomplete, out RemoteButton button, out bool isDown, out bool truncated)
    {
        button = RemoteButton.None;
        isDown = truncated = false;
        if (att.Length < 3 || att[0] is not (0x1B or 0x1D)) return false;

        ushort attribute = BinaryPrimitives.ReadUInt16LittleEndian(att[1..]);
        var report = att[3..];
        if (incomplete)
        {
            truncated = attribute == G9N9NInputAttribute && report.IsEmpty;
            return false;
        }

        if (attribute == G9N9NInputAttribute && report is [_, 0, 0, 0, 0, 0, 0, 0])
        {
            byte code = report[0];
            if (code == 0) return true;
            if (code > G9N9NProtocol.IndexedConsumerUsages.Length) return false;
            isDown = G9N9NProtocol.ConsumerButtons.TryGetValue(G9N9NProtocol.IndexedConsumerUsages[code - 1], out button);
            return isDown;
        }

        if ((attribute is (G9N9NInputAttribute or 0x0025 or 0x0028 or 0x002D)) && report.Length == 2)
        {
            ushort usage = BinaryPrimitives.ReadUInt16LittleEndian(report);
            if (usage == 0) return true;
            if (G9N9NProtocol.ConsumerButtons.TryGetValue(usage, out button))
            {
                isDown = true;
                return true;
            }
        }

        if (report.Length <= 8 && XiaomiProtocol.TryParseReport(report, out var xiaomiButtons))
        {
            if (xiaomiButtons.Count > 0)
            {
                button = xiaomiButtons[0];
                isDown = true;
                return true;
            }
            isDown = false;
            return true;
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
                if (button == _lastHeldButton) return;
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
            _isRunning = false;
            _flushCancellation?.Cancel();
            _flushCancellation?.Dispose();
            _flushCancellation = null;

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
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public uint BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
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
    private static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

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
