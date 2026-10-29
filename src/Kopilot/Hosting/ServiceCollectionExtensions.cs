using Kopilot.Aircraft;
using Kopilot.Atc;
using Kopilot.Ecam;
using Kopilot.FlightPlan;
using Kopilot.Input;
using Kopilot.Monitoring;
using Kopilot.Sim;
using Kopilot.Traffic;
using Microsoft.Extensions.Options;

namespace Kopilot.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>Gemeinsame Dienste für stdio- und HTTP-Modus: Profile, SimConnect, ECAM, SimBrief, Monitoring und Eingabe.</summary>
    public static IServiceCollection AddKopilotCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KopilotOptions>(configuration.GetSection(KopilotOptions.Section));
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<SimConnectClient>();
        services.AddHostedService(sp => sp.GetRequiredService<SimConnectClient>());

        services.AddSingleton<EcamCatalog>();
        services.AddSingleton<SimBriefService>();
        // Verkehr (get_traffic, Hintergrundabfrage für TRAFFIC_PROXIMITY)
        services.AddSingleton<TrafficService>();
        services.AddHostedService(sp => sp.GetRequiredService<TrafficService>());

        services.AddSingleton(sp => new AlertBus(sp.GetRequiredService<IOptions<KopilotOptions>>().Value.Monitoring.HistorySize));
        services.AddSingleton<SnapshotFactory>();
        services.AddSingleton(new FmaLog());
        services.AddSingleton<MonitoringService>();
        services.AddHostedService(sp => sp.GetRequiredService<MonitoringService>());

        // Eingabe-Modul (standardmäßig gesperrt)
        services.AddSingleton<IInputBackend, SimInputBackend>();
        services.AddSingleton<InputService>();

        // ATC-Modul (Zustandsmaschine mit Vorlagen). Die Rollroute ist eine Schnittstelle – bis zur Routenberechnung ohne Route.
        services.AddSingleton<ITaxiRouteProvider, NoTaxiRouteProvider>();
        services.AddSingleton<AtcService>();
        services.AddHostedService(sp => sp.GetRequiredService<AtcService>());
        return services;
    }
}
