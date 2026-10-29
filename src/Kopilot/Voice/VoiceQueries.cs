using System.Globalization;
using Kopilot.Monitoring;
using Kopilot.Sim;

namespace Kopilot.Voice;

/// <summary>Was die Abfragen brauchen – vom Sim unabhängig, damit testbar.</summary>
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
}

public sealed record EngineReading(bool? Running, double? N1Pct);

/// <summary>Liest den Zustand für Abfragen.</summary>
public interface IVoiceFlightStateSource
{
    VoiceFlightState Read();
}

/// <summary>Zustand aus SimConnect und dem letzten Monitoring-Takt.</summary>
public sealed class SimVoiceFlightState(SimConnectClient sim, MonitoringService monitoring) : IVoiceFlightStateSource
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

        return new VoiceFlightState
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
        };
    }
}

/// <summary>
/// Antworten auf Abfragen – nur der reine Callout, ohne Zusatzinfos ("Speedbrake retracted", "Speed 250").
/// Geschwindigkeiten immer in Knoten, nie Mach.
/// </summary>
public static class VoiceQueries
{
    public const string Unable = "Unable";

    /// <summary>Normaldruck: Höhenmesser auf STD.</summary>
    private const double StandardHpa = 1013.25;

    public static string Answer(QueryKind query, VoiceFlightState s)
    {
        if (!s.Connected)
        {
            return Unable;
        }

        return query switch
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
            _ => Unable,
        };
    }

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
            return $"FL{level.ToString("000", CultureInfo.InvariantCulture)}";
        }

        if (s.AltitudeFt is not { } alt)
        {
            return Unable;
        }

        var rounded = (long)Math.Round(alt / 100) * 100;
        return $"Altitude {rounded.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string Fuel(VoiceFlightState s)
    {
        if (s.FuelKg is not { } kg || kg < 0)
        {
            return Unable;
        }

        if (kg >= 1000)
        {
            return $"Fuel {(kg / 1000).ToString("0.0", CultureInfo.InvariantCulture)} tons";
        }

        return $"Fuel {((long)Math.Round(kg / 100) * 100).ToString(CultureInfo.InvariantCulture)} kilos";
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

        return h.ToString("000", CultureInfo.InvariantCulture);
    }

    private static string Int(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
}
