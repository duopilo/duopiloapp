using Kopilot.Aircraft;
using Kopilot.Atc;

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
