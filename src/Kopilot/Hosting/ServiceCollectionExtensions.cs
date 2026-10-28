using Kopilot.Aircraft;
using Kopilot.Ecam;
using Kopilot.FlightPlan;
using Kopilot.Input;
using Kopilot.Monitoring;
using Kopilot.Sim;
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
        services.AddSingleton(sp => new AlertBus(sp.GetRequiredService<IOptions<KopilotOptions>>().Value.Monitoring.HistorySize));
        services.AddSingleton<SnapshotFactory>();
        services.AddSingleton(new FmaLog());
        services.AddSingleton<MonitoringService>();
        services.AddHostedService(sp => sp.GetRequiredService<MonitoringService>());

        // Eingabe-Modul (standardmäßig gesperrt)
        services.AddSingleton<IInputBackend, SimInputBackend>();
        services.AddSingleton<InputService>();
        return services;
    }
}
