using System.Globalization;
using Kopilot.Atc;

namespace Kopilot.Monitoring;

/// <summary>
/// Pilot-Monitoring-Callouts (Startlauf, Landung, Höhenmarken, Konfiguration) und – nur auf Wunsch – die Ansagen,
/// die viele Airliner selbst machen (Radiohöhen, Minimum, Retard).
/// </summary>
public sealed partial class MonitorEngine
{
    private static readonly (double Ft, string Words)[] RadioAltitudeMarks =
    [
        (2500, "Twenty five hundred"), (1000, "One thousand"), (500, "Five hundred"), (400, "Four hundred"),
        (300, "Three hundred"), (200, "Two hundred"), (100, "One hundred"), (50, "Fifty"), (40, "Forty"),
        (30, "Thirty"), (20, "Twenty"), (10, "Ten"),
    ];

    // Startlauf / Steigflug
    private DateTimeOffset? _touchdownUtc;
    private bool _onLandingRoll;
    private bool _positiveClimbDone;

    // Landerollen
    private bool _spoilersDone;
    private bool _reverseDone;
    private bool _decelDone;
    private DateTimeOffset? _decelSince;
    private double? _groundDecelKtPerSec;

    // Höhenmarken
    private bool _tenThousandArmed = true;
    private bool _transitionAltitudeDone;
    private bool _transitionLevelDone;

    // Konfiguration: angesagter Stand und Kandidat, der erst stabil sein muss (Hebel wandert über Zwischenstellungen)
    private string? _flapsAnnounced;
    private string? _flapsCandidate;
    private DateTimeOffset _flapsCandidateSince;
    private bool? _gearAnnounced;
    private bool? _gearCandidate;
    private DateTimeOffset _gearCandidateSince;

    // Ansagen des Flugzeugs (optional)
    private readonly HashSet<double> _raMarksDone = [];
    private bool _hundredAboveDone;
    private bool _minimumDone;
    private bool _retardDone;

    private void CheckPmCallouts(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        var p = _prev;
        void Say(string rule, string callout, string message, AlertSeverity severity = AlertSeverity.Info)
        {
            if (!suppressed && IsEnabled(rule))
            {
                events.Add(Event(now, rule, severity, AlertKind.Event, callout, message));
            }
        }

        // Verzögerung am Boden (kt/s, geglättet) für "Decel" ohne DECEL-Licht
        if (p is not null && s.OnGround == true && p.OnGround == true && s.GroundSpeedKt is { } gs && p.GroundSpeedKt is { } pgs)
        {
            var dt = (now - p.TimeUtc).TotalSeconds;
            if (dt is > 0.05 and < 2)
            {
                var rate = (gs - pgs) / dt;
                _groundDecelKtPerSec = _groundDecelKtPerSec is { } r ? r * 0.7 + rate * 0.3 : rate;
            }
        }
        else
        {
            _groundDecelKtPerSec = null;
        }

        // ---------------- Startlauf: 100 kt, V1, Rotate
        if (Phase == "takeoff_roll" && p is not null && s.IasKt is { } ias && p.IasKt is { } pias && ias > pias)
        {
            if (Crossed(pias, ias, 100))
            {
                Say(Rules.Pm100Knots, "One hundred knots", "Startlauf: 100 kt.");
            }

            if (s.V1Kt is { } v1 && Crossed(pias, ias, v1))
            {
                Say(Rules.PmV1, "V1", $"V1 ({v1:0} kt).");
            }

            if (s.VrKt is { } vr && Crossed(pias, ias, vr))
            {
                Say(Rules.PmRotate, "Rotate", $"VR ({vr:0} kt).");
            }
        }

        // ---------------- Positive climb: kurz nach dem Abheben, wenn es wirklich steigt
        if (s.OnGround == true)
        {
            _positiveClimbDone = false;
        }
        else if (!_positiveClimbDone && _liftoffUtc is { } lift && now - lift < TimeSpan.FromSeconds(40)
                 && s.VerticalSpeedFpm is > 300 && (s.RadioAltFt ?? s.AglFt) is > 30)
        {
            _positiveClimbDone = true;
            Say(Rules.PmPositiveClimb, "Positive climb", "Positive Steigrate nach dem Abheben.");
        }

        // ---------------- Landerollen: Spoilers, Reverse, Decel, 70 kt
        if (_onLandingRoll && _touchdownUtc is { } touchdown)
        {
            var sinceTouchdown = now - touchdown;
            if (!_spoilersDone)
            {
                if (s.GroundSpoilersOut == true)
                {
                    _spoilersDone = true;
                    Say(Rules.PmSpoilers, "Spoilers", "Ground Spoiler ausgefahren.");
                }
                else if (s.GroundSpoilersOut == false && sinceTouchdown > TimeSpan.FromSeconds(3))
                {
                    _spoilersDone = true;
                    Say(Rules.PmSpoilers, "No spoilers", "Ground Spoiler 3 s nach dem Aufsetzen nicht ausgefahren.", AlertSeverity.Caution);
                }
            }

            if (!_reverseDone && s.ReverserPct is >= 90)
            {
                _reverseDone = true;
                Say(Rules.PmReverse, "Reverse green", "Schubumkehr ausgefahren.");
            }

            if (!_decelDone)
            {
                if (s.DecelLight is { } decel)
                {
                    if (decel)
                    {
                        _decelDone = true;
                        Say(Rules.PmDecel, "Decel", "Autobrake DECEL.");
                    }
                }
                else if (_groundDecelKtPerSec is <= -3)
                {
                    // Ohne DECEL-Licht: spürbare Verzögerung (gut 0,15 g) für eine Sekunde
                    _decelSince ??= now;
                    if (now - _decelSince.Value >= TimeSpan.FromSeconds(1))
                    {
                        _decelDone = true;
                        Say(Rules.PmDecel, "Decel", "Verzögerung spürbar.");
                    }
                }
                else
                {
                    _decelSince = null;
                }
            }

            if (p is not null && p.IasKt is { } before && s.IasKt is { } after && before >= 70 && after < 70)
            {
                Say(Rules.Pm70Knots, "Seventy knots", "Landerollen: 70 kt.");
            }
        }
        else
        {
            _spoilersDone = false;
            _reverseDone = false;
            _decelDone = false;
            _decelSince = null;
        }

        // ---------------- Höhenmarken: 10 000 ft, Transition Altitude/Level
        // Sprung um mehr als 2000 ft zwischen zwei Messungen: Positionswechsel/Slew, kein echtes Durchfliegen
        if (s.Airborne && p is not null && s.AltitudeFt is { } alt && p.AltitudeFt is { } palt && Math.Abs(alt - palt) <= MaxAltitudeStepFt)
        {
            if (Math.Abs(alt - 10000) > 500)
            {
                _tenThousandArmed = true;
            }

            if (_tenThousandArmed && Crossed(palt, alt, 10000) && s.VerticalSpeedFpm is { } vs && Math.Abs(vs) > 300)
            {
                _tenThousandArmed = false;
                Say(Rules.PmTenThousand, "Ten thousand", vs > 0 ? "Steigflug durch 10.000 ft." : "Sinkflug durch 10.000 ft.");
            }

            if (!_transitionAltitudeDone && s.TransitionAltitudeFt is { } ta && ta > 0 && s.VerticalSpeedFpm is > 300 && palt < ta && alt >= ta)
            {
                _transitionAltitudeDone = true;
                Say(Rules.PmTransition, "Transition altitude", $"Transition Altitude {ta:0} ft – Höhenmesser auf STD.");
            }
        }

        if (s.Airborne && p is not null && s.PressureAltitudeFt is { } pa && p.PressureAltitudeFt is { } ppa
            && Math.Abs(pa - ppa) <= MaxAltitudeStepFt && !_transitionLevelDone && s.TransitionLevelFt is { } tl && tl > 0 && s.VerticalSpeedFpm is < -300 && ppa > tl && pa <= tl)
        {
            _transitionLevelDone = true;
            Say(Rules.PmTransition, "Transition level", $"Transition Level FL{tl / 100:000} – Höhenmesser auf QNH.");
        }

        if (s.OnGround == true)
        {
            _transitionAltitudeDone = false;
            _transitionLevelDone = false;
        }

        // ---------------- Konfiguration: Klappen und Fahrwerk
        var flaps = s.FlapsLabel ?? (s.FlapsIndex is { } fi ? Math.Round(fi).ToString(CultureInfo.InvariantCulture) : null);
        if (flaps is not null)
        {
            if (_flapsAnnounced is null || suppressed)
            {
                _flapsAnnounced = flaps;
                _flapsCandidate = null;
            }
            else if (flaps != _flapsAnnounced)
            {
                if (flaps != _flapsCandidate)
                {
                    _flapsCandidate = flaps;
                    _flapsCandidateSince = now;
                }
                else if (now - _flapsCandidateSince >= TimeSpan.FromSeconds(1.5))
                {
                    var extending = FlapsOrder(flaps, s) > FlapsOrder(_flapsAnnounced, s);
                    var words = "flaps " + FlapsWords(flaps);
                    string callout;
                    if (extending && s.Airborne && s.IasKt is { } fias && s.Limits.VfeFor(s.FlapsIndex, s.FlapsLabel) is { } vfe)
                    {
                        callout = fias <= vfe ? "Speed checked, " + words : Capitalize(words) + ", speed high";
                    }
                    else
                    {
                        callout = Capitalize(words);
                    }

                    _flapsAnnounced = flaps;
                    _flapsCandidate = null;

                    // Hat Kopilot die Klappen selbst gesetzt, sagt das Eingabe-Modul sie erst bei erreichter Stellung an
                    if (_settings.Claims?.IsClaimed(ConfigCalloutClaims.Flaps, now) != true)
                    {
                        Say(Rules.PmFlaps, callout, $"Klappen {flaps}.");
                    }
                }
            }
        }

        var gearDown = s.GearHandleDown;
        if (gearDown is { } down)
        {
            if (_gearAnnounced is null || suppressed || !s.Airborne)
            {
                _gearAnnounced = down;
                _gearCandidate = null;
            }
            else if (down != _gearAnnounced)
            {
                if (down != _gearCandidate)
                {
                    _gearCandidate = down;
                    _gearCandidateSince = now;
                }
                else if (now - _gearCandidateSince >= TimeSpan.FromSeconds(1))
                {
                    _gearAnnounced = down;
                    _gearCandidate = null;

                    // Hat Kopilot das Fahrwerk selbst bewegt, kommt "Gear down, three green" vom Eingabe-Modul
                    if (_settings.Claims?.IsClaimed(ConfigCalloutClaims.Gear, now) != true)
                    {
                        Say(Rules.PmGear, down ? "Gear down" : "Gear up", down ? "Fahrwerkshebel unten." : "Fahrwerkshebel oben.");
                    }
                }
            }
        }
    }

    /// <summary>Radiohöhen, Hundred above/Minimum und Retard – nur mit Monitoring:AircraftAutoCallouts.</summary>
    private void CheckAircraftAutoCallouts(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        var p = _prev;
        if (!s.Airborne || s.RadioAltFt is not { } ra || p?.RadioAltFt is not { } pra)
        {
            if (s.OnGround == true)
            {
                _raMarksDone.Clear();
                _hundredAboveDone = false;
                _minimumDone = false;
                _retardDone = false;
            }

            return;
        }

        // Steigt das Flugzeug wieder über 2600 ft RA (Durchstarten), beginnt alles von vorn
        if (ra > 2600)
        {
            _raMarksDone.Clear();
            _hundredAboveDone = false;
            _minimumDone = false;
            _retardDone = false;
            return;
        }

        if (s.VerticalSpeedFpm is not < -100)
        {
            return;
        }

        void Say(string rule, string callout, string message)
        {
            if (!suppressed && IsEnabled(rule))
            {
                events.Add(Event(now, rule, AlertSeverity.Info, AlertKind.Event, callout, message));
            }
        }

        // Minimums: DH über Radiohöhe, sonst MDA barometrisch
        double? minimum = null;
        double current = ra;
        double previous = pra;
        if (s.DecisionHeightFt is { } dh && dh > 0)
        {
            minimum = dh;
        }
        else if (s.MinimumDescentAltitudeFt is { } mda && mda > 0 && s.AltitudeFt is { } alt && p.AltitudeFt is { } palt)
        {
            minimum = mda;
            current = alt;
            previous = palt;
        }

        if (minimum is { } m)
        {
            if (!_hundredAboveDone && Crossed(previous, current, m + 100))
            {
                _hundredAboveDone = true;
                Say(Rules.AutoMinimum, "Hundred above", $"100 ft über Minimum ({m:0} ft).");
                return;
            }

            if (!_minimumDone && Crossed(previous, current, m))
            {
                _minimumDone = true;
                Say(Rules.AutoMinimum, "Minimum", $"Minimum ({m:0} ft).");
                return;
            }
        }

        foreach (var (ft, words) in RadioAltitudeMarks)
        {
            if (Crossed(pra, ra, ft) && _raMarksDone.Add(ft))
            {
                // Bei 10/20 ft ruft der Airbus RETARD statt der Zahl, wenn der Schub noch nicht im Leerlauf ist
                if (ft <= 20 && !_retardDone && s.Fma?.Thrust is not null and not "THR IDLE" and not "")
                {
                    _retardDone = true;
                    Say(Rules.AutoRetard, "Retard", "Schubhebel in Leerlauf.");
                }
                else
                {
                    Say(Rules.AutoRadioAltitude, words, $"Radiohöhe {ft:0} ft.");
                }

                break;
            }
        }
    }

    private void ResetCallouts()
    {
        _touchdownUtc = null;
        _onLandingRoll = false;
        _positiveClimbDone = false;
        _spoilersDone = false;
        _reverseDone = false;
        _decelDone = false;
        _decelSince = null;
        _tenThousandArmed = true;
        _transitionAltitudeDone = false;
        _transitionLevelDone = false;
        _flapsAnnounced = null;
        _flapsCandidate = null;
        _gearAnnounced = null;
        _gearCandidate = null;
        _raMarksDone.Clear();
        _hundredAboveDone = false;
        _minimumDone = false;
        _retardDone = false;
        _groundDecelKtPerSec = null;
    }

    /// <summary>Schwelle zwischen zwei Messungen überschritten (in beide Richtungen).</summary>
    private const double MaxAltitudeStepFt = 2000;

    private static bool Crossed(double before, double after, double threshold) =>
        (before < threshold && after >= threshold) || (before > threshold && after <= threshold);

    /// <summary>Reihenfolge der Klappenstellungen, damit "ausfahren" erkannt wird.</summary>
    private static int FlapsOrder(string flaps, FlightSnapshot s) => flaps.ToUpperInvariant() switch
    {
        "0" => 0,
        "1" => 1,
        "1+F" => 2,
        "2" => 3,
        "3" => 4,
        "FULL" => 5,
        _ => int.TryParse(flaps, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
    };

    /// <summary>Sprechfassung: "1+F" heißt im Airbus "one", "0" heißt "zero".</summary>
    private static string FlapsWords(string flaps) => flaps.ToUpperInvariant() switch
    {
        "FULL" => "full",
        "1+F" => "one",
        _ when int.TryParse(flaps, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => Phraseology.DigitByDigit(n),
        _ => flaps.ToLowerInvariant(),
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
