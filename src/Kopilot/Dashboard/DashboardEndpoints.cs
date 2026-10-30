using Kopilot.Aircraft;
using Kopilot.FlightPlan;
using Kopilot.Hosting;
using Kopilot.Input;
using Kopilot.Localization;
using Kopilot.Sim;
using Kopilot.Speech;
using Kopilot.Tools;
using Kopilot.Voice;

namespace Kopilot.Dashboard;

/// <summary>
/// Das Dashboard im Browser (nur lokal): Seite, Schriften, Daten-API und zwei Knöpfe (Profile neu laden, SimBrief laden).
/// Schreibende Aufrufe verlangen den Header "X-Kopilot: 1" – fremde Webseiten im selben Browser können ihn
/// nicht ohne CORS-Freigabe setzen.
/// </summary>
public static class DashboardEndpoints
{
    public const string ActionHeader = "X-Kopilot";

    private const string ResourcePrefix = "Kopilot.Dashboard.";

    private static readonly Dictionary<string, string> Fonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B612-Regular.woff"] = "fonts.B612-Regular.woff",
        ["B612-Bold.woff"] = "fonts.B612-Bold.woff",
        ["B612Mono-Regular.woff"] = "fonts.B612Mono-Regular.woff",
        ["B612Mono-Bold.woff"] = "fonts.B612Mono-Bold.woff",
    };

    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet("/", (HttpContext context) =>
        {
            NoCache(context);
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var html = Page();
            return html is null ? Results.NotFound() : Results.Content(html, "text/html; charset=utf-8");
        }).LocalOnly();

        // Texte des Dashboards in einer Sprache (Standard: eingestellte Sprache) – zum Umschalten ohne Neuladen der Seite
        app.MapGet("/api/i18n", (HttpContext context, string? lang) =>
        {
            NoCache(context);
            return Results.Content(Json.Serialize(I18n(lang)), "application/json");
        }).LocalOnly();

        app.MapGet("/dashboard/fonts/{file}", (string file, HttpContext context) =>
        {
            if (!Fonts.TryGetValue(file, out var resource))
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Resource(resource, "font/woff");
        }).LocalOnly();

        app.MapGet("/favicon.ico", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Resource("favicon.ico", "image/x-icon");
        }).LocalOnly();

        app.MapGet("/api/dashboard", (DashboardData data, HttpContext context, long? since, bool? log) =>
        {
            NoCache(context);
            return Results.Content(Json.Serialize(data.Build(since ?? -1, log == true)), "application/json");
        }).LocalOnly();

        app.MapPost("/api/reload-profiles", (HttpContext context, SimConnectClient sim, ProfileStore profiles) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            profiles.Reload();
            sim.RefreshProfile();
            return Results.Content(Json.Serialize(new { profiles = profiles.All.Count, errors = profiles.Errors }), "application/json");
        }).LocalOnly();

        app.MapGet("/api/speech", async (HttpContext context, CancellationToken ct) =>
        {
            NoCache(context);
            var speech = context.RequestServices.GetService<ISpeechOutput>();
            return speech is null
                ? Results.Content(Json.Serialize(new { available = false, error = "Sprachausgabe gibt es nur in der Tray-Instanz (HTTP-Modus)." }), "application/json")
                : Results.Content(Json.Serialize(SpeechView(await speech.RefreshAsync(ct))), "application/json");
        }).LocalOnly();

        app.MapPost("/api/speech", async (HttpContext context, SpeechSettings settings, CancellationToken ct) =>
        {
            if (!IsDashboardAction(context) || context.RequestServices.GetService<ISpeechOutput>() is not { } speech)
            {
                return Results.BadRequest();
            }

            return Results.Content(Json.Serialize(SpeechView(await speech.UpdateAsync(settings, ct))), "application/json");
        }).LocalOnly();

        app.MapPost("/api/speech/test", async (HttpContext context, CancellationToken ct) =>
        {
            if (!IsDashboardAction(context) || context.RequestServices.GetService<ISpeechOutput>() is not { } speech)
            {
                return Results.BadRequest();
            }

            try
            {
                await speech.SpeakNowAsync("Runway two three left assigned", ct);
                return Results.Content(Json.Serialize(new { ok = true }), "application/json");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
            {
                return Results.Content(Json.Serialize(new { ok = false, error = ex.Message }), "application/json");
            }
        }).LocalOnly();

        // Sprachschleife: an/aus und Mikrofon. Schreibt nur diese zwei Werte in voice-settings.json (Kommentare bleiben)
        // und startet die Schleife neu – Kopilot selbst läuft weiter.
        app.MapGet("/api/voice", (HttpContext context) =>
        {
            NoCache(context);
            if (context.RequestServices.GetService<VoiceLoop>() is not { } loop)
            {
                return Results.Content(Json.Serialize(new { available = false, error = VoiceUnavailable }), "application/json");
            }

            loop.RefreshDevices();
            return Results.Content(Json.Serialize(VoiceView(loop, null)), "application/json");
        }).LocalOnly();

        app.MapPost("/api/voice", (HttpContext context, VoiceToggle body) =>
        {
            if (!IsDashboardAction(context) || context.RequestServices.GetService<VoiceLoop>() is not { } loop)
            {
                return Results.BadRequest();
            }

            VoiceSettings saved;
            try
            {
                saved = loop.Store.Update(body.Enabled, body.Microphone);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return Results.Content(
                    Json.Serialize(new { available = true, error = $"{VoiceSettingsStore.FileName} nicht geschrieben: {ex.Message}" }),
                    "application/json");
            }

            loop.Reload($"Dashboard: Sprachschleife {(saved.Enabled ? "an" : "aus")}, Mikrofon \"{(saved.Microphone.Device.Length > 0 ? saved.Microphone.Device : "Windows-Standardgerät")}\"");
            return Results.Content(Json.Serialize(VoiceView(loop, saved)), "application/json");
        }).LocalOnly();

        app.MapPost("/api/input", (HttpContext context, InputToggle body, InputService input) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            input.SetEnabled(body.Enabled);
            return Results.Content(Json.Serialize(new { enabled = input.Enabled }), "application/json");
        }).LocalOnly();

        app.MapPost("/api/input/cancel", (HttpContext context, InputService input) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var result = input.CancelEverywhere("Abgebrochen (Dashboard)");
            return Results.Content(Json.Serialize(new { status = result.Status, action = result.Action }), "application/json");
        }).LocalOnly();

        app.MapPost("/api/simbrief/refresh", async (HttpContext context, SimBriefService simBrief, CancellationToken ct) =>
        {
            if (!IsDashboardAction(context))
            {
                return Results.BadRequest();
            }

            var ofp = await simBrief.GetAsync(refresh: true, ct);
            return Results.Content(Json.Serialize(new { loaded = ofp is not null, error = simBrief.LastError }), "application/json");
        }).LocalOnly();
    }

    /// <summary>Body von POST /api/input.</summary>
    public sealed record InputToggle(bool Enabled);

    /// <summary>Body von POST /api/voice. null = unverändert; Mikrofon "" = Windows-Standardgerät.</summary>
    public sealed record VoiceToggle(bool? Enabled, string? Microphone);

    private const string VoiceUnavailable = "Die Sprachschleife gibt es nur in der Tray-Instanz (HTTP-Modus).";

    /// <param name="saved">Gerade gespeicherte Einstellungen (der Neustart der Schleife läuft noch) – sonst der aktuelle Stand.</param>
    private static object VoiceView(VoiceLoop loop, VoiceSettings? saved)
    {
        var status = loop.Status;
        return new
        {
            available = true,
            settings = new
            {
                enabled = saved?.Enabled ?? status.Enabled,
                microphone = saved?.Microphone.Device ?? status.ConfiguredMicrophone ?? "",
            },
            microphones = status.Microphones ?? [],
            status = KopilotTools.VoiceView(status),
        };
    }

    internal static object SpeechView(SpeechStatus s) => new
    {
        available = s.Available,
        error = s.Error,
        settings = new
        {
            enabled = s.Settings.Enabled,
            voice = s.Settings.Voice,
            device = s.Settings.Device,
            volume = s.Settings.Volume,
            rate = s.Settings.Rate,
            minSeverity = s.Settings.MinSeverity.ToString(),
            mutedRules = s.Settings.MutedRules,
            aircraftAutoCallouts = s.Settings.AircraftAutoCallouts,
            engine = s.Settings.Engine,
            kokoroVoice = s.Settings.KokoroVoice,
        },
        activeEngine = s.ActiveEngine,
        kokoro = s.Kokoro is { } k
            ? new
            {
                state = k.State.ToString(),
                progress = k.Progress,
                error = k.Error,
                voices = k.Voices,
                lastSynthesisMs = k.LastSynthesisMs,
            }
            : null,
        activeVoice = s.ActiveVoice,
        voices = s.Voices,
        devices = s.Devices,
        speaking = s.Speaking,
        lastSpoken = s.LastSpoken,
    };

    private static bool IsDashboardAction(HttpContext context) =>
        context.Request.Headers.TryGetValue(ActionHeader, out var value) && value == "1";

    private static void NoCache(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    /// <summary>Prefix of the dashboard keys in the language files.</summary>
    public const string I18nPrefix = "dashboard.";

    /// <summary>Marker in dashboard.html, replaced with the texts of the selected language (no flash of English when German is set).</summary>
    public const string I18nMarker = "<!--KOPILOT_I18N-->";

    private static readonly System.Text.Json.JsonSerializerOptions EmbedJson = new()
    {
        // Default encoder: escapes < and > – the JSON sits inside a <script> block
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    /// <summary>Texts for the dashboard: language in use, available languages and all "dashboard." keys (missing ones in English).</summary>
    public static object I18n(string? language = null)
    {
        var lang = Loc.Normalize(language) ?? Loc.Language;
        return new
        {
            language = lang,
            available = Loc.Available.Select(l => new { code = l.Code, name = l.Name }),
            strings = Loc.Strings(I18nPrefix, lang),
        };
    }

    /// <summary>dashboard.html with the texts of the selected language embedded and html lang set.</summary>
    public static string? Page()
    {
        using var stream = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream(ResourcePrefix + "dashboard.html");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        var data = System.Text.Json.JsonSerializer.Serialize(I18n(), EmbedJson);
        html = html.Replace(I18nMarker, $"<script id=\"i18n\" type=\"application/json\">{data}</script>", StringComparison.Ordinal);
        return html.Replace("<html lang=\"en\">", $"<html lang=\"{Loc.Language}\">", StringComparison.Ordinal);
    }

    private static IResult Resource(string name, string contentType)
    {
        var stream = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream(ResourcePrefix + name);
        return stream is null ? Results.NotFound() : Results.Stream(stream, contentType);
    }
}
