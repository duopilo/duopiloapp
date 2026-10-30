namespace Kopilot.Atc;

/// <summary>Zuständige Stelle / Flugabschnitt.</summary>
public enum AtcPhase
{
    Inactive,
    Delivery,
    GroundDeparture,
    TowerDeparture,
    Departure,
    Center,
    Oceanic,
    Approach,
    TowerArrival,
    GroundArrival,
    Done,
}

/// <summary>Schritt innerhalb der Phase: worauf ATC gerade wartet.</summary>
public enum AtcStep
{
    None,

    ClearanceRequest,
    ClearanceReadback,

    PushRequest,
    TaxiRequest,
    TaxiReadback,
    Taxiing,

    ReadyForDeparture,
    LineUpReadback,
    LinedUp,
    TakeoffReadback,
    TakeoffRoll,

    DepartureContact,
    DepartureReadback,
    DepartureClimb,
    DirectReadback,

    CenterContact,
    CenterReadback,
    CenterCruise,

    OceanicReports,

    ApproachContact,
    ApproachReadback,
    ApproachVectoring,
    ApproachClearanceReadback,
    ApproachEstablished,

    LandingContact,
    LandingReadback,
    Landing,

    ArrivalContact,
    GateReadback,
    TaxiIn,

    Done,
}

/// <summary>Stand der Oceanic-Freigabe.</summary>
public enum OceanicClearanceState
{
    /// <summary>Route ohne Oceanic-Teil – Oceanic wird übersprungen.</summary>
    NotRequired,

    NotYetDue,

    /// <summary>Etwa 30 Minuten vor dem Eintrittspunkt: Pilot soll die Freigabe anfordern.</summary>
    Due,

    ReadbackPending,

    Cleared,
}

/// <summary>Was die Zustandsmaschine vom Flugzeug braucht (aus SimConnect). Höhen in ft, Geschwindigkeiten in kt.</summary>
public sealed record AtcInput
{
    public DateTimeOffset TimeUtc { get; init; }

    public bool? OnGround { get; init; }

    public double? AltitudeFt { get; init; }

    public double? AglFt { get; init; }

    public double? GroundSpeedKt { get; init; }

    public double? IasKt { get; init; }

    public double? VerticalSpeedFpm { get; init; }

    public double? Lat { get; init; }

    public double? Lon { get; init; }

    public double? HeadingMagDeg { get; init; }

    public double? WindDirectionDeg { get; init; }

    public double? WindSpeedKt { get; init; }

    /// <summary>Luftdruck auf Meereshöhe an der Flugzeugposition (hPa).</summary>
    public double? QnhHpa { get; init; }

    /// <summary>Sim-Zeit UTC in Sekunden seit Mitternacht.</summary>
    public double? ZuluTimeSec { get; init; }

    public bool? ParkingBrake { get; init; }

    /// <summary>Vom MSFS-ATC zugewiesene Bahn, falls der Plan keine hat.</summary>
    public string? AssignedRunway { get; init; }

    /// <summary>Eingestellter Transpondercode (TRANSPONDER CODE, vier Oktalziffern), null = unbekannt.</summary>
    public string? TransponderCode { get; init; }

    /// <summary>Aktive Frequenz von COM1 (COM ACTIVE FREQUENCY:1, MHz), null = unbekannt.</summary>
    public double? Com1ActiveMhz { get; init; }
}

/// <summary>Was Kopilot überwacht: Transpondercode oder COM1-Frequenz.</summary>
public enum AtcDeviationKind
{
    Squawk,
    Frequency,
}

/// <summary>
/// Abweichung vom Zugewiesenen, die ATC schon angesprochen hat ("check squawk, should be …"). Kopilot setzt nie selbst
/// zurück – <paramref name="Actions"/> sind die Eingaben, die Claude dem Piloten anbieten kann (prepare_action).
/// </summary>
/// <param name="Expected">Zugewiesener Code bzw. Frequenz ("2340", "132.6").</param>
/// <param name="Actual">Eingestellter Code bzw. Frequenz.</param>
/// <param name="Station">Station, deren Frequenz erwartet wird (nur Frequenz).</param>
/// <param name="Calls">Wie oft ATC schon darauf hingewiesen hat.</param>
public sealed record AtcDeviation(
    AtcDeviationKind Kind,
    string Expected,
    string Actual,
    string? Station,
    DateTimeOffset SinceUtc,
    int Calls,
    IReadOnlyList<AtcSuggestedAction> Actions);

/// <summary>Eingabe für prepare_action ("xpdr.code" mit "2340", "com1.standby" mit "132.600", dann "com1.swap").</summary>
public sealed record AtcSuggestedAction(string Action, string? Value);

public enum AtcDeviationChange
{
    /// <summary>ATC hat die Abweichung angesprochen (Hinweis an Claude: Zurücksetzen anbieten).</summary>
    Raised,

    /// <summary>Wieder wie zugewiesen.</summary>
    Cleared,

    /// <summary>Squawk aus dem Transponder übernommen (Einstellung SquawkFromSim).</summary>
    Adopted,
}

/// <summary>Änderung an einer Abweichung – der <see cref="AtcService"/> macht daraus Meldungen im AlertBus.</summary>
/// <param name="Previous">Bei <see cref="AtcDeviationChange.Adopted"/>: der bisher zugewiesene Squawk.</param>
public sealed record AtcDeviationEvent(AtcDeviationKind Kind, AtcDeviationChange Change, string Expected, string? Actual, string? Station, string? Previous = null);

/// <summary>Ein Funkspruch im Verlauf (ATC oder Pilot).</summary>
public sealed record AtcTransmission(
    long Id,
    DateTimeOffset TimeUtc,
    string From,
    string StationKey,
    string StationName,
    string Frequency,
    string Voice,
    string MessageId,
    string Text,
    string Spoken);

/// <summary>Was die Sitzung von außen bekommt: Frequenzen, Rollrouten, Stimmen.</summary>
/// <param name="SquawkFromSim">
/// Squawk aus dem Transponder übernehmen, wenn ihn das MSFS-ATC (auch stumm geschaltet) im Flug ändert – statt "check squawk".
/// </param>
public sealed record AtcSessionContext(
    Func<string, IReadOnlyList<AirportFrequency>?> FacilityFrequencies,
    ITaxiRouteProvider TaxiRoutes,
    string? CalloutVoice = null,
    IReadOnlyCollection<string>? InstalledVoices = null,
    bool SquawkFromSim = false);

/// <summary>Offenes Readback: welche Werte der Pilot noch zurücklesen muss und was danach passiert.</summary>
public sealed record AtcPendingReadback(string MessageId, AtcStation Station, IReadOnlyList<ReadbackItem> Items, Action<List<AtcTransmission>, AtcInput> OnCorrect);
