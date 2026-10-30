// Temporary check: Whisper round trip for a German sample (kikiri Kokoro) against Piper voices, same sentence.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

const string Sentence = "Zwei weiße Zwerge zwängen sich zwischen zwei Zweige.";
var work = Path.GetFullPath(args.Length > 0 ? args[0] : "work");
Directory.CreateDirectory(work);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
var sb = new StringBuilder();

var piperZip = await Get("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip", "piper.zip");
var piperDir = Path.Combine(work, "piper");
if (!Directory.Exists(piperDir)) ZipFile.ExtractToDirectory(piperZip, piperDir);
var piperExe = Directory.GetFiles(piperDir, "piper.exe", SearchOption.AllDirectories).First();

var clips = new List<(string Name, float[] Samples, double Seconds)>();
foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "audio"), "*.wav").Order())
{
    var (s, r) = ReadWav(File.ReadAllBytes(file));
    clips.Add((Path.GetFileNameWithoutExtension(file), To16k(s, r), (double)s.Length / r));
}

foreach (var v in new[] { "de_DE-thorsten-medium", "de_DE-ramona-low", "de_DE-kerstin-low", "de_DE-eva_k-x_low" })
{
    var parts = v.Split('-');
    var baseUrl = $"https://huggingface.co/rhasspy/piper-voices/resolve/main/de/{parts[0]}/{parts[1]}/{parts[2]}/{v}";
    await Get(baseUrl + ".onnx.json", v + ".onnx.json");
    await Get(baseUrl + ".onnx", v + ".onnx");
    var outFile = Path.Combine(work, v + ".wav");
    var psi = new ProcessStartInfo(piperExe) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(piperExe)!, StandardInputEncoding = new UTF8Encoding(false) };
    foreach (var a in new[] { "--model", Path.Combine(work, v + ".onnx"), "--config", Path.Combine(work, v + ".onnx.json"), "--output_file", outFile }) psi.ArgumentList.Add(a);
    using (var p = Process.Start(psi)!)
    {
        var err = p.StandardError.ReadToEndAsync();
        p.StandardInput.WriteLine(Sentence);
        p.StandardInput.Close();
        p.WaitForExit();
        await err;
    }

    var (s, r) = ReadWav(File.ReadAllBytes(outFile));
    clips.Add(("piper " + v, To16k(s, r), (double)s.Length / r));
}

RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
sb.AppendLine($"Satz: \"{Sentence}\"");
sb.AppendLine();
sb.AppendLine("| Stimme | Dauer s | Whisper base-q5_1 (Duopilo) | Whisper small-q5_1 | Whisper medium-q5_0 |");
sb.AppendLine("|---|---:|---|---|---|");
var models = new[] { "ggml-base-q5_1.bin", "ggml-small-q5_1.bin", "ggml-medium-q5_0.bin" };
var heard = new Dictionary<string, List<string>>();
foreach (var m in models)
{
    var path = await Get("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + m, m);
    using var factory = WhisperFactory.FromPath(path);
    using var proc = factory.CreateBuilder().WithThreads(4).WithTemperature(0f).WithLanguage("de").Build();
    foreach (var (name, samples, _) in clips)
    {
        var text = new StringBuilder();
        var padded = samples.Length < 16000 * 2 ? samples.Concat(new float[16000 * 2 - samples.Length]).ToArray() : samples;
        await foreach (var seg in proc.ProcessAsync(padded)) text.Append(seg.Text);
        if (!heard.TryGetValue(name, out var list)) heard[name] = list = [];
        list.Add(text.ToString().Trim());
    }
}

foreach (var (name, _, seconds) in clips)
{
    sb.AppendLine($"| {name} | {seconds:0.00} | {string.Join(" | ", heard[name])} |");
}

Console.WriteLine(sb);
File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "check.md"), sb.ToString());

async Task<string> Get(string url, string file)
{
    var path = Path.Combine(work, file);
    if (File.Exists(path)) return path;
    using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
    {
        response.EnsureSuccessStatusCode();
        await using var f = File.Create(path + ".part");
        await response.Content.CopyToAsync(f);
    }

    File.Move(path + ".part", path, true);
    return path;
}

static (float[] Samples, int Rate) ReadWav(byte[] wav)
{
    var pos = 12; var rate = 22050; var ch = 1;
    while (pos + 8 <= wav.Length)
    {
        var id = Encoding.ASCII.GetString(wav, pos, 4); var size = BitConverter.ToInt32(wav, pos + 4); pos += 8;
        if (id == "fmt ") { ch = BitConverter.ToInt16(wav, pos + 2); rate = BitConverter.ToInt32(wav, pos + 4); }
        else if (id == "data")
        {
            var len = size <= 0 || pos + size > wav.Length ? wav.Length - pos : size; var n = len / (2 * ch); var s = new float[n];
            for (var k = 0; k < n; k++) s[k] = BitConverter.ToInt16(wav, pos + k * 2 * ch) / 32768f;
            return (s, rate);
        }

        pos += size + (size & 1);
    }

    throw new InvalidDataException("bad wav");
}

static float[] To16k(float[] s, int rate)
{
    if (rate == 16000) return s;
    var n = (int)((long)s.Length * 16000 / rate); var r = new float[n];
    for (var k = 0; k < n; k++) { var p = (double)k * rate / 16000; var i0 = (int)p; var i1 = Math.Min(i0 + 1, s.Length - 1); var f = p - i0; r[k] = (float)(s[i0] * (1 - f) + s[i1] * f); }
    return r;
}
