using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>
/// Merkt sich die Texte (ATC ID, Airline, Flugnummer, Typ) je Objekt-ID aus get_traffic und ergänzt sie im
/// Hintergrundscan der Verkehrswarnung, der aus Sicherheitsgründen nur Zahlen abfragt. Threadsicher.
/// </summary>
public sealed class TrafficIdentCache
{
    /// <summary>So lange gilt ein Eintrag, nachdem das Objekt zuletzt gesehen wurde.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<uint, Entry> _entries = [];

    private sealed record Entry(string? AtcId, string? AtcAirline, string? AtcFlightNumber, string? AtcModel)
    {
        public DateTimeOffset SeenUtc { get; set; }
    }

    /// <summary>Texte aus einer Abfrage mit Texten (get_traffic) übernehmen.</summary>
    public void Remember(IReadOnlyList<AircraftSample> samples, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var s in samples)
            {
                if (!s.IsUser && HasText(s))
                {
                    _entries[s.ObjectId] = new Entry(s.AtcId, s.AtcAirline, s.AtcFlightNumber, s.AtcModel) { SeenUtc = now };
                }
            }

            Prune(now);
        }
    }

    /// <summary>Fehlende Texte aus dem Gedächtnis ergänzen (Hintergrundscan ohne Texte).</summary>
    public IReadOnlyList<AircraftSample> Fill(IReadOnlyList<AircraftSample> samples, DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            var result = new List<AircraftSample>(samples.Count);
            foreach (var s in samples)
            {
                if (!s.IsUser && !HasText(s) && _entries.TryGetValue(s.ObjectId, out var e))
                {
                    e.SeenUtc = now;
                    result.Add(s with { AtcId = e.AtcId, AtcAirline = e.AtcAirline, AtcFlightNumber = e.AtcFlightNumber, AtcModel = e.AtcModel });
                }
                else
                {
                    result.Add(s);
                }
            }

            return result;
        }
    }

    private static bool HasText(AircraftSample s) =>
        s.AtcId is not null || s.AtcAirline is not null || s.AtcFlightNumber is not null || s.AtcModel is not null;

    private void Prune(DateTimeOffset now)
    {
        foreach (var id in _entries.Where(kv => now - kv.Value.SeenUtc > Lifetime).Select(kv => kv.Key).ToList())
        {
            _entries.Remove(id);
        }
    }
}
