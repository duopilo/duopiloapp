using System.Globalization;
using Duopilo.FlightPlan;
using Duopilo.Speech;
using Duopilo.Traffic;

namespace Duopilo.Atc;

/// <summary>
/// Verkehrslage für den Duopilo-ATC: eigener Steuerkurs und die Ziele der Verkehrsabfrage (<see cref="TrafficService"/>), vom
/// <see cref="AtcService"/> etwa alle 10 s erneuert. Daraus kommen Traffic-Infos, Antworten auf Rückfragen des Piloten und
/// die Reihenfolge im Endanflug.
/// </summary>
/// <param name="OwnHeadingTrueDeg">Eigener rechtweisender Steuerkurs – Bezug für Uhrzeit-Angabe, Richtung und Annäherung.</param>
public sealed record AtcTrafficPicture(double OwnHeadingTrueDeg, IReadOnlyList<AtcTrafficTarget> Targets)
{
    /// <summary>Aus der Verkehrslage einer Abfrage; null ohne eigenes Flugzeug.</summary>
    public static AtcTrafficPicture? From(TrafficPicture picture)
    {
        if (picture.Own is not { } own)
        {
            return null;
        }

        var targets = new List<AtcTrafficTarget>(picture.Targets.Count);
        foreach (var t in picture.Targets)
        {
            var (lat, lon) = RouteMath.Destination(own.Latitude, own.Longitude, t.BearingTrueDeg, t.DistanceNm);
            targets.Add(new AtcTrafficTarget
            {
                ObjectId = t.ObjectId,
                Callsign = t.Callsign,
                Type = t.Type,
                DistanceNm = t.DistanceNm,
                BearingTrueDeg = t.BearingTrueDeg,
                RelativeBearingDeg = t.RelativeBearingDeg,
                Clock = t.Clock,
                AltitudeFt = t.AltitudeFt,
                AltitudeDiffFt = t.AltitudeDiffFt,
                VerticalTrend = t.VerticalTrend,

                // GROUND VELOCITY ist bei MSFS-Live-Traffic viel zu klein – nur die Groundspeed aus der Positionsänderung zählt
                GroundSpeedKt = t.GroundSpeedSource == TrafficTarget.GroundSpeedFromPosition ? t.GroundSpeedKt : null,
                HeadingTrueDeg = t.HeadingTrueDeg,
                OnGround = t.OnGround,
                Lat = lat,
                Lon = lon,
            });
        }

        return new AtcTrafficPicture(own.HeadingTrueDeg, targets);
    }
}

/// <summary>Ein Verkehrsziel aus Sicht des eigenen Flugzeugs (wie <see cref="TrafficTarget"/>, dazu die Position).</summary>
public sealed record AtcTrafficTarget
{
    public uint ObjectId { get; init; }

    public string? Callsign { get; init; }

    /// <summary>Flugzeugtyp, z. B. "A320" (ATC MODEL), null = unbekannt.</summary>
    public string? Type { get; init; }

    public double DistanceNm { get; init; }

    public double BearingTrueDeg { get; init; }

    /// <summary>Peilung relativ zum eigenen Steuerkurs, 0–360.</summary>
    public double RelativeBearingDeg { get; init; }

    /// <summary>Uhrzeit-Angabe 1–12.</summary>
    public int Clock { get; init; }

    public double AltitudeFt { get; init; }

    /// <summary>Höhe des Ziels minus eigene Höhe (Druckhöhe wie beim TCAS), positiv = darüber.</summary>
    public double AltitudeDiffFt { get; init; }

    /// <summary>"climbing", "descending" oder "level".</summary>
    public string VerticalTrend { get; init; } = "level";

    /// <summary>Groundspeed aus der Positionsänderung; null, solange es nur die unzuverlässige SimVar gibt.</summary>
    public double? GroundSpeedKt { get; init; }

    public double HeadingTrueDeg { get; init; }

    public bool OnGround { get; init; }

    public double Lat { get; init; }

    public double Lon { get; init; }
}

/// <summary>Geringster Abstand zweier Flugzeuge bei gleichbleibendem Kurs und Tempo.</summary>
/// <param name="TimeSec">Zeit bis dahin (0, wenn sie sich nicht mehr nähern).</param>
/// <param name="MissNm">Horizontaler Abstand an diesem Punkt.</param>
/// <param name="Closing">Die beiden nähern sich noch an.</param>
public readonly record struct AtcClosestApproach(double TimeSec, double MissNm, bool Closing);

/// <summary>
/// Rückfrage des Piloten nach Verkehr: "request traffic information", "any traffic for us?", "confirm traffic 12 o'clock",
/// "we have traffic above us on TCAS". Keine Rückfrage sind Quittungen ("traffic in sight", "negative contact", "looking out")
/// und "due traffic".
/// </summary>
/// <param name="Clock">Genannte Uhrzeit-Angabe 1–12 ("ahead" = 12), null ohne.</param>
/// <param name="Vertical">+1 "above", −1 "below", 0 ohne Angabe.</param>
public sealed record AtcTrafficQuery(int? Clock, int Vertical)
{
    private static readonly string[] AcknowledgeWords = ["sight", "insight", "contact", "looking", "visual", "negative"];

    public static AtcTrafficQuery? Parse(HeardText heard)
    {
        var t = heard.Tokens;
        var index = -1;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].IsWord("traffic") && (i == 0 || !t[i - 1].IsWord("due")))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || t.Any(x => x.IsWord(AcknowledgeWords)))
        {
            return null;
        }

        int? clock = null;
        for (var i = 0; i + 1 < t.Count; i++)
        {
            if (t[i].Kind != HeardKind.Number || t[i].Value is < 1.0 or > 12.0 || t[i].Value != Math.Floor(t[i].Value))
            {
                continue;
            }

            // "2 o'clock" → 2, Buchstabe O, "clock"
            var next = t[i + 1];
            if (next.IsWord("clock") || next.IsWord("oclock")
                || (next.Kind == HeardKind.Letter && next.Text == "O" && i + 2 < t.Count && t[i + 2].IsWord("clock")))
            {
                clock = (int)t[i].Value;
                break;
            }
        }

        if (clock is null && t.Any(x => x.IsWord("ahead")))
        {
            clock = 12;
        }

        var vertical = t.Any(x => x.IsWord("above")) ? 1 : t.Any(x => x.IsWord("below")) ? -1 : 0;
        return new AtcTrafficQuery(clock, vertical);
    }
}

/// <summary>Rechnungen und Werte für Traffic-Infos im Funk (Phraseologie nach ICAO: Uhrzeit, Entfernung, Richtung, Typ, Höhe).</summary>
public static class AtcTrafficMath
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Geringster Abstand bei gleichbleibendem Kurs und Tempo (ebene Näherung, für Entfernungen bis etwa 30 nm genau genug).
    /// Null, wenn eine der beiden Groundspeeds fehlt.
    /// </summary>
    public static AtcClosestApproach? ClosestApproach(AtcTrafficPicture picture, AtcTrafficTarget target, double? ownGroundSpeedKt)
    {
        if (target.GroundSpeedKt is not { } targetGs || ownGroundSpeedKt is not { } ownGs || !double.IsFinite(targetGs) || !double.IsFinite(ownGs))
        {
            return null;
        }

        // Ziel relativ zum eigenen Flugzeug (x = Ost, y = Nord, nm) und Relativgeschwindigkeit (kt)
        var bearing = Rad(target.BearingTrueDeg);
        var px = target.DistanceNm * Math.Sin(bearing);
        var py = target.DistanceNm * Math.Cos(bearing);
        var own = Rad(picture.OwnHeadingTrueDeg);
        var other = Rad(target.HeadingTrueDeg);
        var vx = (targetGs * Math.Sin(other)) - (ownGs * Math.Sin(own));
        var vy = (targetGs * Math.Cos(other)) - (ownGs * Math.Cos(own));
        var vv = (vx * vx) + (vy * vy);
        if (vv < 1)
        {
            return new AtcClosestApproach(0, target.DistanceNm, false);
        }

        var hours = -((px * vx) + (py * vy)) / vv;
        if (hours <= 0)
        {
            return new AtcClosestApproach(0, target.DistanceNm, false);
        }

        var mx = px + (vx * hours);
        var my = py + (vy * hours);
        return new AtcClosestApproach(hours * 3600, Math.Sqrt((mx * mx) + (my * my)), true);
    }

    /// <summary>Flugrichtung des Ziels aus eigener Sicht: "same direction", "opposite direction", "crossing left to right" …</summary>
    public static string Direction(double ownHeadingTrueDeg, double targetHeadingTrueDeg)
    {
        var relative = TrafficMath.Normalize(targetHeadingTrueDeg - ownHeadingTrueDeg);
        if (relative <= 30 || relative >= 330)
        {
            return "same direction";
        }

        if (relative is >= 150.0 and <= 210.0)
        {
            return "opposite direction";
        }

        return relative < 180 ? "crossing left to right" : "crossing right to left";
    }

    /// <summary>Kleinster Winkel zwischen zwei Richtungen, 0–180°.</summary>
    public static double AngleDiff(double a, double b)
    {
        var d = TrafficMath.Normalize(a - b);
        return d > 180 ? 360 - d : d;
    }

    /// <summary>Abstand zweier Uhrzeit-Angaben in Stunden (12 und 1 liegen eine Stunde auseinander).</summary>
    public static int ClockDistance(int a, int b)
    {
        var d = Math.Abs(a - b) % 12;
        return Math.Min(d, 12 - d);
    }

    /// <summary>
    /// Ziel zur Rückfrage des Piloten: in der Luft, im Umkreis und Höhenband, passend zu genannter Uhrzeit (± 1 Stunde) und
    /// "above"/"below" – das nächste.
    /// </summary>
    public static AtcTrafficTarget? Match(AtcTrafficPicture picture, AtcTrafficQuery query, double rangeNm, double altitudeFt) =>
        picture.Targets
            .Where(t => !t.OnGround && t.DistanceNm <= rangeNm && Math.Abs(t.AltitudeDiffFt) <= altitudeFt)
            .Where(t => query.Clock is not { } clock || ClockDistance(t.Clock, clock) <= 1)
            .Where(t => query.Vertical == 0 || Math.Sign(Math.Round(t.AltitudeDiffFt / 100)) == query.Vertical)
            .OrderBy(t => t.DistanceNm)
            .FirstOrDefault();

    // ------------------------------------------------------------------ Werte für die Vorlagen

    /// <summary>"2 o'clock", gesprochen "two o'clock".</summary>
    public static AtcValue ClockValue(int clock) =>
        AtcValue.Text(TrafficMath.ClockText(clock), Pronunciation.Natural(clock) + " o'clock");

    /// <summary>"6 miles", "1 mile", "less than 1 mile" – gesprochen natürlich ("six miles").</summary>
    public static AtcValue DistanceValue(double distanceNm)
    {
        if (distanceNm < 0.95)
        {
            return AtcValue.Text("less than 1 mile", "less than one mile");
        }

        var miles = (int)Math.Round(distanceNm, MidpointRounding.AwayFromZero);
        return miles == 1
            ? AtcValue.Text("1 mile", "one mile")
            : AtcValue.Text(miles.ToString(Inv) + " miles", Pronunciation.Natural(miles) + " miles");
    }

    public static AtcValue DirectionValue(double ownHeadingTrueDeg, double targetHeadingTrueDeg)
    {
        var direction = Direction(ownHeadingTrueDeg, targetHeadingTrueDeg);
        return AtcValue.Text(direction, direction);
    }

    /// <summary>Höhenunterschied auf 100 ft: "1000 feet above" ("one thousand feet above"), "same level".</summary>
    public static AtcValue LevelValue(double altitudeDiffFt)
    {
        var rounded = (int)(Math.Round(altitudeDiffFt / 100, MidpointRounding.AwayFromZero) * 100);
        if (rounded == 0)
        {
            return AtcValue.Text("same level", "same level");
        }

        var abs = Math.Abs(rounded);
        var where = rounded > 0 ? "above" : "below";
        return AtcValue.Text($"{abs.ToString(Inv)} feet {where}", $"{Pronunciation.Feet(abs)} {where}");
    }

    /// <summary>"climbing" oder "descending"; null bei horizontalem Flug.</summary>
    public static AtcValue? TrendValue(string? verticalTrend) =>
        verticalTrend is "climbing" or "descending" ? AtcValue.Text(verticalTrend, verticalTrend) : null;

    /// <summary>Flugzeugtyp, angezeigt wie gemeldet ("A320"), gesprochen wie im Funk ("Airbus three twenty"); null = unbekannt.</summary>
    public static AtcValue? TypeValue(string? type) =>
        string.IsNullOrWhiteSpace(type) ? null : AtcValue.Text(type.Trim(), TypeSpoken(type));

    /// <summary>
    /// Typ für die Sprachausgabe: Airbus und Boeing wie im Funk ("A320" → "Airbus three twenty", "B738" → "Boeing seven thirty
    /// eight", "A20N" → "Airbus three twenty neo"), andere ICAO-Kennungen im NATO-Alphabet ("C172" → "charlie one seven two"),
    /// Namen ("CITATION") als Wort.
    /// </summary>
    public static string TypeSpoken(string type)
    {
        var code = Pronunciation.Compact(type);
        switch (code)
        {
            case "A19N":
                return "Airbus three nineteen neo";
            case "A20N":
                return "Airbus three twenty neo";
            case "A21N":
                return "Airbus three twenty one neo";
        }

        if (code.Length == 4 && char.IsAsciiDigit(code[1]) && char.IsAsciiDigit(code[2]) && char.IsAsciiDigit(code[3])
            && ((code[0] == 'A' && code[1] == '3') || (code[0] == 'B' && code[1] == '7')))
        {
            var series = code[0] == 'A' ? "Airbus " : "Boeing ";
            var n = ((code[2] - '0') * 10) + (code[3] - '0');
            var rest = n == 0 ? "hundred" : n < 10 ? "oh " + Pronunciation.Natural(n) : Pronunciation.Natural(n);
            return series + Pronunciation.Digit(code[1]) + " " + rest;
        }

        var trimmed = type.Trim();
        if (code.Length == 0 || trimmed.Contains(' ', StringComparison.Ordinal) || code.Length > 6)
        {
            return AtcSpeech.Literal(trimmed);
        }

        return code.Length >= 5 && code.All(char.IsAsciiLetter) ? Pronunciation.TitleCase(code) : Pronunciation.Spell(code);
    }

    /// <summary>Entfernung des Vorausfliegenden zum Platz: "4 miles" ("four miles"), unter 2 nm "short".</summary>
    public static AtcValue FinalValue(double distanceToAirportNm)
    {
        var miles = (int)Math.Round(distanceToAirportNm, MidpointRounding.AwayFromZero);
        return miles < 2 ? AtcValue.Text("short", "short") : AtcValue.Text(miles.ToString(Inv) + " miles", Pronunciation.Natural(miles) + " miles");
    }

    /// <summary>Platz in der Reihenfolge: "2", gesprochen "two".</summary>
    public static AtcValue SequenceValue(int number) => AtcValue.Text(number.ToString(Inv), Pronunciation.Natural(number));

    private static double Rad(double deg) => deg * Math.PI / 180.0;
}
