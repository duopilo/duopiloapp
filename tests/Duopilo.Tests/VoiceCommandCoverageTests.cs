using Duopilo.Aircraft;
using Duopilo.Input;
using Duopilo.Monitoring;
using Duopilo.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Duopilo.Tests;

/// <summary>
/// Sprachschleife ohne LLM kann alles, was Claude über die Kopilot-Tools kann (v0.9.2, Punkt 1): Jede Aktion jedes Profils hat
/// einen Sprachbefehl – englisch und deutsch, jeder Schalter mit jeder Auswahl. Die Werte passen zum Profil.
/// </summary>
public sealed class VoiceCommandCoverageTests
{
    private static VoiceIntent Intent(string raw) => VoiceIntents.Recognize(VoiceText.Normalize(raw), ["kopilot"]);

    private static ProfileStore RepoProfiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Duopilo", "profiles")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var options = Options.Create(new DuopiloOptions { ProfilesDirectory = Path.Combine(dir!.FullName, "src", "Duopilo", "profiles") });
        var store = new ProfileStore(options, NullLogger<ProfileStore>.Instance);
        Assert.Empty(store.Errors);
        return store;
    }

    [Fact]
    public void Jede_Aktion_jedes_Profils_hat_einen_Sprachbefehl_fuer_jede_Auswahl()
    {
        var missing = new List<string>();
        foreach (var profile in RepoProfiles().All)
        {
            foreach (var action in profile.Actions)
            {
                var examples = VoiceCommandCatalog.For(action.Id).ToList();
                if (examples.Count == 0)
                {
                    missing.Add($"{profile.Name}: {action.Id}");
                    continue;
                }

                if (action.Parameter is { IsChoice: true, Options: { } options })
                {
                    foreach (var option in options.Keys.Where(o => !examples.Any(e => string.Equals(e.Value, o, StringComparison.OrdinalIgnoreCase))))
                    {
                        missing.Add($"{profile.Name}: {action.Id} = {option}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "Ohne Sprachbefehl: " + string.Join(", ", missing));
    }

    [Fact]
    public void Alle_Beispiele_werden_englisch_und_deutsch_erkannt()
    {
        var problems = new List<string>();
        foreach (var example in VoiceCommandCatalog.Examples)
        {
            foreach (var phrase in new[] { example.English, example.German })
            {
                var intent = Intent(phrase);
                if (intent.Kind != IntentKind.Action || intent.ActionId != example.ActionId || !string.Equals(intent.Value, example.Value, StringComparison.Ordinal))
                {
                    problems.Add($"\"{phrase}\" → {intent} (erwartet {example.ActionId} = {example.Value ?? "–"})");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Der erkannte Wert passt zum Profil (Auswahl vorhanden, Zahl im Bereich und Raster) – Bodenaktionen am Boden, sonst bei 150 kt.</summary>
    [Fact]
    public void Erkannte_Werte_sind_in_jedem_Profil_planbar()
    {
        var problems = new List<string>();
        var air = new FlightSnapshot { Connected = true, FlightRunning = true, DataAgeSec = 0.1, OnGround = false, IasKt = 150, AltitudeFt = 3000 };
        var ground = air with { OnGround = true, IasKt = 0 };
        foreach (var profile in RepoProfiles().All)
        {
            foreach (var action in profile.Actions)
            {
                var snapshot = action.Interlocks.Contains(Interlocks.OnGround, StringComparer.OrdinalIgnoreCase) ? ground : air;
                // Auswahl, die es nur bei einem anderen Flugzeug gibt (z. B. Seatbelts "auto" beim A339), gehört nicht zu diesem Profil
                var examples = VoiceCommandCatalog.For(action.Id).Where(e =>
                    action.Parameter is not { IsChoice: true, Options: { } options }
                    || options.Keys.Any(o => string.Equals(o, e.Value, StringComparison.OrdinalIgnoreCase)));
                foreach (var example in examples)
                {
                    var result = ActionPlanner.Plan(action, example.Value, profile.Limits, snapshot);
                    if (result.Plan is null)
                    {
                        problems.Add($"{profile.Name}: {action.Id} = {example.Value}: {result.Refusal}");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Die Aktionen, die im A339-Profil bisher keinen Sprachbefehl hatten.</summary>
    [Theory]
    [InlineData("External power on", "elec.extPwr", "on")]
    [InlineData("Bodenstrom aus", "elec.extPwr", "off")]
    [InlineData("Batteries on", "elec.battery", "on")]
    [InlineData("Batterien aus", "elec.battery", "off")]
    [InlineData("Nav lights on", "lights.nav", "on")]
    [InlineData("Nav and logo lights off", "lights.nav", "off")]
    [InlineData("Logo lights on", "lights.logo", "on")]
    [InlineData("No smoking auto", "signs.noSmoking", "auto")]
    [InlineData("Rauchverbot an", "signs.noSmoking", "on")]
    [InlineData("Probe heat on", "antiice.probeHeat", "on")]
    [InlineData("Sondenheizung auto", "antiice.probeHeat", "auto")]
    [InlineData("Engine one anti ice on", "antiice.eng1", "on")]
    [InlineData("Triebwerk zwei Enteisung aus", "antiice.eng2", "off")]
    [InlineData("ND mode ARC", "efis.ndMode", "arc")]
    [InlineData("ND Modus Plan", "efis.ndMode", "plan")]
    [InlineData("ND range one hundred sixty", "efis.ndRange", "160")]
    [InlineData("Range forty", "efis.ndRange", "40")]
    [InlineData("Altitude increment one thousand", "fcu.altIncrement", "1000")]
    [InlineData("Expedite climb", "fcu.exped", "on")]
    [InlineData("TCAS traffic all", "tcas.traffic", "all")]
    [InlineData("Traffic above", "tcas.traffic", "abv")]
    [InlineData("Altitude reporting off", "xpdr.altReporting", "off")]
    [InlineData("Höhenübermittlung an", "xpdr.altReporting", "on")]
    [InlineData("COM one standby one two one decimal five", "com1.standby", "121.5")]
    [InlineData("Swap COM one", "com1.swap", null)]
    public void Fehlende_A339_Aktionen_haben_jetzt_Sprachbefehle(string raw, string action, string? value)
    {
        var intent = Intent(raw);

        Assert.Equal(IntentKind.Action, intent.Kind);
        Assert.Equal(action, intent.ActionId);
        Assert.Equal(value, intent.Value);
    }

    [Theory]
    [InlineData("Nav and logo lights on")]
    [InlineData("Battery 1 off")]
    [InlineData("Probe heat")]
    public void Keine_falschen_Treffer(string raw)
    {
        var intent = Intent(raw);

        // "Nav and logo" ist nur der Nav-Schalter (beim A339 ein Schalter), Einzelbatterien gibt es nicht
        Assert.NotEqual("lights.logo", intent.ActionId);
        Assert.NotEqual("elec.battery", intent.ActionId);
        Assert.NotEqual("antiice.probeHeat", intent.ActionId);
    }

    // ------------------------------------------------------------------ Sperren und Rückfragen gelten unverändert

    private sealed class Backend : IInputBackend
    {
        public List<string> Sent { get; } = [];

        public Dictionary<string, double> State { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FlightSnapshot Current { get; set; } = new()
        {
            Connected = true, FlightRunning = true, DataAgeSec = 0.1, OnGround = false, IasKt = 250, AltitudeFt = 30000, PressureAltitudeFt = 30000,
        };

        public bool Connected => true;

        public AircraftProfile? Profile { get; init; }

        public FlightSnapshot Snapshot() => Current;

        public Task<double?> ReadAsync(string name, string unit, CancellationToken ct) =>
            Task.FromResult(State.TryGetValue(name, out var v) ? v : (double?)null);

        public Task SendEventAsync(string name, uint data, CancellationToken ct)
        {
            Sent.Add($"{name}:{data}");
            return Task.CompletedTask;
        }

        public Task SetAsync(string name, string unit, double value, CancellationToken ct)
        {
            Sent.Add($"{name}={value}");
            State[name] = value;
            return Task.CompletedTask;
        }
    }

    private static (InputService Input, Backend Backend) A339()
    {
        var profile = RepoProfiles().All.Single(p => p.Name.Contains("A330", StringComparison.OrdinalIgnoreCase));
        var backend = new Backend { Profile = profile };
        var dir = Path.Combine(Path.GetTempPath(), "duopilo-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new DuopiloOptions { DataDirectory = dir, Input = new InputOptions { Enabled = true } });
        return (new InputService(options, backend, NullLogger<InputService>.Instance), backend);
    }

    [Fact]
    public async Task Batterien_aus_im_Flug_bleiben_gesperrt_auch_per_Sprache()
    {
        var (input, backend) = A339();
        backend.State["SIM ON GROUND"] = 0;
        var intent = Intent("Batteries off");

        var result = await input.ExecuteNowAsync(intent.ActionId, intent.Value, default);

        Assert.Equal(InputResult.Refused, result.Status);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task Hoehenuebermittlung_aus_im_Flug_braucht_die_Rueckfrage()
    {
        var (input, backend) = A339();
        var intent = Intent("Altitude reporting off");

        var result = await input.ExecuteNowAsync(intent.ActionId, intent.Value, default);

        // Nicht sofort ausgeführt: Readback, dann Rückfrage mit Warnhinweis
        Assert.Equal(InputResult.AwaitingConfirmation, result.Status);
        Assert.True(result.CautionFollows);
        Assert.Empty(backend.Sent);
        Assert.NotNull(input.Pending);
    }

    [Fact]
    public async Task Bodenstrom_nur_am_Boden()
    {
        var (input, backend) = A339();
        var intent = Intent("External power on");

        Assert.Equal(InputResult.Refused, (await input.ExecuteNowAsync(intent.ActionId, intent.Value, default)).Status);
        Assert.Empty(backend.Sent);
    }
}
