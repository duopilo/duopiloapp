using System.Globalization;
using System.Text.Json;

namespace Kopilot.FlightPlan;

/// <summary>Flugplan (OFP) aus SimBrief. Gewichte und Treibstoff immer in kg.</summary>
public sealed record Ofp
{
    public string? RequestId { get; init; }

    public DateTimeOffset? GeneratedUtc { get; init; }

    /// <summary>Einheit im SimBrief-OFP ("kgs" oder "lbs"). Kopilot rechnet alles in kg um.</summary>
    public string SourceUnits { get; init; } = "kgs";

    public string? Callsign { get; init; }

    public string? FlightNumber { get; init; }

    public string? AircraftIcao { get; init; }

    public string? Registration { get; init; }

    public required OfpAirport Origin { get; init; }

    public required OfpAirport Destination { get; init; }

    public OfpAirport? Alternate { get; init; }

    public string? Route { get; init; }

    public int? InitialAltitudeFt { get; init; }

    /// <summary>SID laut SimBrief (general.sid_ident), falls geliefert.</summary>
    public string? SidIdent { get; init; }

    /// <summary>STAR laut SimBrief (general.star_ident), falls geliefert.</summary>
    public string? StarIdent { get; init; }

    public int? CostIndex { get; init; }

    public int? AirDistanceNm { get; init; }

    public int? AvgWindComponentKt { get; init; }

    public OfpFuel Fuel { get; init; } = new();

    public OfpWeights Weights { get; init; } = new();

    public OfpTimes Times { get; init; } = new();

    public IReadOnlyList<OfpFix> Fixes { get; init; } = [];

    /// <summary>Route als Punktfolge: Startflughafen, alle Navlog-Fixes (der letzte ist der Zielflughafen).</summary>
    public IReadOnlyList<RoutePoint> RoutePoints()
    {
        var points = new List<RoutePoint>();
        if (Origin.Lat is { } olat && Origin.Lon is { } olon)
        {
            points.Add(new RoutePoint(Origin.Icao ?? "ORIGIN", olat, olon, -1));
        }

        for (var i = 0; i < Fixes.Count; i++)
        {
            if (Fixes[i].Lat is { } lat && Fixes[i].Lon is { } lon)
            {
                points.Add(new RoutePoint(Fixes[i].Ident, lat, lon, i));
            }
        }

        return points;
    }
}

public sealed record OfpAirport(
    string? Icao,
    string? Name,
    string? PlanRunway,
    double? Lat,
    double? Lon,
    int? ElevationFt,
    int? TransitionAltitudeFt,
    int? TransitionLevelFt,
    string? Metar);

public sealed record OfpFix
{
    public required string Ident { get; init; }

    public string? Name { get; init; }

    /// <summary>wpt, vor, ndb, apt, ltlg (TOC/TOD) …</summary>
    public string? Type { get; init; }

    public string? Airway { get; init; }

    /// <summary>CLB, CRZ, DSC.</summary>
    public string? Stage { get; init; }

    public bool IsSidStar { get; init; }

    /// <summary>FIR, in der der Fix liegt (z. B. "EDGG"), falls SimBrief sie liefert.</summary>
    public string? Fir { get; init; }

    public double? Lat { get; init; }

    public double? Lon { get; init; }

    public int? AltitudeFt { get; init; }

    public double? LegDistanceNm { get; init; }

    public double? CumulativeDistanceNm { get; init; }

    /// <summary>Geplante Zeit ab Abheben bis zu diesem Fix.</summary>
    public int? TimeTotalSec { get; init; }

    public double? FuelPlanOnboardKg { get; init; }

    public double? FuelMinOnboardKg { get; init; }
}

public sealed record OfpFuel
{
    public double? TaxiKg { get; init; }

    public double? EnrouteBurnKg { get; init; }

    public double? ContingencyKg { get; init; }

    public double? AlternateBurnKg { get; init; }

    public double? ReserveKg { get; init; }

    public double? ExtraKg { get; init; }

    public double? MinTakeoffKg { get; init; }

    public double? PlanTakeoffKg { get; init; }

    public double? PlanRampKg { get; init; }

    public double? PlanLandingKg { get; init; }
}

public sealed record OfpWeights
{
    public int? PaxCount { get; init; }

    public double? PayloadKg { get; init; }

    public double? EstZfwKg { get; init; }

    public double? MaxZfwKg { get; init; }

    public double? EstTowKg { get; init; }

    public double? MaxTowKg { get; init; }

    public double? EstLdwKg { get; init; }

    public double? MaxLdwKg { get; init; }
}

public sealed record OfpTimes
{
    public DateTimeOffset? ScheduledOutUtc { get; init; }

    public DateTimeOffset? ScheduledOffUtc { get; init; }

    public DateTimeOffset? ScheduledOnUtc { get; init; }

    public DateTimeOffset? ScheduledInUtc { get; init; }

    public int? EstTimeEnrouteSec { get; init; }
}

public sealed record RoutePoint(string Ident, double Lat, double Lon, int FixIndex);

/// <summary>Liest das JSON aus https://www.simbrief.com/api/xml.fetcher.php?json=1. SimBrief liefert alle Werte als Strings.</summary>
public static class OfpParser
{
    private const double LbsToKg = 0.45359237;

    public static Ofp Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (Obj(root, "fetch") is { } fetch && Str(fetch, "status") is { } status && !status.Equals("Success", StringComparison.OrdinalIgnoreCase))
        {
            throw new SimBriefException($"SimBrief: {status}");
        }

        var p = Obj(root, "params");
        var units = Str(p, "units")?.ToLowerInvariant() is "lbs" ? "lbs" : "kgs";
        var factor = units == "lbs" ? LbsToKg : 1.0;
        double? Mass(JsonElement? e, string name) => Dbl(e, name) is { } v ? Math.Round(v * factor) : null;

        var general = Obj(root, "general");
        var atc = Obj(root, "atc");
        var aircraft = Obj(root, "aircraft");
        var fuel = Obj(root, "fuel");
        var weights = Obj(root, "weights");
        var times = Obj(root, "times");

        var fixes = new List<OfpFix>();
        double cumulative = 0;
        foreach (var f in Items(Obj(root, "navlog"), "fix"))
        {
            var leg = Dbl(f, "distance");
            cumulative += leg ?? 0;
            fixes.Add(new OfpFix
            {
                Ident = Str(f, "ident") ?? "?",
                Name = Str(f, "name"),
                Type = Str(f, "type"),
                Airway = Str(f, "via_airway"),
                Stage = Str(f, "stage"),
                IsSidStar = Str(f, "is_sid_star") == "1",
                Fir = Str(f, "fir")?.ToUpperInvariant(),
                Lat = Dbl(f, "pos_lat"),
                Lon = Dbl(f, "pos_long"),
                AltitudeFt = Int(f, "altitude_feet"),
                LegDistanceNm = leg,
                CumulativeDistanceNm = leg is null ? null : cumulative,
                TimeTotalSec = Int(f, "time_total"),
                FuelPlanOnboardKg = Mass(f, "fuel_plan_onboard"),
                FuelMinOnboardKg = Mass(f, "fuel_min_onboard"),
            });
        }

        var flightNumber = (Str(general, "icao_airline") ?? "") + (Str(general, "flight_number") ?? "");

        return new Ofp
        {
            RequestId = Str(p, "request_id"),
            GeneratedUtc = Unix(p, "time_generated"),
            SourceUnits = units,
            Callsign = Str(atc, "callsign"),
            FlightNumber = flightNumber.Length > 0 ? flightNumber : null,
            AircraftIcao = Str(aircraft, "icaocode") ?? Str(aircraft, "icao_code"),
            Registration = Str(aircraft, "reg"),
            Origin = Airport(Obj(root, "origin")),
            Destination = Airport(Obj(root, "destination")),
            Alternate = Obj(root, "alternate") is { } alt && Str(alt, "icao_code") is not null ? Airport(alt) : null,
            Route = Str(general, "route"),
            InitialAltitudeFt = Int(general, "initial_altitude"),
            SidIdent = Str(general, "sid_ident")?.ToUpperInvariant(),
            StarIdent = Str(general, "star_ident")?.ToUpperInvariant(),
            CostIndex = Int(general, "costindex"),
            AirDistanceNm = Int(general, "air_distance"),
            AvgWindComponentKt = Int(general, "avg_wind_comp"),
            Fuel = new OfpFuel
            {
                TaxiKg = Mass(fuel, "taxi"),
                EnrouteBurnKg = Mass(fuel, "enroute_burn"),
                ContingencyKg = Mass(fuel, "contingency"),
                AlternateBurnKg = Mass(fuel, "alternate_burn"),
                ReserveKg = Mass(fuel, "reserve"),
                ExtraKg = Mass(fuel, "extra"),
                MinTakeoffKg = Mass(fuel, "min_takeoff"),
                PlanTakeoffKg = Mass(fuel, "plan_takeoff"),
                PlanRampKg = Mass(fuel, "plan_ramp"),
                PlanLandingKg = Mass(fuel, "plan_landing"),
            },
            Weights = new OfpWeights
            {
                PaxCount = Int(weights, "pax_count"),
                PayloadKg = Mass(weights, "payload"),
                EstZfwKg = Mass(weights, "est_zfw"),
                MaxZfwKg = Mass(weights, "max_zfw"),
                EstTowKg = Mass(weights, "est_tow"),
                MaxTowKg = Mass(weights, "max_tow"),
                EstLdwKg = Mass(weights, "est_ldw"),
                MaxLdwKg = Mass(weights, "max_ldw"),
            },
            Times = new OfpTimes
            {
                ScheduledOutUtc = Unix(times, "sched_out"),
                ScheduledOffUtc = Unix(times, "sched_off"),
                ScheduledOnUtc = Unix(times, "sched_on"),
                ScheduledInUtc = Unix(times, "sched_in"),
                EstTimeEnrouteSec = Int(times, "est_time_enroute"),
            },
            Fixes = fixes,
        };
    }

    private static OfpAirport Airport(JsonElement? e) => new(
        Str(e, "icao_code"),
        Str(e, "name"),
        Str(e, "plan_rwy"),
        Dbl(e, "pos_lat"),
        Dbl(e, "pos_long"),
        Int(e, "elevation"),
        Int(e, "trans_alt"),
        Int(e, "trans_level"),
        Str(e, "metar"));

    // ------------------------------------------------------------------ JSON-Helfer (SimBrief liefert fast alles als String)

    private static JsonElement? Obj(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>SimBrief liefert Listen mit einem Element manchmal als Objekt statt Array.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(name, out var v))
        {
            yield break;
        }

        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }
        }
        else if (v.ValueKind == JsonValueKind.Object)
        {
            yield return v;
        }
    }

    private static string? Str(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(name, out var v))
        {
            return null;
        }

        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static double? Dbl(JsonElement? e, string name) =>
        Str(e, name) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    private static int? Int(JsonElement? e, string name) => Dbl(e, name) is { } d ? (int)Math.Round(d) : null;

    private static DateTimeOffset? Unix(JsonElement? e, string name) =>
        Dbl(e, name) is { } s && s > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)s) : null;
}

public sealed class SimBriefException(string message) : Exception(message);
