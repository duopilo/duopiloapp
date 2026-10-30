using Kopilot.Atc;
using Kopilot.FlightPlan;
using Kopilot.Hosting;
using Kopilot.Input;
using Kopilot.Localization;
using Kopilot.Monitoring;
using Kopilot.Remote;
using Kopilot.Voice;
using Microsoft.Extensions.Options;

namespace Kopilot.Settings;

/// <summary>Ergebnis einer Änderung auf der Einstellungsseite.</summary>
/// <param name="Token">Neu erzeugter Bearer-Token – wird genau einmal angezeigt.</param>
public sealed record SettingsResult(bool Ok, string? Message, string? Error = null, string? Token = null)
{
    public static SettingsResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Einstellungsseite im Dashboard: liest den laufenden Stand und schreibt Änderungen nach appsettings.Local.json
/// (bzw. in die vorhandenen Dateien der Module: input.json, voice-settings.json).
/// <list type="bullet">
/// <item>Sofort wirksam: SimBrief-Konto, Eingabe-Modul, Sprachschleife und Mikrofon.</item>
/// <item>Nach einem Neustart von Kopilot: Zugang für Claude (Tunnel-Adresse, Passwort, Token), Monitoring und ATC – diese
/// Dienste lesen ihre Einstellungen beim Start. Die Seite zeigt dann „Neustart nötig“ mit Knopf.</item>
/// </list>
/// Geheimnisse (Passwort, Token) verlassen Kopilot nie Richtung Browser – außer ein frisch erzeugter Token, einmal zum Kopieren.
/// </summary>
public sealed class SettingsService
{
    private static readonly string[] Root = [KopilotOptions.Section];

    /// <summary>Werte, die erst nach einem Neustart gelten: Schlüssel unter "Kopilot" → Anzeigename und laufender Wert.</summary>
    private static readonly Dictionary<string, (string Label, Func<KopilotOptions, string> Running)> RestartKeys = new()
    {
        [Keys.PublicUrl] = ("Öffentliche Adresse (Tunnel)", o => NormalizeUrl(o.Remote.PublicUrl)),
        [Keys.ConsentPassword] = ("Freigabe-Passwort", o => o.Remote.ConsentPassword),
        [Keys.BearerToken] = ("Bearer-Token", o => o.Remote.BearerToken.Trim()),
        [Keys.Monitoring] = ("Monitoring", o => Bool(o.Monitoring.Enabled)),
        [Keys.Atc] = ("ATC", o => Bool(o.Atc.Enabled)),
    };

    /// <summary>Schlüssel, die eine Umgebungsvariable überschreiben kann (dann wirkt die Datei nicht).</summary>
    private static readonly string[] OverridableKeys =
    [
        Keys.SimBriefUsername, Keys.SimBriefPilotId, Keys.PublicUrl, Keys.ConsentPassword, Keys.BearerToken, Keys.Monitoring, Keys.Atc,
        Keys.Language,
    ];

    private static readonly HttpClient TunnelHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly KopilotOptions _options;
    private readonly LocalSettingsStore _store;
    private readonly SimBriefService _simBrief;
    private readonly MonitoringService _monitoring;
    private readonly AtcService _atc;
    private readonly InputService _input;
    private readonly IServiceProvider _services;
    private readonly ILogger<SettingsService> _log;
    private readonly Dictionary<string, string> _saved = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private object? _lastTunnelCheck;

    public SettingsService(
        IOptions<KopilotOptions> options,
        LocalSettingsStore store,
        SimBriefService simBrief,
        MonitoringService monitoring,
        AtcService atc,
        InputService input,
        IServiceProvider services,
        ILogger<SettingsService> log)
    {
        _options = options.Value;
        _store = store;
        _simBrief = simBrief;
        _monitoring = monitoring;
        _atc = atc;
        _input = input;
        _services = services;
        _log = log;
    }

    /// <summary>Schlüssel unter "Kopilot" (Doppelpunkt wie in der .NET-Konfiguration).</summary>
    public static class Keys
    {
        public const string SimBriefUsername = "SimBrief:Username";
        public const string SimBriefPilotId = "SimBrief:PilotId";
        public const string PublicUrl = "Remote:PublicUrl";
        public const string ConsentPassword = "Remote:ConsentPassword";
        public const string BearerToken = "Remote:BearerToken";
        public const string Monitoring = "Monitoring:Enabled";
        public const string Atc = "Atc:Enabled";
        public const string Language = "Language";
    }

    /// <summary>Was erst nach einem Neustart gilt (Anzeigenamen). Leer = alles ist wirksam.</summary>
    public IReadOnlyList<string> PendingRestart()
    {
        lock (_gate)
        {
            return RestartKeys
                .Where(k => _saved.TryGetValue(k.Key, out var saved) && saved != k.Value.Running(_options))
                .Select(k => k.Value.Label)
                .ToList();
        }
    }

    /// <summary>Alles für die Einstellungsseite. Geheimnisse nur als „gesetzt / nicht gesetzt“.</summary>
    public object View()
    {
        var o = _options;
        var saved = new RemoteOptions
        {
            PublicUrl = Saved(Keys.PublicUrl),
            ConsentPassword = Saved(Keys.ConsentPassword),
            BearerToken = Saved(Keys.BearerToken),
        };
        var restart = _services.GetService<AppRestart>();
        var voice = _services.GetService<VoiceLoop>();
        var pending = PendingRestart();
        var ofp = _simBrief.Current;

        return new
        {
            file = _store.FilePath,
            fileExists = _store.Exists,
            backup = _store.BackupPath,
            lastWriteUtc = _store.LastWriteUtc,
            restart = new { required = pending.Count > 0, changes = pending, available = restart?.Available == true },
            environmentOverrides = EnvironmentOverrides(),
            claude = new
            {
                mcpUrl = o.HttpUrl.TrimEnd('/') + "/mcp",
                publicUrl = saved.PublicUrl,
                publicUrlValid = saved.PublicUrl.Length == 0 || saved.PublicBaseUri is not null,
                connectorUrl = saved.ResourceUrl,
                oauth = new { active = o.Remote.OAuthEnabled, configured = saved.OAuthEnabled },
                bearer = new { active = o.Remote.HasBearerToken, configured = saved.HasBearerToken },
                consentPassword = new
                {
                    set = saved.ConsentPassword.Length >= RemoteOptions.MinConsentPasswordLength,
                    minLength = RemoteOptions.MinConsentPasswordLength,
                },
                tunnel = _lastTunnelCheck,
            },
            simBrief = new
            {
                username = _simBrief.Username,
                pilotId = _simBrief.PilotId,
                configured = _simBrief.IsConfigured,
                plan = ofp is null
                    ? null
                    : new { callsign = ofp.Callsign, origin = ofp.Origin.Icao, destination = ofp.Destination.Icao, fetchedUtc = _simBrief.FetchedUtc },
                error = _simBrief.LastError,
            },
            modules = new
            {
                monitoring = new { enabled = Saved(Keys.Monitoring) == "true", running = _monitoring.Enabled },
                atc = new { enabled = Saved(Keys.Atc) == "true", running = _atc.Enabled },
                input = new { enabled = _input.Enabled },
                voice = voice is null
                    ? (object)new { available = false }
                    : new { available = true, enabled = voice.Status.Enabled, state = voice.Status.State, file = voice.Store.FilePath },
            },
            reports = new { repository = o.Reports.GitHubRepository },
            language = new
            {
                current = Loc.Language,
                available = Loc.Available.Select(l => new { code = l.Code, name = l.Name }),
                overrides = Loc.OverrideDirectory,
            },
        };
    }

    /// <summary>SimBrief-Konto speichern und sofort übernehmen (der Plan wird danach neu geladen).</summary>
    public SettingsResult SaveSimBrief(string? username, string? pilotId)
    {
        username = (username ?? "").Trim();
        pilotId = (pilotId ?? "").Trim();
        if (username.Length > 64 || username.Any(char.IsControl))
        {
            return SettingsResult.Fail("Der SimBrief-Username ist zu lang (höchstens 64 Zeichen).");
        }

        if (pilotId.Length > 12 || !pilotId.All(char.IsAsciiDigit))
        {
            return SettingsResult.Fail("Die Pilot-ID besteht nur aus Ziffern (simbrief.com → Account Settings).");
        }

        if (!Write([SettingValue.Of(KeyPath(Keys.SimBriefUsername), username), SettingValue.Of(KeyPath(Keys.SimBriefPilotId), pilotId)], out var error))
        {
            return SettingsResult.Fail(error);
        }

        var changed = _simBrief.ChangeAccount(username, pilotId);
        var account = pilotId.Length > 0 ? $"Pilot-ID {pilotId}" : username.Length > 0 ? $"Username {username}" : null;
        return new SettingsResult(true, account is null
            ? "SimBrief ausgeschaltet."
            : changed ? $"SimBrief gespeichert ({account}) – gilt sofort." : $"SimBrief unverändert ({account}).");
    }

    /// <summary>
    /// Sprache für Dashboard, Tool-Meldungen und Logs speichern und sofort übernehmen. Callouts, Checklisten, FMA-Ansagen,
    /// Warnungen und Funk bleiben Englisch.
    /// </summary>
    public SettingsResult SaveLanguage(string? language)
    {
        var code = Loc.Normalize(language);
        if (code is null)
        {
            return SettingsResult.Fail(Loc.T("settings.language.unknown", language ?? "", string.Join(", ", Loc.Available.Select(l => l.Code))));
        }

        if (!Write([SettingValue.Of(KeyPath(Keys.Language), code)], out var error))
        {
            return SettingsResult.Fail(error);
        }

        var previous = Loc.Language;
        Loc.SetLanguage(code);
        _log.LogInformation(Loc.Log("log.settings.languageChanged"), previous, code);
        return new SettingsResult(true, Loc.T("settings.language.saved", Loc.Available.FirstOrDefault(l => l.Code == code)?.Name ?? code));
    }

    /// <summary>
    /// Zugang für Claude: öffentliche Adresse des Tunnels und Freigabe-Passwort (Claude-Connector, OAuth).
    /// <paramref name="publicUrl"/> null = unverändert, "" = aus. <paramref name="consentPassword"/> null oder "" = unverändert.
    /// </summary>
    public SettingsResult SaveClaude(string? publicUrl, string? consentPassword, bool removeConsentPassword)
    {
        var values = new List<SettingValue>();
        var tracked = new List<(string Key, string Value)>();
        if (publicUrl is not null)
        {
            var url = NormalizeUrl(publicUrl);
            if (url.Length > 0 && new RemoteOptions { PublicUrl = url }.PublicBaseUri is null)
            {
                return SettingsResult.Fail("Die öffentliche Adresse muss mit https:// beginnen und darf keinen Pfad haben, z. B. https://kopilot.example.com (ohne /mcp).");
            }

            values.Add(SettingValue.Of(KeyPath(Keys.PublicUrl), url));
            tracked.Add((Keys.PublicUrl, url));
        }

        if (removeConsentPassword)
        {
            values.Add(SettingValue.Of(KeyPath(Keys.ConsentPassword), ""));
            tracked.Add((Keys.ConsentPassword, ""));
        }
        else if (!string.IsNullOrEmpty(consentPassword))
        {
            if (consentPassword.Length < RemoteOptions.MinConsentPasswordLength)
            {
                return SettingsResult.Fail($"Das Freigabe-Passwort braucht mindestens {RemoteOptions.MinConsentPasswordLength} Zeichen.");
            }

            values.Add(SettingValue.Of(KeyPath(Keys.ConsentPassword), consentPassword));
            tracked.Add((Keys.ConsentPassword, consentPassword));
        }

        if (values.Count == 0)
        {
            return new SettingsResult(true, "Nichts geändert.");
        }

        if (!Write(values, out var error))
        {
            return SettingsResult.Fail(error);
        }

        Track(tracked);
        _log.LogInformation("Einstellungen (Dashboard): Zugang für Claude geändert ({Keys}) – gilt nach Neustart.", string.Join(", ", tracked.Select(t => t.Key)));
        return new SettingsResult(true, RestartMessage("Zugang für Claude gespeichert"));
    }

    /// <summary>Bearer-Token für Claude Code und andere Clients neu erzeugen ("new") oder entfernen ("remove").</summary>
    public SettingsResult BearerToken(string? action)
    {
        string token;
        switch (action)
        {
            case "new":
                token = Secrets.NewToken();
                break;
            case "remove":
                token = "";
                break;
            default:
                return SettingsResult.Fail("Unbekannte Aktion (erlaubt: new, remove).");
        }

        if (!Write([SettingValue.Of(KeyPath(Keys.BearerToken), token)], out var error))
        {
            return SettingsResult.Fail(error);
        }

        Track([(Keys.BearerToken, token)]);
        _log.LogInformation("Einstellungen (Dashboard): Bearer-Token {Action} – gilt nach Neustart.", token.Length > 0 ? "neu erzeugt" : "entfernt");
        return token.Length > 0
            ? new SettingsResult(true, RestartMessage("Neuer Bearer-Token gespeichert – jetzt kopieren, er wird nur einmal angezeigt"), Token: token)
            : new SettingsResult(true, RestartMessage("Bearer-Token entfernt"));
    }

    /// <summary>Module ein- und ausschalten. null = unverändert.</summary>
    public SettingsResult SaveModules(bool? monitoring, bool? atc, bool? input, bool? voice)
    {
        var messages = new List<string>();
        var values = new List<SettingValue>();
        var tracked = new List<(string Key, string Value)>();
        if (monitoring is { } m)
        {
            values.Add(SettingValue.Of(KeyPath(Keys.Monitoring), m));
            tracked.Add((Keys.Monitoring, Bool(m)));
        }

        if (atc is { } a)
        {
            values.Add(SettingValue.Of(KeyPath(Keys.Atc), a));
            tracked.Add((Keys.Atc, Bool(a)));
        }

        if (values.Count > 0)
        {
            if (!Write(values, out var error))
            {
                return SettingsResult.Fail(error);
            }

            Track(tracked);
            messages.Add(string.Join(", ", tracked.Select(t => $"{RestartKeys[t.Key].Label} {(t.Value == "true" ? "an" : "aus")}")));
        }

        if (input is { } i)
        {
            _input.SetEnabled(i);
            messages.Add(i ? "Eingabe freigegeben" : "Eingabe gesperrt");
        }

        if (voice is { } v)
        {
            if (_services.GetService<VoiceLoop>() is not { } loop)
            {
                return SettingsResult.Fail("Die Sprachschleife gibt es nur in der Tray-Instanz (HTTP-Modus).");
            }

            try
            {
                loop.Store.Update(v, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return SettingsResult.Fail($"{VoiceSettingsStore.FileName} nicht geschrieben: {ex.Message}");
            }

            loop.Reload($"Dashboard-Einstellungen: Sprachschleife {(v ? "an" : "aus")}");
            messages.Add(v ? "Sprachschleife an" : "Sprachschleife aus");
        }

        if (messages.Count == 0)
        {
            return new SettingsResult(true, "Nichts geändert.");
        }

        _log.LogInformation("Einstellungen (Dashboard): {Changes}", string.Join("; ", messages));
        return new SettingsResult(true, RestartMessage(string.Join(" · ", messages)));
    }

    /// <summary>
    /// Prüft die öffentliche Adresse von außen: /mcp ohne Token muss mit Kopilots 401 antworten – dann führt der Tunnel bis hierher.
    /// Geprüft wird die laufende Adresse (eine gerade gespeicherte gilt erst nach dem Neustart).
    /// </summary>
    public async Task<object> CheckTunnelAsync(CancellationToken ct)
    {
        var remote = _options.Remote;
        object result;
        if (remote.PublicBaseUri is null)
        {
            result = TunnelResult("off", remote.PublicUrl.Length == 0
                ? "Keine öffentliche Adresse eingetragen – Claude erreicht Kopilot nur lokal (Claude Desktop, Claude Code)."
                : "Die öffentliche Adresse ist ungültig (https:// ohne Pfad).");
        }
        else
        {
            var url = remote.Issuer + "/mcp";
            try
            {
                using var response = await TunnelHttp.GetAsync(url, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                var code = (int)response.StatusCode;
                var cloudflare = response.Headers.Contains("CF-RAY") || response.Headers.Server.ToString().Contains("cloudflare", StringComparison.OrdinalIgnoreCase);
                result = code switch
                {
                    401 when body.Contains("Kopilot", StringComparison.OrdinalIgnoreCase) =>
                        TunnelResult("ok", $"Tunnel steht: {remote.PublicBaseUri.Host} erreicht diese Kopilot-Instanz."),
                    403 when cloudflare =>
                        TunnelResult("blocked", "Cloudflare blockt diesen Rechner (HTTP 403) – vermutlich die WAF-Regel, die /mcp nur für Claude öffnet. " +
                                                "Das ist so gewollt; von hier lässt sich der Tunnel dann nicht prüfen."),
                    400 => TunnelResult("error", "Kopilot hat den Hostnamen abgelehnt (HTTP 400). Passt die öffentliche Adresse zum Hostnamen des Tunnels?"),
                    502 or 503 or 504 or 530 => TunnelResult("error",
                        $"Cloudflare erreicht Kopilot nicht (HTTP {code}). Läuft cloudflared, und zeigt der Tunnel auf {_options.HttpUrl}?"),
                    _ => TunnelResult("error", $"Unerwartete Antwort HTTP {code} von {url}."),
                };
            }
            catch (HttpRequestException ex)
            {
                result = TunnelResult("error", $"{url} nicht erreichbar: {ex.Message} – DNS-Eintrag und Tunnel prüfen.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                result = TunnelResult("error", $"Keine Antwort von {url} nach 10 s.");
            }
        }

        _lastTunnelCheck = result;
        return result;
    }

    private static object TunnelResult(string state, string message) => new { state, message, checkedUtc = DateTimeOffset.UtcNow };

    private string RestartMessage(string text)
    {
        var pending = PendingRestart();
        return pending.Count == 0 ? text + "." : $"{text} – gilt nach einem Neustart von Kopilot.";
    }

    private bool Write(IReadOnlyList<SettingValue> values, out string error)
    {
        try
        {
            _store.Set(values);
            error = "";
            return true;
        }
        catch (SettingsWriteException ex)
        {
            _log.LogWarning("Einstellungen nicht gespeichert: {Message}", ex.Message);
            error = ex.Message;
            return false;
        }
    }

    private void Track(IEnumerable<(string Key, string Value)> values)
    {
        lock (_gate)
        {
            foreach (var (key, value) in values)
            {
                _saved[key] = value;
            }
        }
    }

    /// <summary>Gespeicherter Wert aus dieser Sitzung, sonst der laufende.</summary>
    private string Saved(string key)
    {
        lock (_gate)
        {
            return _saved.TryGetValue(key, out var value) ? value : RestartKeys[key].Running(_options);
        }
    }

    private static IReadOnlyList<string> EnvironmentOverrides() =>
        OverridableKeys
            .Select(k => $"{KopilotOptions.Section}__{k.Replace(":", "__", StringComparison.Ordinal)}")
            .Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            .ToList();

    private static string[] KeyPath(string key) => [.. Root, .. key.Split(':')];

    private static string Bool(bool value) => value ? "true" : "false";

    private static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');
}
