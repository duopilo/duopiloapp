using System.Collections.Concurrent;
using Kopilot.Monitoring;
using Kopilot.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kopilot.Tests;

/// <summary>Eigene Sprachausgabe: Text für die Synthese, Warteschlange und Cockpit-Logik (ohne echtes Modell und Audio).</summary>
public sealed class SpeechTests
{
    [Theory]
    [InlineData("Gear speed", "Gear speed")]
    [InlineData("Runway two three left assigned", "Runway two three left assigned")]
    [InlineData("Master warning, ENG 1 FIRE", "Master warning, engine 1 fire")]
    [InlineData("Master caution, HYD G RSVR LO PR", "Master caution, hydraulic green reservoir low pressure")]
    [InlineData("Master caution, A/THR OFF", "Master caution, auto thrust off")]
    [InlineData("Master warning, APU FIRE", "Master warning, A P U fire")]
    [InlineData("Master warning, L/G GEAR NOT DOWNLOCKED", "Master warning, landing gear gear not downlocked")]
    [InlineData("  ", "")]
    public void Callouts_werden_fuer_die_Sprachsynthese_aufbereitet(string callout, string expected)
    {
        Assert.Equal(expected, CalloutText.ForSpeech(callout));
    }

    [Theory]
    [InlineData("bm_george", true)]
    [InlineData("af_heart", true)]
    [InlineData("zf_001", false)]
    [InlineData("jf_alpha", false)]
    [InlineData("../appsettings", false)]
    [InlineData("bm_george.npy", false)]
    [InlineData("", false)]
    public void Nur_englische_Stimmen_ohne_Pfadangaben(string name, bool expected)
    {
        Assert.Equal(expected, KokoroSpeechEngine.IsEnglishVoice(name));
    }

    [Fact]
    public void Stimmenliste_kommt_aus_dem_Ordner()
    {
        var dir = Directory.CreateTempSubdirectory("kopilot-voices-").FullName;
        try
        {
            foreach (var name in new[] { "bm_george.npy", "af_heart.npy", "zf_001.npy", "readme.txt" })
            {
                File.WriteAllText(Path.Combine(dir, name), "");
            }

            Assert.Equal(new[] { "af_heart", "bm_george" }, KokoroSpeechEngine.ListVoices(dir));
            Assert.Empty(KokoroSpeechEngine.ListVoices(Path.Combine(dir, "fehlt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Stille_am_Anfang_und_Ende_wird_abgeschnitten()
    {
        Assert.Equal(new[] { 0.5f, 0f, -0.3f }, KokoroSpeechEngine.TrimSilence([0f, 0f, 0.5f, 0f, -0.3f, 0f]));
        Assert.Empty(KokoroSpeechEngine.TrimSilence([0f, 0f]));
        Assert.Empty(KokoroSpeechEngine.TrimSilence([]));
    }

    [Fact]
    public void Einstellungen_werden_gemerkt_und_kaputte_Datei_ignoriert()
    {
        var dir = Directory.CreateTempSubdirectory("kopilot-speech-").FullName;
        try
        {
            var store = new SpeechSettingsStore(Path.Combine(dir, "sub", "speech.json"));
            Assert.Equal(new SpeechSettings(null, null), store.Load());

            Assert.True(store.Save(new SpeechSettings(false, "af_heart")));
            Assert.Equal(new SpeechSettings(false, "af_heart"), store.Load());

            File.WriteAllText(store.Path, "{ kaputt");
            Assert.Equal(new SpeechSettings(null, null), store.Load());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Callout_aus_dem_Monitoring_wird_gesprochen_und_vorab_synthetisiert()
    {
        await using var t = await Harness.StartAsync();

        t.Publish(Rules.GearSpeed, AlertSeverity.Caution, "Gear speed");

        await t.WaitPlayedAsync("Gear speed");
        await Until(() => t.Engine.Synthesized.Contains("bm_george|One thousand to go"));
        Assert.Equal(SpeechState.Ready, t.Speaker.State);
        Assert.Equal("Gear speed", t.Speaker.Status().LastSpoken);
    }

    [Fact]
    public async Task Clear_leise_Stufen_und_stumme_Regeln_werden_nicht_gesprochen()
    {
        await using var t = await Harness.StartAsync(o =>
        {
            o.MinSeverity = AlertSeverity.Caution;
            o.MutedRules = [Rules.RunwayAssigned];
        });

        t.Publish(Rules.GearSpeed, AlertSeverity.Caution, "Gear speed", AlertKind.Clear);
        t.Publish(Rules.Alt1000ToGo, AlertSeverity.Info, "One thousand to go", AlertKind.Event);
        t.Publish(Rules.RunwayAssigned, AlertSeverity.Caution, "Runway two seven assigned", AlertKind.Event);
        t.Publish(Rules.ApDisconnect, AlertSeverity.Caution, "Autopilot off", AlertKind.Event);

        await t.WaitPlayedAsync("Autopilot off");
        Assert.Equal(new[] { "Autopilot off" }, t.Audio.Started);
    }

    [Fact]
    public async Task Veraltete_Callouts_verfallen()
    {
        await using var t = await Harness.StartAsync();

        t.Publish(Rules.SpeedLimitFl100, AlertSeverity.Caution, "Speed limit", time: DateTimeOffset.UtcNow.AddSeconds(-30));
        t.Publish(Rules.BankAngle, AlertSeverity.Caution, "Bank angle");

        await t.WaitPlayedAsync("Bank angle");
        Assert.DoesNotContain("Speed limit", t.Audio.Started);
    }

    [Fact]
    public async Task Warning_unterbricht_eine_laufende_Caution()
    {
        await using var t = await Harness.StartAsync(playTime: TimeSpan.FromMilliseconds(800));

        t.Publish(Rules.SpeedLimitFl100, AlertSeverity.Caution, "Speed limit");
        await Until(() => t.Audio.Started.Contains("Speed limit"));
        t.Publish(Rules.BankAngle, AlertSeverity.Caution, "Bank angle");
        t.Publish(Rules.MasterWarning, AlertSeverity.Warning, "Master warning, ENG 1 FIRE");

        await t.WaitPlayedAsync("Master warning, engine 1 fire");
        Assert.Contains("Speed limit", t.Audio.Interrupted);
        Assert.DoesNotContain("Bank angle", t.Audio.Started);
        Assert.DoesNotContain("Speed limit", t.Audio.Played);
    }

    [Fact]
    public async Task Ausgeschaltet_wird_nichts_gesprochen_ausser_dem_Test()
    {
        await using var t = await Harness.StartAsync();

        t.Speaker.SetEnabled(false);
        t.Publish(Rules.GearSpeed, AlertSeverity.Caution, "Gear speed");
        Assert.True(t.Speaker.Test());

        await t.WaitPlayedAsync(CalloutText.TestPhrase);
        Assert.DoesNotContain("Gear speed", t.Audio.Started);
        Assert.Equal(new SpeechSettings(false, "bm_george"), t.Store.Load());
    }

    [Fact]
    public async Task Stimme_wechseln_wird_gemerkt_unbekannte_abgelehnt()
    {
        var dir = Directory.CreateTempSubdirectory("kopilot-speech-").FullName;
        var path = Path.Combine(dir, "speech.json");
        try
        {
            await using (var t = await Harness.StartAsync(storePath: path))
            {
                Assert.NotNull(t.Speaker.SetVoice("xx_unbekannt"));
                Assert.Null(t.Speaker.SetVoice("AF_HEART"));
                Assert.Equal("af_heart", t.Speaker.Voice);

                await t.WaitPlayedAsync(CalloutText.TestPhrase);
                Assert.Contains("af_heart|" + CalloutText.TestPhrase, t.Engine.Synthesized);
            }

            await using var again = await Harness.StartAsync(storePath: path);
            Assert.Equal("af_heart", again.Speaker.Voice);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Gespeicherte_Stimme_die_es_nicht_mehr_gibt_faellt_auf_die_Konfiguration_zurueck()
    {
        var dir = Directory.CreateTempSubdirectory("kopilot-speech-").FullName;
        var path = Path.Combine(dir, "speech.json");
        try
        {
            new SpeechSettingsStore(path).Save(new SpeechSettings(true, "bf_weg"));

            await using var t = await Harness.StartAsync(storePath: path);

            Assert.Equal("bm_george", t.Speaker.Voice);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Modellfehler_wird_angezeigt_statt_Kopilot_zu_stoppen()
    {
        await using var t = await Harness.StartAsync(engine: new FakeEngine { LoadError = new IOException("Download kaputt") });

        var status = t.Speaker.Status();
        Assert.Equal(SpeechState.Failed, status.State);
        Assert.Equal("Download kaputt", status.Error);
        Assert.Null(status.Device);
        Assert.False(t.Speaker.Test());
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Bedingung nicht rechtzeitig erfüllt.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Sprecher mit Attrappen für Modell und Audio, echtem AlertBus und Einstellungsdatei im Temp-Ordner.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string? _ownedDir;

        private Harness(CalloutSpeaker speaker, AlertBus bus, FakeEngine engine, FakeAudio audio, SpeechSettingsStore store, string? ownedDir)
        {
            Speaker = speaker;
            Bus = bus;
            Engine = engine;
            Audio = audio;
            Store = store;
            _ownedDir = ownedDir;
        }

        public CalloutSpeaker Speaker { get; }

        public AlertBus Bus { get; }

        public FakeEngine Engine { get; }

        public FakeAudio Audio { get; }

        public SpeechSettingsStore Store { get; }

        public static async Task<Harness> StartAsync(Action<SpeechOptions>? configure = null, FakeEngine? engine = null, TimeSpan? playTime = null, string? storePath = null)
        {
            var options = new KopilotOptions();
            configure?.Invoke(options.Speech);

            string? ownedDir = null;
            if (storePath is null)
            {
                ownedDir = Directory.CreateTempSubdirectory("kopilot-speech-").FullName;
                storePath = Path.Combine(ownedDir, "speech.json");
            }

            var bus = new AlertBus();
            engine ??= new FakeEngine();
            var audio = new FakeAudio { PlayTime = playTime ?? TimeSpan.Zero };
            var store = new SpeechSettingsStore(storePath);
            var speaker = new CalloutSpeaker(Options.Create(options), bus, engine, audio, store, NullLogger<CalloutSpeaker>.Instance);
            await speaker.StartAsync(CancellationToken.None);
            await Until(() => speaker.State is SpeechState.Ready or SpeechState.Failed);
            return new Harness(speaker, bus, engine, audio, store, ownedDir);
        }

        public void Publish(string rule, AlertSeverity severity, string callout, AlertKind kind = AlertKind.Raise, DateTimeOffset? time = null) =>
            Bus.Publish([new AlertEvent(0, time ?? DateTimeOffset.UtcNow, rule, severity, kind, callout, "Test")]);

        public Task WaitPlayedAsync(string text) => Until(() => Audio.Played.Contains(text));

        public async ValueTask DisposeAsync()
        {
            await Speaker.StopAsync(CancellationToken.None);
            Speaker.Dispose();
            if (_ownedDir is not null)
            {
                Directory.Delete(_ownedDir, recursive: true);
            }
        }
    }

    /// <summary>Statt Audio: der Text steckt Zeichen für Zeichen in den Samples.</summary>
    private sealed class FakeEngine : ISpeechEngine
    {
        public Exception? LoadError { get; init; }

        public ConcurrentQueue<string> Synthesized { get; } = new();

        public bool IsLoaded { get; private set; }

        public IReadOnlyList<string> Voices { get; } = ["af_heart", "bm_george"];

        public Task LoadAsync(Action<double>? downloadProgress, CancellationToken ct)
        {
            if (LoadError is not null)
            {
                throw LoadError;
            }

            downloadProgress?.Invoke(0.5);
            IsLoaded = true;
            return Task.CompletedTask;
        }

        public SpeechClip Synthesize(string text, string voice, double speed)
        {
            Synthesized.Enqueue(voice + "|" + text);
            return new SpeechClip(text.Select(c => (float)c).ToArray(), 24_000);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAudio : IAudioOutput
    {
        public TimeSpan PlayTime { get; init; }

        public ConcurrentQueue<string> Started { get; } = new();

        public ConcurrentQueue<string> Played { get; } = new();

        public ConcurrentQueue<string> Interrupted { get; } = new();

        public IReadOnlyList<string> Devices { get; } = ["Testgerät"];

        public string DeviceName => "Testgerät";

        public async Task PlayAsync(SpeechClip clip, float volume, CancellationToken ct)
        {
            var text = new string(clip.Samples.Select(f => (char)f).ToArray());
            Started.Enqueue(text);
            try
            {
                await Task.Delay(PlayTime, ct);
            }
            catch (OperationCanceledException)
            {
                Interrupted.Enqueue(text);
                throw;
            }

            Played.Enqueue(text);
        }
    }
}
