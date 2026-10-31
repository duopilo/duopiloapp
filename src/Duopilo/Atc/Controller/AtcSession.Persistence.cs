using Duopilo.Localization;

namespace Duopilo.Atc;

/// <summary>Verweis auf eine Station, aus dem sich die Station nach einem Neustart wieder bauen lässt.</summary>
/// <param name="Key">z. B. "EDDF/tower", "FIR/CZQX" oder "FIR/CZUL/MC" (Kontrollposition aus den Sektordaten).</param>
/// <param name="Facility">Flughafen oder FIR (ICAO), leer bei einer unbekannten FIR.</param>
public sealed record AtcStationRef(string Key, AtcStationType Type, string Facility)
{
    public static AtcStationRef Of(AtcStation station) => new(station.Key, station.Type, station.Facility);
}

/// <summary>
/// Gesicherter Stand einer ATC-Sitzung (atc-session.json). Damit geht nach einem Absturz oder Neustart von Duopilo
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

    /// <summary>"duopilo" (bis 0.9.x "kopilot") oder "sim" (Squawk aus dem Transponder übernommen). Fehlt in älteren Sicherungen.</summary>
    public string? SquawkSource { get; init; }

    /// <summary>Stufensteigflüge des OFP vor diesem Index sind erledigt.</summary>
    public int StepClimbIndex { get; init; }

    public int LevelRequests { get; init; }

    /// <summary>Abgelehnte Höhenanfrage, die ATC später freigibt.</summary>
    public AtcExpectedLevel? ExpectedLevel { get; init; }

    /// <summary>
    /// Bisherige Haltezeit der Sim-Uhr in Sekunden (Pausen, Menü, keine Verbindung) – die Zeiten oben sind Sim-Zeit. Fehlt in
    /// Sicherungen bis 0.10.0-beta.1.
    /// </summary>
    public double? SimClockStoppedSec { get; init; }

    /// <summary>Laufender Notfall (Mayday, Pan Pan), null = keiner.</summary>
    public AtcEmergency? Emergency { get; init; }
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
        SquawkSource = SquawkSource,
        StepClimbIndex = _stepClimbIndex,
        LevelRequests = _levelRequests,
        ExpectedLevel = _expectedLevel,
        Emergency = Emergency,
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
            Squawk,
            _stepClimbIndex,
            _expectedLevel?.AltitudeFt,
            Oceanic,
            _nextId,
            _reportIndex,
            _reportDue,
            _firSegment?.Fir,
            _directIssued,
            _descentIssued,
            _approachCleared,
            _landingCleared,
            Emergency is { } emergency ? $"{emergency.Kind}:{emergency.DetailsAsked}:{emergency.Target}" : "",
            string.Join(',', _overrides.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Select(o => $"{o.Key}={o.Value}")));

    /// <summary>
    /// Passt eine Sicherung desselben Plans zur Lage? Nein, wenn das Flugzeug am Startflughafen am Boden steht, die Sicherung aber
    /// schon abgeflogen ist – ein neuer Versuch desselben Plans (Testflug 31.10.: am Gate in Düsseldorf kam „Cologne Approach“
    /// aus der Sicherung vom Vorabend). Ebenso, wenn die Sicherung noch am Start am Boden ist, das Flugzeug aber anderswo am Boden
    /// steht. Rückgabe: Grund fürs Log (englisch), null = passt oder nicht zu entscheiden (Position unbekannt, in der Luft,
    /// Rundflug mit gleichem Start und Ziel).
    /// </summary>
    public static string? SnapshotMismatch(AtcSessionSnapshot s, AtcPlan plan, AtcInput input)
    {
        var origin = plan.Ofp.Origin;
        if (input.OnGround != true || (input.GroundSpeedKt ?? 0) > 40 || input.Lat is not { } lat || input.Lon is not { } lon
            || origin.Lat is not { } olat || origin.Lon is not { } olon
            || string.Equals(origin.Icao, plan.Ofp.Destination.Icao, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fromOrigin = Duopilo.FlightPlan.RouteMath.DistanceNm(lat, lon, olat, olon);
        var departingOnGround = s.Phase is AtcPhase.Delivery or AtcPhase.GroundDeparture
            || (s.Phase == AtcPhase.TowerDeparture && s.Step != AtcStep.TakeoffRoll);
        if (fromOrigin <= 5 && !departingOnGround)
        {
            return $"on the ground at {origin.Icao}, but the backup is already in phase {s.Phase}";
        }

        return fromOrigin > 10 && departingOnGround
            ? $"on the ground {fromOrigin:0} NM from {origin.Icao}, but the backup is still in phase {s.Phase}"
            : null;
    }

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

        // Squawk: aus der Sicherung (auch einer vom MSFS-ATC übernommenen), sonst der feste des Flugs
        if (IsValidSquawk(s.Squawk))
        {
            Squawk = s.Squawk;
            SquawkSource = s.SquawkSource is "sim" ? "sim" : SquawkSourceOwn;
        }

        _stepClimbIndex = Math.Clamp(s.StepClimbIndex, 0, Plan.StepClimbs.Count);
        _levelRequests = Math.Max(0, s.LevelRequests);
        _expectedLevel = s.ExpectedLevel;
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
        RestoreEmergency(s.Emergency);

        PendingReadback = null;
        _oceanicReadback = null;
        _ackPending = false;
        _ackFrequency = null;

        // Der Pilot war auf der Frequenz der gesicherten Station – nach einer kurzen Schonfrist wird wieder geprüft
        StationChanged(input.TimeUtc, handoff: true);

        UpdateRoute(input);
        UpdateOceanicClearance(input);

        return s.PendingReadback is { } pending
            ? Loc.T("atc.session.readbackOpen", pending, s.Step, Step)
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
            // Station einer Kontrollposition ("FIR/CZUL/MC"): aus dem passenden Routenabschnitt neu bauen – Rufname und
            // Frequenz aus den Sektordaten, die Stimme kommt über den Schlüssel aus der Sicherung
            var oceanic = r.Type == AtcStationType.Oceanic;
            if (Plan.Firs.FirstOrDefault(f => f.Oceanic == oceanic
                                               && f.Sector is { Length: > 0 } sector
                                               && string.Equals(AtcStations.SectorKey(f.Fir, sector), r.Key, StringComparison.OrdinalIgnoreCase)) is { } segment)
            {
                return _stations.Segment(segment);
            }

            return _stations.Fir(r.Facility.Length > 0 ? r.Facility : null, oceanic);
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
