using Duopilo.Speech;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Duopilo.Voice;

public static class VoiceServiceCollectionExtensions
{
    /// <summary>
    /// Sprachschleife (nur HTTP-Modus mit Tray, nach der Sprachausgabe registrieren). Funksprüche gehen an das ATC-Modul
    /// (<see cref="Duopilo.Atc.AtcService"/> aus AddDuopiloCore), dessen Antworten über <see cref="Duopilo.Atc.IAtcRadio"/> gesprochen werden.
    /// Ersetzt den Platzhalter <see cref="NoVoiceLoop"/>, damit duopilo_status den echten Zustand zeigt.
    /// </summary>
    public static IServiceCollection AddDuopiloVoice(this IServiceCollection services)
    {
        services.TryAddSingleton<IVoiceOutput>(sp => sp.GetRequiredService<CalloutSpeaker>());
        services.TryAddSingleton<IAtcLink, AtcServiceLink>();
        services.AddSingleton<VoiceLoop>();
        services.Replace(ServiceDescriptor.Singleton<IVoiceStatusSource>(sp => sp.GetRequiredService<VoiceLoop>()));
        services.AddHostedService(sp => sp.GetRequiredService<VoiceLoop>());
        return services;
    }
}
