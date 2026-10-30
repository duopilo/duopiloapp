// Temporary: transcribe every WAV in a folder with Whisper base (as Duopilo) and medium, German.
using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

var dir = Path.GetFullPath(args[0]);
var work = Path.GetFullPath(args[1]);
Directory.CreateDirectory(work);
RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
var sb = new StringBuilder("| Datei | Dauer s | Whisper base | Whisper medium |\n|---|---:|---|---|\n");
var files = Directory.GetFiles(dir, "*.wav").Order().ToList();
var heard = files.ToDictionary(f => f, _ => new List<string>());
foreach (var m in new[] { "ggml-base-q5_1.bin", "ggml-medium-q5_0.bin" })
{
    var path = Path.Combine(work, m);
    if (!File.Exists(path))
    {
        using var r = await http.GetAsync("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + m, HttpCompletionOption.ResponseHeadersRead);
        r.EnsureSuccessStatusCode();
        await using var f = File.Create(path);
        await r.Content.CopyToAsync(f);
    }

    using var factory = WhisperFactory.FromPath(path);
    using var proc = factory.CreateBuilder().WithThreads(4).WithTemperature(0f).WithLanguage("de").Build();
    foreach (var file in files)
    {
        var (s, rate) = ReadWav(File.ReadAllBytes(file));
        var text = new StringBuilder();
        await foreach (var seg in proc.ProcessAsync(To16k(s, rate))) text.Append(seg.Text);
        heard[file].Add(text.ToString().Trim());
    }
}

foreach (var file in files)
{
    var (s, rate) = ReadWav(File.ReadAllBytes(file));
    sb.AppendLine($"| {Path.GetFileName(file)} | {(double)s.Length / rate:0.0} | {string.Join(" | ", heard[file])} |");
}

Console.WriteLine(sb);
File.WriteAllText(Path.Combine(dir, "whisper.md"), sb.ToString());

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
    var ratio = (double)rate / 16000; var cutoff = 0.95 * 0.5 / ratio; const int half = 32;
    var n = (int)(s.Length / ratio); var r = new float[n];
    for (var k = 0; k < n; k++)
    {
        var c = k * ratio; var i0 = (int)Math.Floor(c); double acc = 0, norm = 0;
        for (var i = i0 - half; i <= i0 + half; i++)
        {
            if (i < 0 || i >= s.Length) continue;
            var x = i - c; var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x); var w = 0.5 + 0.5 * Math.Cos(Math.PI * x / (half + 1));
            acc += s[i] * sinc * w; norm += sinc * w;
        }

        r[k] = (float)(norm == 0 ? 0 : acc / norm);
    }

    return r;
}
