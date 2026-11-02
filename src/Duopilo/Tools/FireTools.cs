using System.ComponentModel;
using Duopilo.Fire;
using ModelContextProtocol.Server;

namespace Duopilo.Tools;

/// <summary>Flughafenfeuerwehr: Stand abfragen, zum Testen ohne Funk alarmieren und abbrechen.</summary>
[McpServerToolType]
public static class FireTools
{
    [McpServerTool(Name = "fire_service", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description(
        "Airport fire service (Duopilo's own vehicles from the duopilo-fire package in the Community folder). Normally the radio triggers " +
        "it: a Mayday in the ATC module alerts a full emergency, a Pan Pan a local standby, an emergency declared on the ground a ground " +
        "incident; once the aircraft is within Duopilo:Fire:DispatchRangeNm (20 NM) of the landing airport the vehicles turn out. They " +
        "spawn on a free parking position near the middle of the landing runway (vehicle stands first, gates last, out of sight), drive " +
        "with blue lights to the runway holding points (touchdown zone, middle, expected stopping point, runway end), and after touchdown " +
        "follow the aircraft's actual track (not a planned route) with a gap, below 40 kt. When the aircraft stops they take their posts " +
        "around it (crash tenders fore and aft of the main gear, command car ahead of the nose); when it taxis on, FLF 1 follows to the " +
        "stand and the others return; a minute at the stand and all return. Vehicles per level and ICAO fire category (from the aircraft " +
        "length): up to cat 5 one crash tender, 6–7 two, 8+ three, plus command car; full emergency adds rescue/fire engine, rescue " +
        "stairs and ambulance. 'cancel Mayday' or 'cancel Pan Pan' stands them down. Only the tray instance runs it. action status: state " +
        "(idle, armed, planning, dispatching, responding, standby, following, at_aircraft, returning, finished, unavailable with error), " +
        "level, airport, runway, category, spawn parking, vehicles (unit, model, phase, speed, gap). action alert: developer test without " +
        "the radio: level (local, full, ground), airport (else the nearest airport of the SimBrief plan), runway (else the one the " +
        "aircraft is lined up for). action cancel: all vehicles return.")]
    public static string Fire(
        FireService fire,
        [Description("status (default), alert or cancel.")] string action = "status",
        [Description("alert: local (Pan Pan, default), full (Mayday) or ground (incident on the ground, the aircraft must be on the ground).")] string? level = null,
        [Description("alert: airport ICAO, empty = the nearest airport of the SimBrief plan.")] string? airport = null,
        [Description("alert: landing runway, e.g. 23R; empty = the runway the aircraft is lined up for.")] string? runway = null)
    {
        try
        {
            var info = (action ?? "status").Trim().ToLowerInvariant() switch
            {
                "alert" => fire.Alert(level, airport, runway),
                "cancel" => fire.Cancel(),
                _ => fire.Status(),
            };
            return Json.Serialize(info);
        }
        catch (InvalidOperationException ex)
        {
            return Json.Serialize(new { error = ex.Message, fire = fire.Status() });
        }
    }
}
