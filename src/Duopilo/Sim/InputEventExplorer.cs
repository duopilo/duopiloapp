using System.Globalization;
using System.Text;
using Duopilo.Localization;
using Duopilo.Tools;
using Microsoft.Extensions.Options;

namespace Duopilo.Sim;

/// <summary>Ein Eintrag im Bericht: Input Event mit Wert (und vorherigem Wert, wenn er sich geändert hat).</summary>
public sealed record InputEventEntry(string Name, string Type, object? Value, object? Previous, string? Params);

/// <summary>Ergebnis von <see cref="InputEventExplorer.ExploreAsync"/>.</summary>
public sealed record InputEventReport(
    string? Aircraft,
    string? AircraftPath,
    int Total,
    int Matched,
    int? Changed,
    bool Baseline,
    string? File,
    string? Hint,
    IReadOnlyList<InputEventEntry> Events,
    bool Truncated);

/// <summary>
/// Input Events des geladenen Flugzeugs auslesen – zum Erstellen von Profilen für Flugzeuge ohne Doku. Merkt sich die Werte des
/// letzten Aufrufs je Flugzeug: Mit "nur Änderungen" zeigt der nächste Aufruf, welches Input Event sich bewegt hat, nachdem man
/// im Cockpit einen Schalter betätigt hat. Die vollständige Liste landet als JSON im Datenordner (aircraft-dumps).
/// </summary>
public sealed class InputEventExplorer(SimConnectClient sim, IOptions<DuopiloOptions> options, ILogger<InputEventExplorer> log)
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 2000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _snapshotAircraft;
    private Dictionary<ulong, object?>? _snapshot;

    /// <param name="filter">Teil des Namens (ohne Groß-/Kleinschreibung), mehrere mit Leerzeichen = alle müssen vorkommen.</param>
    /// <param name="values">Werte lesen (nötig für <paramref name="changesOnly"/>).</param>
    /// <param name="parameters">Parameter je Event abfragen (langsamer).</param>
    /// <param name="changesOnly">Nur Events, deren Wert sich seit dem letzten Aufruf geändert hat.</param>
    public async Task<InputEventReport> ExploreAsync(string? filter, bool values, bool parameters, bool changesOnly, int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            values |= changesOnly;
            limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
            var aircraft = sim.Str("title");
            var path = sim.Status.AircraftPath;
            var all = await sim.EnumerateInputEventsAsync(ct);
            if (all.Count == 0)
            {
                return new InputEventReport(aircraft, path, 0, 0, null, false, null, Loc.T("tools.inputEvents.none"), [], false);
            }

            var matched = Filter(all, filter);
            var current = values ? await sim.ReadInputEventValuesAsync(matched, ct) : null;
            var parms = parameters ? await sim.ReadInputEventParamsAsync(matched, ct) : null;

            // Vergleich mit dem letzten Aufruf (gleiches Flugzeug)
            var key = $"{aircraft}|{path}";
            var previous = current is not null && _snapshotAircraft == key ? _snapshot : null;
            if (current is not null)
            {
                var merged = previous is null ? new Dictionary<ulong, object?>() : new Dictionary<ulong, object?>(previous);
                foreach (var (hash, value) in current)
                {
                    merged[hash] = value;
                }

                _snapshot = merged;
                _snapshotAircraft = key;
            }

            var entries = matched.Select(e =>
            {
                object? value = current is not null && current.TryGetValue(e.Hash, out var v) ? Round(v) : null;
                object? before = previous is not null && previous.TryGetValue(e.Hash, out var p) ? Round(p) : null;
                var changed = previous is not null && previous.ContainsKey(e.Hash) && !Equals(value, before);
                return (Entry: new InputEventEntry(e.Name, e.Type, value, changed ? before : null, parms?.GetValueOrDefault(e.Hash)), Changed: changed);
            }).ToList();

            var changedCount = previous is null ? (int?)null : entries.Count(x => x.Changed);
            var shown = (changesOnly && previous is not null ? entries.Where(x => x.Changed) : entries).Select(x => x.Entry).ToList();
            var file = Save(aircraft, path, all, current, parms);
            log.LogInformation(Loc.Log("log.sim.inputEvents"), all.Count, matched.Count, aircraft ?? "-", changedCount?.ToString(CultureInfo.InvariantCulture) ?? "-", file ?? "-");

            var hint = changesOnly && previous is null ? Loc.T("tools.inputEvents.baseline") : null;
            return new InputEventReport(aircraft, path, all.Count, matched.Count, changedCount, previous is null && values, file, hint,
                shown.Take(limit).ToList(), shown.Count > limit);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Filter: alle Wörter müssen im Namen vorkommen (ohne Groß-/Kleinschreibung). Leer = alle.</summary>
    public static List<InputEventInfo> Filter(IReadOnlyList<InputEventInfo> all, string? filter)
    {
        var words = (filter ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all.Where(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Werte für die Ausgabe runden (Positionen sind oft 0.9999999), Texte unverändert.</summary>
    public static object? Round(object? value) => value is double d ? Math.Round(d, 4) : value;

    /// <summary>Dateiname aus dem Flugzeugtitel, nur sichere Zeichen.</summary>
    public static string SafeName(string? aircraft)
    {
        var name = new StringBuilder();
        foreach (var c in string.IsNullOrWhiteSpace(aircraft) ? "aircraft" : aircraft.Trim())
        {
            name.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        var text = name.ToString().Trim('_');
        return text.Length > 80 ? text[..80] : text.Length == 0 ? "aircraft" : text;
    }

    private string? Save(string? aircraft, string? path, IReadOnlyList<InputEventInfo> all, IReadOnlyDictionary<ulong, object?>? values,
        IReadOnlyDictionary<ulong, string?>? parms)
    {
        try
        {
            var directory = Path.Combine(options.Value.ResolveDataDirectory(), "aircraft-dumps");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, SafeName(aircraft) + ".input-events.json");
            var json = Json.Serialize(new
            {
                aircraft,
                aircraftPath = path,
                savedUtc = DateTimeOffset.UtcNow,
                count = all.Count,
                events = all.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => new
                {
                    name = e.Name,
                    type = e.Type,
                    hash = e.Hash.ToString(CultureInfo.InvariantCulture),
                    value = values is not null && values.TryGetValue(e.Hash, out var v) ? Round(v) : null,
                    @params = parms?.GetValueOrDefault(e.Hash),
                }),
            });
            File.WriteAllText(file, json, Encoding.UTF8);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(Loc.Log("log.sim.inputEventsSaveFailed"), ex.Message);
            return null;
        }
    }
}
