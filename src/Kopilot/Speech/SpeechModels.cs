using System.Text.Json;
using System.Text.Json.Serialization;
using Kopilot.Monitoring;

namespace Kopilot.Speech;

/// <summary>Einstellungen der Sprachausgabe, im Dashboard änderbar und in speech.json gespeichert.</summary>
public sealed record SpeechSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Stimme (Id oder Anzeigename). Leer = erste englische Stimme.</summary>
    public string? Voice { get; init; }

    /// <summary>Ausgabegerät (Windows-Geräte-ID). Leer = Standardgerät.</summary>
    public string? Device { get; init; }

    public double Volume { get; init; } = 1.0;

    public double Rate { get; init; } = 1.1;

    [JsonConverter(typeof(JsonStringEnumConverter<AlertSeverity>))]
    public AlertSeverity MinSeverity { get; init; } = AlertSeverity.Info;

    /// <summary>Regeln, die angezeigt, aber nicht gesprochen werden.</summary>
    public IReadOnlyList<string> MutedRules { get; init; } = [];

    /// <summary>Radiohöhen/Minimum/Retard auch von Kopilot (für Flugzeuge ohne eigene Ansagen).</summary>
    public bool AircraftAutoCallouts { get; init; }

    public static SpeechSettings FromOptions(SpeechOptions speech, MonitoringOptions monitoring) => new()
    {
        Enabled = speech.Enabled,
        Voice = string.IsNullOrWhiteSpace(speech.Voice) ? null : speech.Voice,
        Device = string.IsNullOrWhiteSpace(speech.Device) ? null : speech.Device,
        Volume = speech.Volume,
        Rate = speech.Rate,
        MinSeverity = Enum.TryParse<AlertSeverity>(speech.MinSeverity, ignoreCase: true, out var sev) ? sev : AlertSeverity.Info,
        AircraftAutoCallouts = monitoring.AircraftAutoCallouts,
    };

    public SpeechSettings Normalized() => this with
    {
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 1, 0, 1),
        Rate = Math.Clamp(double.IsFinite(Rate) ? Rate : 1, 0.5, 2),
        Voice = string.IsNullOrWhiteSpace(Voice) ? null : Voice.Trim(),
        Device = string.IsNullOrWhiteSpace(Device) ? null : Device.Trim(),
        MutedRules = MutedRules.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim().ToUpperInvariant()).Distinct().ToList(),
    };
}

public sealed record VoiceInfo(string Id, string Name, string Language, string Gender);

public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Zustand der Sprachausgabe für Dashboard und Status.</summary>
public sealed record SpeechStatus(
    bool Available,
    string? Error,
    SpeechSettings Settings,
    string? ActiveVoice,
    IReadOnlyList<VoiceInfo> Voices,
    IReadOnlyList<AudioDeviceInfo> Devices,
    bool Speaking,
    string? LastSpoken);

/// <summary>Sprachausgabe von Kopilot (gibt es nur im HTTP-Modus unter Windows).</summary>
public interface ISpeechOutput
{
    SpeechStatus Status { get; }

    Task<SpeechStatus> RefreshAsync(CancellationToken ct);

    Task<SpeechStatus> UpdateAsync(SpeechSettings settings, CancellationToken ct);

    Task SpeakNowAsync(string text, CancellationToken ct);
}

/// <summary>Speichert die Dashboard-Einstellungen in DataDirectory\speech.json.</summary>
public sealed class SpeechSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath { get; } = path;

    public SpeechSettings? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<SpeechSettings>(File.ReadAllText(FilePath), Options)?.Normalized() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(SpeechSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}
