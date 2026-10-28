using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kopilot.Input;

/// <summary>Eine im Sim geprüfte Aktion. Fingerprint = Definition im Profil – ändert sie sich, gilt die Prüfung nicht mehr.</summary>
public sealed record VerifiedAction(string Profile, string Action, string Fingerprint, string Source, DateTimeOffset Utc, string? Value);

/// <summary>
/// Merkt sich, welche Aktionen im Sim nachweislich funktioniert haben (DataDirectory\verified-actions.json).
/// Liegt außerhalb des Programmordners, damit update.cmd es nicht überschreibt, und gilt für alle Kopilot-Instanzen.
/// <list type="bullet">
/// <item>"sim": Kopilot hat nach dem Ausführen einen echten Flugzeugzustand gelesen, der zum Wert passt.</item>
/// <item>"pilot": Der Pilot hat im Cockpit bestätigt, dass es funktioniert hat (für Aktionen, bei denen Kopilot nur die Schalterstellung lesen kann).</item>
/// </list>
/// </summary>
public sealed class VerifiedActionStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly JsonSerializerOptions FingerprintOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly object _lock = new();

    public string FilePath { get; } = Path.Combine(directory, "verified-actions.json");

    /// <summary>Kurzer Hash der Definition (Schritte, Prüfung, Bedingungen, Wert, Sperren).</summary>
    public static string Fingerprint(ProfileAction action)
    {
        var json = JsonSerializer.Serialize(
            new { action.Steps, action.Check, action.Requires, action.Parameter, action.Interlocks },
            FingerprintOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12].ToLowerInvariant();
    }

    public IReadOnlyList<VerifiedAction> All
    {
        get
        {
            lock (_lock)
            {
                return Read();
            }
        }
    }

    /// <summary>Gilt die Aktion als geprüft – im Profil markiert oder hier mit passender Definition gespeichert?</summary>
    public VerifiedAction? Find(string profile, ProfileAction action)
    {
        var fingerprint = Fingerprint(action);
        return All.FirstOrDefault(v =>
            string.Equals(v.Profile, profile, StringComparison.Ordinal)
            && string.Equals(v.Action, action.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Fingerprint, fingerprint, StringComparison.Ordinal));
    }

    public void Record(string profile, ProfileAction action, string source, string? value, DateTimeOffset now)
    {
        lock (_lock)
        {
            var entries = Read().Where(v => !(string.Equals(v.Profile, profile, StringComparison.Ordinal)
                                               && string.Equals(v.Action, action.Id, StringComparison.OrdinalIgnoreCase))).ToList();
            entries.Add(new VerifiedAction(profile, action.Id, Fingerprint(action), source, now, value));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document(entries.OrderBy(e => e.Profile).ThenBy(e => e.Action).ToList()), Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    private List<VerifiedAction> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), Options)?.Entries?.Where(e => e is not null).ToList() ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record Document(List<VerifiedAction>? Entries);
}
