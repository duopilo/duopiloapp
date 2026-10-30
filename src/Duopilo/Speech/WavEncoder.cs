using System.Buffers.Binary;

namespace Duopilo.Speech;

/// <summary>Mono-Samples (−1…1) → WAV (16 Bit PCM), damit Kokoro über denselben Windows-Player läuft wie die Windows-Stimmen.</summary>
public static class WavEncoder
{
    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRate, double volume = 1.0)
    {
        var gain = (float)Math.Clamp(double.IsFinite(volume) ? volume : 1.0, 0, 1);
        var dataBytes = samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);          // Größe des fmt-Blocks
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);           // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);           // Mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2); // Bytes pro Sekunde
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);           // Block-Ausrichtung
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);          // Bits pro Sample
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        for (var i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i] * gain, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], (short)Math.Round(v * short.MaxValue));
        }

        return wav;
    }
}
