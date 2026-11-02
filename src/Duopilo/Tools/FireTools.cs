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
        "come from the airport's fire stations (OpenStreetMap; else a free parking position near the middle of the landing runway, " +
        "vehicle stands first, gates last, out of sight) over the service roads and taxiways, drive " +
        "with blue lights to the runway holding points (touchdown zone, middle, expected stopping point, runway end), and after touchdown " +
        "follow the aircraft's actual track (not a planned route) with a gap, below 40 kt. When the aircraft stops they take their posts " +
        "around it (crash tenders fore and aft of the main gear, command car ahead of the nose); when it taxis on, FLF 1 follows to the " +
        "stand and the others return; a minute at the stand and all return. Vehicles per level and ICAO fire category (from the aircraft " +
        "length): up to cat 5 one crash tender, 6–7 two, 8+ three, plus command car; full emergency adds rescue/fire engine, rescue " +
        "stairs and ambulance. 'cancel Mayday' or 'cancel Pan Pan' stands them down. Only the tray instance runs it. action status: state " +
        "(idle, armed, planning, dispatching, responding, standby, following, at_aircraft, returning, finished, unavailable with error), " +
        "level, airport, runway, category, spawn parking, vehicles (unit, model, phase, speed, gap). action alert: developer test without " +
        "the radio: level (local, full, ground), airport (else the nearest airport of the SimBrief plan), runway (else the one the " +
        "aircraft is lined up for), tail (follow an AI aircraft of a running traffic test instead of the own aircraft, e.g. DUO01 of an " +
        "A3 landing with taxi-in – airport and runway then come from the test), parking (a ground incident at this parking spot " +
        "instead of at an aircraft, e.g. V61A – they take their posts around it as if an aircraft stood there). action cancel: all vehicles return to the station. action reset: all vehicles disappear at once " +
        "(also while paused), to restart a test.")]
    public static string Fire(
        FireService fire,
        [Description("status (default), alert, cancel or reset.")] string action = "status",
        [Description("alert: local (Pan Pan, default), full (Mayday) or ground (incident on the ground, the aircraft must be on the ground).")] string? level = null,
        [Description("alert: airport ICAO, empty = the nearest airport of the SimBrief plan.")] string? airport = null,
        [Description("alert: landing runway, e.g. 23R; empty = the runway the aircraft is lined up for (with tail: the test's runway).")] string? runway = null,
        [Description("alert: tail of a running traffic test (DUO01 …) to follow instead of the own aircraft; empty = own aircraft.")] string? tail = null,
        [Description("alert: parking spot of the airport as the scene of a ground incident instead of an aircraft, e.g. V61A (traffic_taxi_info lists them); level is then always ground.")] string? parking = null)
    {
        try
        {
            var info = (action ?? "status").Trim().ToLowerInvariant() switch
            {
                "alert" => fire.Alert(level, airport, runway, tail, parking),
                "cancel" => fire.Cancel(),
                "reset" => fire.Reset(),
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
