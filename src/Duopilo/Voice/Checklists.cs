using System.Globalization;
using System.Text.RegularExpressions;

namespace Duopilo.Voice;

/// <summary>Was ein Checklistenpunkt prüfen kann: der Zustand im Sim und die Antwort des Piloten (normalisiert).</summary>
public sealed record ChecklistContext(VoiceFlightState State, string Answer);

/// <summary>
/// Ein Punkt der Checkliste: Challenge (liest der Kopilot), erwartete Antwort (zur Anzeige) und – wo möglich – der Abgleich mit dem Sim.
/// <see cref="Check"/> liefert die Abweichung zum Ansagen ("Beacon off") oder null (passt oder nicht prüfbar).
/// </summary>
public sealed record ChecklistItem(string Challenge, string Response, Func<ChecklistContext, string?>? Check = null)
{
    /// <summary>Der Punkt wird mit dem Sim abgeglichen.</summary>
    public bool Checked => Check is not null;
}

/// <param name="Id">Kennung für den Sprachbefehl, z. B. "beforeStart".</param>
/// <param name="Title">Name wie im Cockpit, z. B. "Before start".</param>
public sealed record ChecklistDefinition(string Id, string Title, IReadOnlyList<ChecklistItem> Items);

/// <summary>
/// Normal Checklist nach dem Muster der Airbus-Normal-Checklist (A320/A330): Before Start, After Start, Before Takeoff, Approach,
/// Landing, After Landing, Parking. Challenge auf Englisch (SOP), Abgleich mit dem Sim, wo Duopilo den Zustand lesen kann.
/// </summary>
public static partial class AirbusChecklists
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly IReadOnlyList<ChecklistDefinition> All =
    [
        new("beforeStart", "Before start",
        [
            new("Cockpit preparation", "Completed"),
            new("Gear pins and covers", "Removed"),
            new("Signs", "On, auto", c => c.State.SeatbeltsOn == false ? "Seat belts off" : null),
            new("ADIRS", "NAV"),
            new("Fuel quantity", "___ kg", FuelQuantity),
            new("Takeoff data", "Set"),
            new("Baro reference", "___ set", BaroReference),
            new("Windows and doors", "Closed"),
            new("Beacon", "On", c => c.State.BeaconOn == false ? "Beacon off" : null),
            new("Thrust levers", "Idle"),
            new("Parking brake", "As required"),
        ]),
        new("afterStart", "After start",
        [
            new("Anti ice", "As required"),
            new("ECAM status", "Checked"),
            new("Pitch trim", "___ set"),
            new("Rudder trim", "Neutral"),
        ]),
        new("beforeTakeoff", "Before takeoff",
        [
            new("Flight controls", "Checked"),
            new("Flight instruments", "Checked"),
            new("Briefing", "Confirmed"),
            new("Flap setting", "Config ___", FlapSetting),
            new("V1, VR, V2", "___ set"),
            new("ATC", "Set"),
            new("ECAM memo", "Takeoff, no blue"),
            new("Takeoff runway", "___ confirmed"),
            new("Cabin crew", "Advised"),
            new("TCAS", "TA or TA RA"),
            new("Engine mode selector", "As required"),
            new("Packs", "As required"),
        ]),
        new("approach", "Approach",
        [
            new("Briefing", "Confirmed"),
            new("ECAM status", "Checked"),
            new("Seat belts", "On", c => c.State.SeatbeltsOn == false ? "Seat belts off" : null),
            new("Baro reference", "___ set", BaroReference),
            new("Minimum", "___ set"),
            new("Engine mode selector", "As required"),
        ]),
        new("landing", "Landing",
        [
            new("Cabin crew", "Advised"),
            new("Autothrust", "Speed or off", Autothrust),
            new("Autobrake", "As required", Autobrake),
            new("ECAM memo", "Landing, no blue", LandingMemo),
        ]),
        new("afterLanding", "After landing",
        [
            new("Flaps", "Retracted", c => FlapsText(c.State) is { } f && f != "0" ? $"Flaps {Spoken(f)}" : null),
            new("Spoilers", "Disarmed", c => c.State.SpoilersArmed == true ? "Spoilers armed" : null),
            new("APU", "Start", c => Says(c.Answer, "start", "started", "starting", "on", "running", "an") && c.State.ApuNPct is <= 3 ? "APU off" : null),
            new("Radar", "Off"),
            new("Predictive windshear", "Off"),
        ]),
        new("parking", "Parking",
        [
            new("APU bleed", "On", c => c.State.ApuBleedOn == false ? "APU bleed off" : null),
            new("Engines", "Off", Engines),
            new("Seat belts", "Off", c => c.State.SeatbeltsOn == true ? "Seat belts on" : null),
            new("External lights", "As required"),
            new("Fuel pumps", "Off"),
            new("Parking brake and chocks", "As required"),
        ]),
    ];

    public static ChecklistDefinition? Find(string? id) =>
        All.FirstOrDefault(c => c.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ Abgleich mit dem Sim

    /// <summary>Gesagte Menge (Tonnen unter 300, sonst kg) gegen den Sim: mehr als 500 kg bzw. 5 % daneben → Menge an Bord ansagen.</summary>
    private static string? FuelQuantity(ChecklistContext c)
    {
        if (c.State.FuelKg is not { } actual || Number(c.Answer) is not { } said)
        {
            return null;
        }

        var kg = said < 300 ? said * 1000 : said;
        return Math.Abs(kg - actual) > Math.Max(500, actual * 0.05)
            ? $"Fuel on board {Duopilo.Monitoring.FuelCheckText.Amount(actual)}"
            : null;
    }

    /// <summary>"Standard" bzw. QNH aus der Antwort gegen den eingestellten Höhenmesser.</summary>
    private static string? BaroReference(ChecklistContext c)
    {
        if (c.State.BaroHpa is not { } hpa)
        {
            return null;
        }

        var standard = Math.Abs(hpa - 1013.25) < 0.15;
        if (Says(c.Answer, "standard", "std"))
        {
            return standard ? null : $"Baro QNH {Math.Round(hpa).ToString("0", Inv)}";
        }

        if (Number(c.Answer) is { } qnh && qnh is >= 900 and <= 1100)
        {
            return !standard && Math.Abs(qnh - hpa) < 1 ? null : standard ? "Baro standard" : $"Baro QNH {Math.Round(hpa).ToString("0", Inv)}";
        }

        return null;
    }

    /// <summary>Gesagte Klappenstellung ("config 1", "1 plus F", "2") gegen den Sim.</summary>
    private static string? FlapSetting(ChecklistContext c)
    {
        if (FlapsText(c.State) is not { } actual || Number(c.Answer) is not { } said || said is < 0 or > 5)
        {
            return null;
        }

        var lever = actual.ToUpperInvariant() switch
        {
            "1+F" => 1,
            "FULL" => 4,
            _ => int.TryParse(actual, NumberStyles.Integer, Inv, out var n) ? n : -1,
        };

        return lever >= 0 && lever != (int)said ? $"Flaps {Spoken(actual)}" : null;
    }

    /// <summary>A/THR: "speed" erwartet aktiv, "off" erwartet aus; ARMED im Anflug ist immer eine Abweichung.</summary>
    private static string? Autothrust(ChecklistContext c)
    {
        var status = c.State.AutothrustStatus?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(status))
        {
            return null;
        }

        if (status == "ARMED")
        {
            return "Autothrust armed";
        }

        if (Says(c.Answer, "off", "aus") && status != "OFF")
        {
            return "Autothrust active";
        }

        return Says(c.Answer, "speed", "active", "on", "an") && status == "OFF" ? "Autothrust off" : null;
    }

    /// <summary>Gesagte Autobrake-Stufe gegen den Sim (nur wenn die Antwort eine Stufe nennt).</summary>
    private static string? Autobrake(ChecklistContext c)
    {
        if (c.State.AutobrakeMode?.Trim().ToUpperInvariant() is not { Length: > 0 } actual)
        {
            return null;
        }

        string? said = null;
        foreach (var (words, mode) in new[] { (new[] { "low", "lo", "niedrig" }, "LO"), (new[] { "medium", "med", "mittel" }, "MED"), (new[] { "max", "maximum" }, "MAX"), (new[] { "off", "aus", "disarm" }, "OFF") })
        {
            if (Says(c.Answer, words))
            {
                said = mode;
                break;
            }
        }

        if (said is null || said == actual || (said == "OFF" && actual is "DISARM"))
        {
            return null;
        }

        var spoken = actual switch { "LO" or "LOW" => "low", "MED" => "medium", "MAX" => "max", "OFF" or "DISARM" => "off", _ => actual.ToLowerInvariant() };
        return $"Autobrake {spoken}";
    }

    /// <summary>LDG MEMO wie im Airbus: Fahrwerk unten, Schilder an, Ground Spoiler scharf, Landeklappen.</summary>
    private static string? LandingMemo(ChecklistContext c)
    {
        var s = c.State;
        var problems = new List<string>();
        var gearDown = s.GearExtendedPct is { } pct ? pct > 95 : s.GearHandleDown;
        if (gearDown == false)
        {
            problems.Add("gear not down");
        }

        if (s.SeatbeltsOn == false)
        {
            problems.Add("seat belts off");
        }

        if (s.SpoilersArmed == false)
        {
            problems.Add("spoilers not armed");
        }

        if (s.LandingFlaps.Count > 0 && FlapsText(s) is { } flaps && !s.LandingFlaps.Contains(flaps, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("flaps not in landing configuration");
        }

        if (problems.Count == 0)
        {
            return null;
        }

        var text = string.Join(", ", problems);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string? Engines(ChecklistContext c)
    {
        var running = c.State.Engines.Select((e, i) => (e, n: i + 1)).Where(x => x.e.Running == true).Select(x => x.n).ToList();
        return running.Count switch
        {
            0 => null,
            _ when running.Count == c.State.Engines.Count => "Engines running",
            _ => string.Join(", ", running.Select(n => $"Engine {n}")) + " running",
        };
    }

    // ------------------------------------------------------------------ Hilfen

    private static string? FlapsText(VoiceFlightState s) =>
        s.FlapsLabel is { Length: > 0 } label ? label.Trim() : s.FlapsIndex is { } i ? Math.Round(i).ToString("0", Inv) : null;

    private static string Spoken(string flaps) => flaps.ToUpperInvariant() switch
    {
        "FULL" => "full",
        "1+F" => "1",
        _ => flaps,
    };

    /// <summary>Kommt eines der Wörter in der Antwort vor?</summary>
    public static bool Says(string answer, params string[] words) =>
        VoiceText.Words(answer).Any(w => words.Contains(w, StringComparer.Ordinal));

    /// <summary>Erste Zahl in der Antwort ("12.4", "1013"), sonst null.</summary>
    public static double? Number(string answer) =>
        NumberPattern().Match(answer) is { Success: true } m && double.TryParse(m.Value, NumberStyles.Float, Inv, out var v) ? v : null;

    [GeneratedRegex(@"(?<![\p{L}\p{N}.])\d+(?:\.\d+)?(?![\p{N}])")]
    private static partial Regex NumberPattern();
}

/// <summary>
/// Challenge-Response: Der Kopilot liest die Challenge, wartet auf die Antwort des Piloten, gleicht sie – wo möglich – mit dem Sim ab
/// und meldet Abweichungen. Bei einer Abweichung bleibt der Punkt offen: Antwortet der Pilot nach dem Korrigieren erneut, geht es
/// weiter; weicht es beim zweiten Mal noch ab, wird der Punkt als offen vermerkt. "Skip" überspringt (offen), "Say again"
/// wiederholt, "Stop checklist" bricht ab. Ohne Antwort wiederholt der Kopilot die Challenge einmal.
/// Nicht threadsicher – die Sprachschleife ruft alles nacheinander auf.
/// </summary>
public sealed class ChecklistRunner(TimeProvider? time = null)
{
    public const string StandbyReply = "Standby";

    private static readonly HashSet<string> WaitWords = new(StringComparer.Ordinal) { "standby", "wait", "warte", "moment", "stop" };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<string> _open = [];
    private int _index;
    private int _deviations;
    private DateTimeOffset _askedUtc;
    private bool _repeated;
    private string? _lastPrompt;

    /// <summary>Ohne Antwort wird die Challenge nach dieser Zeit einmal wiederholt.</summary>
    public TimeSpan RepeatAfter { get; set; } = TimeSpan.FromSeconds(30);

    public ChecklistDefinition? Active { get; private set; }

    public ChecklistItem? Current
    {
        get
        {
            // Auch aus dem Status-Thread lesbar: Index nur einmal lesen
            var index = _index;
            return Active is { } c && index >= 0 && index < c.Items.Count ? c.Items[index] : null;
        }
    }

    /// <summary>Offene Punkte der laufenden bzw. zuletzt beendeten Checkliste.</summary>
    public IReadOnlyList<string> Open => _open;

    public VoiceReply Start(ChecklistDefinition checklist)
    {
        Active = checklist;
        _index = 0;
        _open.Clear();
        _deviations = 0;
        return checklist.Items.Count == 0 ? Complete() : Ask($"{checklist.Title} checklist, {Lower(checklist.Items[0].Challenge)}");
    }

    /// <summary>Antwort des Piloten auf die aktuelle Challenge.</summary>
    public VoiceReply? Respond(string normalized, VoiceFlightState state)
    {
        if (Current is not { } item)
        {
            return null;
        }

        var words = VoiceText.Words(normalized);
        if (words.Length > 0 && words.All(WaitWords.Contains))
        {
            // "Standby": Punkt bleibt offen, die Wiederholung beginnt neu
            _askedUtc = _time.GetUtcNow();
            _repeated = false;
            return null;
        }

        string? deviation = null;
        try
        {
            deviation = item.Check?.Invoke(new ChecklistContext(state, normalized));
        }
        catch (Exception)
        {
            // Ein Abgleich, der scheitert, hält die Checkliste nicht auf
            deviation = null;
        }

        if (deviation is not null)
        {
            _deviations++;
            if (_deviations < 2)
            {
                return Ask(deviation);
            }

            _open.Add(item.Challenge);
        }

        return Next();
    }

    /// <summary>"Skip": Punkt als offen vermerken und weiter.</summary>
    public VoiceReply? Skip()
    {
        if (Current is not { } item)
        {
            return null;
        }

        _open.Add(item.Challenge);
        return Next();
    }

    /// <summary>"Stop checklist".</summary>
    public VoiceReply? Stop()
    {
        if (Active is not { } c)
        {
            return null;
        }

        Active = null;
        return VoiceReply.Of($"{c.Title} checklist interrupted");
    }

    /// <summary>"Say again": letzte Challenge bzw. Abweichung noch einmal.</summary>
    public VoiceReply? Repeat() => Active is null || _lastPrompt is null ? null : Ask(_lastPrompt);

    /// <summary>Regelmäßig aufrufen: ohne Antwort die Challenge einmal wiederholen.</summary>
    public VoiceReply? Tick()
    {
        if (Active is null || _repeated || _lastPrompt is null || _time.GetUtcNow() - _askedUtc < RepeatAfter)
        {
            return null;
        }

        _repeated = true;
        var prompt = _lastPrompt;
        _askedUtc = _time.GetUtcNow();
        return VoiceReply.Of(prompt);
    }

    /// <summary>Die Wartezeit bis zur Wiederholung um eine eingefrorene Zeit verschieben (Sim lief nicht).</summary>
    public void Shift(TimeSpan frozen)
    {
        if (Active is not null && frozen > TimeSpan.Zero)
        {
            _askedUtc += frozen;
        }
    }

    /// <summary>Die Challenge ist ganz gesprochen – die Wartezeit bis zur Wiederholung beginnt jetzt.</summary>
    public void Spoken(DateTimeOffset endUtc)
    {
        if (Active is not null && endUtc > _askedUtc)
        {
            _askedUtc = endUtc;
        }
    }

    private VoiceReply Next()
    {
        _index++;
        _deviations = 0;
        return Current is { } next ? Ask(next.Challenge) : Complete();
    }

    private VoiceReply Complete()
    {
        var c = Active!;
        Active = null;
        _lastPrompt = null;
        var text = _open.Count == 0
            ? $"{c.Title} checklist complete"
            : $"{c.Title} checklist complete, open: {string.Join(", ", _open.Select(Lower))}";
        return VoiceReply.Of(text);
    }

    private VoiceReply Ask(string prompt)
    {
        _lastPrompt = prompt;
        _askedUtc = _time.GetUtcNow();
        _repeated = false;
        return VoiceReply.Of(prompt);
    }

    /// <summary>Erster Buchstabe klein, außer bei Abkürzungen ("ADIRS", "ECAM status", "V1").</summary>
    private static string Lower(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) && !char.IsDigit(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
