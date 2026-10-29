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

    /// <summary>
    /// Entfernt ungültige oder gesperrte Aktionen und meldet sie: leere Einträge, doppelte IDs, nicht erlaubte Events/Variablen,
    /// fehlende Pflicht-Sperren, unbekannte Sperren und Klappenstellungen ohne VFE.
    /// </summary>
    public static void ValidateActions(AircraftProfile profile, List<string> errors)
    {
        var file = profile.SourceFile ?? profile.Name;
        profile.Actions ??= [];
        profile.Limits ??= new();
        profile.Actions.RemoveAll(a => a is null);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        profile.Actions.RemoveAll(action =>
        {
            action.Steps ??= [];
            action.Interlocks ??= [];
            action.Requires ??= [];
            action.Requires.RemoveAll(r => r is null);

            var unknownInterlock = action.Interlocks.FirstOrDefault(i => i is null || !Kopilot.Input.Interlocks.All.Contains(i, StringComparer.OrdinalIgnoreCase));
            var hasUnknownInterlock = action.Interlocks.Any(i => i is null || !Kopilot.Input.Interlocks.All.Contains(i, StringComparer.OrdinalIgnoreCase));
            var needsVfe = action.Interlocks.Contains(Kopilot.Input.Interlocks.FlapsSpeed, StringComparer.OrdinalIgnoreCase);
            var missingVfe = needsVfe
                && (action.Parameter is not { IsChoice: true, Options: { } options }
                    || options.Keys.Any(o => o != "0" && profile.Limits.VfeFor(null, o) is null));

            string? problem = null;
            if (string.IsNullOrWhiteSpace(action.Id) || action.Steps.Count == 0)
            {
                problem = $"Aktion \"{action.Id}\" ohne id oder Schritte";
            }
            else if (!seen.Add(action.Id))
            {
                problem = $"Aktion {action.Id} doppelt";
            }
            else if (Kopilot.Input.ActionSafety.Problem(action) is { } unsafeStep)
            {
                problem = $"Aktion {action.Id}: {unsafeStep}";
            }
            else if (hasUnknownInterlock)
            {
                problem = $"Aktion {action.Id}: unbekannte Sperre \"{unknownInterlock}\"";
            }
            else if (missingVfe)
            {
                problem = $"Aktion {action.Id}: VFE fehlt in limits.vfeKt";
            }

            if (problem is null)
            {
                return false;
            }

            errors.Add($"{file}: {problem} – entfernt");
            return true;
        });
    }

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
                    profile.Fuel ??= [];
                    profile.Fuel.RemoveAll(v => v is null || string.IsNullOrWhiteSpace(v.Key) || string.IsNullOrWhiteSpace(v.Name));
                    profile.Bindings = new Dictionary<string, ProfileBinding>(
                        profile.Bindings.Where(b => !string.IsNullOrWhiteSpace(b.Value.Name)),
                        StringComparer.OrdinalIgnoreCase);
                    ValidateActions(profile, errors);
                    profiles.Add(profile);
                }
                catch (Exception ex)
                {
                    // Ein kaputtes Profil darf den Start nicht verhindern
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
