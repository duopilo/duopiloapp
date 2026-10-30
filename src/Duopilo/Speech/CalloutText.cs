namespace Duopilo.Speech;

/// <summary>
/// Feste Callouts. Vor dem Sprechen bereitet <see cref="Pronunciation.ForSpeech"/> sie auf (ECAM-Titel wie "ENG 1 FIRE",
/// Zahlen, Kennungen) – dieselbe Aussprache wie für ATC, Fuel Check und die Sprachschleife.
/// </summary>
public static class CalloutText
{
    /// <summary>Feste Callouts der Monitoring-Regeln – werden nach dem Laden vorab synthetisiert, damit sie ohne Verzögerung kommen.</summary>
    public static readonly IReadOnlyList<string> Common =
    [
        "Gear speed", "Flap speed", "Overspeed", "Stall", "Sink rate", "Bank angle", "Master warning", "Master caution",
        "Gear still down", "Speed limit", "Speed", "Autopilot off", "One thousand to go", "Sim paused", "Callouts on",
        "Top of descent", "Landing checklist complete",

        // Pilot-Monitoring-Callouts (v0.6)
        "One hundred knots", "V1", "Rotate", "Positive climb", "Spoilers", "No spoilers", "Reverse green", "Decel", "Seventy knots",
        "Ten thousand", "Transition altitude", "Transition level", "Gear down", "Gear up", "Flaps zero", "Flaps one", "Flaps two", "Flaps three",
        "Speed checked, flaps one", "Speed checked, flaps two", "Speed checked, flaps three", "Speed checked, flaps full",

        // Sprachschleife (v0.9): Taxi-Speed-Warnung und kurze Antworten
        "Taxi speed", "Say again", "Unable", "Cancelled",
    ];

    /// <summary>Test-Ansage aus Dashboard und Tray.</summary>
    public const string TestPhrase = "Runway two three left assigned";
}
