using System.Net;
using System.Text.Json;
using Kopilot.Hosting;
using Kopilot.Voice;

namespace Kopilot.Tests;

/// <summary>
/// Testlog 0.9: Sprachschleife stumm (voice-settings.json im Datenordner stand auf "enabled": false), Mikrofonauswahl,
/// Status in kopilot_status und das Absturz-Journal. Ohne Mikrofon und MSFS.
/// </summary>
public sealed class VoiceSetupTests
{
    private static string RepoTemplate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kopilot.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Kopilot", VoiceSettingsStore.FileName));
    }

    // ------------------------------------------------------------------ Werte in der Datei ändern, Kommentare behalten

    [Fact]
    public void Einschalten_und_Mikrofon_in_der_Vorlage_Kommentare_bleiben()
    {
        var template = RepoTemplate();

        var text = JsonTextPatcher.Set(template, ["enabled"], "true");
        text = JsonTextPatcher.Set(text, ["microphone", "device"], JsonTextPatcher.Quote("Headset (Quest 3)"));
        var s = VoiceSettingsStore.Parse(text);

        Assert.True(s.Enabled);
        Assert.Equal("Headset (Quest 3)", s.Microphone.Device);

        // Alles andere unverändert: Kommentare, andere Werte, gleichnamige Schalter tiefer in der Datei
        Assert.Contains("// VAD-Empfindlichkeit", text);
        Assert.Equal(0.5, s.Microphone.Threshold);
        Assert.True(s.Callouts.Enabled);
        Assert.True(s.Callouts.TaxiSpeed.Enabled);
        Assert.Equal(template.Length + "true".Length - "false".Length + "\"Headset (Quest 3)\"".Length - "\"\"".Length, text.Length);
    }

    [Fact]
    public void Ausschalten_laesst_callouts_enabled_in_Ruhe()
    {
        var text = JsonTextPatcher.Set(RepoTemplate(), ["enabled"], "true");
        text = JsonTextPatcher.Set(text, ["enabled"], "false");
        var s = VoiceSettingsStore.Parse(text);

        Assert.False(s.Enabled);
        Assert.True(s.Callouts.Enabled);
    }

    [Fact]
    public void Fehlende_Eigenschaft_und_fehlendes_Objekt_werden_angelegt()
    {
        var missingDevice = JsonTextPatcher.Set("""{ "microphone": { "threshold": 0.4 } }""", ["microphone", "device"], "\"Index\"");
        var a = VoiceSettingsStore.Parse(missingDevice);
        Assert.Equal("Index", a.Microphone.Device);
        Assert.Equal(0.4, a.Microphone.Threshold);

        var missingObject = JsonTextPatcher.Set(
            """
            {
              // nur ein Kommentar
              "enabled": false
            }
            """,
            ["microphone", "device"],
            "\"Index\"");
        var b = VoiceSettingsStore.Parse(missingObject);
        Assert.Equal("Index", b.Microphone.Device);
        Assert.False(b.Enabled);
        Assert.Contains("// nur ein Kommentar", missingObject);

        var empty = VoiceSettingsStore.Parse(JsonTextPatcher.Set("{}", ["enabled"], "true"));
        Assert.True(empty.Enabled);
    }

    [Fact]
    public void Sonderzeichen_im_Geraetenamen()
    {
        const string name = "Mikrofon \"Rode\" \\ Ümläut";
        var text = JsonTextPatcher.Set("""{ "microphone": { "device": "alt" } }""", ["microphone", "device"], JsonTextPatcher.Quote(name));

        Assert.Equal(name, VoiceSettingsStore.Parse(text).Microphone.Device);
    }

    [Fact]
    public void Werte_in_Arrays_werden_nicht_verwechselt()
    {
        var text = JsonTextPatcher.Set(
            """{ "list": [ { "enabled": false } ], "enabled": false }""",
            ["enabled"],
            "true");

        Assert.Equal("""{ "list": [ { "enabled": false } ], "enabled": true }""", text);
    }

    [Fact]
    public void Update_schreibt_in_den_Datenordner_und_meldet_die_Vorlage()
    {
        var root = Directory.CreateTempSubdirectory("kopilot-voice-").FullName;
        try
        {
            var exe = Path.Combine(root, "exe");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(exe);
            File.WriteAllText(Path.Combine(exe, VoiceSettingsStore.FileName), RepoTemplate());
            var store = new VoiceSettingsStore(data, exe);

            // Wie im Testflug: wirksame Datei ist eine Kopie der Vorlage, also aus
            Assert.False(store.Load().Settings.Enabled);
            Assert.Null(store.TemplateMismatch(store.Load().Settings));

            // Jemand schaltet die Vorlage neben der exe ein – Kopilot weist darauf hin
            File.WriteAllText(Path.Combine(exe, VoiceSettingsStore.FileName), JsonTextPatcher.Set(RepoTemplate(), ["enabled"], "true"));
            Assert.Contains("template", store.TemplateMismatch(store.Load().Settings));

            // Dashboard: einschalten und Mikrofon wählen → im Datenordner, Kommentare bleiben
            var saved = store.Update(true, "Quest");
            Assert.True(saved.Enabled);
            Assert.Equal("Quest", saved.Microphone.Device);
            var (reloaded, error) = store.Load();
            Assert.Null(error);
            Assert.True(reloaded.Enabled);
            Assert.Equal("Quest", reloaded.Microphone.Device);
            Assert.Contains("// VAD-Empfindlichkeit", File.ReadAllText(store.FilePath));
            Assert.Null(store.TemplateMismatch(reloaded));

            // Nur das Mikrofon zurück aufs Standardgerät, Schalter bleibt
            var standard = store.Update(null, "");
            Assert.True(standard.Enabled);
            Assert.Equal("", standard.Microphone.Device);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------ Mikrofon

    [Theory]
    [InlineData("", -1, true)]
    [InlineData("oculus", 1, true)]
    [InlineData("Headset (Oculus Virtual Audio Device)", 1, true)] // voller Windows-Name, WinMM kürzt auf 31 Zeichen
    [InlineData("Index", -1, false)]
    public void Mikrofon_nach_Teil_des_Namens(string device, int number, bool found)
    {
        string[] devices = ["Mikrofon (Realtek(R) Audio)", "Headset (Oculus Virtual Audio D"];

        var resolved = MicrophoneCapture.Resolve(device, devices);

        Assert.Equal(number, resolved.Number);
        Assert.Equal(found, resolved.Found);
    }

    [Fact]
    public void Pegel_und_Zaehler()
    {
        Assert.Equal(-120, MicrophoneCapture.PeakDbfs(new float[512]));
        Assert.Equal(-6.0, MicrophoneCapture.PeakDbfs([0.1f, -0.5f, 0.2f]), 1);

        var diag = new VoiceDiagnostics();
        var t0 = new DateTimeOffset(2026, 10, 30, 6, 0, 0, TimeSpan.Zero);
        Assert.True(diag.Audio(new float[512], t0));
        Assert.False(diag.Audio(new float[512], t0.AddMilliseconds(64)));
        Assert.Equal(-120, diag.PeakSinceOpen);

        diag.Audio([0.5f], t0.AddSeconds(1));
        diag.Utterance(1.5);
        diag.Recognized();
        diag.Dropped("Sim pausiert");
        var status = diag.Apply(new VoiceStatus(), t0.AddSeconds(2));

        Assert.Equal(-6.0, status.LevelDbfs!.Value, 1);
        Assert.Equal(1, status.Utterances);
        Assert.Equal(1, status.Recognized);
        Assert.Equal(1, status.Dropped);
        Assert.Equal("Sim pausiert", status.LastDropReason);

        // 10 s ohne Audio: kein Pegel mehr
        Assert.Null(diag.Level(t0.AddSeconds(12)));

        var window = diag.TakeWindow();
        Assert.Equal(3, window.Blocks);
        Assert.Equal(1, window.Utterances);
        Assert.Equal(0, diag.TakeWindow().Blocks);
    }

    // ------------------------------------------------------------------ Status

    [Fact]
    public void Ohne_Sprachschleife_meldet_der_Status_unavailable()
    {
        var status = new NoVoiceLoop().Status;

        Assert.False(status.Available);
        Assert.Equal("unavailable", status.State);
        Assert.Contains("tray instance", status.Note);
    }

    [Fact]
    public async Task Status_und_Voice_Endpunkt_zeigen_die_Sprachschleife()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"kopilot-test-{Guid.NewGuid():N}.json");
        await using var app = await TestApp.StartAsync(stateFile);
        var client = app.Local();

        using var status = JsonDocument.Parse(await client.GetStringAsync("/status"));
        var voice = status.RootElement.GetProperty("voiceLoop");
        Assert.False(voice.GetProperty("available").GetBoolean());
        Assert.Equal("unavailable", voice.GetProperty("state").GetString());

        using var direct = JsonDocument.Parse(await client.GetStringAsync("/voice"));
        Assert.Equal("unavailable", direct.RootElement.GetProperty("state").GetString());

        using var api = JsonDocument.Parse(await client.GetStringAsync("/api/voice"));
        Assert.False(api.RootElement.GetProperty("available").GetBoolean());

        using var post = await client.PostAsync("/api/voice", new StringContent("""{"enabled":true}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);

        using var remote = await app.Remote().GetAsync("/api/voice");
        Assert.Equal(HttpStatusCode.NotFound, remote.StatusCode);

        if (File.Exists(stateFile))
        {
            File.Delete(stateFile);
        }
    }

    // ------------------------------------------------------------------ Absturz-Journal

    [Fact]
    public void Journal_erkennt_einen_nicht_sauber_beendeten_Lauf()
    {
        var root = Directory.CreateTempSubdirectory("kopilot-journal-").FullName;
        var path = Path.Combine(root, "journal-test.bin");
        try
        {
            var (first, previous) = CrashJournal.Open(path, makeCurrent: false);
            Assert.Null(previous);
            first.Add("Traffic-Abfrage 7: Aircraft, 200000 m");
            first.Add("BYTYPE 7: 0/0, Objekt 0, dwSize 48");
            first.SetState("SimConnect.ReceiveMessage");

            // Absturz: kein MarkClean
            first.Dispose();

            var (second, crashed) = CrashJournal.Open(path, makeCurrent: false);
            Assert.NotNull(crashed);
            Assert.Equal(Environment.ProcessId, crashed!.Pid);
            Assert.Equal("SimConnect.ReceiveMessage", crashed.State);
            Assert.Equal(new[] { "Traffic-Abfrage 7: Aircraft, 200000 m", "BYTYPE 7: 0/0, Objekt 0, dwSize 48" }, crashed.Entries.Select(e => e.Text));
            Assert.NotNull(crashed.LastWriteUtc);

            // Sauber beendet: beim nächsten Start nichts zu melden
            second.Add("Start");
            second.MarkClean();
            second.Dispose();
            var (third, clean) = CrashJournal.Open(path, makeCurrent: false);
            Assert.Null(clean);
            third.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Journal_ist_ein_Ring_und_kuerzt_lange_Eintraege()
    {
        var root = Directory.CreateTempSubdirectory("kopilot-journal-").FullName;
        var path = Path.Combine(root, "journal-test.bin");
        try
        {
            var (journal, _) = CrashJournal.Open(path, makeCurrent: false);
            for (var i = 0; i < 300; i++)
            {
                journal.Add($"Eintrag {i}");
            }

            journal.Add(new string('ä', 500));
            var run = journal.Snapshot();
            journal.Dispose();

            Assert.Equal(256, run.Entries.Count);
            Assert.Equal("Eintrag 45", run.Entries[0].Text);
            Assert.True(run.Entries[^1].Text.Length is > 50 and < 100);
            Assert.All(run.Entries[^1].Text, c => Assert.Equal('ä', c));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
