using Kopilot.FlightPlan;
using Kopilot.Sim;

namespace Kopilot.Traffic;

/// <summary>Bezugspunkt für die Verkehrsrechnung: das eigene Flugzeug.</summary>
/// <param name="AltitudeFt">Wahre Höhe über MSL (PLANE ALTITUDE). Nur für die Erkennung des eigenen Objekts in der Abfrage.</param>
/// <param name="PressureAltitudeFt">
/// Eigene Druckhöhe (PRESSURE ALTITUDE, Bezug 1013,25 hPa). Ist sie gesetzt, zählt sie für die Höhendifferenz:
/// MSFS-Live-Traffic liefert als PLANE ALTITUDE seine barometrische Höhe aus ADS-B (Flugfläche), nicht die wahre Höhe.
/// </param>
public sealed record OwnAircraft(double Latitude, double Longitude, double AltitudeFt, double HeadingTrueDeg, bool OnGround, double? PressureAltitudeFt = null)
{
    public bool UsesPressureAltitude => PressureAltitudeFt is { } pa && double.IsFinite(pa);

    /// <summary>Bezugshöhe für die Höhendifferenz: Druckhöhe, ohne sie die wahre Höhe.</summary>
    public double ReferenceAltitudeFt => UsesPressureAltitude ? PressureAltitudeFt!.Value : AltitudeFt;

    /// <summary>"pressure" oder "true" – welche eigene Höhe gegen die Ziele gerechnet wird.</summary>
    public string AltitudeReference => UsesPressureAltitude ? "pressure" : "true";
}

/// <summary>Ein Verkehrsziel relativ zum eigenen Flugzeug.</summary>
/// <param name="Ident">Callsign, sonst ATC-ID.</param>
/// <param name="Callsign">Airline + Flugnummer (KI-Airliner), sonst null.</param>
/// <param name="Type">ATC MODEL in lesbarer Form, z. B. "A320".</param>
/// <param name="DistanceNm">Horizontale Entfernung (Großkreis).</param>
/// <param name="RelativeBearingDeg">Peilung relativ zum eigenen Steuerkurs, 0–360.</param>
/// <param name="Clock">Uhrzeit-Angabe 1–12.</param>
/// <param name="AltitudeFt">Höhe des Ziels, wie SimConnect sie liefert (PLANE ALTITUDE).</param>
/// <param name="AltitudeDiffFt">Höhe des Ziels minus eigene Bezugshöhe (<see cref="OwnAircraft.ReferenceAltitudeFt"/>), positiv = darüber.</param>
/// <param name="VerticalSpeedDerived">V/S aus der Höhenänderung zwischen zwei Abfragen statt aus der SimVar.</param>
/// <param name="GroundSpeedKt">Groundspeed: aus der Positionsänderung, sonst die SimVar (siehe <see cref="GroundSpeedSource"/>).</param>
/// <param name="GroundSpeedSource">"position" (aus der Positionsänderung) oder "simvar" (GROUND VELOCITY, bei Live-Traffic unzuverlässig).</param>
/// <param name="GroundSpeedSimVarKt">Rohwert von GROUND VELOCITY – zum Vergleich.</param>
/// <param name="GroundSpeedBaselineSec">Zeitabstand der Positionen, aus denen die Groundspeed gerechnet wurde; null bei "simvar".</param>
public sealed record TrafficTarget(
    uint ObjectId,
    string Category,
    string? Ident,
    string? Callsign,
    string? AtcId,
    string? Type,
    double DistanceNm,
    double BearingTrueDeg,
    double RelativeBearingDeg,
    int Clock,
    double AltitudeFt,
    double AltitudeDiffFt,
    double? VerticalSpeedFpm,
    bool VerticalSpeedDerived,
    string VerticalTrend,
    double GroundSpeedKt,
    string GroundSpeedSource,
    double GroundSpeedSimVarKt,
    double? GroundSpeedBaselineSec,
    double HeadingTrueDeg,
    double? HeadingMagDeg,
    bool OnGround,
    string Callout)
{
    public string ClockText => TrafficMath.ClockText(Clock);

    public const string GroundSpeedFromPosition = "position";

    public const string GroundSpeedFromSimVar = "simvar";
}

/// <summary>Verkehrslage: eigenes Flugzeug und alle Ziele im Radius, nach Entfernung sortiert.</summary>
/// <param name="OthersReceived">Fremde Flugzeuge in der Antwort von MSFS, auch außerhalb des Radius.</param>
public sealed record TrafficPicture(DateTimeOffset TimeUtc, OwnAircraft? Own, IReadOnlyList<TrafficTarget> Targets, int OthersReceived = 0)
{
    public static TrafficPicture Empty(DateTimeOffset timeUtc) => new(timeUtc, null, []);
}

/// <summary>Rechnet die Rohdaten der Verkehrsabfrage in Ziele relativ zum eigenen Flugzeug um. Rein rechnend, testbar.</summary>
public static class TrafficPictureBuilder
{
    /// <summary>So nah (und so wenig höher/tiefer) liegt nur das eigene Flugzeug – Rückfall, falls IS USER SIM fehlt.</summary>
    private const double SamePositionNm = 0.03;

    private const double SameAltitudeFt = 150;

    /// <param name="samples">Alle Objekte der Abfrage, das eigene Flugzeug eingeschlossen.</param>
    /// <param name="fallbackOwn">Eigene Werte aus den abonnierten SimVars, falls das eigene Flugzeug nicht in der Abfrage steckt.</param>
    /// <param name="radiusNm">Nur Ziele bis zu dieser Entfernung.</param>
    /// <param name="derivedVerticalSpeedFpm">V/S je Objekt-ID aus der Höhenänderung (für Ziele, deren SimVar 0 liefert).</param>
    /// <param name="derivedGroundSpeed">Groundspeed je Objekt-ID aus der Positionsänderung (<see cref="GroundSpeedTracker"/>).</param>
    /// <param name="ownPressureAltitudeFt">
    /// Eigene Druckhöhe. Gesetzt = Höhendifferenz gegen die Druckhöhe; null = gegen die wahre Höhe
    /// (bzw. die Druckhöhe aus <paramref name="fallbackOwn"/>, falls dort eine steht).
    /// </param>
    public static TrafficPicture Build(
        DateTimeOffset timeUtc,
        IReadOnlyList<AircraftSample> samples,
        OwnAircraft? fallbackOwn,
        double radiusNm,
        IReadOnlyDictionary<uint, double>? derivedVerticalSpeedFpm = null,
        IReadOnlyDictionary<uint, DerivedGroundSpeed>? derivedGroundSpeed = null,
        double? ownPressureAltitudeFt = null)
    {
        var user = samples.FirstOrDefault(s => s.IsUser && IsValidPosition(s));
        var pressureAltitude = ownPressureAltitudeFt ?? fallbackOwn?.PressureAltitudeFt;
        var own = user is not null
            ? new OwnAircraft(user.Latitude, user.Longitude, user.AltitudeFt, user.HeadingTrueDeg, user.OnGround, pressureAltitude)
            : fallbackOwn is not null ? fallbackOwn with { PressureAltitudeFt = pressureAltitude } : null;
        if (own is null)
        {
            return TrafficPicture.Empty(timeUtc);
        }

        var targets = new List<TrafficTarget>();
        var others = 0;
        foreach (var s in samples)
        {
            if (s.IsUser || !IsValidPosition(s))
            {
                continue;
            }

            var distance = RouteMath.DistanceNm(own.Latitude, own.Longitude, s.Latitude, s.Longitude);

            // Ohne IS USER SIM: das Objekt an der eigenen Position ist das eigene Flugzeug
            if (user is null && distance < SamePositionNm && Math.Abs(s.AltitudeFt - own.AltitudeFt) < SameAltitudeFt)
            {
                continue;
            }

            others++;
            if (distance > radiusNm)
            {
                continue;
            }

            targets.Add(ToTarget(own, s, distance, derivedVerticalSpeedFpm, derivedGroundSpeed));
        }

        targets.Sort((a, b) => a.DistanceNm.CompareTo(b.DistanceNm));
        return new TrafficPicture(timeUtc, own, targets, others);
    }

    public static TrafficTarget ToTarget(
        OwnAircraft own,
        AircraftSample s,
        double distanceNm,
        IReadOnlyDictionary<uint, double>? derivedVerticalSpeedFpm = null,
        IReadOnlyDictionary<uint, DerivedGroundSpeed>? derivedGroundSpeed = null)
    {
        var bearing = RouteMath.BearingDeg(own.Latitude, own.Longitude, s.Latitude, s.Longitude);
        var relative = TrafficMath.RelativeBearingDeg(bearing, own.HeadingTrueDeg);
        var clock = TrafficMath.ClockPosition(relative);
        var altitudeDiff = TrafficMath.AltitudeDifference(s.AltitudeFt, own);

        // GROUND VELOCITY ist bei MSFS-Live-Traffic um etwa Faktor 3,3 zu klein – die Positionsänderung hat Vorrang
        var groundSpeed = s.GroundSpeedKt;
        var groundSpeedSource = TrafficTarget.GroundSpeedFromSimVar;
        double? baseline = null;
        if (derivedGroundSpeed?.TryGetValue(s.ObjectId, out var g) == true)
        {
            groundSpeed = g.Knots;
            groundSpeedSource = TrafficTarget.GroundSpeedFromPosition;
            baseline = g.BaselineSec;
        }

        // Manche Ziele (v. a. Multiplayer) liefern V/S = 0 – dann zählt die Höhenänderung zwischen zwei Abfragen
        double? vs = double.IsFinite(s.VerticalSpeedFpm) ? s.VerticalSpeedFpm : null;
        var derived = false;
        if (!s.OnGround && vs is null or (> -1 and < 1) && derivedVerticalSpeedFpm?.TryGetValue(s.ObjectId, out var d) == true)
        {
            vs = d;
            derived = true;
        }

        var trend = TrafficMath.VerticalTrend(vs, s.OnGround);
        var airline = TrafficMath.CleanAtcText(s.AtcAirline);
        var callsign = airline is not null || s.AtcFlightNumber is not null ? $"{airline} {s.AtcFlightNumber}".Trim() : null;
        return new TrafficTarget(
            s.ObjectId,
            s.Category,
            callsign ?? s.AtcId,
            callsign,
            s.AtcId,
            TrafficMath.CleanAtcText(s.AtcModel),
            distanceNm,
            bearing,
            relative,
            clock,
            s.AltitudeFt,
            altitudeDiff,
            vs,
            derived,
            trend,
            groundSpeed,
            groundSpeedSource,
            s.GroundSpeedKt,
            baseline,
            TrafficMath.Normalize(s.HeadingTrueDeg),
            double.IsFinite(s.HeadingMagDeg) ? TrafficMath.Normalize(s.HeadingMagDeg) : null,
            s.OnGround,
            TrafficMath.Callout(clock, distanceNm, altitudeDiff, trend, s.OnGround));
    }

    private static bool IsValidPosition(AircraftSample s) =>
        double.IsFinite(s.Latitude) && double.IsFinite(s.Longitude) && double.IsFinite(s.AltitudeFt)
        && Math.Abs(s.Latitude) <= 90 && Math.Abs(s.Longitude) <= 180;
}
