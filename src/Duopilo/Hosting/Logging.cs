using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Duopilo.Localization;

namespace Duopilo.Hosting;

/// <summary>Eine Logzeile für Dashboard und Logdatei.</summary>
public sealed record LogLine(DateTimeOffset Time, LogLevel Level, string Category, string Message);

/// <summary>Die letzten Logzeilen im Speicher – für das Dashboard (ohne Konsolenfenster gibt es sonst keine Sicht darauf).</summary>
public sealed class LogBuffer(int capacity = 300)
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public void Add(LogLine line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > capacity && _lines.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<LogLine> Tail(int count) => _lines.TakeLast(Math.Max(0, count)).ToList();
}

/// <summary>
/// Schreibt Logs nach %LOCALAPPDATA%\Duopilo\logs\duopilo.log (stdio-Modus: duopilo-stdio.log; bei 5 MB wird *.1.log daraus) und in den <see cref="LogBuffer"/>.
/// Geschrieben wird auf einem eigenen Thread, damit SimConnect- und HTTP-Threads nie auf die Platte warten.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly LogBuffer _buffer;
    private readonly LogLevel _minLevel;
    private readonly Channel<LogLine> _channel = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;

    /// <param name="minLevel">
    /// Untergrenze der Datei. Standard Trace: Was im Log landet, entscheidet die Konfiguration ("Logging:LogLevel", Standard Information) –
    /// z. B. "Duopilo.Voice": "Debug" in appsettings.Local.json für jede Satzgrenze der Sprachschleife.
    /// </param>
    public FileLoggerProvider(string directory, string fileName, LogBuffer buffer, LogLevel minLevel = LogLevel.Trace)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, fileName);
        _buffer = buffer;
        _minLevel = minLevel;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string FilePath => _path;

    /// <summary>Name der Logdatei: duopilo.log (Tray/HTTP) bzw. duopilo-stdio.log. Bis 0.9.x hießen sie kopilot*.log.</summary>
    public static string FileName(string mode) =>
        mode == "stdio" ? $"{AppNames.Product.ToLowerInvariant()}-stdio.log" : $"{AppNames.Product.ToLowerInvariant()}.log";

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Beim Beenden egal
        }
    }

    private void Write(LogLine line)
    {
        _buffer.Add(line);
        _channel.Writer.TryWrite(line);
    }

    private async Task WriteLoopAsync()
    {
        var sb = new StringBuilder();
        await foreach (var line in _channel.Reader.ReadAllAsync())
        {
            sb.Clear();
            sb.Append(Format(line));
            while (_channel.Reader.TryRead(out var more))
            {
                sb.Append(Format(more));
            }

            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Move(_path, Path.ChangeExtension(_path, ".1.log"), overwrite: true);
                }

                await File.AppendAllTextAsync(_path, sb.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Datei kurz gesperrt (z. B. im Editor geöffnet) – Zeilen verwerfen statt hängen
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Format(LogLine l) =>
        $"{l.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {Level(l.Level)} {l.Category}: {l.Message}{Environment.NewLine}";

    public static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "   ",
    };

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Logs bleiben Englisch: übersetzte Texte in den Argumenten (Alert-Erklärung, Grund, Fehler) gegen ihre englische Fassung tauschen
            var message = EnglishLog.Format(state, exception, formatter);
            if (exception is not null)
            {
                message += $" | {exception.GetType().Name}: {Loc.ToEnglish(exception.Message)}";
            }

            provider.Write(new LogLine(DateTimeOffset.Now, logLevel, category, message));
        }
    }
}

/// <summary>
/// Logs are always English (Loc.Log gives English templates). Arguments can still carry text the user sees translated – an alert
/// message, a refusal reason, an error. If one of them has an English twin (<see cref="Loc.ToEnglish"/>), the line is rendered again
/// with the English text; otherwise the logger's own formatting is used unchanged.
/// </summary>
public static class EnglishLog
{
    private const string OriginalFormat = "{OriginalFormat}";

    public static string Format<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values || values.Count < 2)
        {
            return formatter(state, exception);
        }

        string? template = null;
        var args = new List<object?>(values.Count);
        var changed = false;
        foreach (var (key, value) in values)
        {
            if (key == OriginalFormat)
            {
                template = value as string;
                continue;
            }

            if (value is string text && Loc.ToEnglish(text) is { } english && !ReferenceEquals(english, text) && english != text)
            {
                args.Add(english);
                changed = true;
            }
            else
            {
                args.Add(value);
            }
        }

        return changed && template is not null ? Render(template, args) : formatter(state, exception);
    }

    /// <summary>Message template with positional values, like Microsoft.Extensions.Logging (invariant culture, "(null)", lists joined).</summary>
    public static string Render(string template, IReadOnlyList<object?> args)
    {
        var sb = new StringBuilder(template.Length + 64);
        var index = 0;
        var pos = 0;
        while (pos < template.Length)
        {
            var c = template[pos];
            if ((c == '{' || c == '}') && pos + 1 < template.Length && template[pos + 1] == c)
            {
                sb.Append(c);
                pos += 2;
                continue;
            }

            if (c == '{')
            {
                var end = template.IndexOf('}', pos + 1);
                if (end > pos)
                {
                    var hole = template[(pos + 1)..end];
                    var colon = hole.IndexOf(':', StringComparison.Ordinal);
                    var format = colon >= 0 ? hole[(colon + 1)..] : null;
                    sb.Append(index < args.Count ? Value(args[index], format) : template[pos..(end + 1)]);
                    index++;
                    pos = end + 1;
                    continue;
                }
            }

            sb.Append(c);
            pos++;
        }

        return sb.ToString();
    }

    private static string Value(object? value, string? format) => value switch
    {
        null => "(null)",
        string s => s,
        IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
        System.Collections.IEnumerable list => string.Join(", ", list.Cast<object?>().Select(v => Value(v, null))),
        _ => value.ToString() ?? "",
    };
}
