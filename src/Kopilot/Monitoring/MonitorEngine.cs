using System.Globalization;

namespace Kopilot.Monitoring;

public sealed class MonitorSettings
{
    public ISet<string> DisabledRules { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Radiohöhen, Minimum und Retard auch von Kopilot ansagen. Standard aus – Airliner wie der FBW A32NX sprechen sie selbst (FWC).
    /// Zur Laufzeit umschaltbar (Dashboard).
    /// </summary>
    public bool AircraftAutoCallouts { get; set; }

    /// <summary>Ruhezeit nach Verbindungsaufbau, Flugzeugwechsel oder Air-Start.</summary>
    public TimeSpan SuppressAfterLoad { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Ruhezeit nach dem Ende einer Pause.</summary>
    public TimeSpan SuppressAfterUnpause { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Ältere Werte gelten als eingefroren – dann wird nichts ausgewertet.</summary>
    public double MaxDataAgeSec { get; init; } = 3;
}

/// <summary>
/// Regelwerk des Pilot Monitoring. Rein rechnend (keine Sim-Abhängigkeit): bekommt Snapshots, liefert Meldungen.
/// Nicht threadsicher – wird nur vom MonitoringService aufgerufen.
/// </summary>
public sealed partial class MonitorEngine
{
    private readonly MonitorSettings _settings;
    private readonly List<StateRule> _stateRules;
    private readonly StateRule _pauseRule;

    private FlightSnapshot? _prev;
    private string? _aircraftKey;
    private bool _seenFlight;
    private bool _wasPaused;
    private DateTimeOffset _suppressUntil;

    // Kontext für Regeln
    private DateTimeOffset? _liftoffUtc;
    private DateTimeOffset? _lastApDisconnectUtc;
    private bool _apWasEngaged;
    private DateTimeOffset? _apOffSince;
    private double? _iasRateKtPerSec;
    private double? _lastSpeedTarget;
    private DateTimeOffset _speedTargetChangedUtc;
    private double? _altTarget;
    private bool _altArmed;
    private string? _announcedRunwayKey;

    // ECAM: wann ist welche Titelzeile zuerst aufgetaucht (für "neueste Meldung")
    private readonly Dictionary<string, DateTimeOffset> _ecamFirstSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ecamAnnounced = new(StringComparer.Ordinal);

    // Top of Descent und Landecheck
    private bool _todAnnounced;
    private bool _todPlanArmed;
    private bool _landingCheck2000Done;
    private bool _landingCheck1000Done;

    public MonitorEngine(MonitorSettings? settings = null)
    {
        _settings = settings ?? new MonitorSettings();
        _pauseRule = new StateRule(Rules.SimPaused, AlertSeverity.Info, 0, 0, 0, s => s.Paused ? new RuleCheck("Sim paused", $"Simulator pausiert ({s.PauseKind ?? "pause"}).") : null);
        _stateRules =
        [
            new(Rules.GearSpeed, AlertSeverity.Warning, 1, 2, 20, CheckGearSpeed),
            new(Rules.FlapSpeed, AlertSeverity.Warning, 1, 2, 20, CheckFlapSpeed),
            new(Rules.Overspeed, AlertSeverity.Warning, 1, 2, 15, CheckOverspeed),
            new(Rules.Stall, AlertSeverity.Warning, 0.5, 1, 5, CheckStall),
            new(Rules.SinkRate, AlertSeverity.Warning, 1.5, 2, 10, CheckSinkRate),
            new(Rules.BankAngle, AlertSeverity.Warning, 1, 1, 10, CheckBankAngle),
            new(Rules.MasterWarning, AlertSeverity.Warning, 0.7, 0.5, 0, s => CheckMaster(s, s.MasterWarning, "Master warning")),
            new(Rules.MasterCaution, AlertSeverity.Caution, 0.7, 0.5, 0, s => CheckMaster(s, s.MasterCaution, "Master caution")),
            new(Rules.GearDownHigh, AlertSeverity.Caution, 15, 3, 120, CheckGearDownHigh),
            new(Rules.SpeedLimitFl100, AlertSeverity.Caution, 5, 3, 60, CheckSpeedLimit),
            new(Rules.SpeedDeviation, AlertSeverity.Caution, 4, 2, 30, CheckSpeedDeviation),
        ];
    }

    /// <summary>Grobe Flugphase (nur zur Information und für Schwellwerte).</summary>
    public string Phase { get; private set; } = "offline";

    public DateTimeOffset SuppressedUntilUtc => _suppressUntil;

    public IEnumerable<string> EnabledRules => Rules.All.Where(IsEnabled);

    public bool IsEnabled(string rule) =>
        !_settings.DisabledRules.Contains(rule) && (!Rules.IsAircraftAutoCallout(rule) || _settings.AircraftAutoCallouts);

    public bool AircraftAutoCallouts
    {
        get => _settings.AircraftAutoCallouts;
        set => _settings.AircraftAutoCallouts = value;
    }

    public IReadOnlyList<AlertEvent> Evaluate(FlightSnapshot s)
    {
        var events = new List<AlertEvent>();
        var now = s.TimeUtc;

        if (!s.Connected || s.FlightRunning == false)
        {
            ResetAll(events, now, s.Connected ? "Kein Flug geladen." : "Keine Verbindung zum Sim.");
            Phase = s.Connected ? "menu" : "offline";
            return events;
        }

        // Neuer Flug, Flugzeugwechsel oder Air-Start: Werte springen – kurz nichts melden
        if (!_seenFlight || !string.Equals(s.AircraftKey, _aircraftKey, StringComparison.Ordinal))
        {
            ResetAll(events, now, "Flugzeug geladen.");
            _seenFlight = true;
            _aircraftKey = s.AircraftKey;
            _suppressUntil = now + _settings.SuppressAfterLoad;
        }

        // Pause: nur SIM_PAUSED, alles andere einfrieren
        if (s.Paused)
        {
            _wasPaused = true;
            if (IsEnabled(Rules.SimPaused))
            {
                Step(_pauseRule, s, now, suppressed: false, events);
            }

            foreach (var rule in _stateRules)
            {
                rule.PendingSince = null;
            }

            _apOffSince = null;
            Phase = "paused";
            _prev = s;
            return events;
        }

        if (_wasPaused)
        {
            _wasPaused = false;
            if (_pauseRule.Active)
            {
                Clear(_pauseRule, now, "Sim läuft wieder.", events);
            }

            Later(ref _suppressUntil, now + _settings.SuppressAfterUnpause);
        }

        if (s.DataAgeSec is not { } age || age > _settings.MaxDataAgeSec)
        {
            Phase = "stale";
            return events;
        }

        UpdateContext(s, now);
        var newEcamTitles = UpdateEcam(s, now);
        var suppressed = now < _suppressUntil;

        foreach (var rule in _stateRules)
        {
            if (IsEnabled(rule.Id))
            {
                Step(rule, s, now, suppressed, events);
            }
        }

        CheckApDisconnect(s, now, suppressed, events);
        CheckEcamMessages(s, now, suppressed, newEcamTitles, events);
        CheckAltitudeToGo(s, now, suppressed, events);
        CheckTopOfDescent(s, now, suppressed, events);
        CheckLandingConfiguration(s, now, suppressed, events);
        CheckRunwayAssigned(s, now, suppressed, events);
        CheckPmCallouts(s, now, suppressed, events);
        CheckAircraftAutoCallouts(s, now, suppressed, events);

        _prev = s;
        return events;
    }

    // ------------------------------------------------------------------ Zustandsregeln

    private RuleCheck? CheckGearSpeed(FlightSnapshot s) =>
        s.Airborne && s.GearNotUp && s.Limits.VleKt is { } vle && s.IasKt is { } ias && ias > vle + 3
            ? new RuleCheck("Gear speed", $"Fahrwerk nicht eingefahren bei {Kt(ias)}, VLE {Kt(vle)}.")
            : null;

    private RuleCheck? CheckFlapSpeed(FlightSnapshot s) =>
        s.Airborne && s.Limits.VfeFor(s.FlapsIndex, s.FlapsLabel) is { } vfe && s.IasKt is { } ias && ias > vfe + 3
            ? new RuleCheck("Flap speed", $"Klappen {s.FlapsLabel ?? s.FlapsIndex?.ToString("0", CultureInfo.InvariantCulture)}: {Kt(ias)} über VFE {Kt(vfe)}.")
            : null;

    private RuleCheck? CheckOverspeed(FlightSnapshot s)
    {
        if (!s.Airborne || s.IasKt is not { } ias)
        {
            return null;
        }

        if (s.Limits.VmoKt is { } vmo && ias > vmo + 4)
        {
            return new RuleCheck("Overspeed", $"{Kt(ias)} über VMO {Kt(vmo)}.");
        }

        return s.OverspeedWarning == true ? new RuleCheck("Overspeed", $"Overspeed-Warnung des Sims bei {Kt(ias)}.") : null;
    }

    private static RuleCheck? CheckStall(FlightSnapshot s) =>
        s.Airborne && s.StallWarning == true && s.RadioAltFt is null or > 50
            ? new RuleCheck("Stall", $"Stall-Warnung bei {Kt(s.IasKt)}.")
            : null;

    private static RuleCheck? CheckSinkRate(FlightSnapshot s)
    {
        if (!s.Airborne || s.RadioAltFt is not { } ra || ra is > 2500 or < 30 || s.VerticalSpeedFpm is not { } vs)
        {
            return null;
        }

        // Unter 1000 ft RA ab 1500 ft/min, darüber linear bis 3000 ft/min bei 2500 ft RA
        var limit = ra < 1000 ? -1500 : -1500 - (ra - 1000) / 1500 * 1500;
        return vs < limit
            ? new RuleCheck("Sink rate", $"Sinkrate {Math.Round(-vs):0} ft/min bei {Math.Round(ra):0} ft Radiohöhe.")
            : null;
    }

    private static RuleCheck? CheckBankAngle(FlightSnapshot s)
    {
        if (!s.Airborne || s.BankRightDeg is not { } bank)
        {
            return null;
        }

        // Grenze wie beim EGPWS: in Bodennähe enger
        double limit;
        if (s.RadioAltFt is not { } ra)
        {
            limit = 45;
        }
        else if (ra < 10)
        {
            return null;
        }
        else if (ra < 30)
        {
            limit = 10;
        }
        else if (ra < 150)
        {
            limit = 10 + (ra - 30) / 120 * 25;
        }
        else if (ra < 2500)
        {
            limit = 35;
        }
        else
        {
            limit = 45;
        }

        return Math.Abs(bank) > limit
            ? new RuleCheck("Bank angle", $"Querneigung {Math.Abs(bank):0}° {(bank > 0 ? "rechts" : "links")} (Grenze {limit:0}°).")
            : null;
    }

    private RuleCheck? CheckMaster(FlightSnapshot s, bool? lit, string callout)
    {
        if (lit != true)
        {
            return null;
        }

        // Die zuletzt aufgetauchte Meldung nennen, nicht die oberste (z. B. ELEC GEN 1 OFF statt eines alten BRAKES HOT)
        var title = NewestEcamTitle(s);
        var message = title is null ? callout + "." : $"{callout}: {string.Join(", ", s.EcamTitles)}.";

        // Doppelte Ansagen vermeiden: AP OFF kommt schon als "Autopilot off"
        if (title is not null && title.Contains("AP OFF", StringComparison.OrdinalIgnoreCase)
            && _lastApDisconnectUtc is { } apOff && s.TimeUtc - apOff < TimeSpan.FromSeconds(5))
        {
            return new RuleCheck(null, message);
        }

        return new RuleCheck(title is null ? callout : $"{callout}, {title}", message);
    }

    private static RuleCheck? CheckGearDownHigh(FlightSnapshot s) =>
        s.Airborne && s.GearDown
        && ((s.AglFt is > 2000 && s.VerticalSpeedFpm is > 500) || s.PressureAltitudeFt is > 15000)
            ? new RuleCheck("Gear still down", $"Fahrwerk ausgefahren in {Ft(s.AltitudeFt)}" + (s.VerticalSpeedFpm is > 500 ? " im Steigflug." : "."))
            : null;

    private static RuleCheck? CheckSpeedLimit(FlightSnapshot s)
    {
        var limit = s.Limits.SpeedLimitBelowFl100Kt;
        return s.Airborne && limit > 0 && s.AltitudeFt is < 9900 && s.AglFt is > 500 && s.IasKt is { } ias && ias > limit + 5
            ? new RuleCheck("Speed limit", $"{Kt(ias)} unter FL100 (Limit {Kt(limit)}).")
            : null;
    }

    private RuleCheck? CheckSpeedDeviation(FlightSnapshot s)
    {
        if (!s.Airborne || Phase == "takeoff" || s.SpeedTargetKt is not { } target || s.IasKt is not { } ias || s.AglFt is not > 100)
        {
            return null;
        }

        // Nach einer Änderung des Ziels braucht das Flugzeug Zeit
        if (s.TimeUtc - _speedTargetChangedUtc < TimeSpan.FromSeconds(20))
        {
            return null;
        }

        var approach = s.FlapsIndex is >= 1 && s.AglFt is < 5000;
        var fastLimit = approach ? 10 : 15;
        var slowLimit = approach ? 5 : 10;
        var dev = ias - target;
        var rate = _iasRateKtPerSec ?? 0;

        // Läuft die Geschwindigkeit schon aufs Ziel zu, ist es keine Abweichung
        var fast = dev > fastLimit && rate > -0.3;
        var slow = dev < -slowLimit && rate < 0.3;
        if (!fast && !slow)
        {
            return null;
        }

        return new RuleCheck(
            "Speed",
            $"IAS {Kt(ias)}, Ziel {Kt(target)} ({(dev > 0 ? "+" : "−")}{Math.Abs(Math.Round(dev)):0} kt).");
    }

    // ------------------------------------------------------------------ Ereignisse

    private void CheckApDisconnect(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        switch (s.ApEngaged)
        {
            case true:
                _apWasEngaged = true;
                _apOffSince = null;
                break;
            case false when _apWasEngaged:
                _apOffSince ??= now;
                if (now - _apOffSince.Value >= TimeSpan.FromSeconds(0.5))
                {
                    _apWasEngaged = false;
                    _apOffSince = null;
                    if (!suppressed && s.Airborne && IsEnabled(Rules.ApDisconnect))
                    {
                        _lastApDisconnectUtc = now;
                        events.Add(Event(now, Rules.ApDisconnect, AlertSeverity.Caution, AlertKind.Event, "Autopilot off", $"Autopilot getrennt in {Ft(s.AltitudeFt)}."));
                    }
                }

                break;
        }
    }

    private void CheckAltitudeToGo(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        if (!s.Airborne || s.SelectedAltitudeFt is not { } sel || s.AltitudeFt is not { } alt || sel < 100)
        {
            _altArmed = false;
            _altTarget = null;
            return;
        }

        var diff = sel - alt;
        if (_altTarget is not { } t || Math.Abs(sel - t) >= 100)
        {
            _altTarget = sel;
            _altArmed = Math.Abs(diff) > 1200;
        }
        else if (Math.Abs(diff) > 1200)
        {
            _altArmed = true;
        }

        if (_altArmed && Math.Abs(diff) is <= 1000 and > 200 && s.VerticalSpeedFpm is { } vs && Math.Abs(vs) > 300 && Math.Sign(vs) == Math.Sign(diff))
        {
            _altArmed = false;
            if (!suppressed && IsEnabled(Rules.Alt1000ToGo))
            {
                events.Add(Event(now, Rules.Alt1000ToGo, AlertSeverity.Info, AlertKind.Event, "One thousand to go", $"Noch 1000 ft bis {Ft(sel)}."));
            }
        }
    }

    private void CheckRunwayAssigned(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        var rwy = s.AssignedRunway;
        var key = rwy is null ? null : $"{rwy.Airport}|{rwy.Number}";
        if (key == _announcedRunwayKey)
        {
            return;
        }

        if (rwy is null)
        {
            _announcedRunwayKey = null;
            return;
        }

        if (suppressed)
        {
            return;
        }

        _announcedRunwayKey = key;
        if (IsEnabled(Rules.RunwayAssigned))
        {
            var where = rwy.Airport is null ? "" : $" ({rwy.Airport})";
            var designatorNote = rwy.Designator is null ? " L/C/R unbekannt." : "";
            events.Add(Event(now, Rules.RunwayAssigned, AlertSeverity.Info, AlertKind.Event, $"Runway {rwy.Spoken} assigned", $"ATC hat Bahn {rwy.Ident}{where} zugewiesen.{designatorNote}"));
        }
    }

    /// <summary>
    /// Neue ECAM-Titel, während Master Warning/Caution schon leuchtet. Die erste Meldung nennt bereits der Master-Callout,
    /// weitere kämen sonst stumm dazu.
    /// </summary>
    private void CheckEcamMessages(FlightSnapshot s, DateTimeOffset now, bool suppressed, IReadOnlyList<string> newTitles, List<AlertEvent> events)
    {
        if (newTitles.Count == 0)
        {
            return;
        }

        var warning = _stateRules.First(r => r.Id == Rules.MasterWarning).Active;
        var caution = _stateRules.First(r => r.Id == Rules.MasterCaution).Active;
        if (suppressed || (!warning && !caution) || !IsEnabled(Rules.EcamMessage))
        {
            return;
        }

        foreach (var title in newTitles)
        {
            if (!_ecamAnnounced.Add(title))
            {
                continue;
            }

            var apOff = title.Contains("AP OFF", StringComparison.OrdinalIgnoreCase)
                && _lastApDisconnectUtc is { } t && now - t < TimeSpan.FromSeconds(5);
            events.Add(Event(now, Rules.EcamMessage, warning ? AlertSeverity.Warning : AlertSeverity.Caution, AlertKind.Event,
                apOff ? null : title, $"Neue ECAM-Meldung: {title}."));
        }
    }

    /// <summary>Top of Descent: beim A32NX die FMS-Meldung T/D REACHED, sonst der TOD aus dem SimBrief-Plan.</summary>
    private void CheckTopOfDescent(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        if (!s.Airborne)
        {
            _todAnnounced = false;
            _todPlanArmed = false;
            return;
        }

        if (s.TdReached is { } reached)
        {
            if (!reached)
            {
                _todAnnounced = false;
                return;
            }

            if (_todAnnounced)
            {
                return;
            }

            _todAnnounced = true;
            if (!suppressed && IsEnabled(Rules.TopOfDescent))
            {
                events.Add(Event(now, Rules.TopOfDescent, AlertSeverity.Info, AlertKind.Event, "Top of descent", "FMS meldet T/D REACHED."));
            }

            return;
        }

        // Ohne FMS-Meldung: Plan-TOD. Scharf, solange er noch mehr als 5 nm entfernt ist.
        switch (s.PlanTodDistanceNm)
        {
            case > 5:
                _todPlanArmed = true;
                break;
            case <= 0.5 when _todPlanArmed:
                _todPlanArmed = false;
                if (!suppressed && IsEnabled(Rules.TopOfDescent))
                {
                    events.Add(Event(now, Rules.TopOfDescent, AlertSeverity.Info, AlertKind.Event, "Top of descent", "Top of Descent laut SimBrief-Plan erreicht."));
                }

                break;
        }
    }

    /// <summary>
    /// Landecheck im Sinkflug: bei 2000 ft RA Fahrwerk, Landeklappen und Ground Spoiler prüfen,
    /// bei 1000 ft RA nur noch melden, was dann immer noch fehlt.
    /// </summary>
    private void CheckLandingConfiguration(FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        if (!s.Airborne || s.RadioAltFt is not { } ra || s.RadioAltFt is > 3000)
        {
            _landingCheck2000Done = false;
            _landingCheck1000Done = false;
            return;
        }

        var prevRa = _prev?.RadioAltFt;
        var descending = s.VerticalSpeedFpm is < -100;
        var crossed2000 = prevRa is > 2000 && ra <= 2000 && descending;
        var crossed1000 = prevRa is > 1000 && ra <= 1000 && descending;
        if (Phase == "takeoff" || (!crossed2000 && !crossed1000))
        {
            return;
        }

        var problems = new List<(string Callout, string Text)>();
        if (!s.GearDown)
        {
            problems.Add(("Gear not down", "Fahrwerk nicht unten"));
        }

        if (s.Limits.LandingFlaps.Count > 0 && !s.Limits.IsLandingFlaps(s.FlapsIndex, s.FlapsLabel))
        {
            problems.Add(("Flaps not in landing configuration", $"Klappen {s.FlapsLabel ?? s.FlapsIndex?.ToString("0", CultureInfo.InvariantCulture)} statt {string.Join("/", s.Limits.LandingFlaps)}"));
        }

        if (s.SpoilersArmed == false)
        {
            problems.Add(("Spoilers not armed", "Ground Spoiler nicht armed"));
        }

        if (crossed2000 && !_landingCheck2000Done)
        {
            _landingCheck2000Done = true;
            if (suppressed || !IsEnabled(Rules.LandingCheck))
            {
                return;
            }

            events.Add(problems.Count == 0
                ? Event(now, Rules.LandingCheck, AlertSeverity.Info, AlertKind.Event, "Landing checklist complete", "2000 ft: Fahrwerk unten, Landeklappen gesetzt" + (s.SpoilersArmed == true ? ", Spoiler armed." : "."))
                : Event(now, Rules.LandingCheck, AlertSeverity.Caution, AlertKind.Event, string.Join(", ", problems.Select(p => p.Callout)), "2000 ft: " + string.Join(", ", problems.Select(p => p.Text)) + "."));
        }
        else if (crossed1000 && !_landingCheck1000Done)
        {
            _landingCheck1000Done = true;
            if (problems.Count > 0 && !suppressed && IsEnabled(Rules.LandingCheck))
            {
                events.Add(Event(now, Rules.LandingCheck, AlertSeverity.Warning, AlertKind.Event, string.Join(", ", problems.Select(p => p.Callout)), "1000 ft: " + string.Join(", ", problems.Select(p => p.Text)) + "."));
            }
        }
    }

    /// <summary>Merkt sich, seit wann jede ECAM-Titelzeile da ist, und liefert die in diesem Takt neu hinzugekommenen.</summary>
    private IReadOnlyList<string> UpdateEcam(FlightSnapshot s, DateTimeOffset now)
    {
        var current = s.EcamTitles;
        foreach (var gone in _ecamFirstSeen.Keys.Where(k => !current.Contains(k)).ToList())
        {
            _ecamFirstSeen.Remove(gone);
            _ecamAnnounced.Remove(gone);
        }

        var added = new List<string>();
        foreach (var title in current)
        {
            if (_ecamFirstSeen.TryAdd(title, now))
            {
                added.Add(title);
            }
        }

        return added;
    }

    /// <summary>Die zuletzt aufgetauchte Titelzeile (bei Gleichstand die oberste), sonst null.</summary>
    private string? NewestEcamTitle(FlightSnapshot s)
    {
        string? newest = null;
        var newestTime = DateTimeOffset.MinValue;
        foreach (var title in s.EcamTitles)
        {
            var seen = _ecamFirstSeen.TryGetValue(title, out var t) ? t : s.TimeUtc;
            if (newest is null || seen > newestTime)
            {
                newest = title;
                newestTime = seen;
            }
        }

        return newest;
    }

    // ------------------------------------------------------------------ Mechanik

    private void UpdateContext(FlightSnapshot s, DateTimeOffset now)
    {
        if (_prev is { } p)
        {
            if (p.OnGround == true && s.OnGround == false)
            {
                _liftoffUtc = now;
            }

            // Aufsetzen: bis unter 30 kt Rollgeschwindigkeit gilt das als Landerollen
            if (p.OnGround == false && s.OnGround == true)
            {
                _touchdownUtc = now;
                _onLandingRoll = true;
            }

            var dt = (now - p.TimeUtc).TotalSeconds;
            if (dt is > 0.05 and < 2 && s.IasKt is { } ias && p.IasKt is { } pias)
            {
                var rate = (ias - pias) / dt;
                _iasRateKtPerSec = _iasRateKtPerSec is { } r ? r * 0.8 + rate * 0.2 : rate;
            }
        }

        if (s.SpeedTargetKt is { } target)
        {
            if (_lastSpeedTarget is not { } last || Math.Abs(target - last) > 1)
            {
                _lastSpeedTarget = target;
                _speedTargetChangedUtc = now;
            }
        }
        else
        {
            _lastSpeedTarget = null;
        }

        if (s.OnGround == true && s.GroundSpeedKt is < 30)
        {
            _onLandingRoll = false;
        }

        Phase = DetectPhase(s, now);
    }

    private string DetectPhase(FlightSnapshot s, DateTimeOffset now)
    {
        if (s.OnGround == true)
        {
            if (s.GroundSpeedKt is not > 40)
            {
                return "ground";
            }

            return _onLandingRoll || Phase is "approach" or "rollout" or "descent" ? "rollout" : "takeoff_roll";
        }

        if (s.OnGround is null)
        {
            return "unknown";
        }

        var vs = s.VerticalSpeedFpm ?? 0;
        if (_liftoffUtc is { } lift && now - lift < TimeSpan.FromMinutes(2) && s.AglFt is < 1500)
        {
            return "takeoff";
        }

        if (s.AglFt is < 3000 && vs < -200 && (s.FlapsIndex is >= 1 || s.GearDown))
        {
            return "approach";
        }

        return vs switch
        {
            > 500 => "climb",
            < -500 => "descent",
            _ => s.AltitudeFt is > 10000 ? "cruise" : "level",
        };
    }

    private static void Step(StateRule rule, FlightSnapshot s, DateTimeOffset now, bool suppressed, List<AlertEvent> events)
    {
        var check = rule.Check(s);
        if (check is { } c)
        {
            rule.FalseSince = null;
            if (rule.Active)
            {
                return;
            }

            rule.PendingSince ??= now;
            var onDelayOver = (now - rule.PendingSince.Value).TotalSeconds >= rule.OnDelaySec;
            var cooledDown = rule.ClearedUtc is not { } cleared || (now - cleared).TotalSeconds >= rule.CooldownSec;
            if (!suppressed && onDelayOver && cooledDown)
            {
                rule.Active = true;
                events.Add(Event(now, rule.Id, rule.Severity, AlertKind.Raise, c.Callout, c.Message));
            }
        }
        else
        {
            rule.PendingSince = null;
            if (!rule.Active)
            {
                return;
            }

            rule.FalseSince ??= now;
            if ((now - rule.FalseSince.Value).TotalSeconds >= rule.OffDelaySec)
            {
                Clear(rule, now, "Normal.", events);
            }
        }
    }

    private static void Clear(StateRule rule, DateTimeOffset now, string message, List<AlertEvent> events)
    {
        rule.Active = false;
        rule.FalseSince = null;
        rule.PendingSince = null;
        rule.ClearedUtc = now;
        events.Add(Event(now, rule.Id, rule.Severity, AlertKind.Clear, null, message));
    }

    private void ResetAll(List<AlertEvent> events, DateTimeOffset now, string reason)
    {
        foreach (var rule in _stateRules.Append(_pauseRule))
        {
            if (rule.Active)
            {
                Clear(rule, now, reason, events);
            }

            rule.PendingSince = null;
            rule.ClearedUtc = null;
        }

        _prev = null;
        _seenFlight = false;
        _wasPaused = false;
        _aircraftKey = null;
        _liftoffUtc = null;
        _lastApDisconnectUtc = null;
        _apWasEngaged = false;
        _apOffSince = null;
        _iasRateKtPerSec = null;
        _lastSpeedTarget = null;
        _altTarget = null;
        _altArmed = false;
        _announcedRunwayKey = null;
        _ecamFirstSeen.Clear();
        _ecamAnnounced.Clear();
        _todAnnounced = false;
        _todPlanArmed = false;
        _landingCheck2000Done = false;
        _landingCheck1000Done = false;
        ResetCallouts();
    }

    private static void Later(ref DateTimeOffset target, DateTimeOffset candidate)
    {
        if (candidate > target)
        {
            target = candidate;
        }
    }

    private static AlertEvent Event(DateTimeOffset now, string rule, AlertSeverity severity, AlertKind kind, string? callout, string message) =>
        new(0, now, rule, severity, kind, callout, message);

    private static string Kt(double? v) => v is { } d ? Math.Round(d).ToString("0", CultureInfo.InvariantCulture) + " kt" : "? kt";

    private static string Ft(double? v) => v is { } d ? Math.Round(d).ToString("0", CultureInfo.InvariantCulture) + " ft" : "? ft";

    private readonly record struct RuleCheck(string? Callout, string Message);

    private sealed class StateRule(string id, AlertSeverity severity, double onDelaySec, double offDelaySec, double cooldownSec, Func<FlightSnapshot, RuleCheck?> check)
    {
        public string Id { get; } = id;

        public AlertSeverity Severity { get; } = severity;

        public double OnDelaySec { get; } = onDelaySec;

        public double OffDelaySec { get; } = offDelaySec;

        public double CooldownSec { get; } = cooldownSec;

        public Func<FlightSnapshot, RuleCheck?> Check { get; } = check;

        public bool Active { get; set; }

        public DateTimeOffset? PendingSince { get; set; }

        public DateTimeOffset? FalseSince { get; set; }

        public DateTimeOffset? ClearedUtc { get; set; }
    }
}
