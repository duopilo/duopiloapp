using Kopilot.Aircraft;
using Kopilot.Atc;
using Kopilot.Ecam;
using Kopilot.FlightPlan;
using Kopilot.Sim;
using Kopilot.Traffic;

namespace Kopilot.Monitoring;

/// <summary>Baut aus den zwischengespeicherten Sim-Werten einen <see cref="FlightSnapshot"/>.</summary>
public sealed class SnapshotFactory(SimConnectClient sim, EcamCatalog ecam, SimBriefService simBrief, TrafficService traffic)
{
    private static readonly AircraftLimits DefaultLimits = new();

    private DateTimeOffset _todComputedUtc;
    private double? _todDistance;
    private FuelPlanSample? _fuelSample;

    public FlightSnapshot Build(DateTimeOffset now)
    {
        if (!sim.Connection.IsConnected)
        {
            return new FlightSnapshot { TimeUtc = now, Connected = false };
        }

        var status = sim.Status;
        var signals = new AircraftSignals(sim);
        var ecamState = EcamReader.Read(signals, ecam);
        UpdatePlan();

        return new FlightSnapshot
        {
            TimeUtc = now,
            Connected = true,
            FlightRunning = status.FlightRunning,
            Paused = status.Paused,
            PauseKind = status.PauseKind,
            DataAgeSec = sim.AgeSeconds("altIndicated"),
            AircraftKey = $"{sim.Str("title")}|{status.AircraftPath}",
            OnGround = sim.Bool("onGround"),
            IasKt = sim.Num("ias"),
            GroundSpeedKt = sim.Num("gs"),
            VerticalSpeedFpm = sim.Num("vs"),
            AltitudeFt = sim.Num("altIndicated"),
            PressureAltitudeFt = sim.Num("altPressure"),
            AglFt = sim.Num("altAgl"),
            RadioAltFt = sim.Num("altRadio"),
            // MSFS: Bank links = positiv
            BankRightDeg = -sim.Num("bank"),
            FlapsIndex = sim.Num("flapsIndex"),
            FlapsLabel = signals.FlapsLabel,
            SpoilersArmed = signals.SpoilersArmed,
            GearExtendedPct = sim.Num("gearCenterPct"),
            GearHandleDown = sim.Bool("gearHandle"),
            ApEngaged = signals.ApEngaged,
            SelectedAltitudeFt = signals.SelectedAltitudeFt,
            SpeedTargetKt = signals.SpeedTargetKt,
            V1Kt = signals.V1Kt,
            VrKt = signals.VrKt,
            GroundSpoilersOut = signals.GroundSpoilersOut,
            ReverserPct = signals.ReverserPct,
            DecelLight = signals.DecelLight,
            TransitionAltitudeFt = signals.TransitionAltitudeFt ?? simBrief.Current?.Origin.TransitionAltitudeFt,
            TransitionLevelFt = signals.TransitionLevelFt ?? simBrief.Current?.Destination.TransitionLevelFt,
            DecisionHeightFt = signals.DecisionHeightFt,
            MinimumDescentAltitudeFt = signals.MinimumDescentAltitudeFt,
            TdReached = signals.TdReached,
            PlanTodDistanceNm = _todDistance,
            Fma = Fma(signals),
            MasterWarning = signals.MasterWarning,
            MasterCaution = signals.MasterCaution,
            EcamTitles = ecamState.Titles,
            StallWarning = sim.Bool("stallWarning"),
            OverspeedWarning = sim.Bool("overspeedWarning"),
            Limits = signals.Profile?.Limits ?? DefaultLimits,
            AssignedRunway = AtcReader.AssignedRunway(sim, simBrief.Current),
            Traffic = traffic.MonitoringPicture,
            FuelTotalKg = sim.Num("fuelTotalKg"),
            ZuluTimeSec = sim.Num("zuluTimeSec"),
            FuelPlan = _fuelSample,
        };
    }

    public static FmaState Fma(AircraftSignals s)
    {
        string? ap = null;
        if (s.Ap1 == true && s.Ap2 == true)
        {
            ap = "AP1+2";
        }
        else if (s.Ap1 == true)
        {
            ap = "AP1";
        }
        else if (s.Ap2 == true)
        {
            ap = "AP2";
        }
        else if (s.ApEngaged == true)
        {
            ap = "AP";
        }

        static string? Join(IReadOnlyList<string>? items) => items is { Count: > 0 } ? string.Join(" ", items) : null;
        static string? Empty(string? text) => string.IsNullOrEmpty(text) ? null : text;

        return new FmaState(
            Empty(s.AutothrustMode),
            Empty(s.FmaVertical),
            Join(s.FmaVerticalArmed),
            Empty(s.FmaLateral),
            Join(s.FmaLateralArmed),
            ap,
            s.FlightDirector,
            Empty(s.AutothrustStatus));
    }

    /// <summary>
    /// Plan-Werte (TOD, Treibstoff gegen Plan) nur alle paar Sekunden neu rechnen – die Route ändert sich nicht im 200-ms-Takt.
    /// Dabei auch den Überflug der Fixes mitschreiben (ETA nach Plan in get_progress und im Dashboard).
    /// </summary>
    private void UpdatePlan()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _todComputedUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _todComputedUtc = now;
        _todDistance = null;
        _fuelSample = null;
        if (simBrief.Current is { } ofp && sim.Num("lat") is { } lat && sim.Num("lon") is { } lon)
        {
            var points = ofp.RoutePoints();
            if (simBrief.Locate(ofp, points, lat, lon) is { } route)
            {
                simBrief.ObservePassage(ofp, points, route, sim.Num("zuluTimeSec"), sim.Num("gs"));
                var progress = ProgressCalculator.Compute(ofp, points, route, sim.Num("fuelTotalKg"));
                _todDistance = progress.TopOfDescent?.DistanceNm;
                _fuelSample = FuelPlanSample.From(ofp, points, route, progress, now);
            }
        }
    }
}
