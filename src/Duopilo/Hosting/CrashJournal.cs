using System.IO.MemoryMappedFiles;
using System.Text;

namespace Duopilo.Hosting;

/// <summary>Was das Journal über einen nicht sauber beendeten Lauf weiß.</summary>
/// <param name="Pid">Prozess-ID des alten Laufs.</param>
/// <param name="StartUtc">Start des alten Laufs.</param>
/// <param name="LastWriteUtc">Letzter Eintrag oder Zustandswechsel (≈ Absturzzeit).</param>
/// <param name="State">Was gerade lief, z. B. "SimConnect.ReceiveMessage" (null = nichts Besonderes).</param>
/// <param name="StateSinceUtc">Seit wann dieser Zustand galt.</param>
/// <param name="Entries">Letzte Einträge, älteste zuerst.</param>
public sealed record CrashJournalRun(
    int Pid,
    DateTimeOffset StartUtc,
    DateTimeOffset? LastWriteUtc,
    string? State,
    DateTimeOffset? StateSinceUtc,
    IReadOnlyList<(DateTimeOffset TimeUtc, string Text)> Entries);

/// <summary>
/// Kleines Journal als speicherabgebildete Datei (logs\journal-*.bin): Was Duopilo zuletzt getan hat, landet sofort in der
/// Datei – auch wenn der Prozess hart abstürzt (AccessViolation in SimConnect lässt sich in .NET nicht abfangen, und
/// die Logzeilen der letzten Millisekunden gehen verloren). Windows schreibt die Seiten auch nach dem Prozessende weg.
/// Beim nächsten Start sieht Duopilo am Kopf "läuft noch", dass der letzte Lauf nicht sauber endete, und gibt die
/// letzten Einträge ins Log. Threadsicher; Einträge sind kurz und kosten nur einen Speicherzugriff.
/// </summary>
public sealed class CrashJournal : IDisposable
{
    private const int Magic = 0x314A4B4B; // "KKJ1"
    private const int HeaderSize = 64;
    private const int StateSize = 256;
    private const int EntrySize = 192;
    private const int EntryCount = 256;
    private const int TextBytes = EntrySize - 10;
    public const int FileSize = HeaderSize + StateSize + (EntrySize * EntryCount);

    // Kopf: Magic(0) Running(4) Pid(8) StartTicks(16) LastWriteTicks(24) Next(32) Count(36)
    private const int OffRunning = 4;
    private const int OffPid = 8;
    private const int OffStart = 16;
    private const int OffLastWrite = 24;
    private const int OffNext = 32;
    private const int OffCount = 36;

    // Zustand: Ticks(0) Länge(8) Text(10…)
    private const int OffState = HeaderSize;

    private static CrashJournal? _current;

    private readonly object _lock = new();
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private int _next;
    private int _count;
    private bool _disposed;

    private CrashJournal(MemoryMappedFile file, MemoryMappedViewAccessor view, string path)
    {
        _file = file;
        _view = view;
        FilePath = path;
    }

    public string FilePath { get; }

    /// <summary>Journal dieses Prozesses (null = keins, z. B. stdio-Instanz oder Tests).</summary>
    public static CrashJournal? Current => Volatile.Read(ref _current);

    /// <summary>Eintrag ins Journal des Prozesses (ohne Journal: nichts).</summary>
    public static void Note(string text) => Current?.Add(text);

    /// <summary>Was gerade läuft (null = nichts). Billig genug für jeden SimConnect-Empfang.</summary>
    public static void State(string? text) => Current?.SetState(text);

    /// <summary>
    /// Öffnet das Journal und macht es zum <see cref="Current"/>. Liefert den alten Lauf, wenn er nicht sauber beendet wurde.
    /// </summary>
    public static (CrashJournal Journal, CrashJournalRun? Unclean) Open(string path, bool makeCurrent = true)
    {
        CrashJournalRun? previous = null;
        if (File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                previous = bytes.Length == FileSize ? ReadRun(bytes) : null;
            }
            catch (IOException)
            {
                // Gesperrt – vermutlich läuft noch eine andere Instanz
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path) && new FileInfo(path).Length != FileSize)
        {
            File.Delete(path);
        }

        var file = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, FileSize, MemoryMappedFileAccess.ReadWrite);
        var view = file.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.ReadWrite);
        var journal = new CrashJournal(file, view, path);
        journal.Start();
        if (makeCurrent)
        {
            Volatile.Write(ref _current, journal);
        }

        return (journal, previous);
    }

    public void Add(string text)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.UtcTicks;
            var offset = HeaderSize + StateSize + (_next * EntrySize);
            WriteText(offset, now, text);
            _next = (_next + 1) % EntryCount;
            _count = Math.Min(EntryCount, _count + 1);
            _view.Write(OffNext, _next);
            _view.Write(OffCount, _count);
            _view.Write(OffLastWrite, now);
        }
    }

    public void SetState(string? text)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.UtcTicks;
            WriteText(OffState, now, text ?? "", StateSize - 10);
            _view.Write(OffLastWrite, now);
        }
    }

    /// <summary>Sauberes Ende: beim nächsten Start kein Absturzbericht.</summary>
    public void MarkClean()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _view.Write(OffRunning, 0);
            _view.Flush();
        }
    }

    /// <summary>Liest den eigenen Stand (für Tests und Diagnose).</summary>
    public CrashJournalRun Snapshot()
    {
        var bytes = new byte[FileSize];
        lock (_lock)
        {
            _view.ReadArray(0, bytes, 0, FileSize);
        }

        return ReadRun(bytes, ignoreRunning: true)!;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _view.Flush();
            _view.Dispose();
            _file.Dispose();
        }

        Interlocked.CompareExchange(ref _current, null, this);
    }

    private void Start()
    {
        lock (_lock)
        {
            var empty = new byte[FileSize];
            _view.WriteArray(0, empty, 0, FileSize);
            _view.Write(0, Magic);
            _view.Write(OffRunning, 1);
            _view.Write(OffPid, Environment.ProcessId);
            _view.Write(OffStart, DateTimeOffset.UtcNow.UtcTicks);
            _view.Flush();
        }
    }

    private void WriteText(int offset, long ticks, string text, int maxBytes = TextBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = Math.Min(bytes.Length, maxBytes);

        // Nicht mitten in einem UTF-8-Zeichen abschneiden
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        _view.Write(offset, ticks);
        _view.Write(offset + 8, (ushort)length);
        _view.WriteArray(offset + 10, bytes, 0, length);
    }

    private static CrashJournalRun? ReadRun(byte[] bytes, bool ignoreRunning = false)
    {
        var span = bytes.AsSpan();
        if (BitConverter.ToInt32(span[..4]) != Magic || (!ignoreRunning && BitConverter.ToInt32(span[OffRunning..]) != 1))
        {
            return null;
        }

        var pid = BitConverter.ToInt32(span[OffPid..]);
        var start = Time(BitConverter.ToInt64(span[OffStart..]));
        var lastWrite = Time(BitConverter.ToInt64(span[OffLastWrite..]));
        var next = Math.Clamp(BitConverter.ToInt32(span[OffNext..]), 0, EntryCount - 1);
        var count = Math.Clamp(BitConverter.ToInt32(span[OffCount..]), 0, EntryCount);

        var (stateTime, state) = ReadText(span, OffState, StateSize - 10);
        var entries = new List<(DateTimeOffset, string)>(count);
        for (var i = 0; i < count; i++)
        {
            var index = (next - count + i + EntryCount) % EntryCount;
            var (time, text) = ReadText(span, HeaderSize + StateSize + (index * EntrySize), TextBytes);
            if (time is { } t)
            {
                entries.Add((t, text));
            }
        }

        return new CrashJournalRun(pid, start ?? DateTimeOffset.MinValue, lastWrite, state.Length > 0 ? state : null, state.Length > 0 ? stateTime : null, entries);
    }

    private static (DateTimeOffset? Time, string Text) ReadText(ReadOnlySpan<byte> span, int offset, int maxBytes)
    {
        var time = Time(BitConverter.ToInt64(span[offset..]));
        var length = Math.Min(BitConverter.ToUInt16(span[(offset + 8)..]), maxBytes);
        return (time, Encoding.UTF8.GetString(span.Slice(offset + 10, length)));
    }

    private static DateTimeOffset? Time(long ticks) =>
        ticks > 0 && ticks < DateTimeOffset.MaxValue.UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
}
