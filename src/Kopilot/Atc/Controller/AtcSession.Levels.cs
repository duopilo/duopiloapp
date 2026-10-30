namespace Kopilot.Atc;

/// <summary>Höhenanfrage aus einem Funkspruch: Höhe in ft, falls genannt, und Richtung (+1 höher, −1 tiefer, 0 ohne Angabe).</summary>
public sealed record AtcLevelRequest(int? Feet, int Direction);

/// <summary>Nach "unable … expect … in … minutes": diese Höhe gibt ATC zum Zeitpunkt <paramref name="DueUtc"/> von sich aus frei.</summary>
public sealed record AtcExpectedLevel(int AltitudeFt, DateTimeOffset DueUtc);

/// <summary>
/// Höhen im Streckenflug: Anfragen des Piloten ("request climb flight level three four zero"), Stufensteigflüge aus dem OFP
/// und die gemeldete Höhe beim Erstanruf ("maintaining FL340"), wenn sie nicht zur Freigabe passt.
/// </summary>
public sealed partial class AtcSession
{
    private static readonly string[] RequestWords = ["request", "requesting", "requests"];
    private static readonly string[] HigherWords = ["climb", "higher", "up"];
    private static readonly string[] LowerWords = ["descent", "descend", "lower", "down"];
    private static readonly string[] MaintainingWords = ["maintaining", "maintain"];

    /// <summary>Stufen des OFP vor diesem Index sind erledigt (freigegeben oder überholt).</summary>
    private int _stepClimbIndex;

    /// <summary>Zähler der Höhenanfragen – macht die Ablehnung nach <see cref="AtcRules.LevelRequestUnablePercent"/> reproduzierbar.</summary>
    private int _levelRequests;

    private AtcExpectedLevel? _expectedLevel;

    /// <summary>Abgelehnte Höhenanfrage, die ATC später von sich aus freigibt (null = keine).</summary>
    public AtcExpectedLevel? ExpectedLevel => _expectedLevel;

    /// <summary>
    /// Höhenanfrage im Funkspruch? "request climb flight level three four zero", "requesting FL340", "request descent to one
    /// two thousand feet", "request higher". Die Höhe muss kurz nach "request" kommen; eine Oceanic-Freigabe-Anfrage zählt nicht.
    /// </summary>
    public static AtcLevelRequest? ParseLevelRequest(HeardText heard)
    {
        var t = heard.Tokens;
        var start = -1;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].IsWord(RequestWords))
            {
                start = i;
                break;
            }
        }

        if (start < 0 || (t.Any(x => x.IsWord("oceanic")) && t.Any(x => x.IsWord("clearance"))))
        {
            return null;
        }

        var direction = 0;
        for (var j = start + 1; j < t.Count && j <= start + 2; j++)
        {
            if (t[j].IsWord(HigherWords))
            {
                direction = 1;
                break;
            }

            if (t[j].IsWord(LowerWords))
            {
                direction = -1;
                break;
            }
        }

        int? feet = null;
        foreach (var (index, value) in ReadbackChecker.AltitudesAt(heard))
        {
            if (index > start && index <= start + 6 && value >= 1000)
            {
                feet = RoundToHundreds(value);
                break;
            }
        }

        return direction == 0 && feet is null ? null : new AtcLevelRequest(feet, direction);
    }

    /// <summary>Gemeldete Höhe vor einer Anfrage: "maintaining FL340" → 34000, sonst null.</summary>
    public static int? ReportedLevel(HeardText heard)
    {
        var t = heard.Tokens;
        var request = t.Count;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].IsWord(RequestWords))
            {
                request = i;
                break;
            }
        }

        foreach (var (index, feet) in ReadbackChecker.AltitudesAt(heard))
        {
            if (index >= request)
            {
                break;
            }

            for (var j = index - 1; j >= 0 && j >= index - 4; j--)
            {
                if (t[j].IsWord(MaintainingWords))
                {
                    return RoundToHundreds(feet);
                }
            }
        }

        return null;
    }

    private static int RoundToHundreds(double feet) => (int)(Math.Round(feet / 100.0, MidpointRounding.AwayFromZero) * 100);

    /// <summary>
    /// Höhenanfrage im Streckenflug (Departure nach dem Erstanruf, Center, Oceanic) beantworten: Freigabe mit Readback,
    /// unter der Übergangshöhe mit QNH. Rückgabe: false, wenn der Spruch keine Höhenanfrage ist (dann normal weiter).
    /// </summary>
    private bool HandleLevelRequest(HeardText heard, AtcInput input, List<AtcTransmission> output)
    {
        if (Phase is not (AtcPhase.Departure or AtcPhase.Center or AtcPhase.Oceanic)
            || (Phase == AtcPhase.Departure && Step == AtcStep.DepartureContact)
            || ParseLevelRequest(heard) is not { } request)
        {
            return false;
        }

        var now = input.TimeUtc;
        if (Step == AtcStep.CenterContact)
        {
            // Erstanruf mit Anfrage ("maintaining FL320, request climb FL340"): identifiziert, gleich die Antwort auf die Anfrage
            AcceptReportedLevel(heard, input);
            SetStep(AtcStep.CenterCruise, now);
        }

        if (Phase == AtcPhase.Oceanic && _reportDue && heard.Tokens.Any(t => t.IsWord("position")))
        {
            _reportDue = false;
            _reportIndex++;
        }

        _levelRequests++;
        var basis = ClearedAltitudeFt ?? RoundToHundreds(input.AltitudeFt ?? Plan.CruiseFt);
        int target;
        if (request.Feet is { } feet)
        {
            target = feet;
        }
        else if (request.Direction > 0)
        {
            target = Plan.StepClimbs.FirstOrDefault(s => s.AltitudeFt > basis)?.AltitudeFt ?? basis + 2000;
        }
        else if (Phase == AtcPhase.Center && !_descentIssued && basis > Rules.CenterDescentFt + 1000 && TopOfDescentReached(input, extraLeadNm: 50))
        {
            // "request descent" kurz vor dem Top of Descent: die übliche Sinkflugfreigabe
            IssueTopOfDescent(output, now);
            return true;
        }
        else
        {
            target = basis - 2000;
        }

        target = Math.Max(target, 2000);

        // Schon abgelehnt und noch nicht dran: dieselbe Antwort mit der Restzeit
        if (_expectedLevel is { } expected && expected.AltitudeFt == target && now < expected.DueUtc)
        {
            if (SayUnable(output, target, expected.DueUtc, now))
            {
                return true;
            }
        }
        else if (Rules.LevelRequestUnablePercent > 0
                 && AtcVoiceAssigner.StableHash($"{Plan.FlightKey}|level|{_levelRequests}") % 100 < Rules.LevelRequestUnablePercent)
        {
            var due = now + TimeSpan.FromMinutes(Math.Max(1, Rules.LevelRequestDelayMinutes));
            if (SayUnable(output, target, due, now))
            {
                _expectedLevel = new AtcExpectedLevel(target, due);
                return true;
            }
        }

        IssueLevel(target, AtcMessageIds.CenterLevelChange, output, now);
        return true;
    }

    /// <summary>"unable FL340 due traffic, maintain FL320, expect FL340 in 10 minutes". False ohne Vorlage.</summary>
    private bool SayUnable(List<AtcTransmission> output, int target, DateTimeOffset due, DateTimeOffset now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((due - now).TotalMinutes));
        var locals = new Dictionary<string, AtcValue>
        {
            ["requested"] = Altitude(target),
            ["minutes"] = AtcValue.Text(minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        return Say(output, AtcMessageIds.CenterLevelUnable, locals) is not null;
    }

    /// <summary>
    /// Höhenfreigabe ("climb and maintain FL340", "when ready climb and maintain FL340", "descend and maintain 12000 ft, QNH 1013")
    /// mit Readback; die freigegebene Höhe gilt sofort.
    /// </summary>
    private void IssueLevel(int feet, string messageId, List<AtcTransmission> output, DateTimeOffset now)
    {
        var from = _input.AltitudeFt ?? ClearedAltitudeFt ?? feet;
        var verb = feet > from + 300 ? "climb" : feet < from - 300 ? "descend" : null;
        ClearedAltitudeFt = feet;
        _expectedLevel = null;

        var level = Altitude(feet);
        var locals = new Dictionary<string, AtcValue>
        {
            ["level"] = level,
            ["levelVerb"] = verb is null ? AtcValue.Text(_t.Word("maintain", "maintain")) : AtcValue.Text(_t.Word(verb + "AndMaintain", verb + " and maintain")),
        };

        // Unter der Übergangshöhe in Fuß – mit QNH
        var id = messageId == AtcMessageIds.CenterLevelChange && !level.FlightLevel && _t.Message(AtcMessageIds.CenterLevelChangeQnh) is not null
            ? AtcMessageIds.CenterLevelChangeQnh
            : messageId;
        if (_t.Message(id) is null)
        {
            // Eigene Vorlagen ohne die neuen Sprüche: die alten "climb/descend {level}"
            id = feet >= from ? AtcMessageIds.CenterClimb : AtcMessageIds.CenterDescend;
            locals = LevelLocals(feet, feet >= from ? "climb" : "descend");
        }

        var (readbackStep, after) = Phase switch
        {
            AtcPhase.Departure => (AtcStep.DepartureReadback, AtcStep.DepartureClimb),
            AtcPhase.Center => (AtcStep.CenterReadback, AtcStep.CenterCruise),
            _ => (Step, Step),
        };
        SetStep(readbackStep, now);
        SayWithReadback(output, id, locals, (o, i) => SetStep(after, i.TimeUtc));
    }

    /// <summary>Sinkflug am Top of Descent auf <see cref="AtcRules.CenterDescentFt"/>.</summary>
    private void IssueTopOfDescent(List<AtcTransmission> output, DateTimeOffset now)
    {
        _descentIssued = true;
        _expectedLevel = null;
        ClearedAltitudeFt = Rules.CenterDescentFt;
        SetStep(AtcStep.CenterReadback, now);
        SayWithReadback(output, AtcMessageIds.CenterDescend, LevelLocals(Rules.CenterDescentFt, "descend"), (o, i) => SetStep(AtcStep.CenterCruise, i.TimeUtc));
    }

    /// <summary>
    /// Takt im Reiseflug (Center, Oceanic): fällige Freigabe nach einer Ablehnung, sonst der nächste Stufensteigflug des OFP
    /// ("when ready climb and maintain FL340"), <see cref="AtcRules.StepClimbLeadNm"/> vor dem Stufenpunkt.
    /// </summary>
    private void TickLevels(AtcInput input, List<AtcTransmission> output)
    {
        if (Phase is not (AtcPhase.Center or AtcPhase.Oceanic) || PendingReadback is not null || output.Count > 0
            || Step is not (AtcStep.CenterCruise or AtcStep.OceanicReports))
        {
            return;
        }

        var now = input.TimeUtc;
        if (_expectedLevel is { } expected)
        {
            if (now >= expected.DueUtc)
            {
                IssueLevel(expected.AltitudeFt, AtcMessageIds.CenterLevelChange, output, now);
            }

            return;
        }

        if (Rules.OfferStepClimbs && !_descentIssued && DueStepClimb() is { } step)
        {
            IssueLevel(step.AltitudeFt, AtcMessageIds.CenterLevelWhenReady, output, now);
        }
    }

    /// <summary>Fälliger Stufensteigflug (der letzte erreichte); schon freigegebene und überholte Stufen werden übersprungen.</summary>
    private AtcStepClimb? DueStepClimb()
    {
        var along = _alongNm ?? 0;
        var cleared = ClearedAltitudeFt ?? 0;
        AtcStepClimb? due = null;
        while (_stepClimbIndex < Plan.StepClimbs.Count)
        {
            var step = Plan.StepClimbs[_stepClimbIndex];
            if (step.AltitudeFt <= cleared)
            {
                _stepClimbIndex++;
                continue;
            }

            if (along < step.AlongNm - Rules.StepClimbLeadNm)
            {
                break;
            }

            due = step;
            _stepClimbIndex++;
        }

        return due;
    }

    /// <summary>Nächster Stufensteigflug über der Freigabe (für den Hinweis "request climb …" an den Piloten).</summary>
    private AtcStepClimb? UpcomingStepClimb()
    {
        var along = _alongNm ?? 0;
        var cleared = ClearedAltitudeFt ?? 0;
        return Plan.StepClimbs.Skip(_stepClimbIndex)
            .FirstOrDefault(s => s.AltitudeFt > cleared && along >= s.AlongNm - (3 * Math.Max(1, Rules.StepClimbLeadNm)));
    }

    /// <summary>
    /// Erstanruf mit gemeldeter Höhe ("maintaining FL340"), die nicht zur Freigabe passt: Liegt das Flugzeug laut Sim dort
    /// (±400 ft, im Horizontalflug), übernimmt ATC sie – so wie nach einer Freigabe, die Kopilot nicht kennt (Stufe ohne
    /// Kopilot-ATC, Neustart). Sonst bleibt die Freigabe.
    /// </summary>
    private bool AcceptReportedLevel(HeardText heard, AtcInput input)
    {
        if (ReportedLevel(heard) is not { } reported || reported < 1000 || ClearedAltitudeFt is not { } cleared || Math.Abs(reported - cleared) < 500)
        {
            return false;
        }

        if ((input.AltitudeFt is { } actual && Math.Abs(actual - reported) > 400) || Math.Abs(input.VerticalSpeedFpm ?? 0) > 500)
        {
            return false;
        }

        ClearedAltitudeFt = reported;
        return true;
    }

    /// <summary>
    /// Einstieg im Streckenflug (Sitzung in der Luft begonnen): liegt das Flugzeug im Horizontalflug über der geplanten
    /// Reiseflughöhe (Stufensteigflug), gilt diese Höhe als freigegeben – sonst die Reiseflughöhe.
    /// </summary>
    private int EnrouteLevel(AtcInput input) =>
        input.AltitudeFt is { } alt && Math.Abs(input.VerticalSpeedFpm ?? 0) < 500 && alt >= Plan.CruiseFt + 500
            ? (int)(Math.Round(alt / 1000.0, MidpointRounding.AwayFromZero) * 1000)
            : Plan.CruiseFt;
}
