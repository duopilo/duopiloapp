using System.Diagnostics;
using System.Net;
using System.Text;
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

    // ------------------------------------------------------------------ Stimmwahl: Supertonic zuerst, Piper als Rückfall

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

    [Theory]
    [InlineData(VoiceGender.Male)]
    [InlineData(VoiceGender.Female)]
    public void Deutsch_spricht_Supertonic_passend_zum_Geschlecht(VoiceGender gender)
    {
        var used = new HashSet<string>();
        foreach (var name in FirstOfficerNames.For(gender))
        {
            var choice = SmalltalkVoices.Choose("de", gender, name, [Supertonic(TtsState.Ready), Piper(TtsState.Ready)]);

            Assert.Equal("de", choice.Language);
            Assert.False(choice.FellBack);
            Assert.Null(choice.Prepare);
            Assert.Equal(SupertonicSpeechEngine.EngineId, choice.Voice!.Engine);
            Assert.Equal(gender, choice.Voice.Gender);
            used.Add(choice.Voice.Id);
        }

        // Männliche Namen verteilen sich auf M1 und M2, weibliche haben F1
        Assert.Equal(gender == VoiceGender.Male ? new[] { "M1", "M2" } : new[] { "F1" }, used.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(TtsState.Idle)]
    [InlineData(TtsState.Downloading)]
    public void Solange_Supertonic_laedt_spricht_Piper_wenn_es_bereit_ist(TtsState state)
    {
        var choice = SmalltalkVoices.Choose("de", VoiceGender.Female, "Lena", [Supertonic(state), Piper(TtsState.Ready)]);

        Assert.Equal("de", choice.Language);
        Assert.False(choice.FellBack);
        Assert.Equal(PiperSpeechEngine.EngineId, choice.Voice!.Engine);
        Assert.Equal("supertonic:F1", choice.Prepare!.Key);
    }

    [Fact]
    public void Ohne_fertige_Stimme_Englisch_und_Supertonic_wird_geladen()
    {
        var choice = SmalltalkVoices.Choose("de", VoiceGender.Male, "Sam", [Supertonic(TtsState.Idle), Piper(TtsState.Idle)]);

        Assert.Equal("en", choice.Language);
        Assert.Equal("downloading", choice.Reason);
        Assert.Equal(SupertonicSpeechEngine.EngineId, choice.Voice!.Engine);
        Assert.Null(choice.Prepare);
    }

    [Fact]
    public void Supertonic_gescheitert_dann_Piper()
    {
        var ready = SmalltalkVoices.Choose("de", VoiceGender.Male, "Sam", [Supertonic(TtsState.Failed), Piper(TtsState.Ready)]);
        Assert.Equal("de", ready.Language);
        Assert.Equal(PiperSpeechEngine.EngineId, ready.Voice!.Engine);
        Assert.Null(ready.Prepare);

        // Piper noch nicht da: Piper wird geladen, solange Englisch
        var loading = SmalltalkVoices.Choose("de", VoiceGender.Male, "Sam", [Supertonic(TtsState.Failed), Piper(TtsState.Idle)]);
        Assert.Equal("en", loading.Language);
        Assert.Equal("downloading", loading.Reason);
        Assert.Equal(PiperSpeechEngine.EngineId, loading.Voice!.Engine);

        // Beide gescheitert: Englisch mit Hinweis
        var failed = SmalltalkVoices.Choose("de", VoiceGender.Male, "Sam", [Supertonic(TtsState.Failed), Piper(TtsState.Failed)]);
        Assert.Equal("en", failed.Language);
        Assert.Equal("failed", failed.Reason);
    }

    [Fact]
    public void Ohne_Supertonic_wie_bisher_Piper()
    {
        var choice = SmalltalkVoices.Choose("de", VoiceGender.Female, "Lena", [Piper(TtsState.Ready)]);

        Assert.Equal(PiperSpeechEngine.EngineId, choice.Voice!.Engine);
        Assert.Null(choice.Prepare);
    }

    [Fact]
    public void Gleicher_Name_gleiche_Supertonic_Stimme()
    {
        var a = SmalltalkVoices.Choose("de", VoiceGender.Male, "Jonas", [Supertonic(TtsState.Ready)]);
        var b = SmalltalkVoices.Choose("de", VoiceGender.Male, "Jonas", [Supertonic(TtsState.Ready)]);

        Assert.Equal(a.Voice, b.Voice);
    }

    [Fact]
    public void Registry_findet_Supertonic_Stimmen()
    {
        var registry = new TtsEngineRegistry([Supertonic(TtsState.Ready), Piper(TtsState.Ready)]);

        Assert.Equal("F1", registry.Find("supertonic:F1")!.Value.Voice.Id);
        Assert.Equal("de_DE-thorsten-medium", registry.Find("piper:de_DE-thorsten-medium")!.Value.Voice.Id);
        Assert.Null(registry.Find("supertonic:M5"));
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
    public async Task Falsche_Pruefsumme_wird_geloescht_und_Piper_uebernimmt()
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

        var choice = SmalltalkVoices.Choose("de", VoiceGender.Male, "Sam", [engine, Piper(TtsState.Ready)]);
        Assert.Equal("de", choice.Language);
        Assert.Equal(PiperSpeechEngine.EngineId, choice.Voice!.Engine);

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
        using var engine = Engine(data);
        var report = new StringBuilder("| Stimme | Satz | Audio s | Wand s |\n|---|---|---:|---:|\n");
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
            foreach (var sentence in sentences)
            {
                var sw = Stopwatch.StartNew();
                var clip = await engine.SynthesizeAsync(sentence, voice, 1.1, CancellationToken.None);
                sw.Stop();
                Assert.Equal(44_100, clip.SampleRate);
                Assert.InRange(clip.Duration.TotalSeconds, 1.5, 20);
                Assert.True(clip.Samples.Max(x => Math.Abs(x)) > 0.05f, "zu leise / stumm");
                report.Append($"| {voice.Id} | {sentence[..24]}… | {clip.Duration.TotalSeconds:0.00} | {sw.Elapsed.TotalSeconds:0.00} |\n");
                all.AddRange(clip.Samples);
                all.AddRange(new float[22_050]);
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
            await File.WriteAllTextAsync(Path.Combine(output, "supertonic-live.md"), report.ToString());
        }
    }
}
