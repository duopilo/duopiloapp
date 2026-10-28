using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Kopilot.Ecam;

/// <summary>Ein Eintrag der ECAM-Tabelle. Mit Gruppe = Titelzeile (z. B. "AUTO FLT AP OFF"), ohne = Aktions-/Memozeile.</summary>
public sealed record EcamEntry(string Code, string? Group, string Text)
{
    public bool IsTitle => Group is not null;

    public string Display => Group is null ? Text : $"{Group} {Text}".Trim();
}

public sealed record EcamLine(long Code, string Text, bool IsTitle, bool Known);

public sealed class EcamTable(IReadOnlyDictionary<string, EcamEntry> entries, DateTimeOffset loadedUtc, string origin)
{
    public IReadOnlyDictionary<string, EcamEntry> Entries { get; } = entries;

    public DateTimeOffset LoadedUtc { get; } = loadedUtc;

    /// <summary>"download" oder "cache".</summary>
    public string Origin { get; } = origin;

    public EcamLine Decode(long code)
    {
        var key = code.ToString("D9", System.Globalization.CultureInfo.InvariantCulture);
        return Entries.TryGetValue(key, out var e)
            ? new EcamLine(code, e.Display, e.IsTitle, true)
            : new EcamLine(code, $"Code {key}", false, false);
    }
}

/// <summary>
/// Lädt ECAM-Meldungstabellen aus dem FlyByWire-Quellcode (GPL-3.0) zur Laufzeit und hält sie lokal im Cache.
/// Die Tabelle wird bewusst nicht ins Kopilot-Repo kopiert.
/// </summary>
public sealed partial class EcamCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["fbw-a32nx"] = "https://raw.githubusercontent.com/flybywiresim/aircraft/master/fbw-a32nx/src/systems/shared/src/EwdMessages.ts",
    };

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(14);
    private static readonly HttpClient Http = CreateHttp();

    private readonly ConcurrentDictionary<string, EcamTable> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<EcamCatalog> _log;
    private readonly string _cacheDir;

    public EcamCatalog(IOptions<KopilotOptions> options, ILogger<EcamCatalog> log)
    {
        _log = log;
        _cacheDir = Path.Combine(options.Value.ResolveDataDirectory(), "ecam");
    }

    public static bool IsKnownFormat(string format) => Sources.ContainsKey(format);

    /// <summary>Liefert die Tabelle, falls geladen. Stößt sonst das Laden im Hintergrund an.</summary>
    public EcamTable? TryGet(string format)
    {
        if (_tables.TryGetValue(format, out var table))
        {
            return table;
        }

        // Nach einem Fehlschlag nicht im Monitoring-Takt erneut versuchen, sondern erst nach einer Pause
        if (Sources.ContainsKey(format) && (!_retryAfter.TryGetValue(format, out var retry) || DateTimeOffset.UtcNow >= retry))
        {
            _loading.GetOrAdd(format, f => Task.Run(() => LoadAsync(f)));
        }

        return null;
    }

    public string StatusFor(string format) =>
        _tables.TryGetValue(format, out var t) ? $"{t.Entries.Count} Meldungen ({t.Origin})"
        : _errors.TryGetValue(format, out var e) ? $"Fehler: {e}"
        : Sources.ContainsKey(format) ? "wird geladen"
        : "unbekanntes Format";

    private async Task LoadAsync(string format)
    {
        var cacheFile = Path.Combine(_cacheDir, format + ".ts");
        try
        {
            string? source = null;
            var origin = "cache";
            var cacheFresh = File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < RefreshAfter;

            if (!cacheFresh)
            {
                try
                {
                    source = await Http.GetStringAsync(Sources[format]);
                    Directory.CreateDirectory(_cacheDir);
                    await File.WriteAllTextAsync(cacheFile, source);
                    origin = "download";
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    _log.LogWarning("ECAM-Tabelle {Format} konnte nicht geladen werden: {Message}", format, ex.Message);
                }
            }

            if (source is null && File.Exists(cacheFile))
            {
                source = await File.ReadAllTextAsync(cacheFile);
                origin = "cache";
            }

            if (source is null)
            {
                _errors[format] = "kein Download und kein Cache";
                return;
            }

            var entries = Parse(source);
            _tables[format] = new EcamTable(entries, DateTimeOffset.UtcNow, origin);
            _errors.TryRemove(format, out _);
            _log.LogInformation("ECAM-Tabelle {Format}: {Count} Meldungen ({Origin})", format, entries.Count, origin);
        }
        catch (Exception ex)
        {
            _errors[format] = ex.Message;
            _log.LogError(ex, "ECAM-Tabelle {Format} fehlerhaft", format);
        }
        finally
        {
            // Nach einem Fehler darf ein späterer Aufruf es erneut versuchen – frühestens in 5 Minuten.
            if (!_tables.ContainsKey(format))
            {
                _retryAfter[format] = DateTimeOffset.UtcNow.AddMinutes(5);
                _loading.TryRemove(format, out _);
            }
        }
    }

    /// <summary>
    /// Liest die FBW-Tabellen aus dem TypeScript-Quelltext:
    ///   Gruppen:  'AUTO FLT$1': '\x1b&lt;2m\x1b4mAUTO FLT\x1bm',
    ///   Einträge: ['220000501', { group: 'AUTO FLT$1', text: ' AP OFF' }],
    /// </summary>
    public static IReadOnlyDictionary<string, EcamEntry> Parse(string source)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in GroupPattern().Matches(source))
        {
            groups[m.Groups[1].Value] = Clean(m.Groups[2].Value);
        }

        var entries = new Dictionary<string, EcamEntry>(StringComparer.Ordinal);
        foreach (Match m in EntryPattern().Matches(source))
        {
            var body = m.Groups[2].Value;
            var text = TextPattern().Match(body) is { Success: true } t ? Clean(t.Groups[1].Value) : "";
            string? group = null;
            if (GroupRefPattern().Match(body) is { Success: true } g)
            {
                group = groups.TryGetValue(g.Groups[1].Value, out var name) ? name : Clean(g.Groups[1].Value.Split('$')[0]);
            }

            entries[m.Groups[1].Value] = new EcamEntry(m.Groups[1].Value, group, text);
        }

        return entries;
    }

    /// <summary>Entfernt Farb-/Formatcodes (\x1b&lt;2m, \x1b4m, \x1bm …), JS-Escapes und überzählige Leerzeichen/Punkte.</summary>
    public static string Clean(string raw)
    {
        var s = EscapePattern().Replace(raw, "");
        s = s.Replace("\u001b", "").Replace("\\'", "'").Replace("\\\\", "\\");
        s = DotsPattern().Replace(s, " … ");
        s = SpacePattern().Replace(s, " ");
        return s.Trim();
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kopilot/1.0 (+https://github.com/Shin-Jin-Jin/Kopilot)");
        return http;
    }

    [GeneratedRegex(@"'([^'\n]+\$\d+)'\s*:\s*'((?:[^'\\\n]|\\.)*)'")]
    private static partial Regex GroupPattern();

    [GeneratedRegex(@"\[\s*'(\d{9})'\s*,\s*\{([^}]*)\}\s*\]")]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"text\s*:\s*'((?:[^'\\]|\\.)*)'")]
    private static partial Regex TextPattern();

    [GeneratedRegex(@"group\s*:\s*'([^']*)'")]
    private static partial Regex GroupRefPattern();

    // \x1b<2m, \x1b4m, \x1bm, \x1b)m, \x1b\'m … (Farbe, Unterstreichung, Box)
    [GeneratedRegex(@"\\x1b[^m\s]{0,3}m")]
    private static partial Regex EscapePattern();

    [GeneratedRegex(@"\.{3,}")]
    private static partial Regex DotsPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacePattern();
}
