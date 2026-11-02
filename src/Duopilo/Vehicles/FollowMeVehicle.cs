using Duopilo.Sim;
using Duopilo.Traffic;

namespace Duopilo.Vehicles;

/// <summary>Abschnitt der Fahrt des Follow-me.</summary>
public enum FollowMePhase
{
    /// <summary>Kommt die Route entgegen und wendet vor dem Flugzeug.</summary>
    Approach,

    /// <summary>Steht gewendet vor dem Flugzeug und wartet, dass es losrollt.</summary>
    InPosition,

    /// <summary>Fährt vor dem Flugzeug her und hält den Abstand.</summary>
    Leading,

    /// <summary>Steht hinter der Einfahrt zum Gate, bis das Flugzeug dort ist.</summary>
    AtStand,

    /// <summary>Fährt weg (am Gate fertig).</summary>
    DrivingOff,

    /// <summary>Abbestellt: fährt ohne Rücksicht auf das Flugzeug davon.</summary>
    Leaving,

    /// <summary>Weg – das Objekt kann entfernt werden.</summary>
    Gone,
}

/// <summary>
/// Kinematik des Follow-me über einen <see cref="FollowMePlan"/>: entgegen, wenden, warten, vorweg mit Abstand, am Gate vorbei und weg.
/// Vorweg regelt es die Geschwindigkeit nach dem Abstand zum Flugzeug (<see cref="SetAircraft"/>): kommt das Flugzeug näher, fährt es
/// schneller, bleibt es stehen, wartet es. <see cref="Advance"/> läuft auf dem SimConnect-Thread, <see cref="SetAircraft"/> und
/// <see cref="Leave"/> kommen aus einem anderen.
/// </summary>
public sealed class FollowMeVehicle
{
    /// <summary>Abstandsregelung: so viel schneller (m/s) je Meter zu wenig Abstand.</summary>
    public const double GapGain = 0.25;

    /// <summary>Am Gate höchstens so lange warten, bis das Flugzeug eingebogen ist.</summary>
    public const double StandWaitSeconds = 120;

    /// <summary>Abbestellt: so weit noch fahren, dann ist es weg.</summary>
    public const double LeaveM = 150;

    /// <summary>Abbestellt auf dem Weg zum Flugzeug und noch so weit vom Wenden weg: gleich verschwinden.</summary>
    public const double LeaveFarM = 200;

    private readonly FollowMePlan _plan;
    private readonly Func<double, double, (double Latitude, double Longitude)> _toLatLon;
    private volatile FollowMePhase _phase = FollowMePhase.Approach;
    private volatile bool _leave;
    private volatile AircraftProgress? _aircraft;
    private AircraftProgress? _aircraftUsed;
    private double _aircraftS = double.NaN;
    private double _aircraftMps;
    private double _phaseSeconds;
    private double _leftM;
    private bool _leavingOnLead;
    private volatile bool _crossingClear;
    private volatile int _crossingsDone;
    private volatile int _approachCrossingsDone;
    private volatile FollowMeCrossing? _holdingAt;
    private double _holdSeconds;

    public FollowMeVehicle(FollowMePlan plan, Func<double, double, (double Latitude, double Longitude)> toLatLon, double groundFt, double heightFt = 0)
    {
        _plan = plan;
        _toLatLon = toLatLon;
        GroundFt = groundFt;
        HeightFt = heightFt;
        var start = plan.Approach.At(0);
        (EastM, NorthM, HeadingDeg) = (start.East, start.North, start.HeadingDeg);
    }

    public FollowMePlan Plan => _plan;

    public FollowMePhase Phase => _phase;

    public double EastM { get; private set; }

    public double NorthM { get; private set; }

    public double HeadingDeg { get; private set; }

    public double SpeedMps { get; private set; }

    /// <summary>Strecke auf dem aktuellen Weg.</summary>
    public double DistanceM { get; private set; }

    /// <summary>Höhe des Bodens (MSL), läuft auf <see cref="GroundTargetFt"/>.</summary>
    public double GroundFt { get; private set; }

    /// <summary>Gemessener Boden unter dem Fahrzeug (aus einem anderen Thread), null = keine Messung.</summary>
    public double? GroundTargetFt { get; set; }

    /// <summary>Bezugspunkt des Modells über dem Boden.</summary>
    public double HeightFt { get; }

    /// <summary>Stelle auf <see cref="FollowMePlan.Reference"/>, ab dem Wenden; vorher NaN.</summary>
    public double ReferenceS => _phase is FollowMePhase.InPosition or FollowMePhase.Leading or FollowMePhase.AtStand || (_phase == FollowMePhase.Leaving && _leavingOnLead)
        ? _plan.LeadOffsetS + DistanceM
        : double.NaN;

    /// <summary>Abstand zum Flugzeug entlang der Route (Meter), NaN = unbekannt.</summary>
    public double GapM => ReferenceS - _aircraftS;

    /// <summary>Wo das Flugzeug auf <see cref="FollowMePlan.Reference"/> steht (weitergerechnet mit seiner Geschwindigkeit), NaN = unbekannt.</summary>
    public double AircraftS => _aircraftS;

    public double PhaseSeconds => _phaseSeconds;

    /// <summary>Vor gekreuzten Bahnen mindestens so lange halten.</summary>
    public const double CrossingHoldSeconds = 3;

    /// <summary>Über eine gekreuzte Bahn mindestens so schnell, bis sie auch für das Flugzeug dahinter frei ist.</summary>
    public const double CrossingMps = 5;

    /// <summary>Hinter einer gekreuzten Bahn noch so viel Luft, bevor es anhalten darf.</summary>
    public const double ClearMarginM = 10;

    /// <summary>Steht vor einer gekreuzten Bahn (<see cref="HoldingAt"/>) und wartet auf <see cref="CrossingClear"/>.</summary>
    public bool HoldingShort => _holdingAt is not null;

    /// <summary>Die Bahn, vor der es gerade steht (auf dem Weg entgegen oder vorweg), sonst null.</summary>
    public FollowMeCrossing? HoldingAt => _holdingAt;

    /// <summary>So viele Bahnen sind beim Vorwegfahren gekreuzt (<see cref="FollowMePlan.Crossings"/>).</summary>
    public int CrossingsDone => _crossingsDone;

    /// <summary>So viele Bahnen sind auf dem Weg entgegen gekreuzt (<see cref="FollowMePlan.ApproachCrossings"/>).</summary>
    public int ApproachCrossingsDone => _approachCrossingsDone;

    /// <summary>Die Bahn vor dem Fahrzeug ist frei (setzt der Verkehrstest aus der Verkehrsabfrage, aus einem anderen Thread).</summary>
    public bool CrossingClear
    {
        get => _crossingClear;
        set => _crossingClear = value;
    }

    /// <summary>Neue Lage des Flugzeugs: Stelle auf <see cref="FollowMePlan.Reference"/> und Geschwindigkeit über Grund (m/s).</summary>
    public void SetAircraft(double referenceS, double groundSpeedMps) => _aircraft = new AircraftProgress(referenceS, Math.Max(0, groundSpeedMps));

    /// <summary>Abbestellt: das Fahrzeug fährt davon und verschwindet.</summary>
    public void Leave() => _leave = true;

    /// <summary>Rechnet <paramref name="dt"/> Sekunden weiter.</summary>
    public void Advance(double dt)
    {
        if (dt <= 0 || _phase == FollowMePhase.Gone)
        {
            return;
        }

        if (GroundTargetFt is { } ground)
        {
            GroundFt = GroundFt < ground
                ? Math.Min(ground, GroundFt + (TrafficKinematics.GroundSlewFtPerSec * dt))
                : Math.Max(ground, GroundFt - (TrafficKinematics.GroundSlewFtPerSec * dt));
        }

        UpdateAircraft(dt);
        _phaseSeconds += dt;
        if (_leave && _phase is not (FollowMePhase.Leaving or FollowMePhase.DrivingOff))
        {
            StartLeaving();
            if (_phase == FollowMePhase.Gone)
            {
                return;
            }
        }

        switch (_phase)
        {
            case FollowMePhase.Approach:
                if (_holdingAt is not null)
                {
                    if (HoldShort(dt))
                    {
                        _approachCrossingsDone++;
                    }

                    break;
                }

                var ahead = NextApproachCrossing();
                Move(_plan.Approach, FollowMeProfile.MaxMps, dt, ahead?.StopM);
                if (ahead is not null && DistanceM >= ahead.StopM - 0.05)
                {
                    StartHold(ahead);
                    break;
                }

                if (AtEnd(_plan.Approach))
                {
                    SpeedMps = 0;
                    Next(FollowMePhase.InPosition);
                }

                break;

            case FollowMePhase.InPosition:
            case FollowMePhase.Leading:
                if (_holdingAt is not null)
                {
                    if (HoldShort(dt))
                    {
                        _crossingsDone++;
                    }

                    break;
                }

                var crossing = NextLeadCrossing();
                double? stopAt = crossing is null ? null : LeadM(crossing.StopM);

                // Auf und dicht hinter einer gekreuzten Bahn nicht stehen bleiben, auch wenn das Flugzeug wartet
                var target = ClearingRunway() ? Math.Max(TargetMps(), CrossingMps) : TargetMps();
                Move(_plan.Lead, target, dt, stopAt);
                if (crossing is not null && DistanceM >= stopAt!.Value - 0.05)
                {
                    StartHold(crossing);
                    break;
                }

                if (_phase == FollowMePhase.InPosition && SpeedMps > 0.3)
                {
                    _phase = FollowMePhase.Leading;
                    _phaseSeconds = 0;
                }

                if (AtEnd(_plan.Lead))
                {
                    SpeedMps = 0;
                    _phase = FollowMePhase.AtStand;
                    _phaseSeconds = 0;
                }

                break;

            case FollowMePhase.AtStand:
                // Das Flugzeug ist an der Einfahrt (oder es dauert zu lange): weg
                if ((_phaseSeconds >= 5 && !double.IsNaN(_aircraftS) && _aircraftS >= _plan.StandEntryS - 10) || _phaseSeconds >= StandWaitSeconds)
                {
                    Next(FollowMePhase.DrivingOff);
                }

                break;

            case FollowMePhase.DrivingOff:
                Move(_plan.DriveOff, FollowMeProfile.MaxMps, dt);
                if (AtEnd(_plan.DriveOff))
                {
                    SpeedMps = 0;
                    _phase = FollowMePhase.Gone;
                }

                break;

            case FollowMePhase.Leaving:
                var before = DistanceM;
                var path = _leavingOnLead ? _plan.Lead : _plan.Approach;
                double? leaveStop = _leavingOnLead
                    ? (NextLeadCrossing() is { } leadCrossing ? LeadM(leadCrossing.StopM) : null)
                    : NextApproachCrossing()?.StopM;
                Move(path, FollowMeProfile.MaxMps, dt, leaveStop);
                if (_leavingOnLead)
                {
                    _leftM += DistanceM - before;
                }

                if (leaveStop is { } bahn && DistanceM >= bahn - 0.05)
                {
                    // Abbestellt kreuzt es keine Bahn mehr: davor verschwinden
                    SpeedMps = 0;
                    _phase = FollowMePhase.Gone;
                }
                else if (AtEnd(path))
                {
                    if (_leavingOnLead)
                    {
                        // Am Ende der Route: wie am Gate weiter und weg
                        Next(FollowMePhase.DrivingOff);
                    }
                    else
                    {
                        // Gewendet: jetzt vom Flugzeug weg
                        _leavingOnLead = true;
                        DistanceM = 0;
                    }
                }
                else if (_leftM >= LeaveM)
                {
                    SpeedMps = 0;
                    _phase = FollowMePhase.Gone;
                }

                break;
        }
    }

    /// <summary>Lage für den Sim: auf dem Weg, Bezugspunkt <see cref="HeightFt"/> über dem Boden, waagerecht.</summary>
    public AiPose Pose()
    {
        var (lat, lon) = _toLatLon(EastM, NorthM);
        return new AiPose(lat, lon, GroundFt + HeightFt, 0, 0, HeadingDeg);
    }

    /// <summary>
    /// Wunschgeschwindigkeit vorweg: so schnell wie das Flugzeug plus <see cref="GapGain"/> je Meter, den der Abstand zu klein ist
    /// (zu groß: langsamer bis Stillstand). Ohne Lage des Flugzeugs: stehen.
    /// </summary>
    private double TargetMps()
    {
        if (double.IsNaN(_aircraftS))
        {
            return 0;
        }

        var gap = GapM;
        if (gap < _plan.GapM * 0.5)
        {
            return FollowMeProfile.MaxMps;
        }

        // Steht das Flugzeug, bleibt auch das Fahrzeug stehen, solange der Abstand reicht (kein Kriechen um die letzten Meter)
        if (_aircraftMps < 0.5 && gap > _plan.GapM * 0.8)
        {
            return 0;
        }

        return Math.Clamp(_aircraftMps + (GapGain * (_plan.GapM - gap)), 0, FollowMeProfile.MaxMps);
    }

    private void StartLeaving()
    {
        _holdingAt = null;
        switch (_phase)
        {
            case FollowMePhase.Approach:
                // Noch weit weg: einfach verschwinden, sonst erst wenden und dann davon
                if (_plan.Approach.LengthM - DistanceM > LeaveFarM)
                {
                    SpeedMps = 0;
                    _phase = FollowMePhase.Gone;
                    return;
                }

                _leavingOnLead = false;
                break;
            case FollowMePhase.AtStand:
                Next(FollowMePhase.DrivingOff);
                return;
            default:
                _leavingOnLead = true;
                break;
        }

        _leftM = 0;
        _phase = FollowMePhase.Leaving;
        _phaseSeconds = 0;
    }

    /// <summary>Lage des Flugzeugs übernehmen (neue Meldung) oder mit seiner Geschwindigkeit weiterrechnen.</summary>
    private void UpdateAircraft(double dt)
    {
        var latest = _aircraft;
        if (latest is not null && !ReferenceEquals(latest, _aircraftUsed))
        {
            _aircraftUsed = latest;
            _aircraftS = latest.S;
            _aircraftMps = latest.Mps;
        }
        else if (!double.IsNaN(_aircraftS))
        {
            _aircraftS += _aircraftMps * dt;
        }
    }

    private void Next(FollowMePhase phase)
    {
        _phase = phase;
        _phaseSeconds = 0;
        DistanceM = 0;
    }

    private bool AtEnd(TaxiPath path) => DistanceM >= path.LengthM - 0.05 || (SpeedMps < 0.1 && path.LengthM - DistanceM < 0.5 && _phase != FollowMePhase.InPosition && _phase != FollowMePhase.Leading);

    /// <summary>Nächste gekreuzte Bahn beim Vorwegfahren, null = keine mehr.</summary>
    private FollowMeCrossing? NextLeadCrossing()
    {
        var done = _crossingsDone;
        return done < _plan.Crossings.Count ? _plan.Crossings[done] : null;
    }

    /// <summary>Nächste gekreuzte Bahn auf dem Weg entgegen, null = keine mehr.</summary>
    private FollowMeCrossing? NextApproachCrossing()
    {
        var done = _approachCrossingsDone;
        return done < _plan.ApproachCrossings.Count ? _plan.ApproachCrossings[done] : null;
    }

    /// <summary>Stelle auf <see cref="FollowMePlan.Reference"/> als Meter auf <see cref="FollowMePlan.Lead"/>.</summary>
    private double LeadM(double referenceS) => Math.Max(0, referenceS - _plan.LeadOffsetS);

    /// <summary>
    /// Die zuletzt gekreuzte Bahn liegt noch nicht so weit hinter dem Fahrzeug, dass auch das Flugzeug mit seinem kleinsten Abstand
    /// (<see cref="FollowMeProfile.MinGapFactor"/>) von ihr weg wäre.
    /// </summary>
    private bool ClearingRunway()
    {
        var done = _crossingsDone;
        if (done == 0 || done > _plan.Crossings.Count)
        {
            return false;
        }

        var clear = _plan.Crossings[done - 1].ClearM + (_plan.GapM * FollowMeProfile.MinGapFactor) + ClearMarginM;
        return LeadM(clear) > DistanceM;
    }

    /// <summary>Vor der Bahn anhalten und auf die Freigabe warten.</summary>
    private void StartHold(FollowMeCrossing crossing)
    {
        SpeedMps = 0;
        _holdSeconds = 0;
        _crossingClear = false;
        _holdingAt = crossing;
    }

    /// <summary>Vor der Bahn stehen: true = Mindestzeit vorbei und frei, es geht weiter.</summary>
    private bool HoldShort(double dt)
    {
        SpeedMps = 0;
        _holdSeconds += dt;
        if (_holdSeconds < CrossingHoldSeconds || !_crossingClear)
        {
            return false;
        }

        _holdingAt = null;
        _crossingClear = false;
        return true;
    }

    /// <summary>
    /// Auf <paramref name="path"/> weiter: zur Wunschgeschwindigkeit beschleunigen bzw. bremsen, Kurven, das Ende und
    /// <paramref name="stopAt"/> (Meter auf dem Weg) beachten.
    /// </summary>
    private void Move(TaxiPath path, double targetMps, double dt, double? stopAt = null)
    {
        var end = Math.Min(stopAt ?? path.LengthM, path.LengthM);
        var allowed = Math.Min(targetMps, path.AllowedMps(DistanceM, FollowMeProfile.DecelMps2));
        if (stopAt is not null)
        {
            allowed = Math.Min(allowed, Math.Sqrt(2 * FollowMeProfile.DecelMps2 * Math.Max(0, end - DistanceM)));
        }

        SpeedMps = SpeedMps < allowed
            ? Math.Min(allowed, SpeedMps + (FollowMeProfile.AccelMps2 * dt))
            : Math.Max(allowed, SpeedMps - (FollowMeProfile.DecelMps2 * dt));

        // Am Ende bzw. Halt nicht kurz davor stehen bleiben (Bremsgrenze 0): kriechen
        if (SpeedMps <= 0 && targetMps > 0 && end - DistanceM > 0.05)
        {
            SpeedMps = Math.Min(0.2, end - DistanceM);
        }

        DistanceM = Math.Min(end, DistanceM + (SpeedMps * dt));
        var (east, north, heading) = path.At(DistanceM);
        (EastM, NorthM, HeadingDeg) = (east, north, heading);
    }

    private sealed record AircraftProgress(double S, double Mps);
}
