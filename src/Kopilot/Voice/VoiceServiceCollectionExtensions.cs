using Kopilot.Speech;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kopilot.Voice;

public static class VoiceServiceCollectionExtensions
{
    /// <summary>
    /// Sprachschleife (nur HTTP-Modus mit Tray, nach der Sprachausgabe registrieren).
    /// Ein ATC-Modul registriert sich vorher als <see cref="IAtcVoiceModule"/>; sonst gilt <see cref="NullAtcVoiceModule"/>.
    /// </summary>
    public static IServiceCollection AddKopilotVoice(this IServiceCollection services)
    {
        services.TryAddSingleton<IVoiceOutput>(sp => sp.GetRequiredService<CalloutSpeaker>());
        services.TryAddSingleton<IAtcVoiceModule, NullAtcVoiceModule>();
        services.AddSingleton<VoiceLoop>();
        services.AddSingleton<IAtcRadio>(sp => sp.GetRequiredService<VoiceLoop>());
        services.AddHostedService(sp => sp.GetRequiredService<VoiceLoop>());
        return services;
    }
}
