using System.Text;
using System.Text.Json;
using Duopilo.Hosting;
using Duopilo.Localization;
using Duopilo.Voice;

namespace Duopilo.Settings;

/// <summary>Schreiben in appsettings.Local.json ging nicht – die Meldung ist für den Piloten gedacht.</summary>
public sealed class SettingsWriteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Ändert einzelne Werte in appsettings.Local.json neben der Duopilo.exe – derselben Datei, die Duopilo beim Start liest.
/// Kommentare, Reihenfolge und Einrückung bleiben erhalten (<see cref="JsonTextPatcher"/>), damit Handarbeit in der Datei
/// nicht verloren geht.
/// <para>
/// Geschrieben wird direkt in die bestehende Datei, nicht über eine Temp-Datei daneben: Unter C:\Program Files darf der
/// Benutzer laut Installer nur diese eine Datei ändern, aber keine neuen Dateien im Programmordner anlegen. Vor jedem
/// Schreiben landet eine Kopie des alten Stands in <see cref="BackupPath"/> (Datenordner).
/// </para>
/// </summary>
public sealed class LocalSettingsStore(string filePath, string? backupPath = null)
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();

    /// <summary>Die Datei neben der Duopilo.exe, Sicherung im Datenordner.</summary>
    public static LocalSettingsStore ForApplication(DuopiloOptions options) => new(
        Path.Combine(AppContext.BaseDirectory, DuopiloConfiguration.LocalSettingsFile),
        Path.Combine(options.ResolveDataDirectory(), "appsettings.Local.backup.json"));

    public string FilePath { get; } = filePath;

    public string? BackupPath { get; } = backupPath;

    public bool Exists => File.Exists(FilePath);

    /// <summary>Wann zuletzt aus dem Dashboard geschrieben wurde (null = in diesem Lauf noch nicht).</summary>
    public DateTimeOffset? LastWriteUtc { get; private set; }

    /// <summary>Werte in einem Schreibvorgang setzen (alles oder nichts). Pfade ab der Wurzel, z. B. ["Duopilo", "SimBrief", "Username"].</summary>
    /// <exception cref="SettingsWriteException">Datei nicht lesbar oder schreibbar, oder sie ist kein gültiges JSON.</exception>
    public void Set(IReadOnlyList<SettingValue> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var original = Read();
            var text = Patch(original, values);
            Backup(original);
            Write(text);
            LastWriteUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Setzt die Werte im Text (rein rechnend, für Tests). Leerer Text oder nur Kommentare = leeres Objekt.</summary>
    /// <exception cref="SettingsWriteException">Der Text ist kein gültiges JSON.</exception>
    public static string Patch(string text, IReadOnlyList<SettingValue> values)
    {
        if (!HasContent(text))
        {
            // Datei vom Installer ("{ // Kommentar }") ist gültig; leer oder nur Kommentare wird zu einem Objekt darunter
            text = (text.TrimEnd() + Environment.NewLine + "{" + Environment.NewLine + "}" + Environment.NewLine).TrimStart();
        }

        try
        {
            using (JsonDocument.Parse(text, DocumentOptions))
            {
            }

            // Alte Datei (bis 0.9.x): Abschnitt "Kopilot" beim ersten Speichern in "Duopilo" umbenennen – sonst stünden die
            // neuen Werte daneben und die alten blieben als Altlast stehen
            text = JsonTextPatcher.RenameRootProperty(text, AppNames.Legacy.ConfigSection, DuopiloOptions.Section);

            foreach (var value in values)
            {
                text = JsonTextPatcher.Set(text, value.Path, value.RawJson);
            }

            // Kontrolle: Nie eine Datei schreiben, die Duopilo beim nächsten Start nicht lesen kann
            using (JsonDocument.Parse(text, DocumentOptions))
            {
            }
        }
        catch (JsonException ex)
        {
            throw new SettingsWriteException(Loc.T("settings.file.invalidJson", DuopiloConfiguration.LocalSettingsFile, ex.Message), ex);
        }

        return text;
    }

    private string Read()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsWriteException(Loc.T("settings.file.notReadable", FilePath, ex.Message), ex);
        }
    }

    private void Write(string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        try
        {
            if (File.Exists(FilePath))
            {
                // In die vorhandene Datei schreiben – braucht nur Schreibrecht auf die Datei, nicht auf den Ordner
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.SetLength(0);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
                File.WriteAllBytes(FilePath, bytes);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SettingsWriteException(Loc.T("settings.file.noWriteAccess", FilePath, DuopiloConfiguration.LocalSettingsFile), ex);
        }
        catch (IOException ex)
        {
            throw new SettingsWriteException(Loc.T("settings.file.notWritten", FilePath, ex.Message), ex);
        }
    }

    private void Backup(string original)
    {
        if (BackupPath is null || original.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(BackupPath))!);
            File.WriteAllText(BackupPath, original, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Die Sicherung ist nur eine Hilfe – ohne sie trotzdem schreiben
        }
    }

    /// <summary>Steht im Text mehr als Leerraum und Kommentare? (Kaputter Text zählt als Inhalt – die Prüfung danach meldet ihn.)</summary>
    private static bool HasContent(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '﻿')
            {
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return true;
                }

                i = end + 1;
                continue;
            }

            return true;
        }

        return false;
    }
}

/// <summary>Ein Wert für <see cref="LocalSettingsStore"/>: Pfad ab der Wurzel und fertiger JSON-Wert.</summary>
public sealed record SettingValue(IReadOnlyList<string> Path, string RawJson)
{
    public static SettingValue Of(IReadOnlyList<string> path, string value) => new(path, JsonTextPatcher.Quote(value));

    public static SettingValue Of(IReadOnlyList<string> path, bool value) => new(path, value ? "true" : "false");
}
