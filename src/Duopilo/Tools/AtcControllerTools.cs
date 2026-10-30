using System.ComponentModel;
using Duopilo.Atc;
using Duopilo.Localization;
using Duopilo.Monitoring;
using ModelContextProtocol.Server;

namespace Duopilo.Tools;

/// <summary>
/// Duopilo-ATC: Zustandsmaschine mit festen Textvorlagen (atc-templates.json). Claude gibt die Funksprüche des Piloten
/// weiter, Duopilo antwortet als Delivery, Ground, Tower, Departure, Center, Oceanic und Approach – jede Station mit eigener Stimme.
/// </summary>
[McpServerToolType]
public static class AtcControllerTools
{
    [McpServerTool(Name = "get_atc_controller", ReadOnly = true)]
    [Description(
        "Duopilo ATC (template-based, no LLM): current station with frequency and voice, phase/step, what the pilot is expected to say next " +
        "(rendered phraseology), pending readback items, squawk, cleared altitude, step climbs from the OFP, SID/STAR/runways, oceanic clearance " +
        "state and the recent radio transcript. Frequencies come from the MSFS facilities API, the template file, or a placeholder (frequencySource). " +
        "'deviations' lists a transponder code or COM1 frequency that doesn't match the assignment (MSFS changes both on its own, even with its ATC muted) " +
        "after ATC said 'check squawk/frequency, should be …': offer the pilot the reset with the listed prepare_action steps, never reset without the pilot's confirmation.")]
    public static string GetAtcController(AtcService atc, AlertBus alerts) =>
        Json.Serialize(atc.WithSession((session, input) => Status(atc, session, input, alerts)));

    [McpServerTool(Name = "atc_transmit", Destructive = false, Idempotent = false)]
    [Description(
        "Send the pilot's radio call to Duopilo ATC and get the controller's reply. Pass the pilot's words exactly as spoken, e.g. " +
        "'Frankfurt Delivery, Lufthansa four alpha bravo, request IFR clearance to Berlin' or a readback " +
        "'cleared to Berlin, MARUN seven golf departure, runway two five center, climb five thousand feet, squawk two two zero five'. " +
        "Readbacks are checked (SID, altitude, squawk, runway, frequency …); a wrong value gets 'negative' with the correct one. " +
        "Level requests in cruise ('request climb flight level three four zero', 'request descent', 'request higher') get 'climb and maintain …' " +
        "with readback; the cleared altitude follows. A call addressed to another station gets 'this is …, say again' (or the old station repeats " +
        "the handoff). 'say again' repeats the last transmission. Duopilo speaks the reply itself with the station's voice – do not read it aloud " +
        "again unless the pilot asks. Starts the ATC session on the first call; after a Duopilo restart the saved session of the same " +
        "SimBrief flight continues (station, cleared altitude, transcript).")]
    public static async Task<string> AtcTransmit(
        AtcService atc,
        AlertBus alerts,
        [Description("The pilot's transmission, verbatim.")] string text,
        CancellationToken cancellationToken = default)
    {
        var result = await atc.TransmitAsync(text, cancellationToken);
        return Json.Serialize(new
        {
            ok = result.Ok,
            error = result.Error,
            responses = result.Responses.Select(View).ToList(),
            spokenByDuopilo = atc.HasRadio,
            state = atc.WithSession((session, input) => Status(atc, session, input, alerts)),
        });
    }

    [McpServerTool(Name = "atc_control", Destructive = false, Idempotent = false)]
    [Description(
        "Control the Duopilo ATC session: 'start' (begin at the current position), 'restart' (new session, new voices/squawk for a new OFP), " +
        "'stop', 'reload' (re-read atc-templates.json after editing) or 'say_again' (repeat the last ATC transmission).")]
    public static async Task<string> AtcControl(
        AtcService atc,
        AlertBus alerts,
        [Description("start, restart, stop, reload or say_again")] string action,
        CancellationToken cancellationToken = default)
    {
        var message = await atc.ControlAsync(action, cancellationToken);
        return Json.Serialize(new
        {
            message,
            state = atc.WithSession((session, input) => Status(atc, session, input, alerts)),
        });
    }

    [McpServerTool(Name = "atc_set_value", Destructive = false, Idempotent = true)]
    [Description(
        "Set a value the ATC templates cannot derive yet: gate, holdingPoint, taxiRoute (taxiways separated by spaces, e.g. 'N N3'), " +
        "direction (pushback facing, e.g. 'east'), approach (e.g. 'ILS', 'RNP Z'), departureRunway or arrivalRunway (e.g. '25C'). " +
        "Empty value clears it. Use when the pilot tells you, e.g. 'we are at gate A26'.")]
    public static string AtcSetValue(
        AtcService atc,
        [Description("gate, holdingPoint, taxiRoute, direction, approach, departureRunway or arrivalRunway")] string name,
        [Description("The value; empty to clear.")] string? value = null) =>
        Json.Serialize(new { message = atc.SetValue(name, value) });

    internal static object Status(AtcService atc, AtcSession? session, AtcInput input, AlertBus alerts)
    {
        if (session is null)
        {
            return new
            {
                active = false,
                enabled = atc.Enabled,
                templates = atc.TemplatesPath,
                templateErrors = atc.TemplateErrors,
                hint = Loc.T("tools.atc.sessionHint"),
                persistence = Persistence(atc),
                templateNotes = atc.TemplateNotes,
                airspace = atc.AirspaceSources,
                activeAlerts = AlertView.Active(alerts),
            };
        }

        var plan = session.Plan;
        var pending = session.PendingReadback;
        var oceanicPending = session.PendingOceanicReadback;
        var deviations = new[] { session.SquawkDeviation, session.FrequencyDeviation }.OfType<AtcDeviation>().ToList();
        return new
        {
            active = true,
            phase = session.Phase.ToString(),
            step = session.Step.ToString(),
            station = new
            {
                name = session.Station.Name,
                type = session.Station.Type.ToString(),
                facility = session.Station.Facility,
                frequency = session.Station.Frequency.Written,
                frequencySource = session.Station.FrequencySource,
                voice = session.Station.Voice,
            },
            expectedPilotCalls = session.ExpectedPilotCalls(input).Select(c => c.Text).ToList(),
            pendingReadback = pending is null ? null : new
            {
                message = pending.MessageId,
                items = pending.Items.Select(i => new { item = i.Name, expected = i.Expected.Written }).ToList(),
            },
            flight = new
            {
                callsign = plan.Callsign,
                spokenCallsign = session.Value("callsign")?.Spoken,
                origin = plan.Ofp.Origin.Icao,
                destination = plan.Ofp.Destination.Icao,
                sid = plan.Sid,
                star = plan.Star,
                departureRunway = plan.DepartureRunway,
                arrivalRunway = plan.ArrivalRunway,
                squawk = session.Squawk,
                squawkSource = session.SquawkSource,
                clearedAltitude = session.ClearedAltitudeFt,
                cruiseFt = plan.CruiseFt,
                stepClimbs = plan.StepClimbs.Select(step => new { fix = step.Fix, altitudeFt = step.AltitudeFt, alongNm = Math.Round(step.AlongNm) }).ToList(),
                expectedLevel = session.ExpectedLevel is { } later
                    ? new { altitudeFt = later.AltitudeFt, dueUtc = later.DueUtc }
                    : null,
                alongTrackNm = session.AlongTrackNm is { } a ? Math.Round(a, 1) : (double?)null,
                firs = plan.Firs.Select(f => new
                {
                    fir = f.Fir,
                    station = session.StationName(f),
                    sector = f.Sector,
                    frequency = f.FrequencyMhz,
                    startNm = Math.Round(f.StartNm),
                    oceanic = f.Oceanic,
                }).ToList(),
            },
            oceanic = plan.Oceanic is null
                ? null
                : new
                {
                    state = session.Oceanic.ToString(),
                    entryFix = plan.Ofp.Fixes[plan.Oceanic.EntryFix].Ident,
                    exitFix = plan.Ofp.Fixes[plan.Oceanic.ExitFix].Ident,
                    reportFixes = plan.Oceanic.ReportFixes.Select(i => plan.Ofp.Fixes[i].Ident).ToList(),
                    pendingReadback = oceanicPending?.Items.Select(i => new { item = i.Name, expected = i.Expected.Written }).ToList(),
                },
            aircraft = new
            {
                transponder = input.TransponderCode,
                com1ActiveMhz = input.Com1ActiveMhz is { } com ? Math.Round(com, 3) : (double?)null,
            },
            deviations = deviations.Count == 0
                ? null
                : new
                {
                    hint = Loc.T("tools.atc.deviationHint"),
                    items = deviations.Select(d => new
                    {
                        kind = d.Kind.ToString().ToLowerInvariant(),
                        expected = d.Expected,
                        actual = d.Actual,
                        station = d.Station,
                        sinceUtc = d.SinceUtc,
                        atcCalls = d.Calls,
                        actions = d.Actions.Select(action => new { action = action.Action, value = action.Value }).ToList(),
                    }).ToList(),
                },
            voices = session.Voices.ToDictionary(v => v.Key, v => v.Value),
            overrides = session.Overrides.ToDictionary(v => v.Key, v => v.Value),
            spokenByDuopilo = atc.HasRadio,
            facilityFrequencies = atc.Frequencies.Counts,
            transcript = session.Transcript.TakeLast(12).Select(View).ToList(),
            templates = atc.TemplatesPath,
            templateErrors = atc.TemplateErrors,
            persistence = Persistence(atc),
            templateNotes = atc.TemplateNotes,
            airspace = atc.AirspaceSources,
            activeAlerts = AlertView.Active(alerts),
        };
    }

    /// <summary>Sitzungssicherung: Datei, zuletzt gesichert, fortgesetzt aus einer Sicherung von wann.</summary>
    private static object Persistence(AtcService atc) => new
    {
        file = atc.SessionFile,
        lastSavedUtc = atc.SessionSavedUtc,
        restoredFromUtc = atc.RestoredFromUtc,
        error = atc.SessionStoreError,
    };

    private static object View(AtcTransmission t) => new
    {
        id = t.Id,
        timeUtc = t.TimeUtc,
        from = t.From,
        station = t.StationName,
        frequency = t.Frequency,
        voice = t.From == "atc" ? t.Voice : null,
        text = t.Text,
    };
}
