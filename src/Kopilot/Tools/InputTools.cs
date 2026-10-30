using System.ComponentModel;
using Kopilot.Input;
using Kopilot.Localization;
using ModelContextProtocol.Server;

namespace Kopilot.Tools;

/// <summary>Eingabe-Modul: Cockpit bedienen – nur nach Readback und Bestätigung durch den Piloten.</summary>
[McpServerToolType]
public static class InputTools
{
    private const string Rules =
        "Two-step rule: prepare_action returns a readback. Say exactly that readback to the pilot and ask for confirmation. " +
        "Only call confirm_action after the pilot clearly answers 'confirm' (or 'affirm', 'yes', 'correct'). " +
        "If the pilot corrects, hesitates or says no, call cancel_action. Never confirm on your own initiative, never chain prepare and confirm without the pilot's answer in between. " +
        "Critical actions (transponder/TCAS standby or ALT RPTG off in flight, squawk 7500/7600/7700, ADIRS off on ground (refused in flight), AP/ATHR off in flight, " +
        "FCU altitude below MSA or outside the ATC clearance, missing aircraft limits) need a second confirmation: after the first confirm_action " +
        "the status is 'awaiting_caution_confirmation' – read the 'caution' text to the pilot word for word and call confirm_action again with the same token " +
        "only after the pilot confirms again.";

    [McpServerTool(Name = "list_actions", ReadOnly = true)]
    [Description(
        "Lists the cockpit inputs Kopilot may operate in the current aircraft (FCU, EFIS, gear/flaps/speedbrake, lights, signs, anti-ice, packs, APU, " +
        "transponder/TCAS, radios, baro, autobrake). Engine masters, fire handles, fuel pumps/crossfeed, hydraulics, IDG and the MCDU are never available. " +
        "'safety' names the built-in plausibility locks and cautions of an action. " +
        "Shows whether input is enabled; it is disabled by default and only the pilot can enable it in the Kopilot dashboard. " + Rules)]
    public static string ListActions(
        InputService input,
        [Description("Optional area filter, e.g. FCU, EFIS, Config, Lights, Signs, AntiIce, Air, APU, Transponder, Radio, Brakes.")] string? area = null)
    {
        var actions = input.Actions
            .Where(a => string.IsNullOrWhiteSpace(area) || a.Area.Equals(area.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(a => new
            {
                id = a.Id,
                area = a.Area,
                label = a.Label,
                value = a.Parameter is { } p ? ActionPlanner.Describe(p) : null,
                verified = input.VerifiedBy(a),
                interlocks = a.Interlocks.Count > 0 ? a.Interlocks : null,
                safety = SafetyRules.Describe(a.Id),
            })
            .ToList();

        return Json.Serialize(new
        {
            enabled = input.Enabled,
            note = input.Enabled ? null : Loc.T("tools.input.disabledNote"),
            areas = input.Actions.Select(a => a.Area).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            actions,
            pending = Pending(input),
        });
    }

    [McpServerTool(Name = "prepare_action", Destructive = false, Idempotent = false)]
    [Description(
        "Step 1 of a cockpit input: checks the action, its value and the interlocks (no gear up on ground, no flaps above VFE, gear only below VLO " +
        "and the gear altitude limit, no speedbrake in inhibited flap settings, batteries off and both packs off only on ground, APU master off not with APU bleed on) " +
        "and returns a readback plus a token valid for about 60 s. Nothing is changed in the aircraft yet. 'cautionFollows' = a second confirmation with a caution follows. " +
        "Only one action is open at a time: every new prepare_action discards the open one (even if the new one is refused), " +
        "so always confirm with the token of the latest successful prepare_action, through the same Kopilot connection. " + Rules)]
    public static async Task<string> PrepareAction(
        InputService input,
        ILogger<InputService> log,
        [Description("Action id from list_actions, e.g. 'fcu.speed', 'fcu.altitude', 'gear.down', 'flaps', 'lights.landing'.")] string? action = null,
        [Description("Value if the action needs one: a number (e.g. '250', '5000', 'FL120', '1013', '121.800', '7000') or an option from list_actions (e.g. 'on', 'off', '2', 'FULL').")] string? value = null,
        CancellationToken cancellationToken = default)
    {
        return await Guard(log, "prepare_action", async () =>
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                return View(new InputResult(InputResult.Refused, Loc.T("tools.input.actionMissing")), null);
            }

            var result = await input.PrepareAsync(action, value, cancellationToken);
            return View(result, result.Status == InputResult.AwaitingConfirmation
                ? result.CautionFollows
                    ? Loc.T("tools.input.readBackThenCaution")
                    : Loc.T("tools.input.readBack")
                : null);
        });
    }

    [McpServerTool(Name = "confirm_action", Destructive = true, Idempotent = false)]
    [Description(
        "Step 2: executes the prepared action after the pilot confirmed the readback. Interlocks are checked again with the current state. " +
        "For critical actions the first call returns 'awaiting_caution_confirmation' with a 'caution' text instead of executing – read it to the pilot, " +
        "and call confirm_action again with the same token after the pilot confirms. " +
        "After gear and flaps Kopilot reads the position back and calls it out itself ('Gear down, three green', 'Flaps two', or 'Gear disagree') – see 'followUp'. " +
        "Afterwards Kopilot reads the aircraft state back ('verification': confirmed/unverified/failed). 'stateAfter' is the raw value of the checked variable – " +
        "for selector switches the position number (e.g. 0 = ON on a 3-position ON/AUTO/OFF switch), so never read it as on/off; " +
        "'stateAfterText' names the matching option. " + Rules)]
    public static async Task<string> ConfirmAction(
        InputService input,
        ILogger<InputService> log,
        [Description("Token from prepare_action.")] string? token = null,
        [Description("The pilot's answer to the readback, word for word, e.g. 'confirm'.")] string? pilotConfirmation = null,
        CancellationToken cancellationToken = default)
    {
        return await Guard(log, "confirm_action", async () =>
        {
            var result = await input.ConfirmAsync(token, pilotConfirmation, cancellationToken);
            return View(result, result.Status == InputResult.AwaitingCautionConfirmation
                ? Loc.T("tools.input.readCaution")
                : null);
        });
    }

    [McpServerTool(Name = "mark_action_verified", Destructive = false, Idempotent = true)]
    [Description(
        "Stores an action as verified after the pilot has seen in the cockpit that it really worked (e.g. the pushbutton light came on, the switch moved). " +
        "Use it for actions where confirm_action reported verification 'unverified' (Kopilot could only read the switch variable). " +
        "Actions with a real aircraft-state check are stored automatically. Only call this when the pilot explicitly says it worked.")]
    public static async Task<string> MarkActionVerified(
        InputService input,
        ILogger<InputService> log,
        [Description("Action id, e.g. 'apu.bleed'.")] string? action = null,
        [Description("The pilot's words confirming it worked, word for word, e.g. 'confirm, APU bleed is on', 'seatbelts on', 'APU avail', 'check'. " +
                     "A confirmation or state word (confirm, check, on, off, avail, läuft) or the action's name is enough, as long as nothing is negated.")] string? pilotConfirmation = null)
    {
        return await Guard(log, "mark_action_verified", () => Task.FromResult(View(input.MarkVerified(action, pilotConfirmation), null)));
    }

    [McpServerTool(Name = "cancel_action", Destructive = false, Idempotent = true)]
    [Description("Discards the prepared action (pilot said no, corrected the value, or the situation changed).")]
    public static string CancelAction(
        InputService input,
        [Description("Token from prepare_action. Empty = cancel whatever is pending.")] string? token = null)
    {
        return Json.Serialize(View(input.Cancel(string.IsNullOrWhiteSpace(token) ? null : token, Loc.T("tools.input.cancelledByClaude")), null));
    }

    /// <summary>Jede Ausnahme wird zu einer strukturierten Antwort (status "error") – nie ein nackter MCP-Fehler.</summary>
    private static async Task<string> Guard(ILogger log, string tool, Func<Task<object>> body)
    {
        try
        {
            return Json.Serialize(await body());
        }
        catch (Exception ex)
        {
            log.LogError(ex, Loc.Log("log.input.toolFailed"), tool);
            return Json.Serialize(new { status = InputResult.Error, reason = Loc.T("tools.input.internalError", tool, ex.Message) });
        }
    }

    internal static object View(InputResult r, string? instruction) => new
    {
        status = r.Status,
        reason = r.Reason,
        token = r.Token,
        action = r.Action,
        value = r.Value,
        readback = r.Readback,
        caution = r.Caution,
        cautions = r.Cautions,
        cautionFollows = r.CautionFollows ? true : (bool?)null,
        expiresInSec = r.ExpiresInSec,

        // confirmed = echter Flugzeugzustand geprüft; unverified = nur die Schalterstellung steht, Wirkung nicht nachgewiesen
        verification = r.Status != InputResult.Executed
            ? null
            : r.Confirmed switch
            {
                true when r.CheckIndependent == true => "confirmed",
                false => "failed",
                _ => "unverified",
            },
        aircraftConfirmed = r.Confirmed == false ? false : r.CheckIndependent == true ? r.Confirmed : null,

        // Rohwert der geprüften Variable (bei Wahlschaltern die Stellung, z. B. 0 = ON) und der Name der passenden Auswahl
        stateAfter = r.StateAfter,
        stateAfterText = r.StateAfterText,
        alreadySet = r.AlreadySet ? true : (bool?)null,
        skippedSteps = r.Skipped is > 0 ? r.Skipped : null,
        checkedBy = r.CheckIndependent switch
        {
            true => "aircraft state",
            false => "switch variable only",
            null => null,
        },
        actionVerified = r.Verified,
        followUp = r.FollowUp,
        instruction,
    };

    private static object? Pending(InputService input) => input.Pending is { } p
        ? new { token = p.Token, action = p.Plan.Action.Id, value = p.Plan.ValueText, readback = p.Prompt, cautionStage = p.CautionStage, expiresInSec = Math.Max(0, (int)(p.ExpiresUtc - DateTimeOffset.UtcNow).TotalSeconds) }
        : null;
}
