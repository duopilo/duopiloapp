namespace Kopilot.Atc;

/// <summary>
/// ATC als Zustandsmaschine mit festen Textvorlagen (kein LLM): Delivery → Ground → Tower → Departure → Center
/// (→ Oceanic) → Approach → Tower → Ground. Übergänge kommen aus Pilotensprüchen (Anfragen, Readbacks) und aus dem
/// Flugzustand (Höhe, Position, FIR-Grenzen, Entfernung zum Ziel). Readbacks werden gegen die Werte der Freigabe geprüft.
/// Nicht threadsicher – der <see cref="AtcService"/> sperrt.
/// </summary>
public sealed partial class AtcSession
{
    private const int TranscriptLimit = 60;

    // Wörter, die einen neuen Anruf kennzeichnen (Anfrage, Erstkontakt, Positionsmeldung) – keine bloße Quittung
    private static readonly string[] NewCallWords =
        ["request", "requesting", "ready", "passing", "climbing", "descending", "maintaining", "position", "established", "vacated", "information"];

    private readonly AtcTemplates _t;
    private readonly AtcSessionContext _context;
    private readonly AtcStations _stations;
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AtcTransmission> _transcript = [];

    private long _nextId;
    private AtcInput _input = new();
    private AtcTransmission? _lastAtc;
    private AtcStation? _lastAtcStation;

    // Fortschritt auf der Route
    private int? _legHint;
    private double? _alongNm;

    // Bewegung
    private DateTimeOffset _stepSince;
    private DateTimeOffset? _stoppedSince;
    private DateTimeOffset? _movingSince;
    private bool _movedInStep;
    private bool _taxiMoved;
    private DateTimeOffset? _taxiClearedAt;
    private DateTimeOffset? _lineUpAt;
    private bool _movedSinceLineUp;
    private bool _airborneInPhase;

    // Freigaben
    private bool _directIssued;
    private bool _descentIssued;
    private bool _approachCleared;
    private bool _landingCleared;
    private AtcFirSegment? _firSegment;
    private int _reportIndex;
    private bool _reportDue;
    private AtcPendingReadback? _oceanicReadback;

    // Quittung nach Anweisungen ohne Readback-Pflicht und nach Übergaben
    private bool _ackPending;
    private AtcValue? _ackFrequency;

    public AtcSession(AtcTemplates templates, AtcPlan plan, AtcSessionContext context)
    {
        _t = templates;
        _context = context;
        Plan = plan;
        _stations = new AtcStations(
            templates,
            context.FacilityFrequencies,
            new AtcVoiceAssigner(templates.Voices, plan.FlightKey, context.CalloutVoice, context.InstalledVoices));
        Squawk = GenerateSquawk(plan.FlightKey);
        Oceanic = plan.Oceanic is null ? OceanicClearanceState.NotRequired : OceanicClearanceState.NotYetDue;
        Station = _stations.Airport(OriginIcao, plan.Ofp.Origin.Name, AtcStationType.Delivery);
    }

    public AtcPlan Plan { get; }

    public AtcPhase Phase { get; private set; } = AtcPhase.Inactive;

    public AtcStep Step { get; private set; } = AtcStep.None;

    public AtcStation Station { get; private set; }

    public string Squawk { get; }

    /// <summary>Zuletzt freigegebene Höhe (ft).</summary>
    public int? ClearedAltitudeFt { get; private set; }

    public OceanicClearanceState Oceanic { get; private set; }

    public AtcPendingReadback? PendingReadback { get; private set; }

    public AtcPendingReadback? PendingOceanicReadback => _oceanicReadback;

    public IReadOnlyList<AtcTransmission> Transcript => _transcript;

    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    public IReadOnlyDictionary<string, string> Voices => _stations.AssignedVoices;

    public double? AlongTrackNm => _alongNm;

    private string OriginIcao => Plan.Ofp.Origin.Icao ?? "ORIG";

    private string DestIcao => Plan.Ofp.Destination.Icao ?? "DEST";

    private AtcRules Rules => _t.Rules;

    private bool ArrivalSide => Phase is AtcPhase.Approach or AtcPhase.TowerArrival or AtcPhase.GroundArrival or AtcPhase.Done;

    // ------------------------------------------------------------------ Öffentliche Abläufe

    /// <summary>Sitzung beginnen: Phase nach der aktuellen Lage (am Boden am Start = Delivery, in der Luft = passende Kontrollstelle).</summary>
    public IReadOnlyList<AtcTransmission> Start(AtcInput input)
    {
        _input = input;
        UpdateRoute(input);
        Place(input);
        UpdateOceanicClearance(input);
        return [];
    }

    /// <summary>Takt (etwa 1×/s): automatische Übergaben und Freigaben nach Flugzustand.</summary>
    public IReadOnlyList<AtcTransmission> Tick(AtcInput input)
    {
        var output = new List<AtcTransmission>();
        if (Phase is AtcPhase.Inactive or AtcPhase.Done)
        {
            return output;
        }

        _input = input;
        UpdateRoute(input);
        UpdateMotion(input);
        Resync(input, output);

        switch (Phase)
        {
            case AtcPhase.GroundDeparture:
                TickGroundDeparture(input, output);
                break;
            case AtcPhase.TowerDeparture:
                TickTowerDeparture(input, output);
                break;
            case AtcPhase.Departure:
                TickDeparture(input, output);
                break;
            case AtcPhase.Center:
                TickCenter(input, output);
                break;
            case AtcPhase.Oceanic:
                TickOceanic(input, output);
                break;
            case AtcPhase.Approach:
                TickApproach(input, output);
                break;
            case AtcPhase.TowerArrival:
                TickTowerArrival(input, output);
                break;
            case AtcPhase.GroundArrival:
                TickGroundArrival(input);
                break;
        }

        UpdateOceanicClearance(input);
        return output;
    }

    /// <summary>Funkspruch des Piloten (Text, wie gesprochen). Antwortet mit den ATC-Sprüchen, die darauf folgen.</summary>
    public IReadOnlyList<AtcTransmission> PilotTransmission(string text, AtcInput input)
    {
        var output = new List<AtcTransmission>();
        _input = input;
        UpdateRoute(input);
        if (Phase == AtcPhase.Inactive)
        {
            Place(input);
            UpdateOceanicClearance(input);
        }

        var heard = HeardText.Parse(text);
        var forOceanic = IsForOceanic(heard);
        var target = forOceanic ? OceanicStation() : Station;
        Record(output, "pilot", target, "pilot", text.Trim(), text.Trim(), addToOutput: false);

        if (heard.Tokens.Count == 0)
        {
            Say(output, AtcMessageIds.CommonSayAgain, null, target);
            return output;
        }

        if (heard.Compact.Contains("SAYAGAIN", StringComparison.Ordinal) && _lastAtc is { } last)
        {
            Record(output, "atc", _lastAtcStation ?? Station, last.MessageId, last.Text, last.Spoken);
            return output;
        }

        if (IsRadioCheck(heard))
        {
            // Nur die Lesbarkeit – kein Readback, keine Anfrage, der Ablauf bleibt, wo er ist. Ohne Vorlage (alte eigene Kopie): "roger".
            if (Say(output, AtcMessageIds.CommonRadioCheck, null, target) is null)
            {
                Say(output, AtcMessageIds.CommonRoger, null, target);
            }

            return output;
        }

        if (forOceanic)
        {
            HandleOceanicClearance(heard, input, output);
            return output;
        }

        if (PendingReadback is { } pending)
        {
            HandleReadback(pending, heard, input, output, oceanic: false);
            return output;
        }

        if (_ackPending)
        {
            _ackPending = false;
            if (!LooksLikeNewCall(heard))
            {
                // Quittung einer Übergabe oder Anweisung – ATC antwortet nicht
                _ackFrequency = null;
                return output;
            }
        }

        _ackFrequency = null;
        HandleCall(heard, input, output);
        return output;
    }

    /// <summary>Was der Pilot als Nächstes sagen sollte (Hinweis für Claude und das Dashboard).</summary>
    public IReadOnlyList<AtcRendered> ExpectedPilotCalls(AtcInput input)
    {
        _input = input;
        var calls = new List<AtcRendered>();
        if (Oceanic == OceanicClearanceState.Due && Plan.Oceanic is { } oceanic)
        {
            var locals = OceanicRequestLocals(oceanic);
            if (RenderMessage(AtcMessageIds.OceanicRequest, locals, OceanicStation()) is { } r)
            {
                calls.Add(r);
            }
        }

        if (PendingReadback is not null)
        {
            return calls;
        }

        (string? Id, Dictionary<string, AtcValue>? Locals) next = Step switch
        {
            AtcStep.ClearanceRequest => (AtcMessageIds.DeliveryRequest, null),
            AtcStep.PushRequest => (AtcMessageIds.GroundPushRequest, null),
            AtcStep.TaxiRequest => (AtcMessageIds.GroundTaxiRequest, null),
            AtcStep.ReadyForDeparture => (AtcMessageIds.TowerReadyRequest, null),
            AtcStep.DepartureContact => (AtcMessageIds.DepartureContact, null),
            AtcStep.CenterContact => (CenterContactMessage(), ClearedLocals()),
            AtcStep.ApproachContact => (AtcMessageIds.ApproachContact, ClearedLocals()),
            AtcStep.LandingContact => (AtcMessageIds.TowerLandingContact, null),
            AtcStep.ArrivalContact => (AtcMessageIds.GroundArrivalContact, null),
            AtcStep.OceanicReports when _reportDue => (AtcMessageIds.OceanicPosition, PositionReportLocals()),
            _ => (null, null),
        };

        if (next.Id is not null && RenderMessage(next.Id, next.Locals, Station) is { } rendered)
        {
            calls.Add(rendered);
        }

        return calls;
    }

    /// <summary>Wert von Hand setzen (gate, holdingPoint, taxiRoute, direction, approach, departureRunway, arrivalRunway). Leer = löschen.</summary>
    public void SetOverride(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _overrides.Remove(name);
        }
        else
        {
            _overrides[name] = value.Trim();
        }
    }

    public static readonly IReadOnlyList<string> OverridableValues =
        ["gate", "holdingPoint", "taxiRoute", "direction", "approach", "departureRunway", "arrivalRunway"];

    /// <summary>Wert einer Variable im aktuellen Zustand (für Status und Tests).</summary>
    public AtcValue? Value(string name) => Resolve(name, null, Station);

    // ------------------------------------------------------------------ Einstieg

    private void Place(AtcInput input)
    {
        var now = input.TimeUtc;
        var airborne = input.OnGround == false;
        var toDest = DistanceToDest(input);
        var fromOrigin = DistanceFromOrigin(input);

        if (!airborne)
        {
            if (toDest is < 5 && fromOrigin is not < 5)
            {
                Enter(AtcPhase.GroundArrival, AtcStep.ArrivalContact, DestStation(AtcStationType.Ground), now);
                return;
            }

            Enter(AtcPhase.Delivery, AtcStep.ClearanceRequest, OriginStation(AtcStationType.Delivery), now);
            return;
        }

        var along = _alongNm ?? 0;
        var alt = input.AltitudeFt ?? 0;
        if (toDest is { } d && d <= Rules.TowerHandoffNm)
        {
            Enter(AtcPhase.TowerArrival, AtcStep.LandingContact, DestStation(AtcStationType.Tower), now);
        }
        else if (toDest is { } d2 && d2 <= Rules.ApproachHandoffNm && along > Plan.TotalNm / 2)
        {
            Enter(AtcPhase.Approach, AtcStep.ApproachContact, DestStation(AtcStationType.Approach), now);
        }
        else if ((input.AglFt ?? alt) < Rules.TowerHandoffAglFt && fromOrigin is < 10)
        {
            Enter(AtcPhase.TowerDeparture, AtcStep.TakeoffRoll, OriginStation(AtcStationType.Tower), now);
        }
        else if (alt < DepartureLevelFt - Rules.DepartureHandoffBelowFt && along < 60)
        {
            ClearedAltitudeFt = InitialClimbFt;
            Enter(AtcPhase.Departure, AtcStep.DepartureContact, OriginStation(AtcStationType.Departure), now);
        }
        else
        {
            ClearedAltitudeFt = Plan.CruiseFt;
            EnterFir(Plan.FirAt(along), now);
        }
    }

    private void Enter(AtcPhase phase, AtcStep step, AtcStation station, DateTimeOffset now)
    {
        Phase = phase;
        Station = station;
        PendingReadback = null;
        _airborneInPhase = _input.OnGround == false;
        SetStep(step, now);
    }

    /// <summary>In eine FIR wechseln: Center oder Oceanic, je nach Abschnitt.</summary>
    private void EnterFir(AtcFirSegment? segment, DateTimeOffset now)
    {
        _firSegment = segment;
        if (segment is { Oceanic: true })
        {
            Enter(AtcPhase.Oceanic, AtcStep.OceanicReports, _stations.Fir(segment.Fir, oceanic: true), now);
            StartReports();
        }
        else
        {
            Enter(AtcPhase.Center, AtcStep.CenterContact, _stations.Fir(segment?.Fir, oceanic: false), now);
        }
    }

    private void SetStep(AtcStep step, DateTimeOffset now)
    {
        Step = step;
        _stepSince = now;
        _movedInStep = false;
    }

    // ------------------------------------------------------------------ Takt je Phase

    private void TickGroundDeparture(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        if (Step is AtcStep.TaxiReadback or AtcStep.Taxiing && (input.GroundSpeedKt ?? 0) >= 8)
        {
            _taxiMoved = true;
        }

        if (Step == AtcStep.TaxiReadback && _taxiMoved && MovingFor(now) >= 10)
        {
            // Rollt schon – Readback gilt als erfolgt
            PendingReadback = null;
            SetStep(AtcStep.Taxiing, now);
        }

        if (Step == AtcStep.Taxiing && _taxiMoved && StoppedFor(now) >= Rules.HoldShortSeconds
            && _taxiClearedAt is { } cleared && (now - cleared).TotalSeconds >= 45)
        {
            HandoffToTower(output, now);
        }
    }

    private void TickTowerDeparture(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        var gs = input.GroundSpeedKt ?? 0;
        if (_lineUpAt is not null && gs > 3)
        {
            _movedSinceLineUp = true;
        }

        switch (Step)
        {
            case AtcStep.LineUpReadback when gs > 3:
                PendingReadback = null;
                SetStep(AtcStep.LinedUp, now);
                break;
            case AtcStep.LinedUp when _lineUpAt is { } since:
                var elapsed = (now - since).TotalSeconds;
                if (elapsed >= 8 && ((_movedSinceLineUp && StoppedFor(now) >= 3) || elapsed >= Rules.TakeoffClearanceMaxSeconds))
                {
                    IssueTakeoff(output, now);
                }

                break;
            case AtcStep.TakeoffReadback when gs > 40 || input.OnGround == false:
                PendingReadback = null;
                SetStep(AtcStep.TakeoffRoll, now);
                break;
            case AtcStep.TakeoffRoll when input.OnGround == false && (input.AglFt ?? 0) >= Rules.TowerHandoffAglFt:
                ClearedAltitudeFt ??= InitialClimbFt;
                Handoff(output, AtcMessageIds.TowerHandoffDeparture, OriginStation(AtcStationType.Departure), AtcPhase.Departure, AtcStep.DepartureContact, now);
                break;
        }
    }

    private void TickDeparture(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        var alt = input.AltitudeFt ?? 0;
        if (ApproachHandoffDue(input))
        {
            // Kurzstrecke: direkt von Departure an Approach
            Handoff(output, AtcMessageIds.CenterHandoffApproach, DestStation(AtcStationType.Approach), AtcPhase.Approach, AtcStep.ApproachContact, now);
            return;
        }

        if (alt >= DepartureLevelFt - Rules.DepartureHandoffBelowFt)
        {
            HandoffToFir(output, AtcMessageIds.DepartureHandoff, Plan.FirAt(_alongNm ?? 0), now);
            return;
        }

        if (Step == AtcStep.DepartureClimb && !_directIssued && Rules.OfferDirect && Plan.DirectFix is { } fix
            && alt >= Rules.DirectMinAltitudeFt && Plan.FixDistanceNm(fix) is { } fixNm && fixNm > (_alongNm ?? 0) + 5)
        {
            _directIssued = true;
            var locals = new Dictionary<string, AtcValue> { ["fix"] = FixValue(fix) };
            SetStep(AtcStep.DirectReadback, now);
            SayWithReadback(output, AtcMessageIds.DepartureDirect, locals, (o, i) => SetStep(AtcStep.DepartureClimb, i.TimeUtc));
        }
    }

    private void TickCenter(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        var along = _alongNm ?? 0;
        if (ApproachHandoffDue(input))
        {
            Handoff(output, AtcMessageIds.CenterHandoffApproach, DestStation(AtcStationType.Approach), AtcPhase.Approach, AtcStep.ApproachContact, now);
            return;
        }

        if (FirChanged(along) is { } segment)
        {
            HandoffToFir(output, AtcMessageIds.CenterHandoff, segment, now);
            return;
        }

        if (!_descentIssued && Step != AtcStep.CenterReadback && TopOfDescentReached(input)
            && (input.AltitudeFt ?? 0) > Rules.CenterDescentFt + 1000)
        {
            _descentIssued = true;
            ClearedAltitudeFt = Rules.CenterDescentFt;
            SetStep(AtcStep.CenterReadback, now);
            SayWithReadback(output, AtcMessageIds.CenterDescend, LevelLocals(Rules.CenterDescentFt, "descend"), (o, i) => SetStep(AtcStep.CenterCruise, i.TimeUtc));
        }
    }

    private void TickOceanic(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        var along = _alongNm ?? 0;
        if (ApproachHandoffDue(input))
        {
            Handoff(output, AtcMessageIds.CenterHandoffApproach, DestStation(AtcStationType.Approach), AtcPhase.Approach, AtcStep.ApproachContact, now);
            return;
        }

        if (FirChanged(along) is { } segment)
        {
            HandoffToFir(output, AtcMessageIds.OceanicHandoff, segment, now);
            return;
        }

        if (Plan.Oceanic is not { } oceanic)
        {
            return;
        }

        // Meldung verpasst und schon am nächsten Punkt: weiter zum nächsten
        while (_reportIndex + 1 < oceanic.ReportFixes.Count && Plan.FixDistanceNm(oceanic.ReportFixes[_reportIndex + 1]) is { } nextNm && along >= nextNm - 2)
        {
            _reportIndex++;
        }

        if (_reportIndex < oceanic.ReportFixes.Count && Plan.FixDistanceNm(oceanic.ReportFixes[_reportIndex]) is { } fixNm && along >= fixNm - 2)
        {
            _reportDue = true;
        }
    }

    private void TickApproach(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        var toDest = DistanceToDest(input);
        if (toDest is { } d && d <= Rules.TowerHandoffNm)
        {
            Handoff(output, AtcMessageIds.ApproachHandoff, DestStation(AtcStationType.Tower), AtcPhase.TowerArrival, AtcStep.LandingContact, now);
            return;
        }

        if (Step is AtcStep.ApproachVectoring or AtcStep.ApproachReadback && !_approachCleared && toDest is { } d2 && d2 <= Rules.ApproachClearanceNm)
        {
            _approachCleared = true;
            SetStep(AtcStep.ApproachClearanceReadback, now);
            SayWithReadback(output, AtcMessageIds.ApproachCleared, null, (o, i) => SetStep(AtcStep.ApproachEstablished, i.TimeUtc));
        }
    }

    private void TickTowerArrival(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        if (input.OnGround == false)
        {
            _airborneInPhase = true;
        }

        if (!_landingCleared && input.OnGround == false && DistanceToDest(input) is { } d && d <= Rules.LandingClearanceFallbackNm)
        {
            IssueLanding(output, now);
            return;
        }

        if (_airborneInPhase && input.OnGround == true && (input.GroundSpeedKt ?? 0) < Rules.VacateSpeedKt)
        {
            Handoff(output, AtcMessageIds.TowerVacate, DestStation(AtcStationType.Ground), AtcPhase.GroundArrival, AtcStep.ArrivalContact, now);
        }
    }

    private void TickGroundArrival(AtcInput input)
    {
        var now = input.TimeUtc;
        if (Step == AtcStep.GateReadback && MovingFor(now) >= 10)
        {
            PendingReadback = null;
            SetStep(AtcStep.TaxiIn, now);
        }

        if (Step == AtcStep.TaxiIn && _movedInStep && StoppedFor(now) >= (input.ParkingBrake == true ? 10 : 60))
        {
            Phase = AtcPhase.Done;
            SetStep(AtcStep.Done, now);
        }
    }

    /// <summary>Passt die Phase nicht mehr zum Flugzustand (z. B. ohne Freigaben gestartet), springt die Sitzung nach.</summary>
    private void Resync(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        var departing = Phase is AtcPhase.Delivery or AtcPhase.GroundDeparture
            || (Phase == AtcPhase.TowerDeparture && Step != AtcStep.TakeoffRoll);
        if (departing && (input.OnGround == false || (input.OnGround == true && (input.GroundSpeedKt ?? 0) > 60)))
        {
            Enter(AtcPhase.TowerDeparture, AtcStep.TakeoffRoll, OriginStation(AtcStationType.Tower), now);
            ClearedAltitudeFt ??= InitialClimbFt;
        }
    }

    // ------------------------------------------------------------------ Pilotensprüche

    private void HandleCall(HeardText heard, AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        switch (Step)
        {
            case AtcStep.ClearanceRequest:
                ClearedAltitudeFt = InitialClimbFt;
                SetStep(AtcStep.ClearanceReadback, now);
                SayWithReadback(output, AtcMessageIds.DeliveryClearance, null, (o, i) =>
                {
                    var ground = OriginStation(AtcStationType.Ground);
                    Handoff(o, AtcMessageIds.DeliveryReadbackCorrect, ground, AtcPhase.GroundDeparture, AtcStep.PushRequest, i.TimeUtc);
                });
                break;

            case AtcStep.PushRequest:
                if (heard.Tokens.Any(t => t.IsWord("taxi")) && !heard.Tokens.Any(t => t.IsWord("push") || t.IsWord("pushback")))
                {
                    // Ohne Pushback (z. B. Außenposition) gleich die Rollfreigabe
                    IssueTaxi(output, now);
                }
                else
                {
                    Say(output, AtcMessageIds.GroundPushApproved, null);
                    SetStep(AtcStep.TaxiRequest, now);
                }

                break;

            case AtcStep.TaxiRequest:
                IssueTaxi(output, now);
                break;

            case AtcStep.Taxiing:
                if (heard.Tokens.Any(t => t.IsWord("ready")))
                {
                    // Pilot ruft am Rollhalt schon den Tower
                    Enter(AtcPhase.TowerDeparture, AtcStep.ReadyForDeparture, OriginStation(AtcStationType.Tower), now);
                    IssueLineUp(output, now);
                }
                else
                {
                    HandoffToTower(output, now);
                }

                break;

            case AtcStep.ReadyForDeparture:
                IssueLineUp(output, now);
                break;

            case AtcStep.LinedUp:
                IssueTakeoff(output, now);
                break;

            case AtcStep.DepartureContact:
                ClearedAltitudeFt = DepartureLevelFt;
                SetStep(AtcStep.DepartureReadback, now);
                SayWithReadback(output, AtcMessageIds.DepartureIdentified, LevelLocals(DepartureLevelFt, "climb"), (o, i) => SetStep(AtcStep.DepartureClimb, i.TimeUtc));
                break;

            case AtcStep.CenterContact:
                if (!_descentIssued && (ClearedAltitudeFt ?? 0) < Plan.CruiseFt)
                {
                    ClearedAltitudeFt = Plan.CruiseFt;
                    SetStep(AtcStep.CenterReadback, now);
                    SayWithReadback(output, AtcMessageIds.CenterClimb, LevelLocals(Plan.CruiseFt, "climb"), (o, i) => SetStep(AtcStep.CenterCruise, i.TimeUtc));
                }
                else
                {
                    Say(output, AtcMessageIds.CenterIdentified, ClearedLocals());
                    SetStep(AtcStep.CenterCruise, now);
                }

                break;

            case AtcStep.OceanicReports:
                Say(output, AtcMessageIds.OceanicRoger, null);
                if (_reportDue)
                {
                    _reportDue = false;
                    _reportIndex++;
                }

                break;

            case AtcStep.ApproachContact:
                ClearedAltitudeFt = ApproachLevelFt;
                SetStep(AtcStep.ApproachReadback, now);
                SayWithReadback(output, AtcMessageIds.ApproachDescend, LevelLocals(ApproachLevelFt, "descend"), (o, i) => SetStep(AtcStep.ApproachVectoring, i.TimeUtc));
                break;

            case AtcStep.LandingContact:
                IssueLanding(output, now);
                break;

            case AtcStep.ArrivalContact:
                SetStep(AtcStep.GateReadback, now);
                SayWithReadback(output, AtcMessageIds.GroundTaxiToGate, null, (o, i) => SetStep(AtcStep.TaxiIn, i.TimeUtc));
                break;

            case AtcStep.Done:
                break;

            default:
                Say(output, AtcMessageIds.CommonRoger, null);
                break;
        }
    }

    private void HandleReadback(AtcPendingReadback pending, HeardText heard, AtcInput input, List<AtcTransmission> output, bool oceanic)
    {
        var result = ReadbackChecker.Check(heard, pending.Items);
        if (result.Correct)
        {
            _ackPending = false;
            if (oceanic)
            {
                _oceanicReadback = null;
            }
            else
            {
                PendingReadback = null;
            }

            pending.OnCorrect(output, input);
            return;
        }

        // "negative" mit den richtigen Werten; danach müssen nur diese noch einmal zurückgelesen werden
        var failed = pending.Items.Where(i => result.Issues.Any(x => x.Item == i.Name)).ToList();
        var corrections = Corrections(failed);
        Say(output, AtcMessageIds.CommonNegative, new Dictionary<string, AtcValue> { ["corrections"] = corrections }, pending.Station);
        var retry = pending with { Items = failed };
        if (oceanic)
        {
            _oceanicReadback = retry;
        }
        else
        {
            PendingReadback = retry;
        }
    }

    private void HandleOceanicClearance(HeardText heard, AtcInput input, List<AtcTransmission> output)
    {
        if (_oceanicReadback is { } pending)
        {
            HandleReadback(pending, heard, input, output, oceanic: true);
            return;
        }

        if (Plan.Oceanic is not { } oceanic)
        {
            return;
        }

        var station = OceanicStation();
        var locals = OceanicRequestLocals(oceanic);

        // Angefragte Höhe und Geschwindigkeit übernehmen ("requesting FL370, speed four eight zero knots")
        if (ReadbackChecker.Altitudes(heard).FirstOrDefault(a => a >= 10000) is var requested && requested > 0)
        {
            locals["level"] = Altitude(requested);
        }

        if (ReadbackChecker.Speeds(heard).FirstOrDefault(v => v is >= 100.0 and <= 600.0) is var speed && speed > 0)
        {
            locals["speedKt"] = AtcSpeech.Speed(speed);
        }
        else if (Resolve("speedKt", null, station) is { } current)
        {
            locals["speedKt"] = current;
        }

        if (locals["level"].Number is { } level)
        {
            ClearedAltitudeFt = (int)level;
        }

        var rendered = Say(output, AtcMessageIds.OceanicClearance, locals, station);
        var items = ReadbackItems(AtcMessageIds.OceanicClearance, locals, station);
        if (rendered is null || items.Count == 0)
        {
            Oceanic = OceanicClearanceState.Cleared;
            return;
        }

        Oceanic = OceanicClearanceState.ReadbackPending;
        _oceanicReadback = new AtcPendingReadback(AtcMessageIds.OceanicClearance, station, items, (o, i) =>
        {
            Say(o, AtcMessageIds.OceanicReadbackCorrect, null, station);
            Oceanic = OceanicClearanceState.Cleared;
        });
    }

    // ------------------------------------------------------------------ Freigaben und Übergaben

    private void IssueTaxi(List<AtcTransmission> output, DateTimeOffset now)
    {
        _taxiClearedAt = now;
        _taxiMoved = false;
        SetStep(AtcStep.TaxiReadback, now);
        SayWithReadback(output, AtcMessageIds.GroundTaxi, null, (o, i) => SetStep(AtcStep.Taxiing, i.TimeUtc));
    }

    private void IssueLineUp(List<AtcTransmission> output, DateTimeOffset now)
    {
        _lineUpAt = now;
        _movedSinceLineUp = false;
        SetStep(AtcStep.LineUpReadback, now);
        SayWithReadback(output, AtcMessageIds.TowerLineUp, null, (o, i) => SetStep(AtcStep.LinedUp, i.TimeUtc));
    }

    private void IssueTakeoff(List<AtcTransmission> output, DateTimeOffset now)
    {
        _lineUpAt = null;
        SetStep(AtcStep.TakeoffReadback, now);
        SayWithReadback(output, AtcMessageIds.TowerTakeoff, null, (o, i) => SetStep(AtcStep.TakeoffRoll, i.TimeUtc));
    }

    private void IssueLanding(List<AtcTransmission> output, DateTimeOffset now)
    {
        _landingCleared = true;
        SetStep(AtcStep.LandingReadback, now);
        SayWithReadback(output, AtcMessageIds.TowerLanding, null, (o, i) => SetStep(AtcStep.Landing, i.TimeUtc));
    }

    private void HandoffToTower(List<AtcTransmission> output, DateTimeOffset now) =>
        Handoff(output, AtcMessageIds.GroundHandoffTower, OriginStation(AtcStationType.Tower), AtcPhase.TowerDeparture, AtcStep.ReadyForDeparture, now);

    /// <summary>Übergabe: aktuelle Station nennt die nächste und deren Frequenz, danach spricht der Pilot mit der neuen.</summary>
    private void Handoff(List<AtcTransmission> output, string messageId, AtcStation next, AtcPhase phase, AtcStep step, DateTimeOffset now)
    {
        var locals = new Dictionary<string, AtcValue>
        {
            ["freq"] = next.Frequency,
            ["nextCenter"] = next.NameValue,
        };
        Say(output, messageId, locals);
        Enter(phase, step, next, now);
        _ackPending = true;
        _ackFrequency = next.Frequency;
    }

    /// <summary>Übergabe an die Kontrollstelle eines FIR-Abschnitts (Center oder Oceanic).</summary>
    private void HandoffToFir(List<AtcTransmission> output, string messageId, AtcFirSegment? segment, DateTimeOffset now)
    {
        var oceanic = segment is { Oceanic: true };
        var station = _stations.Fir(segment?.Fir, oceanic);
        Handoff(output, messageId, station, oceanic ? AtcPhase.Oceanic : AtcPhase.Center, oceanic ? AtcStep.OceanicReports : AtcStep.CenterContact, now);
        _firSegment = segment;
        if (oceanic)
        {
            StartReports();
        }
    }

    private bool ApproachHandoffDue(AtcInput input) =>
        DistanceToDest(input) is { } d && d <= Rules.ApproachHandoffNm && (_alongNm ?? 0) > Plan.TotalNm / 2;

    /// <summary>Neuer FIR-Abschnitt erreicht?</summary>
    private AtcFirSegment? FirChanged(double along)
    {
        var segment = Plan.FirAt(along);
        if (segment is null || _firSegment is null)
        {
            _firSegment ??= segment;
            return null;
        }

        return segment.StartNm > _firSegment.StartNm + 0.01 ? segment : null;
    }

    private bool TopOfDescentReached(AtcInput input)
    {
        var along = _alongNm ?? 0;
        if (Plan.TodNm is { } tod)
        {
            return along >= tod - Rules.TopOfDescentLeadNm;
        }

        // Ohne TOD im Plan: 3-zu-1-Regel (3 nm je 1000 ft) plus Reserve
        var toGo = Plan.TotalNm - along;
        var elevation = Plan.Ofp.Destination.ElevationFt ?? 0;
        return toGo <= ((input.AltitudeFt ?? 0) - elevation) / 1000.0 * 3 + 10;
    }

    private void StartReports()
    {
        _reportDue = false;
        _reportIndex = 0;
        if (Plan.Oceanic is not { } oceanic)
        {
            return;
        }

        var along = _alongNm ?? 0;
        while (_reportIndex < oceanic.ReportFixes.Count && Plan.FixDistanceNm(oceanic.ReportFixes[_reportIndex]) is { } d && d < along - 20)
        {
            _reportIndex++;
        }
    }

    private void UpdateOceanicClearance(AtcInput input)
    {
        if (Oceanic is not (OceanicClearanceState.NotYetDue or OceanicClearanceState.Due) || Plan.Oceanic is not { } oceanic)
        {
            return;
        }

        var entryNm = Plan.FixDistanceNm(oceanic.EntryFix);
        var along = _alongNm;

        // Schon im Oceanic-Luftraum oder am Eintrittspunkt vorbei (z. B. Sitzung erst über dem Atlantik bzw. nach einem
        // Neustart von Kopilot gestartet, oder nie angefordert): die Freigabe gilt als erteilt. Sonst bliebe sie auf
        // NotYetDue stehen, und mit Due ginge eine Positionsmeldung an Shanwick als Freigabe-Anfrage durch.
        if (Phase == AtcPhase.Oceanic || (entryNm is { } passedNm && along is { } a && a >= passedNm))
        {
            Oceanic = OceanicClearanceState.Cleared;
            return;
        }

        if (Oceanic != OceanicClearanceState.NotYetDue || Phase is not (AtcPhase.Departure or AtcPhase.Center)
            || entryNm is not { } nm || along is not { } alongNm)
        {
            return;
        }

        var gs = input.GroundSpeedKt is > 100 ? input.GroundSpeedKt.Value : 450;
        var minutes = (nm - alongNm) / gs * 60;
        if (minutes <= Rules.OceanicRequestMinutes)
        {
            Oceanic = OceanicClearanceState.Due;
        }
    }

    /// <summary>
    /// Gilt der Funkspruch der Oceanic-Freigabe (Anfrage oder Readback)? Die Anfrage muss "oceanic" oder den Namen der
    /// Stelle enthalten. Während das Readback der Freigabe offen ist, gilt jeder Spruch ihr – außer es wartet zugleich ein
    /// Readback der aktuellen Station und der Spruch nennt weder Oceanic noch den Eintrittspunkt.
    /// </summary>
    private bool IsForOceanic(HeardText heard)
    {
        if (Oceanic is not (OceanicClearanceState.Due or OceanicClearanceState.ReadbackPending) || Plan.Oceanic is not { } oceanic)
        {
            return false;
        }

        var name = OceanicStation().Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var mentionsOceanic = heard.Compact.Contains("OCEANIC", StringComparison.Ordinal)
            || (name is { Length: >= 4 } && heard.Compact.Contains(AtcSpeech.Compact(name), StringComparison.Ordinal));
        if (Oceanic == OceanicClearanceState.Due)
        {
            return mentionsOceanic;
        }

        return PendingReadback is null || mentionsOceanic || heard.ContainsCode(Plan.Ofp.Fixes[oceanic.EntryFix].Ident);
    }

    /// <summary>Radio Check ("…, radio check on one two seven decimal six five", "how do you read")?</summary>
    private static bool IsRadioCheck(HeardText heard)
    {
        var t = heard.Tokens;
        for (var i = 0; i + 1 < t.Count; i++)
        {
            if (t[i].IsWord("radio") && t[i + 1].IsWord("check"))
            {
                return true;
            }

            if (i + 3 < t.Count && t[i].IsWord("how") && t[i + 1].IsWord("do") && t[i + 2].IsWord("you") && t[i + 3].IsWord("read"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Neuer Anruf (Anfrage, Erstkontakt) statt Quittung der letzten Anweisung?</summary>
    private bool LooksLikeNewCall(HeardText heard)
    {
        if (_ackFrequency is { Number: { } mhz } && heard.Tokens.Any(t => t.Kind == HeardKind.Decimal && Math.Abs(t.Value - mhz) < 0.003))
        {
            return false;
        }

        var t = heard.Tokens;
        if (t.Any(x => x.IsWord(NewCallWords)))
        {
            return true;
        }

        if (t.Any(x => x.IsWord("contact")))
        {
            return false;
        }

        // Anruf beginnt mit dem Namen der Station ("Langen Radar, …", "Tower, …")
        var nameWords = (Station.Name + " " + _t.StationType(Station.Type).Name).ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .ToHashSet();
        return t.Take(3).Any(x => x.Kind == HeardKind.Word && nameWords.Contains(x.Text));
    }

    // ------------------------------------------------------------------ Sprechen

    /// <summary>Funkspruch rendern und in den Verlauf schreiben.</summary>
    private AtcRendered? Say(List<AtcTransmission> output, string messageId, Dictionary<string, AtcValue>? locals, AtcStation? station = null)
    {
        var speaker = station ?? Station;
        if (RenderMessage(messageId, locals, speaker) is not { } rendered)
        {
            return null;
        }

        Record(output, "atc", speaker, messageId, rendered.Text, rendered.Spoken);

        // Auf Anweisungen folgt meist eine Quittung des Piloten, auf die ATC nicht antwortet
        if (messageId is not (AtcMessageIds.CommonRoger or AtcMessageIds.CommonSayAgain or AtcMessageIds.CommonNegative or AtcMessageIds.CommonRadioCheck or AtcMessageIds.OceanicRoger))
        {
            _ackPending = true;
        }

        return rendered;
    }

    /// <summary>
    /// Freigabe sprechen und Readback verlangen. Ohne prüfbare Werte gilt sie sofort als quittiert. Aufrufer setzen den
    /// Readback-Schritt vorher – <paramref name="onCorrect"/> setzt den Schritt danach.
    /// </summary>
    private void SayWithReadback(List<AtcTransmission> output, string messageId, Dictionary<string, AtcValue>? locals, Action<List<AtcTransmission>, AtcInput> onCorrect)
    {
        var station = Station;
        var items = Say(output, messageId, locals, station) is null ? [] : ReadbackItems(messageId, locals, station);
        if (items.Count == 0)
        {
            // Nichts zu prüfen: gilt als quittiert
            PendingReadback = null;
            onCorrect(output, _input);
            return;
        }

        PendingReadback = new AtcPendingReadback(messageId, station, items, onCorrect);
    }

    private List<ReadbackItem> ReadbackItems(string messageId, Dictionary<string, AtcValue>? locals, AtcStation station)
    {
        var items = new List<ReadbackItem>();
        if (_t.Message(messageId) is not { } template)
        {
            return items;
        }

        foreach (var name in template.Readback)
        {
            if (Resolve(name, locals, station) is { } value)
            {
                items.Add(new ReadbackItem(name, value));
            }
        }

        return items;
    }

    private AtcValue Corrections(IReadOnlyList<ReadbackItem> failed)
    {
        var written = new List<string>();
        var spoken = new List<string>();
        foreach (var item in failed)
        {
            var template = _t.Corrections.TryGetValue(item.Name, out var c) ? c : "{" + item.Name + "}";
            var locals = new Dictionary<string, AtcValue> { [item.Name] = item.Expected };
            if (item.Name is "level" or "initialAlt")
            {
                locals["levelVerb"] = LevelVerb(item.Expected.Number);
            }

            if (AtcRenderer.Render("corrections." + item.Name, [template], n => Resolve(n, locals, Station)) is { } r)
            {
                written.Add(r.Text);
                spoken.Add(r.Spoken);
            }
        }

        return AtcValue.Text(string.Join(", ", written), string.Join(", ", spoken));
    }

    private AtcRendered? RenderMessage(string messageId, Dictionary<string, AtcValue>? locals, AtcStation station) =>
        _t.Message(messageId) is { } template ? AtcRenderer.Render(messageId, template.Variants, n => Resolve(n, locals, station)) : null;

    private void Record(List<AtcTransmission> output, string from, AtcStation station, string messageId, string text, string spoken, bool addToOutput = true)
    {
        var t = new AtcTransmission(++_nextId, _input.TimeUtc, from, station.Key, station.Name, station.Frequency.Written, from == "atc" ? station.Voice : "", messageId, text, spoken);
        _transcript.Add(t);
        if (_transcript.Count > TranscriptLimit)
        {
            _transcript.RemoveRange(0, _transcript.Count - TranscriptLimit);
        }

        if (from == "atc")
        {
            _lastAtc = t;
            _lastAtcStation = station;
        }

        if (addToOutput)
        {
            output.Add(t);
        }
    }
}
