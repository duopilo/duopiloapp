using Kopilot.Aircraft;

namespace Kopilot.Monitoring;

/// <summary>Wie gut ein Callout mit einem Profil funktioniert.</summary>
/// <param name="Rule">Regel-ID, z. B. "TCAS_RA".</param>
/// <param name="Status">"verified" (alle Bindings im Sim geprüft), "unverified" (Bindings vorhanden, mindestens eins ungeprüft) oder "missing" (Binding fehlt – Callout schweigt).</param>
/// <param name="Missing">Fehlende Bindings.</param>
/// <param name="Unverified">Vorhandene, aber im Sim noch nicht geprüfte Bindings.</param>
public sealed record CalloutSupport(string Rule, string Status, IReadOnlyList<string> Missing, IReadOnlyList<string> Unverified);

/// <summary>
/// Welche Profil-Bindings die Autoflug- und Anflug-Callouts brauchen – und je Profil, ob sie vorhanden und im Sim geprüft sind.
/// Fehlt ein Binding, wird nichts geraten: der Callout bleibt für dieses Profil aus und steht hier als "missing".
/// </summary>
public static class CalloutCoverage
{
    public const string Verified = "verified";
    public const string Unverified = "unverified";
    public const string Missing = "missing";

    /// <summary>Regel → nötige Bindings (alle müssen vorhanden sein).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Requirements = new Dictionary<string, IReadOnlyList<string>>
    {
        [Rules.TcasRa] = [BindingKeys.FmaVertical],
        [Rules.AutothrustOff] = [BindingKeys.AthrStatus],
        // Armed-Modi ("Glideslope blue") nur mit fmaLateralArmed/fmaVerticalArmed – ohne sie kommen die aktiven Modi trotzdem
        [Rules.FmaChange] = [BindingKeys.FmaLateral, BindingKeys.FmaVertical],
        [Rules.LocDeviation] = [BindingKeys.LocDeviation, BindingKeys.FmaLateral],
        [Rules.GsDeviation] = [BindingKeys.GsDeviation, BindingKeys.FmaVertical],
        [Rules.ApproachPhase] = [BindingKeys.FmgcPhase, BindingKeys.SpeedManaged],
    };

    /// <summary>Stand aller Autoflug-Callouts für ein Profil (ohne Profil: alles "missing").</summary>
    public static IReadOnlyList<CalloutSupport> For(AircraftProfile? profile) =>
        Requirements.Select(r => Support(r.Key, r.Value, profile)).ToList();

    /// <summary>Stand eines Callouts für ein Profil.</summary>
    public static CalloutSupport For(AircraftProfile? profile, string rule) =>
        Requirements.TryGetValue(rule, out var keys) ? Support(rule, keys, profile) : new CalloutSupport(rule, Verified, [], []);

    private static CalloutSupport Support(string rule, IReadOnlyList<string> keys, AircraftProfile? profile)
    {
        var bindings = profile?.Bindings;
        var missing = keys.Where(k => bindings is null || !bindings.ContainsKey(k)).ToList();
        var unverified = keys.Where(k => bindings is not null && bindings.TryGetValue(k, out var b) && !b.Verified).ToList();
        var status = missing.Count > 0 ? Missing : unverified.Count > 0 ? Unverified : Verified;
        return new CalloutSupport(rule, status, missing, unverified);
    }
}
