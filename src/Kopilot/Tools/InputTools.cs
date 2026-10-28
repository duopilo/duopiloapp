using System.ComponentModel;
using Kopilot.Input;
using ModelContextProtocol.Server;

namespace Kopilot.Tools;

/// <summary>Eingabe-Modul: Cockpit bedienen – nur nach Readback und Bestätigung durch den Piloten.</summary>
[McpServerToolType]
public static class InputTools
{
    private const string Rules =
        "Two-step rule: prepare_action returns a readback. Say exactly that readback to the pilot and ask for confirmation. " +
        "Only call confirm_action after the pilot clearly answers 'confirm' (or 'affirm', 'yes', 'correct'). " +
        "If the pilot corrects, hesitates or says no, call cancel_action. Never confirm on your own initiative, never chain prepare and confirm without the pilot's answer in between.";

    [McpServerTool(Name = "list_actions", ReadOnly = true)]
    [Description(
        "Lists the cockpit inputs Kopilot may operate in the current aircraft (FCU, EFIS, gear/flaps/speedbrake, lights, signs, anti-ice, packs, APU, " +
        "transponder/TCAS, radios, baro, autobrake). Engine masters, fire handles, fuel and the MCDU are never available. " +
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
                verified = a.Verified,
                interlocks = a.Interlocks.Count > 0 ? a.Interlocks : null,
            })
            .ToList();

        return Json.Serialize(new
        {
            enabled = input.Enabled,
            note = input.Enabled ? null : "Eingaben sind gesperrt – der Pilot kann sie im Kopilot-Dashboard freigeben.",
            areas = input.Actions.Select(a => a.Area).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            actions,
            pending = Pending(input),
        });
    }

    [McpServerTool(Name = "prepare_action", Destructive = false, Idempotent = false)]
    [Description(
        "Step 1 of a cockpit input: checks the action, its value and the interlocks (no gear up on ground, no flaps above VFE, gear only below VLO) " +
        "and returns a readback plus a token valid for about 60 s. Nothing is changed in the aircraft yet. " + Rules)]
    public static async Task<string> PrepareAction(
        InputService input,
        [Description("Action id from list_actions, e.g. 'fcu.speed', 'fcu.altitude', 'gear.down', 'flaps', 'lights.landing'.")] string action,
        [Description("Value if the action needs one: a number (e.g. '250', '5000', 'FL120', '1013', '121.800', '7000') or an option from list_actions (e.g. 'on', 'off', '2', 'FULL').")] string? value = null,
        CancellationToken cancellationToken = default)
    {
        var result = await input.PrepareAsync(action, value, cancellationToken);
        return Json.Serialize(View(result, result.Status == InputResult.AwaitingConfirmation
            ? "Read this back to the pilot now and wait for 'confirm'. Then call confirm_action with the token and the pilot's words."
            : null));
    }

    [McpServerTool(Name = "confirm_action", Destructive = true, Idempotent = false)]
    [Description(
        "Step 2: executes the prepared action after the pilot confirmed the readback. Interlocks are checked again with the current state. " +
        "Afterwards Kopilot reads the aircraft state back ('confirmed': true = aircraft shows the new value). " + Rules)]
    public static async Task<string> ConfirmAction(
        InputService input,
        [Description("Token from prepare_action.")] string token,
        [Description("The pilot's answer to the readback, word for word, e.g. 'confirm'.")] string pilotConfirmation,
        CancellationToken cancellationToken = default)
    {
        var result = await input.ConfirmAsync(token, pilotConfirmation, cancellationToken);
        return Json.Serialize(View(result, null));
    }

    [McpServerTool(Name = "cancel_action", Destructive = false, Idempotent = true)]
    [Description("Discards the prepared action (pilot said no, corrected the value, or the situation changed).")]
    public static string CancelAction(
        InputService input,
        [Description("Token from prepare_action. Empty = cancel whatever is pending.")] string? token = null)
    {
        return Json.Serialize(View(input.Cancel(string.IsNullOrWhiteSpace(token) ? null : token, "Abgebrochen (Claude)"), null));
    }

    internal static object View(InputResult r, string? instruction) => new
    {
        status = r.Status,
        reason = r.Reason,
        token = r.Token,
        action = r.Action,
        value = r.Value,
        readback = r.Readback,
        expiresInSec = r.ExpiresInSec,
        aircraftConfirmed = r.Confirmed,
        stateAfter = r.StateAfter,
        alreadySet = r.AlreadySet ? true : (bool?)null,
        skippedSteps = r.Skipped is > 0 ? r.Skipped : null,
        checkedBy = r.CheckIndependent switch
        {
            true => "aircraft state",
            false => "switch variable only",
            null => null,
        },
        bindingVerified = r.Verified,
        instruction,
    };

    private static object? Pending(InputService input) => input.Pending is { } p
        ? new { token = p.Token, action = p.Plan.Action.Id, value = p.Plan.ValueText, readback = p.Plan.Readback, expiresInSec = Math.Max(0, (int)(p.ExpiresUtc - DateTimeOffset.UtcNow).TotalSeconds) }
        : null;
}
