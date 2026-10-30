using Kopilot.Dashboard;
using Kopilot.Hosting;
using Kopilot.Localization;
using Kopilot.Tools;

namespace Kopilot.Settings;

/// <summary>
/// API der Einstellungsseite im Dashboard (nur lokal). Schreibende Aufrufe verlangen wie alle Dashboard-Knöpfe den Header
/// "X-Kopilot: 1" – fremde Webseiten im selben Browser können ihn nicht ohne CORS-Freigabe setzen.
/// Das Mikrofon läuft über die vorhandene API /api/voice.
/// </summary>
public static class SettingsEndpoints
{
    public sealed record SimBriefBody(string? Username, string? PilotId);

    /// <param name="PublicUrl">null = unverändert, "" = aus.</param>
    /// <param name="ConsentPassword">null oder "" = unverändert.</param>
    public sealed record ClaudeBody(string? PublicUrl, string? ConsentPassword, bool RemoveConsentPassword = false);

    /// <param name="Action">"new" oder "remove".</param>
    public sealed record TokenBody(string? Action);

    /// <param name="Language">Sprachcode, z. B. "en" oder "de".</param>
    public sealed record LanguageBody(string? Language);

    /// <summary>null = unverändert.</summary>
    public sealed record ModulesBody(bool? Monitoring, bool? Atc, bool? Input, bool? Voice);

    public static void MapSettings(this WebApplication app)
    {
        app.MapGet("/api/settings", (HttpContext context, SettingsService settings) =>
        {
            NoCache(context);
            return Results.Content(Json.Serialize(settings.View()), "application/json");
        }).LocalOnly();

        app.MapPost("/api/settings/simbrief", (HttpContext context, SettingsService settings, SimBriefBody body) =>
            IsDashboardAction(context) ? Answer(settings, settings.SaveSimBrief(body.Username, body.PilotId)) : Results.BadRequest()).LocalOnly();

        app.MapPost("/api/settings/claude", (HttpContext context, SettingsService settings, ClaudeBody body) =>
            IsDashboardAction(context)
                ? Answer(settings, settings.SaveClaude(body.PublicUrl, body.ConsentPassword, body.RemoveConsentPassword))
                : Results.BadRequest()).LocalOnly();

        app.MapPost("/api/settings/claude/token", (HttpContext context, SettingsService settings, TokenBody body) =>
            IsDashboardAction(context) ? Answer(settings, settings.BearerToken(body.Action)) : Results.BadRequest()).LocalOnly();

        app.MapPost("/api/settings/claude/check", async (HttpContext context, SettingsService settings, CancellationToken ct) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var tunnel = await settings.CheckTunnelAsync(ct);
            return Results.Content(Json.Serialize(new { ok = true, tunnel, settings = settings.View() }), "application/json");
        }).LocalOnly();

        app.MapPost("/api/settings/modules", (HttpContext context, SettingsService settings, ModulesBody body) =>
            IsDashboardAction(context) ? Answer(settings, settings.SaveModules(body.Monitoring, body.Atc, body.Input, body.Voice)) : Results.BadRequest()).LocalOnly();

        app.MapPost("/api/settings/language", (HttpContext context, SettingsService settings, LanguageBody body) =>
            IsDashboardAction(context) ? Answer(settings, settings.SaveLanguage(body.Language)) : Results.BadRequest()).LocalOnly();

        // Neustart der Tray-Instanz (nach Änderungen, die erst beim Start gelesen werden)
        app.MapPost("/api/restart", (HttpContext context) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            if (context.RequestServices.GetService<AppRestart>() is not { } restart)
            {
                return Results.Content(Json.Serialize(new { ok = false, error = Loc.T("settings.restart.trayOnly") }), "application/json");
            }

            var error = restart.Restart(Loc.T("settings.restart.reason"));
            return Results.Content(Json.Serialize(new { ok = error is null, error }), "application/json");
        }).LocalOnly();
    }

    private static IResult Answer(SettingsService settings, SettingsResult result) =>
        Results.Content(
            Json.Serialize(new { ok = result.Ok, message = result.Message, error = result.Error, token = result.Token, settings = settings.View() }),
            "application/json");

    private static bool IsDashboardAction(HttpContext context) =>
        context.Request.Headers.TryGetValue(DashboardEndpoints.ActionHeader, out var value) && value == "1";

    private static void NoCache(HttpContext context) => context.Response.Headers.CacheControl = "no-store";
}
