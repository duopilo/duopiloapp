namespace Duopilo.Atc;

/// <summary>Was der Pilot zum Follow-me sagt.</summary>
public enum AtcFollowMeCallKind
{
    Request,
    Cancel,
}

/// <summary>
/// Follow-me im Funk erkennen: "request follow-me" fordert an, "cancel follow-me" / "follow-me not required" bestellt ab. Ein Readback
/// ("follow the follow-me to gate A26") ist keins von beiden.
/// </summary>
public static class AtcFollowMeCall
{
    private static readonly string[] RequestWords = ["request", "requesting", "require", "need", "want"];

    public static AtcFollowMeCallKind? Parse(HeardText heard)
    {
        // "follow-me", "follow me", "followme" – alles wird zu FOLLOWME
        if (!heard.Compact.Contains("FOLLOWME", StringComparison.Ordinal))
        {
            return null;
        }

        var words = heard.Tokens.Where(t => t.Kind == HeardKind.Word).Select(t => t.Text).ToList();
        var negated = words.Any(w => w is "not" or "no" or "dont" or "don" or "without" or "negative");
        if (words.Contains("cancel") || (negated && words.Any(w => w is "required" or "needed" or "need" or "require" or "longer")))
        {
            return AtcFollowMeCallKind.Cancel;
        }

        return words.Any(w => RequestWords.Contains(w)) && !negated ? AtcFollowMeCallKind.Request : null;
    }
}

/// <summary>
/// Follow-me nach der Landung – nur auf Anfrage, wie meist auch in echt: Der Pilot fragt Ground ("request follow-me"), Ground lässt
/// ihn halten, bis das Fahrzeug gewendet vor ihm steht (<see cref="AtcInput.FollowMe"/> vom FollowMeService), und sagt dann "follow the
/// follow-me to gate …". Kommt keins (Modell fehlt, keine Route, Zeit um), gibt Ground die normale Rollfreigabe.
/// </summary>
public sealed partial class AtcSession
{
    private DateTimeOffset? _followMeSince;

    /// <summary>Follow-me aus Sicht von ATC (für den FollowMeService und den Status).</summary>
    public AtcFollowMe FollowMe { get; private set; }

    /// <summary>Nummer der letzten Anfrage – der FollowMeService meldet seinen Stand mit ihr (<see cref="AtcFollowMeStatus.RequestId"/>).</summary>
    public long FollowMeRequestId { get; private set; }

    /// <summary>Gate, das ATC selbst kennt (von Hand gesetzt oder aus der Rollroute), sonst null – dahin führt dann auch das Fahrzeug.</summary>
    public string? KnownGate => _overrides.GetValueOrDefault("gate") is { Length: > 0 } manual
        ? manual
        : TaxiValue("gate", _input)?.Written;

    /// <summary>
    /// Anfrage oder Abbestellung. Nach der Landung bei Ground: anfordern (auch noch beim Rollen), abbestellen; vor dem Abflug gibt es
    /// keins. Sonst (in der Luft, beim Tower) nicht für das Follow-me – false, der Funkspruch läuft normal weiter.
    /// </summary>
    private bool HandleFollowMeCall(AtcFollowMeCallKind call, AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        if (Phase is AtcPhase.Delivery or AtcPhase.GroundDeparture)
        {
            if (call != AtcFollowMeCallKind.Request)
            {
                return false;
            }

            StationContacted(now);
            Say(output, AtcMessageIds.GroundFollowMeUnable, null);
            return true;
        }

        if (Phase != AtcPhase.GroundArrival || Step is AtcStep.Done)
        {
            return false;
        }

        StationContacted(now);
        _ackFrequency = null;
        _ackPending = false;
        if (call == AtcFollowMeCallKind.Request)
        {
            if (FollowMe is AtcFollowMe.Requested or AtcFollowMe.Following)
            {
                // Schon unterwegs bzw. führt schon
                Say(output, AtcMessageIds.CommonRoger, null);
                return true;
            }

            FollowMeRequestId++;
            PendingReadback = null;
            if (input.FollowMe is null)
            {
                // Kein Fahrzeug in dieser Instanz: gleich die normale Rollfreigabe
                FollowMe = AtcFollowMe.Cancelled;
                SetStep(AtcStep.GateReadback, now);
                SayWithReadback(output, AtcMessageIds.GroundFollowMeUnavailable, null, (o, i) => SetStep(AtcStep.TaxiIn, i.TimeUtc));
                return true;
            }

            FollowMe = AtcFollowMe.Requested;
            _followMeSince = now;
            SetStep(AtcStep.FollowMeWait, now);
            Say(output, AtcMessageIds.GroundFollowMe, null);
            return true;
        }

        if (FollowMe is not (AtcFollowMe.Requested or AtcFollowMe.Following))
        {
            return false;
        }

        var waiting = Step is AtcStep.FollowMeWait or AtcStep.FollowMeReadback;
        FollowMe = AtcFollowMe.Cancelled;
        if (waiting)
        {
            // Noch nicht losgerollt: die normale Rollfreigabe zum Gate
            PendingReadback = null;
            SetStep(AtcStep.GateReadback, now);
            SayWithReadback(output, AtcMessageIds.GroundTaxiToGate, FollowMeLocals(input), (o, i) => SetStep(AtcStep.TaxiIn, i.TimeUtc));
        }
        else
        {
            Say(output, AtcMessageIds.CommonRoger, null);
        }

        return true;
    }

    /// <summary>Takt bei Ground nach der Landung: auf das Fahrzeug warten, dann "follow the follow-me" – oder aufgeben.</summary>
    private void TickFollowMe(AtcInput input, List<AtcTransmission> output)
    {
        var now = input.TimeUtc;
        if (Step == AtcStep.FollowMeReadback && MovingFor(now) >= 10)
        {
            // Rollt schon hinter dem Fahrzeug her – Readback gilt als erfolgt
            PendingReadback = null;
            SetStep(AtcStep.TaxiIn, now);
            return;
        }

        if (Step != AtcStep.FollowMeWait || FollowMe != AtcFollowMe.Requested)
        {
            return;
        }

        var vehicle = input.FollowMe is { } status && status.RequestId == FollowMeRequestId ? status.State : (AtcFollowMeVehicle?)null;
        if (vehicle is AtcFollowMeVehicle.InPosition or AtcFollowMeVehicle.Leading)
        {
            FollowMe = AtcFollowMe.Following;
            SetStep(AtcStep.FollowMeReadback, now);
            SayWithReadback(output, AtcMessageIds.GroundFollowMeFollow, FollowMeLocals(input), (o, i) => SetStep(AtcStep.TaxiIn, i.TimeUtc));
            return;
        }

        var waited = _followMeSince is { } since ? (now - since).TotalSeconds : 0;
        var noVehicle = input.FollowMe is null;
        if (noVehicle || vehicle is AtcFollowMeVehicle.Unavailable or AtcFollowMeVehicle.Finished || waited >= Math.Max(30, Rules.FollowMeTimeoutSeconds))
        {
            FollowMe = AtcFollowMe.Cancelled;
            SetStep(AtcStep.GateReadback, now);
            SayWithReadback(output, AtcMessageIds.GroundFollowMeUnavailable, FollowMeLocals(input), (o, i) => SetStep(AtcStep.TaxiIn, i.TimeUtc));
        }
    }

    /// <summary>Gate des Fahrzeugs, wenn ATC selbst keins kennt (das Fahrzeug hat sich eins gesucht).</summary>
    private Dictionary<string, AtcValue>? FollowMeLocals(AtcInput input) =>
        KnownGate is null && input.FollowMe is { Gate: { Length: > 0 } gate } status && status.RequestId == FollowMeRequestId
            ? new Dictionary<string, AtcValue> { ["gate"] = AtcSpeech.Ident(gate) }
            : null;
}
