// Temporary check: Supertonic 3 voices in German (listening samples, Whisper round trip, CPU time).
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Supertonic;
using Whisper.net;
using Whisper.net.LibraryLoader;

const string Twister = "Zwei weiße Zwerge zwängen sich zwischen zwei Zweige.";
const string Intro = "Guten Abend, ich bin heute dein First Officer. Das Wetter in Frankfurt sieht gut aus, wir sollten pünktlich landen.";
const string Callout = "Flaps one, speed checked. Positive rate, gear up.";
const int Steps = 8;
const float Speed = 1.05f;

var assets = Path.GetFullPath(args[0]);
var work = Path.GetFullPath(args[1]);
var outDir = Path.GetFullPath(args[2]);
Directory.CreateDirectory(work);
Directory.CreateDirectory(outDir);

var tts = Helper.LoadTextToSpeech(Path.Combine(assets, "onnx"));
var rate = tts.SampleRate;
var voices = Directory.GetFiles(Path.Combine(assets, "voice_styles"), "*.json")
    .OrderBy(p => Path.GetFileNameWithoutExtension(p)[0] == 'M' ? 0 : 1).ThenBy(p => p).ToList();

// Warm-up (first call loads/optimizes graphs)
var warm = Helper.LoadVoiceStyle([voices[0]]);
tts.Call("Hallo.", "de", warm, Steps, Speed);

var timing = new List<(string Voice, string Text, double Audio, double Wall, double Cpu)>();
var forWhisper = new List<(string Voice, string Ref, float[] Samples)>();
foreach (var path in voices)
{
    var name = Path.GetFileNameWithoutExtension(path);
    var style = Helper.LoadVoiceStyle([path]);
    var parts = new List<float[]>();
    foreach (var (text, lang) in new[] { (Twister, "de"), (Intro, "de"), (Callout, "en") })
    {
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        var (wav, duration) = tts.Call(text, lang, style, Steps, Speed);
        sw.Stop();
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds;
        var len = Math.Min((int)(rate * duration.Sum()), wav.Length);
        var clip = wav[..len];
        parts.Add(clip);
        timing.Add((name, text, (double)len / rate, sw.Elapsed.TotalSeconds, cpu));
        if (lang == "de") forWhisper.Add((name, text, To16k(clip, rate)));
    }

    var gap = new float[(int)(rate * 0.7)];
    var all = parts[0].Concat(gap).Concat(parts[1]).Concat(gap).Concat(parts[2]).ToArray();
    Helper.WriteWavFile(Path.Combine(outDir, $"Supertonic3_{name}.wav"), all, rate);
}

RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
var heard = new Dictionary<(string, string, string), string>();
var models = new[] { "ggml-base-q5_1.bin", "ggml-medium-q5_0.bin" };
foreach (var m in models)
{
    var file = Path.Combine(work, m);
    if (!File.Exists(file))
    {
        using var response = await http.GetAsync("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + m, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var f = File.Create(file);
        await response.Content.CopyToAsync(f);
    }

    using var factory = WhisperFactory.FromPath(file);
    using var proc = factory.CreateBuilder().WithThreads(4).WithTemperature(0f).WithLanguage("de").Build();
    foreach (var (voice, reference, samples) in forWhisper)
    {
        var text = new StringBuilder();
        var padded = samples.Length < 32000 ? samples.Concat(new float[32000 - samples.Length]).ToArray() : samples;
        await foreach (var seg in proc.ProcessAsync(padded)) text.Append(seg.Text);
        heard[(m, voice, reference)] = text.ToString().Trim();
    }
}

var sb = new StringBuilder();
sb.AppendLine($"Supertonic 3, total-step {Steps}, speed {Speed}, CPU {Environment.ProcessorCount} threads, {rate} Hz");
sb.AppendLine();
sb.AppendLine("## Whisper (Wortfehler / Wörter)");
sb.AppendLine();
sb.AppendLine("| Stimme | Zungenbrecher base | Zungenbrecher medium | Intro base | Intro medium |");
sb.AppendLine("|---|---|---|---|---|");
foreach (var voice in forWhisper.Select(w => w.Voice).Distinct())
{
    var cells = new List<string>();
    foreach (var reference in new[] { Twister, Intro })
        foreach (var m in models)
        {
            var h = heard[(m, voice, reference)];
            cells.Add($"{Errors(reference, h)}/{Words(reference).Length}: {h}");
        }

    sb.AppendLine($"| {voice} | {string.Join(" | ", cells)} |");
}

sb.AppendLine();
sb.AppendLine("## Zeit (Synthese pro Satz)");
sb.AppendLine();
sb.AppendLine("| Stimme | Satz | Audio s | Wand s | CPU s | CPU-s/Audio-s |");
sb.AppendLine("|---|---|---:|---:|---:|---:|");
foreach (var t in timing)
    sb.AppendLine($"| {t.Voice} | {t.Text[..Math.Min(24, t.Text.Length)]}… | {t.Audio:0.00} | {t.Wall:0.00} | {t.Cpu:0.00} | {t.Cpu / t.Audio:0.00} |");
var words = timing.Sum(t => t.Audio);
sb.AppendLine();
sb.AppendLine($"Gesamt: Audio {words:0.0} s, Wand {timing.Sum(t => t.Wall):0.0} s, CPU {timing.Sum(t => t.Cpu):0.0} s, Ø Wand/Satz {timing.Average(t => t.Wall):0.00} s");

Console.WriteLine(sb);
File.WriteAllText(Path.Combine(outDir, "supertonic.md"), sb.ToString());

static string[] Words(string s)
{
    s = s.ToLowerInvariant().Replace("ß", "ss");
    s = Regex.Replace(s, @"\b2\b", "zwei");
    return Regex.Split(Regex.Replace(s, @"[^\p{L}\p{N} ]", " "), @"\s+").Where(w => w.Length > 0).ToArray();
}

static int Errors(string reference, string hypothesis)
{
    var r = Words(reference); var h = Words(hypothesis);
    var d = new int[r.Length + 1, h.Length + 1];
    for (var i = 0; i <= r.Length; i++) d[i, 0] = i;
    for (var j = 0; j <= h.Length; j++) d[0, j] = j;
    for (var i = 1; i <= r.Length; i++)
        for (var j = 1; j <= h.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
    return d[r.Length, h.Length];
}

// Windowed-sinc resampler (low-pass at ~7.6 kHz), good enough for Whisper input.
static float[] To16k(float[] s, int rate)
{
    if (rate == 16000) return s;
    var ratio = (double)rate / 16000;
    var cutoff = 0.95 * 0.5 / ratio; // cycles per input sample
    const int half = 32;
    var n = (int)(s.Length / ratio);
    var r = new float[n];
    for (var k = 0; k < n; k++)
    {
        var center = k * ratio;
        var i0 = (int)Math.Floor(center);
        double acc = 0, norm = 0;
        for (var i = i0 - half; i <= i0 + half; i++)
        {
            if (i < 0 || i >= s.Length) continue;
            var x = i - center;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var w = 0.5 + 0.5 * Math.Cos(Math.PI * x / (half + 1));
            acc += s[i] * sinc * w; norm += sinc * w;
        }

        r[k] = (float)(norm == 0 ? 0 : acc / norm);
    }

    return r;
}
