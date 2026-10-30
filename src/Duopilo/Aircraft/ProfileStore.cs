using System.Text.Json;
using Duopilo.Localization;
using Microsoft.Extensions.Options;

namespace Duopilo.Aircraft;

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
    private volatile IReadOnlyList<string> _warnings = [];

    public ProfileStore(IOptions<DuopiloOptions> options, ILogger<ProfileStore> log)
    {
        _log = log;
        var dir = options.Value.ProfilesDirectory;
        DirectoryPath = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
        Reload();
    }

    public string DirectoryPath { get; }

    public IReadOnlyList<AircraftProfile> All => _profiles;

    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Fehlende Grenzwerte und Quellen: Die Aktionen bleiben, gehen aber nur mit Rückfrage.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Bestes Profil für Titel und aircraft.cfg-Pfad (Pfad-Treffer schlagen Titel-Treffer).</summary>
    public AircraftProfile? Match(string? title, string? path) =>
        _profiles
            .Select(p => (Profile: p, Score: p.MatchScore(title, path)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Profile)
            .FirstOrDefault();

    /// <summary>
    /// Entfernt ungültige oder gesperrte Aktionen und meldet sie: leere Einträge, doppelte IDs, gesperrte Bereiche (Triebwerk,
    /// Treibstoff, Feuer, Hydraulik, IDG), nicht erlaubte Events/Variablen, sicherheitsrelevante Schritte unter fremder ID,
    /// fehlende Pflicht-Sperren, unbekannte Sperren und Klappen ohne Auswahl der Stellung.
    /// Fehlende Grenzwerte (VLO/VLE, VFE je Stellung, Höchsthöhe Fahrwerk, Speedbrake-Klappen, MSA-Quelle) entfernen nichts –
    /// die Aktion geht dann nur mit Rückfrage; das steht in <paramref name="warnings"/>.
    /// </summary>
    public static void ValidateActions(AircraftProfile profile, List<string> errors, List<string>? warnings = null)
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

            var unknownInterlock = action.Interlocks.FirstOrDefault(i => i is null || !Duopilo.Input.Interlocks.All.Contains(i, StringComparer.OrdinalIgnoreCase));
            var hasUnknownInterlock = action.Interlocks.Any(i => i is null || !Duopilo.Input.Interlocks.All.Contains(i, StringComparer.OrdinalIgnoreCase));
            var needsVfe = action.Interlocks.Contains(Duopilo.Input.Interlocks.FlapsSpeed, StringComparer.OrdinalIgnoreCase);
            var flapsWithoutChoice = needsVfe && action.Parameter is not { IsChoice: true, Options.Count: > 0 };

            string? problem = null;
            if (string.IsNullOrWhiteSpace(action.Id) || action.Steps.Count == 0)
            {
                problem = Loc.T("aircraft.action.noIdOrSteps", action.Id);
            }
            else if (!seen.Add(action.Id))
            {
                problem = Loc.T("aircraft.action.duplicate", action.Id);
            }
            else if (Duopilo.Input.ActionSafety.Problem(action) is { } unsafeStep)
            {
                problem = Loc.T("aircraft.action.unsafe", action.Id, unsafeStep);
            }
            else if (hasUnknownInterlock)
            {
                problem = Loc.T("aircraft.action.unknownInterlock", action.Id, unknownInterlock);
            }
            else if (flapsWithoutChoice)
            {
                problem = Loc.T("aircraft.action.flapsWithoutChoice", action.Id);
            }

            if (problem is null)
            {
                return false;
            }

            errors.Add(Loc.T("aircraft.action.removed", file, problem));
            return true;
        });

        if (warnings is not null)
        {
            warnings.AddRange(MissingLimits(profile).Select(w => $"{file}: {w}"));
        }
    }

    /// <summary>Grenzwerte und Quellen, die für die vorhandenen Aktionen fehlen (dann nur mit Rückfrage).</summary>
    public static IEnumerable<string> MissingLimits(AircraftProfile profile)
    {
        var limits = profile.Limits ?? new AircraftLimits();
        bool Has(string id) => profile.Actions.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (Has("gear.down"))
        {
            if (limits.VloKt is null && limits.VleKt is null)
            {
                yield return Loc.T("aircraft.limits.vloMissing");
            }

            if (limits.MaxGearAltitudeFt is null)
            {
                yield return Loc.T("aircraft.limits.maxGearAltitudeMissing");
            }
        }

        foreach (var flaps in profile.Actions.Where(a => a.Interlocks.Contains(Duopilo.Input.Interlocks.FlapsSpeed, StringComparer.OrdinalIgnoreCase)))
        {
            var missing = flaps.Parameter?.Options?.Keys.Where(o => o != "0" && limits.VfeFor(null, o) is null).ToList() ?? [];
            if (missing.Count > 0)
            {
                yield return Loc.T("aircraft.limits.vfeMissing", flaps.Id, string.Join(", ", missing));
            }
        }

        if (Has("speedbrake") && limits.SpeedbrakeInhibitFlaps is null)
        {
            yield return Loc.T("aircraft.limits.speedbrakeInhibitMissing");
        }

        if (Has("fcu.altitude"))
        {
            if (limits.MsaSource is null)
            {
                yield return Loc.T("aircraft.limits.msaSourceMissing");
            }
            else if (!AircraftLimits.MsaSources.Contains(limits.MsaSource.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                yield return Loc.T("aircraft.limits.msaSourceUnknown", limits.MsaSource, string.Join(", ", AircraftLimits.MsaSources));
            }
        }
    }

    public void Reload()
    {
        var profiles = new List<AircraftProfile>();
        var errors = new List<string>();
        var warnings = new List<string>();

        if (Directory.Exists(DirectoryPath))
        {
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<AircraftProfile>(File.ReadAllText(file), ReadOptions);
                    if (profile is null)
                    {
                        errors.Add(Loc.T("aircraft.profile.empty", Path.GetFileName(file)));
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
                    ValidateActions(profile, errors, warnings);
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
            errors.Add(Loc.T("aircraft.profile.directoryMissing", DirectoryPath));
        }

        _profiles = profiles;
        _errors = errors;
        _warnings = warnings;

        _log.LogInformation(Loc.Log("log.aircraft.profilesLoaded"), profiles.Count, DirectoryPath);
        foreach (var error in errors)
        {
            _log.LogWarning(Loc.Log("log.aircraft.profileError"), error);
        }

        foreach (var warning in warnings)
        {
            _log.LogWarning(Loc.Log("log.aircraft.limitMissing"), warning);
        }
    }
}
