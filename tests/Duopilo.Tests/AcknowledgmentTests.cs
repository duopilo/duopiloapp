using Duopilo.Input;
using Duopilo.Monitoring;
using Duopilo.Speech;
using Duopilo.Voice;

namespace Duopilo.Tests;

/// <summary>
/// Bestätigungspflicht (v0.9.2, Punkt 4): Wichtige Meldungen erwarten "check" oder "copy". Ohne Antwort nach etwa 10 s einmal
/// wiederholen, danach eskalieren (Warnton und Eintrag im Dashboard) – nicht endlos wiederholen.
/// </summary>
public sealed class AcknowledgmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 4, 50, 0, TimeSpan.Zero);

    private static AlertEvent Raise(string rule, string? callout, AlertSeverity severity = AlertSeverity.Caution, double atSec = 0, string? reminder = null) =>
        new(1, T0 + TimeSpan.FromSeconds(atSec), rule, severity, AlertKind.Raise, callout, "message") { Reminder = reminder };

    private static AlertEvent Clear(string rule, double atSec) =>
        new(2, T0 + TimeSpan.FromSeconds(atSec), rule, AlertSeverity.Caution, AlertKind.Clear, null, "clear");

    private static DateTimeOffset At(double sec) => T0 + TimeSpan.FromSeconds(sec);

    [Theory]
    [InlineData("Check")]
    [InlineData("Copy")]
    [InlineData("Checked")]
    [InlineData("Copied")]
    [InlineData("Roger, check")]
    public void Check_und_Copy_werden_erkannt(string raw)
    {
        Assert.Equal(IntentKind.Check, VoiceIntents.Recognize(VoiceText.Normalize(raw), ["kopilot"]).Kind);
    }

    [Theory]
    [InlineData("Speed check")]
    [InlineData("Check squawk")]
    [InlineData("Fuel check")]
    public void Abfragen_mit_check_bleiben_Abfragen(string raw)
    {
        Assert.Equal(IntentKind.Query, VoiceIntents.Recognize(VoiceText.Normalize(raw), ["kopilot"]).Kind);
    }

    [Fact]
    public void Standardregeln_mit_Fuel_Squawk_Frequenz_und_TCAS_RA()
    {
        var tracker = new AcknowledgmentTracker();

        Assert.True(tracker.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(10), tracker.RepeatAfter);
        Assert.True(tracker.Requires(Raise("FUEL_WARNING", "Fuel low")));
        Assert.True(tracker.Requires(Raise(Rules.TcasRa, "TCAS RA", AlertSeverity.Warning)));
        Assert.True(tracker.Requires(Raise("ATC_SQUAWK", null, reminder: "Check squawk 2340")));
        Assert.True(tracker.Requires(Raise(Rules.SinkRate, "Sink rate")));

        // Infos, stumme Meldungen und nicht gelistete Regeln brauchen keine Quittung
        Assert.False(tracker.Requires(Raise(Rules.FmaChange, "Loc star", AlertSeverity.Info)));
        Assert.False(tracker.Requires(Raise(Rules.SinkRate, null)));
        Assert.False(tracker.Requires(Raise(Rules.EcamMessage, "ECAM")));
    }

    [Fact]
    public void Ohne_Antwort_einmal_wiederholen_dann_eskalieren_nicht_endlos()
    {
        var tracker = new AcknowledgmentTracker();
        tracker.Observe(Raise("FUEL_WARNING", "Fuel below minimum"));

        Assert.Empty(tracker.Tick(At(9), paused: false));

        var repeat = Assert.Single(tracker.Tick(At(10), paused: false));
        Assert.IsType<AcknowledgmentAction.Repeat>(repeat);
        Assert.Equal("Fuel below minimum", repeat.Item.Callout);

        Assert.Empty(tracker.Tick(At(19), paused: false));
        var escalate = Assert.Single(tracker.Tick(At(20), paused: false));
        Assert.IsType<AcknowledgmentAction.Escalate>(escalate);
        Assert.Equal(new[] { "Fuel below minimum" }, tracker.EscalatedTexts);
        Assert.Empty(tracker.PendingTexts);

        // Danach nichts mehr – nicht endlos
        Assert.Empty(tracker.Tick(At(60), paused: false));
        Assert.Empty(tracker.Tick(At(600), paused: false));
    }

    [Fact]
    public void Check_quittiert_und_hebt_den_Dashboard_Eintrag_auf()
    {
        var tracker = new AcknowledgmentTracker();
        tracker.Observe(Raise("FUEL_WARNING", "Fuel low"));
        tracker.Tick(At(10), false);
        tracker.Tick(At(20), false);

        tracker.Observe(Raise(Rules.SinkRate, "Sink rate", atSec: 25));

        var actions = tracker.Acknowledge(At(26));

        var resolved = Assert.Single(actions);
        Assert.IsType<AcknowledgmentAction.Resolved>(resolved);
        Assert.Equal("FUEL_WARNING", resolved.Item.Rule);
        Assert.Empty(tracker.Pending);
        Assert.Empty(tracker.Escalated);
    }

    [Fact]
    public void Check_vor_der_Meldung_quittiert_sie_nicht()
    {
        var tracker = new AcknowledgmentTracker();
        tracker.Observe(Raise(Rules.TcasRa, "TCAS RA", AlertSeverity.Warning, atSec: 5));

        tracker.Acknowledge(At(4));

        Assert.Single(tracker.Pending);
    }

    [Fact]
    public void Zustand_vorbei_keine_Quittung_mehr()
    {
        var tracker = new AcknowledgmentTracker();
        tracker.Observe(Raise(Rules.SinkRate, "Sink rate"));
        tracker.Observe(Clear(Rules.SinkRate, 3));

        Assert.Empty(tracker.Pending);
        Assert.Empty(tracker.Tick(At(30), false));

        // Eskaliert und dann vorbei: Eintrag im Dashboard aufheben
        tracker.Observe(Raise("ATC_SQUAWK", null, atSec: 40, reminder: "Check squawk 2340"));
        tracker.Tick(At(50), false);
        tracker.Tick(At(60), false);
        Assert.IsType<AcknowledgmentAction.Resolved>(Assert.Single(tracker.Observe(Clear("ATC_SQUAWK", 70))));
    }

    [Fact]
    public void Erinnerung_statt_Callout_bei_ATC_Abweichungen()
    {
        var tracker = new AcknowledgmentTracker();
        tracker.Observe(Raise("ATC_FREQUENCY", null, reminder: "Check frequency 132.25"));

        Assert.Equal("Check frequency 132.25", Assert.Single(tracker.Tick(At(10), false)).Item.Callout);
    }

    [Fact]
    public void Pause_haelt_die_Wartezeit_an()
    {
        var tracker = new AcknowledgmentTracker();
        tracker.Observe(Raise("FUEL_WARNING", "Fuel low"));

        Assert.Empty(tracker.Tick(At(5), paused: true));
        Assert.Empty(tracker.Tick(At(300), paused: true));

        // Pause von 5 s bis zum ersten Tick danach (304 s): 5 s waren vorher vergangen, 5 s fehlen noch → fällig bei 309 s
        Assert.Empty(tracker.Tick(At(304), paused: false));
        Assert.Empty(tracker.Tick(At(308), paused: false));
        Assert.Single(tracker.Tick(At(309), paused: false));
    }

    [Fact]
    public void Dieselbe_Regel_nur_einmal_offen_und_Eskalation_selbst_nie()
    {
        var tracker = new AcknowledgmentTracker();
        tracker.Observe(Raise(Rules.SinkRate, "Sink rate"));
        tracker.Observe(Raise(Rules.SinkRate, "Sink rate", atSec: 4));
        tracker.Observe(Raise(AcknowledgmentTracker.EscalationRule, "Unacknowledged", AlertSeverity.Warning));

        var item = Assert.Single(tracker.Pending);
        Assert.Equal(At(14), item.DueUtc);
    }

    [Fact]
    public void Einstellungen_Regeln_und_Abschalten()
    {
        var off = new AcknowledgmentTracker(new AcknowledgeSettings { Enabled = false });
        Assert.False(off.Requires(Raise("FUEL_WARNING", "Fuel low")));
        Assert.Empty(off.Observe(Raise("FUEL_WARNING", "Fuel low")));
        Assert.Empty(off.Pending);

        var custom = new AcknowledgmentTracker(new AcknowledgeSettings { RepeatAfterSec = 2, Rules = ["sink_rate"] });
        Assert.Equal(TimeSpan.FromSeconds(5), custom.RepeatAfter);
        Assert.True(custom.Requires(Raise(Rules.SinkRate, "Sink rate")));
        Assert.False(custom.Requires(Raise("FUEL_WARNING", "Fuel low")));

        var parsed = VoiceSettingsStore.Parse("""{ "acknowledge": { "enabled": true, "repeatAfterSec": 15, "rules": [ "TCAS_RA" ] } }""");
        Assert.Equal(15, parsed.Acknowledge.RepeatAfterSec);
        Assert.Equal(new[] { "TCAS_RA" }, parsed.Acknowledge.Rules);
        Assert.Contains("TCAS_RA", VoiceSettingsStore.Parse("{}").Acknowledge.Rules);
    }

    // ------------------------------------------------------------------ Im Gespräch

    private sealed class Gateway : IVoiceActionGateway
    {
        public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct) =>
            Task.FromResult(new InputResult(InputResult.Refused));

        public Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct) =>
            Task.FromResult(new InputResult(InputResult.Refused));

        public InputResult Cancel(string token, string reason) => new(InputResult.Cancelled);
    }

    private sealed class NoState : IVoiceFlightStateSource
    {
        public VoiceFlightState Read() => new() { Connected = true };
    }

    [Fact]
    public async Task Check_im_Gespraech_quittiert_still()
    {
        var tracker = new AcknowledgmentTracker();
        var dialog = new VoiceDialog(new Gateway(), new NoState()) { Acknowledgments = tracker };
        tracker.Observe(Raise("FUEL_WARNING", "Fuel low"));
        tracker.Tick(At(10), false);
        tracker.Tick(At(20), false);

        var normalized = VoiceText.Normalize("Copy");
        var reply = await dialog.HandleAsync(VoiceIntents.Recognize(normalized, ["kopilot"]), normalized, false, At(22), CancellationToken.None);

        Assert.Null(reply);
        Assert.Empty(tracker.Escalated);
        Assert.IsType<AcknowledgmentAction.Resolved>(Assert.Single(dialog.TakeAcknowledgmentActions()));
        Assert.Empty(dialog.TakeAcknowledgmentActions());
    }

    [Fact]
    public void Warnton_ist_ein_kurzes_WAV()
    {
        var samples = ToneGenerator.Attention();
        var wav = ToneGenerator.AttentionWav(0.5);

        Assert.InRange(samples.Length, 22050 / 2, 22050);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(44 + (samples.Length * 2), wav.Length);
    }
}
