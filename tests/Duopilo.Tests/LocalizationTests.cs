using System.Text.Json;
using System.Text.RegularExpressions;
using Kopilot.Dashboard;
using Kopilot.Hosting;
using Kopilot.Localization;
using Microsoft.Extensions.Logging;

namespace Kopilot.Tests;

/// <summary>
/// Sprachdateien (src/Kopilot/i18n): Englisch ist vollständig, Übersetzungen passen zu den Platzhaltern, alle Schlüssel aus
/// Code und Dashboard gibt es, und Logs bleiben unabhängig von der Einstellung Englisch.
/// </summary>
public class LocalizationTests
{
    private static IReadOnlyDictionary<string, string> English => Loc.Raw("en") ?? throw new InvalidOperationException("en.json fehlt");

    [Fact]
    public void EnglishIsDefaultAndGermanIsAvailable()
    {
        Assert.Equal("en", Loc.DefaultLanguage);
        Assert.Equal("en", Loc.Available[0].Code);
        Assert.Equal("English", Loc.Available[0].Name);
        Assert.Contains(Loc.Available, l => l.Code == "de" && l.Name == "Deutsch");
    }

    [Fact]
    public void EnglishTextsAreNotEmpty()
    {
        var empty = English.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ToList();
        Assert.True(empty.Count == 0, "Leere englische Texte: " + string.Join(", ", empty));
    }

    [Fact]
    public void EveryTranslatedKeyExistsInEnglish()
    {
        foreach (var language in Loc.Available.Where(l => l.Code != "en"))
        {
            var orphans = Loc.Raw(language.Code)!.Keys.Where(k => !English.ContainsKey(k)).ToList();
            Assert.True(orphans.Count == 0, $"{language.Code}: Schlüssel ohne englischen Text: {string.Join(", ", orphans)}");
        }
    }

    [Fact]
    public void GermanIsComplete()
    {
        // log.-Texte gibt es nur auf Englisch
        var missing = English.Keys.Where(k => !k.StartsWith(Loc.LogPrefix, StringComparison.Ordinal) && !Loc.Raw("de")!.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Ohne deutsche Übersetzung: " + string.Join(", ", missing));
    }

    [Fact]
    public void TranslationsUseTheSamePlaceholders()
    {
        var problems = new List<string>();
        foreach (var language in Loc.Available.Where(l => l.Code != "en"))
        {
            foreach (var (key, text) in Loc.Raw(language.Code)!)
            {
                if (!English.TryGetValue(key, out var english))
                {
                    continue;
                }

                if (!Loc.Placeholders(english).Order(StringComparer.Ordinal).SequenceEqual(Loc.Placeholders(text).Order(StringComparer.Ordinal)))
                {
                    problems.Add($"{language.Code} {key}: {{{string.Join("},{", Loc.Placeholders(text))}}} statt {{{string.Join("},{", Loc.Placeholders(english))}}}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Logs bleiben Englisch: Übersetzungsdateien enthalten keine log.-Schlüssel (sie würden ignoriert).</summary>
    [Fact]
    public void TranslationFilesHaveNoLogKeys()
    {
        foreach (var resource in typeof(Loc).Assembly.GetManifestResourceNames().Where(r => r.StartsWith("Kopilot.i18n.", StringComparison.Ordinal) && !r.EndsWith(".en.json", StringComparison.Ordinal)))
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream(resource)!;
            using var doc = JsonDocument.Parse(stream);
            var logKeys = doc.RootElement.EnumerateObject().Select(p => p.Name).Where(k => k.StartsWith(Loc.LogPrefix, StringComparison.Ordinal)).ToList();
            Assert.True(logKeys.Count == 0, $"{resource}: log.-Schlüssel gehören nur in en.json: {string.Join(", ", logKeys)}");
        }
    }

    [Fact]
    public void LogTemplatesAreAlwaysEnglish()
    {
        using (Loc.Use("de"))
        {
            Assert.Equal(English["log.settings.languageChanged"], Loc.Log("log.settings.languageChanged"));
        }
    }

    [Fact]
    public void TranslatedTextsHaveAnEnglishTwin()
    {
        using (Loc.Use("de"))
        {
            var german = Loc.T("tools.traffic.none", "42");
            Assert.Equal("Kein Verkehr im Umkreis von 42 NM.", german);
            Assert.Equal("No traffic within 42 NM.", Loc.ToEnglish(german));
        }

        Assert.Equal("ICAO EDDL", Loc.ToEnglish("ICAO EDDL"));
        Assert.Null(Loc.ToEnglish(null));
    }

    /// <summary>Auch wenn ein Argument ein übersetzter Text ist (Alert-Erklärung, Grund, Fehler), steht in der Logdatei Englisch.</summary>
    [Fact]
    public void FileLogIsEnglishWhateverTheLanguage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kopilot-logtest-" + Guid.NewGuid().ToString("N"));
        var buffer = new LogBuffer();
        try
        {
            using (var provider = new FileLoggerProvider(directory, "test.log", buffer))
            using (Loc.Use("de"))
            {
                var logger = provider.CreateLogger("Kopilot.Tests.Log");
                var message = Loc.T("tools.traffic.none", "17");
                logger.LogWarning(Loc.Log("log.atc.airspaceMessage"), message);
                logger.LogError(new InvalidOperationException(Loc.T("tools.traffic.none", "18")), Loc.Log("log.atc.airspaceMessage"), "x");
            }

            var lines = buffer.Tail(2);
            Assert.Equal("Airspace data: No traffic within 17 NM.", lines[0].Message);
            Assert.Contains("InvalidOperationException: No traffic within 18 NM.", lines[1].Message);
            Assert.DoesNotContain("Kein Verkehr", string.Join("\n", lines.Select(l => l.Message)));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void LogRenderingMatchesTheLogger()
    {
        Assert.Equal("a 1.5 (null) x, y {Literal}", EnglishLog.Render("a {N:0.0} {Missing} {List} {{Literal}}", [1.5, null, new[] { "x", "y" }]));
    }

    [Fact]
    public void FormatsNamedPlaceholdersAndFallsBackToEnglish()
    {
        using (Loc.Use("en"))
        {
            Assert.Equal("No traffic within 40 NM.", Loc.T("tools.traffic.none", "40"));
        }

        using (Loc.Use("de"))
        {
            Assert.Equal("Kein Verkehr im Umkreis von 40 NM.", Loc.T("tools.traffic.none", "40"));
            Assert.Equal("No traffic within 40 NM.", Loc.En("tools.traffic.none", "40"));
            Assert.Equal("missing.key.for.test", Loc.T("missing.key.for.test"));
        }

        using (Loc.Use("xx-YY"))
        {
            Assert.Equal("en", Loc.Language);
        }

        using (Loc.Use("de-DE"))
        {
            Assert.Equal("de", Loc.Language);
        }
    }

    [Fact]
    public void ScopedLanguageDoesNotLeak()
    {
        var before = Loc.Language;
        using (Loc.Use(before == "de" ? "en" : "de"))
        {
            Assert.NotEqual(before, Loc.Language);
        }

        Assert.Equal(before, Loc.Language);
    }

    [Fact]
    public void PlaceholderParsingHandlesFormatsAndEscapes()
    {
        Assert.Equal(new[] { "a", "b" }, Loc.Placeholders("{a} {b:0.0} {a} {{literal}}"));
        Assert.Equal(new[] { "a", "b", "a" }, Loc.PlaceholderSequence("{a} {b:0.0} {a}"));
    }

    [Fact]
    public void DashboardPageEmbedsTheTexts()
    {
        var html = DashboardEndpoints.Page();
        Assert.NotNull(html);
        Assert.DoesNotContain(DashboardEndpoints.I18nMarker, html);
        Assert.Contains("<script id=\"i18n\" type=\"application/json\">", html);
    }

    /// <summary>Jeder Schlüssel, den dashboard.html verwendet (data-i18n…, t("…")), steht in en.json.</summary>
    [Fact]
    public void DashboardKeysExist()
    {
        using var stream = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream("Kopilot.Dashboard.dashboard.html");
        Assert.NotNull(stream);
        var html = new StreamReader(stream).ReadToEnd();
        var keys = Regex.Matches(html, @"data-i18n(?:-[a-z-]+)?=""([^""]+)""").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(html, @"\bt\(\s*[""']([a-zA-Z0-9_.]+)[""']").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        var strings = Loc.Strings(DashboardEndpoints.I18nPrefix, "en");
        var missing = keys.Where(k => !strings.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Fehlende Dashboard-Schlüssel: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Jeder Schlüssel aus Loc.T/Loc.En/Loc.Log im Quelltext steht in en.json, Loc.Log nimmt nur "log."-Schlüssel, und die Zahl der
    /// Argumente passt zu den Platzhaltern im englischen Text.
    /// </summary>
    [Fact]
    public void CodeKeysExistAndArgumentsMatch()
    {
        var src = SourceDirectory();
        if (src is null)
        {
            return; // Tests außerhalb des Repositorys (nur Binärdateien)
        }

        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(file);
            var name = Path.GetRelativePath(src, file);
            foreach (var call in CSharpCalls.Find(code, "Loc.T", "Loc.En", "Loc.Log"))
            {
                if (call.Args.Count == 0 || !CSharpCalls.TryLiteral(call.Args[0], out var key))
                {
                    continue; // Schlüssel aus einer Variable – nicht prüfbar
                }

                if (!English.TryGetValue(key, out var english))
                {
                    problems.Add($"{name}: Schlüssel \"{key}\" fehlt in en.json");
                    continue;
                }

                if (call.Method == "Loc.Log")
                {
                    if (!key.StartsWith(Loc.LogPrefix, StringComparison.Ordinal))
                    {
                        problems.Add($"{name}: Loc.Log(\"{key}\") – Log-Vorlagen beginnen mit \"{Loc.LogPrefix}\"");
                    }

                    continue;
                }

                if (call.Method == "Loc.T" && key.StartsWith(Loc.LogPrefix, StringComparison.Ordinal))
                {
                    problems.Add($"{name}: Loc.T(\"{key}\") – \"{Loc.LogPrefix}\"-Texte sind nur Englisch, also Loc.Log oder Loc.En");
                }

                var expected = Loc.Placeholders(english).Count;
                if (call.Args.Count - 1 != expected)
                {
                    problems.Add($"{name}: {call.Method}(\"{key}\") mit {call.Args.Count - 1} Argument(en), der Text hat {expected} Platzhalter");
                }
            }

            // ILogger-Aufrufe mit Loc.Log: Argumente nach der Vorlage = Platzhalter im englischen Text
            foreach (var call in CSharpCalls.Find(code, "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical"))
            {
                var index = call.Args.FindIndex(a => a.StartsWith("Loc.Log(", StringComparison.Ordinal));
                if (index < 0)
                {
                    continue;
                }

                var inner = CSharpCalls.Find(call.Args[index], "Loc.Log").FirstOrDefault();
                if (inner is null || inner.Args.Count == 0 || !CSharpCalls.TryLiteral(inner.Args[0], out var key) || !English.TryGetValue(key, out var english))
                {
                    continue;
                }

                var expected = Loc.PlaceholderSequence(english).Count;
                var actual = call.Args.Count - index - 1;
                if (actual != expected)
                {
                    problems.Add($"{name}: {call.Method}(Loc.Log(\"{key}\")) mit {actual} Argument(en), die Vorlage hat {expected} Platzhalter");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string? SourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kopilot.slnx")))
            {
                var src = Path.Combine(dir.FullName, "src", "Kopilot");
                return Directory.Exists(src) ? src : null;
            }
        }

        return null;
    }
}

/// <summary>Findet Methodenaufrufe im C#-Quelltext und teilt ihre Argumente auf (Strings, Interpolation und Klammern berücksichtigt).</summary>
internal static class CSharpCalls
{
    public sealed record Call(string Method, List<string> Args);

    public static IEnumerable<Call> Find(string code, params string[] methods)
    {
        foreach (var method in methods)
        {
            // Davor kein Wortzeichen (ein Punkt ist erlaubt: _log.LogWarning)
            var pattern = new Regex(@"(?<!\w)" + Regex.Escape(method) + @"\s*\(");
            foreach (Match m in pattern.Matches(code))
            {
                if (IsInCommentOrString(code, m.Index))
                {
                    continue;
                }

                var open = m.Index + m.Length - 1;
                var args = SplitArgs(code, open, out var ok);
                if (ok)
                {
                    yield return new Call(method, args);
                }
            }
        }
    }

    /// <summary>"key" → key (nur einfache String-Literale).</summary>
    public static bool TryLiteral(string arg, out string value)
    {
        var m = Regex.Match(arg.Trim(), @"^""((?:[^""\\]|\\.)*)""$");
        value = m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        return m.Success;
    }

    private static List<string> SplitArgs(string code, int open, out bool ok)
    {
        var args = new List<string>();
        var depth = 0;
        var start = open + 1;
        var i = open;
        ok = false;
        while (i < code.Length)
        {
            var c = code[i];
            if (c is '"' or '\'' || (c is '$' or '@' && i + 1 < code.Length && code[i + 1] is '"' or '$' or '@'))
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                var nl = code.IndexOf('\n', i);
                i = nl < 0 ? code.Length : nl;
                continue;
            }

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? code.Length : end + 2;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    var last = code[start..i].Trim();
                    if (last.Length > 0 || args.Count > 0)
                    {
                        args.Add(last);
                    }

                    ok = true;
                    return args;
                }
            }
            else if (c == ',' && depth == 1)
            {
                args.Add(code[start..i].Trim());
                start = i + 1;
            }

            i++;
        }

        return args;
    }

    /// <summary>Überspringt ein String- oder Zeichenliteral ab <paramref name="i"/> (inkl. $"", @"", $@"", """…""").</summary>
    private static int SkipLiteral(string code, int i)
    {
        var interpolated = false;
        var verbatim = false;
        while (i < code.Length && code[i] is '$' or '@')
        {
            interpolated |= code[i] == '$';
            verbatim |= code[i] == '@';
            i++;
        }

        if (i >= code.Length)
        {
            return i;
        }

        if (code[i] == '\'')
        {
            i++;
            while (i < code.Length && code[i] != '\'')
            {
                i += code[i] == '\\' ? 2 : 1;
            }

            return i + 1;
        }

        // Raw string literal
        var quotes = 0;
        while (i + quotes < code.Length && code[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            var close = new string('"', quotes);
            var end = code.IndexOf(close, i + quotes, StringComparison.Ordinal);
            return end < 0 ? code.Length : end + quotes;
        }

        i++; // öffnendes "
        var braces = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (braces == 0)
            {
                if (!verbatim && c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    if (verbatim && i + 1 < code.Length && code[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                if (interpolated && c == '{')
                {
                    if (i + 1 < code.Length && code[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }

                    braces = 1;
                }

                i++;
                continue;
            }

            // In einem Ausdruck der Interpolation
            if (c is '"' or '\'' || (c is '$' or '@' && i + 1 < code.Length && code[i + 1] is '"' or '$' or '@'))
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c == '{')
            {
                braces++;
            }
            else if (c == '}')
            {
                braces--;
            }

            i++;
        }

        return i;
    }

    private static bool IsInCommentOrString(string code, int index)
    {
        var lineStart = code.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var line = code[lineStart..index];
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
        {
            return true;
        }

        // Grobe Prüfung auf "///" und Kommentarblöcke reicht für den Quelltext von Kopilot
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('*');
    }
}
