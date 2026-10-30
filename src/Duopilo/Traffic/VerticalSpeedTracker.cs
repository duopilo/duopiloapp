using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>
/// V/S der Ziele aus der Höhenänderung zwischen zwei Verkehrsabfragen – für Ziele, deren SimVar VERTICAL SPEED 0 liefert
/// (vor allem Multiplayer). Threadsicher: get_traffic und die Hintergrundabfrage nutzen denselben Tracker.
/// </summary>
public sealed class VerticalSpeedTracker
{
    /// <summary>Mehr ist kein Steigen, sondern eine neu vergebene Objekt-ID oder ein Positionssprung.</summary>
    public const double MaxPlausibleFpm = 8000;

    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Dictionary<uint, (double AltitudeFt, DateTimeOffset TimeUtc)> _last = [];

    /// <summary>
    /// Merkt sich die Höhe jedes fremden Flugzeugs und liefert je Objekt-ID die V/S in ft/min seit der letzten Abfrage
    /// (1–60 s zurück). Liegen zwei Abfragen weniger als 1 s auseinander, bleibt der ältere Bezugswert stehen.
    /// </summary>
    public IReadOnlyDictionary<uint, double> Update(IReadOnlyList<AircraftSample> samples, DateTimeOffset now)
    {
        var derived = new Dictionary<uint, double>();
        lock (_gate)
        {
            foreach (var s in samples)
            {
                if (s.IsUser || !double.IsFinite(s.AltitudeFt))
                {
                    continue;
                }

                if (_last.TryGetValue(s.ObjectId, out var prev))
                {
                    var dt = now - prev.TimeUtc;
                    if (dt < MinInterval)
                    {
                        continue;
                    }

                    if (dt <= MaxInterval)
                    {
                        var rate = (s.AltitudeFt - prev.AltitudeFt) / dt.TotalMinutes;
                        if (Math.Abs(rate) <= MaxPlausibleFpm)
                        {
                            derived[s.ObjectId] = rate;
                        }
                    }
                }

                _last[s.ObjectId] = (s.AltitudeFt, now);
            }

            foreach (var id in _last.Where(kv => now - kv.Value.TimeUtc > Lifetime).Select(kv => kv.Key).ToList())
            {
                _last.Remove(id);
            }
        }

        return derived;
    }
}
