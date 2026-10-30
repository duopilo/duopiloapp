// Temporary benchmark for duopilo PR fix/voice-after-beta (not part of the PR).
// 1) Whisper.net: cost of .WithProbabilities() with Duopilo's settings (base-q5_1, 4 threads, single segment, prompt).
// 2) Piper: CPU cost per sentence (one process per sentence, as Duopilo runs it) and intelligibility (Whisper round trip, WER)
//    for the German voices.
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var work = Path.GetFullPath(args.Length > 0 ? args[0] : "work");
Directory.CreateDirectory(work);
var summary = new StringBuilder();
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("duopilo-bench");

const string HfWhisper = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
const string HfPiper = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";
const string DefaultPrompt =
    "Kopilot, Duopilo. Gear up, gear down, flaps one, flaps full, speedbrake retract, arm spoilers, autobrake medium, landing lights on, " +
    "seat belts on, APU start, QNH 1013, standard, altitude 5000, flight level 120, speed 250, heading 090, squawk 7000, TCAS TA/RA, " +
    "speed check, say again. Frankfurt Tower, Langen Radar, Shanwick, Lufthansa. " +
    "Fahrwerk einfahren, Klappen eins, Anschnallzeichen an.";

Console.WriteLine($"Processors: {Environment.ProcessorCount}");
summary.AppendLine($"Runner: {Environment.ProcessorCount} logical processors, {RuntimeInformationText()}");
summary.AppendLine();

// ---------------------------------------------------------------- downloads
var piperZip = await Get("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip", "piper.zip");
var piperDir = Path.Combine(work, "piper");
if (!Directory.Exists(piperDir))
{
    ZipFile.ExtractToDirectory(piperZip, piperDir);
}

var piperExe = Directory.GetFiles(piperDir, "piper.exe", SearchOption.AllDirectories).First();
string[] germanVoices = ["de_DE-thorsten-medium", "de_DE-thorsten-high", "de_DE-karlsson-low", "de_DE-kerstin-low", "de_DE-ramona-low", "de_DE-eva_k-x_low"];
string[] englishVoices = ["en_US-lessac-medium", "en_GB-alan-medium"];
foreach (var v in germanVoices.Concat(englishVoices))
{
    await Get(VoiceUrl(v, ".onnx.json"), v + ".onnx.json");
    await Get(VoiceUrl(v, ".onnx"), v + ".onnx");
}

var baseModel = await Get(HfWhisper + "ggml-base-q5_1.bin", "ggml-base-q5_1.bin");
var smallModel = await Get(HfWhisper + "ggml-small-q5_1.bin", "ggml-small-q5_1.bin");
var jfk = await Get("https://github.com/ggml-org/whisper.cpp/raw/master/samples/jfk.wav", "jfk.wav");

// ---------------------------------------------------------------- 1) Whisper: WithProbabilities
string[] commands =
[
    "Kopilot, flaps one.", "Gear up.", "Lufthansa four alpha bravo, climb flight level one two zero.", "Speed two five zero.",
    "Hey Erik, how are you today?", "Düsseldorf Tower, Mayday, Mayday, Mayday.", "Set QNH one zero one three.", "Before start checklist.",
];
var clips = new List<(string Name, float[] Samples)>();
var i = 0;
foreach (var text in commands)
{
    var voice = englishVoices[i++ % englishVoices.Length];
    var (wav, _, _) = RunPiper(voice, text);
    clips.Add(($"{voice}: {text}", ToMono16k(ReadWav(wav))));
}

clips.Add(("jfk.wav (11 s)", ToMono16k(ReadWav(File.ReadAllBytes(jfk)))));

RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
using (var factory = WhisperFactory.FromPath(baseModel))
{
    WhisperProcessor Build(bool probabilities)
    {
        var b = factory.CreateBuilder().WithThreads(4).WithNoContext().WithSingleSegment().WithTemperature(0f).WithPrompt(DefaultPrompt).WithLanguage("en");
        if (probabilities)
        {
            b = b.WithProbabilities();
        }

        return b.Build();
    }

    using var plain = Build(false);
    using var prob = Build(true);
    const int reps = 10;
    var timesA = new List<double>();
    var timesB = new List<double>();
    var perClip = new List<string>();
    foreach (var (name, samples) in clips)
    {
        var padded = samples.Length < 16000 * 12 / 10 ? samples.Concat(new float[(16000 * 12 / 10) - samples.Length]).ToArray() : samples;
        await Transcribe(plain, padded);
        await Transcribe(prob, padded);
        var a = new List<double>();
        var b = new List<double>();
        (string Text, double? Conf, double MinP) last = default;
        (string Text, double? Conf, double MinP) lastPlain = default;
        for (var r = 0; r < reps; r++)
        {
            // abwechselnd zuerst, damit Cache-/Takteffekte sich ausgleichen
            if (r % 2 == 0)
            {
                var sw = Stopwatch.StartNew(); lastPlain = await Transcribe(plain, padded); a.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); last = await Transcribe(prob, padded); b.Add(sw.Elapsed.TotalMilliseconds);
            }
            else
            {
                var sw = Stopwatch.StartNew(); last = await Transcribe(prob, padded); b.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); lastPlain = await Transcribe(plain, padded); a.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        timesA.AddRange(a);
        timesB.AddRange(b);
        perClip.Add($"| {name} | {Median(a):0} | {Median(b):0} | {Median(b) - Median(a):+0;-0;0} | {lastPlain.Conf?.ToString("0.00") ?? "–"} | {last.Conf?.ToString("0.00") ?? "–"} | {last.Text} |");
    }

    summary.AppendLine("## Whisper.net 1.9.1 – WithProbabilities (base-q5_1, CPU, 4 threads, single segment, Duopilo prompt, en)");
    summary.AppendLine();
    summary.AppendLine($"{reps} runs per clip and variant, interleaved, after one warm-up run each.");
    summary.AppendLine();
    summary.AppendLine("| Clip | median ms without | median ms with | Δ ms | confidence without | confidence with | text |");
    summary.AppendLine("|---|---:|---:|---:|---:|---:|---|");
    perClip.ForEach(l => summary.AppendLine(l));
    summary.AppendLine();
    summary.AppendLine($"All clips: median without {Median(timesA):0.0} ms, with {Median(timesB):0.0} ms; mean without {timesA.Average():0.0} ms, with {timesB.Average():0.0} ms → Δ mean {timesB.Average() - timesA.Average():+0.0;-0.0} ms ({(timesB.Average() / timesA.Average() - 1) * 100:+0.0;-0.0} %).");
    summary.AppendLine();
}

// ---------------------------------------------------------------- 2) Piper: CPU and intelligibility
string[] german =
[
    "Guten Morgen, Kapitän. Wir haben heute ruhiges Wetter auf der ganzen Strecke.",
    "Der Wind in Frankfurt kommt aus Westen mit etwa zwölf Knoten.",
    "Ich schätze, wir landen ungefähr zehn Minuten vor dem Plan.",
    "Die Kabine ist bereit, und alle Passagiere sind angeschnallt.",
    "Über den Alpen könnte es später etwas unruhig werden.",
    "Unser Treibstoff reicht bequem bis zum Ausweichflughafen.",
    "Das war eine schöne Landung, vielen Dank für den angenehmen Flug.",
    "Möchtest du nach dem Start einen Kaffee aus der Bordküche?",
];

summary.AppendLine("## Piper (piper.exe 2023.11.14-2, one process per sentence as in Duopilo)");
summary.AppendLine();
summary.AppendLine("8 German sentences × 2 runs. CPU = user+kernel time of piper.exe incl. model load. Cores = CPU time / wall time. WER = Whisper small-q5_1 (de) round trip, lower is better.");
summary.AppendLine();
summary.AppendLine("| Voice | Model MB | Sample rate | wall ms/sentence | CPU ms/sentence | CPU s per audio s | avg cores while running | WER |");
summary.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
using (var factory = WhisperFactory.FromPath(smallModel))
using (var asr = factory.CreateBuilder().WithThreads(4).WithTemperature(0f).WithLanguage("de").Build())
{
    var wers = new StringBuilder();
    foreach (var voice in germanVoices)
    {
        double wall = 0, cpu = 0, audio = 0;
        var runs = 0;
        var errors = 0;
        var refWords = 0;
        var rate = 0;
        foreach (var sentence in german)
        {
            byte[] wav = [];
            for (var r = 0; r < 2; r++)
            {
                var (w, ms, cpuMs) = RunPiper(voice, sentence);
                wav = w;
                wall += ms;
                cpu += cpuMs;
                var clip = ReadWav(w);
                rate = clip.Rate;
                audio += (double)clip.Samples.Length / clip.Rate;
                runs++;
            }

            var heard = (await Transcribe(asr, Pad(ToMono16k(ReadWav(wav))))).Text;
            var reference = WordsOf(sentence);
            var hyp = WordsOf(heard);
            errors += Levenshtein(reference, hyp);
            refWords += reference.Length;
            wers.AppendLine($"| {voice} | {sentence} | {heard} |");
        }

        var mb = new FileInfo(Path.Combine(work, voice + ".onnx")).Length / 1024.0 / 1024.0;
        summary.AppendLine($"| {voice} | {mb:0} | {rate} | {wall / runs:0} | {cpu / runs:0} | {cpu / 1000 / audio:0.00} | {cpu / wall:0.0} | {100.0 * errors / refWords:0.0} % |");
    }

    summary.AppendLine();
    summary.AppendLine("<details><summary>Round-trip transcripts</summary>");
    summary.AppendLine();
    summary.AppendLine("| Voice | Text | Whisper heard |");
    summary.AppendLine("|---|---|---|");
    summary.Append(wers);
    summary.AppendLine();
    summary.AppendLine("</details>");
}

Console.WriteLine(summary.ToString());
File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "results.md"), summary.ToString());
var stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
if (!string.IsNullOrEmpty(stepSummary))
{
    File.AppendAllText(stepSummary, summary.ToString());
}

return;

// ---------------------------------------------------------------- helpers
async Task<string> Get(string url, string file)
{
    var path = Path.Combine(work, file);
    if (File.Exists(path))
    {
        return path;
    }

    Console.WriteLine($"download {url}");
    using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
    {
        response.EnsureSuccessStatusCode();
        await using var f = File.Create(path + ".part");
        await response.Content.CopyToAsync(f);
    }

    File.Move(path + ".part", path, true);
    return path;
}

(byte[] Wav, double WallMs, double CpuMs) RunPiper(string voice, string text)
{
    var output = Path.Combine(work, $"out-{Guid.NewGuid():N}.wav");
    var psi = new ProcessStartInfo(piperExe)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(piperExe)!,
        StandardInputEncoding = new UTF8Encoding(false),
    };
    psi.ArgumentList.Add("--model");
    psi.ArgumentList.Add(Path.Combine(work, voice + ".onnx"));
    psi.ArgumentList.Add("--config");
    psi.ArgumentList.Add(Path.Combine(work, voice + ".onnx.json"));
    psi.ArgumentList.Add("--output_file");
    psi.ArgumentList.Add(output);
    psi.ArgumentList.Add("--length_scale");
    psi.ArgumentList.Add("1.00");
    var sw = Stopwatch.StartNew();
    using var p = Process.Start(psi)!;
    var so = p.StandardOutput.ReadToEndAsync();
    var se = p.StandardError.ReadToEndAsync();
    p.StandardInput.WriteLine(text);
    p.StandardInput.Close();
    p.WaitForExit();
    var wall = sw.Elapsed.TotalMilliseconds;
    var cpuMs = p.TotalProcessorTime.TotalMilliseconds;
    so.Wait();
    var err = se.Result;
    if (p.ExitCode != 0 || !File.Exists(output))
    {
        throw new InvalidOperationException($"piper {voice} failed ({p.ExitCode}): {err}");
    }

    var bytes = File.ReadAllBytes(output);
    File.Delete(output);
    return (bytes, wall, cpuMs);
}

async Task<(string Text, double? Conf, double MinP)> Transcribe(WhisperProcessor processor, float[] samples)
{
    var sb = new StringBuilder();
    double weighted = 0;
    var weight = 0;
    double minP = 1;
    await foreach (var s in processor.ProcessAsync(samples))
    {
        sb.Append(s.Text);
        var len = Math.Max(1, s.Text.Trim().Length);
        weighted += s.Probability * len;
        weight += len;
        minP = Math.Min(minP, s.MinProbability);
    }

    return (sb.ToString().Trim(), weight > 0 ? weighted / weight : null, minP);
}

static float[] Pad(float[] s) => s.Length < 16000 * 12 / 10 ? s.Concat(new float[(16000 * 12 / 10) - s.Length]).ToArray() : s;

static string VoiceUrl(string id, string ext)
{
    var parts = id.Split('-');
    var lang = parts[0].Split('_')[0];
    return $"{HfPiper}{lang}/{parts[0]}/{parts[1]}/{parts[2]}/{id}{ext}";
}

static (float[] Samples, int Rate) ReadWav(byte[] wav)
{
    var pos = 12;
    var rate = 22050;
    var channels = 1;
    while (pos + 8 <= wav.Length)
    {
        var id = Encoding.ASCII.GetString(wav, pos, 4);
        var size = BitConverter.ToInt32(wav, pos + 4);
        pos += 8;
        if (id == "fmt ")
        {
            channels = BitConverter.ToInt16(wav, pos + 2);
            rate = BitConverter.ToInt32(wav, pos + 4);
        }
        else if (id == "data")
        {
            var length = size <= 0 || pos + size > wav.Length ? wav.Length - pos : size;
            var frames = length / (2 * channels);
            var samples = new float[frames];
            for (var k = 0; k < frames; k++)
            {
                samples[k] = BitConverter.ToInt16(wav, pos + (k * 2 * channels)) / 32768f;
            }

            return (samples, rate);
        }

        pos += size + (size & 1);
    }

    throw new InvalidDataException("bad wav");
}

static float[] ToMono16k((float[] Samples, int Rate) clip)
{
    if (clip.Rate == 16000)
    {
        return clip.Samples;
    }

    var n = (int)((long)clip.Samples.Length * 16000 / clip.Rate);
    var result = new float[n];
    for (var k = 0; k < n; k++)
    {
        var pos = (double)k * clip.Rate / 16000;
        var i0 = (int)pos;
        var i1 = Math.Min(i0 + 1, clip.Samples.Length - 1);
        var f = pos - i0;
        result[k] = (float)((clip.Samples[i0] * (1 - f)) + (clip.Samples[i1] * f));
    }

    return result;
}

static string[] WordsOf(string text) =>
    Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N} ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

static int Levenshtein(string[] a, string[] b)
{
    var d = new int[a.Length + 1, b.Length + 1];
    for (var x = 0; x <= a.Length; x++) d[x, 0] = x;
    for (var y = 0; y <= b.Length; y++) d[0, y] = y;
    for (var x = 1; x <= a.Length; x++)
    {
        for (var y = 1; y <= b.Length; y++)
        {
            d[x, y] = Math.Min(Math.Min(d[x - 1, y] + 1, d[x, y - 1] + 1), d[x - 1, y - 1] + (a[x - 1] == b[y - 1] ? 0 : 1));
        }
    }

    return d[a.Length, b.Length];
}

static double Median(List<double> v)
{
    var s = v.OrderBy(x => x).ToList();
    return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[(s.Count / 2) - 1] + s[s.Count / 2]) / 2;
}

static string RuntimeInformationText() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
