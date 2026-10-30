using Duopilo.Aircraft;
using Duopilo.Atc;
using Duopilo.Ecam;
using Duopilo.FlightPlan;
using Duopilo.Input;
using Duopilo.Monitoring;
using Duopilo.Sim;
using Duopilo.Traffic;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Duopilo.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>Gemeinsame Dienste für stdio- und HTTP-Modus: Profile, SimConnect, ECAM, SimBrief, Monitoring und Eingabe.</summary>
    public static IServiceCollection AddDuopiloCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DuopiloOptions>(configuration.GetSection(DuopiloOptions.Section));

        // Sprache für Dashboard, Tool-Meldungen und Tray (Duopilo:Language) – so früh wie möglich; Logs bleiben Englisch
        Duopilo.Localization.Loc.Configure(configuration);
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<SimConnectClient>();
        services.AddHostedService(sp => sp.GetRequiredService<SimConnectClient>());

        services.AddSingleton<EcamCatalog>();
        services.AddSingleton<SimBriefService>();
        // Verkehr (get_traffic, Hintergrundabfrage für TRAFFIC_PROXIMITY)
        services.AddSingleton<TrafficConnection>();
        services.AddSingleton<TrafficService>();
        services.AddHostedService(sp => sp.GetRequiredService<TrafficService>());

        services.AddSingleton(sp => new AlertBus(sp.GetRequiredService<IOptions<DuopiloOptions>>().Value.Monitoring.HistorySize));
        services.AddSingleton<SnapshotFactory>();
        services.AddSingleton<ConfigCalloutClaims>();
        services.AddSingleton(new FmaLog());
        services.AddSingleton<MonitoringService>();
        services.AddHostedService(sp => sp.GetRequiredService<MonitoringService>());

        // Eingabe-Modul (standardmäßig gesperrt)
        services.AddSingleton<IInputBackend, SimInputBackend>();
        services.AddSingleton<IInputContext, InputContext>();
        services.AddSingleton<ConfigVerifier>();
        services.AddSingleton<InputService>();

        // ATC-Modul (Zustandsmaschine mit Vorlagen). Die Rollroute ist eine Schnittstelle – bis zur Routenberechnung ohne Route.
        services.AddSingleton<ITaxiRouteProvider, NoTaxiRouteProvider>();
        services.AddSingleton<AtcService>();
        services.AddHostedService(sp => sp.GetRequiredService<AtcService>());

        // Zustand der Sprachschleife für duopilo_status – die Tray-Instanz ersetzt den Platzhalter (AddDuopiloVoice)
        services.TryAddSingleton<Duopilo.Voice.IVoiceStatusSource, Duopilo.Voice.NoVoiceLoop>();
        return services;
    }
}
