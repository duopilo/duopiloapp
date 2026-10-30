using System.Security.Cryptography;
using Kopilot.Localization;

namespace Kopilot.Voice;

/// <summary>
/// Die heruntergeladene Datei hat nicht die erwartete SHA-256-Prüfsumme – sie ist gelöscht und darf nicht benutzt werden
/// (z. B. ein Programm, das danach ausgeführt würde).
/// </summary>
public sealed class ModelChecksumException(string file, string expected, string actual)
    : IOException(Loc.T("voice.download.checksum", file, actual, expected))
{
    public string File { get; } = file;

    public string Expected { get; } = expected;

    public string Actual { get; } = actual;
}

/// <summary>
/// Lädt ein Modell einmalig in den Datenordner (Zwischendatei, Größenprüfung, Abbruch bei hängender Verbindung). Mit erwarteter
/// SHA-256-Prüfsumme wird jede Datei geprüft, bevor sie an ihren Platz kommt – bei Abweichung gelöscht.
/// </summary>
public static class ModelDownloader
{
    /// <param name="path">Ziel im Datenordner.</param>
    /// <param name="url">Download-Adresse.</param>
    /// <param name="minBytes">Kleinere Dateien sind sicher unvollständig.</param>
    /// <param name="progress">Fortschritt 0–1 (nur mit bekannter Länge).</param>
    /// <param name="ct">Abbruch.</param>
    /// <param name="sha256">
    /// Erwartete SHA-256-Prüfsumme (hex, Groß-/Kleinschreibung egal). null = keine Prüfung. Eine vorhandene Datei mit anderer
    /// Prüfsumme wird gelöscht und neu geladen; weicht auch der neue Download ab, wird er gelöscht und
    /// <see cref="ModelChecksumException"/> geworfen – die Datei liegt dann nicht am Ziel.
    /// </param>
    /// <param name="handler">Eigener HTTP-Handler (Tests). null = normales Netz.</param>
    public static async Task EnsureAsync(
        string path,
        string url,
        long minBytes,
        Action<double>? progress,
        CancellationToken ct,
        string? sha256 = null,
        HttpMessageHandler? handler = null)
    {
        var expected = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();
        if (File.Exists(path) && new FileInfo(path).Length >= minBytes)
        {
            if (expected is null || await Sha256Async(path, ct) == expected)
            {
                return;
            }

            // Vorhandene Datei passt nicht (alt, beschädigt, ausgetauscht) – nicht benutzen, neu laden
            File.Delete(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".download";
        using (var http = handler is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kopilot");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var lastReported = -1.0;
                int read;
                while ((read = await ReadWithStallTimeoutAsync(source, buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total is > 0)
                    {
                        var p = Math.Min(1.0, done / (double)total.Value);
                        if (p - lastReported >= 0.01)
                        {
                            lastReported = p;
                            progress?.Invoke(p);
                        }
                    }
                }
            }

            var length = new FileInfo(temp).Length;
            if ((total is > 0 && length != total) || length < minBytes)
            {
                File.Delete(temp);
                throw new IOException(Loc.T("voice.download.incomplete", Path.GetFileName(path), length / 1048576.0));
            }
        }

        if (expected is not null)
        {
            var actual = await Sha256Async(temp, ct);
            if (actual != expected)
            {
                File.Delete(temp);
                throw new ModelChecksumException(Path.GetFileName(path), expected, actual);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>SHA-256 einer Datei als Hex (klein).</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    // Hängt die Verbindung, nach einer Minute ohne Daten abbrechen statt ewig zu warten.
    private static async Task<int> ReadWithStallTimeoutAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            return await source.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("voice.download.stalled"));
        }
    }
}
