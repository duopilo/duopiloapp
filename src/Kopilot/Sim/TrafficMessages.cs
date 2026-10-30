using System.Buffers.Binary;
using System.Text;
using Kopilot.Localization;

namespace Kopilot.Sim;

/// <summary>Data Definition der Verkehrsabfrage: nur Zahlen (Hintergrundscan) oder Zahlen plus Texte (get_traffic).</summary>
public sealed record TrafficLayout(uint DefineId, bool WithText)
{
    /// <summary>So viele Byte Nutzdaten muss MSFS je Flugzeug liefern.</summary>
    public int PayloadBytes => TrafficVariables.PayloadBytes(WithText);
}

/// <summary>Werte eines Flugzeugs aus einer BYTYPE-Nachricht. Texte sind null, wenn sie nicht abgefragt wurden oder fehlen.</summary>
public sealed record TrafficRecord(
    double Latitude,
    double Longitude,
    double AltitudeFt,
    double HeadingTrueDeg,
    double HeadingMagDeg,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    bool OnGround,
    bool IsUser,
    string? AtcId,
    string? AtcAirline,
    string? AtcFlightNumber,
    string? AtcModel);

/// <summary>
/// Eine SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE-Nachricht, gelesen mit Größenprüfung.
/// </summary>
/// <param name="DwSize">Größe laut Kopf der Nachricht (dwSize).</param>
/// <param name="ReceivedBytes">Größe laut SimConnect_GetNextDispatch (pcbData).</param>
/// <param name="HeaderComplete">Der Kopf (40 Byte) ist vollständig – sonst sind die IDs 0.</param>
/// <param name="PayloadBytes">Nutzdaten hinter dem Kopf, höchstens min(dwSize, pcbData) − 40.</param>
/// <param name="ExpectedPayloadBytes">Nutzdaten, die die Data Definition verlangt; 0 bei unbekannter Definition.</param>
/// <param name="Record">Gelesene Werte, null bei leerer Antwort, zu kurzem Datenblock oder unbekannter Definition.</param>
public sealed record ByTypeMessage(
    uint DwSize,
    int ReceivedBytes,
    bool HeaderComplete,
    uint RequestId,
    uint ObjectId,
    uint DefineId,
    uint EntryNumber,
    uint OutOf,
    int PayloadBytes,
    int ExpectedPayloadBytes,
    TrafficRecord? Record)
{
    /// <summary>dwoutof = 0: kein Objekt dieses Typs im Umkreis.</summary>
    public bool IsEmptyReply => HeaderComplete && OutOf == 0;

    /// <summary>Datenblock kürzer als die Data Definition (Hypothese: Live-Traffic liefert weniger).</summary>
    public bool IsShort => HeaderComplete && OutOf > 0 && ExpectedPayloadBytes > 0 && PayloadBytes < ExpectedPayloadBytes;

    public bool SizeMismatch => DwSize != (uint)ReceivedBytes;
}

/// <summary>
/// Liest SimConnect-Nachrichten aus dem Rohspeicher, ohne je über die Nachricht hinaus zu lesen.
///
/// Hintergrund (Testlog 0.9, drei Abstürze): Die verwaltete SimConnect-Bibliothek marshalt BYTYPE-Antworten mit der vollen
/// registrierten Struktur (damals 328 Byte mit vier Texten), ohne dwSize zu prüfen. Vermutlich ist die Nachricht manchmal
/// kürzer – die Antwort „kein Objekt im Umkreis“ (dwoutof = 0, bei jeder Hubschrauber-Abfrage) oder ein kurzer Datenblock bei
/// Live-Traffic. Liegt der Speicher dahinter auf einer nicht gemappten Seite, folgt eine nicht abfangbare AccessViolation in
/// CSTRMarshaler.ConvertFixedToManaged. <see cref="TrafficMessageStats"/> prüft beide Vermutungen im Flug.
/// Hier wird nur gelesen, was laut dwSize und pcbData zur Nachricht gehört.
/// </summary>
public static class TrafficMessageParser
{
    /// <summary>SIMCONNECT_RECV: dwSize, dwVersion, dwID.</summary>
    public const int RecvHeaderBytes = 12;

    /// <summary>SIMCONNECT_RECV_SIMOBJECT_DATA bis dwData: Kopf plus dwRequestID, dwObjectID, dwDefineID, dwFlags, dwentrynumber, dwoutof, dwDefineCount.</summary>
    public const int DataHeaderBytes = RecvHeaderBytes + (7 * 4);

    public const uint RecvIdException = 1;
    public const uint RecvIdOpen = 2;
    public const uint RecvIdQuit = 3;
    public const uint RecvIdSimObjectDataByType = 9;

    private static readonly Encoding Ansi = Encoding.Latin1;

    /// <summary>dwID der Nachricht, null wenn nicht einmal der Grundkopf da ist.</summary>
    public static uint? MessageId(ReadOnlySpan<byte> received) =>
        received.Length >= RecvHeaderBytes ? U32(received, 8) : null;

    /// <summary>
    /// Liest eine BYTYPE-Nachricht. <paramref name="received"/> sind die pcbData Byte von SimConnect_GetNextDispatch.
    /// Null, wenn es keine BYTYPE-Nachricht ist.
    /// </summary>
    public static ByTypeMessage? ParseByType(ReadOnlySpan<byte> received, IReadOnlyList<TrafficLayout> layouts)
    {
        if (MessageId(received) != RecvIdSimObjectDataByType)
        {
            return null;
        }

        var dwSize = U32(received, 0);
        var length = (int)Math.Min(dwSize, (uint)received.Length);
        if (length < DataHeaderBytes)
        {
            return new ByTypeMessage(dwSize, received.Length, false, 0, 0, 0, 0, 0, 0, 0, null);
        }

        var requestId = U32(received, 12);
        var objectId = U32(received, 16);
        var defineId = U32(received, 20);
        var entryNumber = U32(received, 28);
        var outOf = U32(received, 32);
        var payload = received[DataHeaderBytes..length];

        TrafficLayout? layout = null;
        foreach (var l in layouts)
        {
            if (l.DefineId == defineId)
            {
                layout = l;
                break;
            }
        }

        var expected = layout?.PayloadBytes ?? 0;
        TrafficRecord? record = null;
        if (outOf > 0 && layout is not null && payload.Length >= TrafficVariables.NumericBytes)
        {
            var textStart = TrafficVariables.NumericBytes;
            var size = TrafficVariables.TextFieldBytes;
            record = new TrafficRecord(
                Number(payload, 0),
                Number(payload, 1),
                Number(payload, 2),
                Number(payload, 3),
                Number(payload, 4),
                Number(payload, 5),
                Number(payload, 6),
                Number(payload, 7) != 0,
                Number(payload, 8) != 0,
                layout.WithText ? Text(payload, textStart, size) : null,
                layout.WithText ? Text(payload, textStart + size, size) : null,
                layout.WithText ? Text(payload, textStart + (2 * size), size) : null,
                layout.WithText ? Text(payload, textStart + (3 * size), size) : null);
        }

        return new ByTypeMessage(dwSize, received.Length, true, requestId, objectId, defineId, entryNumber, outOf, payload.Length, expected, record);
    }

    /// <summary>SIMCONNECT_RECV_EXCEPTION: dwException, dwSendID, dwIndex – null, wenn die Nachricht zu kurz ist.</summary>
    public static (uint Exception, uint SendId, uint Index)? ParseException(ReadOnlySpan<byte> received)
    {
        var length = received.Length >= RecvHeaderBytes ? (int)Math.Min(U32(received, 0), (uint)received.Length) : 0;
        return length >= RecvHeaderBytes + 12 ? (U32(received, 12), U32(received, 16), U32(received, 20)) : null;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static double Number(ReadOnlySpan<byte> payload, int index) =>
        BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(index * TrafficVariables.NumericFieldBytes, TrafficVariables.NumericFieldBytes));

    /// <summary>Fester ANSI-String (STRING64) bis zum ersten Nullbyte; null, wenn das Feld nicht vollständig in der Nachricht liegt oder leer ist.</summary>
    private static string? Text(ReadOnlySpan<byte> payload, int offset, int size)
    {
        if (payload.Length < offset + size)
        {
            return null;
        }

        var field = payload.Slice(offset, size);
        var end = field.IndexOf((byte)0);
        if (end >= 0)
        {
            field = field[..end];
        }

        var text = Ansi.GetString(field).Trim();
        return text.Length > 0 ? text : null;
    }
}

/// <summary>
/// Sammelt die Antworten einer Verkehrsabfrage (je SimObject-Typ eine Anfrage). Antworten zu anderen Anfragen
/// (z. B. verspätet aus einer früheren Abfrage) werden ignoriert. Rein rechnend, testbar.
/// </summary>
public sealed class ByTypeCollector
{
    private readonly Dictionary<uint, Request> _requests = [];

    private sealed class Request(string category)
    {
        public string Category { get; } = category;

        public List<AircraftSample> Samples { get; } = [];

        public int Received { get; set; }

        public bool Done { get; set; }
    }

    public void Expect(uint requestId, string category) => _requests[requestId] = new Request(category);

    public bool IsDone(uint requestId) => _requests.TryGetValue(requestId, out var r) && r.Done;

    /// <summary>Verarbeitet eine Nachricht. false, wenn sie zu keiner erwarteten Anfrage gehört.</summary>
    public bool Add(ByTypeMessage message)
    {
        if (!message.HeaderComplete || !_requests.TryGetValue(message.RequestId, out var request))
        {
            return false;
        }

        // dwentrynumber zählt ab 1 bis dwoutof; jede Nachricht zählt, auch ohne lesbaren Datensatz
        if (message.OutOf > 0)
        {
            request.Received++;
        }

        if (message.Record is { } r)
        {
            request.Samples.Add(new AircraftSample(
                message.ObjectId,
                request.Category,
                r.AtcId,
                r.AtcAirline,
                r.AtcFlightNumber,
                r.AtcModel,
                r.Latitude,
                r.Longitude,
                r.AltitudeFt,
                r.HeadingTrueDeg,
                r.HeadingMagDeg,
                r.GroundSpeedKt,
                r.VerticalSpeedFpm,
                r.OnGround,
                r.IsUser));
        }

        if (message.OutOf == 0 || message.EntryNumber >= message.OutOf || request.Received >= message.OutOf)
        {
            request.Done = true;
        }

        return true;
    }

    public List<AircraftSample> Samples() => _requests.Values.SelectMany(r => r.Samples).ToList();
}

/// <summary>Zähler über alle BYTYPE-Nachrichten – um die beiden Absturz-Hypothesen im Flug zu prüfen.</summary>
/// <param name="EmptyReplies">Antworten mit dwoutof = 0.</param>
/// <param name="EmptyMinDwSize">Kleinste dwSize einer leeren Antwort.</param>
/// <param name="ShortMessages">Datenblock kürzer als die Data Definition (dwoutof &gt; 0).</param>
/// <param name="LegacyDwSize">Nachrichtengröße, die die verwaltete Bibliothek bis 0.9 vorausgesetzt hat (Kopf + 328 Byte).</param>
public sealed record TrafficMessageStatsSnapshot(
    DateTimeOffset? SinceUtc,
    long Messages,
    long EmptyReplies,
    uint? EmptyMinDwSize,
    uint? EmptyMaxDwSize,
    long DataMessages,
    uint? DataMinDwSize,
    uint? DataMaxDwSize,
    long ShortMessages,
    int? ShortMinPayloadBytes,
    long SizeMismatches,
    long Invalid,
    int LegacyDwSize)
{
    /// <summary>Hypothese 2: Die leere Antwort ist kürzer als Kopf + alte Struktur – die alte Bibliothek hätte darüber hinaus gelesen.</summary>
    public string EmptyReplyVerdict => EmptyReplies == 0
        ? Loc.T("traffic.verdict.emptyNone")
        : EmptyMinDwSize < LegacyDwSize
            ? Loc.T("traffic.verdict.emptyConfirmed", EmptyReplies, Range(EmptyMinDwSize, EmptyMaxDwSize), LegacyDwSize)
            : Loc.T("traffic.verdict.emptyRefuted", Range(EmptyMinDwSize, EmptyMaxDwSize));

    /// <summary>Hypothese 1: Live-Traffic liefert kürzere Datenblöcke als die Data Definition.</summary>
    public string ShortBlockVerdict => DataMessages == 0
        ? Loc.T("traffic.verdict.shortNone")
        : ShortMessages > 0
            ? Loc.T("traffic.verdict.shortConfirmed", ShortMessages, DataMessages, ShortMinPayloadBytes)
            : Loc.T("traffic.verdict.shortNotSeen", DataMessages, Range(DataMinDwSize, DataMaxDwSize));

    private static string Range(uint? min, uint? max) => min == max ? $"{min}" : $"{min}–{max}";
}

/// <summary>Threadsichere Zähler über alle BYTYPE-Nachrichten der Verkehrsverbindung.</summary>
public sealed class TrafficMessageStats
{
    /// <summary>Kopf + neun Zahlen + vier Texte (368 Byte): so viel hat die verwaltete Bibliothek bis 0.9 bei jeder Antwort gelesen.</summary>
    public static readonly int LegacyMessageBytes = TrafficMessageParser.DataHeaderBytes + TrafficVariables.PayloadBytes(withText: true);

    private readonly object _gate = new();
    private DateTimeOffset? _since;
    private long _messages;
    private long _empty;
    private uint? _emptyMin;
    private uint? _emptyMax;
    private long _data;
    private uint? _dataMin;
    private uint? _dataMax;
    private long _short;
    private int? _shortMin;
    private long _mismatch;
    private long _invalid;

    public void Record(ByTypeMessage m, DateTimeOffset now)
    {
        lock (_gate)
        {
            _since ??= now;
            _messages++;
            if (m.SizeMismatch)
            {
                _mismatch++;
            }

            if (!m.HeaderComplete)
            {
                _invalid++;
                return;
            }

            if (m.IsEmptyReply)
            {
                _empty++;
                _emptyMin = Min(_emptyMin, m.DwSize);
                _emptyMax = Max(_emptyMax, m.DwSize);
                return;
            }

            _data++;
            _dataMin = Min(_dataMin, m.DwSize);
            _dataMax = Max(_dataMax, m.DwSize);
            if (m.IsShort)
            {
                _short++;
                _shortMin = _shortMin is { } s ? Math.Min(s, m.PayloadBytes) : m.PayloadBytes;
            }
        }
    }

    public TrafficMessageStatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TrafficMessageStatsSnapshot(
                _since, _messages, _empty, _emptyMin, _emptyMax, _data, _dataMin, _dataMax, _short, _shortMin, _mismatch, _invalid, LegacyMessageBytes);
        }
    }

    private static uint? Min(uint? a, uint b) => a is { } x ? Math.Min(x, b) : b;

    private static uint? Max(uint? a, uint b) => a is { } x ? Math.Max(x, b) : b;
}
