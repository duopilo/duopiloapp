using System.Text.Json;
using System.Text.Json.Serialization;
using Kopilot.Localization;

namespace Kopilot.Atc;

/// <summary>
/// Sichert die laufende ATC-Sitzung in einer JSON-Datei (Standard %LOCALAPPDATA%\Kopilot\atc-session.json).
/// Geschrieben wird über eine Zwischendatei, damit ein Absturz mitten im Schreiben keine halbe Datei hinterlässt.
/// </summary>
public sealed class AtcSessionStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();

    /// <param name="path">Datei; null = Sitzung wird nicht gesichert.</param>
    public AtcSessionStore(string? path) => FilePath = path;

    public string? FilePath { get; }

    public DateTimeOffset? LastSavedUtc { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Pfad aus Kopilot:Atc:SessionFile (relativ = im Datenordner, leer = aus).</summary>
    public static AtcSessionStore From(KopilotOptions options)
    {
        var file = options.Atc.SessionFile?.Trim();
        if (string.IsNullOrEmpty(file))
        {
            return new AtcSessionStore(null);
        }

        return new AtcSessionStore(Path.IsPathRooted(file) ? file : Path.Combine(options.ResolveDataDirectory(), file));
    }

    public static string Serialize(AtcSessionSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static AtcSessionSnapshot? Deserialize(string json) => JsonSerializer.Deserialize<AtcSessionSnapshot>(json, Options);

    /// <summary>Gesicherte Sitzung oder null (keine Datei, andere Version, unlesbar).</summary>
    public AtcSessionSnapshot? Load()
    {
        if (FilePath is null || !File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var snapshot = Deserialize(File.ReadAllText(FilePath));
            return snapshot?.Version == AtcSessionSnapshot.CurrentVersion ? snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LastError = Loc.T("atc.session.unreadable", Path.GetFileName(FilePath), ex.Message);
            return null;
        }
    }

    /// <summary>Sichern. Fehler werden gemerkt, nicht geworfen – ATC läuft auch ohne Sicherung weiter.</summary>
    public bool Save(AtcSessionSnapshot snapshot)
    {
        if (FilePath is null)
        {
            return false;
        }

        var json = Serialize(snapshot);
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
                LastSavedUtc = snapshot.SavedUtc;
                LastError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = Loc.T("atc.session.notSaved", Path.GetFileName(FilePath), ex.Message);
                return false;
            }
        }
    }

    /// <summary>Sitzung bewusst beendet (atc_control stop): nichts mehr wiederherstellen.</summary>
    public void Delete()
    {
        if (FilePath is null)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                File.Delete(FilePath);
                LastSavedUtc = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = Loc.T("atc.session.notDeleted", Path.GetFileName(FilePath), ex.Message);
            }
        }
    }
}
