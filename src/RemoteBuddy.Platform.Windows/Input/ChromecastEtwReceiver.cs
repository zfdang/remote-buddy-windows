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
    public event Action<ushort>? ConnectionHandleBound;

    private readonly object _lock = new();
    private readonly HashSet<ushort> _knownGattHandles = new();
    private ushort? _boundConnectionHandle;
    private readonly object _bindingLock = new();

    public ushort? BoundConnectionHandle
    {
        get { lock (_bindingLock) return _boundConnectionHandle; }
        set { lock (_bindingLock) _boundConnectionHandle = value; }
    }

    public void SetKnownGattHandles(IEnumerable<ushort> handles)
    {
        lock (_bindingLock)
        {
            _knownGattHandles.Clear();
            foreach (var h in handles)
            {
                if (h != 0) _knownGattHandles.Add(h);
            }
        }
    }

    public void ResetConnectionBinding()
    {
        lock (_bindingLock)
        {
            _boundConnectionHandle = null;
        }
    }

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

            byte[] zero = new byte[propSize];
            Marshal.Copy(zero, 0, _propertiesBuffer, propSize);

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
                ulong.MaxValue,         // matchAnyKeyword: match all keywords (HCI, HCIRAW, L2CAP, etc.)
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

            // 1. Check Event ID: Only Event 402 is BthPort_Event_BipAclData
            if (header.EventDescriptor.Id != 402) return;

            // 2. Try TDH property extraction first
            byte[]? aclData = null;
            byte[]? typeBytes = GetEventProperty(pEventRecord, "BIP_Type");
            if (typeBytes != null && typeBytes.Length >= 1)
            {
                byte bipType = typeBytes[0];
                // 3 = ACL RX (from remote device), 2 = ACL TX (to remote device, used for handle binding)
                if (bipType != 3 && bipType != 2) return;

                aclData = GetEventProperty(pEventRecord, "BIP_Data");
                if (aclData != null && aclData.Length >= 7)
                {
                    ProcessAclData(aclData, bipType);
                    return;
                }
            }

            // 3. Fall back to raw UserData inspection (handles missing TDH manifests or custom layouts)
            var record = Marshal.PtrToStructure<EVENT_RECORD>(pEventRecord);
            if (record.UserDataLength >= 8 && record.UserData != IntPtr.Zero)
            {
                byte[] raw = new byte[record.UserDataLength];
                Marshal.Copy(record.UserData, raw, 0, record.UserDataLength);

                // Event 402 UserData layout:
                // Offset 0: CommandPacketsAllowed (uint8)
                // Offset 1..2: AclWriteCredits (uint16)
                // Offset 3: BIP_Type (uint8) -> must be 3 (ACL RX) or 2 (ACL TX)
                // Offset 4..7: BIP_DataLen (uint32)
                // Offset 8..: BIP_Data
                byte bipType = raw[3];
                if (bipType != 3 && bipType != 2) return;

                uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
                int actualLen = (int)Math.Min((long)dataLen, raw.Length - 8);
                if (actualLen >= 7)
                {
                    byte[] rawAcl = new byte[actualLen];
                    Array.Copy(raw, 8, rawAcl, 0, actualLen);
                    ProcessAclData(rawAcl, bipType);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Exception processing ETW event record: {ex.Message}", "ChromecastEtwReceiver");
        }
    }

    private void ProcessAclData(ReadOnlySpan<byte> aclData, byte bipType)
    {
        if (aclData.Length < 7) return;

        // Extract connection handle from ACL header (bytes 0..1, lowest 12 bits)
        ushort handleAndFlags = BinaryPrimitives.ReadUInt16LittleEndian(aclData.Slice(0, 2));
        ushort connectionHandle = (ushort)(handleAndFlags & 0x0FFF);

        // Check if this packet is an ATT transaction on one of our known ATVV characteristic handles
        ReadOnlySpan<byte> l2cap;
        if (aclData.Length >= 8)
        {
            ushort declaredLen = BinaryPrimitives.ReadUInt16LittleEndian(aclData.Slice(2, 2));
            if (declaredLen > 0 && declaredLen <= aclData.Length - 4)
            {
                l2cap = aclData.Slice(4);
            }
            else
            {
                l2cap = aclData;
            }
        }
        else
        {
            l2cap = aclData;
        }

        if (l2cap.Length >= 7)
        {
            ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(l2cap.Slice(2, 2));
            if (cid == 4) // ATT
            {
                ushort attrHandle = BinaryPrimitives.ReadUInt16LittleEndian(l2cap.Slice(5, 2));
                lock (_bindingLock)
                {
                    if (_knownGattHandles.Contains(attrHandle))
                    {
                        if (_boundConnectionHandle != connectionHandle)
                        {
                            _boundConnectionHandle = connectionHandle;
                            AppLogger.Info($"Chromecast ETW: Bound ACL connection handle 0x{connectionHandle:X4} to target device via ATVV handle 0x{attrHandle:X4}", "ChromecastEtwReceiver");
                            ConnectionHandleBound?.Invoke(connectionHandle);
                        }
                        return; // Known ATVV protocol traffic; not a button report
                    }
                }
            }
        }

        // Button reports must only come from ACL RX (incoming from remote device)
        if (bipType != 3) return;

        // Verify that this packet comes from our bound remote device, not another Bluetooth peripheral
        lock (_bindingLock)
        {
            if (_boundConnectionHandle.HasValue && connectionHandle != _boundConnectionHandle.Value)
            {
                return; // From a different Bluetooth device; ignore
            }
        }

        // Parse button event from ACL payload
        if (TryParseAclPacket(aclData, out RemoteButton button, out bool isDown, out bool isTruncated))
        {
            AppLogger.Info($"[ETW BthPort] Detected button: {button}, isDown={isDown} (ConnectionHandle=0x{connectionHandle:X4})", "ChromecastEtwReceiver");
            DispatchButtonEvent(button, isDown);
        }
        else if (isTruncated)
        {
            AppLogger.Warn($"[ETW BthPort] Detected truncated packet without payload from ConnectionHandle=0x{connectionHandle:X4}. Discarding event.", "ChromecastEtwReceiver");
            TruncatedPacketDetected?.Invoke();
        }
    }

    public static bool TryParseAclPacket(ReadOnlySpan<byte> aclPacket, out RemoteButton button, out bool isDown)
    {
        return TryParseAclPacket(aclPacket, out button, out isDown, out _);
    }

    public static bool TryParseAclPacket(ReadOnlySpan<byte> aclPacket, out RemoteButton button, out bool isDown, out bool isTruncated)
    {
        button = RemoteButton.None;
        isDown = false;
        isTruncated = false;

        if (aclPacket.Length < 5) return false;

        // 1. Direct structured parse (standard ACL / L2CAP packet alignment)
        if (TryDirectParse(aclPacket, out button, out isDown, out isTruncated))
        {
            return true;
        }

        if (isTruncated) return false;

        // 2. Fallback pattern scan (scans entire buffer for ATT Handle Value Notification: 0x1B or 0x1D)
        return ScanAttNotification(aclPacket, out button, out isDown, out isTruncated);
    }

    private static bool TryDirectParse(ReadOnlySpan<byte> aclPacket, out RemoteButton button, out bool isDown, out bool isTruncated)
    {
        button = RemoteButton.None;
        isDown = false;
        isTruncated = false;

        if (aclPacket.Length < 7) return false;

        ReadOnlySpan<byte> l2capPacket;
        if (aclPacket.Length >= 8)
        {
            ushort declaredLen = BinaryPrimitives.ReadUInt16LittleEndian(aclPacket.Slice(2, 2));
            if (declaredLen > 0 && declaredLen <= aclPacket.Length - 4)
            {
                l2capPacket = aclPacket.Slice(4);
            }
            else
            {
                l2capPacket = aclPacket;
            }
        }
        else
        {
            l2capPacket = aclPacket;
        }

        if (l2capPacket.Length < 4) return false;

        ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(l2capPacket.Slice(2, 2));
        if (cid != 4) return false; // ATT CID is 4
        if (l2capPacket.Length < 5) return false;

        byte attOpcode = l2capPacket[4];
        if (attOpcode != 0x1B && attOpcode != 0x1D) return false;

        if (l2capPacket.Length < 7) return false;
        ushort attrHandle = BinaryPrimitives.ReadUInt16LittleEndian(l2capPacket.Slice(5, 2));
        if (attrHandle != 0x0029 && attrHandle != 0x002D && attrHandle != 0x0025 && attrHandle != 0x0028) return false;

        if (l2capPacket.Length > 7)
        {
            ReadOnlySpan<byte> report = l2capPacket.Slice(7);
            if (report.IsEmpty)
            {
                isTruncated = true;
                return false;
            }

            if (attrHandle == 0x0029)
            {
                if (report.Length >= 8)
                {
                    byte code = report[0];
                    if (code == 0)
                    {
                        button = RemoteButton.None;
                        isDown = false;
                        return true;
                    }

                    button = MapG9N9NButtonCode(code);
                    if (button != RemoteButton.None)
                    {
                        isDown = true;
                        return true;
                    }
                }

                if (G9N9NProtocol.TryParseReport(report, out var parsedButtons))
                {
                    if (parsedButtons.Count > 0)
                    {
                        button = parsedButtons[0];
                        isDown = true;
                        return true;
                    }
                    else
                    {
                        button = RemoteButton.None;
                        isDown = false;
                        return true;
                    }
                }

                if (report.Length >= 1)
                {
                    byte code = report[0];
                    if (code == 0)
                    {
                        button = RemoteButton.None;
                        isDown = false;
                        return true;
                    }

                    button = MapG9N9NButtonCode(code);
                    if (button != RemoteButton.None)
                    {
                        isDown = true;
                        return true;
                    }
                }
            }
            else // 0x002D, 0x0025, 0x0028
            {
                if (report.Length >= 2)
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
            }
        }
        else
        {
            // Truncated by BthPort due to sensitive logging pending adapter restart
            isTruncated = true;
            return false;
        }

        return false;
    }

    private static bool ScanAttNotification(ReadOnlySpan<byte> span, out RemoteButton button, out bool isDown, out bool isTruncated)
    {
        button = RemoteButton.None;
        isDown = false;
        isTruncated = false;

        for (int i = 2; i <= span.Length - 3; i++)
        {
            byte opcode = span[i];
            if (opcode != 0x1B && opcode != 0x1D) continue;

            // Verify L2CAP CID is ATT (Channel 4)
            ushort cid = (ushort)(span[i - 2] | (span[i - 1] << 8));
            if (cid != 4) continue;

            ushort attrHandle = (ushort)(span[i + 1] | (span[i + 2] << 8));
            if (attrHandle == 0x0029)
            {
                ReadOnlySpan<byte> payload = span.Slice(i + 3);
                if (payload.IsEmpty)
                {
                    isTruncated = true;
                    return false;
                }

                if (payload.Length >= 8)
                {
                    byte code = payload[0];
                    if (code == 0)
                    {
                        button = RemoteButton.None;
                        isDown = false;
                        return true;
                    }

                    var mapped = MapG9N9NButtonCode(code);
                    if (mapped != RemoteButton.None)
                    {
                        button = mapped;
                        isDown = true;
                        return true;
                    }
                }

                if (G9N9NProtocol.TryParseReport(payload, out var parsedButtons))
                {
                    if (parsedButtons.Count > 0)
                    {
                        button = parsedButtons[0];
                        isDown = true;
                        return true;
                    }
                    else
                    {
                        button = RemoteButton.None;
                        isDown = false;
                        return true;
                    }
                }

                if (payload.Length >= 1)
                {
                    byte code = payload[0];
                    if (code == 0)
                    {
                        button = RemoteButton.None;
                        isDown = false;
                        return true;
                    }

                    var mapped = MapG9N9NButtonCode(code);
                    if (mapped != RemoteButton.None)
                    {
                        button = mapped;
                        isDown = true;
                        return true;
                    }
                }
            }
            else if (attrHandle == 0x002D || attrHandle == 0x0025 || attrHandle == 0x0028)
            {
                ReadOnlySpan<byte> payload = span.Slice(i + 3);
                if (payload.IsEmpty)
                {
                    isTruncated = true;
                    return false;
                }

                if (payload.Length >= 2)
                {
                    ushort usage = (ushort)(payload[0] | (payload[1] << 8));
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
            }
        }

        return false;
    }

    private static RemoteButton MapG9N9NButtonCode(byte code) => code switch
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
