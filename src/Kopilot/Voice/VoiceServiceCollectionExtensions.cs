using Kopilot.Speech;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kopilot.Voice;

public static class VoiceServiceCollectionExtensions
{
    /// <summary>
    /// Sprachschleife (nur HTTP-Modus mit Tray, nach der Sprachausgabe registrieren). Funksprüche gehen an das ATC-Modul
    /// (<see cref="Kopilot.Atc.AtcService"/> aus AddKopilotCore), dessen Antworten über <see cref="Kopilot.Atc.IAtcRadio"/> gesprochen werden.
    /// Ersetzt den Platzhalter <see cref="NoVoiceLoop"/>, damit kopilot_status den echten Zustand zeigt.
    /// </summary>
    public static IServiceCollection AddKopilotVoice(this IServiceCollection services)
    {
        services.TryAddSingleton<IVoiceOutput>(sp => sp.GetRequiredService<CalloutSpeaker>());
        services.TryAddSingleton<IAtcLink, AtcServiceLink>();
        services.AddSingleton<VoiceLoop>();
        services.Replace(ServiceDescriptor.Singleton<IVoiceStatusSource>(sp => sp.GetRequiredService<VoiceLoop>()));
        services.AddHostedService(sp => sp.GetRequiredService<VoiceLoop>());
        return services;
    }
}
