using System.Globalization;
using Duopilo.FlightPlan;
using Duopilo.Sim;
using Duopilo.Vehicles;

namespace Duopilo.Traffic;

/// <summary>
/// Follow-me für den eigenen KI-Verkehr (A3, A4 mit followMe, docs/research/follow-me.md): Nach der Prüfung der Parkposition 2 NM vor
/// der Schwelle fährt das Fahrzeug los und wartet gewendet hinter der Stelle, an der das Flugzeug die Bahn verlässt. Das Flugzeug rollt
/// bis dorthin und folgt dann mit Abstand (Halt hinter dem Fahrzeug über <see cref="TaxiKinematics.TrafficStopS"/>); das Fahrzeug regelt
/// seine Geschwindigkeit nach dem Flugzeug, hält vor gekreuzten Bahnen (auch auf dem Weg entgegen), bis sie frei sind, und fährt am Gate
/// vorbei und weg.
/// </summary>
public sealed partial class TrafficTestService
{
    /// <summary>So nah darf das Flugzeug dem Fahrzeug kommen (Anteil des Abstands) – darunter bremst es.</summary>
    public const double FollowMeMinGapFactor = FollowMeProfile.MinGapFactor;

    private const double MetersPerSecondPerKnot = 0.514444;

    /// <summary>Halt des Flugzeugs hinter dem Fahrzeug (Meter auf seinem Rollweg), NaN = keiner.</summary>
    public static double FollowMeStop(FollowMeVehicle vehicle) => vehicle.Phase switch
    {
        // Noch unterwegs zum Treffpunkt: das Flugzeug rollt nur, bis es die Bahn verlassen hat
        FollowMePhase.Approach => vehicle.Plan.MeetS - vehicle.Plan.GapM,
        FollowMePhase.InPosition or FollowMePhase.Leading => vehicle.ReferenceS - (vehicle.Plan.GapM * FollowMeMinGapFactor),
        _ => double.NaN,
    };

    /// <summary>Der nähere von zwei Halten (NaN = keiner).</summary>
    public static double CombineStops(double a, double b) =>
        double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Min(a, b);

    /// <summary>
    /// Fahrzeug planen und erzeugen: Weg = Rollweg der Landung (nach einer neuen Parkposition deren Weg), ein Fahrzeug des Flughafens.
    /// Geht es nicht, rollt das Flugzeug allein (Ereignis follow_me_none).
    /// </summary>
    private async Task StartFollowMeAsync(TrafficTestRun run, double seconds)
    {
        if (run.TaxiGraph is not { } graph || run.Taxi is not { } route || run.EndReason is not null || !run.Once("follow_me"))
        {
            return;
        }

        if (run.TaxiActive)
        {
            run.AddEvent(seconds, "follow_me_none taxiing already");
            return;
        }

        var plan = FollowMePlan.FromArrival(graph, route, run.InitialPlan.Runway, FollowMeProfile.GapM(run.Wake));
        if (plan is null)
        {
            run.AddEvent(seconds, "follow_me_none route too short");
            return;
        }

        var lease = _fleet.TryReserve(graph.Icao);
        if (lease is null)
        {
            run.AddEvent(seconds, string.Create(CultureInfo.InvariantCulture, $"follow_me_none all {FollowMeFleet.MaxPerAirport} vehicles busy"));
            return;
        }

        run.FollowMePlan = plan;
        run.AddEvent(seconds, string.Create(CultureInfo.InvariantCulture,
            $"follow_me_planned meet {plan.MeetS:0} m (off the runway at {plan.ClearS:0} m), gap {plan.GapM:0} m, approach {plan.Approach.LengthM:0} m, crossings {plan.ApproachCrossings.Count} approaching, {plan.Crossings.Count} leading"));
        try
        {
            var unit = await _followMe.SpawnAsync(plan, graph, run.InitialPlan.Runway.ElevationFt, vehicle => FollowMeFrameBefore(run, vehicle), vehicle => FollowMeFrameAfter(run, vehicle), CancellationToken.None);
            run.FollowMeLease = lease;
            run.FollowMe = unit;
            run.AddEvent(seconds, "follow_me_spawn object " + unit.ObjectId.ToString(CultureInfo.InvariantCulture));
            if (run.EndReason is not null)
            {
                await RemoveFollowMeAsync(run);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or NotSupportedException)
        {
            lease.Dispose();
            run.AddEvent(seconds, "warning:follow_me_failed " + ex.Message);
        }
    }

    /// <summary>Jeden Frame vor dem Fahrzeug: wo das Flugzeug auf seinem Rollweg ist (vor dem Abrollen: am Anfang, stehend).</summary>
    private static void FollowMeFrameBefore(TrafficTestRun run, FollowMeVehicle vehicle)
    {
        if (run.TaxiActive && run.TaxiKinematics is { } taxi)
        {
            var s = taxi.Phase == TaxiPhase.Holding ? taxi.Taxi.LengthM : taxi.DistanceM;
            vehicle.SetAircraft(s, taxi.SpeedKt * MetersPerSecondPerKnot);
        }
        else
        {
            vehicle.SetAircraft(0, 0);
        }
    }

    /// <summary>Jeden Frame nach dem Fahrzeug: Halt des Flugzeugs dahinter (mit dem Abstand zu anderem Testverkehr zusammen).</summary>
    private static void FollowMeFrameAfter(TrafficTestRun run, FollowMeVehicle vehicle)
    {
        run.FollowMeStopS = FollowMeStop(vehicle);
        var taxi = run.TaxiActive ? run.TaxiKinematics : run.PendingTaxi ?? run.TaxiKinematics;
        if (taxi is not null)
        {
            taxi.TrafficStopS = CombineStops(run.SpacingStopS, run.FollowMeStopS);
        }
    }

    /// <summary>Takt (je Messpunkt des Flugzeugs): Phasen als Ereignisse, vor gekreuzten Bahnen prüfen, weg = entfernen.</summary>
    private void UpdateFollowMe(TrafficTestRun run, double seconds)
    {
        if (run.FollowMe is not { } unit)
        {
            return;
        }

        var vehicle = unit.Vehicle;
        run.FollowMeCrossingsDone = vehicle.ApproachCrossingsDone + vehicle.CrossingsDone;
        var phase = FollowMePhaseName(vehicle);
        if (phase != run.FollowMePhaseLogged)
        {
            run.FollowMePhaseLogged = phase;
            var gap = double.IsNaN(vehicle.GapM) ? "" : string.Create(CultureInfo.InvariantCulture, $", gap {vehicle.GapM:0} m");
            run.AddEvent(seconds, phase switch
            {
                "in_position" => "follow_me_wait in position" + gap,
                "leading" => "follow_me_lead" + gap,
                "holding_short" => "follow_me_holding_short " + (vehicle.HoldingAt?.Runway ?? "?") + (vehicle.Phase == FollowMePhase.Approach ? " approaching" : ""),
                "finished" => "follow_me_done",
                _ => "follow_me_" + phase,
            });
        }

        if (vehicle.HoldingAt is { } crossing && !vehicle.CrossingClear)
        {
            CheckRunwayForFollowMe(run, vehicle, crossing.Runway, seconds);
        }

        if (vehicle.Phase == FollowMePhase.Gone)
        {
            _ = RemoveFollowMeAsync(run);
        }
    }

    /// <summary>
    /// Das Fahrzeug steht vor einer gekreuzten Bahn: Verkehr abfragen (höchstens eine Abfrage zugleich) und freigeben, wenn nichts auf
    /// der Bahn oder im Endanflug bis 4 NM ist (<see cref="RunwayOccupancy"/>); ohne Antwort nach <see cref="RunwayCheckGiveUpSeconds"/>
    /// trotzdem. Das eigene Testflugzeug zählt nicht.
    /// </summary>
    private void CheckRunwayForFollowMe(TrafficTestRun run, FollowMeVehicle vehicle, string crossedRunway, double seconds)
    {
        if (run.TaxiGraph is not { } graph || run.FollowMeScanBusy || RunwayOf(graph.Runways, crossedRunway.Split('/')[0]) is not { } runway)
        {
            return;
        }

        run.FollowMeScanBusy = true;
        var started = DateTimeOffset.UtcNow;
        run.FollowMeHoldSinceUtc ??= started;
        var holding = (started - run.FollowMeHoldSinceUtc.Value).TotalSeconds;
        _ = Task.Run(async () =>
        {
            try
            {
                var radius = RunwayScanRadiusM;
                if (_sim.Num("lat") is { } lat && _sim.Num("lon") is { } lon)
                {
                    radius += RouteMath.DistanceNm(lat, lon, graph.Latitude, graph.Longitude) * RunwayEnd.MetersPerNm;
                }

                var scan = await _traffic.ScanAsync((uint)Math.Min(radius, TrafficConnection.MaxScanRadiusMeters), includeHelicopters: true, withText: true, CancellationToken.None);
                if (scan.Error is not null)
                {
                    if (holding >= RunwayCheckGiveUpSeconds)
                    {
                        Clear("warning:follow_me_runway_unknown " + crossedRunway + " (" + scan.Error + "), going on");
                    }

                    return;
                }

                var blocker = RunwayOccupancy.Blocker(graph.Icao, runway, scan.Samples, run.ObjectId, RunwayOccupancy.FinalNm);
                if (blocker is null)
                {
                    Clear(string.Create(CultureInfo.InvariantCulture, $"follow_me_runway_clear {crossedRunway} after {holding:0} s"));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException or ObjectDisposedException)
            {
                _log.LogDebug(ex, "Follow-me runway check failed");
            }
            finally
            {
                run.FollowMeScanBusy = false;
            }
        });

        void Clear(string text)
        {
            vehicle.CrossingClear = true;
            run.FollowMeHoldSinceUtc = null;
            run.AddEvent(seconds, text);
        }
    }

    /// <summary>Fahrzeug aus dem Sim nehmen und das Fahrzeug des Flughafens freigeben (am Ende des Follow-me oder des Tests).</summary>
    private async Task RemoveFollowMeAsync(TrafficTestRun run)
    {
        if (run.FollowMe is not { } unit)
        {
            return;
        }

        run.FollowMe = null;
        run.FollowMeStopS = double.NaN;
        if (run.TaxiKinematics is { } taxi)
        {
            taxi.TrafficStopS = run.SpacingStopS;
        }

        await _followMe.RemoveAsync(unit);
        run.FollowMeLease?.Dispose();
        run.FollowMeLease = null;
    }

    private static string FollowMePhaseName(FollowMeVehicle vehicle) => vehicle.HoldingShort ? "holding_short" : vehicle.Phase switch
    {
        FollowMePhase.Approach => "approach",
        FollowMePhase.InPosition => "in_position",
        FollowMePhase.Leading => "leading",
        FollowMePhase.AtStand => "at_stand",
        FollowMePhase.DrivingOff => "driving_off",
        FollowMePhase.Leaving => "leaving",
        _ => "finished",
    };

    /// <summary>Follow-me im Status des Rollens (taxi.followMe), null = keins.</summary>
    private static TrafficTestFollowMe? FollowMeStatus(TrafficTestRun run)
    {
        if (run.FollowMePlan is not { } plan)
        {
            return null;
        }

        var vehicle = run.FollowMe?.Vehicle;
        return new TrafficTestFollowMe(
            vehicle is null ? (run.FollowMePhaseLogged is null ? "planned" : "finished") : FollowMePhaseName(vehicle),
            run.FollowMe?.ObjectId,
            Math.Round(plan.MeetS),
            Math.Round(plan.ClearS ?? plan.MeetS - plan.GapM),
            plan.GapM,
            vehicle is not null && !double.IsNaN(vehicle.GapM) ? Math.Round(vehicle.GapM) : null,
            vehicle is not null ? Math.Round(vehicle.SpeedMps / MetersPerSecondPerKnot, 1) : 0,
            vehicle is null ? run.FollowMeCrossingsDone : vehicle.ApproachCrossingsDone + vehicle.CrossingsDone,
            plan.ApproachCrossings.Count + plan.Crossings.Count);
    }
}
