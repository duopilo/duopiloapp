using System.Runtime.InteropServices;
using Kopilot.Localization;
using Microsoft.Extensions.Options;

namespace Kopilot.Sim;

/// <summary>
/// Eigene SimConnect-Verbindung nur für die Verkehrsabfrage (RequestDataOnSimObjectType), direkt über die native
/// SimConnect.dll statt über die verwaltete Bibliothek.
///
/// Warum: Bis 0.9 lief die Abfrage über die Hauptverbindung. Deren verwaltete Bibliothek marshalt jede BYTYPE-Antwort mit
/// der vollen Struktur, ohne dwSize zu prüfen – drei Abstürze (AccessViolation in CSTRMarshaler.ConvertFixedToManaged,
/// in .NET nicht abfangbar). Hier liest <see cref="TrafficMessageParser"/> nur, was laut dwSize zur Nachricht gehört.
/// Die Hauptverbindung stellt keine BYTYPE-Anfragen mehr.
///
/// Der Hintergrundscan der Verkehrswarnung fragt nur Zahlen ab; Texte (ATC ID, Airline, Flugnummer, Typ) nur get_traffic.
/// Alle Aufrufe laufen nacheinander (eine Abfrage zur Zeit). Jede BYTYPE-Nachricht wird gezählt und, wenn eingeschaltet,
/// in logs\traffic-messages.csv geschrieben (<see cref="TrafficMessageLog"/>).
/// </summary>
public sealed class TrafficConnection : IDisposable
{
    /// <summary>Größter Radius, den SimConnect bei RequestDataOnSimObjectType annimmt (200 km).</summary>
    public const uint MaxScanRadiusMeters = 200_000;

    public static readonly TrafficLayout NumericLayout = new(1, WithText: false);

    public static readonly TrafficLayout TextLayout = new(2, WithText: true);

    private static readonly TrafficLayout[] Layouts = [NumericLayout, TextLayout];

    /// <summary>Größere Nachrichten gibt es bei BYTYPE nicht – Schutz gegen unsinnige pcbData.</summary>
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>So viele BYTYPE-Nachrichten je Verbindung kommen zusätzlich ins normale Log.</summary>
    private const int LoggedSampleMessages = 12;

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HelicopterGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(30);

    private readonly SimConnectClient _sim;
    private readonly KopilotOptions _options;
    private readonly ILogger<TrafficConnection> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AutoResetEvent _event = new(false);
    private readonly TrafficMessageStats _stats = new();
    private readonly TrafficMessageLog? _messageLog;

    // Nur unter _gate benutzt
    private IntPtr _handle;
    private DateTimeOffset? _openedFor;
    private bool _openReceived;
    private uint _nextRequestId;
    private int _timeoutsInRow;
    private int _loggedMessages;
    private int _loggedWarnings;
    private int _loggedExceptions;
    private DateTimeOffset _retryAfterUtc;
    private DateTimeOffset _lastSummaryUtc = DateTimeOffset.UtcNow;
    private string? _unavailable;

    public TrafficConnection(SimConnectClient sim, IOptions<KopilotOptions> options, ILogger<TrafficConnection> log)
    {
        _sim = sim;
        _options = options.Value;
        _log = log;
        if (_options.Monitoring.Traffic.MessageLog)
        {
            try
            {
                _messageLog = new TrafficMessageLog(Path.Combine(_options.ResolveDataDirectory(), "logs"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log.LogWarning(Loc.Log("log.traffic.messageLogUnavailable"), ex.Message);
            }
        }
    }

    /// <summary>Zähler über alle BYTYPE-Nachrichten seit dem Start, mit Urteil zu beiden Absturz-Hypothesen.</summary>
    public TrafficMessageStatsSnapshot Diagnostics => _stats.Snapshot();

    /// <summary>Pfad von traffic-messages.csv, null wenn aus.</summary>
    public string? MessageLogPath => _messageLog?.FilePath;

    /// <summary>
    /// Fragt alle Flugzeuge (auf Wunsch auch Hubschrauber) im Umkreis einmalig ab, das eigene Flugzeug eingeschlossen.
    /// Bleibt eine Antwort aus, kommt nach dem Timeout zurück, was bis dahin da ist (<see cref="AircraftScan.Complete"/> = false).
    /// </summary>
    /// <param name="withText">Auch ATC ID, Airline, Flugnummer und Typ (nur für get_traffic).</param>
    public async Task<AircraftScan> ScanAsync(uint radiusMeters, bool includeHelicopters, bool withText, CancellationToken ct)
    {
        if (!_sim.Connection.IsConnected)
        {
            return AircraftScan.Fail(Loc.T("sim.notConnected"));
        }

        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Scan(Math.Min(radiusMeters, MaxScanRadiusMeters), includeHelicopters, withText, ct), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait(TimeSpan.FromSeconds(2));
        try
        {
            Close(Loc.T("traffic.connection.closeShutdown"));
            _messageLog?.Dispose();
        }
        finally
        {
            _event.Dispose();
        }
    }

    // ------------------------------------------------------------------ Abfrage (unter _gate)

    private AircraftScan Scan(uint radiusMeters, bool includeHelicopters, bool withText, CancellationToken ct)
    {
        try
        {
            // Hauptverbindung neu aufgebaut (MSFS neu gestartet): eigene Verbindung auch neu
            var since = _sim.Connection.SinceUtc;
            if (_handle != IntPtr.Zero && _openedFor != since)
            {
                Close(Loc.T("traffic.connection.closeReconnected"));
            }

            if (_handle == IntPtr.Zero && Open(since) is { } openError)
            {
                return AircraftScan.Fail(openError);
            }

            // Liegengebliebenes (verspätete Antworten, Exceptions) vorher abholen
            Dispatch(null);
            if (_handle == IntPtr.Zero)
            {
                return AircraftScan.Fail(Loc.T("traffic.connection.endedBySim"));
            }

            var layout = withText ? TextLayout : NumericLayout;
            var collector = new ByTypeCollector();
            var aircraftId = Request(collector, layout, radiusMeters, NativeSimConnect.ObjectTypeAircraft, "aircraft");
            uint? helicopterId = includeHelicopters
                ? Request(collector, layout, radiusMeters, NativeSimConnect.ObjectTypeHelicopter, "helicopter")
                : null;

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1000, _options.ReadTimeoutMs));
            DateTimeOffset? aircraftDoneAt = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Dispatch(collector);
                if (_handle == IntPtr.Zero)
                {
                    return AircraftScan.Fail(Loc.T("traffic.connection.endedBySim"));
                }

                var now = DateTimeOffset.UtcNow;
                if (collector.IsDone(aircraftId))
                {
                    // Ohne Hubschrauber im Umkreis antwortet MSFS evtl. gar nicht – dann nicht den vollen Timeout abwarten
                    aircraftDoneAt ??= now;
                    if (helicopterId is not { } h || collector.IsDone(h) || now - aircraftDoneAt >= HelicopterGrace)
                    {
                        break;
                    }
                }
                else if (now >= deadline)
                {
                    break;
                }

                _event.WaitOne(20);
            }

            var complete = collector.IsDone(aircraftId);
            _timeoutsInRow = complete ? 0 : _timeoutsInRow + 1;
            if (_timeoutsInRow >= 3)
            {
                Close(Loc.T("traffic.connection.closeIncomplete"));
            }

            LogSummaryIfDue();
            return new AircraftScan(collector.Samples(), complete, null);
        }
        catch (TrafficConnectionException ex)
        {
            Close(ex.Message);
            return AircraftScan.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _unavailable = Loc.T("traffic.connection.dllUnusable", ex.Message);
            _log.LogError(ex, Loc.Log("log.traffic.dllUnusable"));
            _handle = IntPtr.Zero;
            return AircraftScan.Fail(_unavailable);
        }
    }

    /// <summary>Öffnet die Verbindung und legt beide Data Definitions an. Fehlertext oder null.</summary>
    private string? Open(DateTimeOffset? since)
    {
        if (_unavailable is not null)
        {
            return _unavailable;
        }

        if (DateTimeOffset.UtcNow < _retryAfterUtc)
        {
            return Loc.T("traffic.connection.retryLater");
        }

        var hr = NativeSimConnect.SimConnect_Open(
            out var handle, _options.ClientName + " Traffic", IntPtr.Zero, 0, _event.SafeWaitHandle.DangerousGetHandle(), 0);
        if (hr < 0 || handle == IntPtr.Zero)
        {
            _retryAfterUtc = DateTimeOffset.UtcNow + RetryDelay;
            return Loc.T("traffic.connection.openFailed", hr);
        }

        _handle = handle;
        _openedFor = since;
        _openReceived = false;
        _nextRequestId = 0;
        _timeoutsInRow = 0;
        _loggedMessages = 0;

        var deadline = DateTimeOffset.UtcNow + OpenTimeout;
        // Bewusst ohne Abbruch: halb geöffnet (ohne Data Definitions) darf die Verbindung nicht bleiben
        while (!_openReceived && _handle != IntPtr.Zero && DateTimeOffset.UtcNow < deadline)
        {
            _event.WaitOne(20);
            Dispatch(null);
        }

        if (!_openReceived)
        {
            Close(Loc.T("traffic.connection.closeNoAck"));
            _retryAfterUtc = DateTimeOffset.UtcNow + RetryDelay;
            return Loc.T("traffic.connection.notAcknowledged");
        }

        foreach (var layout in Layouts)
        {
            Define(layout);
        }

        if (_messageLog is null)
        {
            _log.LogInformation(Loc.Log("log.traffic.opened"));
        }
        else
        {
            _log.LogInformation(Loc.Log("log.traffic.openedWithLog"), _messageLog.FilePath);
        }

        return null;
    }

    private void Define(TrafficLayout layout)
    {
        foreach (var (name, unit) in TrafficVariables.Numeric)
        {
            Check(NativeSimConnect.SimConnect_AddToDataDefinition(
                _handle, layout.DefineId, name, unit, NativeSimConnect.DataTypeFloat64, 0, NativeSimConnect.Unused), "AddToDataDefinition " + name);
        }

        if (!layout.WithText)
        {
            return;
        }

        foreach (var name in TrafficVariables.Text)
        {
            Check(NativeSimConnect.SimConnect_AddToDataDefinition(
                _handle, layout.DefineId, name, null, NativeSimConnect.DataTypeString64, 0, NativeSimConnect.Unused), "AddToDataDefinition " + name);
        }
    }

    private uint Request(ByTypeCollector collector, TrafficLayout layout, uint radiusMeters, int objectType, string category)
    {
        var requestId = ++_nextRequestId;
        collector.Expect(requestId, category);
        Check(NativeSimConnect.SimConnect_RequestDataOnSimObjectType(_handle, requestId, layout.DefineId, radiusMeters, objectType), "RequestDataOnSimObjectType");
        return requestId;
    }

    /// <summary>Holt alle anstehenden Nachrichten ab. Jede wird sofort in einen eigenen Puffer kopiert (der Zeiger gilt nur bis zum nächsten Aufruf).</summary>
    private void Dispatch(ByTypeCollector? collector)
    {
        for (var i = 0; i < 1000 && _handle != IntPtr.Zero; i++)
        {
            if (NativeSimConnect.SimConnect_GetNextDispatch(_handle, out var data, out var size) < 0 || data == IntPtr.Zero || size == 0)
            {
                return;
            }

            var buffer = new byte[(int)Math.Min(size, MaxMessageBytes)];
            Marshal.Copy(data, buffer, 0, buffer.Length);
            Handle(buffer, collector);
        }
    }

    private void Handle(byte[] received, ByTypeCollector? collector)
    {
        switch (TrafficMessageParser.MessageId(received))
        {
            case TrafficMessageParser.RecvIdOpen:
                _openReceived = true;
                break;

            case TrafficMessageParser.RecvIdQuit:
                Close(Loc.T("traffic.connection.closeSimQuit"));
                break;

            case TrafficMessageParser.RecvIdException:
                if (TrafficMessageParser.ParseException(received) is { } ex && _loggedExceptions++ < 20)
                {
                    _log.LogWarning(
                        Loc.Log("log.traffic.exception"),
                        ex.Exception, ex.SendId, ex.Index);
                }

                break;

            case TrafficMessageParser.RecvIdSimObjectDataByType:
                if (TrafficMessageParser.ParseByType(received, Layouts) is { } message)
                {
                    var now = DateTimeOffset.UtcNow;
                    var kind = collector?.Add(message) == true ? "current" : "stale";
                    _stats.Record(message, now);
                    _messageLog?.Write(now, kind, message);
                    LogMessage(message, kind);
                }

                break;
        }
    }

    private void LogMessage(ByTypeMessage m, string kind)
    {
        if (_loggedMessages < LoggedSampleMessages)
        {
            _loggedMessages++;
            _log.LogInformation(
                Loc.Log("log.traffic.message"),
                kind, m.RequestId, m.DefineId, m.ObjectId, m.EntryNumber, m.OutOf, m.DwSize, m.ReceivedBytes, m.PayloadBytes, m.ExpectedPayloadBytes, TrafficMessageLog.Flags(m));
        }

        // Auffälliges immer melden (anfangs jede, dann jede hundertste)
        if ((m.IsShort || !m.HeaderComplete || m.SizeMismatch) && (_loggedWarnings++ < 10 || _loggedWarnings % 100 == 0))
        {
            _log.LogWarning(
                Loc.Log("log.traffic.messageSuspicious"),
                TrafficMessageLog.Flags(m), m.ObjectId, m.EntryNumber, m.OutOf, m.DwSize, m.ReceivedBytes, m.PayloadBytes, m.ExpectedPayloadBytes);
        }
    }

    private void LogSummaryIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSummaryUtc < SummaryInterval)
        {
            return;
        }

        _lastSummaryUtc = now;
        LogSummary();
    }

    private void LogSummary()
    {
        var d = _stats.Snapshot();
        if (d.Messages == 0)
        {
            return;
        }

        _log.LogInformation(
            Loc.Log("log.traffic.summary"),
            d.SinceUtc, d.Messages, d.EmptyReplyVerdict, d.ShortBlockVerdict);
    }

    private void Close(string reason)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeSimConnect.SimConnect_Close(_handle);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, Loc.Log("log.traffic.closeFailed"));
        }

        _handle = IntPtr.Zero;
        _openedFor = null;
        _log.LogInformation(Loc.Log("log.traffic.closed"), reason);
        LogSummary();
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new TrafficConnectionException(Loc.T("traffic.connection.callFailed", what, hr));
        }
    }

    private sealed class TrafficConnectionException(string message) : Exception(message);
}
