// Inferenz nach dem C#-Beispiel von Supertonic (csharp/Helper.cs, https://github.com/supertone-oss-archive/supertonic):
// Textaufbereitung, Stil-Dateien und der Ablauf Dauer → Text-Encoder → Entrauschen → Vocoder.
// Copyright (c) 2025 Supertone Inc. – MIT-Lizenz (licenses\Supertonic-MIT.txt). Für Duopilo umgebaut: eine Stimme pro Aufruf,
// flache Arrays, Abbruch zwischen den Schritten. Die Modellgewichte (OpenRAIL-M) gehören nicht dazu – sie lädt Duopilo bei Bedarf.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Duopilo.Localization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Duopilo.Speech;

/// <summary>Stil einer Supertonic-Stimme (voice_styles/&lt;Name&gt;.json): zwei Tensoren für Klang und Sprechdauer.</summary>
public sealed record SupertonicStyle(float[] Ttl, int[] TtlShape, float[] Dp, int[] DpShape)
{
    public static SupertonicStyle Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var (ttl, ttlShape) = Tensor(doc.RootElement.GetProperty("style_ttl"));
        var (dp, dpShape) = Tensor(doc.RootElement.GetProperty("style_dp"));
        if (ttlShape.Length != 3 || dpShape.Length != 3 || ttlShape[0] != 1 || dpShape[0] != 1)
        {
            throw new InvalidDataException(Loc.T("speech.supertonic.badStyle"));
        }

        return new SupertonicStyle(ttl, ttlShape, dp, dpShape);
    }

    private static (float[] Data, int[] Shape) Tensor(JsonElement element)
    {
        var shape = element.GetProperty("dims").EnumerateArray().Select(d => d.GetInt32()).ToArray();
        var size = shape.Aggregate(1, (a, b) => a * b);
        var data = new float[size];
        var i = 0;
        Flatten(element.GetProperty("data"), data, ref i);
        if (i != size)
        {
            throw new InvalidDataException(Loc.T("speech.supertonic.badStyle"));
        }

        return (data, shape);
    }

    private static void Flatten(JsonElement element, float[] data, ref int i)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                Flatten(child, data, ref i);
            }

            return;
        }

        if (i >= data.Length)
        {
            throw new InvalidDataException(Loc.T("speech.supertonic.badStyle"));
        }

        data[i++] = element.GetSingle();
    }
}

/// <summary>Textaufbereitung wie im Supertonic-Beispiel: Supertonic liest Zeichen (keine Phoneme) mit Sprach-Tag.</summary>
public static partial class SupertonicText
{
    /// <summary>Sprachen des Modells (Supertonic 3, 31 Sprachen).</summary>
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.Ordinal)
    {
        "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et", "fi", "fr", "hi", "hr", "hu", "id", "it", "lt", "lv", "nl", "pl",
        "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk", "vi", "na",
    };

    private static readonly (string From, string To)[] Replacements =
    [
        ("–", "-"), ("‑", "-"), ("—", "-"), ("_", " "), ("“", "\""), ("”", "\""), ("‘", "'"), ("’", "'"),
        ("´", "'"), ("`", "'"), ("[", " "), ("]", " "), ("|", " "), ("/", " "), ("#", " "), ("→", " "), ("←", " "),
        ("@", " at "), ("e.g.,", "for example, "), ("i.e.,", "that is, "),
    ];

    /// <summary>
    /// Satz für das Modell: Unicode-Kompatibilitätszerlegung (NFKD – „ü“ wird zu „u“ + Trema, so wurde das Modell trainiert), ohne
    /// Emojis und Sonderzeichen, Satzzeichen am Ende, eingerahmt von &lt;de&gt;…&lt;/de&gt;.
    /// </summary>
    public static string Prepare(string text, string language)
    {
        if (!Languages.Contains(language))
        {
            throw new ArgumentException(Loc.T("speech.supertonic.language", language), nameof(language));
        }

        text = RemoveEmojis(text.Normalize(NormalizationForm.FormKD));
        foreach (var (from, to) in Replacements)
        {
            text = text.Replace(from, to, StringComparison.Ordinal);
        }

        text = SpecialSymbols().Replace(text, "");
        text = SpaceBeforePunctuation().Replace(text, "$1");
        foreach (var q in new[] { "\"", "'", "`" })
        {
            while (text.Contains(q + q, StringComparison.Ordinal))
            {
                text = text.Replace(q + q, q, StringComparison.Ordinal);
            }
        }

        text = Whitespace().Replace(text, " ").Trim();
        if (!EndsWithPunctuation().IsMatch(text))
        {
            text += ".";
        }

        return $"<{language}>{text}</{language}>";
    }

    /// <summary>Zeichen → Kennungen des Modells (unicode_indexer.json: Index = UTF-16-Zeichen; unbekannte Zeichen = 0).</summary>
    public static long[] Ids(string prepared, long[] indexer)
    {
        var ids = new long[prepared.Length];
        for (var i = 0; i < prepared.Length; i++)
        {
            var c = prepared[i];
            ids[i] = c < indexer.Length ? indexer[c] : 0;
        }

        return ids;
    }

    /// <summary>Lange Antworten in Stücke bis <paramref name="maxLength"/> Zeichen, an Satzgrenzen (das Modell rechnet pro Stück).</summary>
    public static IReadOnlyList<string> Chunks(string text, int maxLength = 300)
    {
        var chunks = new List<string>();
        foreach (var paragraph in Paragraphs().Split(text.Trim()).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var current = "";
            foreach (var sentence in SentenceEnd().Split(paragraph).Where(s => s.Length > 0))
            {
                if (current.Length + sentence.Length + 1 <= maxLength)
                {
                    current = current.Length == 0 ? sentence : current + " " + sentence;
                }
                else
                {
                    if (current.Length > 0)
                    {
                        chunks.Add(current.Trim());
                    }

                    current = sentence;
                }
            }

            if (current.Length > 0)
            {
                chunks.Add(current.Trim());
            }
        }

        return chunks.Count == 0 && text.Trim().Length > 0 ? [text.Trim()] : chunks;
    }

    private static string RemoveEmojis(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            int codePoint = text[i];
            var pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (pair)
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
            }

            var emoji = codePoint is (>= 0x1F300 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or (>= 0x1F1E6 and <= 0x1F1FF);
            if (!emoji)
            {
                sb.Append(text[i]);
                if (pair)
                {
                    sb.Append(text[i + 1]);
                }
            }

            if (pair)
            {
                i++;
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"[♥☆♡©\\]")]
    private static partial Regex SpecialSymbols();

    [GeneratedRegex(@" ([,.!?;:'])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("[.!?;:,'\"“”‘’)\\]}…。」』】〉》›»]$")]
    private static partial Regex EndsWithPunctuation();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex Paragraphs();

    [GeneratedRegex(@"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|Ph\.D\.|etc\.|e\.g\.|i\.e\.|vs\.|Inc\.|Ltd\.|Co\.|Corp\.|St\.|Ave\.|Blvd\.|bzw\.|ca\.|usw\.|z\.B\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+")]
    private static partial Regex SentenceEnd();
}

/// <summary>
/// Supertonic 3 (ONNX Runtime, CPU): Dauer schätzen → Text kodieren → Rauschen in <see cref="Steps"/> Schritten entrauschen →
/// Vocoder (44,1 kHz). Nicht threadsicher – der Aufrufer rechnet immer nur einen Satz gleichzeitig.
/// </summary>
public sealed class SupertonicModel : IDisposable
{
    /// <summary>Entrauschungs-Schritte (wie im Beispiel; weniger = schneller, aber unsauberer).</summary>
    public const int Steps = 8;

    /// <summary>Pause zwischen zwei Stücken einer langen Antwort (s).</summary>
    private const float ChunkPause = 0.3f;

    private readonly OnnxSessionOptions _options;
    private readonly InferenceSession _duration;
    private readonly InferenceSession _textEncoder;
    private readonly InferenceSession _vectorEstimator;
    private readonly InferenceSession _vocoder;
    private readonly long[] _indexer;
    private readonly int _baseChunkSize;
    private readonly int _chunkCompressFactor;
    private readonly int _latentDim;

    /// <param name="onnxDirectory">Ordner mit duration_predictor/text_encoder/vector_estimator/vocoder.onnx, tts.json, unicode_indexer.json.</param>
    public SupertonicModel(string onnxDirectory, int threads)
    {
        using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(onnxDirectory, "tts.json"))))
        {
            var root = config.RootElement;
            SampleRate = root.GetProperty("ae").GetProperty("sample_rate").GetInt32();
            _baseChunkSize = root.GetProperty("ae").GetProperty("base_chunk_size").GetInt32();
            _chunkCompressFactor = root.GetProperty("ttl").GetProperty("chunk_compress_factor").GetInt32();
            _latentDim = root.GetProperty("ttl").GetProperty("latent_dim").GetInt32();
        }

        _indexer = JsonSerializer.Deserialize<long[]>(File.ReadAllText(Path.Combine(onnxDirectory, "unicode_indexer.json")))
            ?? throw new InvalidDataException(Loc.T("speech.supertonic.badModel", "unicode_indexer.json"));
        _options = new OnnxSessionOptions
        {
            IntraOpNumThreads = Math.Clamp(threads, 1, 16),
            InterOpNumThreads = 1,
            EnableMemoryPattern = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _duration = new InferenceSession(Path.Combine(onnxDirectory, "duration_predictor.onnx"), _options);
        _textEncoder = new InferenceSession(Path.Combine(onnxDirectory, "text_encoder.onnx"), _options);
        _vectorEstimator = new InferenceSession(Path.Combine(onnxDirectory, "vector_estimator.onnx"), _options);
        _vocoder = new InferenceSession(Path.Combine(onnxDirectory, "vocoder.onnx"), _options);
    }

    public int SampleRate { get; }

    /// <summary>Text (beliebig lang) in einer Sprache sprechen. <paramref name="speed"/> 1 = normal, größer = schneller.</summary>
    public float[] Synthesize(string text, string language, SupertonicStyle style, float speed, CancellationToken ct)
    {
        var output = new List<float>();
        foreach (var chunk in SupertonicText.Chunks(text))
        {
            ct.ThrowIfCancellationRequested();
            if (output.Count > 0)
            {
                output.AddRange(new float[(int)(ChunkPause * SampleRate)]);
            }

            output.AddRange(Infer(chunk, language, style, speed, ct));
        }

        return [.. output];
    }

    private float[] Infer(string text, string language, SupertonicStyle style, float speed, CancellationToken ct)
    {
        var ids = SupertonicText.Ids(SupertonicText.Prepare(text, language), _indexer);
        var length = ids.Length;
        var textIds = new DenseTensor<long>(ids, [1, length]);
        var textMask = new DenseTensor<float>(Ones(length), [1, 1, length]);
        var styleTtl = new DenseTensor<float>(style.Ttl, style.TtlShape);
        var styleDp = new DenseTensor<float>(style.Dp, style.DpShape);

        // 1. Dauer (s), mit dem Tempo verrechnet
        float seconds;
        using (var result = _duration.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("text_ids", textIds),
            NamedOnnxValue.CreateFromTensor("style_dp", styleDp),
            NamedOnnxValue.CreateFromTensor("text_mask", textMask),
        }))
        {
            seconds = result.First(o => o.Name == "duration").AsTensor<float>().GetValue(0) / speed;
        }

        if (!float.IsFinite(seconds) || seconds <= 0)
        {
            return [];
        }

        // 2. Text kodieren
        DenseTensor<float> textEmb;
        using (var result = _textEncoder.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("text_ids", textIds),
            NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
            NamedOnnxValue.CreateFromTensor("text_mask", textMask),
        }))
        {
            textEmb = result.First(o => o.Name == "text_emb").AsTensor<float>().ToDenseTensor();
        }

        // 3. Rauschen der passenden Länge entrauschen
        var samples = (long)(seconds * SampleRate);
        var chunkSize = (long)_baseChunkSize * _chunkCompressFactor;
        var latentLength = (int)((samples + chunkSize - 1) / chunkSize);
        var latentChannels = _latentDim * _chunkCompressFactor;
        var latent = new float[latentChannels * latentLength];
        for (var i = 0; i < latent.Length; i++)
        {
            latent[i] = Gaussian();
        }

        var latentShape = new[] { 1, latentChannels, latentLength };
        var latentMask = new DenseTensor<float>(Ones(latentLength), [1, 1, latentLength]);
        var totalStep = new DenseTensor<float>(new float[] { Steps }, [1]);
        for (var step = 0; step < Steps; step++)
        {
            ct.ThrowIfCancellationRequested();
            using var result = _vectorEstimator.Run(new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("noisy_latent", new DenseTensor<float>(latent, latentShape)),
                NamedOnnxValue.CreateFromTensor("text_emb", textEmb),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
                NamedOnnxValue.CreateFromTensor("text_mask", textMask),
                NamedOnnxValue.CreateFromTensor("latent_mask", latentMask),
                NamedOnnxValue.CreateFromTensor("total_step", totalStep),
                NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(new float[] { step }, [1])),
            });
            var denoised = result.First(o => o.Name == "denoised_latent").AsTensor<float>();
            var i = 0;
            foreach (var v in denoised)
            {
                if (i >= latent.Length)
                {
                    break;
                }

                latent[i++] = v;
            }
        }

        // 4. Vocoder → Audio, auf die geschätzte Dauer gekürzt
        ct.ThrowIfCancellationRequested();
        using (var result = _vocoder.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("latent", new DenseTensor<float>(latent, latentShape)) }))
        {
            var wav = result.First(o => o.Name == "wav_tts").AsTensor<float>().ToArray();
            return wav.Length > samples ? wav[..(int)samples] : wav;
        }
    }

    /// <summary>Normalverteiltes Rauschen (Box-Muller).</summary>
    private static float Gaussian()
    {
        var u1 = 1.0 - Random.Shared.NextDouble();
        var u2 = 1.0 - Random.Shared.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    private static float[] Ones(int length)
    {
        var ones = new float[length];
        Array.Fill(ones, 1f);
        return ones;
    }

    public void Dispose()
    {
        _duration.Dispose();
        _textEncoder.Dispose();
        _vectorEstimator.Dispose();
        _vocoder.Dispose();
        _options.Dispose();
    }
}
