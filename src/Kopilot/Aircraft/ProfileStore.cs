using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kopilot.Aircraft;

/// <summary>Lädt Flugzeugprofile aus dem Ordner "profiles" (*.json).</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<ProfileStore> _log;
    private volatile IReadOnlyList<AircraftProfile> _profiles = [];
    private volatile IReadOnlyList<string> _errors = [];

    public ProfileStore(IOptions<KopilotOptions> options, ILogger<ProfileStore> log)
    {
        _log = log;
        var dir = options.Value.ProfilesDirectory;
        DirectoryPath = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
        Reload();
    }

    public string DirectoryPath { get; }

    public IReadOnlyList<AircraftProfile> All => _profiles;

    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Bestes Profil für Titel und aircraft.cfg-Pfad (Pfad-Treffer schlagen Titel-Treffer).</summary>
    public AircraftProfile? Match(string? title, string? path) =>
        _profiles
            .Select(p => (Profile: p, Score: p.MatchScore(title, path)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Profile)
            .FirstOrDefault();

    public void Reload()
    {
        var profiles = new List<AircraftProfile>();
        var errors = new List<string>();

        if (Directory.Exists(DirectoryPath))
        {
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<AircraftProfile>(File.ReadAllText(file), ReadOptions);
                    if (profile is null)
                    {
                        errors.Add($"{Path.GetFileName(file)}: leer");
                        continue;
                    }

                    profile.SourceFile = Path.GetFileName(file);
                    profile.Variables.RemoveAll(v => string.IsNullOrWhiteSpace(v.Key) || string.IsNullOrWhiteSpace(v.Name));
                    profile.Electrical.RemoveAll(v => string.IsNullOrWhiteSpace(v.Key) || string.IsNullOrWhiteSpace(v.Name));
                    profile.Bindings = new Dictionary<string, ProfileBinding>(
                        profile.Bindings.Where(b => !string.IsNullOrWhiteSpace(b.Value.Name)),
                        StringComparer.OrdinalIgnoreCase);
                    profiles.Add(profile);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        else
        {
            errors.Add($"Profilordner nicht gefunden: {DirectoryPath}");
        }

        _profiles = profiles;
        _errors = errors;

        _log.LogInformation("{Count} Flugzeugprofil(e) geladen aus {Dir}", profiles.Count, DirectoryPath);
        foreach (var error in errors)
        {
            _log.LogWarning("Profilfehler: {Error}", error);
        }
    }
}
