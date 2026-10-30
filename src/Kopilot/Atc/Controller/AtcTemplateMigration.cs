using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kopilot.Atc;

/// <summary>
/// Hebt atc-templates.json beim Laden auf das aktuelle Format (<see cref="AtcTemplates.CurrentVersion"/>). Die Datei auf der
/// Platte bleibt unverändert; was angepasst wurde, steht in den Hinweisen (Log, get_atc_controller).
/// </summary>
public static class AtcTemplateMigration
{
    /// <summary>
    /// Version 1 → 2: Gander ist geteilt. Bisher stand unter "CZQX" die Oceanic-Stelle; jetzt ist CZQX Gander Center
    /// (Domestic, nach dem Oceanic-Ausgang) und die Oceanic-Stelle heißt wie in VAT-Spy "CZQO". Ein alter Eintrag
    /// "CZQX" mit "oceanic": true wandert samt Frequenz und fester Stimme nach "CZQO". Die übrigen Neuerungen (FIR-Zusätze,
    /// Sektor-Regel, weitere FIRs) kommen über die mitgelieferte Grundlage dazu, Rufzeichen aus OpenFlights.
    /// </summary>
    public static JsonObject Migrate(JsonObject root, List<string> notes, string label)
    {
        var version = Version(root);
        if (version >= AtcTemplates.CurrentVersion)
        {
            return root;
        }

        if (version < 2)
        {
            if (Child(root, "firs") is JsonObject firs && Key(firs, "CZQX") is { } czqxKey && firs[czqxKey] is JsonObject czqx
                && IsTrue(czqx, "oceanic"))
            {
                firs.Remove(czqxKey);
                if (Key(firs, "CZQO") is null)
                {
                    firs["CZQO"] = czqx;
                    notes.Add($"{label}: Gander Oceanic von \"CZQX\" nach \"CZQO\" übernommen – CZQX ist jetzt Gander Center (Domestic).");
                }
                else
                {
                    notes.Add($"{label}: alter Oceanic-Eintrag \"CZQX\" entfernt – CZQX ist jetzt Gander Center (Domestic), Oceanic steht unter \"CZQO\".");
                }
            }

            if (Child(root, "voices") is JsonObject voices && Child(voices, "stations") is JsonObject stations
                && Key(stations, "FIR/CZQX") is { } voiceKey && Key(stations, "FIR/CZQO") is null)
            {
                var voice = stations[voiceKey];
                stations.Remove(voiceKey);
                stations["FIR/CZQO"] = voice;
                notes.Add($"{label}: feste Stimme von FIR/CZQX nach FIR/CZQO übernommen.");
            }
        }

        root["version"] = AtcTemplates.CurrentVersion;
        return root;
    }

    private static int Version(JsonObject root)
    {
        if (Key(root, "version") is not { } key || root[key] is not JsonValue value)
        {
            return 1;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<double>(out var real))
        {
            return (int)real;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : 1;
    }

    private static bool IsTrue(JsonObject node, string name) =>
        Key(node, name) is { } key && node[key] is JsonValue value
        && ((value.TryGetValue<bool>(out var flag) && flag) || (value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed));

    private static JsonNode? Child(JsonObject node, string name) => Key(node, name) is { } key ? node[key] : null;

    private static string? Key(JsonObject node, string name) =>
        node.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
}
