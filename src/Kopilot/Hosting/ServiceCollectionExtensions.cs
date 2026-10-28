using Kopilot.Aircraft;
using Kopilot.Sim;

namespace Kopilot.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>Gemeinsame Dienste für stdio- und HTTP-Modus: Profile und SimConnect-Verbindung.</summary>
    public static IServiceCollection AddKopilotCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KopilotOptions>(configuration.GetSection(KopilotOptions.Section));
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<SimConnectClient>();
        services.AddHostedService(sp => sp.GetRequiredService<SimConnectClient>());
        return services;
    }
}
