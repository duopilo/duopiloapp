using System.Text.Json;

namespace Kopilot.Speech;

/// <summary>Im Dashboard oder Tray gewählte Einstellungen (an/aus, Stimme). Null = Wert aus appsettings.</summary>
public sealed record SpeechSettings(bool? Enabled, string? Voice);

/// <summary>Merkt sich die Laufzeit-Auswahl in <c>speech.json</c> im Datenordner, damit sie einen Neustart übersteht.</summary>
public sealed class SpeechSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _lock = new();

    public string Path { get; } = path;

    public SpeechSettings Load()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(Path)
                    ? JsonSerializer.Deserialize<SpeechSettings>(File.ReadAllText(Path), Options) ?? new SpeechSettings(null, null)
                    : new SpeechSettings(null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new SpeechSettings(null, null);
            }
        }
    }

    /// <summary>Speichert; Fehler (Rechte, gesperrte Datei) werden geschluckt – dann gilt die Auswahl eben nur bis zum Neustart.</summary>
    public bool Save(SpeechSettings settings)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
                File.Move(temp, Path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
