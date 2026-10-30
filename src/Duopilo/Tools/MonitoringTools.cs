using System.ComponentModel;
using Duopilo.Ecam;
using Duopilo.Localization;
using Duopilo.Monitoring;
using Duopilo.Sim;
using ModelContextProtocol.Server;

namespace Duopilo.Tools;

/// <summary>Pilot Monitoring: Warnungen, ECAM und die Meldungen der Monitoring-Regeln.</summary>
[McpServerToolType]
public static class MonitoringTools
{
    [McpServerTool(Name = "get_warnings", ReadOnly = true)]
    [Description(
        "Warnings and cautions: master warning/caution lights and the ECAM messages in plain text (left lower E/WD area: warnings with title and " +
        "procedure lines; right area: memos), plus stall/overspeed. ECAM decoding is available for the FlyByWire A32NX and the Headwind A330-900neo. " +
        "Read titles as callouts, e.g. 'Master caution, AUTO FLT AP OFF'.")]
    public static string GetWarnings(SimConnectClient sim, EcamCatalog catalog, AlertBus alerts)
    {
        if (!sim.Connection.IsConnected)
        {
            return DuopiloTools.NotConnected(alerts);
        }

        var signals = new AircraftSignals(sim);
        var ecam = EcamReader.Read(signals, catalog);
        return Json.Serialize(new
        {
            connected = true,
            masterWarning = signals.MasterWarning,
            masterCaution = signals.MasterCaution,
            stall = sim.Bool("stallWarning"),
            overspeed = sim.Bool("overspeedWarning"),
            ecam = ecam.Available
                ? new
                {
                    table = ecam.TableStatus,
                    titles = ecam.Titles,
                    warnings = ecam.Left.Select(l => new { text = l.Text, title = l.IsTitle, code = l.Code.ToString("D9", System.Globalization.CultureInfo.InvariantCulture), known = l.Known }),
                    memos = ecam.Right.Select(l => new { text = l.Text, code = l.Code.ToString("D9", System.Globalization.CultureInfo.InvariantCulture), known = l.Known }),
                }
                : null,
            message = ecam.Available ? null : Loc.T("tools.monitor.noEcam"),
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_alerts", ReadOnly = true)]
    [Description(
        "Monitoring alerts raised by Duopilo (gear/flap speed, speed limit below FL100, speed deviation, sink rate, bank angle, autopilot off, " +
        "master warning/caution with the newest ECAM message, new ECAM messages, one thousand to go, top of descent, landing checklist at 2000/1000 ft, " +
        "runway assigned by ATC, sim paused). Returns events with id > sinceId (oldest first) and the " +
        "currently active alerts. Each event has a short English 'callout' to say aloud (null for silent events such as 'Clear'). " +
        "Remember 'lastId' and pass it as sinceId next time.")]
    public static string GetAlerts(
        AlertBus alerts,
        [Description("Only return events with an id greater than this. 0 = recent history.")] long sinceId = 0,
        [Description("Maximum number of events (newest are kept). Default 30.")] int max = 30)
    {
        var events = alerts.Since(sinceId, Math.Clamp(max, 1, 200));
        return Json.Serialize(new
        {
            lastId = alerts.LastId,
            events = events.Select(AlertView.Full),
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "wait_for_alerts", ReadOnly = true)]
    [Description(
        "Waits until Duopilo raises a new monitoring alert after sinceId (long poll, max 25 s), then returns the new events. " +
        "Use in a loop to monitor the flight continuously: call with the returned lastId each time. Returns an empty list on timeout.")]
    public static async Task<string> WaitForAlerts(
        AlertBus alerts,
        [Description("Last seen event id (from get_alerts or a previous wait_for_alerts). Use -1 to start from now.")] long sinceId = -1,
        [Description("Maximum wait in seconds (1–25). Default 20.")] int timeoutSec = 20,
        CancellationToken cancellationToken = default)
    {
        var since = sinceId < 0 ? alerts.LastId : sinceId;
        var events = await alerts.WaitAsync(since, TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 25)), cancellationToken);
        return Json.Serialize(new
        {
            lastId = alerts.LastId,
            timedOut = events.Count == 0,
            events = events.Select(AlertView.Full),
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_fma_log", ReadOnly = true)]
    [Description(
        "History of flight mode annunciator changes (thrust, vertical, lateral, armed modes, AP, FD, A/THR) with UTC time, IAS, altitude and radio altitude " +
        "at each change. Use it to check what the autoflight system did, e.g. after an autoland whether LAND, FLARE and ROLL OUT were active.")]
    public static string GetFmaLog(
        FmaLog fmaLog,
        AlertBus alerts,
        [Description("Number of most recent changes to return (1–200). Default 40.")] int last = 40)
    {
        var entries = fmaLog.Last(Math.Clamp(last, 1, 200));
        var since = DateTimeOffset.UtcNow.AddMinutes(-30);
        return Json.Serialize(new
        {
            entries = entries.Select(e => new
            {
                id = e.Id,
                timeUtc = e.TimeUtc.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z",
                changes = e.Changes,
                fma = FmaLog.Describe(e.State),
                iasKt = Fmt.Int(e.IasKt),
                altitudeFt = Fmt.Int(e.AltitudeFt),
                radioAltFt = e.RadioAltFt is < 5000 ? Fmt.Int(e.RadioAltFt) : null,
                onGround = e.OnGround,
            }),
            last30Minutes = new
            {
                land = fmaLog.Seen("LAND", since),
                flare = fmaLog.Seen("FLARE", since),
                rollOut = fmaLog.Seen("ROLL OUT", since),
            },
            activeAlerts = AlertView.Active(alerts),
        });
    }

    [McpServerTool(Name = "get_monitoring_status", ReadOnly = true)]
    [Description("State of Duopilo's monitoring: enabled, detected flight phase, enabled/disabled rules, quiet period after loading or unpausing.")]
    public static string GetMonitoringStatus(MonitoringService monitoring, AlertBus alerts)
    {
        var now = DateTimeOffset.UtcNow;
        return Json.Serialize(new
        {
            enabled = monitoring.Enabled,
            intervalMs = monitoring.IntervalMs,
            phase = monitoring.Phase,
            lastEvaluationUtc = monitoring.LastEvaluationUtc,
            quietForSec = monitoring.SuppressedUntilUtc > now ? Math.Round((monitoring.SuppressedUntilUtc - now).TotalSeconds, 1) : 0,
            enabledRules = monitoring.EnabledRules,
            disabledRules = monitoring.DisabledRules,
            lastId = alerts.LastId,
            activeAlerts = AlertView.Active(alerts),
        });
    }
}
