using System.Net;
using System.Text;
using System.Text.Json;
using Duopilo.Atc;
using Duopilo.Input;
using Duopilo.Localization;
using Duopilo.Sim;
using Duopilo.Smalltalk;
using Duopilo.Speech;
using Duopilo.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Duopilo.Tests;

/// <summary>
/// Befunde aus dem ersten Test mit v0.10.0-beta.1 (Logs 30.10.): Whisper-Konfidenz, leere Antworten von gpt-oss, Anrede mit
/// dem Namen des First Officers, Stimmen, Sprachschleife nur bei laufendem Sim.
/// </summary>
public sealed class VoiceAfterBetaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 20, 55, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Gateway : IVoiceActionGateway
    {
        public List<string> Calls { get; } = [];

        public Task<InputResult> PrepareAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"prepare {actionId} {value}");
            return Task.FromResult(new InputResult(InputResult.AwaitingConfirmation, Token: "t1", Action: actionId, Value: value, Readback: "Gear up"));
        }

        public Task<InputResult> ExecuteAsync(string actionId, string? value, CancellationToken ct)
        {
            Calls.Add($"execute {actionId} {value}");
            return Task.FromResult(new InputResult(InputResult.Executed, Action: actionId, Value: value, Readback: "Gear up", Confirmed: true) { Callout = "Gear up" });
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

    private sealed class NoState : IVoiceFlightStateSource
    {
        public VoiceFlightState Read() => new() { Connected = true, OnGround = true };
    }

    /// <summary>ATC-Modul zum Mithören: nimmt jeden Funkspruch an.</summary>
    private sealed class RecordingAtc : IAtcLink
    {
        public List<string> Heard { get; } = [];

        public bool Enabled => true;

        public AtcRoutingInfo RoutingInfo() => AtcRoutingInfo.Empty;

        public Task<AtcTransmitResult> TransmitAsync(string text, CancellationToken ct)
        {
            Heard.Add(text);
            return Task.FromResult(new AtcTransmitResult(true, null, []));
        }
    }

    private static Task<VoiceReply?> Say(VoiceDialog dialog, string raw, double? confidence, DateTimeOffset start)
    {
        var normalized = VoiceText.Normalize(raw);
        return dialog.HandleAsync(VoiceIntents.Recognize(normalized, ["kopilot"]), normalized, false, start, confidence, CancellationToken.None);
    }

    // ------------------------------------------------------------------ 1. Konfidenz

    [Fact]
    public void Konfidenz_ohne_berechnete_Wahrscheinlichkeiten_ist_unbekannt()
    {
        // So lieferte Whisper.net 1.9.1 ohne WithProbabilities(): Probability = 0 – das ist kein „unsicher“
        Assert.Null(WhisperRecognizer.Confidence([("Hey, Eric?", 0f, 0f)]));
        Assert.Null(WhisperRecognizer.Confidence([]));
        Assert.Null(WhisperRecognizer.Confidence([("Gear up.", float.NaN, float.NaN)]));
    }

    [Fact]
    public void Konfidenz_aus_den_Token_Wahrscheinlichkeiten_nach_Textlaenge()
    {
        Assert.Equal(0.9, WhisperRecognizer.Confidence([("Gear up.", 0.9f, 0.99f)])!.Value, 3);

        // "Flaps one" (9 Zeichen) mit 0,8 und "x" (1 Zeichen) mit 0,2 → 0,74; ein Segment ohne Wert zählt nicht
        Assert.Equal(0.74, WhisperRecognizer.Confidence([("Flaps one", 0.8f, 0.95f), ("x", 0.2f, 0.3f), ("y", 0f, 0f)])!.Value, 3);
    }

    [Fact]
    public async Task Unbekannte_Konfidenz_fuehrt_aus_unsichere_liest_zurueck()
    {
        var gateway = new Gateway();
        var dialog = new VoiceDialog(gateway, new NoState(), new ManualTime(T0));

        var unknown = await Say(dialog, "Gear up", WhisperRecognizer.Confidence([("Gear up.", 0f, 0f)]), T0);
        var sure = await Say(dialog, "Gear up", 0.92, T0);
        var unsure = await Say(dialog, "Gear up", 0.31, T0);

        Assert.Null(unknown?.ReadbackToken);
        Assert.Null(sure?.ReadbackToken);
        Assert.Equal("t1", unsure?.ReadbackToken);
        Assert.Equal(["execute gear.up ", "execute gear.up ", "prepare gear.up "], gateway.Calls);
    }

    // ------------------------------------------------------------------ 4. Name des First Officers

    [Fact]
    public void Name_steht_vorn_im_Whisper_Prompt()
    {
        Assert.Equal(WhisperRecognizer.DefaultPrompt, WhisperRecognizer.PromptFor(null, null));
        Assert.Equal("Erik, " + WhisperRecognizer.DefaultPrompt, WhisperRecognizer.PromptFor("", "Erik"));
        Assert.Equal("Erik, Kopilot, flaps", WhisperRecognizer.PromptFor("Kopilot, flaps", "Erik"));
        Assert.Equal("Hey Sam, flaps one", WhisperRecognizer.PromptFor("Hey Sam, flaps one", "Sam"));

        // Kommt der Hinweis bei Stille zurück, wird er auch mit dem Namen davor verworfen
        Assert.Equal("", TranscriptFilter.Clean(WhisperRecognizer.PromptFor(null, "Erik")));
        Assert.Equal("Erik, gear up.", TranscriptFilter.Clean("Erik, gear up."));
    }

    [Theory]
    [InlineData("erik", "eric")]
    [InlineData("erik", "erick")]
    [InlineData("erik", "erich")]
    [InlineData("nico", "niko")]
    [InlineData("lucas", "lukas")]
    [InlineData("sophie", "sofie")]
    [InlineData("sophie", "sophia")]
    [InlineData("hannah", "hanna")]
    [InlineData("sarah", "sara")]
    [InlineData("clara", "klara")]
    [InlineData("thomas", "tomas")]
    [InlineData("lucy", "lucie")]
    [InlineData("marco", "marko")]
    [InlineData("jonas", "jonah")]
    [InlineData("peter", "pieter")]
    [InlineData("oliver", "olivier")]
    [InlineData("simon", "simone")]
    [InlineData("adrian", "adrien")]
    [InlineData("felix", "phelix")]
    [InlineData("emma", "ema")]
    [InlineData("anna", "ana")]
    [InlineData("lisa", "liza")]
    [InlineData("lena", "leena")]
    [InlineData("laura", "lara")]
    [InlineData("maria", "marie")]
    [InlineData("mia", "miya")]
    [InlineData("tim", "timm")]
    [InlineData("ben", "benn")]
    public void Schreibvarianten_des_Namens_gelten_als_Anrede(string name, string heard)
    {
        Assert.True(FirstOfficerNames.IsAddress(heard, name), $"{heard} → {name}");
    }

    [Theory]
    [InlineData("ben", "been")]
    [InlineData("paul", "pull")]
    [InlineData("nico", "nice")]
    [InlineData("lucy", "lucky")]
    [InlineData("alice", "alike")]
    [InlineData("leon", "lean")]
    [InlineData("noah", "now")]
    [InlineData("nora", "north")]
    [InlineData("lisa", "list")]
    [InlineData("hannah", "hand")]
    [InlineData("marco", "mark")]
    [InlineData("hugo", "huge")]
    [InlineData("clara", "klar")]
    [InlineData("sophie", "sorry")]
    [InlineData("peter", "pepper")]
    [InlineData("erik", "epic")]
    [InlineData("nina", "nine")]
    [InlineData("julia", "july")]
    [InlineData("martin", "margin")]
    [InlineData("oliver", "olive")]
    [InlineData("simon", "summon")]
    [InlineData("lena", "lens")]
    [InlineData("emma", "era")]
    [InlineData("sam", "some")]
    [InlineData("tim", "time")]
    [InlineData("leo", "lee")]
    [InlineData("erik", "frik")]
    public void Normale_Woerter_sind_keine_Anrede(string name, string heard)
    {
        Assert.False(FirstOfficerNames.IsAddress(heard, name), $"{heard} → {name}");
    }

    [Fact]
    public void Alle_Namen_der_Listen_tolerant_und_ohne_Befehlswoerter()
    {
        string[] commands =
        [
            "gear", "flaps", "speed", "check", "checked", "confirm", "correct", "negative", "affirm", "roger", "copy", "standby", "heading",
            "altitude", "squawk", "mayday", "briefing", "checklist", "kopilot", "copilot", "duopilo", "hello", "okay", "thanks", "please",
        ];
        foreach (var name in FirstOfficerNames.Female.Concat(FirstOfficerNames.Male))
        {
            var word = FirstOfficerNames.AddressWord(name)!;
            Assert.True(FirstOfficerNames.IsAddress(word, word), name);
            Assert.True(FirstOfficerNames.IsAddress(word + word[^1], word), $"{name}: doppelter Endbuchstabe");
            foreach (var command in commands)
            {
                Assert.False(FirstOfficerNames.IsAddress(command, word), $"{command} → {name}");
            }

            foreach (var common in FirstOfficerNames.CommonWords)
            {
                Assert.False(FirstOfficerNames.IsAddress(common, word), $"{common} → {name}");
            }
        }
    }

    [Theory]
    [InlineData("hey eric", "erik", "kopilot")]
    [InlineData("eric gear up", "erik", "kopilot gear up")]
    [InlineData("hey niko how are you", "nico", "kopilot how are you")]
    [InlineData("nice landing", "nico", "nice landing")]
    [InlineData("pull heading", "paul", "pull heading")]
    [InlineData("been there", "ben", "been there")]
    [InlineData("hey there", "erik", "hey there")]
    public void Anrede_mit_Schreibvariante_wird_vereinheitlicht(string normalized, string name, string expected)
    {
        Assert.Equal(expected, VoiceDispatcher.Address(normalized, name, ["kopilot"]));
    }

    [Fact]
    public async Task Hey_Eric_aus_dem_Testflug_ist_eine_Anrede_an_Erik()
    {
        var dialog = new VoiceDialog(new Gateway(), new NoState(), new ManualTime(T0));
        var dispatcher = new VoiceDispatcher(dialog, new RecordingAtc(), new VoiceRouter(["kopilot"]), ["kopilot"])
        {
            PersonaName = () => FirstOfficerNames.AddressWord("Erik"),
            ChatWords = () => 12,
        };

        var outcome = await dispatcher.HandleAsync("Hey, Eric?", T0, CancellationToken.None);

        Assert.Equal("kopilot", outcome.Normalized);
        Assert.Equal(VoiceTarget.Kopilot, outcome.Route.Target);
    }

    // ------------------------------------------------------------------ 6. Notruf in der Sprachschleife

    [Fact]
    public async Task Notruf_geht_immer_an_ATC_ohne_Station_und_mitten_in_der_Checkliste()
    {
        var atc = new RecordingAtc();
        var dialog = new VoiceDialog(new Gateway(), new NoState(), new ManualTime(T0));
        var dispatcher = new VoiceDispatcher(dialog, atc, new VoiceRouter(["kopilot"]), ["kopilot"]) { ChatWords = () => 12 };
        await dispatcher.HandleAsync("Before start checklist", T0, CancellationToken.None);
        Assert.NotNull(dialog.Checklists.Active);

        var mayday = await dispatcher.HandleAsync("Mayday, Mayday, engine fire", T0.AddSeconds(5), CancellationToken.None);
        var panPan = await dispatcher.HandleAsync("Pan pan", T0.AddSeconds(9), CancellationToken.None);
        var cancel = await dispatcher.HandleAsync("Cancel Mayday", T0.AddSeconds(15), CancellationToken.None);

        Assert.All(new[] { mayday, panPan, cancel }, o => Assert.Equal(VoiceTarget.Atc, o.Route.Target));
        Assert.All(new[] { mayday, panPan, cancel }, o => Assert.False(o.Chat));
        Assert.Equal(["Mayday, Mayday, engine fire", "Pan pan", "Cancel Mayday"], atc.Heard);
    }

    // ------------------------------------------------------------------ 2. Reasoning (gpt-oss über Groq)

    private const string GroqUrl = "https://api.groq.com/openai/v1";

    /// <summary>
    /// Antwort von Groq für openai/gpt-oss-20b mit max_tokens 104 (2 × 40 Wörter + 24), nach dem Format der Groq-API: das Modell
    /// denkt in "reasoning", "content" bleibt leer, finish_reason "length" – so kam „empty answer“ im Log zustande.
    /// </summary>
    private const string GroqLengthResponse = """
        {"id":"chatcmpl-5f0e2c1b-7a8d-4c1e-9b0a-2f3e4d5c6b7a","object":"chat.completion","created":1761857700,"model":"openai/gpt-oss-20b",
         "choices":[{"index":0,"message":{"role":"assistant","content":"","reasoning":"The pilot says \"Hey, Eric?\" — they address me by name. I should answer briefly and friendly as first officer. Keep it under forty words, no lists. Maybe ask what they need. Let's craft: \"Yes, captain"},
         "logprobs":null,"finish_reason":"length"}],
         "usage":{"queue_time":0.0512,"prompt_tokens":587,"prompt_time":0.031,"completion_tokens":104,"completion_time":0.087,"total_tokens":691,"total_time":0.118,
         "completion_tokens_details":{"reasoning_tokens":104}},
         "usage_breakdown":null,"system_fingerprint":"fp_0f2d1a9c3b","x_groq":{"id":"req_01k8v0q3a9e6s7t2b4c5d8f1g0"},"service_tier":"on_demand"}
        """;

    /// <summary>Dieselbe Frage mit reasoning_effort "low" und Spielraum: Gedanken in "reasoning", Antwort in "content".</summary>
    private const string GroqOkResponse = """
        {"id":"chatcmpl-9a1b","object":"chat.completion","created":1761857760,"model":"openai/gpt-oss-20b",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Yes, captain? Everything looks good up here.","reasoning":"Short friendly answer."},
         "logprobs":null,"finish_reason":"stop"}],
         "usage":{"prompt_tokens":591,"completion_tokens":38,"total_tokens":629,"completion_tokens_details":{"reasoning_tokens":21}},
         "x_groq":{"id":"req_01k8v0r"}}
        """;

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void Reasoning_auto_sendet_low_nur_bei_gpt_oss_und_gibt_Spielraum()
    {
        var oss = SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Url = GroqUrl, Model = "openai/gpt-oss-20b", ApiKey = "gsk_test" });
        var llama = SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Url = GroqUrl, Model = "llama-3.3-70b-versatile" });
        var off = SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Url = GroqUrl, Model = "openai/gpt-oss-120b", Reasoning = "off" });
        var medium = SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Url = GroqUrl, Model = "qwen/qwen3-32b", Reasoning = "Medium" });
        var claude = SmalltalkConfig.From(new SmalltalkOptions { Provider = "claude", ApiKey = "sk-ant-x", Reasoning = "high" });

        Assert.Equal("auto", oss.Reasoning);
        Assert.Equal("low", oss.ReasoningEffort);
        Assert.Equal(104, oss.AnswerTokens);
        Assert.Equal(104 + 512, oss.MaxTokens);
        Assert.Null(llama.ReasoningEffort);
        Assert.Equal(104, llama.MaxTokens);
        Assert.Null(off.ReasoningEffort);
        Assert.Equal(104, off.MaxTokens);
        Assert.Equal("medium", medium.ReasoningEffort);
        Assert.Equal(104 + 1024, medium.MaxTokens);
        Assert.Null(claude.ReasoningEffort);

        Assert.Equal("high", SmalltalkReasoning.Normalize("Hoch"));
        Assert.Equal("auto", SmalltalkReasoning.Normalize("whatever"));
        Assert.False(SmalltalkReasoning.IsValid("whatever"));
        Assert.True(SmalltalkReasoning.IsValid("LOW"));
    }

    [Fact]
    public async Task Anfrage_an_Groq_mit_reasoning_effort_nur_bei_gpt_oss()
    {
        var oss = SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Url = GroqUrl, Model = "openai/gpt-oss-20b", ApiKey = "gsk_test" });
        var llama = SmalltalkConfig.From(new SmalltalkOptions { Provider = "openai", Url = GroqUrl, Model = "llama-3.3-70b-versatile", ApiKey = "gsk_test" });

        using var withEffort = HttpChatModel.Build(oss, new ChatRequest("system", [new ChatTurn(ChatTurn.User, "Hey, Eric?")], oss.MaxTokens));
        using var withoutEffort = HttpChatModel.Build(llama, new ChatRequest("system", [new ChatTurn(ChatTurn.User, "Hey, Eric?")], llama.MaxTokens));
        using var a = JsonDocument.Parse(await withEffort.Content!.ReadAsStringAsync());
        using var b = JsonDocument.Parse(await withoutEffort.Content!.ReadAsStringAsync());

        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", withEffort.RequestUri!.ToString());
        Assert.Equal("low", a.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(616, a.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(b.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(104, b.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Leere_Antwort_am_Tokenlimit_ist_eine_eigene_verstaendliche_Warnung()
    {
        var completion = HttpChatModel.ParseCompletion("openai", GroqLengthResponse);
        Assert.Equal("", completion.Text);
        Assert.True(completion.HitTokenLimit);

        using var handler = new Handler(HttpStatusCode.OK, GroqLengthResponse);
        using var http = new HttpClient(handler);
        var options = new SmalltalkOptions { Provider = "openai", Url = GroqUrl, Model = "openai/gpt-oss-20b", ApiKey = "gsk_test", Reasoning = "off" };
        var service = new SmalltalkService(Options.Create(new DuopiloOptions { Smalltalk = options }), NullLogger<SmalltalkService>.Instance, new HttpChatModel(http));

        var answer = await service.AskAsync(new SmalltalkQuestion("Hey, Eric?", "en", "Erik", SmalltalkContext.Empty), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Contains("token limit of 104", answer.Reason);
        Assert.NotNull(answer.Warning);
        Assert.Contains("104", answer.Warning);
        Assert.Contains("Reasoning", answer.Warning);
        Assert.Equal(Loc.En("smalltalk.reply.unavailable"), answer.Text);
        Assert.Contains("\"max_tokens\":104", handler.Bodies.Single());
    }

    [Fact]
    public async Task Mit_reasoning_low_kommt_nur_die_Antwort_ohne_Gedanken()
    {
        using var handler = new Handler(HttpStatusCode.OK, GroqOkResponse);
        using var http = new HttpClient(handler);
        var options = new SmalltalkOptions { Provider = "openai", Url = GroqUrl, Model = "openai/gpt-oss-20b", ApiKey = "gsk_test" };
        var service = new SmalltalkService(Options.Create(new DuopiloOptions { Smalltalk = options }), NullLogger<SmalltalkService>.Instance, new HttpChatModel(http));

        var answer = await service.AskAsync(new SmalltalkQuestion("Hey, Eric?", "en", "Erik", SmalltalkContext.Empty), CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal("Yes, captain? Everything looks good up here.", answer.Text);
        using var sent = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("low", sent.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(616, sent.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Antwort_wird_weiter_auf_MaxReplyWords_gekuerzt()
    {
        var longAnswer = string.Join(' ', Enumerable.Repeat("word", 80)) + ".";
        var cleaned = SmalltalkPrompt.Clean(longAnswer, 40);
        Assert.True(cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 41);
    }

    // ------------------------------------------------------------------ 5. Stimmen

    [Fact]
    public void First_Officer_nur_mit_guten_Stimmen_und_nie_af_nicole()
    {
        string[] female = ["af_heart", "af_bella", "bf_emma", "af_aoede", "af_kore", "af_sarah"];
        string[] male = ["am_michael", "am_fenrir", "am_puck"];
        string[] notForFirstOfficer = ["af_nicole", "am_liam", "am_eric", "am_echo", "am_onyx", "am_adam", "am_santa"];

        Assert.Equal(female, FirstOfficerCasting.FemaleVoices);
        Assert.Equal(male, FirstOfficerCasting.MaleVoices);
        Assert.Equal(["bm_george", "bm_fable"], FirstOfficerCasting.MaleReserve);
        Assert.All(FirstOfficerCasting.Voices, v => Assert.DoesNotContain(v, notForFirstOfficer));
        Assert.DoesNotContain("af_nicole", FirstOfficerCasting.DefaultVoices);
        Assert.All(FirstOfficerCasting.FemaleVoices, v => Assert.Equal(VoiceGender.Female, FirstOfficerCasting.GenderOfVoice(v)));
        Assert.All(FirstOfficerCasting.MaleVoices.Concat(FirstOfficerCasting.MaleReserve), v => Assert.Equal(VoiceGender.Male, FirstOfficerCasting.GenderOfVoice(v)));

        // Alle Kokoro-Stimmen installiert: der First Officer bekommt trotzdem nur gute
        string[] installed = [.. female, .. male, "bm_george", "bm_fable", .. notForFirstOfficer, "bf_lily", "bm_lewis", "af_jessica"];
        for (var seed = 0; seed < 300; seed++)
        {
            Assert.Contains(FirstOfficerCasting.PickVoice(VoiceGender.Female, installed, [], new Random(seed)), female);
            Assert.Contains(FirstOfficerCasting.PickVoice(VoiceGender.Male, installed, [], new Random(seed)), male);
        }
    }

    [Fact]
    public void Reserve_nur_wenn_ATC_die_guten_maennlichen_Stimmen_hat()
    {
        var voice = FirstOfficerCasting.PickVoice(VoiceGender.Male, [], ["am_michael", "am_fenrir", "am_puck"], new Random(3));
        Assert.Contains(voice, FirstOfficerCasting.MaleReserve);

        var any = FirstOfficerCasting.PickVoice(VoiceGender.Male, [], ["am_michael", "am_fenrir", "am_puck", "bm_george", "bm_fable"], new Random(3));
        Assert.Contains(any, FirstOfficerCasting.MaleVoices.Concat(FirstOfficerCasting.MaleReserve));
    }

    [Fact]
    public void ATC_ohne_af_nicole_mit_genug_eigenen_Stimmen_und_nie_der_First_Officer()
    {
        var templates = TestData.Templates();
        Assert.DoesNotContain("af_nicole", templates.Voices.Pool);

        // Mindestens acht Stimmen, die der First Officer nie bekommt
        Assert.True(templates.Voices.Pool.Except(FirstOfficerCasting.Voices).Count() >= 8);

        // Auch aus einer eigenen Kopie der Vorlagen nicht – weder im Pool noch fest für eine Station
        var own = new AtcVoiceConfig { Pool = ["af_nicole", "bm_lewis"], Stations = new(StringComparer.OrdinalIgnoreCase) { ["tower"] = "af_nicole" } };
        var assigner = new AtcVoiceAssigner(own, "flight", null, null);
        Assert.Equal("bm_lewis", assigner.VoiceFor("EDDF/tower", AtcStationType.Tower));
        Assert.Equal("bm_lewis", assigner.VoiceFor("EDDF/ground", AtcStationType.Ground));

        // Keine Station mit der Stimme des First Officers
        foreach (var fo in FirstOfficerCasting.Voices)
        {
            var stations = new AtcVoiceAssigner(templates.Voices, "flight-" + fo, null, null, () => fo);
            var keys = new[] { "EDDF/delivery", "EDDF/ground", "EDDF/tower", "EDDF/departure", "FIR/EDGG", "FIR/EDWW", "FIR/EDUU", "EDDB/approach", "EDDB/tower", "EDDB/ground" };
            Assert.All(keys, k => Assert.NotEqual(fo, stations.VoiceFor(k, AtcStationType.Center)));
        }
    }

    [Fact]
    public void Feste_Stimme_im_Dashboard_behaelt_den_Namen_bei_gleichem_Geschlecht()
    {
        var roster = new FirstOfficerRoster(null, new ManualTime(T0), new Random(5));
        var config = new FirstOfficerConfig();
        var (auto, _) = roster.Ensure("flight-1", config, null, null, [], []);
        roster.MarkIntroduced();
        var sameGender = auto!.Gender == VoiceGender.Female ? "af_kore" : "am_puck";
        var otherGender = auto.Gender == VoiceGender.Female ? "am_fenrir" : "af_aoede";

        var (fixedVoice, changed) = roster.Ensure("flight-1", config, sameGender, null, [], []);
        Assert.True(changed);
        Assert.Equal(auto.Name, fixedVoice!.Name);
        Assert.Equal(sameGender, fixedVoice.Voice);
        Assert.True(fixedVoice.FixedVoice);
        Assert.True(fixedVoice.Introduced);

        var (back, _) = roster.Ensure("flight-1", config, null, null, [], []);
        Assert.Equal(auto.Name, back!.Name);
        Assert.False(back.FixedVoice);
        Assert.Contains(back.Voice, FirstOfficerCasting.Voices);

        var (other, _) = roster.Ensure("flight-1", config, otherGender, null, [], []);
        Assert.NotEqual(auto.Gender, other!.Gender);
        Assert.Contains(other.Name, FirstOfficerNames.For(other.Gender));
        Assert.Equal(otherGender, other.Voice);
    }

    [Fact]
    public void Probehoeren_mit_dem_Namen()
    {
        Assert.Equal("Hi, I'm Erik, I'll be your first officer today. Flaps one, speed checked.", FirstOfficerCasting.PreviewText("Erik"));
        Assert.StartsWith("Hi, I'm Alex", FirstOfficerCasting.PreviewText(null));
    }

    // ------------------------------------------------------------------ 7. Nur aktiv, wenn der Sim läuft

    [Theory]
    [InlineData(false, true, false, SimActivityState.NotConnected)]
    [InlineData(false, null, true, SimActivityState.NotConnected)]
    [InlineData(true, null, false, SimActivityState.NoFlight)]
    [InlineData(true, false, false, SimActivityState.NoFlight)]
    [InlineData(true, false, true, SimActivityState.NoFlight)]
    [InlineData(true, true, true, SimActivityState.Paused)]
    [InlineData(true, true, false, SimActivityState.Running)]
    public void Zustand_des_Sims(bool connected, bool? flightRunning, bool paused, SimActivityState expected)
    {
        Assert.Equal(expected, SimGate.Read(connected, flightRunning, paused));
    }

    [Theory]
    [InlineData(SimActivityState.NotConnected)]
    [InlineData(SimActivityState.NoFlight)]
    [InlineData(SimActivityState.Paused)]
    public void Inaktiv_in_allen_drei_Zustaenden_und_zurueck(SimActivityState state)
    {
        var gate = new SimGate();
        Assert.Null(gate.Update(SimActivityState.Running, true, T0));

        var off = gate.Update(state, true, T0.AddSeconds(10));
        Assert.NotNull(off);
        Assert.True(off.Gated);
        Assert.True(gate.Gated);
        Assert.Equal(state, gate.State);

        // Grund wechselt (z. B. Pause → Hauptmenü): bleibt inaktiv, kein neuer Wechsel
        var other = state == SimActivityState.Paused ? SimActivityState.NoFlight : SimActivityState.Paused;
        Assert.Null(gate.Update(other, true, T0.AddSeconds(20)));
        Assert.True(gate.Gated);

        var on = gate.Update(SimActivityState.Running, true, T0.AddSeconds(70));
        Assert.NotNull(on);
        Assert.False(on.Gated);
        Assert.Equal(TimeSpan.FromSeconds(60), on.InactiveFor);
        Assert.False(gate.Gated);
    }

    [Fact]
    public void Pause_sperrt_nur_mit_silentWhenPaused_nicht_verbunden_und_kein_Flug_immer()
    {
        Assert.False(SimGate.Blocks(SimActivityState.Paused, silentWhenPaused: false));
        Assert.True(SimGate.Blocks(SimActivityState.Paused, silentWhenPaused: true));
        Assert.True(SimGate.Blocks(SimActivityState.NotConnected, silentWhenPaused: false));
        Assert.True(SimGate.Blocks(SimActivityState.NoFlight, silentWhenPaused: false));
        Assert.False(SimGate.Blocks(SimActivityState.Running, silentWhenPaused: true));
        Assert.Equal(["notConnected", "noFlight", "paused", "running"], new[] { SimActivityState.NotConnected, SimActivityState.NoFlight, SimActivityState.Paused, SimActivityState.Running }.Select(SimGate.Code));
    }

    [Fact]
    public void Gruende_fuers_Dashboard_in_beiden_Sprachen()
    {
        Assert.Equal("Sim not connected", Loc.En("voice.sim.notConnected"));
        Assert.Equal("No flight loaded", Loc.En("voice.sim.noFlight"));
        Assert.Equal("Paused", Loc.En("voice.sim.paused"));
        using (Loc.Use("de"))
        {
            Assert.Equal("Sim nicht verbunden", Loc.T("voice.sim.notConnected"));
            Assert.Equal("Kein Flug geladen", Loc.T("voice.sim.noFlight"));
            Assert.Equal("Pausiert", Loc.T("voice.sim.paused"));
        }
    }

    [Fact]
    public void Sim_Uhr_steht_solange_der_Sim_nicht_laeuft()
    {
        var time = new ManualTime(T0);
        var clock = new SimClock(time);

        time.Now = T0.AddSeconds(10);
        Assert.Equal(T0.AddSeconds(10), clock.Now);

        clock.Update(running: false);
        time.Now = T0.AddSeconds(70);
        Assert.Equal(T0.AddSeconds(10), clock.Now);
        Assert.False(clock.Running);
        Assert.Equal(TimeSpan.FromSeconds(60), clock.StoppedTotal);

        clock.Update(running: true);
        time.Now = T0.AddSeconds(75);
        Assert.Equal(T0.AddSeconds(15), clock.Now);

        // Nach einem Neustart von Duopilo geht die Sim-Zeit aus der Sicherung weiter
        var restarted = new SimClock(time);
        restarted.Restore(TimeSpan.FromSeconds(60));
        Assert.Equal(T0.AddSeconds(15), restarted.Now);
    }

    [Fact]
    public async Task Offener_Readback_friert_ein_statt_zu_verfallen()
    {
        var time = new ManualTime(T0);
        var gateway = new Gateway();
        var dialog = new VoiceDialog(gateway, new NoState(), time) { ExecuteWithCallout = false };

        var readback = await Say(dialog, "Gear up", 0.95, T0);
        dialog.Spoken(readback!, T0.AddSeconds(2));
        Assert.Equal(T0.AddSeconds(62), dialog.Pending!.ExpiresUtc);

        time.Now = T0.AddSeconds(10);
        dialog.Freeze();
        time.Now = T0.AddSeconds(500);
        Assert.Null(dialog.Tick());
        dialog.Expire();
        Assert.NotNull(dialog.Pending);

        // Weiter: die eingefrorenen 490 s zählen nicht – noch 52 s Zeit
        dialog.Thaw();
        Assert.Equal(T0.AddSeconds(552), dialog.Pending!.ExpiresUtc);
        time.Now = T0.AddSeconds(540);
        dialog.Tick();
        Assert.NotNull(dialog.Pending);
        time.Now = T0.AddSeconds(560);
        dialog.Tick();
        Assert.Null(dialog.Pending);
    }

    [Fact]
    public async Task Checkliste_wiederholt_nicht_waehrend_der_Sim_steht()
    {
        var time = new ManualTime(T0);
        var dialog = new VoiceDialog(new Gateway(), new NoState(), time);

        var challenge = await Say(dialog, "Before start checklist", null, T0);
        Assert.NotNull(dialog.Checklists.Active);
        dialog.Spoken(challenge!, T0);

        dialog.Freeze();
        time.Now = T0.AddMinutes(10);
        Assert.Null(dialog.Tick());
        dialog.Thaw();
        Assert.Null(dialog.Tick());

        // Die Wartezeit bis zur Wiederholung (30 s) läuft erst jetzt weiter
        time.Now = T0.AddMinutes(10).AddSeconds(29);
        Assert.Null(dialog.Tick());
        time.Now = T0.AddMinutes(10).AddSeconds(31);
        Assert.NotNull(dialog.Tick());
    }

    [Fact]
    public void Quittungen_warten_waehrend_der_Sim_steht()
    {
        var acks = new AcknowledgmentTracker(new AcknowledgeSettings());
        var alert = new Monitoring.AlertEvent(1, T0, "MASTER_WARNING", Monitoring.AlertSeverity.Warning, Monitoring.AlertKind.Raise, "Master warning", "Master warning");
        acks.Observe(alert);
        Assert.Single(acks.Pending);

        Assert.Empty(acks.Tick(T0.AddSeconds(5), paused: true));
        Assert.Empty(acks.Tick(T0.AddMinutes(5), paused: true));
        Assert.Empty(acks.Tick(T0.AddMinutes(5).AddSeconds(1), paused: false));
        Assert.Single(acks.Tick(T0.AddMinutes(5).AddSeconds(10), paused: false));
    }
}
