namespace Kopilot.Atc;

/// <summary>Verweis auf eine Station, aus dem sich die Station nach einem Neustart wieder bauen lässt.</summary>
/// <param name="Key">z. B. "EDDF/tower" oder "FIR/CZQX".</param>
/// <param name="Facility">Flughafen oder FIR (ICAO), leer bei einer unbekannten FIR.</param>
public sealed record AtcStationRef(string Key, AtcStationType Type, string Facility)
{
    public static AtcStationRef Of(AtcStation station) => new(station.Key, station.Type, station.Facility);
}

/// <summary>
/// Gesicherter Stand einer ATC-Sitzung (atc-session.json). Damit geht nach einem Absturz oder Neustart von Kopilot
/// nichts verloren: Station, Phase, freigegebene Höhe, Oceanic-Freigabe, Werte von Hand (Gate …), Stimmen und Funkverlauf.
/// Offene Readbacks lassen sich nicht sichern (dahinter steckt Code) – sie gelten nach dem Wiederherstellen als erledigt.
/// </summary>
public sealed record AtcSessionSnapshot
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public DateTimeOffset SavedUtc { get; init; }

    /// <summary>Kennung des Flugs (SimBrief-Request-ID) – nur derselbe Flug wird wiederhergestellt.</summary>
    public string FlightKey { get; init; } = "";

    public string Callsign { get; init; } = "";

    public string Squawk { get; init; } = "";

    public AtcPhase Phase { get; init; }

    public AtcStep Step { get; init; }

    public AtcStationRef? Station { get; init; }

    public AtcStationRef? LastAtcStation { get; init; }

    public int? ClearedAltitudeFt { get; init; }

    public OceanicClearanceState Oceanic { get; init; }

    public Dictionary<string, string> Overrides { get; init; } = [];

    public Dictionary<string, string> Voices { get; init; } = [];

    public List<AtcTransmission> Transcript { get; init; } = [];

    public long NextId { get; init; }

    public double? AlongTrackNm { get; init; }

    public int? LegHint { get; init; }

    public AtcFirSegment? FirSegment { get; init; }

    public int ReportIndex { get; init; }

    public bool ReportDue { get; init; }

    public bool DirectIssued { get; init; }

    public bool DescentIssued { get; init; }

    public bool ApproachCleared { get; init; }

    public bool LandingCleared { get; init; }

    public bool TaxiMoved { get; init; }

    public bool AirborneInPhase { get; init; }

    public bool MovedSinceLineUp { get; init; }

    public DateTimeOffset StepSinceUtc { get; init; }

    public DateTimeOffset? TaxiClearedAtUtc { get; init; }

    public DateTimeOffset? LineUpAtUtc { get; init; }

    /// <summary>Beim Sichern offenes Readback (Nachrichten-ID) – nur zur Information.</summary>
    public string? PendingReadback { get; init; }
}

public sealed partial class AtcSession
{
    /// <summary>Aktueller Stand zum Sichern.</summary>
    public AtcSessionSnapshot Snapshot(DateTimeOffset now) => new()
    {
        SavedUtc = now,
        FlightKey = Plan.FlightKey,
        Callsign = Plan.Callsign,
        Squawk = Squawk,
        Phase = Phase,
        Step = Step,
        Station = AtcStationRef.Of(Station),
        LastAtcStation = _lastAtcStation is { } last ? AtcStationRef.Of(last) : null,
        ClearedAltitudeFt = ClearedAltitudeFt,
        Oceanic = Oceanic,
        Overrides = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase),
        Voices = new Dictionary<string, string>(_stations.AssignedVoices, StringComparer.OrdinalIgnoreCase),
        Transcript = [.. _transcript],
        NextId = _nextId,
        AlongTrackNm = _alongNm,
        LegHint = _legHint,
        FirSegment = _firSegment,
        ReportIndex = _reportIndex,
        ReportDue = _reportDue,
        DirectIssued = _directIssued,
        DescentIssued = _descentIssued,
        ApproachCleared = _approachCleared,
        LandingCleared = _landingCleared,
        TaxiMoved = _taxiMoved,
        AirborneInPhase = _airborneInPhase,
        MovedSinceLineUp = _movedSinceLineUp,
        StepSinceUtc = _stepSince,
        TaxiClearedAtUtc = _taxiClearedAt,
        LineUpAtUtc = _lineUpAt,
        PendingReadback = (PendingReadback ?? _oceanicReadback)?.MessageId,
    };

    /// <summary>
    /// Kurzer Fingerabdruck der Werte, die eine Sicherung lohnen (ohne Streckenposition) – ändert er sich, wird gesichert.
    /// </summary>
    public string StateFingerprint() =>
        string.Join(
            '|',
            Phase,
            Step,
            Station.Key,
            ClearedAltitudeFt,
            Oceanic,
            _nextId,
            _reportIndex,
            _reportDue,
            _firSegment?.Fir,
            _directIssued,
            _descentIssued,
            _approachCleared,
            _landingCleared,
            string.Join(',', _overrides.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Select(o => $"{o.Key}={o.Value}")));

    /// <summary>
    /// Sitzung aus einer Sicherung desselben Flugs fortsetzen (statt <see cref="Start"/>). Offene Readbacks gelten als erledigt.
    /// Rückgabe: Hinweis fürs Log (null = nichts Besonderes).
    /// </summary>
    public string? Restore(AtcSessionSnapshot s, AtcInput input)
    {
        _input = input;
        _stations.RestoreVoices(s.Voices);
        foreach (var (key, value) in s.Overrides)
        {
            SetOverride(key, value);
        }

        _transcript.Clear();
        _transcript.AddRange(s.Transcript.TakeLast(TranscriptLimit));
        _nextId = Math.Max(s.NextId, _transcript.Count > 0 ? _transcript[^1].Id : 0);
        _lastAtc = _transcript.LastOrDefault(t => t.From == "atc");

        _legHint = s.LegHint;
        _alongNm = s.AlongTrackNm;

        Phase = s.Phase;
        Step = StepAfterReadback(s.Step);
        _stepSince = s.StepSinceUtc == default ? input.TimeUtc : s.StepSinceUtc;
        _movedInStep = false;
        _stoppedSince = null;
        _movingSince = null;
        if (s.Station is { } station)
        {
            Station = ResolveStation(station);
        }

        _lastAtcStation = s.LastAtcStation is { } lastStation ? ResolveStation(lastStation) : null;
        ClearedAltitudeFt = s.ClearedAltitudeFt;
        Oceanic = Plan.Oceanic is null
            ? OceanicClearanceState.NotRequired
            : s.Oceanic == OceanicClearanceState.ReadbackPending ? OceanicClearanceState.Due : s.Oceanic;

        _firSegment = s.FirSegment is { } fir
            ? Plan.Firs.FirstOrDefault(f => f.Fir == fir.Fir && Math.Abs(f.StartNm - fir.StartNm) < 1) ?? Plan.FirAt(s.AlongTrackNm ?? fir.StartNm)
            : null;
        _reportIndex = s.ReportIndex;
        _reportDue = s.ReportDue;
        _directIssued = s.DirectIssued;
        _descentIssued = s.DescentIssued;
        _approachCleared = s.ApproachCleared;
        _landingCleared = s.LandingCleared;
        _taxiMoved = s.TaxiMoved;
        _airborneInPhase = s.AirborneInPhase;
        _movedSinceLineUp = s.MovedSinceLineUp;
        _taxiClearedAt = s.TaxiClearedAtUtc;
        _lineUpAt = s.LineUpAtUtc;

        PendingReadback = null;
        _oceanicReadback = null;
        _ackPending = false;
        _ackFrequency = null;

        UpdateRoute(input);
        UpdateOceanicClearance(input);

        return s.PendingReadback is { } pending
            ? $"Readback {pending} war beim Sichern noch offen – gilt als erledigt ({s.Step} → {Step})."
            : null;
    }

    /// <summary>Schritt nach einem korrekten Readback – so wie die Rückrufe der Readbacks ihn setzen.</summary>
    public static AtcStep StepAfterReadback(AtcStep step) => step switch
    {
        // Die Freigabe selbst kann nicht als gelesen gelten: Delivery wiederholt sie auf die nächste Anfrage
        AtcStep.ClearanceReadback => AtcStep.ClearanceRequest,
        AtcStep.TaxiReadback => AtcStep.Taxiing,
        AtcStep.LineUpReadback => AtcStep.LinedUp,
        AtcStep.TakeoffReadback => AtcStep.TakeoffRoll,
        AtcStep.DepartureReadback or AtcStep.DirectReadback => AtcStep.DepartureClimb,
        AtcStep.CenterReadback => AtcStep.CenterCruise,
        AtcStep.ApproachReadback => AtcStep.ApproachVectoring,
        AtcStep.ApproachClearanceReadback => AtcStep.ApproachEstablished,
        AtcStep.LandingReadback => AtcStep.Landing,
        AtcStep.GateReadback => AtcStep.TaxiIn,
        _ => step,
    };

    private AtcStation ResolveStation(AtcStationRef r)
    {
        if (r.Type is AtcStationType.Center or AtcStationType.Oceanic)
        {
            return _stations.Fir(r.Facility.Length > 0 ? r.Facility : null, r.Type == AtcStationType.Oceanic);
        }

        if (string.Equals(r.Facility, OriginIcao, StringComparison.OrdinalIgnoreCase))
        {
            return OriginStation(r.Type);
        }

        return string.Equals(r.Facility, DestIcao, StringComparison.OrdinalIgnoreCase)
            ? DestStation(r.Type)
            : _stations.Airport(r.Facility, null, r.Type);
    }
}
