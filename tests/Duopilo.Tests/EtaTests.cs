using Kopilot.FlightPlan;

namespace Kopilot.Tests;

/// <summary>ETA nach SimBrief-Navlog (geplante Winde) statt nur mit der aktuellen Groundspeed.</summary>
public sealed class EtaTests
{
    /// <summary>Route auf dem Äquator nach Osten: 1° Länge ≈ 60 nm, Start AAAA bei 0/0.</summary>
    private static Ofp Plan(params (string Ident, double Lon, int? TimeSec)[] fixes) => new()
    {
        Origin = new OfpAirport("AAAA", null, "09", 0, 0, null, null, null, null),
        Destination = new OfpAirport("BBBB", null, "27", 0, fixes[^1].Lon, null, null, null, null),
        Fixes = fixes.Select(f => new OfpFix { Ident = f.Ident, Lat = 0, Lon = f.Lon, TimeTotalSec = f.TimeSec }).ToList(),
    };

    private static (IReadOnlyList<RoutePoint> Points, RouteProgress Route, FlightProgress Progress) At(Ofp ofp, double lon)
    {
        var points = ofp.RoutePoints();
        var route = RouteTracker.Locate(points, 0, lon);
        Assert.NotNull(route);
        return (points, route, ProgressCalculator.Compute(ofp, points, route, null));
    }

    [Fact]
    public void Gegenwind_im_Plan_verlaengert_die_Restzeit_trotz_hoher_Groundspeed()
    {
        // Jetzt Rückenwind (GS 480), ab TWO laut Plan Gegenwind: 60 nm je 720 s
        var ofp = Plan(("ONE", 1, 450), ("TWO", 2, 900), ("TOD", 3, 1620), ("BBBB", 4, 2340));
        var (points, _, progress) = At(ofp, 1.5);

        var eta = EtaCalculator.Predict(ofp, points, progress, groundSpeedKt: 480);

        Assert.Equal(EtaCalculator.SimBrief, eta.Source);
        Assert.InRange(eta.NextSec ?? 0, 224, 226);
        Assert.InRange(eta.TopOfDescentSec ?? 0, 944, 946);
        Assert.InRange(eta.DestinationSec ?? 0, 1664, 1666);

        // Nur mit der Groundspeed wären es knapp 19 statt 28 Minuten
        Assert.InRange(progress.DistanceToGoNm / 480 * 3600, 1120, 1130);
    }

    [Fact]
    public void Rueckenwind_im_Plan_verkuerzt_die_Restzeit_trotz_niedriger_Groundspeed()
    {
        // Jetzt im Steigflug (GS 300), danach laut Plan Jetstream: 120 nm in 720 s
        var ofp = Plan(("ONE", 1, 720), ("TWO", 2, 1440), ("BBBB", 4, 2160));
        var (points, _, progress) = At(ofp, 1.5);

        var eta = EtaCalculator.Predict(ofp, points, progress, groundSpeedKt: 300);

        Assert.Equal(EtaCalculator.SimBrief, eta.Source);
        Assert.InRange(eta.NextSec ?? 0, 359, 361);
        Assert.InRange(eta.DestinationSec ?? 0, 1079, 1081);
        Assert.InRange(progress.DistanceToGoNm / 300 * 3600, 1795, 1805);
    }

    [Fact]
    public void Abweichung_am_letzten_Fix_verschiebt_die_Planzeiten()
    {
        // ONE vor 120 s überflogen, Abschnitt ONE–TWO geplant 600 s: nach Plan noch 480 s bis TWO
        var ofp = Plan(("ONE", 1, 600), ("TWO", 2, 1200), ("BBBB", 3, 1800));
        var (points, _, progress) = At(ofp, 1.5);

        var eta = EtaCalculator.Predict(ofp, points, progress, 420, new FixPassage(1, "ONE", 120));

        Assert.InRange(eta.NextSec ?? 0, 479, 481);
        Assert.InRange(eta.DestinationSec ?? 0, 1079, 1081);
    }

    [Fact]
    public void Langsamer_als_geplant_zaehlt_die_Planzeit_ab_der_Position()
    {
        // ONE vor 400 s überflogen, erst die Hälfte geschafft: TWO nicht in 200 s, sondern frühestens in 300 s
        var ofp = Plan(("ONE", 1, 600), ("TWO", 2, 1200), ("BBBB", 3, 1800));
        var (points, _, progress) = At(ofp, 1.5);

        var eta = EtaCalculator.Predict(ofp, points, progress, 420, new FixPassage(1, "ONE", 400));

        Assert.InRange(eta.NextSec ?? 0, 299, 301);
        Assert.InRange(eta.DestinationSec ?? 0, 899, 901);
    }

    [Fact]
    public void Ueberflug_eines_anderen_Fixes_ist_kein_Bezug()
    {
        // Bezug muss der Fix direkt vor dem nächsten sein (sonst z. B. nach DIR TO falsch)
        var ofp = Plan(("ONE", 1, 600), ("TWO", 2, 1200), ("BBBB", 3, 1800));
        var (points, _, progress) = At(ofp, 1.5);

        var eta = EtaCalculator.Predict(ofp, points, progress, 420, new FixPassage(0, "AAAA", 50));

        Assert.InRange(eta.NextSec ?? 0, 299, 301);
    }

    [Fact]
    public void Ohne_Navlog_Zeiten_zaehlt_die_Groundspeed()
    {
        var ofp = Plan(("ONE", 1, null), ("TWO", 2, null), ("BBBB", 3, null));
        var (points, _, progress) = At(ofp, 1.5);

        var eta = EtaCalculator.Predict(ofp, points, progress, 360);

        Assert.Equal(EtaCalculator.GroundSpeed, eta.Source);
        Assert.InRange(eta.NextSec ?? 0, 299, 302);
        Assert.InRange(eta.DestinationSec ?? 0, 899, 902);
        Assert.Null(EtaCalculator.Predict(ofp, points, progress, 20).DestinationSec);
    }

    [Fact]
    public void Ueberflug_wird_mit_Sim_Uhrzeit_mitgeschrieben()
    {
        var ofp = Plan(("ONE", 1, 600), ("TWO", 2, 1200), ("BBBB", 3, 1800));
        var points = ofp.RoutePoints();
        var tracker = new FixPassageTracker();
        FixPassage? Observe(double lon, double zulu) => tracker.Observe(ofp, points, RouteTracker.Locate(points, 0, lon)!, zulu, 360);

        Assert.Null(Observe(0.95, 36000));

        // 12 s später 1,2 nm hinter ONE: bei 360 kt vor 12 s überflogen
        var passed = Observe(1.02, 36012);
        Assert.NotNull(passed);
        Assert.Equal("ONE", passed.Ident);
        Assert.Equal(1, passed.PointIndex);
        Assert.InRange(passed.ElapsedSec, 11.5, 12.5);

        // 240 s nach ONE auf halber Strecke – schneller als geplant (300 s): ETA nach Plan ab ONE
        var later = Observe(1.5, 36240);
        Assert.NotNull(later);
        Assert.InRange(later.ElapsedSec, 239.5, 240.5);

        var route = RouteTracker.Locate(points, 0, 1.5)!;
        var eta = EtaCalculator.Predict(ofp, points, ProgressCalculator.Compute(ofp, points, route, null), 450, later);
        Assert.InRange(eta.NextSec ?? 0, 359, 361);
        Assert.InRange(eta.DestinationSec ?? 0, 959, 961);
    }

    [Fact]
    public void Ueberflug_ueber_Mitternacht()
    {
        var ofp = Plan(("ONE", 1, 600), ("TWO", 2, 1200), ("BBBB", 3, 1800));
        var points = ofp.RoutePoints();
        var tracker = new FixPassageTracker();
        FixPassage? Observe(double lon, double zulu) => tracker.Observe(ofp, points, RouteTracker.Locate(points, 0, lon)!, zulu, 360);

        Observe(0.98, 86395);
        Assert.NotNull(Observe(1.01, 5));

        var later = Observe(1.07, 65);
        Assert.NotNull(later);
        Assert.InRange(later.ElapsedSec, 65.5, 66.5);
    }

    [Fact]
    public void Spruenge_und_verstellte_Uhr_loeschen_den_Bezug()
    {
        var ofp = Plan(("ONE", 1, 600), ("TWO", 2, 1200), ("BBBB", 3, 1800));
        var points = ofp.RoutePoints();
        var tracker = new FixPassageTracker();
        FixPassage? Observe(double lon, double zulu) => tracker.Observe(ofp, points, RouteTracker.Locate(points, 0, lon)!, zulu, 360);

        // Zwei Abschnitte auf einmal (Slew, DIR TO): kein Überflug
        Observe(0.5, 1000);
        Assert.Null(Observe(2.5, 1002));

        // Uhr zurückgestellt: Bezug weg
        Observe(1.98, 2000);
        Assert.NotNull(Observe(2.02, 2004));
        Assert.Null(Observe(2.05, 1000));

        // Neuer Plan: Bezug weg
        Observe(1.98, 3000);
        Assert.NotNull(Observe(2.02, 3004));
        var other = Plan(("ONE", 1, 600), ("TWO", 2, 1200), ("BBBB", 3, 1800)) with { RequestId = "neu" };
        Assert.Null(tracker.Observe(other, points, RouteTracker.Locate(points, 0, 2.05)!, 3010, 360));
    }
}
