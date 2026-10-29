using Kopilot.FlightPlan;

namespace Kopilot.Atc;

/// <summary>
/// Was die Sprachschleife braucht, um Funksprüche an ATC zu erkennen: die Stationsnamen des Flugs (aktuelle Station,
/// Stationen an Start und Ziel, alle FIR-Kontrollstellen aus den Vorlagen) und das eigene Rufzeichen als Code ("DLH4AB")
/// und in der Sprechform ("Lufthansa four alpha bravo"). Rein rechnend – damit testbar.
/// </summary>
/// <param name="LastAtcUtc">Zeit des letzten Funkspruchs einer Station (für "say again" ohne Stationsnamen).</param>
public sealed record AtcRoutingInfo(IReadOnlyList<string> Stations, IReadOnlyList<string> Callsigns, DateTimeOffset? LastAtcUtc = null)
{
    public static readonly AtcRoutingInfo Empty = new([], []);

    private static readonly AtcStationType[] AirportStations =
    [
        AtcStationType.Delivery, AtcStationType.Ground, AtcStationType.Tower, AtcStationType.Departure, AtcStationType.Approach,
    ];

    /// <param name="templates">Geladene Vorlagen (null = noch keine, dann nur das Rufzeichen).</param>
    /// <param name="session">Laufende Sitzung oder null.</param>
    /// <param name="ofp">SimBrief-Plan, falls noch keine Sitzung läuft.</param>
    /// <param name="simCallsign">Rufzeichen aus MSFS, falls der Plan keins hat.</param>
    public static AtcRoutingInfo From(AtcTemplates? templates, AtcSession? session, Ofp? ofp, string? simCallsign)
    {
        var plan = session?.Plan.Ofp ?? ofp;
        var stations = new List<string>();
        if (session is not null)
        {
            stations.Add(session.Station.Name);
        }

        if (templates is not null)
        {
            if (plan is not null)
            {
                var names = new AtcStations(templates, _ => null, new AtcVoiceAssigner(templates.Voices, "routing", null, null));
                foreach (var airport in new[] { plan.Origin, plan.Destination })
                {
                    if (string.IsNullOrWhiteSpace(airport.Icao))
                    {
                        continue;
                    }

                    var name = names.AirportName(airport.Icao, airport.Name);
                    foreach (var type in AirportStations)
                    {
                        var custom = templates.Airport(airport.Icao)?.Stations.GetValueOrDefault(AtcStations.TypeKey(type));
                        stations.Add(custom is { Length: > 0 } ? custom.Trim() : $"{name} {templates.StationType(type).Name}".Trim());
                    }
                }
            }

            stations.AddRange(templates.Firs.Values.Select(f => f.Name).OfType<string>());
        }

        var callsigns = new List<string>();
        var code = session?.Plan.Callsign;
        if (string.IsNullOrWhiteSpace(code))
        {
            code = AtcPlan.CallsignCode(plan, simCallsign);
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            callsigns.Add(code);
            if (templates is not null)
            {
                callsigns.Add(AtcSpeech.Callsign(code, templates.Telephony).Spoken);
            }
        }

        var lastAtc = session?.Transcript.LastOrDefault(t => t.From == "atc")?.TimeUtc;
        return new AtcRoutingInfo(
            stations.Where(s => IsUsableStation(s, templates))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            callsigns.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            lastAtc);
    }

    // Nur die Stationsart ("Oceanic", "Tower") oder der Name einer generischen Station ("Center") ist zu allgemein, um
    // daran einen Funkspruch zu erkennen – "runway two five center" ist kein Anruf
    private static bool IsUsableStation(string? name, AtcTemplates? templates)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Contains(' ', StringComparison.Ordinal) || templates is null)
        {
            return true;
        }

        return !templates.StationTypes.Values.Any(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            && !new[] { AtcStations.GenericOceanicFir, AtcStations.GenericCenterFir }
                .Any(g => string.Equals(templates.Fir(g)?.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
