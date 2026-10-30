using System.Buffers.Binary;
using Duopilo.Speech;

namespace Duopilo.Tests;

/// <summary>Kokoro als zweite Stimme (v0.7.1): Auswahl, Text für die Synthese, WAV, Stimmennamen. Ohne echtes Modell und Audio.</summary>
public sealed class SpeechEngineTests
{
    [Theory]
    [InlineData("auto", true, true, false, "windows")]
    [InlineData("auto", true, false, false, null)]
    [InlineData("auto", true, false, true, "kokoro")]
    [InlineData("auto", true, true, true, "windows")]
    [InlineData("windows", true, false, true, "windows")]
    [InlineData("windows", false, false, true, null)]
    [InlineData("kokoro", true, true, true, "kokoro")]
    [InlineData("kokoro", true, true, false, "windows")]
    [InlineData("kokoro", true, false, false, null)]
    [InlineData("kokoro", false, false, true, null)]
    [InlineData("Unsinn", true, true, false, "windows")]
    public void Welche_Stimme_spricht(string setting, bool windows, bool english, bool kokoroReady, string? expected)
    {
        Assert.Equal(expected, SpeechEngines.Effective(setting, windows, english, kokoroReady));
    }

    [Theory]
    [InlineData("auto", true, false)]
    [InlineData("auto", false, true)]
    [InlineData("kokoro", true, true)]
    [InlineData("windows", false, false)]
    public void Kokoro_wird_nur_bei_Bedarf_geladen(string setting, bool english, bool expected)
    {
        Assert.Equal(expected, SpeechEngines.NeedsKokoro(setting, english));
    }

    [Fact]
    public void Einstellungen_fuer_Kokoro_werden_bereinigt()
    {
        var s = new SpeechSettings { Engine = " KOKORO ", KokoroVoice = "../appsettings" }.Normalized();
        Assert.Equal("kokoro", s.Engine);
        Assert.Equal("bm_george", s.KokoroVoice);

        var t = new SpeechSettings { Engine = "egal", KokoroVoice = "bf_emma" }.Normalized();
        Assert.Equal("auto", t.Engine);
        Assert.Equal("bf_emma", t.KokoroVoice);
    }

    [Theory]
    [InlineData("Gear speed", "Gear speed")]
    [InlineData("Runway two three left assigned", "Runway two three left assigned")]
    [InlineData("Master warning, ENG 1 FIRE", "Master warning, engine one fire")]
    [InlineData("Master caution, HYD G RSVR LO PR", "Master caution, hydraulic green reservoir low pressure")]
    [InlineData("Master caution, A/THR OFF", "Master caution, auto thrust off")]
    [InlineData("Master warning, APU FIRE", "Master warning, A P U fire")]
    [InlineData("Speed checked, flaps two", "Speed checked, flaps two")]
    [InlineData("V1", "V1")]
    [InlineData("  ", "")]
    public void Callouts_werden_fuer_die_Sprachsynthese_aufbereitet(string callout, string expected)
    {
        Assert.Equal(expected, Pronunciation.ForSpeech(callout, SpeechContext.Empty));
    }

    [Theory]
    [InlineData("bm_george", true)]
    [InlineData("af_heart", true)]
    [InlineData("zf_001", false)]
    [InlineData("../appsettings", false)]
    [InlineData("bm_george.npy", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Nur_englische_Kokoro_Stimmen_ohne_Pfadangaben(string? name, bool expected)
    {
        Assert.Equal(expected, KokoroSpeechEngine.IsEnglishVoice(name));
    }

    [Fact]
    public void Stimmenliste_kommt_aus_dem_Ordner()
    {
        var dir = Directory.CreateTempSubdirectory("duopilo-voices-").FullName;
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
    public void WAV_mit_Kopf_und_Lautstaerke()
    {
        var wav = WavEncoder.Encode([0f, 1f, -1f, 0.5f, 2f], 24_000, volume: 0.5);

        Assert.Equal(44 + (5 * 2), wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(24_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));

        short Sample(int i) => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 2)));
        Assert.Equal(0, Sample(0));
        Assert.Equal(16384, Sample(1));
        Assert.Equal(-16384, Sample(2));
        Assert.Equal(8192, Sample(3));
        Assert.Equal(short.MaxValue, Sample(4)); // 2 × 0,5 = 1 → begrenzt
    }

    [Fact]
    public void Feste_Callouts_der_Regeln_werden_vorab_berechnet()
    {
        Assert.Contains("V1", CalloutText.Common);
        Assert.Contains("Ten thousand", CalloutText.Common);
        Assert.Contains("Master warning", CalloutText.Common);
        Assert.Equal(CalloutText.Common.Count, CalloutText.Common.Distinct().Count());
    }
}
