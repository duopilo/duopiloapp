using System.Globalization;

namespace Duopilo.Sim;

/// <summary>
/// Schreibt jede BYTYPE-Nachricht der Verkehrsverbindung als Zeile nach logs\traffic-messages.csv (bei 10 MB wird
/// traffic-messages.1.csv daraus). Dient zum Prüfen der Absturz-Hypothesen; abschaltbar mit Monitoring:Traffic:MessageLog.
/// </summary>
public sealed class TrafficMessageLog : IDisposable
{
    public const string Header = "utc,requestId,kind,defineId,dwSize,cbData,objectId,entry,outOf,payloadBytes,expectedBytes,flags";

    private const long MaxBytes = 10 * 1024 * 1024;

    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _failed;

    public TrafficMessageLog(string directory)
    {
        FilePath = Path.Combine(directory, "traffic-messages.csv");
    }

    public string FilePath { get; }

    /// <summary>Fehler beim Schreiben, sonst null. Danach schreibt das Log nichts mehr.</summary>
    public string? Error { get; private set; }

    public void Write(DateTimeOffset now, string kind, ByTypeMessage m)
    {
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }

            try
            {
                var writer = _writer ??= Open();
                writer.WriteLine(Line(now, kind, m));
                if (writer.BaseStream.Length > MaxBytes)
                {
                    writer.Dispose();
                    _writer = null;
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".1.csv"), overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failed = true;
                Error = ex.Message;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    /// <summary>Eine CSV-Zeile, Spalten wie <see cref="Header"/>.</summary>
    public static string Line(DateTimeOffset now, string kind, ByTypeMessage m) => string.Join(
        ',',
        now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        m.RequestId.ToString(CultureInfo.InvariantCulture),
        kind,
        m.DefineId.ToString(CultureInfo.InvariantCulture),
        m.DwSize.ToString(CultureInfo.InvariantCulture),
        m.ReceivedBytes.ToString(CultureInfo.InvariantCulture),
        m.ObjectId.ToString(CultureInfo.InvariantCulture),
        m.EntryNumber.ToString(CultureInfo.InvariantCulture),
        m.OutOf.ToString(CultureInfo.InvariantCulture),
        m.PayloadBytes.ToString(CultureInfo.InvariantCulture),
        m.ExpectedPayloadBytes.ToString(CultureInfo.InvariantCulture),
        Flags(m));

    /// <summary>"invalid", "empty", "short", "ok", bei abweichender Größe mit "+mismatch".</summary>
    public static string Flags(ByTypeMessage m)
    {
        var flag = !m.HeaderComplete ? "invalid" : m.IsEmptyReply ? "empty" : m.IsShort ? "short" : "ok";
        return m.SizeMismatch ? flag + "+mismatch" : flag;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var isNew = !File.Exists(FilePath) || new FileInfo(FilePath).Length == 0;
        var writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        if (isNew)
        {
            writer.WriteLine(Header);
        }

        return writer;
    }
}
