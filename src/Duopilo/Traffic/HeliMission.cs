using Duopilo.FlightPlan;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Abschnitt eines Einsatzflugs (<see cref="HeliMission"/>).</summary>
public enum HeliMissionPhase
{
    /// <summary>Anlassen, Abheben und Steigen auf dem Kurs zum nächsten Stopp (<see cref="HeliKinematics.Departure"/>).</summary>
    Departure,

    /// <summary>Anflug auf den nächsten Stopp (Reiseflug, Sinkflug, Schweben, Absetzen).</summary>
    Approach,

    /// <summary>Am Stopp gelandet, Wartezeit läuft.</summary>
    Waiting,

    /// <summary>Am letzten Stopp gelandet.</summary>
    Done,
}

/// <summary>
/// Einsatzflug eines Helis über mehrere Stopps (H3), z. B. Station → Einsatzort → Klinik → Station: am ersten Stopp anlassen und
/// abheben (<see cref="HeliKinematics.Departure"/>), geradeaus zum nächsten Stopp steigen; sobald der Heli auf Reisehöhe ist oder den
/// Anflugweg des Stopps erreicht (kurze Strecken), übernimmt im selben Frame der Anflug (<see cref="HeliKinematics"/>) mit Lage,
/// Höhe, Fahrt und Nase von dort. Nach der Landung <see cref="WaitSeconds"/> warten, dann wieder anlassen und weiter; am letzten
/// Stopp ist er fertig (<see cref="HeliMissionPhase.Done"/>). Rein rechnerisch – Ereignisse, Lichter und Rotor macht der Dienst.
/// </summary>
public sealed class HeliMission
{
    private readonly HeliProfile _profile;
    private readonly double _cruiseHeightFt;

    public HeliMission(IReadOnlyList<HeliPad> stops, HeliProfile profile, double cruiseHeightFt, double waitSeconds, double startGroundFt, double startHeadingDeg, double cgHeightFt, double groundPitchDeg)
    {
        if (stops.Count < 2)
        {
            throw new ArgumentException("A mission needs at least two stops.", nameof(stops));
        }

        Stops = stops;
        _profile = profile;
        _cruiseHeightFt = cruiseHeightFt;
        WaitSeconds = Math.Max(0, waitSeconds);
        CgHeightFt = cgHeightFt;
        GroundPitchDeg = groundPitchDeg;
        Leg = 1;
        Current = HeliKinematics.Departure(LegFrame(stops[0], stops[1], startGroundFt), profile, cruiseHeightFt, startHeadingDeg, startGroundFt, cgHeightFt, groundPitchDeg);
    }

    public IReadOnlyList<HeliPad> Stops { get; }

    /// <summary>Wartezeit an jedem Stopp zwischen dem ersten und dem letzten.</summary>
    public double WaitSeconds { get; }

    public double CgHeightFt { get; }

    public double GroundPitchDeg { get; }

    /// <summary>Index des Stopps, zu dem es gerade geht (bzw. an dem er gerade steht), ab 1.</summary>
    public int Leg { get; private set; }

    public HeliMissionPhase Phase { get; private set; } = HeliMissionPhase.Departure;

    /// <summary>Die Kinematik des Abschnitts (Abflug oder Anflug); wechselt beim Übergang im selben Frame.</summary>
    public HeliKinematics Current { get; private set; }

    /// <summary>Restliche Wartezeit am Stopp (nur in <see cref="HeliMissionPhase.Waiting"/>).</summary>
    public double WaitRemainingSeconds { get; private set; }

    /// <summary>Der Stopp, von dem er gerade abfliegt (Abflug) bzw. den er anfliegt oder an dem er steht.</summary>
    public HeliPad CurrentPad => Phase == HeliMissionPhase.Departure ? Stops[Leg - 1] : Stops[Leg];

    public HeliPad Target => Stops[Leg];

    /// <summary>Restweg zum nächsten Stopp (m).</summary>
    public double RemainingM
    {
        get
        {
            var pose = Current.Pose();
            return RouteMath.DistanceNm(pose.Latitude, pose.Longitude, Target.Latitude, Target.Longitude) * RunwayEnd.MetersPerNm;
        }
    }

    public AiPose Pose() => Current.Pose();

    public void Advance(double dt)
    {
        Current.Advance(dt);
        switch (Phase)
        {
            case HeliMissionPhase.Departure when Current.Phase is HeliPhase.Transition or HeliPhase.Climb or HeliPhase.Cruise:
                TryStartApproach();
                break;
            case HeliMissionPhase.Approach when Current.Phase == HeliPhase.Landed:
                if (Leg >= Stops.Count - 1)
                {
                    Phase = HeliMissionPhase.Done;
                }
                else
                {
                    Phase = HeliMissionPhase.Waiting;
                    WaitRemainingSeconds = WaitSeconds;
                }

                break;
            case HeliMissionPhase.Waiting:
                WaitRemainingSeconds = Math.Max(0, WaitRemainingSeconds - dt);
                if (WaitRemainingSeconds <= 0)
                {
                    var here = Stops[Leg];
                    var ground = Current.GroundFt;
                    Leg++;
                    Current = HeliKinematics.Departure(LegFrame(here, Stops[Leg], ground), _profile, _cruiseHeightFt, Current.HeadingTrueDeg, ground, CgHeightFt, Current.GroundPitchDeg);
                    Phase = HeliMissionPhase.Departure;
                }

                break;
        }
    }

    /// <summary>
    /// Vom Abflug in den Anflug: auf Reisehöhe, oder sobald die Höhe über dem Ziel den Anflugweg (Profil des Anflugs) erreicht. Die
    /// Höhe bleibt absolut gleich – über einem Stopp mit gemessenem Boden gilt der Boden unter dem Heli, sonst die Höhe des Pads.
    /// </summary>
    private void TryStartApproach()
    {
        var departure = Current;
        var pose = departure.Pose();
        var target = Target;
        var remaining = RouteMath.DistanceNm(pose.Latitude, pose.Longitude, target.Latitude, target.Longitude) * RunwayEnd.MetersPerNm;
        var ground = target.Measured ? departure.GroundFt : target.ElevationFt;
        var height = departure.GroundFt + departure.HeightFt - ground;
        var profile = Math.Min(_cruiseHeightFt, _profile.HoverFt + (remaining * Math.Tan(_profile.ApproachAngleDeg * Math.PI / 180) * RunwayEnd.FeetPerMeter));
        if (departure.Phase != HeliPhase.Cruise && height < profile - 1)
        {
            return;
        }

        var course = RouteMath.BearingDeg(pose.Latitude, pose.Longitude, target.Latitude, target.Longitude);
        var frame = new RunwayEnd(target.Airport, target.Label, target.Latitude, target.Longitude, ground, TrafficMath.Normalize(course), target.LengthM, target.WidthM);
        Current = new HeliKinematics(frame, _profile, remaining, height, departure.SpeedKt, target.HeadingDeg, ground, CgHeightFt, departure.PitchDeg)
        {
            GroundPitchDeg = departure.GroundPitchDeg,
            // Gemessener Boden nur bei einem Punkt weiterführen – ein Pad der Datenbank misst der Dienst erst über dem Pad
            GroundTargetFt = target.Measured ? departure.GroundTargetFt : null,
        };
        Phase = HeliMissionPhase.Approach;
    }

    /// <summary>Rahmen eines Abschnitts: Abflug-Pad als Schwelle (Höhe <paramref name="groundFt"/>), Kurs zum nächsten Stopp.</summary>
    public static RunwayEnd LegFrame(HeliPad from, HeliPad to, double groundFt) =>
        new(from.Airport, from.Label, from.Latitude, from.Longitude, groundFt,
            TrafficMath.Normalize(RouteMath.BearingDeg(from.Latitude, from.Longitude, to.Latitude, to.Longitude)), from.LengthM, from.WidthM);
}
