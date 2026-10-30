using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kopilot.FlightPlan;
using Kopilot.Voice;

namespace Kopilot.Smalltalk;

/// <summary>
/// Lesender Kontext für das Sprachmodell: Flugphase, Position, Route, Sprit, Wetter. Nur Werte – nichts davon kann etwas auslösen.
/// </summary>
public sealed record SmalltalkContext
{
    public static SmalltalkContext Empty { get; } = new();

    public bool SimConnected { get; init; }

    public bool Paused { get; init; }

    /// <summary>Flugphase des Monitorings (ground, takeoff, climb, cruise, descent, approach, rollout …).</summary>
    public string? Phase { get; init; }

    public bool? OnGround { get; init; }

    public string? Aircraft { get; init; }

    public string? Callsign { get; init; }

    public double? Lat { get; init; }

    public double? Lon { get; init; }

    public double? AltitudeFt { get; init; }

    public double? IasKt { get; init; }

    public double? GroundSpeedKt { get; init; }

    public double? HeadingDeg { get; init; }

    public string? Origin { get; init; }

    public string? Destination { get; init; }

    public string? Alternate { get; init; }

    public string? Route { get; init; }

    public string? NextWaypoint { get; init; }

    public double? NextWaypointNm { get; init; }

    /// <summary>ETA am Ziel (Sekunden seit Mitternacht UTC).</summary>
    public double? EtaZuluSec { get; init; }

    public double? TopOfDescentNm { get; init; }

    /// <summary>Sim-Zeit (Sekunden seit Mitternacht UTC).</summary>
    public double? ZuluSec { get; init; }

    public double? FuelKg { get; init; }

    public double? FuelVsPlanKg { get; init; }

    public double? PredictedLandingFuelKg { get; init; }

    public double? WindDirectionDeg { get; init; }

    public double? WindSpeedKt { get; init; }

    public double? OutsideTempC { get; init; }

    public string? OriginMetar { get; init; }

    public string? DestinationMetar { get; init; }

    public string? AtcStation { get; init; }

    /// <summary>Kontext aus dem Zustand der Abfragen, dem Monitoring und dem SimBrief-Plan.</summary>
    public static SmalltalkContext From(VoiceFlightState state, string? phase, Ofp? ofp, double? lat, double? lon, string? aircraft, double? zuluSec)
    {
        static string? Airport(OfpAirport? a) => a?.Icao is null ? null : a.Name is { Length: > 0 } name ? $"{a.Icao} ({name})" : a.Icao;

        var next = state.NextWaypoint;
        return new SmalltalkContext
        {
            SimConnected = state.Connected,
            Paused = state.Paused,
            Phase = phase,
            OnGround = state.OnGround,
            Aircraft = aircraft,
            Callsign = ofp?.Callsign,
            Lat = lat,
            Lon = lon,
            AltitudeFt = state.AltitudeFt,
            IasKt = state.IasKt,
            GroundSpeedKt = state.GroundSpeedKt,
            HeadingDeg = state.HeadingMagDeg,
            Origin = Airport(ofp?.Origin),
            Destination = Airport(ofp?.Destination),
            Alternate = Airport(ofp?.Alternate),
            Route = ofp?.Route,
            NextWaypoint = next is null ? null : next.Name is { Length: > 0 } n ? $"{next.Ident} ({n})" : next.Ident,
            NextWaypointNm = next?.DistanceNm,
            EtaZuluSec = state.DestinationEtaZuluSec,
            TopOfDescentNm = state.TodDistanceNm,
            ZuluSec = zuluSec,
            FuelKg = state.FuelKg,
            FuelVsPlanKg = state.FuelPlan?.DiffKg,
            PredictedLandingFuelKg = state.FuelPlan?.PredictedLandingKg,
            WindDirectionDeg = state.WindDirectionDeg,
            WindSpeedKt = state.WindSpeedKt,
            OutsideTempC = state.OutsideTempC,
            OriginMetar = ofp?.Origin.Metar,
            DestinationMetar = ofp?.Destination.Metar,
            AtcStation = state.Station is { } s ? s.FrequencyMhz is { } f ? $"{s.Name} {f.ToString("0.000", CultureInfo.InvariantCulture)}" : s.Name : null,
        };
    }

    /// <summary>Kontext als Zeilen "Schlüssel: Wert" (englisch, invariante Zahlen), nur bekannte Werte.</summary>
    public IReadOnlyList<string> Lines()
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add($"{key}: {value.Trim()}");
            }
        }

        static string? Num(double? v, string format, string unit) => v is { } x && double.IsFinite(x) ? x.ToString(format, CultureInfo.InvariantCulture) + unit : null;
        static string? Clock(double? sec) => sec is { } s && double.IsFinite(s)
            ? TimeSpan.FromSeconds(((s % 86400) + 86400) % 86400).ToString(@"hh\:mm", CultureInfo.InvariantCulture) + "Z"
            : null;

        if (!SimConnected)
        {
            lines.Add("simulator: not connected");
        }
        else if (Paused)
        {
            lines.Add("simulator: paused");
        }

        Add("aircraft", Aircraft);
        Add("callsign", Callsign);
        Add("flight phase", Phase);
        Add("on ground", OnGround is { } g ? (g ? "yes" : "no") : null);
        Add("position", Lat is { } la && Lon is { } lo ? $"{la.ToString("0.00", inv)}, {lo.ToString("0.00", inv)}" : null);
        Add("altitude", Num(AltitudeFt, "0", " ft"));
        Add("indicated airspeed", Num(IasKt, "0", " kt"));
        Add("ground speed", Num(GroundSpeedKt, "0", " kt"));
        Add("heading", Num(HeadingDeg, "000", "°"));
        Add("origin", Origin);
        Add("destination", Destination);
        Add("alternate", Alternate);
        Add("route", Route is { Length: > 300 } r ? r[..300] + " …" : Route);
        Add("next waypoint", NextWaypoint is null ? null : NextWaypoint + (Num(NextWaypointNm, "0", " NM") is { } d ? ", " + d : ""));
        Add("top of descent in", Num(TopOfDescentNm, "0", " NM"));
        Add("ETA destination", Clock(EtaZuluSec));
        Add("time now", Clock(ZuluSec));
        Add("fuel on board", Num(FuelKg, "0", " kg"));
        Add("fuel versus plan", FuelVsPlanKg is { } diff && double.IsFinite(diff) ? (diff >= 0 ? "+" : "") + diff.ToString("0", inv) + " kg" : null);
        Add("predicted landing fuel", Num(PredictedLandingFuelKg, "0", " kg"));
        Add("wind", WindDirectionDeg is { } wd && WindSpeedKt is { } ws ? $"{wd.ToString("000", inv)}° / {ws.ToString("0", inv)} kt" : null);
        Add("outside air temperature", Num(OutsideTempC, "0", " °C"));
        Add("origin weather (METAR)", OriginMetar);
        Add("destination weather (METAR)", DestinationMetar);
        Add("ATC station", AtcStation);
        return lines;
    }
}

/// <summary>Systemtext für das Sprachmodell und Aufbereitung der Antwort für die Sprachausgabe.</summary>
public static partial class SmalltalkPrompt
{
    /// <summary>
    /// Rolle (First Officer, kollegial, kurz), Sprache, Grenzen (keine Aktionen, keine Freigaben, nur lesender Kontext) und der Kontext.
    /// </summary>
    public static string System(string? name, string languageName, int maxWords, SmalltalkContext context)
    {
        var sb = new StringBuilder();
        var who = string.IsNullOrWhiteSpace(name) ? "the first officer" : $"{name.Trim()}, the first officer";
        sb.Append("You are ").Append(who)
          .Append(" (pilot monitoring) on an airliner in Microsoft Flight Simulator, chatting with the captain in the cockpit. ")
          .Append("Talk like a friendly, professional colleague: natural spoken language, short. ")
          .Append("Answer in ").Append(languageName).Append(", in at most ").Append(maxWords.ToString(CultureInfo.InvariantCulture))
          .Append(" words, one to three sentences. No lists, no markdown, no emojis – your answer is read aloud. Speeds in knots, never Mach.")
          .AppendLine();
        sb.Append("This chat is read-only. You cannot operate anything, set values, run checklists, acknowledge warnings or talk to ATC, ")
          .Append("and nothing you say is executed. If the captain wants an action, tell them to give the command directly (for example \"Gear up\"). ")
          .Append("Never ask the captain to confirm an action, never read back clearances and never invent instructions or clearances. ")
          .Append("For facts about this flight use only the data below; if something is not there, say you don't know.")
          .AppendLine();
        sb.AppendLine().AppendLine("Current flight data (read-only):");
        foreach (var line in context.Lines())
        {
            sb.Append("- ").AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Antwort sprechbar machen: Denkblöcke (&lt;think&gt;) und Markdown weg, eine Zeile, höchstens <paramref name="maxWords"/> Wörter
    /// (am Satzende gekürzt, wenn möglich).
    /// </summary>
    public static string Clean(string? raw, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var text = ThinkBlock().Replace(raw, " ");
        text = Emphasis().Replace(text, "");
        text = Markdown().Replace(text, " ");
        text = ListMarker().Replace(text, " ");
        text = Spaces().Replace(text, " ").Trim();
        text = SpaceBeforePunctuation().Replace(text, "$1");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (maxWords <= 0 || words.Length <= maxWords)
        {
            return text;
        }

        var cut = string.Join(' ', words.Take(maxWords));
        var end = Math.Max(cut.LastIndexOf(". ", StringComparison.Ordinal), Math.Max(cut.LastIndexOf("! ", StringComparison.Ordinal), cut.LastIndexOf("? ", StringComparison.Ordinal)));
        if (cut.Length > 0 && cut[^1] is '.' or '!' or '?')
        {
            return cut;
        }

        return end > cut.Length / 3 ? cut[..(end + 1)] : cut.TrimEnd(',', ';', ':', '-', '–') + " …";
    }

    [GeneratedRegex(@"<think>.*?</think>|<think>.*$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"[*_`]+")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"[#>|]+")]
    private static partial Regex Markdown();

    [GeneratedRegex(@"\s+([.,!?;:])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"(?m)^\s*(?:[-•]|\d+[.)])\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
