using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kopilot.Reports;

/// <summary>Art einer Meldung: Kennung fürs Dashboard, Label im GitHub-Repository, Anzeigename.</summary>
public sealed record BugReportCategory(string Id, string Label, string Name);

/// <summary>ATC-Stand laut Kopilot im Moment der Meldung (null-Felder = unbekannt).</summary>
/// <param name="FirHere">FIR laut Plan an der aktuellen Streckenposition.</param>
/// <param name="FirHereSinceNm">Streckenposition, ab der <paramref name="FirHere"/> gilt.</param>
public sealed record BugReportAtc(
    string Station,
    string StationType,
    string Facility,
    string Frequency,
    string FrequencySource,
    string Phase,
    string Step,
    string? FirHere,
    double? FirHereSinceNm,
    string? NextFir,
    double? NextFirNm,
    string? SpokenCallsign,
    string? Squawk,
    int? ClearedAltitudeFt);

/// <summary>Momentaufnahme beim Druck auf „Fehler melden“ – Werte so, wie Kopilot sie in dem Moment hatte.</summary>
public sealed record BugReportContext
{
    public required DateTimeOffset CapturedUtc { get; init; }

    public required string Version { get; init; }

    public bool SimConnected { get; init; }

    public bool? Paused { get; init; }

    /// <summary>Sim-Uhrzeit (UTC) als "HH:mm:ss".</summary>
    public string? SimZulu { get; init; }

    public string? Aircraft { get; init; }

    public string? Profile { get; init; }

    public double? Lat { get; init; }

    public double? Lon { get; init; }

    public double? AltitudeFt { get; init; }

    public double? PressureAltitudeFt { get; init; }

    public double? GroundSpeedKt { get; init; }

    public double? HeadingDeg { get; init; }

    public bool? OnGround { get; init; }

    /// <summary>Flugphase laut Monitoring (cruise, approach …).</summary>
    public string? FlightPhase { get; init; }

    public double? Com1ActiveMhz { get; init; }

    public double? Com1StandbyMhz { get; init; }

    public string? Transponder { get; init; }

    public string? Callsign { get; init; }

    public string? Origin { get; init; }

    public string? Destination { get; init; }

    /// <summary>Strecke ab dem Start laut SimBrief-Plan.</summary>
    public double? AlongTrackNm { get; init; }

    public double? TotalNm { get; init; }

    public BugReportAtc? Atc { get; init; }

    /// <summary>Letzte Funksprüche, älteste zuerst (fertig formatiert).</summary>
    public IReadOnlyList<string> Transcript { get; init; } = [];

    /// <summary>Was die Sprachschleife zuletzt gehört hat und wohin es ging.</summary>
    public string? VoiceLast { get; init; }
}

/// <summary>Fertige Meldung: Text für GitHub und die lokale Kopie, Link zum vorausgefüllten Issue.</summary>
/// <param name="Url">Link zum neuen Issue mit Vorlage, null ohne Repository.</param>
/// <param name="Body">Vollständiger Text (Beschreibung und Kontext ungekürzt) – für die Zwischenablage und die lokale Kopie.</param>
/// <param name="Truncated">Für den Link mussten Funksprüche oder Beschreibung gekürzt werden.</param>
public sealed record BugReportIssue(string Title, IReadOnlyList<string> Labels, string? Url, string Body, bool Truncated);

/// <summary>
/// „Fehler melden“ im Dashboard, rein rechnend (testbar): Kontext als Markdown, Titelvorschlag und der Link auf ein neues
/// GitHub-Issue mit der Vorlage .github/ISSUE_TEMPLATE/kopilot-meldung.yml. Der Link füllt die Felder "beschreibung" und
/// "kontext" der Vorlage und setzt die Labels (wirkt direkt nur für Leute mit Triage-Rechten – für alle anderen setzt die
/// Action issue-labels.yml sie anhand der Markierung im Kontext).
/// </summary>
public static partial class BugReport
{
    /// <summary>Länger sollte der Link nicht werden – GitHub lehnt sehr lange URLs ab.</summary>
    public const int MaxUrlLength = 7000;

    /// <summary>So viele Funksprüche kommen höchstens mit.</summary>
    public const int MaxTranscriptLines = 10;

    /// <summary>Markierung im Kontext, aus der die Action die Labels liest.</summary>
    public const string LabelMarker = "kopilot-meldung labels:";

    public static readonly IReadOnlyList<BugReportCategory> Categories =
    [
        new("fir", "data/fir", "FIR-Grenze / Station"),
        new("frequency", "data/frequency", "Frequenz"),
        new("airline", "data/airline", "Airline / Rufzeichen"),
        new("bug", "bug", "Programmfehler"),
    ];

    private const string Nl = "\n";

    /// <summary>Bekannte Kategorien in fester Reihenfolge, Unbekanntes fällt weg.</summary>
    public static IReadOnlyList<BugReportCategory> Resolve(IEnumerable<string>? ids)
    {
        var set = new HashSet<string>(ids ?? [], StringComparer.OrdinalIgnoreCase);
        return Categories.Where(c => set.Contains(c.Id)).ToList();
    }

    /// <summary>Titelvorschlag je nach Art, z. B. "[Frequenz] Montreal Center 132.600 bei 53.51 N 64.65 W".</summary>
    public static string SuggestTitle(IReadOnlyList<BugReportCategory> categories, BugReportContext c)
    {
        var prefix = categories.Count == 0 ? "[Meldung]" : "[" + string.Join(", ", categories.Select(k => k.Name)) + "]";
        var where = c.Lat is { } lat && c.Lon is { } lon ? " bei " + ShortPosition(lat, lon) : "";
        var first = categories.FirstOrDefault()?.Id;
        var subject = first switch
        {
            "frequency" when c.Atc is { } a => $"{a.Station} {a.Frequency}",
            "fir" when c.Atc is { } a => a.FirHere is { Length: > 0 } fir && !a.Station.Contains(fir, StringComparison.OrdinalIgnoreCase) ? $"{a.Station} ({fir})" : a.Station,
            "airline" => c.Atc?.SpokenCallsign is { Length: > 0 } spoken ? $"{c.Callsign} „{spoken}“" : c.Callsign ?? "Rufzeichen",
            "bug" => "",
            _ => c.Atc?.Station ?? "",
        };

        if (first is "airline" or "bug")
        {
            where = "";
        }

        return $"{prefix} {subject}{where}".Trim();
    }

    /// <summary>Kontext als Markdown (Liste plus Funksprüche im Codeblock).</summary>
    /// <param name="labels">Labels für die Markierung am Anfang (unsichtbar im Issue). Leer = keine Markierung.</param>
    public static string ContextMarkdown(BugReportContext c, int transcriptLines = MaxTranscriptLines, IReadOnlyList<string>? labels = null)
    {
        var s = new StringBuilder();
        if (labels is { Count: > 0 })
        {
            s.Append("<!-- ").Append(LabelMarker).Append(' ').Append(string.Join(",", labels)).Append(" -->").Append(Nl);
        }

        var time = c.CapturedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        Line(s, "Zeit", time + (c.SimZulu is { } z ? $" (Sim {z}Z)" : "") + (c.Paused == true ? ", Sim pausiert" : ""));
        Line(s, "Version", "Kopilot " + c.Version);
        if (!c.SimConnected)
        {
            Line(s, "Sim", "nicht verbunden");
        }

        if (c.Lat is { } lat && c.Lon is { } lon)
        {
            Line(s, "Position", $"{Coordinate(lat, 'N', 'S')}, {Coordinate(lon, 'E', 'W')} ([Karte]({MapLink(lat, lon)}))");
        }

        var height = new List<string>();
        if (c.PressureAltitudeFt is { } pa && c.OnGround != true)
        {
            height.Add($"FL{Math.Max(0, Math.Round(pa / 100)):000}");
        }

        if (c.AltitudeFt is { } alt)
        {
            height.Add($"{Math.Round(alt):0} ft");
        }

        if (c.OnGround == true)
        {
            height.Add("am Boden");
        }

        if (c.GroundSpeedKt is { } gs)
        {
            height.Add($"GS {Math.Round(gs):0} kt");
        }

        if (c.HeadingDeg is { } hdg)
        {
            height.Add($"Kurs {((Math.Round(hdg) % 360) + 360) % 360:000}°");
        }

        if (height.Count > 0)
        {
            Line(s, "Höhe", string.Join(" · ", height));
        }

        if (c.Atc is { } a)
        {
            var source = a.FrequencySource switch
            {
                "facility" => "aus MSFS",
                "sectors" => "aus VATSIM-Sektordaten",
                "templates" => "aus atc-templates.json",
                "placeholder" => "Platzhalter",
                _ => a.FrequencySource,
            };
            Line(s, "ATC laut Kopilot", $"{a.Station} ({Join(a.Facility, a.StationType)}) · {a.Frequency} ({source}) · Phase {a.Phase}/{a.Step}");

            var fir = new List<string>();
            if (a.FirHere is { Length: > 0 } here)
            {
                fir.Add(a.FirHereSinceNm is { } since ? $"{here} ab Meile {since:0}" : here);
            }

            if (a.NextFir is { Length: > 0 } next)
            {
                fir.Add(a.NextFirNm is { } at ? $"nächste {next} bei Meile {at:0}" : $"nächste {next}");
            }

            if (fir.Count > 0)
            {
                Line(s, "FIR laut Plan", string.Join(" · ", fir));
            }
        }
        else
        {
            Line(s, "ATC laut Kopilot", "keine Sitzung");
        }

        if (c.AlongTrackNm is { } along)
        {
            Line(s, "Strecke", c.TotalNm is { } total ? $"Meile {along:0} von {total:0}" : $"Meile {along:0}");
        }

        if (c.Com1ActiveMhz is { } com1)
        {
            Line(s, "COM1", $"{com1.ToString("0.000", CultureInfo.InvariantCulture)} aktiv" +
                            (c.Com1StandbyMhz is { } sby ? $" · {sby.ToString("0.000", CultureInfo.InvariantCulture)} standby" : ""));
        }

        if (c.Transponder is not null || c.Atc?.Squawk is not null)
        {
            Line(s, "Squawk", Join(c.Transponder is { } t ? $"Transponder {t}" : null, c.Atc?.Squawk is { } sq ? $"Kopilot vergibt {sq}" : null, " · "));
        }

        if (c.Callsign is not null || c.Origin is not null)
        {
            var flight = $"{c.Callsign} {c.Origin}–{c.Destination}".Trim();
            if (c.Atc?.SpokenCallsign is { Length: > 0 } spoken)
            {
                flight += $", gesprochen „{spoken}“";
            }

            if (c.Atc?.ClearedAltitudeFt is { } cleared)
            {
                flight += $", freigegeben {cleared} ft";
            }

            Line(s, "Flug", flight);
        }

        if (c.Aircraft is not null)
        {
            Line(s, "Flugzeug", c.Aircraft + (c.Profile is { } p ? $" (Profil {p})" : " (ohne Profil)"));
        }

        if (c.FlightPhase is { } phase)
        {
            Line(s, "Phase (Monitoring)", phase);
        }

        if (c.VoiceLast is { } voice)
        {
            Line(s, "Sprachschleife zuletzt", voice);
        }

        var lines = c.Transcript.TakeLast(Math.Max(0, transcriptLines)).ToList();
        if (lines.Count > 0)
        {
            s.Append(Nl).Append("Letzte Funksprüche:").Append(Nl).Append(Nl).Append("```text").Append(Nl);
            foreach (var line in lines)
            {
                // Backticks würden den Codeblock beenden
                s.Append(line.Replace("```", "'''", StringComparison.Ordinal)).Append(Nl);
            }

            s.Append("```").Append(Nl);
            if (lines.Count < c.Transcript.Count)
            {
                s.Append($"({c.Transcript.Count - lines.Count} ältere Funksprüche in der lokalen Kopie)").Append(Nl);
            }
        }

        return s.ToString();
    }

    /// <summary>Meldung fertigstellen: Titel, Labels, vollständiger Text und – mit Repository – der Link zum Issue.</summary>
    /// <param name="repository">"Besitzer/Name" oder leer.</param>
    /// <param name="template">Dateiname der Issue-Vorlage.</param>
    public static BugReportIssue Build(
        string? repository,
        string template,
        IReadOnlyList<BugReportCategory> categories,
        string? title,
        string? description,
        BugReportContext context)
    {
        var labels = categories.Select(c => c.Label).ToList();
        title = Clean(title);
        if (title.Length == 0)
        {
            title = SuggestTitle(categories, context);
        }

        if (title.Length > 200)
        {
            title = title[..200];
        }

        description = (description ?? "").Trim();
        var body = BodyText(description, ContextMarkdown(context, int.MaxValue, labels));

        if (!IsRepository(repository))
        {
            return new BugReportIssue(title, labels, null, body, false);
        }

        // Für den Link erst Funksprüche weglassen, dann die Beschreibung kürzen
        for (var lines = Math.Min(MaxTranscriptLines, context.Transcript.Count); ; lines--)
        {
            var url = Url(repository!, template, title, labels, description, ContextMarkdown(context, lines, labels));
            if (url.Length <= MaxUrlLength)
            {
                return new BugReportIssue(title, labels, url, body, lines < context.Transcript.Count);
            }

            if (lines == 0)
            {
                break;
            }
        }

        // Selbst ohne Funksprüche zu lang: Beschreibung kürzen (vollständig steht sie in der lokalen Kopie)
        var contextText = ContextMarkdown(context, 0, labels);
        var low = 0;
        var high = description.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Url(repository!, template, title, labels, Shorten(description, mid), contextText).Length <= MaxUrlLength)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return new BugReportIssue(title, labels, Url(repository!, template, title, labels, Shorten(description, low), contextText), body, true);
    }

    /// <summary>Text des Issues wie ihn die Vorlage zusammensetzt – für die Zwischenablage und die lokale Kopie.</summary>
    public static string BodyText(string description, string context) =>
        "### Was ist falsch, was wäre richtig?" + Nl + Nl + (description.Length > 0 ? description : "_keine Beschreibung_") + Nl + Nl +
        "### Kontext aus Kopilot" + Nl + Nl + context;

    /// <summary>"Besitzer/Name" wie bei GitHub erlaubt.</summary>
    public static bool IsRepository(string? repository) => repository is not null && RepositoryPattern().IsMatch(repository);

    private static string Url(string repository, string template, string title, IReadOnlyList<string> labels, string description, string context)
    {
        var s = new StringBuilder("https://github.com/").Append(repository).Append("/issues/new?template=").Append(Uri.EscapeDataString(template))
            .Append("&title=").Append(Uri.EscapeDataString(title));
        if (labels.Count > 0)
        {
            s.Append("&labels=").Append(Uri.EscapeDataString(string.Join(",", labels)));
        }

        if (description.Length > 0)
        {
            s.Append("&beschreibung=").Append(Uri.EscapeDataString(description));
        }

        return s.Append("&kontext=").Append(Uri.EscapeDataString(context)).ToString();
    }

    private static string Shorten(string text, int length) =>
        length >= text.Length ? text : text[..length].TrimEnd() + " … (gekürzt, vollständig in der lokalen Kopie)";

    private static void Line(StringBuilder s, string name, string value) => s.Append("- **").Append(name).Append(":** ").Append(value).Append(Nl);

    private static string Join(string? a, string? b, string separator = ", ") =>
        string.Join(separator, new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string Clean(string? text) => Regex.Replace((text ?? "").Trim(), @"\s+", " ");

    private static string Coordinate(double value, char positive, char negative) =>
        Math.Abs(value).ToString("0.0000", CultureInfo.InvariantCulture) + " " + (value >= 0 ? positive : negative);

    private static string ShortPosition(double lat, double lon) =>
        $"{Math.Abs(lat).ToString("0.00", CultureInfo.InvariantCulture)} {(lat >= 0 ? 'N' : 'S')} " +
        $"{Math.Abs(lon).ToString("0.00", CultureInfo.InvariantCulture)} {(lon >= 0 ? 'E' : 'W')}";

    private static string MapLink(double lat, double lon)
    {
        var la = lat.ToString("0.0000", CultureInfo.InvariantCulture);
        var lo = lon.ToString("0.0000", CultureInfo.InvariantCulture);
        return $"https://www.openstreetmap.org/?mlat={la}&mlon={lo}#map=7/{la}/{lo}";
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();
}
