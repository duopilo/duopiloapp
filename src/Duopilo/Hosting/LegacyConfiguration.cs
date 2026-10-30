using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Primitives;

namespace Duopilo.Hosting;

/// <summary>
/// Liest Einstellungen unter dem alten Abschnitt "Kopilot" (appsettings.Local.json, Umgebungsvariablen Kopilot__…,
/// Kommandozeile --Kopilot:…) zusätzlich als "Duopilo:…".
/// <para>
/// Wirkt je Quelle: Innerhalb einer Quelle gewinnt der neue Name, zwischen den Quellen bleibt die gewohnte Reihenfolge
/// (appsettings.json &lt; appsettings.Local.json &lt; Umgebungsvariablen &lt; Kommandozeile). Eine alte appsettings.Local.json
/// mit "Kopilot": { … } überschreibt also weiter die Vorgaben aus appsettings.json, die schon "Duopilo" heißen.
/// Die Einstellungsseite des Dashboards benennt den alten Abschnitt beim ersten Speichern um (LocalSettingsStore).
/// </para>
/// </summary>
public sealed class LegacySectionSource(IConfigurationSource inner, string legacySection, string section) : IConfigurationSource
{
    public IConfigurationSource Inner { get; } = inner;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new LegacySectionProvider(Inner.Build(builder), legacySection, section);

    /// <summary>Für Duopilo: alter Abschnitt "Kopilot" → "Duopilo".</summary>
    public static LegacySectionSource For(IConfigurationSource inner) =>
        new(inner, AppNames.Legacy.ConfigSection, DuopiloOptions.Section);

    /// <summary>appsettings.Local.json o. Ä. (absoluter Pfad), alter Abschnitt wird mitgelesen.</summary>
    public static LegacySectionSource Json(string path, bool optional = true, bool reloadOnChange = false)
    {
        var json = new JsonConfigurationSource { Path = path, Optional = optional, ReloadOnChange = reloadOnChange };
        json.ResolveFileProvider();
        return For(json);
    }

    /// <summary>Umgebungsvariablen, Kopilot__… wird mitgelesen.</summary>
    public static LegacySectionSource EnvironmentVariables() => For(new EnvironmentVariablesConfigurationSource());

    /// <summary>Kommandozeile, --Kopilot:… wird mitgelesen.</summary>
    public static LegacySectionSource CommandLine(string[] args) => For(new CommandLineConfigurationSource { Args = args });
}

internal sealed class LegacySectionProvider : ConfigurationProvider, IDisposable
{
    private readonly IConfigurationProvider _inner;
    private readonly string _legacyPrefix;
    private readonly string _section;
    private readonly IDisposable _reload;

    public LegacySectionProvider(IConfigurationProvider inner, string legacySection, string section)
    {
        _inner = inner;
        _legacyPrefix = legacySection + ConfigurationPath.KeyDelimiter;
        _section = section;
        _reload = ChangeToken.OnChange(_inner.GetReloadToken, () =>
        {
            Copy();
            OnReload();
        });
    }

    public override void Load()
    {
        _inner.Load();
        Copy();
    }

    public void Dispose()
    {
        _reload.Dispose();
        (_inner as IDisposable)?.Dispose();
    }

    public override string ToString() => $"{_inner} (+ {_legacyPrefix.TrimEnd(':')})";

    private void Copy()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Keys(_inner, null))
        {
            if (_inner.TryGet(key, out var value))
            {
                data[key] = value;
            }
        }

        foreach (var (key, value) in data.ToList())
        {
            // Neuer Name in derselben Quelle gewinnt
            if (key.StartsWith(_legacyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                data.TryAdd(ConfigurationPath.Combine(_section, key[_legacyPrefix.Length..]), value);
            }
            else if (LegacyLogCategory(key) is { } category)
            {
                // "Logging:LogLevel:Kopilot.Voice": "Debug" → Kategorie Duopilo.Voice
                data.TryAdd(LogLevelPrefix + _section + category, value);
            }
        }

        Data = data;
    }

    private const string LogLevelPrefix = "Logging:LogLevel:";

    /// <summary>Rest nach dem alten Namen bei "Logging:LogLevel:Kopilot…" (z. B. ".Voice" oder ""), sonst null.</summary>
    private string? LegacyLogCategory(string key)
    {
        var legacy = LogLevelPrefix + _legacyPrefix.TrimEnd(':');
        if (!key.StartsWith(legacy, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = key[legacy.Length..];
        return rest.Length == 0 || rest[0] == '.' ? rest : null;
    }

    private static IEnumerable<string> Keys(IConfigurationProvider provider, string? parent)
    {
        foreach (var child in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = parent is null ? child : ConfigurationPath.Combine(parent, child);
            yield return path;
            foreach (var nested in Keys(provider, path))
            {
                yield return nested;
            }
        }
    }
}
