using System.Globalization;
using Kopilot.FlightPlan;
using Kopilot.Localization;

namespace Kopilot.Monitoring;

/// <summary>
/// Fuel Check im Reiseflug: an jedem OFP-Wegpunkt (und spätestens alle <see cref="FuelCheckSettings.IntervalMinutes"/>)
/// Ist-Treibstoff gegen den SimBrief-Plan vergleichen und ansagen ("HENDY, fuel 34.1, 2.2 below plan").
/// Gewarnt wird, wenn die Abweichung seit dem letzten Check um mehr als die Schwelle schlechter wird (Leckverdacht)
/// oder die prognostizierte Landemenge unter Minimum + Puffer fällt – beides wird laufend geprüft, nicht nur an Wegpunkten.
/// Steigt der Treibstoff (Nachtanken über Toolbar/EFB) oder fällt er schneller, als ein Flugzeug verbrennen kann,
/// ist das eine manuelle Änderung: keine Warnung, der Vergleich beginnt neu.
/// </summary>
public sealed partial class MonitorEngine
{
    /// <summary>Schneller verbrennt kein Airliner (A380 mit TOGA ≈ 13 kg/s) – größere Sprünge nach unten sind manuell.</summary>
    private const double MaxBurnKgPerSec = 15;

    /// <summary>Spielraum für den Sprung nach unten (Messung im Sekundentakt).</summary>
    private const double FuelJumpToleranceKg = 150;

    /// <summary>Ab so viel Zuwachs gilt es als Nachtanken.</summary>
    private const double RefuelMinKg = 100;

    /// <summary>Schon ab so viel Zuwachs ruht die Prüfung, bis das Tanken vorbei ist.</summary>
    private const double RefuelHoldKg = 30;

    /// <summary>So lange ohne weiteren Zuwachs, dann ist das Tanken vorbei.</summary>
    private static readonly TimeSpan RefuelSettle = TimeSpan.FromSeconds(4);

    /// <summary>Die Plan-Stichprobe gilt nur, wenn ihr Ist-Wert zum aktuellen passt – sonst stammt sie von vor einem Tankvorgang.</summary>
    private const double FuelSampleToleranceKg = 100;

    /// <summary>Weiter neben der Route taugt der Planvergleich nicht (anderer Plan geladen, große Umleitung).</summary>
    private const double FuelCheckMaxCrossTrackNm = 25;

    /// <summary>Springt der Abschnitt weiter (Slew, Neuzuordnung), ist es kein Überflug.</summary>
    private const int FuelCheckMaxLegSkip = 3;

    /// <summary>So weit muss die Landemenge wieder steigen, bevor dieselbe Warnung erneut kommen kann.</summary>
    private const double LandingHysteresisKg = 100;

    private string? _fuelPlanId;
    private int? _fuelLeg;
    private double? _fuelPrevKg;
    private DateTimeOffset _fuelPrevUtc;
    private bool _fuelGap;
    private double? _fuelRiseFromKg;
    private double _fuelRiseTopKg;
    private DateTimeOffset _fuelRiseLastUtc;
    private FuelBaseline? _fuelBaseline;
    private FuelSince? _fuelRestartLabel;
    private double _fuelSinceCheckSec;
    private double? _fuelClockZulu;
    private DateTimeOffset? _fuelClockUtc;
    private int _fuelLandingStage;

    /// <summary>Verlauf der Fuel Checks (Dashboard, get_progress). Threadsicher.</summary>
    public FuelCheckLog FuelChecks { get; } = new();

    public FuelCheckSettings FuelCheckSettings => _settings.Fuel;

    private void CheckFuel(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        var dt = AdvanceFuelClock(s, now);
        var sample = s.FuelPlan;
        if (s.FuelTotalKg is not { } kg || !double.IsFinite(kg) || kg < 0)
        {
            _fuelPrevKg = null;
            return;
        }

        if (sample is not null && sample.PlanId != _fuelPlanId)
        {
            StartFuelPlan(sample);
        }

        var refuelling = TrackFuelQuantity(s, kg, now, sample, suppressed, events);
        if (sample is null || suppressed)
        {
            // Nach dem Laden oder einer Pause erst die Ruhezeit abwarten – ein Überflug darin wird danach nachgeholt
            return;
        }

        var passed = TrackFuelLeg(sample);
        if (refuelling || !FuelCruise(s, sample) || Math.Abs(kg - sample.ActualKg) > FuelSampleToleranceKg)
        {
            return;
        }

        _fuelSinceCheckSec += dt;
        var baseline = _fuelBaseline;
        if (baseline is null)
        {
            // Erster Wert im Reiseflug (oder nach dem Tanken): Bezug für "seit dem letzten Check", ohne Ansage
            baseline = new FuelBaseline(sample.DiffKg, _fuelRestartLabel ?? FuelSince.Start);
            _fuelBaseline = baseline;
            _fuelRestartLabel = null;
            _fuelSinceCheckSec = 0;
            passed = false;
        }

        var settings = _settings.Fuel;
        var waypoint = passed && FuelCheckFixEligible(sample.LastFix)
            && (settings.MinSpacingMinutes <= 0 || _fuelSinceCheckSec >= settings.MinSpacingMinutes * 60);
        var interval = !waypoint && settings.IntervalMinutes > 0 && _fuelSinceCheckSec >= settings.IntervalMinutes * 60;

        var change = sample.DiffKg - baseline.DiffKg;
        var deviationWarn = settings.DeviationIncreaseKg > 0 && -change > settings.DeviationIncreaseKg;

        var (stage, threshold) = LandingStage(sample, 0);
        var landingWarn = stage > _fuelLandingStage;
        if (landingWarn)
        {
            _fuelLandingStage = stage;
        }
        else if (LandingStage(sample, LandingHysteresisKg).Stage < _fuelLandingStage)
        {
            // Wieder deutlich darüber (z. B. Rückenwind): dieselbe Warnung darf später erneut kommen
            _fuelLandingStage = LandingStage(sample, LandingHysteresisKg).Stage;
        }

        if (!waypoint && !interval && !deviationWarn && !landingWarn)
        {
            return;
        }

        var kind = waypoint ? FuelCheckKind.Waypoint
            : interval ? FuelCheckKind.Interval
            : deviationWarn ? FuelCheckKind.Deviation
            : FuelCheckKind.Landing;
        var place = waypoint ? FuelCheckText.Place(sample.LastFix) : null;
        var ident = waypoint ? sample.LastFix?.Ident : null;

        var warnings = new List<string>();
        var extras = new List<string>();
        var notes = new List<string>();
        if (deviationWarn)
        {
            warnings.Add("deviation");
            extras.Add("deviation increased by " + FuelCheckText.Difference(-change, sample.ActualKg));
            notes.Add(Loc.T("monitor.fuel.deviationNote", FuelCheckText.Kg(-change), SinceText(baseline), FuelCheckText.Kg(settings.DeviationIncreaseKg)));
        }

        if (landingWarn && sample.PredictedLandingKg is { } predicted && sample.MinimumLandingKg is { } minimum)
        {
            if (stage >= 2)
            {
                warnings.Add("landing_minimum");
                extras.Add($"landing fuel {FuelCheckText.Amount(predicted)}, below minimum");
                notes.Add(Loc.T("monitor.fuel.landingBelowMinimum", FuelCheckText.Kg(predicted), FuelCheckText.Kg(minimum)));
            }
            else
            {
                warnings.Add("landing_buffer");
                extras.Add($"landing fuel {FuelCheckText.Amount(predicted)}, minimum {FuelCheckText.Amount(minimum)}");
                notes.Add(Loc.T("monitor.fuel.landingBelowBuffer", FuelCheckText.Kg(predicted), FuelCheckText.Kg(minimum), FuelCheckText.Kg((threshold ?? minimum) - minimum)));
            }
        }

        var where = kind switch
        {
            FuelCheckKind.Waypoint => Loc.T("monitor.fuel.checkAt", ident),
            FuelCheckKind.Interval => Loc.T("monitor.fuel.checkInterval"),
            _ => Loc.T("monitor.fuel.check"),
        };
        var message = Loc.T("monitor.fuel.summary",
                where,
                FuelCheckText.Kg(sample.ActualKg),
                FuelCheckText.Kg(sample.PlannedKg),
                FuelCheckText.SignedKg(sample.DiffKg),
                SinceText(baseline),
                FuelCheckText.SignedKg(change))
            + (sample.PredictedLandingKg is { } landing
                ? " " + (sample.MinimumLandingKg is { } min
                    ? Loc.T("monitor.fuel.landingWithMinimum", FuelCheckText.Kg(landing), FuelCheckText.Kg(min))
                    : Loc.T("monitor.fuel.landing", FuelCheckText.Kg(landing)))
                : "")
            + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");

        var warn = warnings.Count > 0 && IsEnabled(Rules.FuelWarning);
        var announce = warn || ((kind is FuelCheckKind.Waypoint or FuelCheckKind.Interval) && IsEnabled(Rules.FuelCheck));
        var callout = FuelCheckText.Callout(place, sample.ActualKg, sample.DiffKg, warn ? extras : null);

        FuelChecks.Add(new FuelCheckEntry
        {
            TimeUtc = now,
            ZuluSec = s.ZuluTimeSec,
            Kind = kind,
            Ident = ident,
            ActualKg = sample.ActualKg,
            PlannedKg = sample.PlannedKg,
            DiffKg = sample.DiffKg,
            ChangeKg = change,
            PredictedLandingKg = sample.PredictedLandingKg,
            MinimumLandingKg = sample.MinimumLandingKg,
            LandingThresholdKg = threshold,
            DistanceFlownNm = sample.DistanceFlownNm,
            Warnings = warnings,
            Callout = announce ? callout : null,
            Message = message,
        });

        if (announce)
        {
            events.Add(warn
                ? Event(now, Rules.FuelWarning, AlertSeverity.Caution, AlertKind.Event, callout, message)
                : Event(now, Rules.FuelCheck, AlertSeverity.Info, AlertKind.Event, callout, message));
        }

        // Eine Landewarnung allein ist kein neuer Bezugspunkt – die Abweichung zählt weiter ab dem letzten Check
        if (kind != FuelCheckKind.Landing)
        {
            _fuelBaseline = ident is not null ? new FuelBaseline(sample.DiffKg, FuelSince.Fix, ident)
                : kind == FuelCheckKind.Interval ? new FuelBaseline(sample.DiffKg, FuelSince.Check, Clock(s.ZuluTimeSec, now))
                : new FuelBaseline(sample.DiffKg, FuelSince.Warning);
            _fuelSinceCheckSec = 0;
        }
    }

    /// <summary>
    /// Verfolgt die Menge an Bord: Zuwachs = Nachtanken, Sprung nach unten = manuelle Änderung. Beides beginnt den Vergleich neu.
    /// Rückgabe: Es wird gerade getankt (dann ruht die Prüfung).
    /// </summary>
    private bool TrackFuelQuantity(FlightSnapshot s, double kg, DateTimeOffset now, FuelPlanSample? sample, bool suppressed, List<AlertEvent> events)
    {
        if (_fuelPrevKg is not { } prev)
        {
            _fuelPrevKg = kg;
            _fuelPrevUtc = now;
            _fuelGap = false;
            return false;
        }

        // Nach einer Pause oder eingefrorenen Werten zählt die Lücke nicht als Flugzeit – der Sim hat nichts verbrannt.
        // Sonst darf in einer längeren Lücke (Monitoring stockt) entsprechend mehr verbrannt worden sein.
        var dt = _fuelGap ? 0 : Math.Clamp((now - _fuelPrevUtc).TotalSeconds, 0, 600);
        var delta = kg - prev;
        _fuelPrevKg = kg;
        _fuelPrevUtc = now;
        _fuelGap = false;

        if (delta > 0.5)
        {
            if (_fuelRiseFromKg is null)
            {
                _fuelRiseFromKg = prev;
            }

            _fuelRiseTopKg = kg;
            _fuelRiseLastUtc = now;
        }

        if (_fuelRiseFromKg is { } from && (now - _fuelRiseLastUtc >= RefuelSettle || delta < -20))
        {
            _fuelRiseFromKg = null;
            if (_fuelRiseTopKg - from >= RefuelMinKg)
            {
                FuelQuantityChanged(FuelCheckKind.Refuel, from, _fuelRiseTopKg, s, now, sample, suppressed, events);
            }
        }

        if (_fuelRiseFromKg is null && delta < -(MaxBurnKgPerSec * Math.Max(dt, 1.5) + FuelJumpToleranceKg))
        {
            FuelQuantityChanged(FuelCheckKind.Defuel, prev, kg, s, now, sample, suppressed, events);
        }

        return _fuelRiseFromKg is { } rising && kg - rising > RefuelHoldKg;
    }

    private void FuelQuantityChanged(
        FuelCheckKind kind, double fromKg, double toKg, FlightSnapshot s, DateTimeOffset now, FuelPlanSample? sample, bool suppressed, List<AlertEvent> events)
    {
        _fuelBaseline = null;
        _fuelLandingStage = 0;
        _fuelSinceCheckSec = 0;
        if (suppressed)
        {
            // Werte beim Laden eines Flugs springen immer – nur neu beginnen, nichts melden
            return;
        }

        _fuelRestartLabel = kind == FuelCheckKind.Refuel ? FuelSince.Refuel : FuelSince.FuelChange;

        var message = kind == FuelCheckKind.Refuel
            ? Loc.T("monitor.fuel.refuel", FuelCheckText.Kg(fromKg), FuelCheckText.Kg(toKg), FuelCheckText.SignedKg(toKg - fromKg))
            : Loc.T("monitor.fuel.defuel", FuelCheckText.Kg(fromKg), FuelCheckText.Kg(toKg), FuelCheckText.SignedKg(toKg - fromKg));

        if (_fuelPlanId is not null)
        {
            FuelChecks.Add(new FuelCheckEntry
            {
                TimeUtc = now,
                ZuluSec = s.ZuluTimeSec,
                Kind = kind,
                ActualKg = toKg,
                FromKg = fromKg,
                DistanceFlownNm = sample?.DistanceFlownNm,
                Message = message,
            });
        }

        if (IsEnabled(Rules.FuelCheck))
        {
            // Stumm (ohne Callout): nur im Verlauf, damit klar ist, warum der Vergleich neu beginnt
            events.Add(Event(now, Rules.FuelCheck, AlertSeverity.Info, AlertKind.Event, null, message));
        }
    }

    /// <summary>Wurde seit dem letzten Takt ein Wegpunkt überflogen? Nur Schritte nach vorn zählen, jeder Abschnitt einmal.</summary>
    private bool TrackFuelLeg(FuelPlanSample sample)
    {
        if (_fuelLeg is not { } last)
        {
            _fuelLeg = sample.LegIndex;
            return false;
        }

        if (sample.LegIndex > last)
        {
            _fuelLeg = sample.LegIndex;
            return sample.LegIndex - last <= FuelCheckMaxLegSkip;
        }

        if (sample.LegIndex < last - FuelCheckMaxLegSkip)
        {
            // Weit zurück: neu zugeordnet (Slew, Route geändert)
            _fuelLeg = sample.LegIndex;
        }

        return false;
    }

    /// <summary>Reiseflug laut Plan: Abschnitt mit SimBrief-Phase CRZ (ohne Phase: erkannte Phase "cruise"), auf der Route, nicht im SID/STAR.</summary>
    private bool FuelCruise(FlightSnapshot s, FuelPlanSample sample) =>
        s.Airborne
        && !sample.InTerminalArea
        && Math.Abs(sample.CrossTrackNm) <= FuelCheckMaxCrossTrackNm
        && (sample.NextFix?.Stage is { } stage ? stage == "CRZ" : Phase == "cruise");

    /// <summary>TOC und TOD sind keine Wegpunkte für den Check (TOD hat seine eigene Ansage).</summary>
    private bool FuelCheckFixEligible(FuelCheckFix? fix) =>
        fix is not null
        && !fix.Ident.Equals("TOC", StringComparison.OrdinalIgnoreCase)
        && !fix.Ident.Equals("TOD", StringComparison.OrdinalIgnoreCase)
        && (fix.Stage is { } stage ? stage == "CRZ" : Phase == "cruise");

    /// <summary>0 = in Ordnung, 1 = unter Minimum + Puffer, 2 = unter Minimum. <paramref name="marginKg"/> für die Hysterese.</summary>
    private (int Stage, double? ThresholdKg) LandingStage(FuelPlanSample sample, double marginKg)
    {
        if (sample.PredictedLandingKg is not { } predicted || sample.MinimumLandingKg is not { } minimum)
        {
            return (0, null);
        }

        var buffer = Math.Max(0, _settings.Fuel.LandingBufferKg);
        if (sample.PlannedLandingKg is { } planned)
        {
            buffer = Math.Min(buffer, Math.Max(0, (planned - minimum) / 2));
        }

        var threshold = minimum + buffer;
        var value = predicted - marginKg;
        return (value < minimum ? 2 : value < threshold ? 1 : 0, threshold);
    }

    /// <summary>Sim-Sekunden seit dem letzten Takt (ZULU TIME, damit Pausen und Sim-Rate stimmen), sonst Wanduhr.</summary>
    private double AdvanceFuelClock(FlightSnapshot s, DateTimeOffset now)
    {
        double dt = 0;
        if (s.ZuluTimeSec is { } z && double.IsFinite(z))
        {
            if (_fuelClockZulu is { } prev)
            {
                var dz = z - prev;
                if (dz < -43200)
                {
                    dz += 86400;
                }

                // Rückwärts oder großer Sprung = Uhr verstellt: zählt nicht
                dt = dz is >= 0 and <= 120 ? dz : 0;
            }

            _fuelClockZulu = z;
        }
        else if (_fuelClockUtc is { } last && !_fuelGap)
        {
            dt = Math.Clamp((now - last).TotalSeconds, 0, 2);
        }

        _fuelClockUtc = now;
        return dt;
    }

    private void StartFuelPlan(FuelPlanSample sample)
    {
        _fuelPlanId = sample.PlanId;
        _fuelLeg = null;
        _fuelBaseline = null;
        _fuelRestartLabel = null;
        _fuelLandingStage = 0;
        _fuelSinceCheckSec = 0;
        FuelChecks.BeginPlan(sample.PlanId);
    }

    /// <summary>Nach Pause oder eingefrorenen Werten: die Lücke nicht als verbrannte Zeit werten.</summary>
    private void NoteFuelGap() => _fuelGap = true;

    private void ResetFuel()
    {
        _fuelPlanId = null;
        _fuelLeg = null;
        _fuelPrevKg = null;
        _fuelGap = false;
        _fuelRiseFromKg = null;
        _fuelBaseline = null;
        _fuelRestartLabel = null;
        _fuelSinceCheckSec = 0;
        _fuelClockZulu = null;
        _fuelClockUtc = null;
        _fuelLandingStage = 0;
    }

    /// <summary>Sim-Uhrzeit "HH:mmZ" (ohne Sim-Uhr die Wanduhr).</summary>
    private static string Clock(double? zuluSec, DateTimeOffset now)
    {
        var t = zuluSec is { } z && double.IsFinite(z) ? TimeSpan.FromSeconds(((z % 86400) + 86400) % 86400) : now.UtcDateTime.TimeOfDay;
        return t.ToString(@"hh\:mm", CultureInfo.InvariantCulture) + "Z";
    }

    /// <summary>Bezug für "seit dem letzten Check": Abweichung damals und wo das war (für die Meldung).</summary>
    private sealed record FuelBaseline(double DiffKg, FuelSince Since, string? Arg = null);

    /// <summary>Worauf sich "seit …" bezieht – der Text entsteht erst in der Meldung, in deren Sprache.</summary>
    private enum FuelSince
    {
        Start,
        Fix,
        Check,
        Warning,
        Refuel,
        FuelChange,
    }

    /// <summary>"since HENDY", "since the 14:20Z check" … in der gewählten Sprache.</summary>
    private static string SinceText(FuelBaseline baseline) => baseline.Since switch
    {
        FuelSince.Fix => Loc.T("monitor.fuel.sinceFix", baseline.Arg),
        FuelSince.Check => Loc.T("monitor.fuel.sinceCheck", baseline.Arg),
        FuelSince.Warning => Loc.T("monitor.fuel.sinceWarning"),
        FuelSince.Refuel => Loc.T("monitor.fuel.sinceRefuel"),
        FuelSince.FuelChange => Loc.T("monitor.fuel.sinceFuelChange"),
        _ => Loc.T("monitor.fuel.sinceStart"),
    };
}
