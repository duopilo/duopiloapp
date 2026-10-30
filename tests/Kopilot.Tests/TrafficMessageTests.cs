using System.Buffers.Binary;
using System.Text;
using Kopilot.Sim;

namespace Kopilot.Tests;

/// <summary>
/// Absturzanalyse Testlog 0.9: BYTYPE-Antworten werden nur so weit gelesen, wie dwSize und pcbData reichen.
/// Die Puffer in diesen Tests sind genau so lang wie die Nachricht – jeder Lesezugriff dahinter würde werfen.
/// </summary>
public sealed class TrafficMessageTests
{
    private static readonly IReadOnlyList<TrafficLayout> Layouts = [TrafficConnection.NumericLayout, TrafficConnection.TextLayout];

    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 0, 35, 0, TimeSpan.Zero);

    private const uint NumericDefine = 1;
    private const uint TextDefine = 2;

    /// <summary>Nutzdaten: neun FLOAT64, danach je Text ein STRING64-Feld (null = nur Nullbytes).</summary>
    private static byte[] Payload(double lat = 46.21, double lon = -87.86, double alt = 31_000, bool user = false, params string?[] texts)
    {
        var data = new byte[TrafficVariables.NumericBytes + (texts.Length * TrafficVariables.TextFieldBytes)];
        double[] numbers = [lat, lon, alt, 270, 268, 450, -800, 0, user ? 1 : 0];
        for (var i = 0; i < numbers.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(i * 8), numbers[i]);
        }

        for (var i = 0; i < texts.Length; i++)
        {
            if (texts[i] is { } t)
            {
                Encoding.Latin1.GetBytes(t).CopyTo(data.AsSpan(TrafficVariables.NumericBytes + (i * TrafficVariables.TextFieldBytes)));
            }
        }

        return data;
    }

    /// <summary>SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE; <paramref name="received"/> kürzt den Puffer (pcbData).</summary>
    private static byte[] ByType(uint requestId, uint objectId, uint defineId, uint entry, uint outOf, byte[] payload, uint? dwSize = null, int? received = null)
    {
        var full = new byte[TrafficMessageParser.DataHeaderBytes + payload.Length];
        void U(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(offset), value);
        U(0, dwSize ?? (uint)full.Length);
        U(4, 6);
        U(8, TrafficMessageParser.RecvIdSimObjectDataByType);
        U(12, requestId);
        U(16, objectId);
        U(20, defineId);
        U(24, 0);
        U(28, entry);
        U(32, outOf);
        U(36, 1);
        payload.CopyTo(full, TrafficMessageParser.DataHeaderBytes);
        return received is { } r ? full[..Math.Min(r, full.Length)] : full;
    }

    private static ByTypeMessage Parse(byte[] received) => Assert.IsType<ByTypeMessage>(TrafficMessageParser.ParseByType(received, Layouts));

    // ------------------------------------------------------------------ Parser

    [Fact]
    public void Numerische_Antwort_des_Hintergrundscans()
    {
        var m = Parse(ByType(7, 42, NumericDefine, 2, 3, Payload()));

        Assert.True(m.HeaderComplete);
        Assert.Equal(7u, m.RequestId);
        Assert.Equal(42u, m.ObjectId);
        Assert.Equal(2u, m.EntryNumber);
        Assert.Equal(3u, m.OutOf);
        Assert.Equal(72, m.PayloadBytes);
        Assert.Equal(72, m.ExpectedPayloadBytes);
        Assert.False(m.IsShort);
        Assert.False(m.IsEmptyReply);
        Assert.False(m.SizeMismatch);

        var r = Assert.IsType<TrafficRecord>(m.Record);
        Assert.Equal(46.21, r.Latitude, 9);
        Assert.Equal(-87.86, r.Longitude, 9);
        Assert.Equal(31_000, r.AltitudeFt, 9);
        Assert.Equal(450, r.GroundSpeedKt, 9);
        Assert.Equal(-800, r.VerticalSpeedFpm, 9);
        Assert.False(r.OnGround);
        Assert.False(r.IsUser);
        Assert.Null(r.AtcId);
        Assert.Null(r.AtcModel);
    }

    [Fact]
    public void Antwort_mit_Texten_fuer_get_traffic()
    {
        var payload = Payload(texts: ["N605SD", null, "1432126", "ATCCOM.AC MODEL C441.0.tts"]);
        var m = Parse(ByType(8, 99, TextDefine, 1, 1, payload));

        Assert.Equal(328, m.ExpectedPayloadBytes);
        Assert.Equal(328, m.PayloadBytes);
        var r = Assert.IsType<TrafficRecord>(m.Record);
        Assert.Equal("N605SD", r.AtcId);
        Assert.Null(r.AtcAirline);
        Assert.Equal("1432126", r.AtcFlightNumber);
        Assert.Equal("ATCCOM.AC MODEL C441.0.tts", r.AtcModel);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(44)]
    public void Leere_Antwort_ohne_Datenblock_wird_nicht_ueberlesen(int dwSize)
    {
        // Hypothese 2: „kein Objekt im Umkreis“ (z. B. jede Hubschrauber-Abfrage) ohne Datenblock
        var message = ByType(9, 0, TextDefine, 0, 0, new byte[dwSize - TrafficMessageParser.DataHeaderBytes]);
        Assert.Equal(dwSize, message.Length);

        var m = Parse(message);
        Assert.True(m.IsEmptyReply);
        Assert.False(m.IsShort);
        Assert.Null(m.Record);
        Assert.Equal(dwSize - 40, m.PayloadBytes);
    }

    [Fact]
    public void Kurzer_Datenblock_liefert_nur_vollstaendige_Felder()
    {
        // Hypothese 1: Live-Traffic liefert weniger als die Data Definition – Zahlen + ATC ID + 10 Byte vom nächsten Text
        var payload = Payload(texts: ["DLH400", "Lufthansa", "400", "B748"])[..(72 + 64 + 10)];
        var m = Parse(ByType(10, 55, TextDefine, 1, 1, payload));

        Assert.True(m.IsShort);
        Assert.Equal(146, m.PayloadBytes);
        var r = Assert.IsType<TrafficRecord>(m.Record);
        Assert.Equal("DLH400", r.AtcId);
        Assert.Null(r.AtcAirline);
        Assert.Null(r.AtcFlightNumber);
        Assert.Null(r.AtcModel);
    }

    [Fact]
    public void Ohne_alle_Zahlen_gibt_es_keinen_Datensatz()
    {
        var m = Parse(ByType(11, 56, NumericDefine, 1, 1, Payload()[..50]));

        Assert.True(m.IsShort);
        Assert.Null(m.Record);
    }

    [Fact]
    public void Kleineres_pcbData_begrenzt_das_Lesen()
    {
        // dwSize behauptet 368 Byte, SimConnect liefert aber nur 120
        var message = ByType(12, 57, TextDefine, 1, 1, Payload(texts: ["A", "B", "C", "D"]), received: 120);
        var m = Parse(message);

        Assert.Equal(368u, m.DwSize);
        Assert.Equal(120, m.ReceivedBytes);
        Assert.True(m.SizeMismatch);
        Assert.Equal(80, m.PayloadBytes);
        Assert.NotNull(m.Record);
        Assert.Null(m.Record!.AtcId);
    }

    [Fact]
    public void Kleineres_dwSize_begrenzt_das_Lesen()
    {
        var full = Payload(texts: ["N12345", "Delta", "363", "B757"]);
        var m = Parse(ByType(13, 58, TextDefine, 1, 1, full, dwSize: (uint)(40 + 72)));

        Assert.True(m.SizeMismatch);
        Assert.Equal(72, m.PayloadBytes);
        Assert.True(m.IsShort);
        Assert.Null(m.Record!.AtcId);
    }

    [Fact]
    public void Kopf_unvollstaendig()
    {
        var m = Parse(ByType(14, 59, TextDefine, 1, 1, [], dwSize: 20, received: 20));

        Assert.False(m.HeaderComplete);
        Assert.Equal(0u, m.RequestId);
        Assert.Null(m.Record);
    }

    [Fact]
    public void Unbekannte_Definition_wird_nicht_gelesen()
    {
        var m = Parse(ByType(15, 60, 77, 1, 1, Payload()));

        Assert.Equal(0, m.ExpectedPayloadBytes);
        Assert.Null(m.Record);
        Assert.False(m.IsShort);
    }

    [Fact]
    public void Text_ohne_Nullbyte_fuellt_das_ganze_Feld()
    {
        var text = new string('X', 64);
        var m = Parse(ByType(16, 61, TextDefine, 1, 1, Payload(texts: [text, "  Condor ", null, null])));

        Assert.Equal(text, m.Record!.AtcId);
        Assert.Equal("Condor", m.Record.AtcAirline);
    }

    [Fact]
    public void Andere_Nachrichten_und_Exceptions()
    {
        var open = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(open, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(open.AsSpan(8), TrafficMessageParser.RecvIdOpen);
        Assert.Equal(TrafficMessageParser.RecvIdOpen, TrafficMessageParser.MessageId(open));
        Assert.Null(TrafficMessageParser.ParseByType(open, Layouts));
        Assert.Null(TrafficMessageParser.MessageId(new byte[8]));

        var exception = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(exception, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(exception.AsSpan(8), TrafficMessageParser.RecvIdException);
        BinaryPrimitives.WriteUInt32LittleEndian(exception.AsSpan(12), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(exception.AsSpan(16), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(exception.AsSpan(20), 2);
        Assert.Equal((7u, 5u, 2u), TrafficMessageParser.ParseException(exception));
        Assert.Null(TrafficMessageParser.ParseException(exception[..20]));
    }

    // ------------------------------------------------------------------ Sammeln

    [Fact]
    public void Abfrage_ist_fertig_nach_dem_letzten_Eintrag_und_der_leeren_Hubschrauber_Antwort()
    {
        var collector = new ByTypeCollector();
        collector.Expect(1, "aircraft");
        collector.Expect(2, "helicopter");

        Assert.True(collector.Add(Parse(ByType(1, 1, NumericDefine, 1, 3, Payload(user: true)))));
        Assert.True(collector.Add(Parse(ByType(1, 20, NumericDefine, 2, 3, Payload()))));
        Assert.False(collector.IsDone(1));

        // Verspätete Antwort einer früheren Abfrage zählt nicht
        Assert.False(collector.Add(Parse(ByType(99, 21, NumericDefine, 1, 1, Payload()))));

        // Kurzer Block: zählt als empfangen, liefert aber kein Ziel
        Assert.True(collector.Add(Parse(ByType(1, 22, NumericDefine, 3, 3, Payload()[..40]))));
        Assert.True(collector.IsDone(1));

        Assert.True(collector.Add(Parse(ByType(2, 0, NumericDefine, 0, 0, []))));
        Assert.True(collector.IsDone(2));

        var samples = collector.Samples();
        Assert.Equal(new uint[] { 1, 20 }, samples.Select(s => s.ObjectId));
        Assert.True(samples[0].IsUser);
        Assert.All(samples, s => Assert.Equal("aircraft", s.Category));
    }

    // ------------------------------------------------------------------ Zähler und Urteil zu den Hypothesen

    [Fact]
    public void Ohne_Nachrichten_kein_Urteil()
    {
        var d = new TrafficMessageStats().Snapshot();

        Assert.Equal(0, d.Messages);
        Assert.StartsWith("no empty reply", d.EmptyReplyVerdict);
        Assert.StartsWith("no data message", d.ShortBlockVerdict);
        Assert.Equal(368, d.LegacyDwSize);
    }

    [Fact]
    public void Leere_Antworten_kuerzer_als_die_alte_Struktur_bestaetigen_Hypothese_2()
    {
        var stats = new TrafficMessageStats();
        stats.Record(Parse(ByType(2, 0, NumericDefine, 0, 0, [])), T0);
        stats.Record(Parse(ByType(4, 0, NumericDefine, 0, 0, new byte[4])), T0.AddSeconds(3));
        stats.Record(Parse(ByType(1, 1, NumericDefine, 1, 1, Payload(user: true))), T0.AddSeconds(3));

        var d = stats.Snapshot();
        Assert.Equal(3, d.Messages);
        Assert.Equal(2, d.EmptyReplies);
        Assert.Equal(40u, d.EmptyMinDwSize);
        Assert.Equal(44u, d.EmptyMaxDwSize);
        Assert.Equal(T0, d.SinceUtc);
        Assert.Equal("confirmed: 2 empty replies with dwSize 40–44 bytes instead of 368 – up to 0.9 Kopilot read past the end", d.EmptyReplyVerdict);
        Assert.Equal("not observed so far (1 data messages, dwSize 112 bytes)", d.ShortBlockVerdict);
    }

    [Fact]
    public void Kurze_Datenbloecke_bestaetigen_Hypothese_1()
    {
        var stats = new TrafficMessageStats();
        stats.Record(Parse(ByType(8, 30, TextDefine, 1, 2, Payload(texts: ["A", "B", "C", "D"]))), T0);
        stats.Record(Parse(ByType(8, 31, TextDefine, 2, 2, Payload(texts: ["A", "B", "C", "D"])[..200])), T0);
        stats.Record(Parse(ByType(9, 0, TextDefine, 0, 0, new byte[328])), T0);

        var d = stats.Snapshot();
        Assert.Equal(1, d.ShortMessages);
        Assert.Equal(200, d.ShortMinPayloadBytes);
        Assert.Equal("confirmed: 1 of 2 data messages too short (smallest data block 200 bytes)", d.ShortBlockVerdict);
        Assert.StartsWith("refuted", d.EmptyReplyVerdict);
    }

    // ------------------------------------------------------------------ Nachrichtenlog

    [Fact]
    public void Nachrichtenlog_schreibt_eine_Zeile_je_Nachricht()
    {
        var empty = Parse(ByType(2, 0, NumericDefine, 0, 0, []));
        Assert.Equal("2026-10-30T00:35:00.000Z,2,current,1,40,40,0,0,0,0,72,empty", TrafficMessageLog.Line(T0, "current", empty));

        var mismatch = Parse(ByType(12, 57, TextDefine, 1, 1, Payload(texts: ["A", "B", "C", "D"]), received: 120));
        Assert.Equal("short+mismatch", TrafficMessageLog.Flags(mismatch));

        var directory = Path.Combine(Path.GetTempPath(), "kopilot-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new TrafficMessageLog(directory))
            {
                log.Write(T0, "current", empty);
                log.Write(T0, "stale", mismatch);
            }

            var lines = File.ReadAllLines(Path.Combine(directory, "traffic-messages.csv"));
            Assert.Equal(3, lines.Length);
            Assert.Equal(TrafficMessageLog.Header, lines[0]);
            Assert.EndsWith(",empty", lines[1]);
            Assert.Contains(",stale,", lines[2]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
