using Kopilot.Aircraft;
using Kopilot.Ecam;
using Kopilot.FlightPlan;
using Kopilot.Monitoring;
using Kopilot.Sim;
using Kopilot.Speech;
using Microsoft.Extensions.Options;

namespace Kopilot.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>Gemeinsame Dienste für stdio- und HTTP-Modus: Profile, SimConnect, ECAM, SimBrief und Monitoring.</summary>
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
        services.AddSingleton<MonitoringService>();
        services.AddHostedService(sp => sp.GetRequiredService<MonitoringService>());
        return services;
    }

    /// <summary>
    /// Eigene Sprachausgabe der Callouts. Nur im HTTP-Modus: Die stdio-Instanz von Claude Desktop läuft parallel
    /// und würde sonst jeden Callout ein zweites Mal sprechen.
    /// </summary>
    public static IServiceCollection AddKopilotSpeech(this IServiceCollection services)
    {
        services.AddSingleton<ISpeechEngine, KokoroSpeechEngine>();
        services.AddSingleton<IAudioOutput, WaveOutAudio>();
        services.AddSingleton(sp => new SpeechSettingsStore(
            Path.Combine(sp.GetRequiredService<IOptions<KopilotOptions>>().Value.ResolveDataDirectory(), "speech.json")));
        services.AddSingleton<CalloutSpeaker>();
        services.AddHostedService(sp => sp.GetRequiredService<CalloutSpeaker>());
        return services;
    }
}
