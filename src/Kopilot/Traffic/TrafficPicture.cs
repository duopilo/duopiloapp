using Kopilot.FlightPlan;
using Kopilot.Sim;

namespace Kopilot.Traffic;

/// <summary>Bezugspunkt für die Verkehrsrechnung: das eigene Flugzeug.</summary>
/// <param name="AltitudeFt">Wahre Höhe über MSL (PLANE ALTITUDE), wie bei den Zielen.</param>
public sealed record OwnAircraft(double Latitude, double Longitude, double AltitudeFt, double HeadingTrueDeg, bool OnGround);

/// <summary>Ein Verkehrsziel relativ zum eigenen Flugzeug.</summary>
/// <param name="Ident">Callsign, sonst ATC-ID.</param>
/// <param name="Callsign">Airline + Flugnummer (KI-Airliner), sonst null.</param>
/// <param name="Type">ATC MODEL in lesbarer Form, z. B. "A320".</param>
/// <param name="DistanceNm">Horizontale Entfernung (Großkreis).</param>
/// <param name="RelativeBearingDeg">Peilung relativ zum eigenen Steuerkurs, 0–360.</param>
/// <param name="Clock">Uhrzeit-Angabe 1–12.</param>
/// <param name="AltitudeDiffFt">Höhe des Ziels minus eigene Höhe (positiv = darüber).</param>
/// <param name="VerticalSpeedDerived">V/S aus der Höhenänderung zwischen zwei Abfragen statt aus der SimVar.</param>
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
    double HeadingTrueDeg,
    double? HeadingMagDeg,
    bool OnGround,
    string Callout)
{
    public string ClockText => TrafficMath.ClockText(Clock);
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
    public static TrafficPicture Build(
        DateTimeOffset timeUtc,
        IReadOnlyList<AircraftSample> samples,
        OwnAircraft? fallbackOwn,
        double radiusNm,
        IReadOnlyDictionary<uint, double>? derivedVerticalSpeedFpm = null)
    {
        var user = samples.FirstOrDefault(s => s.IsUser && IsValidPosition(s));
        var own = user is not null
            ? new OwnAircraft(user.Latitude, user.Longitude, user.AltitudeFt, user.HeadingTrueDeg, user.OnGround)
            : fallbackOwn;
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

            targets.Add(ToTarget(own, s, distance, derivedVerticalSpeedFpm));
        }

        targets.Sort((a, b) => a.DistanceNm.CompareTo(b.DistanceNm));
        return new TrafficPicture(timeUtc, own, targets, others);
    }

    public static TrafficTarget ToTarget(OwnAircraft own, AircraftSample s, double distanceNm, IReadOnlyDictionary<uint, double>? derivedVerticalSpeedFpm = null)
    {
        var bearing = RouteMath.BearingDeg(own.Latitude, own.Longitude, s.Latitude, s.Longitude);
        var relative = TrafficMath.RelativeBearingDeg(bearing, own.HeadingTrueDeg);
        var clock = TrafficMath.ClockPosition(relative);
        var altitudeDiff = s.AltitudeFt - own.AltitudeFt;

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
            s.GroundSpeedKt,
            TrafficMath.Normalize(s.HeadingTrueDeg),
            double.IsFinite(s.HeadingMagDeg) ? TrafficMath.Normalize(s.HeadingMagDeg) : null,
            s.OnGround,
            TrafficMath.Callout(clock, distanceNm, altitudeDiff, trend, s.OnGround));
    }

    private static bool IsValidPosition(AircraftSample s) =>
        double.IsFinite(s.Latitude) && double.IsFinite(s.Longitude) && double.IsFinite(s.AltitudeFt)
        && Math.Abs(s.Latitude) <= 90 && Math.Abs(s.Longitude) <= 180;
}
