using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kopilot.Localization;

namespace Kopilot.Reports;

/// <summary>Art einer Meldung: Kennung fürs Dashboard, Label im GitHub-Repository, Anzeigename (in der eingestellten Sprache).</summary>
public sealed record BugReportCategory(string Id, string Label)
{
    public string Name => Id switch
    {
        "fir" => Loc.T("report.category.fir"),
        "frequency" => Loc.T("report.category.frequency"),
        "airline" => Loc.T("report.category.airline"),
        "bug" => Loc.T("report.category.bug"),
        _ => Id,
    };
}

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
        new("fir", "data/fir"),
        new("frequency", "data/frequency"),
        new("airline", "data/airline"),
        new("bug", "bug"),
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
        var prefix = "[" + (categories.Count == 0 ? Loc.T("report.title.generic") : string.Join(", ", categories.Select(k => k.Name))) + "]";
        var where = c.Lat is { } lat && c.Lon is { } lon ? " " + Loc.T("report.title.position", ShortPosition(lat, lon)) : "";
        var first = categories.FirstOrDefault()?.Id;
        var subject = first switch
        {
            "frequency" when c.Atc is { } a => $"{a.Station} {a.Frequency}",
            "fir" when c.Atc is { } a => a.FirHere is { Length: > 0 } fir && !a.Station.Contains(fir, StringComparison.OrdinalIgnoreCase) ? $"{a.Station} ({fir})" : a.Station,
            "airline" => c.Atc?.SpokenCallsign is { Length: > 0 } spoken
                ? Loc.T("report.title.spokenCallsign", c.Callsign, spoken)
                : c.Callsign ?? Loc.T("report.title.callsign"),
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
        var simTime = c.SimZulu is { } z ? " " + Loc.T("report.context.simTime", z) : "";
        var paused = c.Paused == true ? ", " + Loc.T("report.context.paused") : "";
        Line(s, Loc.T("report.context.time"), time + simTime + paused);
        Line(s, Loc.T("report.context.version"), "Kopilot " + c.Version);
        if (!c.SimConnected)
        {
            Line(s, Loc.T("report.context.sim"), Loc.T("report.context.notConnected"));
        }

        if (c.Lat is { } lat && c.Lon is { } lon)
        {
            var map = Loc.T("report.context.map", MapLink(lat, lon));
            Line(s, Loc.T("report.context.position"), $"{Coordinate(lat, 'N', 'S')}, {Coordinate(lon, 'E', 'W')} {map}");
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
            height.Add(Loc.T("report.context.onGround"));
        }

        if (c.GroundSpeedKt is { } gs)
        {
            height.Add($"GS {Math.Round(gs):0} kt");
        }

        if (c.HeadingDeg is { } hdg)
        {
            height.Add(Loc.T("report.context.heading", ((Math.Round(hdg) % 360) + 360) % 360));
        }

        if (height.Count > 0)
        {
            Line(s, Loc.T("report.context.altitude"), string.Join(" · ", height));
        }

        if (c.Atc is { } a)
        {
            var source = a.FrequencySource switch
            {
                "facility" => Loc.T("report.source.facility"),
                "sectors" => Loc.T("report.source.sectors"),
                "templates" => Loc.T("report.source.templates"),
                "placeholder" => Loc.T("report.source.placeholder"),
                _ => a.FrequencySource,
            };
            Line(s, Loc.T("report.context.atc"), Loc.T("report.context.atcValue", a.Station, Join(a.Facility, a.StationType), a.Frequency, source, a.Phase, a.Step));

            var fir = new List<string>();
            if (a.FirHere is { Length: > 0 } here)
            {
                fir.Add(a.FirHereSinceNm is { } since ? Loc.T("report.context.firFrom", here, since) : here);
            }

            if (a.NextFir is { Length: > 0 } next)
            {
                fir.Add(a.NextFirNm is { } at ? Loc.T("report.context.nextFirAt", next, at) : Loc.T("report.context.nextFir", next));
            }

            if (fir.Count > 0)
            {
                Line(s, Loc.T("report.context.fir"), string.Join(" · ", fir));
            }
        }
        else
        {
            Line(s, Loc.T("report.context.atc"), Loc.T("report.context.noSession"));
        }

        if (c.AlongTrackNm is { } along)
        {
            Line(s, Loc.T("report.context.route"), c.TotalNm is { } total ? Loc.T("report.context.mileOf", along, total) : Loc.T("report.context.mile", along));
        }

        if (c.Com1ActiveMhz is { } com1)
        {
            Line(s, "COM1", Loc.T("report.context.com1Active", com1.ToString("0.000", CultureInfo.InvariantCulture)) +
                            (c.Com1StandbyMhz is { } sby ? " · " + Loc.T("report.context.com1Standby", sby.ToString("0.000", CultureInfo.InvariantCulture)) : ""));
        }

        if (c.Transponder is not null || c.Atc?.Squawk is not null)
        {
            Line(s, "Squawk", Join(
                c.Transponder is { } t ? Loc.T("report.context.transponder", t) : null,
                c.Atc?.Squawk is { } sq ? Loc.T("report.context.assignedSquawk", sq) : null,
                " · "));
        }

        if (c.Callsign is not null || c.Origin is not null)
        {
            var flight = $"{c.Callsign} {c.Origin}–{c.Destination}".Trim();
            if (c.Atc?.SpokenCallsign is { Length: > 0 } spoken)
            {
                flight += ", " + Loc.T("report.context.spoken", spoken);
            }

            if (c.Atc?.ClearedAltitudeFt is { } cleared)
            {
                flight += ", " + Loc.T("report.context.cleared", cleared);
            }

            Line(s, Loc.T("report.context.flight"), flight);
        }

        if (c.Aircraft is not null)
        {
            Line(s, Loc.T("report.context.aircraft"), c.Aircraft + " " + (c.Profile is { } p ? Loc.T("report.context.profile", p) : Loc.T("report.context.noProfile")));
        }

        if (c.FlightPhase is { } phase)
        {
            Line(s, Loc.T("report.context.phase"), phase);
        }

        if (c.VoiceLast is { } voice)
        {
            Line(s, Loc.T("report.context.voice"), voice);
        }

        var lines = c.Transcript.TakeLast(Math.Max(0, transcriptLines)).ToList();
        if (lines.Count > 0)
        {
            s.Append(Nl).Append(Loc.T("report.context.transcript")).Append(Nl).Append(Nl).Append("```text").Append(Nl);
            foreach (var line in lines)
            {
                // Backticks würden den Codeblock beenden
                s.Append(line.Replace("```", "'''", StringComparison.Ordinal)).Append(Nl);
            }

            s.Append("```").Append(Nl);
            if (lines.Count < c.Transcript.Count)
            {
                s.Append(Loc.T("report.context.olderCalls", c.Transcript.Count - lines.Count)).Append(Nl);
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
        "### " + Loc.T("report.body.description") + Nl + Nl + (description.Length > 0 ? description : Loc.T("report.body.noDescription")) + Nl + Nl +
        "### " + Loc.T("report.body.context") + Nl + Nl + context;

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
        length >= text.Length ? text : text[..length].TrimEnd() + " " + Loc.T("report.body.shortened");

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
