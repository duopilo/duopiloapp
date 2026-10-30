namespace Kopilot.Voice;

/// <summary>Einstellungen für das Zerlegen in Sätze (aus <see cref="MicrophoneSettings"/>).</summary>
public sealed record SegmenterOptions
{
    public const int SampleRate = 16_000;

    /// <summary>Silero VAD arbeitet mit 512 Samples (32 ms) bei 16 kHz.</summary>
    public const int FrameSamples = 512;

    public double Threshold { get; init; } = 0.5;

    public int MinSpeechMs { get; init; } = 200;

    public int SilenceMs { get; init; } = 700;

    public int PreRollMs { get; init; } = 300;

    public double MaxUtteranceSec { get; init; } = 12;

    /// <summary>Am Satzende so viel Stille behalten (Whisper mag kein hartes Abschneiden).</summary>
    public int TailMs { get; init; } = 200;

    public static SegmenterOptions From(MicrophoneSettings m) => new()
    {
        Threshold = m.Threshold,
        MinSpeechMs = m.MinSpeechMs,
        SilenceMs = m.SilenceMs,
        PreRollMs = m.PreRollMs,
        MaxUtteranceSec = m.MaxUtteranceSec,
    };
}

/// <summary>
/// Zerlegt den Mikrofonstrom anhand der Sprachwahrscheinlichkeit (Silero VAD) in Sätze:
/// Start ab der Schwelle (mit etwas Vorlauf), Ende nach <see cref="SegmenterOptions.SilenceMs"/> Stille unter der
/// unteren Schwelle (Schwelle − 0,15). Zu kurze Geräusche werden verworfen, zu lange Sätze abgeschnitten.
/// Rein rechnend – damit testbar.
/// </summary>
public sealed class UtteranceSegmenter
{
    private const double FrameMs = SegmenterOptions.FrameSamples * 1000.0 / SegmenterOptions.SampleRate;

    private readonly SegmenterOptions _options;
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float[]> _frames = [];
    private readonly int _preRollFrames;
    private readonly int _maxFrames;
    private bool _triggered;
    private double _speechMs;
    private double _silenceMs;
    private int _trailingSilentFrames;

    public UtteranceSegmenter(SegmenterOptions? options = null)
    {
        _options = options ?? new SegmenterOptions();
        _preRollFrames = (int)Math.Round(_options.PreRollMs / FrameMs);
        _maxFrames = (int)Math.Ceiling(_options.MaxUtteranceSec * 1000 / FrameMs);
    }

    /// <summary>Gerade ein Satz im Gang, der schon nach Sprache klingt (nicht nur ein kurzes Geräusch).</summary>
    public bool InSpeech => _triggered && _speechMs >= _options.MinSpeechMs;

    private double EndThreshold => Math.Max(0.05, _options.Threshold - 0.15);

    /// <summary>Nächster Block (512 Samples) mit seiner Sprachwahrscheinlichkeit. Rückgabe: fertiger Satz oder null.</summary>
    public float[]? Push(ReadOnlySpan<float> frame, float probability)
    {
        var copy = frame.ToArray();
        if (!_triggered)
        {
            if (probability >= _options.Threshold)
            {
                _triggered = true;
                _frames.AddRange(_preRoll);
                _preRoll.Clear();
                _frames.Add(copy);
                _speechMs = FrameMs;
                _silenceMs = 0;
                _trailingSilentFrames = 0;
                return null;
            }

            _preRoll.Enqueue(copy);
            while (_preRoll.Count > _preRollFrames)
            {
                _preRoll.Dequeue();
            }

            return null;
        }

        _frames.Add(copy);
        if (probability >= _options.Threshold)
        {
            _speechMs += FrameMs;
            _silenceMs = 0;
            _trailingSilentFrames = 0;
        }
        else if (probability < EndThreshold)
        {
            _silenceMs += FrameMs;
            _trailingSilentFrames++;
        }
        else
        {
            // Zwischen den Schwellen: weder Sprache noch Stille – Satz läuft weiter
            _trailingSilentFrames = 0;
        }

        if (_silenceMs >= _options.SilenceMs)
        {
            return Finish();
        }

        if (_frames.Count >= _maxFrames)
        {
            return Finish();
        }

        return null;
    }

    /// <summary>Laufenden Satz verwerfen (z. B. wenn Kopilot zu sprechen beginnt).</summary>
    public void Reset()
    {
        _triggered = false;
        _frames.Clear();
        _preRoll.Clear();
        _speechMs = 0;
        _silenceMs = 0;
        _trailingSilentFrames = 0;
    }

    private float[]? Finish()
    {
        var enough = _speechMs >= _options.MinSpeechMs;

        // Stille am Ende bis auf einen kurzen Rest abschneiden
        var keepTail = (int)Math.Round(_options.TailMs / FrameMs);
        var drop = Math.Max(0, _trailingSilentFrames - keepTail);
        var count = Math.Max(0, _frames.Count - drop);
        float[]? result = null;
        if (enough && count > 0)
        {
            result = new float[_frames.Take(count).Sum(f => f.Length)];
            var offset = 0;
            for (var i = 0; i < count; i++)
            {
                _frames[i].CopyTo(result, offset);
                offset += _frames[i].Length;
            }
        }

        Reset();
        return result;
    }
}

/// <summary>Halbduplex: Mikrofon zu, solange Kopilot spricht, und noch kurz danach (Nachhall).</summary>
public static class HalfDuplex
{
    public static bool MicOpen(bool speaking, DateTimeOffset lastSpeechEndUtc, DateTimeOffset now, TimeSpan tail) =>
        !speaking && now - lastSpeechEndUtc >= tail;
}
