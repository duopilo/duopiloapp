using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kopilot.Localization;

/// <summary>A language Kopilot can show: code ("en", "de") and its own name ("English", "Deutsch").</summary>
public sealed record LanguageInfo(string Code, string Name);

/// <summary>
/// Texts for the dashboard, tool messages (message/note fields), tray and logs, from one JSON file per language
/// (<c>src/Kopilot/i18n/&lt;code&gt;.json</c>, embedded in Kopilot.dll). English is the default and the fallback for missing keys.
/// <para>
/// What stays English regardless of the setting (like a real crew, SOP): callouts, checklists, FMA calls, warnings and radio
/// (ATC templates) – everything that is spoken or is phraseology. Those texts never go through <see cref="T"/>.
/// </para>
/// <para>
/// Logs are always English, whatever the setting (issues, international contributors): keys starting with "log." exist only in
/// en.json, <see cref="Log"/> always returns the English template, and translated texts that end up in a log line as an argument
/// are swapped for their English twin (<see cref="ToEnglish"/>, done by the file logger).
/// </para>
/// <para>
/// Placeholders are named: <c>{radius}</c> or <c>{radius:0.0}</c> (format with the invariant culture). <see cref="T"/> maps its
/// arguments to the names in the order they first appear in the <b>English</b> text, so a translation may reorder them.
/// </para>
/// <para>
/// Optional: extra or corrected translations in <c>%LOCALAPPDATA%\Kopilot\i18n\&lt;code&gt;.json</c> (key by key on top of the
/// embedded file) – for trying a translation without building Kopilot.
/// </para>
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "en";

    /// <summary>Key with the language's own name in every language file.</summary>
    public const string NameKey = "language.name";

    /// <summary>Prefix of log texts (ILogger templates, crash reports). English only – translations of these keys are ignored.</summary>
    public const string LogPrefix = "log.";

    /// <summary>Size of one generation of <see cref="ToEnglish"/> twins (two generations are kept).</summary>
    private const int TwinGeneration = 16384;

    private const string ResourcePrefix = "Kopilot.i18n.";

    private static readonly AsyncLocal<string?> ScopedLanguage = new();
    private static readonly object TwinGate = new();
    private static volatile Catalog _catalog = Catalog.Load(null);
    private static volatile string _language = DefaultLanguage;
    private static volatile ConcurrentDictionary<string, string> _twins = new(StringComparer.Ordinal);
    private static volatile ConcurrentDictionary<string, string> _oldTwins = new(StringComparer.Ordinal);

    /// <summary>Raised after <see cref="SetLanguage"/> changed the language (e.g. the tray menu rebuilds its texts).</summary>
    public static event Action<string>? LanguageChanged;

    /// <summary>Language in use (for this async flow, if a test set one with <see cref="Use"/>).</summary>
    public static string Language => ScopedLanguage.Value ?? _language;

    /// <summary>Languages with a file, English first.</summary>
    public static IReadOnlyList<LanguageInfo> Available => _catalog.Languages;

    /// <summary>Folder with extra translation files, if one was loaded.</summary>
    public static string? OverrideDirectory => _catalog.OverrideDirectory;

    /// <summary>
    /// Startup: language from "Kopilot:Language" and extra translation files from the data folder. Without a setting the language
    /// stays as it is (English by default).
    /// </summary>
    public static void Configure(IConfiguration configuration)
    {
        var dataDirectory = (configuration.GetSection(KopilotOptions.Section).Get<KopilotOptions>() ?? new KopilotOptions()).ResolveDataDirectory();
        LoadOverrides(Path.Combine(dataDirectory, "i18n"));
        var language = configuration[$"{KopilotOptions.Section}:{nameof(KopilotOptions.Language)}"];
        if (!string.IsNullOrWhiteSpace(language))
        {
            SetLanguage(language);
        }
    }

    /// <summary>Loads extra translation files (&lt;code&gt;.json) from <paramref name="directory"/>, if it exists.</summary>
    public static void LoadOverrides(string? directory)
    {
        _catalog = Catalog.Load(directory);
        if (!_catalog.Has(_language))
        {
            _language = DefaultLanguage;
        }
    }

    /// <summary>Normalizes a language code ("de-DE" → "de"). Unknown or empty → null.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var c = code.Trim().Replace('_', '-').ToLowerInvariant();
        var catalog = _catalog;
        if (catalog.Has(c))
        {
            return c;
        }

        var dash = c.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && catalog.Has(c[..dash]) ? c[..dash] : null;
    }

    /// <summary>Switches the language for the whole process. Unknown codes fall back to English. Returns the code in use.</summary>
    public static string SetLanguage(string? code)
    {
        var next = Normalize(code) ?? DefaultLanguage;
        var changed = next != _language;
        _language = next;
        if (changed)
        {
            LanguageChanged?.Invoke(next);
        }

        return next;
    }

    /// <summary>
    /// Language for the current async flow only (tests, or one request rendered in another language). Dispose restores the previous one.
    /// </summary>
    public static IDisposable Use(string code)
    {
        var previous = ScopedLanguage.Value;
        ScopedLanguage.Value = Normalize(code) ?? DefaultLanguage;
        return new Scope(previous);
    }

    /// <summary>Text in the selected language (fallback English, then the key itself), placeholders filled in.</summary>
    public static string T(string key, params object?[] args)
    {
        var language = Language;
        var text = Format(key, Lookup(key, language), args);
        if (language != DefaultLanguage)
        {
            RememberTwin(text, Format(key, Lookup(key, DefaultLanguage), args));
        }

        return text;
    }

    /// <summary>Text in English, whatever the setting.</summary>
    public static string En(string key, params object?[] args) => Format(key, Lookup(key, DefaultLanguage), args);

    /// <summary>
    /// Message template for ILogger, always English: <c>_log.LogWarning(Loc.Log("log.sim.connectFailed"), message)</c>.
    /// </summary>
    public static string Log(string key) => Lookup(key, DefaultLanguage);

    /// <summary>
    /// The English version of a text that <see cref="T"/> produced in another language (exact match, recent texts only);
    /// anything else comes back unchanged. Used for log arguments, so a log line stays English even if it quotes an alert message,
    /// a refusal reason or an error that the user sees translated.
    /// </summary>
    public static string? ToEnglish(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return _twins.TryGetValue(text, out var en) || _oldTwins.TryGetValue(text, out en) ? en : text;
    }

    /// <summary>True if the key exists in English.</summary>
    public static bool Has(string key) => _catalog.Get(DefaultLanguage)?.ContainsKey(key) == true;

    /// <summary>All keys with <paramref name="prefix"/> in <paramref name="language"/>, missing ones filled from English (for the dashboard).</summary>
    public static IReadOnlyDictionary<string, string> Strings(string prefix, string? language = null)
    {
        var catalog = _catalog;
        var lang = Normalize(language) ?? Language;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in new[] { catalog.Get(DefaultLanguage), lang == DefaultLanguage ? null : catalog.Get(lang) })
        {
            if (source is null)
            {
                continue;
            }

            foreach (var (key, value) in source)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>The raw strings of one language file (embedded plus overrides), without fallback. Null if there is no such language.
    /// Translations never contain "log." keys (logs are English only).</summary>
    public static IReadOnlyDictionary<string, string>? Raw(string language) => _catalog.Get(Normalize(language) ?? language);

    /// <summary>Placeholder names in order of first appearance: "{a} {b:0.0} {a}" → a, b.</summary>
    public static IReadOnlyList<string> Placeholders(string template)
    {
        var names = new List<string>();
        foreach (var (name, _) in Holes(template))
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Placeholder names in order, with repeats (for comparing log templates).</summary>
    public static IReadOnlyList<string> PlaceholderSequence(string template) => Holes(template).Select(h => h.Name).ToList();

    private static void RememberTwin(string text, string english)
    {
        if (text == english)
        {
            return;
        }

        var twins = _twins;
        twins[text] = english;
        if (twins.Count > TwinGeneration)
        {
            // Two generations: a text from just before the switch is still found
            lock (TwinGate)
            {
                if (ReferenceEquals(twins, _twins))
                {
                    _oldTwins = twins;
                    _twins = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
                }
            }
        }
    }

    private static string Lookup(string key, string language)
    {
        var catalog = _catalog;
        if (catalog.Get(language) is { } strings && strings.TryGetValue(key, out var text))
        {
            return text;
        }

        return catalog.Get(DefaultLanguage) is { } en && en.TryGetValue(key, out var english) ? english : key;
    }

    private static string Format(string key, string template, object?[] args)
    {
        if (template.IndexOf('{') < 0 && template.IndexOf('}') < 0)
        {
            return template;
        }

        // Names from the English text: the call site passes arguments in that order
        var english = _catalog.Get(DefaultLanguage) is { } en && en.TryGetValue(key, out var e) ? e : template;
        var names = Placeholders(english);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count && i < args.Length; i++)
        {
            values[names[i]] = args[i];
        }

        var sb = new StringBuilder(template.Length + 32);
        var pos = 0;
        while (pos < template.Length)
        {
            var c = template[pos];
            if (c == '{' && pos + 1 < template.Length && template[pos + 1] == '{')
            {
                sb.Append('{');
                pos += 2;
                continue;
            }

            if (c == '}' && pos + 1 < template.Length && template[pos + 1] == '}')
            {
                sb.Append('}');
                pos += 2;
                continue;
            }

            if (c == '{')
            {
                var end = template.IndexOf('}', pos + 1);
                if (end > pos)
                {
                    var hole = template[(pos + 1)..end];
                    var colon = hole.IndexOf(':', StringComparison.Ordinal);
                    var name = (colon >= 0 ? hole[..colon] : hole).Trim();
                    var format = colon >= 0 ? hole[(colon + 1)..] : null;
                    if (values.TryGetValue(name, out var value))
                    {
                        sb.Append(value switch
                        {
                            null => "",
                            IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
                            _ => value.ToString(),
                        });
                    }
                    else
                    {
                        sb.Append(template, pos, end - pos + 1);
                    }

                    pos = end + 1;
                    continue;
                }
            }

            sb.Append(c);
            pos++;
        }

        return sb.ToString();
    }

    private static IEnumerable<(string Name, string? Format)> Holes(string template)
    {
        var pos = 0;
        while (pos < template.Length)
        {
            var c = template[pos];
            if ((c == '{' || c == '}') && pos + 1 < template.Length && template[pos + 1] == c)
            {
                pos += 2;
                continue;
            }

            if (c == '{')
            {
                var end = template.IndexOf('}', pos + 1);
                if (end > pos)
                {
                    var hole = template[(pos + 1)..end];
                    var colon = hole.IndexOf(':', StringComparison.Ordinal);
                    yield return ((colon >= 0 ? hole[..colon] : hole).Trim(), colon >= 0 ? hole[(colon + 1)..] : null);
                    pos = end + 1;
                    continue;
                }
            }

            pos++;
        }
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => ScopedLanguage.Value = previous;
    }

    /// <summary>All language files: embedded ones plus overrides from a folder.</summary>
    private sealed class Catalog
    {
        private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _languages;

        private Catalog(Dictionary<string, Dictionary<string, string>> languages, string? overrideDirectory)
        {
            _languages = languages.ToFrozenDictionary(l => l.Key, l => l.Value.ToFrozenDictionary(StringComparer.Ordinal), StringComparer.Ordinal);
            OverrideDirectory = overrideDirectory;
            Languages = _languages
                .Select(l => new LanguageInfo(l.Key, l.Value.TryGetValue(NameKey, out var name) ? name : l.Key))
                .OrderBy(l => l.Code == DefaultLanguage ? 0 : 1)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<LanguageInfo> Languages { get; }

        public string? OverrideDirectory { get; }

        public bool Has(string code) => _languages.ContainsKey(code);

        public FrozenDictionary<string, string>? Get(string code) => _languages.TryGetValue(code, out var strings) ? strings : null;

        public static Catalog Load(string? overrideDirectory)
        {
            var languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var assembly = typeof(Loc).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }

                var code = resource[ResourcePrefix.Length..^".json".Length].ToLowerInvariant();
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is not null)
                {
                    Merge(languages, code, Read(stream, code));
                }
            }

            string? loaded = null;
            if (!string.IsNullOrWhiteSpace(overrideDirectory) && Directory.Exists(overrideDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(overrideDirectory, "*.json"))
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        var code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        Merge(languages, code, Read(stream, code));
                        loaded = overrideDirectory;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        // A broken extra file must not take the embedded texts down with it
                    }
                }
            }

            return new Catalog(languages, loaded);
        }

        private static void Merge(Dictionary<string, Dictionary<string, string>> languages, string code, Dictionary<string, string> strings)
        {
            if (!languages.TryGetValue(code, out var target))
            {
                languages[code] = target = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            foreach (var (key, value) in strings)
            {
                target[key] = value;
            }
        }

        /// <summary>
        /// Flat JSON object: "key": "text". Keys starting with "_" are comments for translators. "log." keys only count in English.
        /// </summary>
        private static Dictionary<string, string> Read(Stream stream, string code)
        {
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.StartsWith('_') || property.Value.ValueKind != JsonValueKind.String
                    || (code != DefaultLanguage && property.Name.StartsWith(LogPrefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                result[property.Name] = property.Value.GetString() ?? "";
            }

            return result;
        }
    }
}
