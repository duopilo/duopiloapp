namespace Duopilo.Traffic;

/// <summary>
/// Abstand beim Rollen: Steht oder rollt ein anderes Flugzeug vor einem auf dessen Weg (höchstens <see cref="LateralM"/> daneben, bis
/// <see cref="LookAheadM"/> voraus), bleibt es mit <c>spacingM</c> Abstand dahinter – in der Schlange am Haltepunkt und vor Kreuzungen.
/// </summary>
public static class TaxiTraffic
{
    /// <summary>So weit voraus zählt ein anderes Flugzeug.</summary>
    public const double LookAheadM = 250;

    /// <summary>So weit neben dem eigenen Weg zählt ein anderes Flugzeug noch als „auf dem Weg“.</summary>
    public const double LateralM = 12;

    /// <summary>Abstand (Bezugspunkt zu Bezugspunkt) hinter einem Verkehrsflugzeug.</summary>
    public const double AirlinerSpacingM = 48;

    /// <summary>Abstand hinter einem Flugzeug der Allgemeinen Luftfahrt.</summary>
    public const double GeneralAviationSpacingM = 22;

    public static double SpacingFor(TrafficCategory category) =>
        category == TrafficCategory.GeneralAviation ? GeneralAviationSpacingM : AirlinerSpacingM;

    /// <summary>
    /// Bis wohin auf <paramref name="path"/> (Meter ab dem Anfang) das Flugzeug bei <paramref name="s"/> rollen darf, und welches der
    /// <paramref name="others"/> (Ost/Nord) der Grund ist; null = frei.
    /// </summary>
    public static (double StopS, int Index)? StopBehind(TaxiPath path, double s, IReadOnlyList<(double East, double North)> others, double spacingM)
    {
        (double StopS, int Index)? best = null;
        var (hereE, hereN, _) = path.At(s);
        for (var i = 0; i < others.Count; i++)
        {
            var (e, n) = others[i];
            if (Math.Sqrt(((e - hereE) * (e - hereE)) + ((n - hereN) * (n - hereN))) > LookAheadM + LateralM)
            {
                continue;
            }

            var sb = path.NearestS(e, n, s, s + LookAheadM);
            if (sb <= s + 0.5 || sb - s > LookAheadM)
            {
                continue;
            }

            var (pe, pn, _) = path.At(sb);
            if (Math.Sqrt(((pe - e) * (pe - e)) + ((pn - n) * (pn - n))) > LateralM)
            {
                continue;
            }

            var stop = Math.Max(s, sb - spacingM);
            if (best is null || stop < best.Value.StopS)
            {
                best = (stop, i);
            }
        }

        return best;
    }
}
