using System.Net;
using System.Text;
using System.Text.Json;
using Kopilot.Atc;
using Kopilot.Input;
using Kopilot.Localization;
using Kopilot.Monitoring;
using Kopilot.Smalltalk;
using Kopilot.Speech;
using Kopilot.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kopilot.Tests;

/// <summary>
/// Smalltalk und First Officer (Prompt N): Routing Befehl gegen Smalltalk, Vorrang der Callouts, Name und Stimme pro Flug,
/// Piper als zweite Stimme mit Rückfall auf Englisch, der Dienst für das Sprachmodell (Grenzen, Fehler, Schlüssel).
/// </summary>
public sealed class SmalltalkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 18, 0, 0, TimeSpan.Zero);

    private const string Key = "sk-ant-api03-SECRETSECRETSECRET1234567890";

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Gateway : IVoiceActionGateway
    {
        public List<string> Calls { get; } = [];

        public InputResult Next { get; set; } = new(InputResult.Executed, Action: "gear.up", Callout: "Gear up", Confirmed: true);

        public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"prepare {actionId} {value}");
            return Task.FromResult(new InputResult(InputResult.AwaitingConfirmation, Token: "t1", Action: actionId, Value: value, Readback: "Flaps 1"));
        }

        public Task<InputResult> ExecuteAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"execute {actionId} {value}");
            return Task.FromResult(Next);
        }

        public Task<InputResult> ConfirmAsync(string token, string pilotWords, CancellationToken ct)
        {
            Calls.Add($"confirm {token}");
            return Task.FromResult(new InputResult(InputResult.Executed, Confirmed: true));
        }

        public InputResult Cancel(string token, string reason)
        {
            Calls.Add($"cancel {token}");
            return new InputResult(InputResult.Cancelled);
        }
    }

    private sealed class State : IVoiceFlightStateSource
    {
        public VoiceFlightState Read() => new()
        {
            Connected = true, OnGround = false, IasKt = 280, GroundSpeedKt = 450, AltitudeFt = 35000, FuelKg = 12400,
            Engines = [new EngineReading(true, 85), new EngineReading(true, 85)],
        };
    }

    private sealed class NoAtc : IAtcLink
    {
        public bool Enabled => false;

        public AtcRoutingInfo RoutingInfo() => AtcRoutingInfo.Empty;

        public Task<AtcTransmitResult> TransmitAsync(string text, CancellationToken ct) => throw new InvalidOperationException("ATC is off");
    }

    private static (VoiceDispatcher Dispatcher, VoiceDialog Dialog, Gateway Gateway) Create(int? chatWords = 12, string? persona = "sam")
    {
        var gateway = new Gateway();
        var dialog = new VoiceDialog(gateway, new State(), new ManualTime(T0));
        var dispatcher = new VoiceDispatcher(dialog, new NoAtc(), new VoiceRouter(["kopilot"]), ["kopilot"])
        {
            ChatWords = () => chatWords,
            PersonaName = () => persona,
        };
        return (dispatcher, dialog, gateway);
    }

    // ------------------------------------------------------------------ Routing: Befehl, Abfrage, Funk gegen Smalltalk

    [Theory]
    [InlineData("Gear up", IntentKind.Action)]
    [InlineData("Flaps one", IntentKind.Action)]
    [InlineData("Speed check", IntentKind.Query)]
    [InlineData("Wann landen wir", IntentKind.Query)]
    [InlineData("Before start checklist", IntentKind.Checklist)]
    [InlineData("Say again", IntentKind.SayAgain)]
    [InlineData("Check", IntentKind.Check)]
    [InlineData("Briefing", IntentKind.Briefing)]
    [InlineData("Let's do the briefing", IntentKind.Briefing)]
    [InlineData("Briefing starten", IntentKind.Briefing)]
    public async Task Befehle_und_Abfragen_gehen_nie_ans_Sprachmodell(string said, IntentKind expected)
    {
        var (dispatcher, _, _) = Create();

        var outcome = await dispatcher.HandleAsync(said, T0, CancellationToken.None);

        Assert.False(outcome.Chat);
        Assert.Equal(expected, outcome.Intent!.Kind);
    }

    [Fact]
    public async Task Funkspruch_geht_ans_ATC_nicht_ans_Sprachmodell()
    {
        var (dispatcher, _, _) = Create();

        var outcome = await dispatcher.HandleAsync("Frankfurt Tower, how is the weather today", T0, CancellationToken.None);

        Assert.False(outcome.Chat);
        Assert.Equal(VoiceTarget.Atc, outcome.Route.Target);
        Assert.Equal(VoiceDispatcher.AtcUnavailable, outcome.Reply!.Text);
    }

    [Fact]
    public async Task Unbekannter_Satz_geht_ans_Sprachmodell_statt_Say_again()
    {
        var (dispatcher, _, gateway) = Create();

        var outcome = await dispatcher.HandleAsync("What's the weather like in Berlin today?", T0, CancellationToken.None);

        Assert.True(outcome.Chat);
        Assert.Null(outcome.Reply);
        Assert.Equal(VoiceTarget.Kopilot, outcome.Route.Target);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task Ohne_Sprachmodell_bleibt_es_bei_Say_again()
    {
        var (dispatcher, _, _) = Create(chatWords: null);

        var outcome = await dispatcher.HandleAsync("What's the weather like in Berlin today?", T0, CancellationToken.None);

        Assert.False(outcome.Chat);
        Assert.Equal(VoiceDialog.SayAgain, outcome.Reply!.Text);
    }

    [Fact]
    public async Task Langer_Satz_ohne_Anrede_ist_Gespraech_im_Raum_mit_Anrede_Smalltalk()
    {
        var (dispatcher, _, _) = Create(chatWords: 12);
        const string text = "tell me something about the history of the airport and why it has so many runways";

        var room = await dispatcher.HandleAsync(text, T0, CancellationToken.None);
        var addressed = await dispatcher.HandleAsync("Sam, " + text, T0, CancellationToken.None);
        var kopilot = await dispatcher.HandleAsync("Kopilot, " + text, T0, CancellationToken.None);

        Assert.False(room.Chat);
        Assert.Null(room.Reply);
        Assert.True(addressed.Chat);
        Assert.True(kopilot.Chat);
    }

    [Fact]
    public async Task Nur_mit_Anrede_bei_requireWakeWord()
    {
        var (dispatcher, dialog, _) = Create();
        dialog.RequireWakeWord = true;

        var plain = await dispatcher.HandleAsync("How are you today", T0, CancellationToken.None);
        var named = await dispatcher.HandleAsync("Hey Sam, how are you today", T0, CancellationToken.None);

        Assert.False(plain.Chat);
        Assert.True(named.Chat);
    }

    [Fact]
    public async Task Offener_Readback_hat_Vorrang()
    {
        var (dispatcher, dialog, gateway) = Create();
        dialog.ExecuteWithCallout = false;

        var readback = await dispatcher.HandleAsync("Flaps one", T0, CancellationToken.None);
        dialog.Spoken(readback.Reply!, T0.AddSeconds(1));
        var chat = await dispatcher.HandleAsync("How are you today", T0.AddSeconds(2), CancellationToken.None);

        Assert.NotNull(dialog.Pending);
        Assert.False(chat.Chat);
        Assert.Equal(VoiceDialog.SayAgain, chat.Reply!.Text);
        Assert.Equal(new[] { "prepare flaps 1" }, gateway.Calls);
    }

    [Fact]
    public async Task Laufende_Checkliste_hat_Vorrang()
    {
        var (dispatcher, dialog, _) = Create();

        await dispatcher.HandleAsync("Before start checklist", T0, CancellationToken.None);
        var answer = await dispatcher.HandleAsync("How are you today", T0.AddSeconds(5), CancellationToken.None);

        Assert.NotNull(dialog.Checklists.Active);
        Assert.False(answer.Chat);
    }

    [Fact]
    public async Task Offene_Bestaetigungspflicht_hat_Vorrang()
    {
        var (dispatcher, dialog, _) = Create();
        var acks = new AcknowledgmentTracker();
        dialog.Acknowledgments = acks;
        acks.Observe(new AlertEvent(1, T0, "FUEL_WARNING", AlertSeverity.Warning, AlertKind.Raise, "Fuel low", "message"));
        Assert.NotEmpty(acks.PendingTexts);

        var chat = await dispatcher.HandleAsync("How are you today", T0.AddSeconds(1), CancellationToken.None);

        Assert.False(chat.Chat);
    }

    [Fact]
    public async Task Stumm_geschaltet_kein_Smalltalk()
    {
        var (dispatcher, dialog, _) = Create();

        await dispatcher.HandleAsync("Kopilot pause", T0, CancellationToken.None);
        var chat = await dispatcher.HandleAsync("How are you today", T0.AddSeconds(1), CancellationToken.None);

        Assert.True(dialog.Muted);
        Assert.False(chat.Chat);
        Assert.Null(chat.Reply);
    }

    [Fact]
    public async Task Name_als_Anrede_wie_Kopilot()
    {
        var (dispatcher, _, gateway) = Create();

        var named = await dispatcher.HandleAsync("Sam, gear up", T0, CancellationToken.None);
        var hey = await dispatcher.HandleAsync("Hey Sam, gear up", T0, CancellationToken.None);
        var kopilot = await dispatcher.HandleAsync("Kopilot, gear up", T0, CancellationToken.None);

        Assert.Equal("gear.up", named.Intent!.ActionId);
        Assert.Equal("gear.up", hey.Intent!.ActionId);
        Assert.Equal("gear.up", kopilot.Intent!.ActionId);
        Assert.Equal(VoiceTarget.Kopilot, named.Route.Target);
        Assert.Equal(3, gateway.Calls.Count(c => c == "execute gear.up "));
    }

    [Theory]
    [InlineData("sam gear up", "sam", "kopilot gear up")]
    [InlineData("hey sam how are you", "sam", "kopilot how are you")]
    [InlineData("okay kopilot flaps 1", null, "kopilot flaps 1")]
    [InlineData("samuel gear up", "sam", "samuel gear up")]
    [InlineData("gear up sam", "sam", "gear up sam")]
    [InlineData("hey there", "sam", "hey there")]
    public void Anrede_wird_vereinheitlicht(string normalized, string? name, string expected)
    {
        Assert.Equal(expected, VoiceDispatcher.Address(normalized, name, ["kopilot"]));
    }

    [Fact]
    public async Task Antwort_des_Sprachmodells_loest_nie_etwas_aus()
    {
        var (dispatcher, dialog, gateway) = Create();

        var chat = await dispatcher.HandleAsync("Do you think we will be on time today?", T0, CancellationToken.None);
        var spoken = dialog.RememberChat(VoiceReply.Chat("Gear down, flaps three. Confirm?"));
        var confirm = await dispatcher.HandleAsync("Confirm", T0.AddSeconds(5), CancellationToken.None);
        var again = await dispatcher.HandleAsync("Say again", T0.AddSeconds(6), CancellationToken.None);

        Assert.True(chat.Chat);
        Assert.True(spoken.Smalltalk);
        Assert.Null(spoken.ReadbackToken);
        Assert.Null(dialog.Pending);
        Assert.Null(confirm.Reply);
        Assert.Empty(gateway.Calls);
        Assert.Equal("Gear down, flaps three. Confirm?", again.Reply!.Text);
    }

    [Fact]
    public async Task Briefing_stellt_den_First_Officer_vor()
    {
        var (dispatcher, dialog, _) = Create();
        dialog.Briefing = () => VoiceReply.Chat(FirstOfficerCasting.Introduction("Sam", "en"));

        var outcome = await dispatcher.HandleAsync("Kopilot, briefing", T0, CancellationToken.None);

        Assert.Equal("Hi, I'm Sam, I'll be your first officer today.", outcome.Reply!.Text);
        Assert.True(outcome.Reply.Smalltalk);
    }

    // ------------------------------------------------------------------ Vorrang in der Warteschlange

    [Theory]
    [InlineData(SpeechPriority.Callout, true)]
    [InlineData(SpeechPriority.Warning, true)]
    [InlineData(SpeechPriority.Response, true)]
    [InlineData(SpeechPriority.Atc, true)]
    [InlineData(SpeechPriority.Smalltalk, false)]
    public void Alles_unterbricht_Smalltalk(SpeechPriority incoming, bool interrupts)
    {
        Assert.Equal(interrupts, CalloutQueue.Interrupts(incoming, SpeechPriority.Smalltalk));
    }

    [Theory]
    [InlineData(SpeechPriority.Callout)]
    [InlineData(SpeechPriority.Response)]
    [InlineData(SpeechPriority.Atc)]
    public void Smalltalk_unterbricht_nichts_und_Callouts_bleiben_wie_bisher(SpeechPriority speaking)
    {
        Assert.False(CalloutQueue.Interrupts(SpeechPriority.Smalltalk, speaking));
        Assert.True(CalloutQueue.Interrupts(SpeechPriority.Warning, speaking));
        Assert.False(CalloutQueue.Interrupts(SpeechPriority.Callout, SpeechPriority.Response));
    }

    [Fact]
    public void Smalltalk_steht_ganz_hinten_und_kommt_nach_Unterbrechung_nicht_wieder()
    {
        var queue = new CalloutQueue();
        var chat = Item("Lovely weather today", SpeechPriority.Smalltalk);
        Assert.True(queue.Offer(chat, T0, null).Accepted);
        Assert.True(queue.Offer(Item("Lufthansa four alpha bravo, contact Langen Radar", SpeechPriority.Atc), T0, null).Accepted);

        var callout = queue.Offer(Item("One thousand to go", SpeechPriority.Callout), T0, SpeechPriority.Smalltalk);

        Assert.True(callout.Preempt);
        Assert.Equal(SpeechPriority.Callout, queue.Take(T0)!.Priority);
        Assert.Equal(SpeechPriority.Atc, queue.Take(T0)!.Priority);
        Assert.Equal(SpeechPriority.Smalltalk, queue.Take(T0)!.Priority);
        Assert.False(queue.Requeue(chat, T0));
        Assert.Equal(CalloutQueue.SmalltalkMaxAge, CalloutQueue.DefaultMaxAge(SpeechPriority.Smalltalk));
    }

    private static SpeechItem Item(string text, SpeechPriority priority) => new()
    {
        Text = text,
        Priority = priority,
        TimeUtc = T0,
        ExpiresUtc = T0 + CalloutQueue.DefaultMaxAge(priority),
        Plain = priority == SpeechPriority.Smalltalk,
    };

    // ------------------------------------------------------------------ Name und Stimme

    [Fact]
    public void Namenslisten_sind_sauber()
    {
        var all = FirstOfficerNames.Female.Concat(FirstOfficerNames.Male).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        string[] nato = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "mike", "november",
            "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu"];
        foreach (var name in all)
        {
            var word = FirstOfficerNames.AddressWord(name);
            Assert.NotNull(word);
            Assert.Equal(name.ToLowerInvariant(), word);
            Assert.DoesNotContain(word, nato);
            Assert.NotEqual("kopilot", word);
            Assert.Equal(IntentKind.Unknown, VoiceIntents.Recognize(word, ["kopilot"]).Kind);
            Assert.NotNull(FirstOfficerNames.GenderOf(name));
        }
    }

    [Fact]
    public void Stimme_passt_zum_Geschlecht_und_ist_nie_eine_ATC_Stimme()
    {
        var installed = FirstOfficerCasting.DefaultVoices;
        var atc = new[] { "bf_emma", "bm_george", "am_michael", "af_heart" };
        for (var seed = 0; seed < 200; seed++)
        {
            var p = FirstOfficerCasting.Create("flight", new FirstOfficerConfig(), null, null, installed, atc, new Random(seed), T0);

            Assert.Equal(p.Gender, FirstOfficerCasting.GenderOfVoice(p.Voice));
            Assert.Equal(p.Gender, FirstOfficerNames.GenderOf(p.Name));
            Assert.DoesNotContain(p.Voice, atc);
            Assert.Contains(p.Name, FirstOfficerNames.For(p.Gender));
        }
    }

    [Fact]
    public void Letzte_freie_Stimme_statt_der_ATC_Stimme()
    {
        var voice = FirstOfficerCasting.PickVoice(VoiceGender.Female, ["bf_emma", "af_heart", "bm_george"], ["bf_emma"], new Random(1));

        Assert.Equal("af_heart", voice);
    }

    [Theory]
    [InlineData("Emma", "auto", VoiceGender.Female)]
    [InlineData("Sam", "auto", VoiceGender.Male)]
    [InlineData("Kim", "male", VoiceGender.Male)]
    [InlineData("Kim", "female", VoiceGender.Female)]
    public void Fester_Name_bestimmt_das_Geschlecht(string name, string gender, VoiceGender expected)
    {
        var config = new FirstOfficerConfig { Name = name, Gender = gender };

        var p = FirstOfficerCasting.Create("f", config, null, null, [], [], new Random(3), T0);

        Assert.Equal(name, p.Name);
        Assert.True(p.FixedName);
        Assert.Equal(expected, p.Gender);
        Assert.Equal(expected, FirstOfficerCasting.GenderOfVoice(p.Voice));
    }

    [Fact]
    public void Unbekannter_fester_Name_bekommt_immer_dasselbe_Geschlecht()
    {
        var config = new FirstOfficerConfig { Name = "Kim" };
        var a = FirstOfficerCasting.Create("a", config, null, null, [], [], new Random(1), T0);
        var b = FirstOfficerCasting.Create("b", config, null, null, [], [], new Random(99), T0);

        Assert.Equal(a.Gender, b.Gender);
    }

    [Fact]
    public void Feste_Stimme_bestimmt_den_Namen()
    {
        var p = FirstOfficerCasting.Create("f", new FirstOfficerConfig(), "bm_george", VoiceGender.Female, [], [], new Random(7), T0);

        Assert.Equal("bm_george", p.Voice);
        Assert.True(p.FixedVoice);
        Assert.Equal(VoiceGender.Male, p.Gender);
        Assert.Contains(p.Name, FirstOfficerNames.Male);
    }

    [Fact]
    public void Windows_Stimme_bestimmt_das_Geschlecht_des_Namens()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var p = FirstOfficerCasting.Create("f", new FirstOfficerConfig(), null, VoiceGender.Female, [], [], new Random(seed), T0);
            Assert.Equal(VoiceGender.Female, p.Gender);
        }
    }

    [Fact]
    public void Gleicher_Flug_gleicher_First_Officer_neuer_Plan_neuer()
    {
        var store = new FirstOfficerStore(Path.Combine(Path.GetTempPath(), "kopilot-tests", Guid.NewGuid().ToString("N"), FirstOfficerStore.FileName));
        var time = new ManualTime(T0);
        var roster = new FirstOfficerRoster(store, time, new Random(5));
        var config = new FirstOfficerConfig();

        var (first, created) = roster.Ensure("flight-1", config, null, null, [], []);
        var (again, changedAgain) = roster.Ensure("flight-1", config, null, null, [], []);
        roster.MarkIntroduced();

        // Neustart von Kopilot: derselbe Flug aus der Datei, schon vorgestellt
        var restarted = new FirstOfficerRoster(store, time, new Random(77));
        var (restored, changedRestored) = restarted.Ensure("flight-1", config, null, null, [], []);

        var (next, changedNext) = restarted.Ensure("flight-2", config, null, null, [], []);

        Assert.True(created);
        Assert.False(changedAgain);
        Assert.Equal(first, again);
        Assert.False(changedRestored);
        Assert.Equal(first!.Name, restored!.Name);
        Assert.Equal(first.Voice, restored.Voice);
        Assert.True(restored.Introduced);
        Assert.True(changedNext);
        Assert.Equal("flight-2", next!.FlightKey);
        Assert.False(next.Introduced);
    }

    [Fact]
    public void Ohne_Plan_bleibt_der_First_Officer_wenn_der_Plan_kommt()
    {
        var roster = new FirstOfficerRoster(null, new ManualTime(T0), new Random(5));
        var config = new FirstOfficerConfig();

        var (unplanned, _) = roster.Ensure(FirstOfficerRoster.Unplanned, config, null, null, [], []);
        var (planned, changed) = roster.Ensure("flight-1", config, null, null, [], []);

        Assert.False(changed);
        Assert.Equal(unplanned!.Name, planned!.Name);
        Assert.Equal("flight-1", planned.FlightKey);
    }

    [Fact]
    public void Fester_Name_aus_den_Einstellungen_gilt_sofort_und_aus_heisst_aus()
    {
        var roster = new FirstOfficerRoster(null, new ManualTime(T0), new Random(5));
        roster.Ensure("flight-1", new FirstOfficerConfig(), null, null, [], []);

        var (named, changed) = roster.Ensure("flight-1", new FirstOfficerConfig { Name = "Lena" }, null, null, [], []);
        var (off, changedOff) = roster.Ensure("flight-1", new FirstOfficerConfig { Enabled = false }, null, null, [], []);

        Assert.True(changed);
        Assert.Equal("Lena", named!.Name);
        Assert.Equal(VoiceGender.Female, named.Gender);
        Assert.True(changedOff);
        Assert.Null(off);
        Assert.Null(roster.Current);
    }

    [Fact]
    public void Vorstellung_in_der_Sprache_des_Smalltalks()
    {
        Assert.Equal("Hi, I'm Sam, I'll be your first officer today.", FirstOfficerCasting.Introduction("Sam", "en"));
        using (Loc.Use("de"))
        {
            Assert.Equal("Hallo, ich bin Lena, ich fliege heute als dein First Officer mit.", FirstOfficerCasting.Introduction("Lena", "de"));
            Assert.Equal("Hi, I'm Lena, I'll be your first officer today.", FirstOfficerCasting.Introduction("Lena", "en"));
        }
    }

    [Fact]
    public void Neue_ATC_Stationen_meiden_die_Stimme_des_First_Officers()
    {
        var config = new AtcVoiceConfig { Pool = ["bf_emma", "bm_george"], AvoidCalloutVoice = true };
        var kopilot = "bf_emma";
        var assigner = new AtcVoiceAssigner(config, "flight", null, null, () => kopilot);

        var tower = assigner.VoiceFor("EDDF/tower", AtcStationType.Tower);
        kopilot = "bm_george";
        var ground = assigner.VoiceFor("EDDF/ground", AtcStationType.Ground);

        Assert.Equal("bm_george", tower);
        Assert.Equal("bf_emma", ground);
    }

    // ------------------------------------------------------------------ Sprache und Piper

    private sealed class FakeEngine(TtsState state) : ITtsEngine
    {
        public TtsState State { get; set; } = state;

        public string Id => PiperSpeechEngine.EngineId;

        public IReadOnlyList<TtsVoice> Voices => PiperSpeechEngine.Catalog;

        public string? Error => State == TtsState.Failed ? "download failed" : null;

        public List<TtsVoice> Prepared { get; } = [];

        public TtsState StateOf(TtsVoice voice) => State;

        public Task PrepareAsync(TtsVoice voice, CancellationToken ct)
        {
            Prepared.Add(voice);
            return Task.CompletedTask;
        }

        public Task<SpeechClip> SynthesizeAsync(string text, TtsVoice voice, double rate, CancellationToken ct) =>
            Task.FromResult(new SpeechClip([0.1f], 22_050));
    }

    [Fact]
    public void Englisch_spricht_Kokoro()
    {
        var choice = SmalltalkVoices.Choose("en", VoiceGender.Female, "Emma", [new FakeEngine(TtsState.Ready)]);

        Assert.Equal("en", choice.Language);
        Assert.Null(choice.Voice);
        Assert.False(choice.FellBack);
    }

    [Theory]
    [InlineData(VoiceGender.Male)]
    [InlineData(VoiceGender.Female)]
    public void Deutsch_spricht_Piper_passend_zum_Geschlecht(VoiceGender gender)
    {
        foreach (var name in FirstOfficerNames.For(gender))
        {
            var choice = SmalltalkVoices.Choose("de", gender, name, [new FakeEngine(TtsState.Ready)]);

            Assert.Equal("de", choice.Language);
            Assert.False(choice.FellBack);
            Assert.Equal(gender, choice.Voice!.Gender);
            Assert.Equal("de", choice.Voice.Language);
            Assert.StartsWith("piper:de_DE-", choice.Voice.Key);
        }
    }

    [Fact]
    public void Gleicher_Name_gleiche_Piper_Stimme()
    {
        var a = SmalltalkVoices.Choose("de", VoiceGender.Female, "Lena", [new FakeEngine(TtsState.Ready)]);
        var b = SmalltalkVoices.Choose("de", VoiceGender.Female, "Lena", [new FakeEngine(TtsState.Ready)]);

        Assert.Equal(a.Voice, b.Voice);
    }

    [Theory]
    [InlineData(TtsState.Idle, "downloading")]
    [InlineData(TtsState.Downloading, "downloading")]
    [InlineData(TtsState.Failed, "failed")]
    public void Piper_noch_nicht_da_heisst_Englisch(TtsState state, string reason)
    {
        var choice = SmalltalkVoices.Choose("de", VoiceGender.Male, "Sam", [new FakeEngine(state)]);

        Assert.Equal("en", choice.Language);
        Assert.Equal("de", choice.Wanted);
        Assert.Equal(reason, choice.Reason);
        Assert.NotNull(choice.Voice);
    }

    [Fact]
    public void Sprache_ohne_Stimme_heisst_Englisch()
    {
        var none = SmalltalkVoices.Choose("fr", VoiceGender.Male, "Sam", [new FakeEngine(TtsState.Ready)]);
        var noEngine = SmalltalkVoices.Choose("de", VoiceGender.Male, "Sam", []);

        Assert.Equal("en", none.Language);
        Assert.Equal("noVoice", none.Reason);
        Assert.Null(none.Voice);
        Assert.Equal("noVoice", noEngine.Reason);
    }

    [Fact]
    public void Piper_Katalog_Adressen_und_Hilfen()
    {
        Assert.Contains(PiperSpeechEngine.Catalog, v => v.Language == "de" && v.Gender == VoiceGender.Male);
        Assert.Contains(PiperSpeechEngine.Catalog, v => v.Language == "de" && v.Gender == VoiceGender.Female);
        Assert.Equal(
            "https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx",
            PiperSpeechEngine.VoiceUrl("de_DE-thorsten-medium", ".onnx"));
        Assert.Equal("0.80", PiperSpeechEngine.LengthScale(1.25));
        Assert.Equal("1.00", PiperSpeechEngine.LengthScale(double.NaN));
        Assert.Equal("Hallo Welt, wie geht's?", PiperSpeechEngine.OneLine("Hallo\r\nWelt,\twie   geht's?"));
        Assert.Equal(("piper", "de_DE-kerstin-low"), TtsVoice.ParseKey("piper:de_DE-kerstin-low"));
        Assert.Null(TtsVoice.ParseKey("bf_emma"));

        var registry = new TtsEngineRegistry([new FakeEngine(TtsState.Ready)]);
        Assert.Equal("de_DE-kerstin-low", registry.Find("piper:de_DE-kerstin-low")!.Value.Voice.Id);
        Assert.Null(registry.Find("piper:fr_FR-unknown-low"));
        Assert.Null(registry.Find("bm_george"));
    }

    [Fact]
    public void Piper_WAV_wird_gelesen()
    {
        float[] samples = [0f, 0.5f, -0.5f, 0.25f];
        var wav = WavEncoder.Encode(samples, 22_050);

        var clip = PiperSpeechEngine.ReadWav(wav);

        Assert.Equal(22_050, clip.SampleRate);
        Assert.Equal(samples.Length, clip.Samples.Length);
        Assert.InRange(clip.Samples[1], 0.49f, 0.51f);
        Assert.InRange(clip.Samples[2], -0.51f, -0.49f);
        Assert.Throws<InvalidDataException>(() => PiperSpeechEngine.ReadWav(Encoding.ASCII.GetBytes("not a wave file")));
    }

    // ------------------------------------------------------------------ Dienst: Grenzen, Fehler, Schlüssel

    private sealed class FakeModel : IChatModel
    {
        public List<ChatRequest> Requests { get; } = [];

        public Func<ChatRequest, CancellationToken, Task<string>> Answer { get; set; } = (_, _) => Task.FromResult("Sunny and calm, captain.");

        public Task<string> CompleteAsync(SmalltalkConfig config, ChatRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Answer(request, ct);
        }
    }

    private static (SmalltalkService Service, FakeModel Model) Service(SmalltalkOptions? options = null, TimeSpan? timeout = null)
    {
        options ??= new SmalltalkOptions { Provider = "ollama", Url = "http://localhost:11434", Model = "llama3.2", RequestsPerMinute = 6 };
        var model = new FakeModel();
        var service = new SmalltalkService(
            Options.Create(new KopilotOptions { Smalltalk = options }),
            NullLogger<SmalltalkService>.Instance,
            model,
            new ManualTime(T0),
            timeout);
        return (service, model);
    }

    private static SmalltalkQuestion Question(string text = "How is the weather in Berlin?", string language = "en") =>
        new(text, language, "Sam", new SmalltalkContext { SimConnected = true, Phase = "cruise", Destination = "EDDB (Berlin Brandenburg)", FuelKg = 6400 });

    [Fact]
    public async Task Antwort_mit_Kontext_Rolle_und_Verlauf()
    {
        var (service, model) = Service();

        var first = await service.AskAsync(Question(), CancellationToken.None);
        await service.AskAsync(Question("And at the alternate?"), CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal("Sunny and calm, captain.", first.Text);
        var system = model.Requests[0].System;
        Assert.Contains("Sam, the first officer", system);
        Assert.Contains("Answer in English", system);
        Assert.Contains("read-only", system);
        Assert.Contains("destination: EDDB (Berlin Brandenburg)", system);
        Assert.Contains("fuel on board: 6400 kg", system);
        Assert.Single(model.Requests[0].Messages);
        Assert.Equal(3, model.Requests[1].Messages.Count);
        Assert.Equal(ChatTurn.Assistant, model.Requests[1].Messages[1].Role);
    }

    [Fact]
    public async Task Deutsch_im_Systemtext_und_Ersatz_auf_Deutsch()
    {
        using var language = Loc.Use("de");
        var (service, model) = Service();
        model.Answer = (_, _) => throw new HttpRequestException("connection refused");

        var answer = await service.AskAsync(Question("Wie ist das Wetter in Berlin?", "de"), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal("Ich kann gerade nicht plaudern.", answer.Text);
        Assert.Contains("Answer in German", model.Requests[0].System);
    }

    [Fact]
    public async Task Ausgeschaltet_fragt_nichts()
    {
        var (service, model) = Service(new SmalltalkOptions { Provider = "off" });

        var answer = await service.AskAsync(Question(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal("I can't chat right now.", answer.Text);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Grenze_pro_Minute_antwortet_sofort()
    {
        var (service, model) = Service(new SmalltalkOptions { Provider = "ollama", Url = "http://localhost:11434", Model = "m", RequestsPerMinute = 1 });

        var first = await service.AskAsync(Question(), CancellationToken.None);
        var second = await service.AskAsync(Question(), CancellationToken.None);

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal("I can't chat right now.", second.Text);
        Assert.Contains("per minute", second.Reason);
        Assert.Single(model.Requests);
        Assert.Equal(1, service.Status.RequestsLastMinute);
    }

    [Fact]
    public async Task Zeitlimit_statt_haengen()
    {
        var (service, model) = Service(timeout: TimeSpan.FromMilliseconds(150));
        model.Answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var answer = await service.AskAsync(Question(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal("I can't chat right now.", answer.Text);
        Assert.Contains("no answer", answer.Reason);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task Fehler_verraten_den_Schluessel_nie()
    {
        var (service, model) = Service(new SmalltalkOptions { Provider = "claude", ApiKey = Key });
        model.Answer = (_, _) => throw new ChatModelException($"HTTP 401: invalid x-api-key {Key} (Bearer {Key})");

        var answer = await service.AskAsync(Question(), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.DoesNotContain("SECRET", answer.Reason);
        Assert.DoesNotContain("SECRET", service.Status.LastError);
        Assert.DoesNotContain("SECRET", service.Config.ToString());
        Assert.True(service.Status.KeySet);
    }

    [Theory]
    [InlineData("sk-proj-abcdefghijklmnop1234", "sk-proj-***")]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "Authorization: Bearer ***")]
    [InlineData("nothing secret here", "nothing secret here")]
    public void Schluessel_werden_aus_Texten_entfernt(string text, string expected)
    {
        Assert.Equal(expected, SmalltalkService.Redact(text, null));
    }

    [Fact]
    public void Antwort_wird_sprechbar_und_kurz()
    {
        Assert.Equal("Sunny and calm.", SmalltalkPrompt.Clean("<think>The user asks about weather.</think>**Sunny** and *calm*.", 40));
        Assert.Equal("First point. Second point.", SmalltalkPrompt.Clean("- First point.\n- Second point.", 40));
        Assert.Equal("One two three four five.", SmalltalkPrompt.Clean("One two three four five. Six seven eight nine ten eleven", 8));
        Assert.EndsWith("…", SmalltalkPrompt.Clean("one two three four five six seven eight nine ten", 5));
    }

    [Fact]
    public void Einstellungen_werden_geprueft_und_begrenzt()
    {
        Assert.NotNull(SmalltalkConfig.From(new SmalltalkOptions { Provider = "claude" }).Problem());
        Assert.Null(SmalltalkConfig.From(new SmalltalkOptions { Provider = "claude", ApiKey = Key }).Problem());
        Assert.Equal(SmalltalkConfig.DefaultClaudeModel, SmalltalkConfig.From(new SmalltalkOptions { Provider = "claude", ApiKey = Key }).EffectiveModel);
        Assert.NotNull(SmalltalkConfig.From(new SmalltalkOptions { Provider = "ollama", Model = "m" }).Problem());
        Assert.NotNull(SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai" }).Problem());
        Assert.Null(SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Model = "gpt" }).Problem());
        Assert.Equal(SmalltalkConfig.OpenAiUrl, SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Model = "gpt" }).EffectiveUrl);
        Assert.False(SmalltalkConfig.From(new SmalltalkOptions { Provider = "nonsense" }).Enabled);

        var clamped = SmalltalkConfig.From(new SmalltalkOptions { Provider = "ollama", MaxReplyWords = 5000, RequestsPerMinute = 0, TimeoutSec = 999 });
        Assert.Equal(150, clamped.MaxReplyWords);
        Assert.Equal(1, clamped.RequestsPerMinute);
        Assert.Equal(30, clamped.TimeoutSec);
    }

    // ------------------------------------------------------------------ HTTP an die Anbieter

    [Fact]
    public async Task Claude_Anfrage_und_Antwort()
    {
        var config = SmalltalkConfig.From(new SmalltalkOptions { Provider = "claude", ApiKey = Key });
        using var message = HttpChatModel.Build(config, new ChatRequest("system text", [new ChatTurn(ChatTurn.User, "hello")], 100));
        using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());

        Assert.Equal("https://api.anthropic.com/v1/messages", message.RequestUri!.ToString());
        Assert.Equal(Key, message.Headers.GetValues("x-api-key").Single());
        Assert.Equal(HttpChatModel.AnthropicVersion, message.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("claude-haiku-5-5", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("system text", body.RootElement.GetProperty("system").GetString());
        Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Hi there.", HttpChatModel.Parse("claude", """{"content":[{"type":"text","text":"Hi "},{"type":"text","text":"there."}]}"""));
    }

    [Fact]
    public async Task OpenAI_kompatibel_Anfrage_und_Antwort()
    {
        var config = SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Url = "http://192.168.1.20:1234/v1/", Model = "local", ApiKey = "abc" });
        using var message = HttpChatModel.Build(config, new ChatRequest("system text", [new ChatTurn(ChatTurn.User, "hello")], 100));
        using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());

        Assert.Equal("http://192.168.1.20:1234/v1/chat/completions", message.RequestUri!.ToString());
        Assert.Equal("Bearer abc", message.Headers.Authorization!.ToString());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Hello.", HttpChatModel.Parse("openai", """{"choices":[{"message":{"role":"assistant","content":"Hello."}}]}"""));
    }

    [Fact]
    public async Task Ollama_Anfrage_und_Antwort()
    {
        var config = SmalltalkConfig.From(new SmalltalkOptions { Provider = "ollama", Url = "http://192.168.1.20:11434", Model = "llama3.2" });
        using var message = HttpChatModel.Build(config, new ChatRequest("system text", [new ChatTurn(ChatTurn.User, "hello")], 100));
        using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());

        Assert.Equal("http://192.168.1.20:11434/api/chat", message.RequestUri!.ToString());
        Assert.Null(message.Headers.Authorization);
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(100, body.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal("Hallo.", HttpChatModel.Parse("ollama", """{"message":{"role":"assistant","content":"Hallo."},"done":true}"""));
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task HTTP_Fehler_ohne_Schluessel_in_der_Meldung()
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key """ + Key + "\"}}"));
        var model = new HttpChatModel(http);
        var config = SmalltalkConfig.From(new SmalltalkOptions { Provider = "claude", ApiKey = Key });

        var ex = await Assert.ThrowsAsync<ChatModelException>(() => model.CompleteAsync(config, new ChatRequest("s", [new ChatTurn(ChatTurn.User, "hi")], 50), CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);
    }
}
