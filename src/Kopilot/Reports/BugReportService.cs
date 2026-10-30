using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Kopilot.Atc;
using Kopilot.FlightPlan;
using Kopilot.Localization;
using Kopilot.Monitoring;
using Kopilot.Sim;
using Kopilot.Tools;
using Kopilot.Voice;
using Microsoft.Extensions.Options;

namespace Kopilot.Reports;

/// <summary>Gespeicherte Meldung: Issue-Daten und die lokale Kopie.</summary>
/// <param name="File">Pfad der lokalen Kopie, null wenn sie nicht geschrieben werden konnte.</param>
public sealed record BugReportSaved(BugReportIssue Issue, string? File, string? FileError);

/// <summary>
/// „Fehler melden“: hält beim Druck auf den Knopf fest, was Kopilot gerade weiß (Position, Höhe, ATC-Station und Frequenz,
/// COM1, Funksprüche …) – bis der Pilot seinen Text geschrieben hat, ist das Flugzeug längst weiter. Beim Absenden landet
/// eine Kopie unter %LOCALAPPDATA%\Kopilot\reports, der Link öffnet das vorausgefüllte GitHub-Issue.
/// </summary>
public sealed class BugReportService(
    IOptions<KopilotOptions> options,
    SimConnectClient sim,
    SimBriefService simBrief,
    AtcService atc,
    MonitoringService monitoring,
    IVoiceStatusSource voice,
    ILogger<BugReportService> log)
{
    /// <summary>So viele Momentaufnahmen werden gehalten (je Knopfdruck eine).</summary>
    private const int MaxCaptures = 20;

    private static readonly TimeSpan CaptureLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, BugReportContext> _captures = new(StringComparer.Ordinal);

    public ReportOptions Options => options.Value.Reports;

    /// <summary>Ordner der lokalen Kopien.</summary>
    public string Directory => string.IsNullOrWhiteSpace(Options.Directory)
        ? Path.Combine(options.Value.ResolveDataDirectory(), "reports")
        : Options.Directory;

    /// <summary>Momentaufnahme festhalten. Rückgabe: Kennung für <see cref="Submit"/> und der Kontext.</summary>
    public (string Id, BugReportContext Context) Capture()
    {
        var context = Snapshot(DateTimeOffset.UtcNow);
        var id = Guid.NewGuid().ToString("N")[..12];
        _captures[id] = context;

        foreach (var old in _captures.Where(c => DateTimeOffset.UtcNow - c.Value.CapturedUtc > CaptureLifetime).Select(c => c.Key).ToList())
        {
            _captures.TryRemove(old, out _);
        }

        while (_captures.Count > MaxCaptures && _captures.MinBy(c => c.Value.CapturedUtc) is { Key: var oldest })
        {
            _captures.TryRemove(oldest, out _);
        }

        return (id, context);
    }

    public BugReportContext? Find(string? id) => id is not null && _captures.TryGetValue(id, out var c) ? c : null;

    /// <summary>Meldung fertigstellen und die lokale Kopie schreiben.</summary>
    public BugReportSaved Submit(BugReportContext context, IReadOnlyList<BugReportCategory> categories, string? title, string? description)
    {
        var issue = BugReport.Build(Options.GitHubRepository, Options.IssueTemplate, categories, title, description, context);
        string? file = null;
        string? error = null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var stamp = context.CapturedUtc.UtcDateTime.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            var kind = categories.FirstOrDefault()?.Id ?? "meldung";
            file = Path.Combine(Directory, $"{stamp}Z_{kind}.md");
            for (var n = 2; File.Exists(file); n++)
            {
                file = Path.Combine(Directory, $"{stamp}Z_{kind}_{n}.md");
            }

            File.WriteAllText(file, LocalCopy(issue), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = Loc.T("report.copy.notWritten", ex.Message);
            file = null;
            log.LogWarning(Loc.Log("log.report.copyFailed"), error);
        }

        log.LogInformation(Loc.Log("log.report.saved"), issue.Title, string.Join(", ", issue.Labels), file is null ? "" : $" → {file}");
        return new BugReportSaved(issue, file, error);
    }

    /// <summary>Lokale Kopie: Titel, Labels, Link und der ungekürzte Text.</summary>
    public static string LocalCopy(BugReportIssue issue) =>
        $"# {issue.Title}\n\n" +
        (issue.Labels.Count > 0 ? Loc.T("report.copy.labels", string.Join(", ", issue.Labels)) + "\n\n" : "") +
        (issue.Url is null ? "" : Loc.T("report.copy.github", issue.Url) + "\n\n") +
        issue.Body;

    private BugReportContext Snapshot(DateTimeOffset now)
    {
        var connected = sim.Connection.IsConnected;
        double? Num(string key) => connected && sim.Num(key) is { } value && double.IsFinite(value) ? value : null;

        var lat = Num("lat");
        var lon = Num("lon");
        var ofp = simBrief.Current;

        // ATC-Stand unter der Sperre der Sitzung lesen
        var (atcPart, transcript, alongNm, totalNm, atcCallsign) = atc.WithSession((session, _) =>
            session is null
                ? ((BugReportAtc?)null, (IReadOnlyList<string>)Array.Empty<string>(), (double?)null, (double?)null, (string?)null)
                : (AtcPart(session), Transcript(session), session.AlongTrackNm, session.Plan.TotalNm, session.Plan.Callsign));

        // Ohne ATC-Sitzung die Streckenposition direkt aus dem Plan
        if (alongNm is null && ofp is not null && lat is { } la && lon is { } lo)
        {
            var points = ofp.RoutePoints();
            if (RouteTracker.Locate(points, la, lo) is { } route)
            {
                alongNm = route.DistanceFlownNm;
                totalNm = route.DistanceFlownNm + route.DistanceToGoNm;
            }
        }

        var heardStatus = voice.Status;
        string? voiceLast = null;
        if (heardStatus.LastTranscript is { Length: > 0 } heard)
        {
            voiceLast = Loc.T("report.voice.heard", heard, heardStatus.LastRoute ?? "?") +
                        (heardStatus.LastReply is { Length: > 0 } reply ? " " + Loc.T("report.voice.reply", reply) : "") +
                        (heardStatus.LastHeardUtc is { } at ? $" ({at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}Z)" : "");
        }

        return new BugReportContext
        {
            CapturedUtc = now,
            Version = typeof(BugReportService).Assembly.GetName().Version?.ToString(3) ?? "?",
            SimConnected = connected,
            Paused = connected ? sim.Status.Paused : null,
            SimZulu = Num("zuluTimeSec") is { } z ? TimeSpan.FromSeconds(((z % 86400) + 86400) % 86400).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : null,
            Aircraft = connected ? sim.Str("title") : null,
            Profile = connected ? sim.ActiveProfile?.Name : null,
            Lat = lat,
            Lon = lon,
            AltitudeFt = Num("altIndicated"),
            PressureAltitudeFt = Num("altPressure"),
            GroundSpeedKt = Num("gs"),
            HeadingDeg = Num("hdgMag"),
            OnGround = connected ? sim.Bool("onGround") : null,
            FlightPhase = connected ? monitoring.Phase : null,
            Com1ActiveMhz = Num("com1Active"),
            Com1StandbyMhz = Num("com1Standby"),
            Transponder = connected ? Fmt.Squawk(Num("xpdrCode")) : null,
            Callsign = atcCallsign ?? ofp?.FlightNumber ?? ofp?.Callsign,
            Origin = ofp?.Origin.Icao,
            Destination = ofp?.Destination.Icao,
            AlongTrackNm = alongNm is { } a ? Math.Round(a, 1) : null,
            TotalNm = totalNm is { } t ? Math.Round(t) : null,
            Atc = atcPart,
            Transcript = transcript,
            VoiceLast = voiceLast,
        };
    }

    private static BugReportAtc AtcPart(AtcSession session)
    {
        var plan = session.Plan;
        var along = session.AlongTrackNm;
        var here = along is { } a ? plan.FirAt(a) : null;
        var next = along is { } b ? plan.NextFir(b) : null;
        return new BugReportAtc(
            session.Station.Name,
            session.Station.Type.ToString(),
            session.Station.Facility,
            session.Station.Frequency.Written,
            session.Station.FrequencySource,
            session.Phase.ToString(),
            session.Step.ToString(),
            here?.Fir,
            here is null ? null : Math.Round(here.StartNm),
            next?.Fir,
            next is null ? null : Math.Round(next.StartNm),
            session.Value("callsign")?.Spoken,
            session.Squawk,
            session.ClearedAltitudeFt);
    }

    private static IReadOnlyList<string> Transcript(AtcSession session) =>
        session.Transcript.TakeLast(30).Select(t =>
            $"{t.TimeUtc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}Z " +
            $"{(t.From == "atc" ? "ATC  " : "Pilot")} {t.StationName}" +
            (string.IsNullOrEmpty(t.Frequency) ? "" : $" {t.Frequency}") +
            $": {t.Text}").ToList();
}
