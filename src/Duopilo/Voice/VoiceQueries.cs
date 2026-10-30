using System.Globalization;
using Duopilo.FlightPlan;
using Duopilo.Monitoring;
using Duopilo.Sim;
using Duopilo.Speech;
using Duopilo.Traffic;

namespace Duopilo.Voice;

/// <summary>Nächster Wegpunkt laut SimBrief-Plan (bzw. FMS): Kennung, Name (Funkfeuer), Lage, Distanz und ETA (Sekunden seit Mitternacht UTC).</summary>
public sealed record WaypointReading(string Ident, string? Name, double? Lat, double? Lon, double DistanceNm, double? EtaZuluSec);

/// <summary>Verkehr für die Abfrage: <see cref="Available"/> = abgefragt, <see cref="Callout"/> = Meldung zum nächsten Ziel (null = kein Verkehr).</summary>
public sealed record TrafficReading(bool Available, string? Callout);

/// <summary>Aktive ATC-Station und ihre Frequenz (MHz).</summary>
public sealed record StationReading(string Name, double? FrequencyMhz);

/// <summary>Was die Abfragen und Checklisten brauchen – vom Sim unabhängig, damit testbar.</summary>
public sealed record VoiceFlightState
{
    public bool Connected { get; init; }

    public bool Paused { get; init; }

    public bool? OnGround { get; init; }

    public double? IasKt { get; init; }

    public double? GroundSpeedKt { get; init; }

    /// <summary>Angezeigte Höhe (ft, mit eingestelltem Baro).</summary>
    public double? AltitudeFt { get; init; }

    public double? PressureAltitudeFt { get; init; }

    /// <summary>Eingestellter Höhenmesser (hPa).</summary>
    public double? BaroHpa { get; init; }

    public double? HeadingMagDeg { get; init; }

    /// <summary>Klappenstellung aus dem Profil ("1+F", "FULL"), sonst null.</summary>
    public string? FlapsLabel { get; init; }

    public double? FlapsIndex { get; init; }

    public double? GearExtendedPct { get; init; }

    public bool? GearHandleDown { get; init; }

    public double? SpeedbrakeHandlePct { get; init; }

    public bool? SpoilersArmed { get; init; }

    public bool? SeatbeltsOn { get; init; }

    public double? ApuNPct { get; init; }

    public double? FuelKg { get; init; }

    public IReadOnlyList<EngineReading> Engines { get; init; } = [];

    // ---------------- Plan (SimBrief)

    /// <summary>Ein SimBrief-Plan ist geladen und die Position liegt auf der Route.</summary>
    public bool HasPlan { get; init; }

    /// <summary>Treibstoff gegen Plan an der Position (null ohne Plan).</summary>
    public FuelPlanSample? FuelPlan { get; init; }

    public WaypointReading? NextWaypoint { get; init; }

    /// <summary>ETA am Ziel (Sekunden seit Mitternacht UTC).</summary>
    public double? DestinationEtaZuluSec { get; init; }

    /// <summary>Distanz bis zum Top of Descent laut Plan (null = keiner mehr vor dem Flugzeug).</summary>
    public double? TodDistanceNm { get; init; }

    public double? TodEtaZuluSec { get; init; }

    /// <summary>Der Plan hat einen Top of Descent, der schon hinter dem Flugzeug liegt (oder das FMS meldet T/D REACHED).</summary>
    public bool TodPassed { get; init; }

    // ---------------- Umgebung, Transponder, Funk

    public double? WindDirectionDeg { get; init; }

    public double? WindSpeedKt { get; init; }

    /// <summary>Außentemperatur (SAT, °C).</summary>
    public double? OutsideTempC { get; init; }

    /// <summary>Transpondercode ("2340").</summary>
    public string? Squawk { get; init; }

    /// <summary>Transponder-Modus wie get_systems: OFF, STBY, TEST, ON, ALT, GROUND.</summary>
    public string? TransponderMode { get; init; }

    /// <summary>Aktive Station der ATC-Sitzung (null = keine Sitzung).</summary>
    public StationReading? Station { get; init; }

    public double? Com1ActiveMhz { get; init; }

    // ---------------- Für Checklisten

    public bool? BeaconOn { get; init; }

    public bool? ParkingBrakeSet { get; init; }

    public bool? ApuBleedOn { get; init; }

    /// <summary>Autobrake laut Profil (OFF, LO, MED, MAX …).</summary>
    public string? AutobrakeMode { get; init; }

    /// <summary>A/THR-Status laut Profil (OFF, ARMED, ACTIVE).</summary>
    public string? AutothrustStatus { get; init; }

    /// <summary>Entscheidungshöhe (ft) laut Profil.</summary>
    public double? DecisionHeightFt { get; init; }

    /// <summary>Klappenstellungen, die als Landekonfiguration gelten (Profil).</summary>
    public IReadOnlyList<string> LandingFlaps { get; init; } = [];
}

public sealed record EngineReading(bool? Running, double? N1Pct);

/// <summary>Liest den Zustand für Abfragen und Checklisten.</summary>
public interface IVoiceFlightStateSource
{
    VoiceFlightState Read();

    /// <summary>Verkehr abfragen (wie get_traffic). Standard: nicht verfügbar.</summary>
    Task<TrafficReading> TrafficAsync(CancellationToken ct) => Task.FromResult(new TrafficReading(false, null));
}

/// <summary>Zustand aus SimConnect, dem letzten Monitoring-Takt, dem SimBrief-Plan, dem Verkehr und dem ATC-Modul.</summary>
public sealed class SimVoiceFlightState(
    SimConnectClient sim,
    MonitoringService monitoring,
    SimBriefService? simBrief = null,
    TrafficService? traffic = null,
    IAtcLink? atc = null) : IVoiceFlightStateSource
{
    public VoiceFlightState Read()
    {
        if (!sim.Connection.IsConnected)
        {
            return new VoiceFlightState();
        }

        var signals = new AircraftSignals(sim);
        var snapshot = monitoring.LastSnapshot;
        var count = Math.Clamp((int)Math.Round(sim.Num("engineCount") ?? 2), 1, SimVarCatalog.MaxEngines);
        var engines = Enumerable.Range(1, count)
            .Select(i => new EngineReading(sim.Bool($"eng{i}Running"), sim.Num($"eng{i}N1")))
            .ToList();

        var state = new VoiceFlightState
        {
            Connected = true,
            Paused = snapshot?.Paused ?? sim.Status.Paused,
            OnGround = sim.Bool("onGround"),
            IasKt = sim.Num("ias"),
            GroundSpeedKt = sim.Num("gs"),
            AltitudeFt = sim.Num("altIndicated"),
            PressureAltitudeFt = sim.Num("altPressure"),
            BaroHpa = sim.Num("baroSettingHpa"),
            HeadingMagDeg = sim.Num("hdgMag"),
            FlapsLabel = signals.FlapsLabel,
            FlapsIndex = sim.Num("flapsIndex"),
            GearExtendedPct = sim.Num("gearCenterPct"),
            GearHandleDown = sim.Bool("gearHandle"),
            SpeedbrakeHandlePct = sim.Num("spoilersHandlePct"),
            SpoilersArmed = signals.SpoilersArmed,
            SeatbeltsOn = sim.Bool("seatbeltSign"),
            ApuNPct = signals.ApuNPct,
            FuelKg = sim.Num("fuelTotalKg"),
            Engines = engines,
            WindDirectionDeg = sim.Num("windDir"),
            WindSpeedKt = sim.Num("windKt"),
            OutsideTempC = sim.Num("oatC"),
            Squawk = Duopilo.Tools.Fmt.Squawk(sim.Num("xpdrCode")),
            TransponderMode = Duopilo.Tools.Fmt.TransponderMode(sim.Num("xpdrState")),
            Com1ActiveMhz = sim.Num("com1Active"),
            BeaconOn = sim.Bool("lightBeacon"),
            ParkingBrakeSet = signals.ParkingBrake,
            ApuBleedOn = signals.Has(Duopilo.Aircraft.BindingKeys.ApuBleed) ? signals.Flag(Duopilo.Aircraft.BindingKeys.ApuBleed) : null,
            AutobrakeMode = signals.Autobrake,
            AutothrustStatus = signals.AutothrustStatus,
            DecisionHeightFt = signals.DecisionHeightFt,
            LandingFlaps = signals.Profile?.Limits.LandingFlaps ?? [],
            Station = Station(),
        };

        return WithPlan(state, signals, snapshot);
    }

    public async Task<TrafficReading> TrafficAsync(CancellationToken ct)
    {
        if (traffic is null || !sim.Connection.IsConnected || sim.Status.FlightRunning == false)
        {
            return new TrafficReading(false, null);
        }

        var result = await traffic.ScanAsync(TrafficService.DefaultRadiusNm, ct);
        if (result.Error is not null || result.Picture.Own is null)
        {
            return new TrafficReading(false, null);
        }

        // In der Luft zählt nur Verkehr in der Luft – ein geparktes Flugzeug unter uns ist keine Meldung wert
        var airborne = sim.Bool("onGround") == false;
        var target = result.Picture.Targets.FirstOrDefault(t => !airborne || !t.OnGround);
        return new TrafficReading(true, target?.Callout);
    }

    private StationReading? Station()
    {
        try
        {
            return atc is { Enabled: true } ? atc.ActiveStation() : null;
        }
        catch (Exception)
        {
            // ATC-Modul nicht bereit – Abfrage nimmt dann COM1
            return null;
        }
    }

    /// <summary>Plan-Werte wie get_progress: nächster Wegpunkt (FMS vor Plan), ETA am Ziel, TOD, Treibstoff gegen Plan.</summary>
    private VoiceFlightState WithPlan(VoiceFlightState state, AircraftSignals signals, FlightSnapshot? snapshot)
    {
        var tdReached = signals.TdReached == true;
        if (simBrief?.Current is not { } ofp || sim.Num("lat") is not { } lat || sim.Num("lon") is not { } lon)
        {
            return state with { TodPassed = tdReached };
        }

        var points = ofp.RoutePoints();
        if (points.Count < 2 || simBrief!.Locate(ofp, points, lat, lon) is not { } route)
        {
            return state with { TodPassed = tdReached };
        }

        var zulu = sim.Num("zuluTimeSec");
        var gs = sim.Num("gs");
        var passage = simBrief.ObservePassage(ofp, points, route, zulu, gs);
        var progress = ProgressCalculator.Compute(ofp, points, route, sim.Num("fuelTotalKg"), signals.FmsWaypoint(lat, lon));
        var eta = EtaCalculator.Predict(ofp, points, progress, gs, passage);
        double? At(double? ete) => zulu is { } z && ete is { } e ? z + e : null;

        var hasTod = points.Any(p => p.Ident.Equals("TOD", StringComparison.OrdinalIgnoreCase));
        return state with
        {
            HasPlan = true,
            FuelPlan = snapshot?.FuelPlan ?? FuelPlanSample.From(ofp, points, route, progress, DateTimeOffset.UtcNow),
            NextWaypoint = new WaypointReading(
                progress.Next.Ident,
                progress.NextFix?.Name,
                progress.Next.Lat,
                progress.Next.Lon,
                progress.DistanceToNextNm,
                At(eta.NextSec)),
            DestinationEtaZuluSec = At(eta.DestinationSec),
            TodDistanceNm = progress.TopOfDescent?.DistanceNm,
            TodEtaZuluSec = At(eta.TopOfDescentSec),
            TodPassed = tdReached || (hasTod && progress.TopOfDescent is null),
        };
    }
}

/// <summary>
/// Antworten auf Abfragen – nur der reine Callout, ohne Zusatzinfos ("Speedbrake retracted", "Speed 250").
/// Geschwindigkeiten immer in Knoten, nie Mach. Die Sprechform kommt aus der gemeinsamen Aussprache (<see cref="Pronunciation"/>).
/// </summary>
public static class VoiceQueries
{
    public const string Unable = "Unable";

    public const string NoTraffic = "No traffic";

    /// <summary>Normaldruck: Höhenmesser auf STD.</summary>
    private const double StandardHpa = 1013.25;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Antwort als Text (Anzeige und Log).</summary>
    public static string Answer(QueryKind query, VoiceFlightState s) => Reply(query, s).Text;

    /// <summary>Antwort mit Sprechform (Wegpunkte mit Namen aus dem Navlog, Zeiten Ziffer für Ziffer).</summary>
    public static VoiceReply Reply(QueryKind query, VoiceFlightState s)
    {
        if (!s.Connected)
        {
            return VoiceReply.Of(Unable);
        }

        return query switch
        {
            QueryKind.NextWaypoint => NextWaypoint(s),
            QueryKind.Traffic => VoiceReply.Of(Unable),
            _ => VoiceReply.Of(Text(query, s)),
        };
    }

    /// <summary>Antwort auf die Verkehrsabfrage: der Callout wie bei get_traffic, sonst "No traffic".</summary>
    public static VoiceReply Traffic(TrafficReading reading) =>
        VoiceReply.Of(!reading.Available ? Unable : reading.Callout ?? NoTraffic);

    private static string Text(QueryKind query, VoiceFlightState s) => query switch
    {
        QueryKind.Speed => Speed(s),
        QueryKind.Altitude => Altitude(s),
        QueryKind.Fuel => Fuel(s),
        QueryKind.Engines => Engines(s),
        QueryKind.Gear => Gear(s),
        QueryKind.Flaps => Flaps(s),
        QueryKind.Speedbrake => Speedbrake(s),
        QueryKind.Spoilers => s.SpoilersArmed switch { true => "Spoilers armed", false => "Spoilers disarmed", _ => Unable },
        QueryKind.Seatbelts => s.SeatbeltsOn switch { true => "Seat belts on", false => "Seat belts off", _ => Unable },
        QueryKind.Apu => s.ApuNPct switch { > 95 => "APU available", > 3 => "APU starting", not null => "APU off", _ => Unable },
        QueryKind.Baro => Baro(s),
        QueryKind.Heading => s.HeadingMagDeg is { } h ? $"Heading {Heading(h)}" : Unable,
        QueryKind.FuelPlan => FuelPlan(s),
        QueryKind.Eta => s.DestinationEtaZuluSec is { } eta ? $"ETA {Clock(eta)}" : Unable,
        QueryKind.TopOfDescent => TopOfDescent(s),
        QueryKind.Wind => Wind(s) ?? Unable,
        QueryKind.Temperature => Temperature(s) ?? Unable,
        QueryKind.Weather => Wind(s) is { } wind && Temperature(s) is { } temp ? $"{wind}, {char.ToLowerInvariant(temp[0])}{temp[1..]}" : Wind(s) ?? Temperature(s) ?? Unable,
        QueryKind.Squawk => Squawk(s),
        QueryKind.Station => Station(s),
        _ => Unable,
    };

    private static string Speed(VoiceFlightState s)
    {
        // Am Boden beim Rollen zählt die Groundspeed
        if (s.OnGround == true && s.GroundSpeedKt is { } gs && gs < 40 && (s.IasKt is not { } slow || slow < 40))
        {
            return $"Ground speed {Int(gs)}";
        }

        return s.IasKt is { } ias ? $"Speed {Int(ias)}" : Unable;
    }

    private static string Altitude(VoiceFlightState s)
    {
        if (IsStandard(s.BaroHpa) && s.PressureAltitudeFt is { } pressure && pressure > 1000)
        {
            var level = (int)Math.Round(pressure / 100);
            return $"FL{level.ToString("000", Inv)}";
        }

        if (s.AltitudeFt is not { } alt)
        {
            return Unable;
        }

        var rounded = (long)Math.Round(alt / 100) * 100;
        return $"Altitude {rounded.ToString(Inv)}";
    }

    private static string Fuel(VoiceFlightState s)
    {
        if (s.FuelKg is not { } kg || kg < 0)
        {
            return Unable;
        }

        if (kg >= 1000)
        {
            return $"Fuel {(kg / 1000).ToString("0.0", Inv)} tons";
        }

        return $"Fuel {((long)Math.Round(kg / 100) * 100).ToString(Inv)} kilos";
    }

    /// <summary>"Fuel 34.1, 2.2 below plan, landing fuel 6.8" – wie der Fuel Check, dazu die prognostizierte Landemenge.</summary>
    private static string FuelPlan(VoiceFlightState s)
    {
        if (s.FuelPlan is not { } plan)
        {
            return Unable;
        }

        var actual = s.FuelKg ?? plan.ActualKg;
        var diff = actual - plan.PlannedKg;
        var extras = new List<string>();
        if (plan.PlannedLandingKg is { } landing)
        {
            var predicted = landing + diff;
            extras.Add("landing fuel " + FuelCheckText.Amount(predicted));
            if (plan.MinimumLandingKg is { } minimum && predicted < minimum)
            {
                extras.Add("below minimum");
            }
        }

        return FuelCheckText.Callout(null, actual, diff, extras);
    }

    private static VoiceReply NextWaypoint(VoiceFlightState s)
    {
        if (s.NextWaypoint is not { } wp)
        {
            return VoiceReply.Of(Unable);
        }

        var ident = wp.Ident.Trim().ToUpperInvariant();
        var rest = $"{Miles(wp.DistanceNm)}" + (wp.EtaZuluSec is { } eta ? $", ETA {Clock(eta)}" : "");
        return new VoiceReply($"{ident}, {rest}", $"{Pronunciation.Waypoint(ident, wp.Name, wp.Lat, wp.Lon)}, {Pronunciation.ForSpeech(rest)}");
    }

    private static string TopOfDescent(VoiceFlightState s)
    {
        if (s.TodDistanceNm is { } distance)
        {
            return $"Top of descent {Miles(distance)}" + (s.TodEtaZuluSec is { } eta ? $", ETA {Clock(eta)}" : "");
        }

        return s.TodPassed ? "Top of descent passed" : Unable;
    }

    /// <summary>"Wind 270 at 45" – Richtung auf 1° (dreistellig), unter 3 kt "Wind calm".</summary>
    private static string? Wind(VoiceFlightState s)
    {
        if (s.WindDirectionDeg is not { } dir || s.WindSpeedKt is not { } kt)
        {
            return null;
        }

        var speed = (int)Math.Round(kt, MidpointRounding.AwayFromZero);
        return speed < 3 ? "Wind calm" : $"Wind {Heading(dir)} at {speed.ToString(Inv)}";
    }

    private static string? Temperature(VoiceFlightState s) =>
        s.OutsideTempC is { } c ? $"Temperature {Math.Round(c).ToString("0", Inv)}" : null;

    private static string Squawk(VoiceFlightState s)
    {
        if (s.Squawk is not { } code)
        {
            return Unable;
        }

        return s.TransponderMode is "STBY" or "OFF" ? $"Squawk {code}, standby" : $"Squawk {code}";
    }

    /// <summary>"Montreal Center, 132.6" aus der ATC-Sitzung, ohne Sitzung die aktive COM1-Frequenz.</summary>
    private static string Station(VoiceFlightState s)
    {
        if (s.Station is { } station && !string.IsNullOrWhiteSpace(station.Name))
        {
            return station.FrequencyMhz is { } mhz ? $"{station.Name}, {Frequency(mhz)}" : station.Name;
        }

        return s.Com1ActiveMhz is { } com ? $"Active frequency {Frequency(com)}" : Unable;
    }

    private static string Engines(VoiceFlightState s)
    {
        var engines = s.Engines;
        if (engines.Count == 0 || engines.All(e => e.Running is null))
        {
            return Unable;
        }

        var off = engines.Select((e, i) => (e, n: i + 1)).Where(x => x.e.Running == false).Select(x => x.n).ToList();
        if (off.Count == engines.Count)
        {
            return "Engines shut down";
        }

        if (off.Count > 0)
        {
            return string.Join(", ", off.Select(n => $"Engine {n}")) + " not running";
        }

        var n1 = engines.Select(e => e.N1Pct).OfType<double>().ToList();
        if (n1.Count >= 2 && n1.Max() - n1.Min() > 5)
        {
            return "N1 split";
        }

        return "Engines normal";
    }

    private static string Gear(VoiceFlightState s) => s.GearExtendedPct switch
    {
        > 95 => "Gear down",
        < 5 => "Gear up",
        not null => "Gear in transit",
        _ => s.GearHandleDown switch { true => "Gear down", false => "Gear up", _ => Unable },
    };

    private static string Flaps(VoiceFlightState s)
    {
        if (s.FlapsLabel is { Length: > 0 } label)
        {
            var l = label.Trim().ToUpperInvariant();
            return l switch
            {
                "FULL" => "Flaps full",
                "1+F" => "Flaps 1",
                _ => $"Flaps {l}",
            };
        }

        return s.FlapsIndex is { } index ? $"Flaps {Int(index)}" : Unable;
    }

    private static string Speedbrake(VoiceFlightState s) => s.SpeedbrakeHandlePct switch
    {
        <= 2 => "Speedbrake retracted",
        >= 95 => "Speedbrake full",
        >= 40 and <= 60 => "Speedbrake half",
        not null => "Speedbrake extended",
        _ => Unable,
    };

    private static string Baro(VoiceFlightState s)
    {
        if (s.BaroHpa is not { } hpa)
        {
            return Unable;
        }

        return IsStandard(hpa) ? "Standard" : $"QNH {Int(hpa)}";
    }

    private static bool IsStandard(double? hpa) => hpa is { } h && Math.Abs(h - StandardHpa) < 0.15;

    private static string Heading(double deg)
    {
        var h = (int)Math.Round(deg) % 360;
        if (h <= 0)
        {
            h += 360;
        }

        return h.ToString("000", Inv);
    }

    /// <summary>Distanz für die Ansage: unter 10 NM mit einer Nachkommastelle ("4.5 miles"), sonst ganz ("120 miles").</summary>
    private static string Miles(double nm) =>
        nm < 9.95 ? $"{Math.Round(nm, 1).ToString("0.0", Inv)} miles" : $"{Math.Round(nm).ToString("0", Inv)} miles";

    /// <summary>Uhrzeit UTC vierstellig ("0342") – die Aussprache spricht sie nach "ETA" Ziffer für Ziffer.</summary>
    public static string Clock(double zuluSec)
    {
        var minutes = (long)Math.Round(zuluSec / 60);
        minutes = ((minutes % 1440) + 1440) % 1440;
        return (minutes / 60).ToString("00", Inv) + (minutes % 60).ToString("00", Inv);
    }

    /// <summary>Frequenz mit den nötigen Nachkommastellen: 132.6, 118.275.</summary>
    private static string Frequency(double mhz)
    {
        var khz = (long)Math.Round(mhz * 1000, MidpointRounding.AwayFromZero);
        var fraction = (khz % 1000).ToString("000", Inv).TrimEnd('0');
        return $"{(khz / 1000).ToString(Inv)}.{(fraction.Length == 0 ? "0" : fraction)}";
    }

    private static string Int(double v) => Math.Round(v).ToString("0", Inv);
}
