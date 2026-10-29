using Kopilot.Aircraft;
using Kopilot.Atc;
using Kopilot.Traffic;

namespace Kopilot.Monitoring;

/// <summary>
/// Momentaufnahme aller Werte, die das Monitoring braucht. Vom Sim unabhängig, damit die Regeln testbar sind.
/// Bank: rechts positiv. Höhen in ft, Geschwindigkeiten in kt, V/S in ft/min.
/// </summary>
public sealed record FlightSnapshot
{
    public DateTimeOffset TimeUtc { get; init; }

    public bool Connected { get; init; }

    /// <summary>false = Hauptmenü/Ladebildschirm.</summary>
    public bool? FlightRunning { get; init; }

    public bool Paused { get; init; }

    public string? PauseKind { get; init; }

    /// <summary>Alter der schnellen Werte in Sekunden.</summary>
    public double? DataAgeSec { get; init; }

    /// <summary>Kennung des geladenen Flugzeugs (Titel + Pfad), um Flugzeugwechsel zu erkennen.</summary>
    public string? AircraftKey { get; init; }

    public bool? OnGround { get; init; }

    public double? IasKt { get; init; }

    public double? GroundSpeedKt { get; init; }

    public double? VerticalSpeedFpm { get; init; }

    public double? AltitudeFt { get; init; }

    public double? PressureAltitudeFt { get; init; }

    public double? AglFt { get; init; }

    public double? RadioAltFt { get; init; }

    public double? BankRightDeg { get; init; }

    public double? FlapsIndex { get; init; }

    /// <summary>Klappenstellung im Klartext aus dem Profil ("1+F", "FULL"), null ohne Profil.</summary>
    public string? FlapsLabel { get; init; }

    public bool? SpoilersArmed { get; init; }

    public double? GearExtendedPct { get; init; }

    public bool? GearHandleDown { get; init; }

    public bool? ApEngaged { get; init; }

    public double? SelectedAltitudeFt { get; init; }

    public double? SpeedTargetKt { get; init; }

    public double? V1Kt { get; init; }

    public double? VrKt { get; init; }

    /// <summary>Ground Spoiler ausgefahren (null = unbekannt).</summary>
    public bool? GroundSpoilersOut { get; init; }

    /// <summary>Größte Schubumkehr-Stellung aller Triebwerke in Prozent.</summary>
    public double? ReverserPct { get; init; }

    /// <summary>Autobrake DECEL-Licht (null = Flugzeug liefert es nicht).</summary>
    public bool? DecelLight { get; init; }

    /// <summary>Transition Altitude am Start (ft, QNH).</summary>
    public double? TransitionAltitudeFt { get; init; }

    /// <summary>Transition Level am Ziel (ft, Druckhöhe – FL70 = 7000).</summary>
    public double? TransitionLevelFt { get; init; }

    /// <summary>Entscheidungshöhe (Radiohöhe) für Minimum-Ansagen.</summary>
    public double? DecisionHeightFt { get; init; }

    /// <summary>Minimum Descent Altitude (barometrisch) für Minimum-Ansagen ohne DH.</summary>
    public double? MinimumDescentAltitudeFt { get; init; }

    /// <summary>FMS-Meldung T/D REACHED (null, wenn das Flugzeug sie nicht liefert).</summary>
    public bool? TdReached { get; init; }

    /// <summary>Entfernung zum Top of Descent laut SimBrief-Plan (nm), null wenn unbekannt oder schon vorbei.</summary>
    public double? PlanTodDistanceNm { get; init; }

    /// <summary>FMA-Anzeige für das FMA-Log.</summary>
    public FmaState? Fma { get; init; }

    public bool? MasterWarning { get; init; }

    public bool? MasterCaution { get; init; }

    /// <summary>ECAM-Titelzeilen links, in Anzeigereihenfolge.</summary>
    public IReadOnlyList<string> EcamTitles { get; init; } = [];

    public bool? StallWarning { get; init; }

    public bool? OverspeedWarning { get; init; }

    public AircraftLimits Limits { get; init; } = new();

    public AssignedRunway? AssignedRunway { get; init; }

    /// <summary>Verkehrslage der letzten Hintergrundabfrage (null = keine, z. B. am Boden oder Regel aus).</summary>
    public TrafficPicture? Traffic { get; init; }

    public bool Airborne => OnGround == false;

    public bool GearDown => GearExtendedPct is > 90 || (GearExtendedPct is null && GearHandleDown == true);

    public bool GearNotUp => GearExtendedPct is > 5 || (GearExtendedPct is null && GearHandleDown == true);
}

/// <summary>Was die FMA gerade zeigt.</summary>
public sealed record FmaState(
    string? Thrust,
    string? Vertical,
    string? VerticalArmed,
    string? Lateral,
    string? LateralArmed,
    string? Autopilot,
    bool? FlightDirector,
    string? Autothrust);
