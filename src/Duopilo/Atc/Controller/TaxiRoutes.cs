namespace Duopilo.Atc;

/// <summary>Wofür eine Rollroute gebraucht wird.</summary>
/// <param name="AirportIcao">Flughafen, an dem gerollt wird.</param>
/// <param name="RunwayIdent">Startbahn (Abflug) bzw. Landebahn (Ankunft), z. B. "25C".</param>
/// <param name="Lat">Position des Flugzeugs.</param>
/// <param name="HeadingDeg">Missweisender Kurs des Flugzeugs.</param>
/// <param name="Gate">Gewünschtes Gate/Parkposition bei der Ankunft, falls bekannt.</param>
public sealed record TaxiRequest(
    string AirportIcao,
    string? RunwayIdent,
    double? Lat,
    double? Lon,
    double? HeadingDeg,
    string? Gate);

/// <summary>Ergebnis der Rollroutenberechnung.</summary>
/// <param name="Taxiways">Rollwege in Reihenfolge, z. B. ["N", "N3"] → {taxiRoute} "november, november three".</param>
/// <param name="HoldingPoint">Rollhalt vor der Bahn ({holdingPoint}), z. B. "S".</param>
/// <param name="Gate">Ziel-Gate bei der Ankunft ({gate}).</param>
/// <param name="PushbackFacing">Ausrichtung nach dem Pushback ({direction}), z. B. "east".</param>
public sealed record TaxiInstruction(
    IReadOnlyList<string> Taxiways,
    string? HoldingPoint = null,
    string? Gate = null,
    string? PushbackFacing = null);

/// <summary>
/// Schnittstelle für die Rollroute ({taxiRoute}, {holdingPoint}, {gate}, {direction}). Die eigentliche Routenberechnung
/// (Taxiway-Graph aus der Facilities-API) kommt in einem eigenen Schritt. Liefert die Schnittstelle nichts, nehmen die
/// Vorlagen eine Variante ohne diese Angaben.
/// </summary>
public interface ITaxiRouteProvider
{
    /// <summary>Route vom Stand zum Rollhalt der Startbahn.</summary>
    TaxiInstruction? Departure(TaxiRequest request);

    /// <summary>Route von der Landebahn zum Gate.</summary>
    TaxiInstruction? Arrival(TaxiRequest request);
}

/// <summary>Platzhalter, bis die Routenberechnung existiert: keine Route.</summary>
public sealed class NoTaxiRouteProvider : ITaxiRouteProvider
{
    public TaxiInstruction? Departure(TaxiRequest request) => null;

    public TaxiInstruction? Arrival(TaxiRequest request) => null;
}
