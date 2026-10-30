namespace Kopilot.Speech;

/// <summary>
/// Warnton für die Bestätigungspflicht: drei kurze Töne (wie ein Attention Getter), als WAV für denselben Windows-Player,
/// über den auch Kokoro spielt. Rein rechnend – damit testbar.
/// </summary>
public static class ToneGenerator
{
    public const int SampleRate = 22050;

    /// <summary>Tonhöhe (Hz).</summary>
    public const double FrequencyHz = 1000;

    /// <summary>Länge eines Tons und der Pause dazwischen (s).</summary>
    public const double BeepSec = 0.15;

    public const double GapSec = 0.08;

    public const int Beeps = 3;

    /// <summary>Samples (−1…1): drei Töne mit weichem Ein- und Ausblenden (kein Knacken).</summary>
    public static float[] Attention()
    {
        var beep = (int)(SampleRate * BeepSec);
        var gap = (int)(SampleRate * GapSec);
        var fade = SampleRate / 200;
        var samples = new float[(Beeps * beep) + ((Beeps - 1) * gap)];
        for (var b = 0; b < Beeps; b++)
        {
            var start = b * (beep + gap);
            for (var i = 0; i < beep; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, beep - 1 - i) / (double)fade);
                samples[start + i] = (float)(0.6 * envelope * Math.Sin(2 * Math.PI * FrequencyHz * i / SampleRate));
            }
        }

        return samples;
    }

    /// <summary>Warnton als WAV (16 Bit PCM, mono).</summary>
    public static byte[] AttentionWav(double volume = 1.0) => WavEncoder.Encode(Attention(), SampleRate, volume);
}
