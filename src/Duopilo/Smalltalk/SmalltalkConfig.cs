using Kopilot.Localization;

namespace Kopilot.Smalltalk;

/// <summary>Anbieter für den Smalltalk.</summary>
public static class SmalltalkProviders
{
    public const string Off = "off";
    public const string Claude = "claude";
    public const string OpenAi = "openai";
    public const string Ollama = "ollama";

    public static readonly IReadOnlyList<string> All = [Off, Claude, OpenAi, Ollama];

    public static string Normalize(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        Claude or "anthropic" => Claude,
        OpenAi or "openai-compatible" or "openai_compatible" => OpenAi,
        Ollama => Ollama,
        _ => Off,
    };

    /// <summary>Anzeigename (Datenschutzhinweis, Log).</summary>
    public static string DisplayName(string provider) => Normalize(provider) switch
    {
        Claude => "Claude (Anthropic)",
        OpenAi => "OpenAI-compatible",
        Ollama => "Ollama",
        _ => "off",
    };
}

/// <summary>Gültige, bereinigte Einstellungen für den Smalltalk (aus <see cref="SmalltalkOptions"/>).</summary>
public sealed record SmalltalkConfig
{
    /// <summary>Schnell und günstig – für ein paar Sätze im Cockpit reicht das kleinste Modell.</summary>
    public const string DefaultClaudeModel = "claude-haiku-5-5";

    public const string ClaudeUrl = "https://api.anthropic.com";

    public const string OpenAiUrl = "https://api.openai.com/v1";

    public static SmalltalkConfig Off { get; } = new();

    public string Provider { get; init; } = SmalltalkProviders.Off;

    public string Model { get; init; } = "";

    public string Url { get; init; } = "";

    public string ApiKey { get; init; } = "";

    public int MaxReplyWords { get; init; } = 40;

    public int RequestsPerMinute { get; init; } = 6;

    public int TimeoutSec { get; init; } = 10;

    public int UnaddressedMaxWords { get; init; } = 12;

    public bool Enabled => Provider != SmalltalkProviders.Off;

    public bool HasApiKey => ApiKey.Length > 0;

    /// <summary>Modell, das wirklich angefragt wird.</summary>
    public string EffectiveModel => Model.Length > 0 ? Model : Provider == SmalltalkProviders.Claude ? DefaultClaudeModel : "";

    /// <summary>Basisadresse ohne Schrägstrich am Ende.</summary>
    public string EffectiveUrl => Provider switch
    {
        SmalltalkProviders.Claude => ClaudeUrl,
        SmalltalkProviders.OpenAi => Url.Length > 0 ? Url : OpenAiUrl,
        _ => Url,
    };

    /// <summary>Rechner, an den die Gespräche gehen (für den Datenschutzhinweis), null ohne gültige Adresse.</summary>
    public string? Host => Uri.TryCreate(EffectiveUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>Höchstzahl der Tokens in der Antwort – grob zwei pro Wort, damit der Satz nicht abreißt.</summary>
    public int MaxTokens => Math.Clamp((MaxReplyWords * 2) + 24, 64, 600);

    /// <summary>Was fehlt, damit Anfragen gehen (in der eingestellten Sprache) – null = vollständig.</summary>
    public string? Problem() => Provider switch
    {
        SmalltalkProviders.Off => null,
        SmalltalkProviders.Claude when !HasApiKey => Loc.T("smalltalk.config.keyMissing"),
        SmalltalkProviders.Ollama when Url.Length == 0 => Loc.T("smalltalk.config.urlMissing"),
        _ when !ValidUrl(EffectiveUrl) => Loc.T("smalltalk.config.urlInvalid", EffectiveUrl),
        _ when EffectiveModel.Length == 0 => Loc.T("smalltalk.config.modelMissing"),
        _ => null,
    };

    public static SmalltalkConfig From(SmalltalkOptions? o)
    {
        o ??= new SmalltalkOptions();
        return new SmalltalkConfig
        {
            Provider = SmalltalkProviders.Normalize(o.Provider),
            Model = (o.Model ?? "").Trim(),
            Url = NormalizeUrl(o.Url),
            ApiKey = (o.ApiKey ?? "").Trim(),
            MaxReplyWords = Math.Clamp(o.MaxReplyWords, 10, 150),
            RequestsPerMinute = Math.Clamp(o.RequestsPerMinute, 1, 30),
            TimeoutSec = Math.Clamp(o.TimeoutSec, 2, 30),
            UnaddressedMaxWords = Math.Clamp(o.UnaddressedMaxWords, 0, 40),
        };
    }

    public static string NormalizeUrl(string? url) => (url ?? "").Trim().TrimEnd('/');

    /// <summary>Absolute http(s)-Adresse ohne Benutzerdaten (http erlaubt: Ollama und lokale Server im eigenen Netz).</summary>
    public static bool ValidUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query);

    /// <summary>Ohne Schlüssel – für Log und Status.</summary>
    public override string ToString() => $"{Provider} {EffectiveModel} @ {Host ?? "-"} (key {(HasApiKey ? "set" : "not set")})";
}

/// <summary>Gültige Einstellungen für den First Officer (aus <see cref="FirstOfficerOptions"/>).</summary>
public sealed record FirstOfficerConfig
{
    public const string Auto = "auto";
    public const string Female = "female";
    public const string Male = "male";

    public bool Enabled { get; init; } = true;

    /// <summary>Fester Name ("" = pro Flug zufällig).</summary>
    public string Name { get; init; } = "";

    /// <summary>"auto", "female" oder "male".</summary>
    public string Gender { get; init; } = Auto;

    public bool Introduce { get; init; } = true;

    public static FirstOfficerConfig From(FirstOfficerOptions? o)
    {
        o ??= new FirstOfficerOptions();
        return new FirstOfficerConfig
        {
            Enabled = o.Enabled,
            Name = CleanName(o.Name),
            Gender = NormalizeGender(o.Gender),
            Introduce = o.Introduce,
        };
    }

    public static string NormalizeGender(string? gender) => gender?.Trim().ToLowerInvariant() switch
    {
        Female or "f" or "weiblich" or "w" => Female,
        Male or "m" or "männlich" or "maennlich" => Male,
        _ => Auto,
    };

    /// <summary>Name bereinigen: höchstens 24 Zeichen, nur Buchstaben, Leerzeichen, Bindestrich, Apostroph.</summary>
    public static string CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => char.IsLetter(c) || c is ' ' or '-' or '\'').ToArray()).Trim();
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 24 ? cleaned[..24].Trim() : cleaned;
    }
}
