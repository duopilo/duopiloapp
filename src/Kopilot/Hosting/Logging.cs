using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Kopilot.Hosting;

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
/// Schreibt Logs nach %LOCALAPPDATA%\Kopilot\logs\kopilot.log (stdio-Modus: kopilot-stdio.log; bei 5 MB wird *.1.log daraus) und in den <see cref="LogBuffer"/>.
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

    public FileLoggerProvider(string directory, string fileName, LogBuffer buffer, LogLevel minLevel = LogLevel.Information)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, fileName);
        _buffer = buffer;
        _minLevel = minLevel;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string FilePath => _path;

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

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $" | {exception.GetType().Name}: {exception.Message}";
            }

            provider.Write(new LogLine(DateTimeOffset.Now, logLevel, category, message));
        }
    }
}
