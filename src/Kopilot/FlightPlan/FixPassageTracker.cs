namespace Kopilot.FlightPlan;

/// <summary>Letzter überflogener Fix: Index in der Punktliste der Route und wie lange der Überflug her ist (Sim-Zeit).</summary>
public sealed record FixPassage(int PointIndex, string Ident, double ElapsedSec);

/// <summary>
/// Schreibt mit, wann das Flugzeug die Fixes der SimBrief-Route überfliegt (Sim-Uhr, ZULU TIME). Grundlage der ETA nach Plan:
/// Navlog-Zeiten ab dem letzten überflogenen Fix, verschoben um die Abweichung dort.
/// Threadsicher – gefüttert von Monitoring, Dashboard und get_progress.
/// </summary>
public sealed class FixPassageTracker
{
    /// <summary>Höchstens so lange zurück wird ein Überflug aus Strecke und Groundspeed zurückgerechnet.</summary>
    public const double MaxBacktrackSec = 600;

    /// <summary>Ohne brauchbare Groundspeed dürfen zwei Beobachtungen höchstens so weit auseinanderliegen (Überflug = Mitte).</summary>
    public const double MaxGapSec = 60;

    private readonly object _lock = new();
    private string? _planId;
    private Ofp? _planRef;
    private int? _lastLeg;
    private double? _lastZulu;
    private double? _lastTime;
    private double _dayOffset;
    private (int Index, string Ident, double Time)? _passage;

    /// <param name="zuluSec">Sim-Uhrzeit in Sekunden seit Mitternacht (ZULU TIME).</param>
    public FixPassage? Observe(Ofp ofp, IReadOnlyList<RoutePoint> points, RouteProgress progress, double? zuluSec, double? groundSpeedKt)
    {
        lock (_lock)
        {
            // Neuer Plan: alles vergessen (gleiche RequestId = nur neu geladen)
            var samePlan = ofp.RequestId is { } id ? id == _planId : ReferenceEquals(ofp, _planRef);
            if (!samePlan)
            {
                ResetTrack();
                _planId = ofp.RequestId;
                _planRef = ofp;
            }

            if (zuluSec is not { } z || !double.IsFinite(z))
            {
                return null;
            }

            // Sim-Uhr fortlaufend machen: Sprung über Mitternacht weiterzählen, sonst rückwärts = Uhr verstellt
            if (_lastZulu is { } lz)
            {
                if (z < lz - 43200)
                {
                    _dayOffset += 86400;
                }
                else if (z < lz - 5)
                {
                    ResetTrack();
                }
            }

            var now = z + _dayOffset;
            var leg = progress.LegIndex;
            if (_lastLeg is { } prevLeg && _lastTime is { } prevTime && leg != prevLeg)
            {
                // Genau ein Abschnitt weiter = Fix überflogen. Sprünge (DIR TO, Slew, neu zugeordnet) taugen nicht als Bezug.
                _passage = leg == prevLeg + 1 && leg < points.Count ? Passage(points, progress, now, now - prevTime, groundSpeedKt) : null;
            }

            _lastLeg = leg;
            _lastTime = now;
            _lastZulu = z;

            return _passage is { } p && p.Index == leg ? new FixPassage(p.Index, p.Ident, Math.Max(0, now - p.Time)) : null;
        }
    }

    /// <summary>Überflugzeit: aus der Strecke seit dem Fix und der Groundspeed, sonst Mitte zwischen zwei nahen Beobachtungen.</summary>
    private static (int, string, double)? Passage(IReadOnlyList<RoutePoint> points, RouteProgress progress, double now, double gapSec, double? groundSpeedKt)
    {
        double? time = null;
        if (groundSpeedKt is > 50)
        {
            // Muss zwischen die beiden Beobachtungen passen (10 s Spielraum für Groundspeed-Schwankungen)
            var since = progress.AlongLegNm / groundSpeedKt.Value * 3600;
            if (since <= Math.Max(0, gapSec) + 10 && since <= MaxBacktrackSec)
            {
                time = now - since;
            }
        }

        if (time is null && gapSec <= MaxGapSec)
        {
            time = now - gapSec / 2;
        }

        return time is { } t ? (progress.LegIndex, points[progress.LegIndex].Ident, t) : null;
    }

    private void ResetTrack()
    {
        _lastLeg = null;
        _lastZulu = null;
        _lastTime = null;
        _dayOffset = 0;
        _passage = null;
    }
}
