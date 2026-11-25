// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;
using RemoteBuddy.Core.Protocols;

namespace RemoteBuddy.Platform.Windows.Input;

public enum EtwProtocolKind
{
    GoogleG9N9N = 0,
    Xiaomi = 1,
    T1Remote = 2
}

public readonly record struct EtwButtonSource(
    ulong Address,
    Guid Marker,
    EtwProtocolKind Protocol,
    ushort HidHandleStart = 0,
    ushort HidHandleEnd = 0,
    ushort[]? ExcludedAttributes = null);

/// <summary>
/// Captures Chromecast with Google TV Voice Remote (G9N9N) button reports in real time
/// directly from the Windows Bluetooth driver (BthPort) via an ETW trace session.
/// </summary>
public sealed class ChromecastEtwReceiver : IDisposable
{
    private static readonly Guid BthPortProviderGuid = new("8a1f9517-3a8c-4a9e-a018-4f17a200f277");
    private const string SessionNamePrefix = "RemoteBuddy_BthEtw_";

    public event Action<ulong, RemoteButton, bool>? ButtonEvent;
    public event Action<ulong>? TruncatedPacketDetected;
    public event Action<ulong>? DeviceDisconnected;
    public event Action<ulong>? SourceBound;
    public event Action<string>? UnexpectedStop;

    private const byte HciEvent = 2;
    private const byte AclReceived = 3;
    private const byte AclSent = 4;
    private const ushort G9N9NInputAttribute = 0x0029;
    private const ushort G9N9NShortReportAttribute = 0x0046;
    private const ushort XiaomiKeyboardAttribute = 0x0064;
    private const int MaxDiagnosticEntriesPerConnection = 32;
    private const int MaxDiagnosticHexBytes = 16;
    public static readonly ushort[] G9N9NVerifiedAttributes = [G9N9NInputAttribute];
    public static readonly ushort[] XiaomiVerifiedAttributes = [0x002B, 0x0056, 0x0058, 0x0059, 0x005B, 0x005E];
    private static readonly HashSet<ushort> _g9n9nAttributesSet = [G9N9NInputAttribute];
    private static readonly HashSet<ushort> _xiaomiAttributesSet = [0x002B, 0x0056, 0x0058, 0x0059, 0x005B, 0x005E];

    internal enum G9N9NReportFormat
    {
        Unknown = 0,
        Indexed8Byte,
        Indexed1Byte,
        Indexed2Byte,
        ConsumerUsage2Byte,
        ConsumerUsage3Byte,
        ReportId3Byte,
        Release2Byte
    }

    private enum G9N9NAttributeDecision
    {
        Allowed = 0,
        Excluded,
        IgnoredAfterLock,
        OutsideHidRange,
        UnlistedAttribute
    }

    private enum AttPacketRejection
    {
        None,
        ShortHeader,
        UnsupportedAclHeader,
        InconsistentLengths,
        NonAttChannel,
        UnsupportedTruncation,
        EmptyAtt
    }

    private sealed class SourceState(EtwButtonSource source, byte[] request)
    {
        public EtwButtonSource Source { get; } = source;
        public byte[] Request { get; } = request;
        public ushort HidHandleStart => Source.HidHandleStart;
        public ushort HidHandleEnd => Source.HidHandleEnd;
        public bool HasHidRange => Source.HidHandleStart > 0 && Source.HidHandleEnd >= Source.HidHandleStart;
        public HashSet<ushort> ExcludedAttributes { get; } = source.ExcludedAttributes is { Length: > 0 } excluded
            ? new HashSet<ushort>(excluded)
            : [];
        public ushort? PendingHandle { get; set; }
        public bool LoggedUnmatchedMarker { get; set; }
        public ushort? BoundHandle { get; set; }
        public ushort? LockedG9N9NHandle { get; set; }
        public RemoteButton Held { get; set; }
        public bool SawRedaction { get; set; }
        public HashSet<(ushort Attribute, G9N9NReportFormat Format)> LoggedFormats { get; } = [];
        public HashSet<string> LoggedDiagnostics { get; } = [];
        public bool DiagnosticCapLogged { get; set; }

        public void ResetConnectionState()
        {
            BoundHandle = null;
            LockedG9N9NHandle = null;
            Held = RemoteButton.None;
            SawRedaction = false;
            LoggedFormats.Clear();
            LoggedDiagnostics.Clear();
            DiagnosticCapLogged = false;
        }
    }

    private readonly object _lock = new();
    private readonly object _stateLock = new();
    private SourceState[] _sources = [];
    private volatile bool _sawPacketEvent;
    private readonly HashSet<string> _loggedProbeStages = [];

    internal void SetSources(IEnumerable<EtwButtonSource> sources)
    {
        var next = sources.Select(source =>
        {
            byte[] request = [0x06, 0x01, 0x00, 0xFF, 0xFF, 0x00, 0x28,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
            var uuid = request.AsSpan(7);
            source.Marker.TryWriteBytes(uuid, bigEndian: true, out _);
            uuid.Reverse();
            return new SourceState(source, request);
        }).ToArray();
        lock (_stateLock)
        {
            _sources = next;
            _sawPacketEvent = false;
            _loggedProbeStages.Clear();
        }
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

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _isRunning;
        }
    }

    public bool Start(IReadOnlyList<EtwButtonSource> sources)
    {
        lock (_lock)
        {
            if (_isRunning) return true;
            SetSources(sources);

            // Ensure BthPort registry parameters are configured
            bool registryConfigured = BthPortRegistryHelper.TryConfigure();
            AppLogger.Info($"BthPort ETW registry check: configured={registryConfigured}, writtenInThisProcess={BthPortRegistryHelper.JustConfigured}, systemUptimeSeconds={Environment.TickCount64 / 1000}", "ChromecastEtwReceiver");

            // A trace can outlive its process. Reuse the name so the next instance can reclaim it.
            using var process = Process.GetCurrentProcess();
            _sessionName = SessionNamePrefix + "S" + process.SessionId;
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
                4,                      // TRACE_LEVEL_INFORMATION
                0x8000000000000000UL,   // HCIRAW, the keyword declared for Event 402
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

            _ = CheckSourceBindingAsync(_flushCancellation.Token);

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

    private async Task CheckSourceBindingAsync(CancellationToken cancellationToken)
    {
        foreach (int seconds in new[] { 4, 8 })
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested) return;
            string[] unboundSources;
            lock (_stateLock)
            {
                unboundSources = _sources.Where(s => !s.BoundHandle.HasValue).Select(s =>
                    $"0x{s.Source.Address:X12}/{s.Source.Protocol}=" +
                    (s.PendingHandle is ushort handle ? $"awaiting ATT response on ACL 0x{handle:X4}" : "no matching ATT request"))
                    .ToArray();
            }
            if (cancellationToken.IsCancellationRequested) return;
            if (unboundSources.Length == 0) continue;
            AppLogger.Warn($"ETW GATT probe unbound at the {(seconds == 4 ? 4 : 12)}s check: event402Seen={_sawPacketEvent}; {string.Join(", ", unboundSources)}",
                "ChromecastEtwReceiver");
        }
    }

    private bool ShouldLogProbeStage(string stage)
    {
        lock (_stateLock)
        {
            return !_loggedProbeStages.Contains(stage) && _sources.Any(source => !source.BoundHandle.HasValue) &&
                _loggedProbeStages.Add(stage);
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
            AppLogger.Warn($"ProcessTrace exited with code {result}", "ChromecastEtwReceiver");
            if (_isRunning)
            {
                _isRunning = false;
                UnexpectedStop?.Invoke($"ProcessTrace exited with Win32Error={result}");
            }
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
                if (!IsPacketEventKind(kind[0]))
                {
                    if (ShouldLogProbeStage("EventKind"))
                        AppLogger.Info($"ETW probe saw unsupported Event 402 packet kind: version={header.EventDescriptor.Version}, kind=0x{kind[0]:X2}", "ChromecastEtwReceiver");
                    return;
                }
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
            var record = Marshal.PtrToStructure<EVENT_RECORD>(pEventRecord);
            if (ShouldLogProbeStage("EventProperties"))
                AppLogger.Info($"ETW probe could not decode Event 402 properties: version={header.EventDescriptor.Version}, typeSize={kind?.Length ?? 0}, eventLen={record.UserDataLength}", "ChromecastEtwReceiver");
            if (header.EventDescriptor.Version != 0) return;
            if (record.UserDataLength <= 8 || record.UserData == IntPtr.Zero) return;
            byte[] data = new byte[record.UserDataLength];
            Marshal.Copy(record.UserData, data, 0, data.Length);
            uint packetLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
            if (packetLength == data.Length - 8)
            {
                if (ShouldLogProbeStage("RawFallback"))
                    AppLogger.Info($"ETW probe used Event 402 raw layout: kind=0x{data[3]:X2}, len={packetLength}", "ChromecastEtwReceiver");
                ProcessPacket(data[3], data.AsSpan(8));
            }
            else if (ShouldLogProbeStage("InvalidRawLength"))
                AppLogger.Info($"ETW probe rejected Event 402 raw layout: declaredLen={packetLength}, availableLen={data.Length - 8}", "ChromecastEtwReceiver");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Exception processing ETW event record: {ex.Message}", "ChromecastEtwReceiver");
        }
    }

    private static bool IsPacketEventKind(byte kind) => kind is HciEvent or AclReceived or AclSent;

    internal void ProcessPacket(byte kind, ReadOnlySpan<byte> packet)
    {
        List<ulong>? disconnectedAddrs = null;
        ulong? newlyBoundAddr = null;
        ulong? truncatedAddr = null;
        ulong buttonAddr = 0;
        RemoteButton edge1 = RemoteButton.None;
        bool edge1Down = false;
        RemoteButton edge2 = RemoteButton.None;
        bool edge2Down = false;
        string? gattQueryLog = null;
        string? bindLog = null;
        string? formatLog = null;
        string? redactionLog = null;
        string? diagnosticLog = null;

        if (kind == HciEvent)
        {
            if (packet is [0x05, 0x04, 0x00, _, _, _, ..])
            {
                ushort handle = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]) & 0x0FFF);
                lock (_stateLock)
                {
                    foreach (var source in _sources)
                    {
                        if (source.PendingHandle == handle) source.PendingHandle = null;
                        if (source.BoundHandle != handle) continue;
                        source.ResetConnectionState();
                        disconnectedAddrs ??= [];
                        disconnectedAddrs.Add(source.Source.Address);
                    }
                }
                if (disconnectedAddrs != null)
                {
                    foreach (var addr in disconnectedAddrs) DeviceDisconnected?.Invoke(addr);
                }
            }
            return;
        }

        if (kind is not (AclSent or AclReceived)) return;
        var rejection = ReadAttPacket(packet, out ushort connection, out var att, out bool incomplete, out int expectedPayloadLength);
        if (rejection != AttPacketRejection.None)
        {
            if (kind == AclSent)
            {
                if (ShouldLogProbeStage($"Sent.{rejection}"))
                    AppLogger.Info($"ETW probe rejected outgoing ACL: reason={rejection}, capturedLen={packet.Length}, acl=0x{connection:X4}", "ChromecastEtwReceiver");
                LogUnmatchedMarker(packet, rejection.ToString());
            }
            return;
        }
        if (kind == AclSent)
        {
            if (ShouldLogProbeStage("OutgoingAtt"))
                AppLogger.Info($"ETW probe saw outgoing ATT: opcode=0x{att[0]:X2}, len={att.Length}, incomplete={incomplete}", "ChromecastEtwReceiver");
            if (incomplete)
            {
                if (att[0] == 0x06 && ShouldLogProbeStage("IncompleteRequest"))
                    AppLogger.Info($"ETW probe saw incomplete ATT Find By Type Value request: capturedLen={att.Length}, acl=0x{connection:X4}", "ChromecastEtwReceiver");
                LogUnmatchedMarker(packet, "incomplete ATT request");
                return;
            }
            lock (_stateLock)
            {
                foreach (var source in _sources)
                {
                    if (!att.SequenceEqual(source.Request)) continue;
                    source.PendingHandle = connection;
                    gattQueryLog = $"BthPort observed GATT query for 0x{source.Source.Address:X12}";
                    break;
                }
            }
            if (gattQueryLog != null)
            {
                AppLogger.Info(gattQueryLog, "ChromecastEtwReceiver");
            }
            else
            {
                if (att[0] == 0x06 && ShouldLogProbeStage("UnmatchedRequest"))
                {
                    bool expectedPrefix = att is [0x06, 0x01, 0x00, 0xFF, 0xFF, 0x00, 0x28, ..];
                    AppLogger.Info($"ETW probe saw unmatched ATT Find By Type Value request: len={att.Length}, expectedPrefix={expectedPrefix}, acl=0x{connection:X4}", "ChromecastEtwReceiver");
                }
                LogUnmatchedMarker(att, "unexpected ATT request layout");
            }
            return;
        }

        lock (_stateLock)
        {
            var sources = _sources;
            var pendingSource = sources.FirstOrDefault(source => source.PendingHandle == connection);
            var boundSource = sources.FirstOrDefault(source => source.BoundHandle == connection);
            foreach (var source in new[] { pendingSource, boundSource })
            {
                if (source == null) continue;
                bool pending = source == pendingSource;
                bool response = pending && !incomplete &&
                    (att is [0x01, 0x06, _, _, _] || att is [0x07, ..]);
                if (response)
                {
                    BindLocked(sources, source, $"ATT response 0x{att[0]:X2}");
                    break;
                }
                if (!pending && source.BoundHandle != connection) continue;

                bool parsed = false;
                bool truncated = false;
                RemoteButton button = RemoteButton.None;
                bool isDown = false;
                G9N9NReportFormat format = G9N9NReportFormat.Unknown;
                ushort attribute = 0;

                if (source.Source.Protocol == EtwProtocolKind.GoogleG9N9N)
                {
                    if (att.Length >= 3 && att[0] is 0x1B or 0x1D)
                    {
                        byte opcode = att[0];
                        attribute = BinaryPrimitives.ReadUInt16LittleEndian(att[1..]);
                        var report = att[3..];
                        var decision = EvaluateG9N9NAttribute(source, attribute);
                        if (decision == G9N9NAttributeDecision.Allowed)
                        {
                            if (incomplete)
                            {
                                truncated = report.IsEmpty;
                            }
                            else
                            {
                                parsed = TryParseG9N9NPayload(report, out button, out isDown, out format);
                                if (!parsed && source.BoundHandle == connection)
                                {
                                    diagnosticLog ??= TryRecordG9N9NDiagnosticLocked(
                                        source, "UnknownPayload", attribute, opcode, report);
                                }
                            }
                        }
                        else if (source.BoundHandle == connection && !incomplete &&
                                 decision is G9N9NAttributeDecision.OutsideHidRange or G9N9NAttributeDecision.UnlistedAttribute)
                        {
                            string reason = decision == G9N9NAttributeDecision.OutsideHidRange
                                ? "OutsideHidRange"
                                : "UnlistedAttribute";
                            diagnosticLog ??= TryRecordG9N9NDiagnosticLocked(
                                source, reason, attribute, opcode, report);
                        }
                    }
                }
                else
                {
                    parsed = TryParseButtonReport(att, incomplete, source.Source.Protocol,
                        out button, out isDown, out truncated);
                }

                if (pending && parsed && isDown) BindLocked(sources, source, "button notification");
                if (source.BoundHandle != connection) continue;
                if (parsed)
                {
                    if (source.Source.Protocol == EtwProtocolKind.GoogleG9N9N)
                    {
                        if (isDown && attribute == G9N9NInputAttribute && !source.LockedG9N9NHandle.HasValue)
                        {
                            source.LockedG9N9NHandle = G9N9NInputAttribute;
                        }
                        if (source.LoggedFormats.Add((attribute, format)))
                        {
                            formatLog =
                                $"G9N9N ETW report format observed: addr=0x{source.Source.Address:X12}, attr=0x{attribute:X4}, format={format}, len={att.Length - 3}, sample={FormatHex(att[3..])}, button={button}, down={isDown}";
                        }
                    }
                    else if (!isDown && source.Source.Protocol == EtwProtocolKind.Xiaomi &&
                        att.Length == 11 && BinaryPrimitives.ReadUInt16LittleEndian(att[1..]) == XiaomiKeyboardAttribute &&
                        source.Held is not (RemoteButton.Back or RemoteButton.VolumeUp or RemoteButton.VolumeDown))
                    {
                        break;
                    }
                    RecordButtonEdgesLocked(source, button, isDown);
                }
                else if (truncated)
                {
                    if (!source.SawRedaction)
                    {
                        source.SawRedaction = true;
                        redactionLog = source.Source.Protocol == EtwProtocolKind.GoogleG9N9N
                            ? $"Windows omitted a verified button code (addr=0x{source.Source.Address:X12}, attr=0x{attribute:X4}, expectedLen={expectedPayloadLength})"
                            : "Windows omitted a verified button code";
                    }
                    RecordButtonEdgesLocked(source, RemoteButton.None, false);
                    truncatedAddr = source.Source.Address;
                }
                break;
            }

            void BindLocked(SourceState[] allSources, SourceState source, string evidence)
            {
                bool rebindingSameHandle = source.BoundHandle == connection;
                foreach (var other in allSources)
                {
                    if (other == source || other.BoundHandle != connection) continue;
                    other.ResetConnectionState();
                    disconnectedAddrs ??= [];
                    disconnectedAddrs.Add(other.Source.Address);
                }
                if (!rebindingSameHandle)
                {
                    source.ResetConnectionState();
                }
                source.BoundHandle = connection;
                source.PendingHandle = null;
                newlyBoundAddr = source.Source.Address;
                string hidRange = source.HasHidRange
                    ? $"0x{source.HidHandleStart:X4}..0x{source.HidHandleEnd:X4}"
                    : "none";
                bindLog =
                    $"ETW bound 0x{source.Source.Address:X12} to ACL 0x{connection:X4} (via={evidence}, protocol={source.Source.Protocol}, hidRange={hidRange}, excluded={source.ExcludedAttributes.Count})";
            }

            void RecordButtonEdgesLocked(SourceState source, RemoteButton btn, bool isDown)
            {
                var next = isDown ? btn : RemoteButton.None;
                var previous = source.Held;
                if (next == previous) return;
                source.Held = next;
                buttonAddr = source.Source.Address;
                if (previous != RemoteButton.None)
                {
                    edge1 = previous;
                    edge1Down = false;
                }
                if (next != RemoteButton.None)
                {
                    if (edge1 == RemoteButton.None)
                    {
                        edge1 = next;
                        edge1Down = true;
                    }
                    else
                    {
                        edge2 = next;
                        edge2Down = true;
                    }
                }
            }
        }

        if (bindLog != null)
        {
            AppLogger.Info(bindLog, "ChromecastEtwReceiver");
        }
        if (formatLog != null)
        {
            AppLogger.Info(formatLog, "ChromecastEtwReceiver");
        }
        if (diagnosticLog != null)
        {
            AppLogger.Info(diagnosticLog, "ChromecastEtwReceiver");
        }
        if (redactionLog != null)
        {
            AppLogger.Warn(redactionLog, "ChromecastEtwReceiver");
        }
        if (disconnectedAddrs != null)
        {
            foreach (var addr in disconnectedAddrs) DeviceDisconnected?.Invoke(addr);
        }
        if (newlyBoundAddr.HasValue)
        {
            SourceBound?.Invoke(newlyBoundAddr.Value);
        }
        if (edge1 != RemoteButton.None)
        {
            AppLogger.Info($"ETW button 0x{buttonAddr:X12}: {edge1} {(edge1Down ? "Down" : "Up")}", "ChromecastEtwReceiver");
            ButtonEvent?.Invoke(buttonAddr, edge1, edge1Down);
        }
        if (edge2 != RemoteButton.None)
        {
            AppLogger.Info($"ETW button 0x{buttonAddr:X12}: {edge2} {(edge2Down ? "Down" : "Up")}", "ChromecastEtwReceiver");
            ButtonEvent?.Invoke(buttonAddr, edge2, edge2Down);
        }
        if (truncatedAddr.HasValue)
        {
            TruncatedPacketDetected?.Invoke(truncatedAddr.Value);
        }
    }

    private void LogUnmatchedMarker(ReadOnlySpan<byte> packet, string reason)
    {
        if (packet.Length < 16) return;
        string? message = null;
        lock (_stateLock)
        {
            foreach (var source in _sources)
            {
                if (source.BoundHandle.HasValue || source.LoggedUnmatchedMarker) continue;
                int offset = packet.IndexOf(source.Request.AsSpan(7));
                if (offset < 0) continue;
                source.LoggedUnmatchedMarker = true;
                message = $"ETW probe marker seen in rejected packet: addr=0x{source.Source.Address:X12}, reason={reason}, markerOffset={offset}, capturedLen={packet.Length}";
                break;
            }
        }
        if (message != null) AppLogger.Info(message, "ChromecastEtwReceiver");
    }

    private static G9N9NAttributeDecision EvaluateG9N9NAttribute(SourceState source, ushort attribute)
    {
        if (source.ExcludedAttributes.Contains(attribute))
        {
            return G9N9NAttributeDecision.Excluded;
        }
        if (source.LockedG9N9NHandle is { } locked)
        {
            return attribute == locked
                ? G9N9NAttributeDecision.Allowed
                : G9N9NAttributeDecision.IgnoredAfterLock;
        }
        if (source.HasHidRange)
        {
            return attribute >= source.HidHandleStart && attribute <= source.HidHandleEnd
                ? G9N9NAttributeDecision.Allowed
                : G9N9NAttributeDecision.OutsideHidRange;
        }
        return attribute is G9N9NInputAttribute or G9N9NShortReportAttribute
            ? G9N9NAttributeDecision.Allowed
            : G9N9NAttributeDecision.UnlistedAttribute;
    }

    private static string? TryRecordG9N9NDiagnosticLocked(
        SourceState source,
        string reason,
        ushort attribute,
        byte opcode,
        ReadOnlySpan<byte> report)
    {
        if (source.DiagnosticCapLogged) return null;

        // Never log raw bytes for oversized notifications (e.g. 160-byte ADPCM audio frames).
        string hex = report.Length > MaxDiagnosticHexBytes ? "<omitted>" : FormatHex(report);
        string key = $"{reason}:{attribute:X4}:{report.Length}:{hex}";
        if (!source.LoggedDiagnostics.Add(key)) return null;

        if (source.LoggedDiagnostics.Count > MaxDiagnosticEntriesPerConnection)
        {
            source.DiagnosticCapLogged = true;
            return $"G9N9N ETW unrecognized notification log cap ({MaxDiagnosticEntriesPerConnection}) reached for addr=0x{source.Source.Address:X12}; suppressing further entries for this connection.";
        }

        string hidRange = source.HasHidRange
            ? $"0x{source.HidHandleStart:X4}..0x{source.HidHandleEnd:X4}"
            : "none";
        return $"G9N9N ETW unrecognized notification [{reason}]: addr=0x{source.Source.Address:X12}, attr=0x{attribute:X4}, opcode=0x{opcode:X2}, len={report.Length}, hex={hex} (hidRange={hidRange})";
    }

    private static string FormatHex(ReadOnlySpan<byte> data) =>
        data.IsEmpty ? "-" : Convert.ToHexString(data);

    private static AttPacketRejection ReadAttPacket(ReadOnlySpan<byte> packet, out ushort connection, out ReadOnlySpan<byte> att, out bool incomplete, out int expectedPayloadLength)
    {
        connection = 0;
        att = default;
        incomplete = false;
        expectedPayloadLength = 0;
        if (packet.Length < 8) return AttPacketRejection.ShortHeader;

        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        connection = (ushort)(header & 0x0FFF);
        if (connection > 0x0EFF || (header & 0xD000) != 0) return AttPacketRejection.UnsupportedAclHeader;

        int aclLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        int l2capLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
        if (aclLength != l2capLength + 4) return AttPacketRejection.InconsistentLengths;
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]) != 4) return AttPacketRejection.NonAttChannel;

        int availableAtt = packet.Length - 8;
        incomplete = availableAtt < l2capLength;
        att = incomplete ? packet[8..] : packet.Slice(8, l2capLength);
        expectedPayloadLength = l2capLength >= 3 ? l2capLength - 3 : 0;
        // Observed redactions strip HID notification payloads of 1, 2, 3, or 8 bytes down to the 3-byte ATT header.
        if (incomplete && !(l2capLength is 4 or 5 or 6 or 11 && att.Length == 3))
            return AttPacketRejection.UnsupportedTruncation;
        return att.IsEmpty ? AttPacketRejection.EmptyAtt : AttPacketRejection.None;
    }

    public static bool TryParseAclPacket(ReadOnlySpan<byte> packet, out RemoteButton button, out bool isDown, EtwProtocolKind protocolKind = EtwProtocolKind.GoogleG9N9N) =>
        TryParseAclPacket(packet, out button, out isDown, out _, protocolKind);

    public static bool TryParseAclPacket(ReadOnlySpan<byte> packet, out RemoteButton button, out bool isDown, out bool truncated, EtwProtocolKind protocolKind = EtwProtocolKind.GoogleG9N9N, IReadOnlySet<ushort>? verifiedAttributes = null)
    {
        button = RemoteButton.None;
        isDown = truncated = false;
        return ReadAttPacket(packet, out _, out var att, out bool incomplete, out _) == AttPacketRejection.None &&
            TryParseButtonReport(att, incomplete, protocolKind, out button, out isDown, out truncated, verifiedAttributes);
    }

    private static bool TryParseButtonReport(ReadOnlySpan<byte> att, bool incomplete, EtwProtocolKind protocol, out RemoteButton button, out bool isDown, out bool truncated, IReadOnlySet<ushort>? customAttributes = null)
    {
        button = RemoteButton.None;
        isDown = truncated = false;
        if (att.Length < 3 || att[0] is not (0x1B or 0x1D)) return false;

        ushort attribute = BinaryPrimitives.ReadUInt16LittleEndian(att[1..]);
        var report = att[3..];

        if (protocol == EtwProtocolKind.GoogleG9N9N)
        {
            var allowed = customAttributes ?? _g9n9nAttributesSet;
            if (!allowed.Contains(attribute)) return false;

            if (incomplete)
            {
                truncated = report.IsEmpty;
                return false;
            }

            return TryParseG9N9NPayload(report, out button, out isDown, out _);
        }
        else if (protocol == EtwProtocolKind.Xiaomi)
        {
            if (incomplete) return false;

            if (customAttributes != null)
            {
                if (!customAttributes.Contains(attribute)) return false;
            }
            else if (!IsXiaomiButtonAttribute(attribute))
            {
                return false;
            }

            // The RC003 sends an eight-byte keyboard report without a report ID.
            // Standard keys already arrive through Raw Input. Recover only the dropped usages.
            if (attribute == XiaomiKeyboardAttribute && report.Length == 8)
            {
                if (report[0] != 0 || report[1] != 0) return false;
                foreach (byte usage in report[2..])
                {
                    button = usage switch
                    {
                        0xF1 => RemoteButton.Back,
                        0x80 => RemoteButton.VolumeUp,
                        0x81 => RemoteButton.VolumeDown,
                        _ => RemoteButton.None
                    };
                    if (button != RemoteButton.None)
                    {
                        isDown = true;
                        return true;
                    }
                }

                return report is [0, 0, 0, 0, 0, 0, 0, 0];
            }

            // Exclude non-button reports: ATVV AudioSync (0x0A) and mouse movement (Report ID 3)
            if (report.Length >= 7 && report[0] == 0x0A) return false;
            if (report.Length >= 3 && report[0] == 0x03) return false;

            // Reports with Report ID: must be Report ID 1 (keyboard) or 2 (consumer)
            bool isReportWithId = report.Length is >= 3 and <= 9 && (report[0] is 0x01 or 0x02);
            // Reports without Report ID: 2-byte consumer reports (Back 0x0224, VolumeUp 0x00E9, VolumeDown 0x00EA)
            bool isReportWithoutId = report.Length == 2;

            if ((isReportWithId || isReportWithoutId) && XiaomiProtocol.TryParseReport(report, out var xiaomiButtons))
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
        else if (protocol == EtwProtocolKind.T1Remote)
        {
            if (incomplete) return false;
            if (customAttributes != null && !customAttributes.Contains(attribute)) return false;

            if (report.Length is >= 3 and <= 9 && (report[0] is 0x01 or 0x02 or 0x5A) &&
                T1RemoteProtocol.TryParseReport(report, out var t1Buttons, out _))
            {
                if (t1Buttons.Count > 0)
                {
                    button = t1Buttons[0];
                    isDown = true;
                    return true;
                }
                isDown = false;
                return true;
            }
            return false;
        }

        return false;
    }

    internal static bool TryParseG9N9NPayload(
        ReadOnlySpan<byte> report,
        out RemoteButton button,
        out bool isDown,
        out G9N9NReportFormat format)
    {
        button = RemoteButton.None;
        isDown = false;
        format = G9N9NReportFormat.Unknown;

        if (report.IsEmpty) return false;

        // Match 8-byte indexed reports first so Up (0x03) and Home (0x0A) are not filtered out as mouse/ATVV headers.
        if (report is [_, 0, 0, 0, 0, 0, 0, 0])
        {
            byte code = report[0];
            if (code == 0)
            {
                format = G9N9NReportFormat.Indexed8Byte;
                return true;
            }
            if (code > G9N9NProtocol.IndexedConsumerUsages.Length) return false;
            isDown = G9N9NProtocol.ConsumerButtons.TryGetValue(G9N9NProtocol.IndexedConsumerUsages[code - 1], out button);
            if (isDown) format = G9N9NReportFormat.Indexed8Byte;
            return isDown;
        }

        // Exclude non-button reports: ATVV AudioSync / GetCapabilities (0x0A) and mouse movement (Report ID 3)
        if (report.Length >= 7 && report[0] == 0x0A) return false;
        if (report.Length >= 3 && report[0] == 0x03) return false;

        if (report.Length == 1)
        {
            byte code = report[0];
            if (code == 0)
            {
                format = G9N9NReportFormat.Indexed1Byte;
                return true;
            }
            if (code > G9N9NProtocol.IndexedConsumerUsages.Length) return false;
            isDown = G9N9NProtocol.ConsumerButtons.TryGetValue(G9N9NProtocol.IndexedConsumerUsages[code - 1], out button);
            if (isDown) format = G9N9NReportFormat.Indexed1Byte;
            return isDown;
        }

        if (report.Length == 2)
        {
            if (report[0] == 0 && report[1] == 0)
            {
                format = G9N9NReportFormat.Release2Byte;
                return true;
            }

            // First byte >= 1 avoids misinterpreting ATVV AUDIO_STOP [0x00, 0x02] as Play/Pause index 2.
            if (report[0] >= 1 &&
                report[0] <= G9N9NProtocol.IndexedConsumerUsages.Length &&
                report[1] <= G9N9NProtocol.IndexedConsumerUsages.Length)
            {
                isDown = G9N9NProtocol.ConsumerButtons.TryGetValue(
                    G9N9NProtocol.IndexedConsumerUsages[report[0] - 1],
                    out button);
                if (isDown) format = G9N9NReportFormat.Indexed2Byte;
                return isDown;
            }

            ushort usage = BinaryPrimitives.ReadUInt16LittleEndian(report);
            isDown = G9N9NProtocol.ConsumerButtons.TryGetValue(usage, out button);
            if (isDown) format = G9N9NReportFormat.ConsumerUsage2Byte;
            return isDown;
        }

        if (report.Length == 3)
        {
            if (report[0] is 0x01 or 0x02 && G9N9NProtocol.TryParseReport(report, out var g9Buttons))
            {
                format = G9N9NReportFormat.ReportId3Byte;
                if (g9Buttons.Count > 0)
                {
                    button = g9Buttons[0];
                    isDown = true;
                }
                return true;
            }

            if (report is [0, 0, 0])
            {
                format = G9N9NReportFormat.ConsumerUsage3Byte;
                return true;
            }

            if (report[2] == 0)
            {
                ushort usage = BinaryPrimitives.ReadUInt16LittleEndian(report);
                isDown = G9N9NProtocol.ConsumerButtons.TryGetValue(usage, out button);
                if (isDown) format = G9N9NReportFormat.ConsumerUsage3Byte;
                return isDown;
            }
        }

        return false;
    }

    private static bool IsXiaomiButtonAttribute(ushort attribute)
    {
        if (_xiaomiAttributesSet.Contains(attribute)) return true;
        // Range 0x0050..0x0075 corresponds to Xiaomi HID over GATT service instance
        if (attribute is >= 0x0050 and <= 0x0075) return true;
        return false;
    }

    private void DispatchButtonEvent(SourceState source, RemoteButton button, bool isDown)
    {
        var next = isDown ? button : RemoteButton.None;
        var previous = source.Held;
        if (next == previous) return;
        source.Held = next;
        if (previous != RemoteButton.None) Emit(previous, false);
        if (next != RemoteButton.None) Emit(next, true);

        void Emit(RemoteButton edge, bool down)
        {
            AppLogger.Info($"ETW button 0x{source.Source.Address:X12}: {edge} {(down ? "Down" : "Up")}", "ChromecastEtwReceiver");
            ButtonEvent?.Invoke(source.Source.Address, edge, down);
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
        Thread? threadToJoin;
        IntPtr logfileToFree;
        IntPtr propertiesToFree;
        EventRecordCallback? callbackToKeepAlive;

        lock (_lock)
        {
            _isRunning = false;
            try { _flushCancellation?.Cancel(); } catch { }
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

            SetSources([]);

            threadToJoin = _processThread;
            _processThread = null;
            logfileToFree = _logfileBuffer;
            _logfileBuffer = IntPtr.Zero;
            propertiesToFree = _propertiesBuffer;
            _propertiesBuffer = IntPtr.Zero;
            callbackToKeepAlive = _callbackHolder;
            _callbackHolder = null;
        }

        bool exited = true;
        if (threadToJoin != null && threadToJoin.IsAlive)
        {
            exited = threadToJoin.Join(2000);
        }

        if (exited)
        {
            FreeUnmanagedBuffers(logfileToFree, propertiesToFree);
        }
        else
        {
            AppLogger.Warn("Chromecast ETW worker thread did not exit within 2000ms during Stop(); skipping unmanaged buffer free to avoid use-after-free", "ChromecastEtwReceiver");
        }
        GC.KeepAlive(callbackToKeepAlive);

        AppLogger.Info("Chromecast ETW receiver stopped", "ChromecastEtwReceiver");
    }

    private void CleanupUnmanaged()
    {
        FreeUnmanagedBuffers(_logfileBuffer, _propertiesBuffer);
        _logfileBuffer = IntPtr.Zero;
        _propertiesBuffer = IntPtr.Zero;
        _callbackHolder = null;
    }

    private static void FreeUnmanagedBuffers(IntPtr logfileBuffer, IntPtr propertiesBuffer)
    {
        if (logfileBuffer != IntPtr.Zero)
        {
            var logfile = Marshal.PtrToStructure<EVENT_TRACE_LOGFILEW>(logfileBuffer);
            if (logfile.LoggerName != IntPtr.Zero) Marshal.FreeHGlobal(logfile.LoggerName);
            Marshal.FreeHGlobal(logfileBuffer);
        }

        if (propertiesBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(propertiesBuffer);
        }
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
