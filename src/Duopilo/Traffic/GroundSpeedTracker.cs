using Duopilo.FlightPlan;
using Duopilo.Sim;

namespace Duopilo.Traffic;

/// <summary>Groundspeed eines Ziels aus seiner Positionsänderung.</summary>
/// <param name="Knots">Großkreisentfernung zwischen zwei Positionen geteilt durch die Zeit dazwischen.</param>
/// <param name="BaselineSec">Zeitabstand der beiden Positionen.</param>
public readonly record struct DerivedGroundSpeed(double Knots, double BaselineSec);

/// <summary>
/// Groundspeed der Ziele aus der Positionsänderung zwischen Verkehrsabfragen.
///
/// Hintergrund: GROUND VELOCITY (knots) ist bei MSFS-Live-Traffic unbrauchbar. Im Testflug 0.9 lag sie meist um etwa
/// Faktor 3,3 unter Flightradar (Emirates 222: 169 statt 556 kt, Southwest 3469: 146 statt 464 kt) – das passt zu m/s,
/// die als ft/s umgerechnet werden. Einzelne Ziele wichen aber anders ab, ein fester Korrekturfaktor taugt also nicht.
///
/// Es werden keine zusätzlichen Abfragen ausgelöst: Gerechnet wird nur aus Abfragen, die ohnehin laufen (Hintergrundscan
/// der Verkehrswarnung, get_traffic). Threadsicher – beide nutzen denselben Tracker.
/// </summary>
public sealed class GroundSpeedTracker
{
    /// <summary>Mehr schafft kein Verkehrsflugzeug, auch nicht im Jetstream – dann wurde das Ziel versetzt oder die ID neu vergeben.</summary>
    public const double MaxPlausibleKt = 900;

    /// <summary>Kürzester Abstand der Positionen: Live-Traffic bewegt sich teils ruckartig, zu kurze Abstände rauschen.</summary>
    public static readonly TimeSpan MinBaseline = TimeSpan.FromSeconds(5);

    /// <summary>Bevorzugter Abstand: lang genug gegen Rauschen, kurz genug für Kurven und Speed-Änderungen.</summary>
    public static readonly TimeSpan PreferredBaseline = TimeSpan.FromSeconds(20);

    /// <summary>Längster Abstand – auch zwei get_traffic-Aufrufe ohne Hintergrundscan sollen noch reichen.</summary>
    public static readonly TimeSpan MaxBaseline = TimeSpan.FromSeconds(120);

    /// <summary>Positionen, die enger beieinander liegen, werden nicht gespeichert (get_traffic und Hintergrundscan gleichzeitig).</summary>
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(1);

    private const int MaxFixesPerTarget = 64;

    private readonly object _gate = new();
    private readonly Dictionary<uint, List<Fix>> _history = [];

    private readonly record struct Fix(DateTimeOffset TimeUtc, double Latitude, double Longitude);

    /// <summary>
    /// Merkt sich die Position jedes fremden Flugzeugs und liefert je Objekt-ID die Groundspeed gegenüber einer früheren
    /// Position (5–120 s zurück, möglichst nah an 20 s). Ziele ohne passende frühere Position fehlen im Ergebnis.
    /// </summary>
    public IReadOnlyDictionary<uint, DerivedGroundSpeed> Update(IReadOnlyList<AircraftSample> samples, DateTimeOffset now)
    {
        var derived = new Dictionary<uint, DerivedGroundSpeed>();
        lock (_gate)
        {
            foreach (var s in samples)
            {
                if (s.IsUser || !IsValidPosition(s))
                {
                    continue;
                }

                if (!_history.TryGetValue(s.ObjectId, out var fixes))
                {
                    fixes = [];
                    _history[s.ObjectId] = fixes;
                }

                fixes.RemoveAll(f => f.TimeUtc > now || now - f.TimeUtc > MaxBaseline);

                if (Reference(fixes, now) is { } reference)
                {
                    var baseline = now - reference.TimeUtc;
                    var knots = RouteMath.DistanceNm(reference.Latitude, reference.Longitude, s.Latitude, s.Longitude) / baseline.TotalHours;
                    if (knots <= MaxPlausibleKt)
                    {
                        derived[s.ObjectId] = new DerivedGroundSpeed(knots, baseline.TotalSeconds);
                    }
                    else
                    {
                        // Positionssprung: alte Positionen gehören nicht zu dieser Bahn
                        fixes.Clear();
                    }
                }

                if (fixes.Count == 0 || now - fixes[^1].TimeUtc >= MinSpacing)
                {
                    fixes.Add(new Fix(now, s.Latitude, s.Longitude));
                    if (fixes.Count > MaxFixesPerTarget)
                    {
                        fixes.RemoveAt(0);
                    }
                }
            }

            foreach (var id in _history.Where(kv => kv.Value.Count == 0 || now - kv.Value[^1].TimeUtc > MaxBaseline).Select(kv => kv.Key).ToList())
            {
                _history.Remove(id);
            }
        }

        return derived;
    }

    /// <summary>Die gespeicherte Position mit mindestens <see cref="MinBaseline"/> Abstand, die <see cref="PreferredBaseline"/> am nächsten liegt.</summary>
    private static Fix? Reference(List<Fix> fixes, DateTimeOffset now)
    {
        Fix? best = null;
        var bestDelta = TimeSpan.MaxValue;
        foreach (var f in fixes)
        {
            var age = now - f.TimeUtc;
            if (age < MinBaseline)
            {
                continue;
            }

            var delta = (age - PreferredBaseline).Duration();
            if (delta < bestDelta)
            {
                best = f;
                bestDelta = delta;
            }
        }

        return best;
    }

    private static bool IsValidPosition(AircraftSample s) =>
        double.IsFinite(s.Latitude) && double.IsFinite(s.Longitude) && Math.Abs(s.Latitude) <= 90 && Math.Abs(s.Longitude) <= 180;
}
