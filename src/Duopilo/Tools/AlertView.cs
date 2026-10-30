using Kopilot.Monitoring;

namespace Kopilot.Tools;

/// <summary>Kompakte Darstellung von Monitoring-Meldungen in Tool-Antworten.</summary>
internal static class AlertView
{
    /// <summary>Aktive Alerts für das Feld "activeAlerts", das jede Tool-Antwort mitliefert.</summary>
    public static IReadOnlyList<object> Active(AlertBus bus) => bus.Active().Select(Compact).ToList();

    public static object Compact(AlertEvent e) => new
    {
        id = e.Id,
        rule = e.Rule,
        severity = e.Severity,
        callout = e.Callout,
        message = e.Message,
        timeUtc = e.TimeUtc.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z",
    };

    public static object Full(AlertEvent e) => new
    {
        id = e.Id,
        kind = e.Kind,
        rule = e.Rule,
        severity = e.Severity,
        callout = e.Callout,
        message = e.Message,
        timeUtc = e.TimeUtc.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z",
    };
}
