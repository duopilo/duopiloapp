using Duopilo.Localization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Duopilo.Voice;

/// <summary>Sprachwahrscheinlichkeit je Block von 512 Samples (16 kHz).</summary>
public interface IVoiceActivityDetector : IDisposable
{
    float Process(ReadOnlySpan<float> frame);

    void Reset();
}

/// <summary>
/// Silero VAD (MIT) über ONNX Runtime auf der CPU, ein Thread – rechnet pro 32-ms-Block weit unter einer Millisekunde.
/// Eingang wie im Original: 64 Samples Kontext + 512 neue, Zustand [2, 1, 128], Abtastrate als int64.
/// </summary>
public sealed class SileroVad : IVoiceActivityDetector
{
    /// <summary>Feste Version (v6.2), ~2,3 MB.</summary>
    public const string ModelUrl = "https://github.com/snakers4/silero-vad/raw/v6.2/src/silero_vad/data/silero_vad.onnx";

    public const string ModelFileName = "silero_vad.onnx";

    public const long MinModelBytes = 1_000_000;

    private const int ContextSamples = 64;
    private const int FrameSamples = SegmenterOptions.FrameSamples;

    private readonly object _lock = new();
    private readonly InferenceSession _session;
    private readonly OnnxSessionOptions _sessionOptions;
    private readonly float[] _input = new float[ContextSamples + FrameSamples];
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly long[] _sampleRate = [SegmenterOptions.SampleRate];

    public SileroVad(string modelPath)
    {
        _sessionOptions = new OnnxSessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            EnableCpuMemArena = true,
        };
        _session = new InferenceSession(modelPath, _sessionOptions);
    }

    public float Process(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples)
        {
            throw new ArgumentException(Loc.T("voice.vad.frameSize", FrameSamples, frame.Length), nameof(frame));
        }

        lock (_lock)
        {
            // Kontext (letzte 64 Samples des vorigen Blocks) + neuer Block
            Array.Copy(_input, FrameSamples, _input, 0, ContextSamples);
            frame.CopyTo(_input.AsSpan(ContextSamples));

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, [1, ContextSamples + FrameSamples])),
                NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, [2, 1, 128])),
                NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(_sampleRate, [1])),
            };

            using var results = _session.Run(inputs);
            var output = results.FirstOrDefault(r => r.Name == "output") ?? results[0];
            var stateN = results.FirstOrDefault(r => r.Name == "stateN") ?? results[1];

            var i = 0;
            foreach (var v in stateN.AsTensor<float>())
            {
                if (i >= _state.Length)
                {
                    break;
                }

                _state[i++] = v;
            }

            return output.AsTensor<float>().FirstOrDefault();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_state);
            Array.Clear(_input);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _sessionOptions.Dispose();
    }
}
