using Kopilot.Aircraft;
using Kopilot.Atc;
using Kopilot.Ecam;
using Kopilot.FlightPlan;
using Kopilot.Sim;

namespace Kopilot.Monitoring;

/// <summary>Baut aus den zwischengespeicherten Sim-Werten einen <see cref="FlightSnapshot"/>.</summary>
public sealed class SnapshotFactory(SimConnectClient sim, EcamCatalog ecam, SimBriefService simBrief)
{
    private static readonly AircraftLimits DefaultLimits = new();

    public FlightSnapshot Build(DateTimeOffset now)
    {
        if (!sim.Connection.IsConnected)
        {
            return new FlightSnapshot { TimeUtc = now, Connected = false };
        }

        var status = sim.Status;
        var signals = new AircraftSignals(sim);
        var ecamState = EcamReader.Read(signals, ecam);

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
            GearExtendedPct = sim.Num("gearCenterPct"),
            GearHandleDown = sim.Bool("gearHandle"),
            ApEngaged = signals.ApEngaged,
            SelectedAltitudeFt = signals.SelectedAltitudeFt,
            SpeedTargetKt = signals.SpeedTargetKt,
            MasterWarning = signals.MasterWarning,
            MasterCaution = signals.MasterCaution,
            EcamTitles = ecamState.Titles,
            StallWarning = sim.Bool("stallWarning"),
            OverspeedWarning = sim.Bool("overspeedWarning"),
            Limits = signals.Profile?.Limits ?? DefaultLimits,
            AssignedRunway = AtcReader.AssignedRunway(sim, simBrief.Current),
        };
    }
}
