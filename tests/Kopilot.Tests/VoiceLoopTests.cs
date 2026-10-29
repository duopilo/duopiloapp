using Kopilot.Aircraft;
using Kopilot.Monitoring;
using Kopilot.Speech;
using Kopilot.Voice;

namespace Kopilot.Tests;

/// <summary>
/// Sprachschleife (v0.9): Satzgrenzen (VAD), Halbduplex, Warteschlange mit Prioritäten, Taxi-Speed-Warnung,
/// voice-settings.json, Tastenkürzel und Whisper-Halluzinationen. Ohne Mikrofon, Modell und Audio.
/// </summary>
public sealed class VoiceLoopTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ Satzgrenzen

    private static float[] Frame(float value = 0.1f) => Enumerable.Repeat(value, SegmenterOptions.FrameSamples).ToArray();

    private static List<float[]> Feed(UtteranceSegmenter segmenter, int frames, float probability)
    {
        var done = new List<float[]>();
        for (var i = 0; i < frames; i++)
        {
            if (segmenter.Push(Frame(probability), probability) is { } u)
            {
                done.Add(u);
            }
        }

        return done;
    }

    [Fact]
    public void Satz_mit_Vorlauf_endet_nach_der_Stille()
    {
        var segmenter = new UtteranceSegmenter(new SegmenterOptions { Threshold = 0.5, MinSpeechMs = 200, SilenceMs = 700, PreRollMs = 300 });

        Assert.Empty(Feed(segmenter, 10, 0.05f));
        Assert.Empty(Feed(segmenter, 20, 0.9f));
        Assert.True(segmenter.InSpeech);
        Assert.Empty(Feed(segmenter, 21, 0.05f));

        var done = Feed(segmenter, 1, 0.05f);

        // 9 Blöcke Vorlauf (≈300 ms) + 20 Sprache + 6 Blöcke Stille (≈200 ms)
        var utterance = Assert.Single(done);
        Assert.Equal(35 * SegmenterOptions.FrameSamples, utterance.Length);
        Assert.False(segmenter.InSpeech);
    }

    [Fact]
    public void Kurzes_Geraeusch_ist_kein_Satz()
    {
        var segmenter = new UtteranceSegmenter(new SegmenterOptions { MinSpeechMs = 200, SilenceMs = 300 });

        Assert.Empty(Feed(segmenter, 3, 0.9f));
        Assert.False(segmenter.InSpeech);
        Assert.Empty(Feed(segmenter, 20, 0.05f));
    }

    [Fact]
    public void Zu_lange_Saetze_werden_abgeschnitten()
    {
        var segmenter = new UtteranceSegmenter(new SegmenterOptions { MaxUtteranceSec = 2, PreRollMs = 0 });

        var done = Feed(segmenter, 70, 0.9f);

        Assert.Equal(63 * SegmenterOptions.FrameSamples, Assert.Single(done).Length);
    }

    [Fact]
    public void Zwischen_den_Schwellen_laeuft_der_Satz_weiter()
    {
        var segmenter = new UtteranceSegmenter(new SegmenterOptions { Threshold = 0.5, SilenceMs = 300 });

        Feed(segmenter, 10, 0.9f);
        Assert.Empty(Feed(segmenter, 30, 0.42f)); // unter 0,5, aber über 0,35
        Assert.Single(Feed(segmenter, 10, 0.05f));
    }

    [Fact]
    public void Reset_verwirft_den_angefangenen_Satz()
    {
        var segmenter = new UtteranceSegmenter(new SegmenterOptions { SilenceMs = 300 });
        Feed(segmenter, 10, 0.9f);

        segmenter.Reset();

        Assert.False(segmenter.InSpeech);
        Assert.Empty(Feed(segmenter, 20, 0.05f));
    }

    [Fact]
    public void Halbduplex_Mikrofon_stumm_waehrend_und_kurz_nach_der_Ansage()
    {
        var tail = TimeSpan.FromMilliseconds(350);

        Assert.False(HalfDuplex.MicOpen(speaking: true, T0.AddSeconds(-10), T0, tail));
        Assert.False(HalfDuplex.MicOpen(speaking: false, T0.AddMilliseconds(-200), T0, tail));
        Assert.True(HalfDuplex.MicOpen(speaking: false, T0.AddMilliseconds(-400), T0, tail));
        Assert.True(HalfDuplex.MicOpen(speaking: false, DateTimeOffset.MinValue, T0, tail));
    }

    // ------------------------------------------------------------------ Warteschlange: Warnungen > Callouts > Antworten > ATC

    private static SpeechItem Item(string text, SpeechPriority priority, double maxAgeSec = 20) => new()
    {
        Text = text,
        Priority = priority,
        TimeUtc = T0,
        ExpiresUtc = T0.AddSeconds(maxAgeSec),
    };

    [Fact]
    public void Reihenfolge_nach_Prioritaet()
    {
        var queue = new CalloutQueue();
        queue.Offer(Item("Frankfurt Tower: line up runway two five center", SpeechPriority.Atc, 60), T0, null);
        queue.Offer(Item("Speed two five zero", SpeechPriority.Response), T0, null);
        queue.OfferAlert(new AlertEvent(1, T0, Rules.PmV1, AlertSeverity.Info, AlertKind.Event, "V1", "x"), new SpeechSettings(), T0, null);
        queue.OfferAlert(new AlertEvent(2, T0, Rules.Stall, AlertSeverity.Warning, AlertKind.Raise, "Stall", "x"), new SpeechSettings(), T0, null);

        // Die Warnung verdrängt den wartenden Callout, Antwort und ATC bleiben stehen
        Assert.Equal("Stall", queue.Take(T0)?.Text);
        Assert.Equal("Speed two five zero", queue.Take(T0)?.Text);
        Assert.Equal("Frankfurt Tower: line up runway two five center", queue.Take(T0)?.Text);
        Assert.Null(queue.Take(T0));
    }

    [Fact]
    public void Callout_vor_Antwort_vor_ATC()
    {
        var queue = new CalloutQueue();
        queue.Offer(Item("ATC", SpeechPriority.Atc, 60), T0, null);
        queue.Offer(Item("Antwort", SpeechPriority.Response), T0, null);
        queue.OfferAlert(new AlertEvent(1, T0, Rules.TaxiSpeed, AlertSeverity.Caution, AlertKind.Event, "Taxi speed", "x"), new SpeechSettings(), T0, null);

        Assert.Equal(new[] { "Taxi speed", "Antwort", "ATC" }, new[] { queue.Take(T0)!.Text, queue.Take(T0)!.Text, queue.Take(T0)!.Text });
    }

    [Theory]
    [InlineData(SpeechPriority.Atc)]
    [InlineData(SpeechPriority.Response)]
    [InlineData(SpeechPriority.Callout)]
    public void Warnung_unterbricht_alles_andere(SpeechPriority speaking)
    {
        var queue = new CalloutQueue();

        var (accepted, preempt) = queue.OfferAlert(
            new AlertEvent(1, T0, Rules.MasterWarning, AlertSeverity.Warning, AlertKind.Raise, "Master warning", "x"), new SpeechSettings(), T0, speaking);

        Assert.True(accepted);
        Assert.True(preempt);
    }

    [Fact]
    public void Antworten_unterbrechen_nicht()
    {
        var queue = new CalloutQueue();

        Assert.Equal((true, false), queue.Offer(Item("Speed two five zero", SpeechPriority.Response), T0, SpeechPriority.Atc));
        Assert.Equal((true, false), queue.Offer(Item("Unable", SpeechPriority.Response), T0, SpeechPriority.Callout));
    }

    [Fact]
    public void Unterbrochene_Antwort_kommt_einmal_wieder_Callout_nicht()
    {
        var queue = new CalloutQueue();
        var answer = Item("Gear up", SpeechPriority.Response) with { Seq = 7 };

        Assert.True(queue.Requeue(answer, T0.AddSeconds(3)));
        var again = queue.Take(T0.AddSeconds(3))!;
        Assert.True(again.Requeued);
        Assert.Equal(T0.AddSeconds(23), again.ExpiresUtc);

        Assert.False(queue.Requeue(again, T0.AddSeconds(4)));
        Assert.False(queue.Requeue(Item("V1", SpeechPriority.Callout), T0));
    }

    [Fact]
    public void ATC_wartet_laenger_als_Callouts()
    {
        var queue = new CalloutQueue();
        queue.Offer(Item("ATC", SpeechPriority.Atc, CalloutQueue.AtcMaxAge.TotalSeconds), T0, null);
        queue.OfferAlert(new AlertEvent(1, T0, Rules.PmV1, AlertSeverity.Info, AlertKind.Event, "V1", "x"), new SpeechSettings(), T0, null);

        var later = T0.AddSeconds(30);
        Assert.Equal("ATC", queue.Take(later)?.Text);
        Assert.Null(queue.Take(later));
    }

    [Fact]
    public void Aktive_und_stumme_Callouts()
    {
        var all = new VoiceOutputOptions().Normalized();
        Assert.True(all.SpeaksRule(Rules.PmV1));

        var some = new VoiceOutputOptions { ActiveRules = ["pm_v1", "TAXI_SPEED"], MutedRules = ["taxi_speed"] }.Normalized();
        Assert.True(some.SpeaksRule(Rules.PmV1));
        Assert.False(some.SpeaksRule(Rules.TaxiSpeed));
        Assert.False(some.SpeaksRule(Rules.PmRotate));

        Assert.False((all with { CalloutsEnabled = false }).SpeaksRule(Rules.PmV1));
        Assert.Null(new VoiceOutputOptions { KopilotVoice = "../x" }.Normalized().KopilotVoice);
    }

    // ------------------------------------------------------------------ Taxi-Speed-Warnung

    private static FlightSnapshot Ground(double sec, double gs) => new()
    {
        TimeUtc = T0.AddSeconds(sec),
        Connected = true,
        FlightRunning = true,
        DataAgeSec = 0.1,
        AircraftKey = "test",
        Limits = new AircraftLimits { VleKt = 280 },
        OnGround = true,
        IasKt = gs,
        GroundSpeedKt = gs,
        VerticalSpeedFpm = 0,
        AltitudeFt = 150,
        AglFt = 0,
        RadioAltFt = 0,
        GearExtendedPct = 100,
        GearHandleDown = true,
        FlapsIndex = 0,
        FlapsLabel = "0",
    };

    private static List<AlertEvent> Taxi(MonitorEngine engine, double from, double to, Func<double, double> gs, double step = 1)
    {
        var events = new List<AlertEvent>();
        for (var t = from; t <= to + 1e-9; t += step)
        {
            events.AddRange(engine.Evaluate(Ground(t, gs(t))).Where(e => e.Rule == Rules.TaxiSpeed));
        }

        return events;
    }

    [Fact]
    public void Zu_schnell_gerollt_Taxi_speed_mit_Cooldown()
    {
        var engine = new MonitorEngine();
        Taxi(engine, 0, 16, _ => 15);

        // Gleichmäßig von 15 auf 28 kt (1 kt/s), dann konstant
        var events = Taxi(engine, 17, 58, t => Math.Min(28, 15 + (t - 16)));
        var first = Assert.Single(events);
        Assert.Equal("Taxi speed", first.Callout);
        Assert.Equal(AlertSeverity.Caution, first.Severity);

        // Nach dem Cooldown (30 s) erneut
        Assert.Single(Taxi(engine, 59, 62, _ => 28));
    }

    [Fact]
    public void Startlauf_ist_kein_Taxi_speed()
    {
        var engine = new MonitorEngine();
        Taxi(engine, 0, 12, _ => 0);

        var events = Taxi(engine, 12.2, 30, t => (t - 12) * 4, step: 0.2);

        Assert.Empty(events);
    }

    [Fact]
    public void Abrollen_nach_der_Landung_ist_kein_Taxi_speed()
    {
        var engine = new MonitorEngine();
        Taxi(engine, 0, 12, _ => 0);
        Taxi(engine, 13, 14, _ => 130);

        // Verzögern bis 30 kt, dann schnell abrollen
        var events = Taxi(engine, 15, 45, t => Math.Max(30, 130 - ((t - 14) * 5)));

        Assert.Empty(events);
    }

    [Fact]
    public void Taxi_speed_einstellbar_und_abschaltbar()
    {
        var engine = new MonitorEngine();
        engine.ConfigureTaxiSpeed(enabled: true, thresholdKt: 30, cooldownSec: 30);
        Taxi(engine, 0, 16, _ => 15);
        Assert.Empty(Taxi(engine, 17, 40, t => Math.Min(28, 15 + (t - 16))));

        var off = new MonitorEngine();
        off.ConfigureTaxiSpeed(enabled: false, thresholdKt: 25, cooldownSec: 30);
        Taxi(off, 0, 16, _ => 15);
        Assert.Empty(Taxi(off, 17, 40, t => Math.Min(28, 15 + (t - 16))));
    }

    [Fact]
    public void Im_Pausenmodus_kein_Taxi_speed()
    {
        var engine = new MonitorEngine();
        Taxi(engine, 0, 16, _ => 15);

        var events = new List<AlertEvent>();
        for (var t = 17; t <= 40; t++)
        {
            events.AddRange(engine.Evaluate(Ground(t, Math.Min(28, 15 + (t - 16))) with { Paused = true }).Where(e => e.Rule == Rules.TaxiSpeed));
        }

        Assert.Empty(events);
    }

    // ------------------------------------------------------------------ voice-settings.json

    [Fact]
    public void Einstellungen_mit_Kommentaren_und_Grenzen()
    {
        var s = VoiceSettingsStore.Parse("""
            {
              // Kommentar
              "enabled": true,
              "microphone": { "device": " Headset ", "threshold": 3, "silenceMs": 50 },
              "recognition": { "model": "Small-Q5_1", "device": "Vulkan", "language": "DE", "threads": 99 },
              "voices": { "kopilot": "bm_george", "kopilotVolume": -1, "atcVolume": 7 },
              "commands": { "wakeWords": [ "Co-Pilot" ], "confirmTimeoutSec": 1 },
              "callouts": { "active": [ "pm_v1" ], "taxiSpeed": { "thresholdKt": "30" } },
            }
            """);

        Assert.True(s.Enabled);
        Assert.Equal("Headset", s.Microphone.Device);
        Assert.Equal(0.95, s.Microphone.Threshold);
        Assert.Equal(200, s.Microphone.SilenceMs);
        Assert.Equal("small-q5_1", s.Recognition.Model);
        Assert.Equal("gpu", s.Recognition.Device);
        Assert.Equal("de", s.Recognition.Language);
        Assert.Equal(16, s.Recognition.Threads);
        Assert.Equal("bm_george", s.Voices.Kopilot);
        Assert.Equal(0, s.Voices.KopilotVolume);
        Assert.Equal(1, s.Voices.AtcVolume);
        Assert.Equal(new[] { "kopilot" }, s.Commands.WakeWords);
        Assert.Equal(10, s.Commands.ConfirmTimeoutSec);
        Assert.Equal(new[] { "PM_V1" }, s.Callouts.Active);
        Assert.Equal(30, s.Callouts.TaxiSpeed.ThresholdKt);
    }

    [Fact]
    public void Standardwerte_und_unbekanntes_Modell()
    {
        var s = VoiceSettingsStore.Parse("""{ "recognition": { "model": "large-v3" } }""");

        Assert.False(s.Enabled);
        Assert.Equal("base-q5_1", s.Recognition.Model);
        Assert.Equal("cpu", s.Recognition.Device);
        Assert.Equal("auto", s.Recognition.Language);
        Assert.Equal("Ctrl+Alt+K", s.Hotkeys.Mute);
        Assert.Equal(60, s.Commands.ConfirmTimeoutSec);
        Assert.True(s.Callouts.SilentWhenPaused);
        Assert.Equal(25, s.Callouts.TaxiSpeed.ThresholdKt);
        Assert.Equal(new[] { "*" }, s.Callouts.Active);
    }

    [Fact]
    public void Vorlage_wird_in_den_Datenordner_kopiert()
    {
        var root = Directory.CreateTempSubdirectory("kopilot-voice-").FullName;
        try
        {
            var template = Path.Combine(root, "exe");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(template);
            File.WriteAllText(Path.Combine(template, VoiceSettingsStore.FileName), """{ "enabled": true }""");

            var store = new VoiceSettingsStore(data, template);
            var (settings, error) = store.Load();

            Assert.Null(error);
            Assert.True(settings.Enabled);
            Assert.True(File.Exists(Path.Combine(data, VoiceSettingsStore.FileName)));

            // Kaputte Datei: Standardwerte, Fehler, Datei bleibt
            File.WriteAllText(store.FilePath, "{ nicht json");
            var (fallback, problem) = store.Load();
            Assert.NotNull(problem);
            Assert.False(fallback.Enabled);
            Assert.Equal("{ nicht json", File.ReadAllText(store.FilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Mitgelieferte_Vorlage_ist_gueltig_und_aus()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kopilot.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var s = VoiceSettingsStore.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "Kopilot", VoiceSettingsStore.FileName)));

        Assert.False(s.Enabled);
        Assert.Equal("base-q5_1", s.Recognition.Model);
        Assert.Equal(25, s.Callouts.TaxiSpeed.ThresholdKt);
        Assert.Equal(60, s.Commands.ConfirmTimeoutSec);
        Assert.Equal(1, s.Voices.AtcVolume);
    }

    // ------------------------------------------------------------------ Tastenkürzel, Halluzinationen

    [Theory]
    [InlineData("Ctrl+Alt+K", HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x4Bu)]
    [InlineData("Strg + Umschalt + F12", HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x7Bu)]
    [InlineData("Pause", 0u, 0x13u)]
    [InlineData("Alt+NumPad5", HotkeyBinding.ModAlt, 0x65u)]
    public void Tastenkuerzel_werden_gelesen(string text, uint modifiers, uint key)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding, out var error));
        Assert.Null(error);
        Assert.Equal(modifiers, binding!.Modifiers);
        Assert.Equal(key, binding.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+Alt", true)]
    [InlineData("Ctrl+Banane", true)]
    [InlineData("K+L", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Ungueltige_Tastenkuerzel(string? text, bool hasError)
    {
        Assert.False(HotkeyBinding.TryParse(text, out var binding, out var error));
        Assert.Null(binding);
        Assert.Equal(hasError, error is not null);
    }

    [Theory]
    [InlineData(" Flaps one. ", "Flaps one.")]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("(Musik)", "")]
    [InlineData("Untertitel der Amara.org-Community", "")]
    [InlineData("Thanks for watching!", "")]
    [InlineData("...", "")]
    [InlineData("Gear up [Musik]", "Gear up")]
    public void Whisper_Halluzinationen_werden_aussortiert(string text, string expected)
    {
        Assert.Equal(expected, TranscriptFilter.Clean(text));
    }
}
