using Duopilo.Localization;

namespace Duopilo.Monitoring;

/// <summary>
/// Autoflug und Anflug (Testlog 0.9, Anflug KLAX): TCAS RA und "Clear of conflict", "Autothrust off", FMA-Moduswechsel
/// ("Loc star", "Glideslope star", "Alt blue"), LOC- und Glideslope-Abweichung im Endanflug und "Approach phase not activated".
/// Alle hängen an Profil-Bindings (FMA, A/THR, FMGC-Phase, LOC/GS-Abweichung) – ohne Binding schweigt die Regel
/// (<see cref="CalloutCoverage"/> zeigt je Profil, was geprüft, ungeprüft oder fehlend ist).
/// </summary>
public sealed partial class MonitorEngine
{
    /// <summary>LOC-Abweichung, ab der der PM "Loc" ruft (Airbus: 1/4 dot).</summary>
    public const double LocDeviationLimitDots = 0.25;

    /// <summary>Glideslope-Abweichung, ab der der PM "Glideslope" ruft (Airbus: 1 dot).</summary>
    public const double GsDeviationLimitDots = 1.0;

    /// <summary>"Approach phase not activated" ab dieser Entfernung zum Zielflughafen.</summary>
    public const double ApproachPhaseCheckNm = 15;

    /// <summary>So lange muss eine neue FMA stehen, bevor sie angesagt wird (Zwischenzustände wie ALT* → ALT kurz hintereinander).</summary>
    private static readonly TimeSpan FmaSettle = TimeSpan.FromSeconds(1);

    // Bekannte Modi mit mehreren Wörtern zuerst – die Armed-Anzeige kommt als ein Text ("ALT CST G/S")
    private static readonly string[] ArmedModes = ["ALT CST", "ALT", "CLB", "DES", "G/S", "FINAL", "TCAS", "NAV", "LOC", "APP NAV"];

    private static readonly Dictionary<string, string> FmaWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HDG"] = "Heading",
        ["TRK"] = "Track",
        ["NAV"] = "Nav",
        ["LOC*"] = "Loc star",
        ["LOC"] = "Loc",
        ["LAND"] = "Land",
        ["FLARE"] = "Flare",
        ["ROLL OUT"] = "Roll out",
        ["RWY"] = "Runway",
        ["RWY TRK"] = "Runway track",
        ["GA TRK"] = "Go around track",
        ["APP NAV"] = "App nav",
        ["ALT"] = "Alt",
        ["ALT*"] = "Alt star",
        ["ALT CRZ"] = "Alt cruise",
        ["ALT CST"] = "Alt constraint",
        ["ALT CST*"] = "Alt constraint star",
        ["OP CLB"] = "Open climb",
        ["OP DES"] = "Open descent",
        ["EXP CLB"] = "Expedite climb",
        ["EXP DES"] = "Expedite descent",
        ["V/S"] = "Vertical speed",
        ["FPA"] = "FPA",
        ["CLB"] = "Climb",
        ["DES"] = "Descent",
        ["FINAL"] = "Final",
        ["FINAL APP"] = "Final approach",
        ["G/S*"] = "Glideslope star",
        ["G/S"] = "Glideslope",
        ["SRS"] = "SRS",
        ["SRS GA"] = "SRS go around",
    };

    // TCAS RA
    private bool _tcasRa;
    private bool _tcasRaPublished;
    private DateTimeOffset? _tcasSince;
    private DateTimeOffset? _tcasClearSince;

    // A/THR
    private bool _athrWasOn;
    private DateTimeOffset? _athrOffSince;

    // FMA: angesagter Stand und Kandidat
    private FmaModes? _fmaAnnounced;
    private FmaModes? _fmaCandidate;
    private DateTimeOffset _fmaCandidateSince;

    // Approach-Phase
    private bool _approachPhaseDone;

    /// <summary>Die FMA-Teile, die angesagt werden (Schub, AP und FD nicht).</summary>
    private sealed record FmaModes(string? Lateral, string? Vertical, IReadOnlyList<string> LateralArmed, IReadOnlyList<string> VerticalArmed)
    {
        public bool Same(FmaModes other) =>
            Lateral == other.Lateral && Vertical == other.Vertical
            && LateralArmed.SequenceEqual(other.LateralArmed) && VerticalArmed.SequenceEqual(other.VerticalArmed);

        public static FmaModes From(FmaState s) =>
            new(Clean(s.Lateral), Clean(s.Vertical), Armed(s.LateralArmed), Armed(s.VerticalArmed));

        private static string? Clean(string? mode) => string.IsNullOrWhiteSpace(mode) ? null : mode.Trim().ToUpperInvariant();
    }

    private void CheckAutoflight(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        CheckTcasRa(s, now, suppressed, events);
        CheckAutothrustOff(s, now, suppressed, events);
        CheckFmaChanges(s, now, suppressed, events);
        CheckApproachPhase(s, now, suppressed, events);
    }

    // ------------------------------------------------------------------ TCAS RA

    /// <summary>Vertikale FMA "TCAS" = Resolution Advisory: "TCAS RA" (Warnung), danach "Clear of conflict".</summary>
    private void CheckTcasRa(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        var ra = string.Equals(s.Fma?.Vertical?.Trim(), "TCAS", StringComparison.OrdinalIgnoreCase);
        if (ra)
        {
            _tcasClearSince = null;
            if (_tcasRa)
            {
                return;
            }

            _tcasSince ??= now;
            if (now - _tcasSince.Value < TimeSpan.FromSeconds(0.3))
            {
                return;
            }

            _tcasRa = true;
            _tcasSince = null;
            if (!suppressed && s.Airborne && IsEnabled(Rules.TcasRa))
            {
                _tcasRaPublished = true;
                events.Add(Event(now, Rules.TcasRa, AlertSeverity.Warning, AlertKind.Raise, "TCAS RA", Loc.T("monitor.alert.tcasRa", Ft(s.AltitudeFt))));
            }

            return;
        }

        _tcasSince = null;
        if (!_tcasRa)
        {
            return;
        }

        _tcasClearSince ??= now;
        if (now - _tcasClearSince.Value < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        _tcasRa = false;
        _tcasClearSince = null;
        if (_tcasRaPublished)
        {
            _tcasRaPublished = false;
            events.Add(Event(now, Rules.TcasRa, AlertSeverity.Warning, AlertKind.Clear, null, Loc.T("monitor.clear.normal")));
            if (!suppressed)
            {
                events.Add(Event(now, Rules.TcasRa, AlertSeverity.Info, AlertKind.Event, "Clear of conflict", Loc.T("monitor.alert.tcasClear", Ft(s.AltitudeFt))));
            }
        }
    }

    // ------------------------------------------------------------------ Autothrust

    /// <summary>A/THR von ARMED/ACTIVE auf OFF in der Luft (nicht beim Aufsetzen unter 100 ft): "Autothrust off".</summary>
    private void CheckAutothrustOff(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        var status = s.Fma?.Autothrust?.Trim().ToUpperInvariant();
        switch (status)
        {
            case null or "":
                _athrWasOn = false;
                _athrOffSince = null;
                return;
            case "ACTIVE" or "ARMED":
                _athrWasOn = true;
                _athrOffSince = null;
                return;
            case "OFF" when _athrWasOn:
                _athrOffSince ??= now;
                if (now - _athrOffSince.Value < TimeSpan.FromSeconds(1))
                {
                    return;
                }

                _athrWasOn = false;
                _athrOffSince = null;
                var height = s.RadioAltFt ?? s.AglFt;
                if (!suppressed && s.Airborne && height is null or > 100 && IsEnabled(Rules.AutothrustOff))
                {
                    events.Add(Event(now, Rules.AutothrustOff, AlertSeverity.Caution, AlertKind.Event, "Autothrust off", Loc.T("monitor.alert.athrOff", Ft(s.AltitudeFt), Kt(s.IasKt))));
                }

                return;
        }
    }

    // ------------------------------------------------------------------ FMA

    /// <summary>
    /// Moduswechsel der FMA in der Luft ansagen: neuer lateraler/vertikaler Modus ("Loc star", "Glideslope") und neu gearmte
    /// Modi ("Glideslope blue"). Erst wenn der neue Stand 1 s steht; TCAS kommt als eigener Callout.
    /// </summary>
    private void CheckFmaChanges(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        if (s.Fma is not { } fma)
        {
            _fmaAnnounced = null;
            _fmaCandidate = null;
            return;
        }

        var current = FmaModes.From(fma);
        if (_fmaAnnounced is null || suppressed || !s.Airborne)
        {
            _fmaAnnounced = current;
            _fmaCandidate = null;
            return;
        }

        if (current.Same(_fmaAnnounced))
        {
            _fmaCandidate = null;
            return;
        }

        if (_fmaCandidate is null || !current.Same(_fmaCandidate))
        {
            _fmaCandidate = current;
            _fmaCandidateSince = now;
            return;
        }

        if (now - _fmaCandidateSince < FmaSettle)
        {
            return;
        }

        var parts = FmaChangeWords(_fmaAnnounced, current);
        _fmaAnnounced = current;
        _fmaCandidate = null;
        if (parts.Count > 0 && IsEnabled(Rules.FmaChange))
        {
            events.Add(Event(now, Rules.FmaChange, AlertSeverity.Info, AlertKind.Event, Join(parts), Loc.T("monitor.alert.fmaChange", FmaLog.Describe(fma))));
        }
    }

    /// <summary>Was sich an der FMA geändert hat, als Callout-Teile ("Loc star", "Glideslope blue"). Weggefallene Modi nicht.</summary>
    private static List<string> FmaChangeWords(FmaModes before, FmaModes after)
    {
        var parts = new List<string>();
        if (after.Lateral is { } lateral && lateral != before.Lateral)
        {
            parts.Add(FmaWord(lateral));
        }

        // LAND, FLARE und ROLL OUT stehen in beiden Spalten – nur einmal ansagen
        if (after.Vertical is { } vertical && vertical != before.Vertical && vertical != "TCAS" && vertical != after.Lateral)
        {
            parts.Add(FmaWord(vertical));
        }

        foreach (var armed in after.LateralArmed.Where(m => !before.LateralArmed.Contains(m) && m != after.Lateral))
        {
            parts.Add(FmaWord(armed) + " blue");
        }

        foreach (var armed in after.VerticalArmed.Where(m => !before.VerticalArmed.Contains(m) && m != after.Vertical && m != "TCAS"))
        {
            parts.Add(FmaWord(armed) + " blue");
        }

        return parts;
    }

    /// <summary>Sprechfassung eines FMA-Modus ("LOC*" → "Loc star", "G/S" → "Glideslope"); Unbekanntes so, wie es angezeigt wird.</summary>
    public static string FmaWord(string mode)
    {
        var key = mode.Trim();
        if (FmaWords.TryGetValue(key, out var word))
        {
            return word;
        }

        var text = key.Replace("*", " star", StringComparison.Ordinal).Replace("/", " ", StringComparison.Ordinal).ToLowerInvariant();
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Gearmte Modi aus dem Anzeigetext ("ALT CST G/S" → ["ALT CST", "G/S"]), bekannte mehrteilige Namen zuerst.</summary>
    private static IReadOnlyList<string> Armed(string? text)
    {
        var modes = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return modes;
        }

        var rest = " " + text.Trim().ToUpperInvariant() + " ";
        foreach (var mode in ArmedModes)
        {
            var token = " " + mode + " ";
            while (rest.Contains(token, StringComparison.Ordinal))
            {
                if (!modes.Contains(mode))
                {
                    modes.Add(mode);
                }

                var index = rest.IndexOf(token, StringComparison.Ordinal);
                rest = rest[..index] + " " + rest[(index + token.Length)..];
            }
        }

        // Unbekannte Reste als einzelne Modi
        modes.AddRange(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(m => !modes.Contains(m)));
        modes.Sort(StringComparer.Ordinal);
        return modes;
    }

    private static string Join(List<string> parts)
    {
        var text = string.Join(", ", parts.Select((p, i) => i == 0 || p.Length < 2 || !char.IsLower(p[1]) ? p : char.ToLowerInvariant(p[0]) + p[1..]));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ------------------------------------------------------------------ LOC / Glideslope im Endanflug

    /// <summary>LOC gefangen (LOC oder LAND), 50–2000 ft über Grund, Abweichung über 1/4 dot: "Loc".</summary>
    private static RuleCheck? CheckLocDeviation(FlightSnapshot s)
    {
        if (!s.Airborne || s.LocDeviationDots is not { } dots || (s.RadioAltFt ?? s.AglFt) is not { } height || height is < 50 or > 2000
            || s.Fma?.Lateral?.Trim().ToUpperInvariant() is not ("LOC" or "LAND"))
        {
            return null;
        }

        return Math.Abs(dots) > LocDeviationLimitDots
            ? new RuleCheck("Loc", Loc.T("monitor.alert.locDeviation", Math.Abs(dots), LocDeviationLimitDots))
            : null;
    }

    /// <summary>Glideslope gefangen (G/S oder LAND), 100–2000 ft über Grund, Abweichung über 1 dot: "Glideslope".</summary>
    private static RuleCheck? CheckGsDeviation(FlightSnapshot s)
    {
        if (!s.Airborne || s.GsDeviationDots is not { } dots || (s.RadioAltFt ?? s.AglFt) is not { } height || height is < 100 or > 2000
            || s.Fma?.Vertical?.Trim().ToUpperInvariant() is not ("G/S" or "LAND"))
        {
            return null;
        }

        return Math.Abs(dots) > GsDeviationLimitDots
            ? new RuleCheck("Glideslope", Loc.T("monitor.alert.gsDeviation", Math.Abs(dots), GsDeviationLimitDots))
            : null;
    }

    // ------------------------------------------------------------------ Approach-Phase

    /// <summary>
    /// Ab 15 nm vor dem Zielflughafen mit Managed Speed, aber die FMGC-Phase ist noch nicht APPROACH: einmal je Anflug
    /// "Approach phase not activated" (Testlog 0.9: Managed Speed ohne Ziel im Endanflug). Wieder scharf ab 20 nm Entfernung.
    /// </summary>
    private void CheckApproachPhase(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        if (s.OnGround == true || s.DestinationDistanceNm is not { } distance || distance > ApproachPhaseCheckNm + 5)
        {
            _approachPhaseDone = false;
            return;
        }

        if (!s.Airborne || _approachPhaseDone || distance > ApproachPhaseCheckNm || s.SpeedManaged != true
            || s.FmgcPhase?.Trim().ToUpperInvariant() is not { Length: > 0 } phase)
        {
            return;
        }

        _approachPhaseDone = true;
        if (phase is "APPROACH" or "GO AROUND" or "DONE")
        {
            return;
        }

        if (!suppressed && IsEnabled(Rules.ApproachPhase))
        {
            events.Add(Event(now, Rules.ApproachPhase, AlertSeverity.Caution, AlertKind.Event, "Approach phase not activated",
                Loc.T("monitor.alert.approachPhase", Math.Round(distance), phase)));
        }
    }

    private void ResetAutoflight()
    {
        _tcasRa = false;
        _tcasRaPublished = false;
        _tcasSince = null;
        _tcasClearSince = null;
        _athrWasOn = false;
        _athrOffSince = null;
        _fmaAnnounced = null;
        _fmaCandidate = null;
        _approachPhaseDone = false;
    }
}
