using System.Net;
using System.Text;
using System.Text.Json;
using Duopilo.Reports;

namespace Duopilo.Tests;

/// <summary>„Fehler melden“: Kontext, Titel, Labels und Link zum vorausgefüllten GitHub-Issue.</summary>
public sealed class BugReportTests : IAsyncLifetime
{
    private const string Repository = "duopilo/duopiloapp";
    private const string Template = "duopilo-meldung.yml";

    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"duopilo-test-{Guid.NewGuid():N}.json");
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "duopilo-report-tests", Guid.NewGuid().ToString("N"));
    private TestApp _app = null!;

    /// <summary>Wie bei HOIST im Testlog 0.9: Duopilo schon bei Montreal (Platzhalterfrequenz), Sim-ATC noch bei Gander.</summary>
    private static BugReportContext Hoist(int transcriptLines = 3) => new()
    {
        CapturedUtc = new DateTimeOffset(2026, 10, 29, 21, 18, 7, TimeSpan.Zero),
        Version = "0.9.1",
        SimConnected = true,
        SimZulu = "21:18:05",
        Aircraft = "A330-900neo Condor",
        Profile = "headwind-a339",
        Lat = 53.51,
        Lon = -64.65,
        AltitudeFt = 30630,
        PressureAltitudeFt = 32000,
        GroundSpeedKt = 480,
        HeadingDeg = 268,
        OnGround = false,
        FlightPhase = "cruise",
        Com1ActiveMhz = 124.725,
        Com1StandbyMhz = 133.3,
        Transponder = "3172",
        Callsign = "CFG2205",
        Origin = "EDDF",
        Destination = "KLAX",
        AlongTrackNm = 2390,
        TotalNm = 5312,
        Atc = new BugReportAtc("Montreal Center", "Center", "CZUL", "132.600", "placeholder", "Center", "CenterContact",
            "CZUL", 2377, "CZYZ", 3010, "Condor two two zero five", "2340", 32000),
        Transcript = Enumerable.Range(1, transcriptLines)
            .Select(i => $"21:{10 + i:00}:00Z Pilot Gander Oceanic 127.650: Gander, Condor 2205, position report {i}")
            .ToList(),
        VoiceLast = "„Montreal Center, Condor 2205“ → atc",
    };

    public async Task InitializeAsync() =>
        _app = await TestApp.StartAsync(_stateFile, new Dictionary<string, string?> { ["Duopilo:DataDirectory"] = _dataDirectory });

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        foreach (var path in new[] { _stateFile })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Kontext_enthaelt_alles_fuer_eine_Datenkorrektur()
    {
        var text = BugReport.ContextMarkdown(Hoist(), labels: ["data/fir", "data/frequency"]);

        Assert.StartsWith("<!-- duopilo-meldung labels: data/fir,data/frequency -->", text);
        Assert.Contains("2026-10-29 21:18:07Z (sim 21:18:05Z)", text);
        Assert.Contains("Duopilo 0.9.1", text);
        Assert.Contains("53.5100 N, 64.6500 W", text);
        Assert.Contains("openstreetmap.org/?mlat=53.5100&mlon=-64.6500", text);
        Assert.Contains("FL320 · 30630 ft · GS 480 kt · HDG 268°", text);
        Assert.Contains("Montreal Center (CZUL, Center) · 132.600 (placeholder) · phase Center/CenterContact", text);
        Assert.Contains("CZUL from mile 2377 · next CZYZ at mile 3010", text);
        Assert.Contains("Mile 2390 of 5312", text);
        Assert.Contains("124.725 active · 133.300 standby", text);
        Assert.Contains("Transponder 3172 · Duopilo assigned 2340", text);
        Assert.Contains("CFG2205 EDDF–KLAX, spoken “Condor two two zero five”", text);
        Assert.Contains("```text\n21:11:00Z Pilot Gander Oceanic 127.650: Gander, Condor 2205, position report 1\n", text);
        Assert.Contains("Voice loop, last heard", text);
    }

    [Fact]
    public void Ohne_Sim_und_ohne_ATC_bleibt_der_Kontext_kurz()
    {
        var text = BugReport.ContextMarkdown(new BugReportContext { CapturedUtc = DateTimeOffset.UnixEpoch, Version = "0.9.1" });

        Assert.Contains("**Sim:** not connected", text);
        Assert.Contains("**ATC per Duopilo:** no session", text);
        Assert.DoesNotContain("Position", text);
        Assert.DoesNotContain("```", text);
    }

    [Fact]
    public void Backticks_im_Funk_beenden_den_Codeblock_nicht()
    {
        var context = Hoist() with { Transcript = ["21:00:00Z ATC  X: ```bösartig```"] };

        var text = BugReport.ContextMarkdown(context);

        Assert.Equal(2, text.Split("```").Length - 1);
    }

    [Theory]
    [InlineData("frequency", "[Frequency] Montreal Center 132.600 at 53.51 N 64.65 W")]
    [InlineData("fir", "[FIR boundary / station] Montreal Center (CZUL) at 53.51 N 64.65 W")]
    [InlineData("airline", "[Airline / callsign] CFG2205 “Condor two two zero five”")]
    [InlineData("bug", "[Bug]")]
    public void Titelvorschlag_je_Art(string category, string expected)
    {
        Assert.Equal(expected, BugReport.SuggestTitle(BugReport.Resolve([category]), Hoist()));
    }

    [Fact]
    public void Unbekannte_Arten_fallen_weg_Reihenfolge_ist_fest()
    {
        var categories = BugReport.Resolve(["bug", "evil;rm", "FIR", "frequency"]);

        Assert.Equal(new[] { "data/fir", "data/frequency", "bug" }, categories.Select(c => c.Label).ToArray());
    }

    [Fact]
    public void Link_fuellt_die_Vorlage_und_setzt_die_Labels()
    {
        var issue = BugReport.Build(Repository, Template, BugReport.Resolve(["frequency", "fir"]), "", "Sim-ATC übergibt erst bei 64.65 W an Montreal 132.25", Hoist());

        Assert.NotNull(issue.Url);
        var url = issue.Url!;
        Assert.StartsWith("https://github.com/duopilo/duopiloapp/issues/new?template=duopilo-meldung.yml&title=", url);
        Assert.Contains("&labels=data%2Ffir%2Cdata%2Ffrequency", url);
        Assert.Contains("&beschreibung=" + Uri.EscapeDataString("Sim-ATC übergibt erst bei 64.65 W an Montreal 132.25"), url);
        Assert.Contains("&kontext=" + Uri.EscapeDataString("<!-- duopilo-meldung labels: data/fir,data/frequency -->"), url);
        Assert.False(issue.Truncated);
        Assert.Equal("[FIR boundary / station, Frequency] Montreal Center (CZUL) at 53.51 N 64.65 W", issue.Title);
        Assert.Contains("### What is wrong, and what would be right?", issue.Body);
        Assert.Contains("### Context from Duopilo", issue.Body);
    }

    [Fact]
    public void Zu_langer_Link_wird_gekuerzt_die_lokale_Kopie_nicht()
    {
        var description = string.Join(" ", Enumerable.Repeat("Übergabe viel zu früh, Frequenz falsch.", 400));
        var issue = BugReport.Build(Repository, Template, BugReport.Resolve(["bug"]), "Titel", description, Hoist(transcriptLines: 30));

        Assert.True(issue.Truncated);
        Assert.True(issue.Url!.Length <= BugReport.MaxUrlLength, $"{issue.Url.Length} Zeichen");
        Assert.Contains(Uri.EscapeDataString("(shortened, full text in the local copy)"), issue.Url);
        Assert.Contains(description, issue.Body);
        Assert.Contains("position report 30", issue.Body);
        Assert.Contains("position report 1\n", issue.Body);
    }

    [Fact]
    public void Viele_Funksprueche_nur_die_letzten_zehn_im_Link()
    {
        var issue = BugReport.Build(Repository, Template, BugReport.Resolve(["fir"]), "x", "kurz", Hoist(transcriptLines: 14));

        Assert.True(issue.Truncated);
        Assert.Contains(Uri.EscapeDataString("position report 14"), issue.Url);
        Assert.DoesNotContain(Uri.EscapeDataString("position report 4\n"), issue.Url);
        Assert.Contains(Uri.EscapeDataString("(4 older calls in the local copy)"), issue.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("kein-schraegstrich")]
    [InlineData("a/b/c")]
    [InlineData("evil.com/x?y")]
    public void Ohne_gueltiges_Repository_kein_Link(string repository)
    {
        var issue = BugReport.Build(repository, Template, BugReport.Resolve(["bug"]), "x", "y", Hoist());

        Assert.Null(issue.Url);
        Assert.Contains("y", issue.Body);
    }

    [Fact]
    public async Task Meldung_ueber_das_Dashboard_mit_lokaler_Kopie()
    {
        var capture = await PostAsync("/api/report/capture", "{}");
        var id = capture.GetProperty("id").GetString();
        Assert.Equal(4, capture.GetProperty("categories").GetArrayLength());
        Assert.Equal(Repository, capture.GetProperty("repository").GetString());
        Assert.Contains("Duopilo", capture.GetProperty("context").GetString());
        Assert.False(capture.GetProperty("summary").GetProperty("simConnected").GetBoolean());

        var report = await PostAsync("/api/report", $$"""{"id":"{{id}}","categories":["frequency"],"title":"","description":"Platzhalter 132.6 statt 132.25"}""");

        Assert.True(report.GetProperty("ok").GetBoolean(), report.ToString());
        Assert.Equal(new[] { "data/frequency" }, report.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ToArray());
        Assert.Contains("labels=data%2Ffrequency", report.GetProperty("url").GetString());
        var file = report.GetProperty("file").GetString()!;
        Assert.StartsWith(Path.Combine(_dataDirectory, "reports"), file);
        Assert.EndsWith("_frequency.md", file);
        var copy = File.ReadAllText(file);
        Assert.Contains("Platzhalter 132.6 statt 132.25", copy);
        Assert.Contains("Labels: data/frequency", copy);
        Assert.Contains("GitHub: [new issue", copy);
    }

    [Fact]
    public async Task Meldung_braucht_Art_oder_Text_und_eine_Momentaufnahme()
    {
        var unknown = await PostAsync("/api/report", """{"id":"gibtsnicht","categories":["bug"],"description":"x"}""");
        Assert.False(unknown.GetProperty("ok").GetBoolean());
        Assert.Contains("snapshot", unknown.GetProperty("error").GetString());

        var id = (await PostAsync("/api/report/capture", "{}")).GetProperty("id").GetString();
        var empty = await PostAsync("/api/report", $$"""{"id":"{{id}}","categories":[],"description":"  "}""");
        Assert.False(empty.GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(_dataDirectory, "reports")));
    }

    private async Task<JsonElement> PostAsync(string url, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Duopilo", "1");
        using var response = await _app.Local().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
