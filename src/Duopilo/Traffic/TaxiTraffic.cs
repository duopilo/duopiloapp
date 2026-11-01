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

    /// <summary>Gegenverkehr: so weit voraus wird der eigene Weg geprüft.</summary>
    public const double HeadOnLookAheadM = 800;

    /// <summary>Gegenverkehr: so weit voraus zählt der Weg des anderen.</summary>
    public const double HeadOnOtherAheadM = 1200;

    /// <summary>Gegenverkehr: Abstand der Prüfpunkte auf den Wegen.</summary>
    public const double HeadOnSampleM = 5;

    /// <summary>Gegenverkehr: so nah beieinander gelten zwei Wege als dasselbe Stück Rollweg.</summary>
    public const double SharedM = 10;

    /// <summary>Gegenverkehr: so weit weg vom Weg des anderen wartet das Flugzeug (zwei A320-Halbspannweiten und Rand).</summary>
    public const double HeadOnClearM = 40;

    /// <summary>Gegenverkehr: so weit weichen die Richtungen mindestens voneinander ab (Grad).</summary>
    public const double OppositeDeg = 120;

    /// <summary>
    /// Punkte auf dem Rest eines Wegs ab <paramref name="s"/> bis <paramref name="aheadM"/> voraus (alle <see cref="HeadOnSampleM"/>),
    /// mit Wegmeter und Richtung – für <see cref="FindHeadOn"/>; <paramref name="toLocal"/> rechnet in das Netz des anderen um.
    /// </summary>
    public static IReadOnlyList<TaxiPathPoint> Sample(TaxiPath path, double s, double aheadM, Func<double, double, (double East, double North)>? toLocal = null)
    {
        var points = new List<TaxiPathPoint>();
        var end = Math.Min(path.LengthM, s + aheadM);
        for (var x = Math.Max(0, s); x <= end + 1e-6; x += HeadOnSampleM)
        {
            var (e, n, h) = path.At(Math.Min(x, path.LengthM));
            var (le, ln) = toLocal?.Invoke(e, n) ?? (e, n);
            points.Add(new TaxiPathPoint(x, le, ln, h));
        }

        return points;
    }

    /// <summary>
    /// Gegenverkehr: Teilen sich der eigene Weg ab <paramref name="s"/> und der Rest des Wegs eines anderen (<paramref name="other"/>,
    /// <see cref="Sample"/>) ein Stück Rollweg in Gegenrichtung (höchstens <see cref="SharedM"/> auseinander, Richtungen mehr als
    /// <see cref="OppositeDeg"/> verschieden)? Dann: wo das Stück auf dem eigenen Weg beginnt und endet, wo das Flugzeug davor wartet
    /// (der letzte Punkt davor, der <see cref="HeadOnClearM"/> vom Weg des anderen weg ist; sonst gleich hier) und wo der andere in
    /// das Stück hineinfährt (dessen Wegmeter). Null = kein Gegenverkehr.
    /// </summary>
    public static TaxiHeadOn? FindHeadOn(TaxiPath path, double s, IReadOnlyList<TaxiPathPoint> other)
    {
        if (other.Count == 0)
        {
            return null;
        }

        double? entry = null;
        double exit = 0;
        double otherEntry = 0;
        double otherExit = 0;
        var clear = new List<(double S, bool Clear)>();
        var end = Math.Min(path.LengthM, s + HeadOnLookAheadM);
        for (var x = s; x <= end + 1e-6; x += HeadOnSampleM)
        {
            var (e, n, h) = path.At(Math.Min(x, path.LengthM));
            var nearest = other[0];
            var best = double.MaxValue;
            foreach (var p in other)
            {
                var d = ((p.East - e) * (p.East - e)) + ((p.North - n) * (p.North - n));
                if (d < best)
                {
                    best = d;
                    nearest = p;
                }
            }

            var distance = Math.Sqrt(best);
            if (entry is null)
            {
                clear.Add((x, distance >= HeadOnClearM));
            }

            var opposite = Math.Abs(TrafficMath.Normalize(h - nearest.HeadingDeg + 180) - 180) > OppositeDeg;
            if (distance <= SharedM && opposite)
            {
                if (entry is null)
                {
                    entry = x;
                    otherExit = nearest.S;
                }

                exit = x;
                otherEntry = nearest.S;
            }
        }

        if (entry is not { } start)
        {
            return null;
        }

        var hold = s;
        for (var i = clear.Count - 1; i >= 0; i--)
        {
            if (clear[i].S < start && clear[i].Clear)
            {
                hold = clear[i].S;
                break;
            }
        }

        return new TaxiHeadOn(start, exit, hold, otherEntry, otherExit);
    }
}

/// <summary>Punkt auf einem Rollweg: Wegmeter, Ost/Nord im Netz, Richtung.</summary>
public readonly record struct TaxiPathPoint(double S, double East, double North, double HeadingDeg);

/// <summary>Gegenverkehr auf einem Stück Rollweg (<see cref="TaxiTraffic.FindHeadOn"/>).</summary>
/// <param name="EntryS">Hier beginnt das gemeinsame Stück auf dem eigenen Weg.</param>
/// <param name="ExitS">Hier endet es (oder am Ende der Prüfung).</param>
/// <param name="HoldS">Hier wartet das Flugzeug, wenn es dem anderen Vorfahrt lässt.</param>
/// <param name="OtherEntryS">Hier fährt der andere in das Stück hinein (seine Wegmeter).</param>
/// <param name="OtherExitS">Hier verlässt der andere das Stück (seine Wegmeter).</param>
public sealed record TaxiHeadOn(double EntryS, double ExitS, double HoldS, double OtherEntryS, double OtherExitS);
