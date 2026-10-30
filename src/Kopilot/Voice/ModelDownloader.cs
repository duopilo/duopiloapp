using Kopilot.Localization;

namespace Kopilot.Voice;

/// <summary>Lädt ein Modell einmalig in den Datenordner (Zwischendatei, Größenprüfung, Abbruch bei hängender Verbindung).</summary>
public static class ModelDownloader
{
    public static async Task EnsureAsync(string path, string url, long minBytes, Action<double>? progress, CancellationToken ct)
    {
        if (File.Exists(path) && new FileInfo(path).Length >= minBytes)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".download";
        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
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

        File.Move(temp, path, overwrite: true);
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
