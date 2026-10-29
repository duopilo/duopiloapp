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
}

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
public sealed record AtcSessionContext(
    Func<string, IReadOnlyList<AirportFrequency>?> FacilityFrequencies,
    ITaxiRouteProvider TaxiRoutes,
    string? CalloutVoice = null,
    IReadOnlyCollection<string>? InstalledVoices = null);

/// <summary>Offenes Readback: welche Werte der Pilot noch zurücklesen muss und was danach passiert.</summary>
public sealed record AtcPendingReadback(string MessageId, AtcStation Station, IReadOnlyList<ReadbackItem> Items, Action<List<AtcTransmission>, AtcInput> OnCorrect);
