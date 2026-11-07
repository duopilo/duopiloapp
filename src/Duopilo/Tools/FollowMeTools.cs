using System.ComponentModel;
using Duopilo.Vehicles;
using ModelContextProtocol.Server;

namespace Duopilo.Tools;

/// <summary>Follow-me: Stand abfragen, zum Testen ohne Funk anfordern und abbestellen.</summary>
[McpServerToolType]
public static class FollowMeTools
{
    [McpServerTool(Name = "follow_me", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description(
        "Follow-me vehicle after landing. Normally the pilot requests it on the radio – it only ever comes on request, never on its own: " +
        "after vacating, call ground with 'request follow-me' (also while taxiing in); ground answers 'hold position, follow-me is on the " +
        "way', the vehicle (Duopilo's own van, model Duopilo:FollowMe:Model from the Duopilo vehicle package in the Community folder) comes " +
        "towards the aircraft over the taxiways, turns around in front of it and ground says 'follow the follow-me to gate …' (gate as ATC " +
        "knows it, else a free one that fits the type, the nearest of the first fitting ones). It drives ahead keeping its gap (35 m light, " +
        "60 m medium, 80 m heavy, 95 m super, from the aircraft's reference point along the route): faster when the aircraft closes in, it " +
        "waits when the aircraft stops; beacon and headlights on. At the gate it drives 40 m past the stand entry, waits until the aircraft " +
        "is there, drives off and disappears. 'cancel follow-me' or 'follow-me not required' sends it away. Without a vehicle (package " +
        "missing, no route, gate too close, nothing in front of the aircraft after 4 minutes) ground says 'follow-me not available' with " +
        "the taxi clearance. Only the tray instance runs it. action status: state (idle, planning, approach, in_position, leading, at_stand, " +
        "driving_off, leaving, finished, unavailable with error), source (atc/tool), gate, taxiways, route length, gap and target gap, " +
        "speed. action request: developer test without the radio at any airport (on the ground on a taxiway): airport (else the nearer " +
        "airport of the SimBrief plan) and gate (else a free one). action cancel: the vehicle drives off.")]
    public static string FollowMe(
        FollowMeService followMe,
        [Description("status (default), request or cancel.")] string action = "status",
        [Description("request: airport ICAO, empty = the nearer airport of the SimBrief plan.")] string? airport = null,
        [Description("request: parking position/gate label, e.g. A12; empty = a free one that fits the aircraft.")] string? gate = null)
    {
        try
        {
            var info = (action ?? "status").Trim().ToLowerInvariant() switch
            {
                "request" => followMe.Request(airport, gate),
                "cancel" => followMe.Cancel(),
                _ => followMe.Status(),
            };
            return Json.Serialize(info);
        }
        catch (InvalidOperationException ex)
        {
            return Json.Serialize(new { error = ex.Message, followMe = followMe.Status() });
        }
    }
}
