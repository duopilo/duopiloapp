using System.Diagnostics;
using System.Net;
using System.Text;
using Duopilo.Smalltalk;
using Duopilo.Speech;
using Duopilo.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Duopilo.Tests;

/// <summary>
/// Supertonic 3 als deutsche Smalltalk-Stimme (M1, M2, F1) vor Piper: Katalog und gepinnte Downloads, Textaufbereitung wie im
/// Supertonic-Beispiel, Stimmwahl mit Rückfall auf Piper, Prüfsumme. Der Live-Test (echtes Modell, ~400 MB) läuft nur mit
/// DUOPILO_SUPERTONIC_LIVE=1.
/// </summary>
public sealed class SupertonicTests
{
    // ------------------------------------------------------------------ Katalog und Downloads

    [Fact]
    public void Katalog_M1_M2_maennlich_F1_weiblich_auf_Deutsch()
    {
        Assert.Equal(new[] { "M1", "M2" }, SupertonicSpeechEngine.Catalog.Where(v => v.Gender == VoiceGender.Male).Select(v => v.Id));
        Assert.Equal(new[] { "F1" }, SupertonicSpeechEngine.Catalog.Where(v => v.Gender == VoiceGender.Female).Select(v => v.Id));
        Assert.All(SupertonicSpeechEngine.Catalog, v =>
        {
            Assert.Equal("de", v.Language);
            Assert.Equal(SupertonicSpeechEngine.EngineId, v.Engine);
            Assert.True(SupertonicSpeechEngine.StyleFiles.ContainsKey(v.Id));
            Assert.Contains(v.Language, SupertonicText.Languages);
        });
        Assert.Equal(("supertonic", "M1"), TtsVoice.ParseKey("supertonic:M1"));
    }

    [Fact]
    public void Downloads_aus_der_gepinnten_Revision_mit_Pruefsumme()
    {
        var voice = SupertonicSpeechEngine.Catalog.First(v => v.Id == "F1");
        var files = SupertonicSpeechEngine.FilesFor(voice);

        Assert.Equal(SupertonicSpeechEngine.ModelFiles.Count + 1, files.Count);
        Assert.Contains(files, f => f.Path == "voice_styles/F1.json");
        Assert.Contains(files, f => f.Path == "LICENSE");
        Assert.All(files, f =>
        {
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            Assert.True(f.Bytes > 0);
        });
        Assert.Equal(
            "https://huggingface.co/supertone-oss-archive/supertonic-3/resolve/aafc6e32416a594460b32413efc49d7fe4ce6d46/onnx/vocoder.onnx",
            SupertonicSpeechEngine.Url(files.Single(f => f.Path == "onnx/vocoder.onnx")));

        // Modell rund 400 MB – steht so im README
        var megabytes = SupertonicSpeechEngine.ModelFiles.Sum(f => f.Bytes) / (1024.0 * 1024);
        Assert.InRange(megabytes, 370, 400);

        Assert.Throws<ArgumentException>(() => SupertonicSpeechEngine.FilesFor(new TtsVoice("supertonic", "M3", "de", VoiceGender.Male)));
    }

    [Theory]
    [InlineData(1.0, 1.0f)]
    [InlineData(1.1, 1.1f)]
    [InlineData(1.25, 1.25f)]
    [InlineData(3.0, 1.5f)]
    [InlineData(0.5, 0.9f)]
    [InlineData(double.NaN, 1.0f)]
    [InlineData(0.0, 1.0f)]
    public void Sprechtempo_aus_dem_Dashboard(double rate, float speed)
    {
        Assert.Equal((double)speed, SupertonicSpeechEngine.Speed(rate), 3);
    }

    // ------------------------------------------------------------------ Textaufbereitung (wie im Supertonic-Beispiel)

    [Theory]
    [InlineData("Hallo Welt", "<de>Hallo Welt.</de>")]
    [InlineData("Hallo , Welt !", "<de>Hallo, Welt!</de>")]
    [InlineData("  Guten   Abend\n\nwie geht's?  ", "<de>Guten Abend wie geht's?</de>")]
    [InlineData("Rückenwind – bis Frankfurt", "<de>Rückenwind - bis Frankfurt.</de>")]
    [InlineData("„Klar“, sagte er 😀", "<de>„Klar\", sagte er.</de>")]
    [InlineData("Straße/Weg #1", "<de>Straße Weg 1.</de>")]
    public void Text_wird_wie_im_Beispiel_aufbereitet(string text, string expected)
    {
        Assert.Equal(expected, SupertonicText.Prepare(text, "de"));
    }

    [Fact]
    public void Unbekannte_Sprache_wird_abgelehnt()
    {
        Assert.Throws<ArgumentException>(() => SupertonicText.Prepare("Hallo", "xx"));
    }

    [Fact]
    public void Zeichen_werden_zu_Kennungen()
    {
        var indexer = new long[128];
        indexer['a'] = 5;
        indexer['<'] = 1;

        Assert.Equal(new long[] { 1, 5, 0, 0 }, SupertonicText.Ids("<abä", indexer));
    }

    [Fact]
    public void Lange_Antworten_in_Stuecken_an_Satzgrenzen()
    {
        var sentence = new string('a', 140) + ".";
        var chunks = SupertonicText.Chunks($"{sentence} {sentence} {sentence}");

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 300));
        Assert.Equal(new[] { "Kurz. Und gut." }, SupertonicText.Chunks("Kurz. Und gut."));
        Assert.Empty(SupertonicText.Chunks("   "));
    }

    [Fact]
    public void Stil_Datei_wird_gelesen()
    {
        const string json = """
            {"style_ttl": {"dims": [1, 2, 2], "data": [[[0.5, 1.0], [1.5, 2.0]]]},
             "style_dp": {"dims": [1, 1, 3], "data": [[[3.0, 4.0, 5.0]]]}}
            """;

        var style = SupertonicStyle.Parse(json);

        Assert.Equal(new[] { 0.5f, 1.0f, 1.5f, 2.0f }, style.Ttl);
        Assert.Equal(new[] { 1, 2, 2 }, style.TtlShape);
        Assert.Equal(new[] { 3.0f, 4.0f, 5.0f }, style.Dp);
        Assert.Throws<InvalidDataException>(() => SupertonicStyle.Parse(json.Replace("[1, 1, 3]", "[1, 1, 4]", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ Derselbe Sprecher: die Stimme des First Officers entscheidet

    private sealed class FakeTts(string id, IReadOnlyList<TtsVoice> voices, TtsState state) : ITtsEngine
    {
        public TtsState State { get; set; } = state;

        public string Id => id;

        public IReadOnlyList<TtsVoice> Voices => voices;

        public string? Error => State == TtsState.Failed ? "failed" : null;

        public TtsState StateOf(TtsVoice voice) => State;

        public Task PrepareAsync(TtsVoice voice, CancellationToken ct) => Task.CompletedTask;

        public Task<SpeechClip> SynthesizeAsync(string text, TtsVoice voice, double rate, CancellationToken ct) =>
            Task.FromResult(new SpeechClip([0.1f], 44_100));
    }

    private static FakeTts Supertonic(TtsState state) => new(SupertonicSpeechEngine.EngineId, SupertonicSpeechEngine.Catalog, state);

    private static FakeTts Piper(TtsState state) => new(PiperSpeechEngine.EngineId, PiperSpeechEngine.Catalog, state);

    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("supertonic:M1")]
    [InlineData("supertonic:M2")]
    [InlineData("supertonic:F1")]
    public void Supertonic_Stimme_spricht_den_Smalltalk_in_der_Oberflaechensprache(string key)
    {
        var choice = SmalltalkVoices.Choose("de", key, [Supertonic(TtsState.Ready), Piper(TtsState.Ready)]);

        Assert.Equal("de", choice.Language);
        Assert.False(choice.FellBack);
        Assert.Null(choice.Prepare);
        Assert.Equal(key, choice.Voice!.Key);
        Assert.Equal("de", choice.Voice.Language);
    }

    [Fact]
    public void Supertonic_spricht_auch_englischen_Smalltalk_mit_derselben_Stimme()
    {
        var choice = SmalltalkVoices.Choose("en", "supertonic:F1", [Supertonic(TtsState.Ready)]);

        Assert.Equal("en", choice.Language);
        Assert.False(choice.FellBack);
        Assert.Equal("supertonic:F1", choice.Voice!.Key);
        Assert.Equal("en", choice.Voice.Language);
    }

    [Theory]
    [InlineData(TtsState.Idle)]
    [InlineData(TtsState.Downloading)]
    public void Solange_Supertonic_laedt_spricht_die_Kokoro_Stimme_auch_den_Smalltalk(TtsState state)
    {
        // Piper wäre bereit, spricht aber kein Englisch – nicht derselbe Sprecher wie bei den Callouts, also nicht
        var choice = SmalltalkVoices.Choose("de", "supertonic:F1", [Supertonic(state), Piper(TtsState.Ready)]);

        Assert.Equal("en", choice.Language);
        Assert.Equal("downloading", choice.Reason);
        Assert.Null(choice.Voice);
        Assert.Equal("supertonic:F1", choice.Prepare!.Key);
    }

    [Fact]
    public void Supertonic_gescheitert_dann_Kokoro_auf_Englisch()
    {
        var german = SmalltalkVoices.Choose("de", "supertonic:M1", [Supertonic(TtsState.Failed), Piper(TtsState.Ready)]);
        Assert.Equal("en", german.Language);
        Assert.Equal("failed", german.Reason);
        Assert.Null(german.Voice);

        // Englisch gewünscht: nichts zu melden, die Kokoro-Stimme spricht
        var english = SmalltalkVoices.Choose("en", "supertonic:M1", [Supertonic(TtsState.Failed)]);
        Assert.Equal("en", english.Language);
        Assert.False(english.FellBack);
        Assert.Null(english.Voice);
    }

    [Fact]
    public void Sprache_ohne_Supertonic_spricht_dieselbe_Stimme_englisch()
    {
        var choice = SmalltalkVoices.Choose("zh", "Supertonic:m2", [Supertonic(TtsState.Ready)]);

        Assert.Equal("en", choice.Language);
        Assert.Equal("noVoice", choice.Reason);
        Assert.Equal("supertonic:M2", choice.Voice!.Key);
        Assert.Equal("en", choice.Voice.Language);
    }

    [Fact]
    public void Registry_findet_Supertonic_Stimmen()
    {
        var registry = new TtsEngineRegistry([Supertonic(TtsState.Ready), Piper(TtsState.Ready)]);

        Assert.Equal("F1", registry.Find("supertonic:F1")!.Value.Voice.Id);
        Assert.Equal("de_DE-thorsten-medium", registry.Find("piper:de_DE-thorsten-medium")!.Value.Voice.Id);
        Assert.Null(registry.Find("supertonic:M5"));
    }

    [Fact]
    public void Mehrsprachige_Stimmen_Geschlecht_und_Schreibweise()
    {
        Assert.Equal(["supertonic:M1", "supertonic:M2", "supertonic:F1"], MultilingualVoices.All.Select(v => v.Key));
        Assert.All(MultilingualVoices.All, v => Assert.True(MultilingualVoices.Speaks(v, "en") && MultilingualVoices.Speaks(v, "de")));
        Assert.False(MultilingualVoices.Speaks(PiperSpeechEngine.Catalog[0], "en"));

        Assert.Equal(VoiceGender.Female, FirstOfficerCasting.GenderOfVoice("supertonic:F1"));
        Assert.Equal(VoiceGender.Male, FirstOfficerCasting.GenderOfVoice("supertonic:M2"));
        Assert.Equal("supertonic:F1", FirstOfficerCasting.NormalizeVoice(" Supertonic:f1 "));
        Assert.Equal("bf_emma", FirstOfficerCasting.NormalizeVoice("bf_emma"));
        Assert.Null(FirstOfficerCasting.NormalizeVoice("piper:de_DE-thorsten-medium"));
        Assert.Null(FirstOfficerCasting.NormalizeVoice("supertonic:M5"));

        // Feste Stimme in voice-settings.json und an der Sprachausgabe
        Assert.Equal("supertonic:M2", new VoicesSettings { Kopilot = "Supertonic:m2" }.Normalized().Kopilot);
        Assert.Equal("", new VoicesSettings { Kopilot = "piper:de_DE-thorsten-medium" }.Normalized().Kopilot);
        Assert.Equal("supertonic:F1", new VoiceOutputOptions { KopilotEngineVoice = "supertonic:f1" }.Normalized().KopilotEngineVoice);
        Assert.Null(new VoiceOutputOptions { KopilotEngineVoice = "bf_emma" }.Normalized().KopilotEngineVoice);
    }

    [Fact]
    public void Automatisch_mit_Supertonic_passend_zum_Geschlecht_und_Kokoro_als_Rueckfall()
    {
        var genders = new HashSet<VoiceGender>();
        for (var seed = 0; seed < 100; seed++)
        {
            // Die Windows-Stimme zählt nicht, wenn Supertonic spricht
            var p = FirstOfficerCasting.Create("f", new FirstOfficerConfig(), null, VoiceGender.Female, [], [], new Random(seed), T0, MultilingualVoices.All);

            Assert.NotNull(p.EngineVoice);
            Assert.Equal(p.EngineVoice, p.SpeakingVoice);
            Assert.Equal(p.Gender, FirstOfficerCasting.GenderOfVoice(p.EngineVoice));
            Assert.Equal(p.Gender, FirstOfficerCasting.GenderOfVoice(p.Voice));
            Assert.True(KokoroSpeechEngine.IsEnglishVoice(p.Voice));
            Assert.False(p.FixedVoice);
            genders.Add(p.Gender);
        }

        Assert.Equal(2, genders.Count);

        // Ohne Supertonic wie bisher nur Kokoro
        var kokoro = FirstOfficerCasting.Create("f", new FirstOfficerConfig(), null, null, [], [], new Random(1), T0);
        Assert.Null(kokoro.EngineVoice);
        Assert.Equal(kokoro.Voice, kokoro.SpeakingVoice);
    }

    [Fact]
    public void Feste_Supertonic_Stimme_bestimmt_das_Geschlecht()
    {
        var p = FirstOfficerCasting.Create("f", new FirstOfficerConfig(), "supertonic:F1", VoiceGender.Male, [], [], new Random(3), T0);

        Assert.True(p.FixedVoice);
        Assert.Equal("supertonic:F1", p.EngineVoice);
        Assert.Equal(VoiceGender.Female, p.Gender);
        Assert.Contains(p.Name, FirstOfficerNames.Female);
        Assert.Equal(VoiceGender.Female, FirstOfficerCasting.GenderOfVoice(p.Voice));
    }

    [Fact]
    public void Supertonic_an_und_aus_behaelt_Name_und_Vorstellung()
    {
        var roster = new FirstOfficerRoster(null, null, new Random(5));
        var config = new FirstOfficerConfig();
        var (kokoro, _) = roster.Ensure("flight-1", config, null, null, [], []);
        roster.MarkIntroduced();
        Assert.Null(kokoro!.EngineVoice);

        // Smalltalk auf Deutsch eingeschaltet: Supertonic dazu, Name bleibt
        var (multilingual, changed) = roster.Ensure("flight-1", config, null, null, [], [], MultilingualVoices.All);
        Assert.True(changed);
        Assert.Equal(kokoro.Name, multilingual!.Name);
        Assert.True(multilingual.Introduced);
        Assert.Equal(kokoro.Gender, FirstOfficerCasting.GenderOfVoice(multilingual.EngineVoice));

        var (again, changedAgain) = roster.Ensure("flight-1", config, null, null, [], [], MultilingualVoices.All);
        Assert.False(changedAgain);
        Assert.Equal(multilingual, again);

        // Feste Supertonic-Stimme gleichen Geschlechts
        var fixedKey = kokoro.Gender == VoiceGender.Female ? "supertonic:F1" : "supertonic:M2";
        var (fixedVoice, changedFixed) = roster.Ensure("flight-1", config, fixedKey, null, [], [], MultilingualVoices.All);
        Assert.True(changedFixed);
        Assert.Equal(kokoro.Name, fixedVoice!.Name);
        Assert.True(fixedVoice.FixedVoice);
        Assert.Equal(fixedKey, fixedVoice.EngineVoice);

        // Smalltalk aus (automatisch, nur Kokoro): wieder Kokoro, Name bleibt
        var (back, changedBack) = roster.Ensure("flight-1", config, null, null, [], []);
        Assert.True(changedBack);
        Assert.Equal(kokoro.Name, back!.Name);
        Assert.Null(back.EngineVoice);
        Assert.False(back.FixedVoice);
    }

    [Fact]
    public void Alte_Sicherung_ohne_mehrsprachige_Stimme_bleibt_gueltig()
    {
        var path = Path.Combine(TempDirectory(), FirstOfficerStore.FileName);
        var store = new FirstOfficerStore(path);
        var roster = new FirstOfficerRoster(store, null, new Random(5));
        var (first, _) = roster.Ensure("flight-1", new FirstOfficerConfig(), null, null, [], []);

        // So schrieb 0.10.0-beta.2 die Datei: ohne engineVoice
        var json = File.ReadAllText(path);
        Assert.Contains("\"engineVoice\"", json);
        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(json, @",\s*""engineVoice""\s*:\s*null", ""));
        Assert.DoesNotContain("engineVoice", File.ReadAllText(path));

        var restarted = new FirstOfficerRoster(store, null, new Random(77));
        var (restored, changed) = restarted.Ensure("flight-1", new FirstOfficerConfig(), null, null, [], []);

        Assert.False(changed);
        Assert.Equal(first!.Name, restored!.Name);
        Assert.Null(restored.EngineVoice);
    }

    // ------------------------------------------------------------------ Engine: Zustand und Prüfsumme

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "duopilo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SupertonicSpeechEngine Engine(string data, HttpMessageHandler? handler = null) =>
        new(Options.Create(new DuopiloOptions { DataDirectory = data }), NullLogger<SupertonicSpeechEngine>.Instance, handler);

    [Fact]
    public async Task Ohne_Modell_nicht_bereit_und_Sprechen_scheitert_verstaendlich()
    {
        using var engine = Engine(TempDirectory());
        var voice = SupertonicSpeechEngine.Catalog[0];

        Assert.Equal(TtsState.Idle, engine.StateOf(voice));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SynthesizeAsync("Hallo", voice, 1, CancellationToken.None));
        Assert.Contains("M1", ex.Message);
    }

    [Fact]
    public async Task Falsche_Pruefsumme_wird_geloescht_und_Kokoro_uebernimmt()
    {
        // Groß genug für die Größenprüfung der ersten Datei (duration_predictor.onnx), aber nicht die erwarteten Bytes
        var body = new byte[4 * 1024 * 1024];
        new Random(7).NextBytes(body);
        var data = TempDirectory();
        var handler = new BytesHandler(body);
        using var engine = Engine(data, handler);
        var voice = SupertonicSpeechEngine.Catalog.First(v => v.Gender == VoiceGender.Male);

        await engine.PrepareAsync(voice, CancellationToken.None);

        Assert.Equal(TtsState.Failed, engine.StateOf(voice));
        Assert.Contains("SHA-256", engine.Error);
        Assert.False(File.Exists(Path.Combine(engine.Root, "onnx", "duration_predictor.onnx")));

        // Der First Officer spricht dann mit seiner Kokoro-Stimme, der Smalltalk auf Englisch
        var choice = SmalltalkVoices.Choose("de", "supertonic:M1", [engine, Piper(TtsState.Ready)]);
        Assert.Equal("en", choice.Language);
        Assert.Equal("failed", choice.Reason);
        Assert.Null(choice.Voice);

        // Bis zum Neustart kein neuer Versuch
        await engine.PrepareAsync(voice, CancellationToken.None);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void Daten_liegen_unter_models_supertonic_3()
    {
        var data = TempDirectory();
        using var engine = Engine(data);

        Assert.Equal(Path.Combine(data, "models", "supertonic-3"), engine.Root);
    }

    // ------------------------------------------------------------------ Live (echtes Modell, nur auf Wunsch)

    /// <summary>
    /// Lädt das echte Modell und spricht deutsche Sätze mit M1, M2 und F1. Nur mit DUOPILO_SUPERTONIC_LIVE=1 (z. B. im Mess-Workflow);
    /// DUOPILO_SUPERTONIC_OUT = Ordner für die WAV-Dateien und die Zeiten.
    /// </summary>
    [Fact]
    public async Task Live_spricht_Deutsch()
    {
        if (Environment.GetEnvironmentVariable("DUOPILO_SUPERTONIC_LIVE") != "1")
        {
            return;
        }

        var data = Environment.GetEnvironmentVariable("DUOPILO_SUPERTONIC_DATA") is { Length: > 0 } d ? d : TempDirectory();
        var output = Environment.GetEnvironmentVariable("DUOPILO_SUPERTONIC_OUT");
        var threads = int.TryParse(Environment.GetEnvironmentVariable("DUOPILO_SUPERTONIC_THREADS"), out var t) ? t : new SmalltalkOptions().SupertonicThreads;
        using var engine = new SupertonicSpeechEngine(
            Options.Create(new DuopiloOptions { DataDirectory = data, Smalltalk = new SmalltalkOptions { SupertonicThreads = threads } }),
            NullLogger<SupertonicSpeechEngine>.Instance);
        var report = new StringBuilder($"Threads {threads}\n\n| Stimme | Satz | Audio s | Wand s |\n|---|---|---:|---:|\n");
        string[] sentences =
        [
            "Guten Abend, ich bin heute dein First Officer.",
            "Das Wetter in Frankfurt sieht gut aus, wir sollten pünktlich landen. Möchtest du noch einen Kaffee vor dem Sinkflug?",
        ];
        foreach (var voice in SupertonicSpeechEngine.Catalog)
        {
            await engine.PrepareAsync(voice, CancellationToken.None);
            Assert.True(engine.StateOf(voice) == TtsState.Ready, engine.Error);
            var all = new List<float>();
            var spoken = 0;
            // Zweimal: der erste Durchlauf enthält das Laden des Modells, der zweite zeigt die Zeit im laufenden Betrieb
            foreach (var sentence in sentences.Concat(sentences))
            {
                var sw = Stopwatch.StartNew();
                var clip = await engine.SynthesizeAsync(sentence, voice, 1.1, CancellationToken.None);
                sw.Stop();
                Assert.Equal(44_100, clip.SampleRate);
                Assert.InRange(clip.Duration.TotalSeconds, 1.5, 20);
                Assert.True(clip.Samples.Max(x => Math.Abs(x)) > 0.05f, "zu leise / stumm");
                report.Append($"| {voice.Id} | {sentence[..24]}… | {clip.Duration.TotalSeconds:0.00} | {sw.Elapsed.TotalSeconds:0.00} |\n");
                if (++spoken <= sentences.Length)
                {
                    all.AddRange(clip.Samples);
                    all.AddRange(new float[22_050]);
                }
            }

            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                await File.WriteAllBytesAsync(Path.Combine(output, $"duopilo-supertonic-{voice.Id}.wav"), WavEncoder.Encode([.. all], 44_100));
            }
        }

        Assert.True(File.Exists(Path.Combine(engine.Root, "LICENSE")));
        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(Path.Combine(output, $"supertonic-live-{threads}.md"), report.ToString());
        }
    }
}
