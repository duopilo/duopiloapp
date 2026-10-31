using Duopilo.Atc;
using Duopilo.FlightPlan;
using Duopilo.Hosting;
using Duopilo.Input;
using Duopilo.Localization;
using Duopilo.Monitoring;
using Duopilo.Remote;
using Duopilo.Smalltalk;
using Duopilo.Speech;
using Duopilo.Voice;
using Microsoft.Extensions.Options;

namespace Duopilo.Settings;

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
/// <item>Nach einem Neustart von Duopilo: Zugang für Claude (Tunnel-Adresse, Passwort, Token), Monitoring und ATC – diese
/// Dienste lesen ihre Einstellungen beim Start. Die Seite zeigt dann „Neustart nötig“ mit Knopf.</item>
/// </list>
/// Geheimnisse (Passwort, Token) verlassen Duopilo nie Richtung Browser – außer ein frisch erzeugter Token, einmal zum Kopieren.
/// </summary>
public sealed class SettingsService
{
    private static readonly string[] Root = [DuopiloOptions.Section];

    /// <summary>Werte, die erst nach einem Neustart gelten: Schlüssel unter "Duopilo" → Anzeigename (eingestellte Sprache) und laufender Wert.</summary>
    private static readonly Dictionary<string, (Func<string> Label, Func<DuopiloOptions, string> Running)> RestartKeys = new()
    {
        [Keys.PublicUrl] = (() => Loc.T("settings.label.publicUrl"), o => NormalizeUrl(o.Remote.PublicUrl)),
        [Keys.ConsentPassword] = (() => Loc.T("settings.label.consentPassword"), o => o.Remote.ConsentPassword),
        [Keys.BearerToken] = (() => Loc.T("settings.label.bearerToken"), o => o.Remote.BearerToken.Trim()),
        [Keys.Monitoring] = (() => Loc.T("settings.label.monitoring"), o => Bool(o.Monitoring.Enabled)),
        [Keys.Atc] = (() => Loc.T("settings.label.atc"), o => Bool(o.Atc.Enabled)),
    };

    /// <summary>Schlüssel, die eine Umgebungsvariable überschreiben kann (dann wirkt die Datei nicht).</summary>
    private static readonly string[] OverridableKeys =
    [
        Keys.SimBriefUsername, Keys.SimBriefPilotId, Keys.PublicUrl, Keys.ConsentPassword, Keys.BearerToken, Keys.Monitoring, Keys.Atc,
        Keys.Language, Keys.SmalltalkProvider, Keys.SmalltalkModel, Keys.SmalltalkUrl, Keys.SmalltalkApiKey, Keys.FirstOfficerName,
    ];

    private static readonly HttpClient TunnelHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly DuopiloOptions _options;
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
        IOptions<DuopiloOptions> options,
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

    /// <summary>Schlüssel unter "Duopilo" (Doppelpunkt wie in der .NET-Konfiguration).</summary>
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
        public const string SmalltalkProvider = "Smalltalk:Provider";
        public const string SmalltalkModel = "Smalltalk:Model";
        public const string SmalltalkUrl = "Smalltalk:Url";
        public const string SmalltalkApiKey = "Smalltalk:ApiKey";
        public const string SmalltalkMaxReplyWords = "Smalltalk:MaxReplyWords";
        public const string SmalltalkRequestsPerMinute = "Smalltalk:RequestsPerMinute";
        public const string SmalltalkReasoning = "Smalltalk:Reasoning";
        public const string FirstOfficerEnabled = "FirstOfficer:Enabled";
        public const string FirstOfficerName = "FirstOfficer:Name";
        public const string FirstOfficerGender = "FirstOfficer:Gender";
        public const string FirstOfficerIntroduce = "FirstOfficer:Introduce";
        public const string UpdatesCheck = "Updates:Check";
        public const string UpdatesPrereleases = "Updates:Prereleases";
    }

    /// <summary>Was erst nach einem Neustart gilt (Anzeigenamen). Leer = alles ist wirksam.</summary>
    public IReadOnlyList<string> PendingRestart()
    {
        lock (_gate)
        {
            return RestartKeys
                .Where(k => _saved.TryGetValue(k.Key, out var saved) && saved != k.Value.Running(_options))
                .Select(k => k.Value.Label())
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
            reports = new { repository = o.Reports.ResolveGitHubRepository() },
            smalltalk = SmalltalkView(),
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
            return SettingsResult.Fail(Loc.T("settings.simbrief.usernameTooLong"));
        }

        if (pilotId.Length > 12 || !pilotId.All(char.IsAsciiDigit))
        {
            return SettingsResult.Fail(Loc.T("settings.simbrief.pilotIdDigits"));
        }

        if (!Write([SettingValue.Of(KeyPath(Keys.SimBriefUsername), username), SettingValue.Of(KeyPath(Keys.SimBriefPilotId), pilotId)], out var error))
        {
            return SettingsResult.Fail(error);
        }

        var changed = _simBrief.ChangeAccount(username, pilotId);
        var account = pilotId.Length > 0 ? Loc.T("settings.simbrief.pilotId", pilotId) : username.Length > 0 ? Loc.T("settings.simbrief.username", username) : null;
        return new SettingsResult(true, account is null
            ? Loc.T("settings.simbrief.off")
            : changed ? Loc.T("settings.simbrief.saved", account) : Loc.T("settings.simbrief.unchanged", account));
    }

    /// <summary>
    /// Sprache für Dashboard, Tool-Meldungen und Tray speichern und sofort übernehmen (Logs bleiben Englisch). Callouts, Checklisten, FMA-Ansagen,
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

    /// <summary>Update-Prüfung ein/aus und Vorabversionen ("auto", "on", "off") – gilt sofort. null = unverändert.</summary>
    public SettingsResult SaveUpdates(bool? check, string? prereleases)
    {
        var updates = _services.GetService<Duopilo.Updates.UpdateService>();
        var current = updates?.Status;
        var values = new List<SettingValue>();
        if (check is { } c)
        {
            values.Add(SettingValue.Of(KeyPath(Keys.UpdatesCheck), c));
        }

        string? normalized = null;
        if (prereleases is not null)
        {
            normalized = Duopilo.Updates.UpdateLogic.NormalizePrereleases(prereleases);
            values.Add(SettingValue.Of(KeyPath(Keys.UpdatesPrereleases), normalized));
        }

        if (values.Count == 0)
        {
            return new SettingsResult(true, Loc.T("settings.nothingChanged"));
        }

        if (!Write(values, out var error))
        {
            return SettingsResult.Fail(error);
        }

        var newCheck = check ?? current?.CheckEnabled ?? _options.Updates.Check;
        var newPrereleases = normalized ?? current?.Prereleases ?? _options.Updates.Prereleases;
        updates?.Configure(newCheck, newPrereleases);
        _log.LogInformation(Loc.Log("log.settings.updatesChanged"), newCheck, newPrereleases);
        return new SettingsResult(true, newCheck ? Loc.T("settings.updates.savedOn") : Loc.T("settings.updates.savedOff"));
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
                return SettingsResult.Fail(Loc.T("settings.claude.publicUrlInvalid"));
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
                return SettingsResult.Fail(Loc.T("settings.claude.passwordTooShort", RemoteOptions.MinConsentPasswordLength));
            }

            values.Add(SettingValue.Of(KeyPath(Keys.ConsentPassword), consentPassword));
            tracked.Add((Keys.ConsentPassword, consentPassword));
        }

        if (values.Count == 0)
        {
            return new SettingsResult(true, Loc.T("settings.nothingChanged"));
        }

        if (!Write(values, out var error))
        {
            return SettingsResult.Fail(error);
        }

        Track(tracked);
        _log.LogInformation(Loc.Log("log.settings.claudeChanged"), string.Join(", ", tracked.Select(t => t.Key)));
        return new SettingsResult(true, RestartMessage(Loc.T("settings.claude.saved")));
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
                return SettingsResult.Fail(Loc.T("settings.token.unknownAction"));
        }

        if (!Write([SettingValue.Of(KeyPath(Keys.BearerToken), token)], out var error))
        {
            return SettingsResult.Fail(error);
        }

        Track([(Keys.BearerToken, token)]);
        if (token.Length > 0)
        {
            _log.LogInformation(Loc.Log("log.settings.tokenCreated"));
            return new SettingsResult(true, RestartMessage(Loc.T("settings.token.created")), Token: token);
        }

        _log.LogInformation(Loc.Log("log.settings.tokenRemoved"));
        return new SettingsResult(true, RestartMessage(Loc.T("settings.token.removed")));
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
            messages.Add(string.Join(", ", tracked.Select(t => ModuleState(RestartKeys[t.Key].Label(), t.Value == "true"))));
        }

        if (input is { } i)
        {
            _input.SetEnabled(i);
            messages.Add(i ? Loc.T("settings.modules.inputEnabled") : Loc.T("settings.modules.inputDisabled"));
        }

        if (voice is { } v)
        {
            if (_services.GetService<VoiceLoop>() is not { } loop)
            {
                return SettingsResult.Fail(Loc.T("settings.voice.trayOnly"));
            }

            try
            {
                loop.Store.Update(v, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return SettingsResult.Fail(Loc.T("settings.file.notWritten", VoiceSettingsStore.FileName, ex.Message));
            }

            var voiceChange = v ? Loc.T("settings.modules.voiceOn") : Loc.T("settings.modules.voiceOff");
            loop.Reload(Loc.T("settings.voice.reloadReason", voiceChange));
            messages.Add(voiceChange);
        }

        if (messages.Count == 0)
        {
            return new SettingsResult(true, Loc.T("settings.nothingChanged"));
        }

        _log.LogInformation(Loc.Log("log.settings.modulesChanged"), string.Join("; ", messages.Select(Loc.ToEnglish)));
        return new SettingsResult(true, RestartMessage(string.Join(" · ", messages)));
    }

    /// <summary>
    /// Prüft die öffentliche Adresse von außen: /mcp ohne Token muss mit Duopilos 401 antworten – dann führt der Tunnel bis hierher.
    /// Geprüft wird die laufende Adresse (eine gerade gespeicherte gilt erst nach dem Neustart).
    /// </summary>
    public async Task<object> CheckTunnelAsync(CancellationToken ct)
    {
        var remote = _options.Remote;
        object result;
        if (remote.PublicBaseUri is null)
        {
            result = TunnelResult("off", remote.PublicUrl.Length == 0
                ? Loc.T("settings.tunnel.noPublicUrl")
                : Loc.T("settings.tunnel.publicUrlInvalid"));
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
                    // Antwort von Duopilo selbst (oder einem alten Kopilot hinter derselben Adresse)
                    401 when body.Contains(AppNames.Product, StringComparison.OrdinalIgnoreCase)
                        || body.Contains(AppNames.Legacy.Product, StringComparison.OrdinalIgnoreCase) =>
                        TunnelResult("ok", Loc.T("settings.tunnel.ok", remote.PublicBaseUri.Host)),
                    403 when cloudflare =>
                        TunnelResult("blocked", Loc.T("settings.tunnel.blocked")),
                    400 => TunnelResult("error", Loc.T("settings.tunnel.hostRejected")),
                    502 or 503 or 504 or 530 => TunnelResult("error", Loc.T("settings.tunnel.originUnreachable", code, _options.HttpUrl)),
                    _ => TunnelResult("error", Loc.T("settings.tunnel.unexpected", code, url)),
                };
            }
            catch (HttpRequestException ex)
            {
                result = TunnelResult("error", Loc.T("settings.tunnel.unreachable", url, ex.Message));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                result = TunnelResult("error", Loc.T("settings.tunnel.timeout", url));
            }
        }

        _lastTunnelCheck = result;
        return result;
    }

    /// <summary>
    /// First Officer und Smalltalk speichern und sofort übernehmen. null = unverändert; der Schlüssel nur, wenn einer eingegeben wurde
    /// (er geht nie zurück an den Browser und nie ins Log).
    /// </summary>
    public SettingsResult SaveSmalltalk(SmalltalkChange change)
    {
        if (_services.GetService<SmalltalkService>() is not { } service)
        {
            return SettingsResult.Fail(Loc.T("settings.smalltalk.unavailable"));
        }

        var values = new List<SettingValue>();
        var current = service.Config;
        var options = new SmalltalkOptions
        {
            Provider = current.Provider,
            Model = current.Model,
            Url = current.Url,
            ApiKey = current.ApiKey,
            MaxReplyWords = current.MaxReplyWords,
            RequestsPerMinute = current.RequestsPerMinute,
            TimeoutSec = current.TimeoutSec,
            UnaddressedMaxWords = current.UnaddressedMaxWords,
            Reasoning = current.Reasoning,
        };

        if (change.Provider is not null)
        {
            var provider = SmalltalkProviders.Normalize(change.Provider);
            if (provider == SmalltalkProviders.Off && !string.Equals(change.Provider.Trim(), SmalltalkProviders.Off, StringComparison.OrdinalIgnoreCase))
            {
                return SettingsResult.Fail(Loc.T("settings.smalltalk.unknownProvider", change.Provider, string.Join(", ", SmalltalkProviders.All)));
            }

            options.Provider = provider;
            values.Add(SettingValue.Of(KeyPath(Keys.SmalltalkProvider), provider));
        }

        if (change.Model is not null)
        {
            var model = change.Model.Trim();
            if (model.Length > 100 || model.Any(char.IsControl) || model.Any(char.IsWhiteSpace))
            {
                return SettingsResult.Fail(Loc.T("settings.smalltalk.modelInvalid"));
            }

            options.Model = model;
            values.Add(SettingValue.Of(KeyPath(Keys.SmalltalkModel), model));
        }

        if (change.Url is not null)
        {
            var url = SmalltalkConfig.NormalizeUrl(change.Url);
            if (url.Length > 0 && !SmalltalkConfig.ValidUrl(url))
            {
                return SettingsResult.Fail(Loc.T("settings.smalltalk.urlInvalid"));
            }

            options.Url = url;
            values.Add(SettingValue.Of(KeyPath(Keys.SmalltalkUrl), url));
        }

        if (change.RemoveApiKey)
        {
            options.ApiKey = "";
            values.Add(SettingValue.Of(KeyPath(Keys.SmalltalkApiKey), ""));
        }
        else if (!string.IsNullOrWhiteSpace(change.ApiKey))
        {
            var key = change.ApiKey.Trim();
            if (key.Length > 400 || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            {
                return SettingsResult.Fail(Loc.T("settings.smalltalk.keyInvalid"));
            }

            options.ApiKey = key;
            values.Add(SettingValue.Of(KeyPath(Keys.SmalltalkApiKey), key));
        }

        if (change.MaxReplyWords is { } words)
        {
            if (words is < 10 or > 150)
            {
                return SettingsResult.Fail(Loc.T("settings.smalltalk.wordsRange", 10, 150));
            }

            options.MaxReplyWords = words;
            values.Add(new SettingValue(KeyPath(Keys.SmalltalkMaxReplyWords), words.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (change.RequestsPerMinute is { } rpm)
        {
            if (rpm is < 1 or > 30)
            {
                return SettingsResult.Fail(Loc.T("settings.smalltalk.rpmRange", 1, 30));
            }

            options.RequestsPerMinute = rpm;
            values.Add(new SettingValue(KeyPath(Keys.SmalltalkRequestsPerMinute), rpm.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (change.Reasoning is not null)
        {
            if (!SmalltalkReasoning.IsValid(change.Reasoning))
            {
                return SettingsResult.Fail(Loc.T("settings.smalltalk.reasoningInvalid", string.Join(", ", SmalltalkReasoning.All)));
            }

            var reasoning = SmalltalkReasoning.Normalize(change.Reasoning);
            options.Reasoning = reasoning;
            values.Add(SettingValue.Of(KeyPath(Keys.SmalltalkReasoning), reasoning));
        }

        var fo = service.FirstOfficer;
        var firstOfficer = new FirstOfficerOptions { Enabled = fo.Enabled, Name = fo.Name, Gender = fo.Gender, Introduce = fo.Introduce };
        if (change.FirstOfficerEnabled is { } enabled)
        {
            firstOfficer.Enabled = enabled;
            values.Add(SettingValue.Of(KeyPath(Keys.FirstOfficerEnabled), enabled));
        }

        if (change.FirstOfficerName is not null)
        {
            var raw = change.FirstOfficerName.Trim();
            var name = FirstOfficerConfig.CleanName(raw);
            if (raw.Length > 0 && (name.Length == 0 || FirstOfficerNames.AddressWord(name) is null))
            {
                return SettingsResult.Fail(Loc.T("settings.smalltalk.nameInvalid"));
            }

            firstOfficer.Name = name;
            values.Add(SettingValue.Of(KeyPath(Keys.FirstOfficerName), name));
        }

        if (change.FirstOfficerGender is not null)
        {
            var gender = FirstOfficerConfig.NormalizeGender(change.FirstOfficerGender);
            firstOfficer.Gender = gender;
            values.Add(SettingValue.Of(KeyPath(Keys.FirstOfficerGender), gender));
        }

        if (change.Introduce is { } introduce)
        {
            firstOfficer.Introduce = introduce;
            values.Add(SettingValue.Of(KeyPath(Keys.FirstOfficerIntroduce), introduce));
        }

        // Stimme des First Officers: voices.kopilot in voice-settings.json ("" = automatisch), gilt sofort
        string? fixedVoice = null;
        var loop = _services.GetService<VoiceLoop>();
        if (change.FirstOfficerVoice is not null)
        {
            var voice = change.FirstOfficerVoice.Trim();
            voice = voice.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : voice;
            if (voice.Length > 0)
            {
                // Kokoro (nur Englisch) oder mehrsprachig (Supertonic, wenn eingeschaltet)
                var normalized = FirstOfficerCasting.NormalizeVoice(voice);
                if (normalized is null || (MultilingualVoices.IsMultilingual(normalized) && EngineVoices().All(v => v.Key != normalized)))
                {
                    return SettingsResult.Fail(Loc.T("settings.smalltalk.voiceInvalid", voice));
                }

                voice = normalized;
            }

            var currentVoice = loop?.Store.Load().Settings.Voices.Kopilot ?? "";
            if (!string.Equals(currentVoice, voice, StringComparison.Ordinal))
            {
                if (loop is null)
                {
                    return SettingsResult.Fail(Loc.T("settings.voice.trayOnly"));
                }

                fixedVoice = voice;
            }
        }

        if (values.Count == 0 && fixedVoice is null)
        {
            return new SettingsResult(true, Loc.T("settings.nothingChanged"));
        }

        if (fixedVoice is not null)
        {
            try
            {
                loop!.SetFixedVoice(fixedVoice);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return SettingsResult.Fail(Loc.T("settings.file.notWritten", VoiceSettingsStore.FileName, ex.Message));
            }

            if (values.Count == 0)
            {
                return new SettingsResult(true, Loc.T("settings.smalltalk.voiceSaved", fixedVoice.Length > 0 ? fixedVoice : Loc.T("settings.smalltalk.voiceAutomatic")));
            }
        }

        if (!Write(values, out var error))
        {
            return SettingsResult.Fail(error);
        }

        var config = SmalltalkConfig.From(options);
        service.Update(config, FirstOfficerConfig.From(firstOfficer));
        _log.LogInformation(Loc.Log("log.settings.smalltalkChanged"), config.ToString(), firstOfficer.Enabled, firstOfficer.Name.Length > 0 ? firstOfficer.Name : "random");
        var saved = config.Enabled ? Loc.T("settings.smalltalk.saved", SmalltalkProviders.DisplayName(config.Provider)) : Loc.T("settings.smalltalk.savedOff");
        return new SettingsResult(true, config.Problem() is { } problem ? $"{saved} {problem}" : saved);
    }

    /// <summary>Probefrage an das Sprachmodell (Knopf „Testen“) – zählt wie jede Anfrage gegen die Grenze pro Minute.</summary>
    public async Task<object> TestSmalltalkAsync(CancellationToken ct)
    {
        if (_services.GetService<SmalltalkService>() is not { } service)
        {
            return new { ok = false, error = Loc.T("settings.smalltalk.unavailable") };
        }

        if (!service.Config.Enabled)
        {
            return new { ok = false, error = Loc.T("smalltalk.config.off") };
        }

        var name = _services.GetService<VoiceLoop>()?.Status.FirstOfficer?.Name ?? service.FirstOfficer.Name;
        var question = new SmalltalkQuestion("Please say hello to the captain in one short sentence.", "en", name.Length > 0 ? name : null, SmalltalkContext.Empty);
        var answer = await service.AskAsync(question, ct);
        service.ResetHistory();
        return answer.Ok
            ? new { ok = true, text = answer.Text, ms = answer.ElapsedMs, error = (string?)null }
            : new { ok = false, text = answer.Text, ms = answer.ElapsedMs, error = answer.Warning ?? answer.Reason };
    }

    /// <summary>
    /// Stimme des First Officers probehören (Knopf „Probehören“): die gewählte, bei „Automatisch“ die des aktuellen First Officers.
    /// </summary>
    public async Task<object> PreviewFirstOfficerVoiceAsync(string? voice, CancellationToken ct)
    {
        if (_services.GetService<Speech.CalloutSpeaker>() is not { } speaker)
        {
            return new { ok = false, error = Loc.T("dashdata.speech.trayOnly"), voice = (string?)null };
        }

        var status = _services.GetService<VoiceLoop>()?.Status.FirstOfficer;
        var wanted = (voice ?? "").Trim();
        if (wanted.Length == 0 || wanted.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            wanted = status?.Voice ?? FirstOfficerCasting.FemaleVoices[0];
        }

        wanted = FirstOfficerCasting.NormalizeVoice(wanted) ?? wanted;
        var name = status?.Name ?? (_services.GetService<SmalltalkService>()?.FirstOfficer.Name is { Length: > 0 } fixedName ? fixedName : null);
        var error = await speaker.PreviewVoiceAsync(wanted, FirstOfficerCasting.PreviewText(name), SmalltalkVoices.English, ct);

        // Mehrsprachige Stimme: danach dieselbe Stimme in der Oberflächensprache (so klingt der Smalltalk)
        var language = Loc.Language.Trim().ToLowerInvariant();
        if (error is null && language != SmalltalkVoices.English && MultilingualVoices.Find(wanted) is { } multilingual
            && MultilingualVoices.Speaks(multilingual, language))
        {
            error = await speaker.PreviewVoiceAsync(wanted, FirstOfficerCasting.Introduction(name ?? "Alex", language), language, ct);
        }

        return new { ok = error is null, error, voice = wanted };
    }

    /// <summary>Smalltalk und First Officer für die Einstellungsseite – der Schlüssel nur als „gesetzt / nicht gesetzt“.</summary>
    private object SmalltalkView()
    {
        if (_services.GetService<SmalltalkService>() is not { } service)
        {
            return new { available = false };
        }

        var c = service.Config;
        var fo = service.FirstOfficer;
        var st = service.Status;
        var voice = _services.GetService<VoiceLoop>()?.Status;
        return new
        {
            available = true,
            provider = c.Provider,
            providers = SmalltalkProviders.All,
            providerName = SmalltalkProviders.DisplayName(c.Provider),
            model = c.Model,
            effectiveModel = c.EffectiveModel,
            defaultModel = SmalltalkConfig.DefaultClaudeModel,
            url = c.Url,
            host = c.Enabled ? c.Host : null,
            apiKey = new { set = c.HasApiKey },
            maxReplyWords = c.MaxReplyWords,
            requestsPerMinute = c.RequestsPerMinute,
            timeoutSec = c.TimeoutSec,
            reasoning = c.Reasoning,
            reasoningOptions = SmalltalkReasoning.All,
            reasoningEffort = c.ReasoningEffort ?? c.ClaudeThinking?.Effort,
            maxTokens = c.MaxTokens,
            problem = c.Enabled ? c.Problem() : null,
            status = new
            {
                requestsLastMinute = st.RequestsLastMinute,
                lastUtc = st.LastUtc,
                lastOk = st.LastOk,
                lastError = st.LastError,
                lastMs = st.LastMs,
            },
            note = voice?.SmalltalkNote,
            language = voice?.SmalltalkLanguage,
            firstOfficer = new
            {
                enabled = fo.Enabled,
                name = fo.Name,
                gender = fo.Gender,
                introduce = fo.Introduce,
                voice = FixedVoice(),
                voices = FirstOfficerCasting.Voices
                    .Select(v => new { id = v, gender = FirstOfficerCasting.GenderOfVoice(v) == Speech.VoiceGender.Female ? "female" : "male", engine = "kokoro", name = v })
                    .Concat(EngineVoices().Select(v => new { id = v.Key, gender = v.Gender == Speech.VoiceGender.Female ? "female" : "male", engine = v.Engine, name = v.Id })),
                current = voice?.FirstOfficer is { } p
                    ? new { name = p.Name, gender = p.Gender, voice = p.Voice, smalltalkVoice = p.SmalltalkVoice, introduced = p.Introduced }
                    : null,
            },
        };
    }

    /// <summary>Mehrsprachige Stimmen, die der First Officer bekommen kann (nur Engines, die in dieser Instanz laufen).</summary>
    private IReadOnlyList<Speech.TtsVoice> EngineVoices() =>
        _services.GetService<Speech.TtsEngineRegistry>() is { } registry
            ? MultilingualVoices.All.Where(v => registry.Find(v.Key) is not null).ToList()
            : [];

    /// <summary>Feste Stimme des First Officers aus voice-settings.json ("" = automatisch).</summary>
    private string FixedVoice()
    {
        try
        {
            return _services.GetService<VoiceLoop>()?.Store.Load().Settings.Voices.Kopilot ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string ModuleState(string label, bool on) => on ? Loc.T("settings.modules.on", label) : Loc.T("settings.modules.off", label);

    private static object TunnelResult(string state, string message) => new { state, message, checkedUtc = DateTimeOffset.UtcNow };

    private string RestartMessage(string text)
    {
        var pending = PendingRestart();
        return pending.Count == 0 ? text + "." : Loc.T("settings.result.afterRestart", text);
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
            _log.LogWarning(Loc.Log("log.settings.notSaved"), ex.Message);
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

    /// <summary>Gesetzte Umgebungsvariablen, die Werte der Seite überschreiben – auch unter dem alten Namen Kopilot__….</summary>
    private static IReadOnlyList<string> EnvironmentOverrides() =>
        OverridableKeys
            .SelectMany(k => new[] { DuopiloOptions.Section, AppNames.Legacy.ConfigSection }
                .Select(section => $"{section}__{k.Replace(":", "__", StringComparison.Ordinal)}"))
            .Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            .ToList();

    private static string[] KeyPath(string key) => [.. Root, .. key.Split(':')];

    private static string Bool(bool value) => value ? "true" : "false";

    private static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');
}

/// <summary>Änderung auf der Einstellungsseite: First Officer und Smalltalk. null = unverändert.</summary>
/// <param name="ApiKey">Neuer Schlüssel; null oder "" = unverändert.</param>
public sealed record SmalltalkChange(
    string? Provider = null,
    string? Model = null,
    string? Url = null,
    string? ApiKey = null,
    bool RemoveApiKey = false,
    int? MaxReplyWords = null,
    int? RequestsPerMinute = null,
    bool? FirstOfficerEnabled = null,
    string? FirstOfficerName = null,
    string? FirstOfficerGender = null,
    bool? Introduce = null,
    string? Reasoning = null,
    string? FirstOfficerVoice = null);
