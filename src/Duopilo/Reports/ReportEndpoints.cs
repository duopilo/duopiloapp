using Kopilot.Dashboard;
using Kopilot.Hosting;
using Kopilot.Localization;
using Kopilot.Tools;

namespace Kopilot.Reports;

/// <summary>
/// Knopf „Fehler melden“ im Dashboard (nur lokal, schreibend nur mit Header "X-Kopilot: 1"):
/// <list type="number">
/// <item>POST /api/report/capture – Momentaufnahme beim Druck auf den Knopf.</item>
/// <item>POST /api/report – Art, Titel und Text dazu: lokale Kopie schreiben, Link zum GitHub-Issue zurückgeben.</item>
/// </list>
/// </summary>
public static class ReportEndpoints
{
    /// <param name="Id">Kennung aus /api/report/capture.</param>
    /// <param name="Categories">Kennungen aus <see cref="BugReport.Categories"/> (fir, frequency, airline, bug).</param>
    public sealed record ReportBody(string? Id, IReadOnlyList<string>? Categories, string? Title, string? Description);

    public static void MapReports(this WebApplication app)
    {
        app.MapPost("/api/report/capture", (HttpContext context, BugReportService reports) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var (id, capture) = reports.Capture();
            return Results.Content(Json.Serialize(new
            {
                id,
                capturedUtc = capture.CapturedUtc,
                repository = BugReport.IsRepository(reports.Options.GitHubRepository) ? reports.Options.GitHubRepository : null,
                directory = reports.Directory,
                categories = BugReport.Categories.Select(c => new
                {
                    id = c.Id,
                    label = c.Label,
                    name = c.Name,
                    title = BugReport.SuggestTitle([c], capture),
                }),
                title = BugReport.SuggestTitle([], capture),
                context = BugReport.ContextMarkdown(capture),
                summary = new
                {
                    station = capture.Atc is { } a ? $"{a.Station} {a.Frequency}" : null,
                    com1 = capture.Com1ActiveMhz,
                    lat = capture.Lat,
                    lon = capture.Lon,
                    altitudeFt = capture.AltitudeFt,
                    simConnected = capture.SimConnected,
                },
            }), "application/json");
        }).LocalOnly();

        app.MapPost("/api/report", (HttpContext context, BugReportService reports, ReportBody body) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            if (reports.Find(body.Id) is not { } capture)
            {
                return Results.Content(Json.Serialize(new
                {
                    ok = false,
                    error = Loc.T("report.error.captureMissing"),
                }), "application/json");
            }

            var categories = BugReport.Resolve(body.Categories);
            if (categories.Count == 0 && string.IsNullOrWhiteSpace(body.Description))
            {
                return Results.Content(Json.Serialize(new { ok = false, error = Loc.T("report.error.empty") }), "application/json");
            }

            var saved = reports.Submit(capture, categories, body.Title, body.Description);
            return Results.Content(Json.Serialize(new
            {
                ok = true,
                title = saved.Issue.Title,
                labels = saved.Issue.Labels,
                url = saved.Issue.Url,
                truncated = saved.Issue.Truncated,
                body = saved.Issue.Body,
                file = saved.File,
                fileError = saved.FileError,
            }), "application/json");
        }).LocalOnly();
    }

    private static bool IsDashboardAction(HttpContext context) =>
        context.Request.Headers.TryGetValue(DashboardEndpoints.ActionHeader, out var value) && value == "1";
}
