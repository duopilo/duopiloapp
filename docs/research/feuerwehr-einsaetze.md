# Planung: Feuerwehr-Einsätze

Stand: 02.11.2026. Status: Planung, noch kein Code. Die Fahrzeuge selbst (Modelle, Licht) stehen in
[Feuerwehr-Fahrzeuge](feuerwehr-fahrzeuge.md).

Fragen:

- Wann rückt die Flughafenfeuerwehr in Duopilo aus, und was macht sie dann?
- Woher weiß Duopilo, wo die Wache ist und wohin die Fahrzeuge fahren?
- Wo ergibt die Feuerwehr außer bei Not- und Sicherheitslandungen noch Sinn (z. B. beim Betanken)?

## Ergebnis

- **Drei Alarmstufen wie in echt:** *Local Standby* bei Pan Pan, *Full Emergency* bei Mayday, *Aircraft Ground Incident*
  bei einem Notfall am Boden. Die Stufe entscheidet, wie viele Fahrzeuge kommen.
- **Ablauf:** ausrücken, an der Bahn bereitstellen, nach dem Aufsetzen hinterherfahren, das Flugzeug umstellen und
  kontrollieren, dann zum Stand begleiten oder dort bleiben, zum Schluss einrücken.
- **Auslöser gibt es schon:** Das ATC-Modul kennt Mayday, Pan Pan und Squawk 7700, der ECAM-Leser meldet `ENG 1 FIRE`,
  `APU FIRE` oder `BRAKES HOT`, die Spritüberwachung erkennt das Betanken.
- **Außer Notlandungen sinnvoll:** heiße Bremsen, Startabbruch, Brand am Boden, Anforderung durch den Piloten. Beim
  Betanken mit Passagieren an Bord wird die Feuerwehr in echt meist nur informiert, eine Wache vor Ort ist nicht die Regel.
  Das kommt deshalb höchstens als Option.
- **Keine Wachen-Liste nötig:** Die Fahrzeuge spawnen auf einer freien Parkposition nahe der Bahnmitte, außer Sicht.
- **Erster Schritt:** Mayday und Pan Pan in EDDL mit Bereitstellung an der Bahn, sobald Follow-me Fahrzeuge spawnen und
  bewegen kann.

## Wie es in echt läuft

### Alarmstufen

| Stufe | Bedeutung | Typischer Auslöser | Feuerwehr |
| --- | --- | --- | --- |
| Local Standby | Defekt bekannt oder vermutet, die Landung sollte aber ohne ernste Schwierigkeiten gehen | Pan Pan | Bereitstellungsräume an der Bahn, nur Flughafen-Kräfte |
| Full Emergency | Flugzeug in der Luft in Schwierigkeiten, Unfallgefahr | Mayday | Bereitstellungsräume, dazu Kräfte von außerhalb |
| Aircraft Ground Incident | Notfall am Boden, kein Unfall | Brand, Rauch, heiße Bremsen | direkt zum Flugzeug |
| Aircraft Accident | Unfall auf oder nahe dem Platz | Absturz | direkt zur Unfallstelle |

- Die Zuordnung Pan Pan → Local Standby und Mayday → Full Emergency nennt die australische Flugsicherung so.
- *Aircraft Accident* lassen wir weg. MSFS-Abstürze sind meist ausgeschaltet, und ein Flug endet dort ohnehin.
- Daneben gibt es *Weather Standby* (Seitenwind, schlechte Sicht): Die Wache wird nur vorgewarnt, kein Fahrzeug fährt raus.
  Für Duopilo nicht sichtbar, entfällt.

### Eintreffzeit und Fahrzeugzahl

- Jedes Bahnende soll in höchstens 2 Minuten erreicht sein, jeder andere Teil der Bewegungsflächen in höchstens 3 Minuten.
- Die Feuerwehr-Kategorie eines Flugzeugs richtet sich nach Gesamtlänge und Rumpfbreite (ICAO, 10 Kategorien). A320-Familie
  und B737 sind Kategorie 7, A330, B777, B787 und A350 Kategorie 9, A380 und B747-8 Kategorie 10.
- Mindestens 1 Löschfahrzeug in den kleinen Kategorien, 2 in den mittleren, 3 ab Kategorie 8. Wo genau die Grenze zwischen
  1 und 2 liegt, geben Quellen unterschiedlich an. Für Duopilo reicht die Größenordnung.

### An der Bahn und am Flugzeug

- Die Fahrzeuge fahren zu festgelegten Bereitstellungsräumen je Landebahn.
- Bei Fahrwerks- oder Reifenproblemen stehen sie nahe dem Aufsetzpunkt und folgen dem Flugzeug nach dem Aufsetzen.
- Der Einsatzleiter meldet sich beim Tower und versucht, direkt mit dem Piloten zu sprechen. Ob evakuiert wird, entscheidet
  der Kommandant.
- An heißen Rädern nähert man sich nur von vorn oder hinten, nie seitlich in Achsrichtung. Schmelzsicherungen der Reifen
  lösen bei etwa 177 °C aus, im Extremfall 2 bis 30 Minuten nach dem letzten starken Bremsen. Normal heiße Bremsen kühlen
  von selbst ab.

### Betanken mit Passagieren an Bord

- **Österreich (ZFBO 2024, § 35):** Das Verfahren ist mit den lokalen Einsatzkräften abzusprechen, und sie sind zu
  informieren, dass Passagiere an Bord sind. Ausgänge bleiben offen, Fluchtwege frei. Eine Wache vor Ort verlangt der Text
  nicht.
- **Rom (ADR):** Die Feuerwehr wird in Echtzeit über das Betanken mit Passagieren informiert, muss aber nicht vor Ort sein.
  Läuft Sprit über eine Fläche von mehr als etwa 80 m² aus, wird Alarm ausgelöst und die Feuerwehr rückt aus.
- **Hannover:** Bei ausgelaufenem Kraftstoff ist die Flughafenfeuerwehr sofort zu verständigen.
- Für Deutschland allgemein haben wir keine Pflicht zur Wache gefunden. Einzelne Flughäfen oder Länder können strenger sein.

## Auslöser in Duopilo

| Auslöser | Woher | Stufe | Stand |
| --- | --- | --- | --- |
| Pan Pan | `AtcSession.Emergency` (Kind `PanPan`) | Local Standby | vorhanden |
| Mayday, Squawk 7700 bestätigt | `AtcSession.Emergency` (Kind `Mayday`) | Full Emergency | vorhanden |
| „cancel Mayday“ / „cancel Pan Pan“ | `AtcEmergencyCall.Cancel` | Einsatz abbrechen | vorhanden |
| Squawk 7700 ohne ATC-Modul | Transponder-Code | Full Emergency (Option) | neu |
| `ENG x FIRE`, `APU FIRE` am Boden | ECAM-Leser | Aircraft Ground Incident | Leser vorhanden |
| `BRAKES HOT` nach Landung oder Startabbruch | ECAM-Leser, sonst Flugzeugprofil | Local Standby am Boden | Leser vorhanden |
| Startabbruch über etwa 100 kt | Flugzustand (Startschub, dann Bremsen auf der Bahn) | Local Standby am Boden | neu |
| Pilot fordert an | ATC-Phrasen „request fire services“, „request brake check“ | Local Standby | neu |
| Betanken am Boden | Spritüberwachung (`FuelCheckKind.Refuel`) | Wache (Option) | Erkennung vorhanden |
| Test | Dashboard-Knopf und MCP-Werkzeug | frei wählbar | neu |

Beim A320 von FlyByWire und Fenix kommen die ECAM-Meldungen über den vorhandenen Leser. Für andere Flugzeuge braucht es
eine Bremstemperatur oder Brandwarnung im Flugzeugprofil, sonst bleiben nur die ATC-Auslöser.

## Ablauf eines Einsatzes

1. **Alarm:** Stufe, Feuerwehr-Kategorie und Landebahn bestimmen. Die Bahn kommt aus der ATC-Freigabe oder dem Flugplan.
2. **Ausrücken:** Fahrzeuge an der Wache spawnen, Blaulicht und Fahrlicht an. Liegt die Wache im Sichtbereich, stehen sie
   schon vorher dort (ruhige Variante) und fahren los.
3. **Bereitstellung:** an Rollwegen neben der Bahn, nie auf der Bahn, hinter der Haltelinie. Das erste Löschfahrzeug etwa
   dort, wo das Flugzeug voraussichtlich stehen bleibt, die anderen näher am Aufsetzpunkt. Der Abstand richtet sich nach
   der Landestrecke des Typs.
4. **Landung:** Hat das Flugzeug ein Fahrzeug passiert und ist langsamer als etwa 40 kt, fährt dieses Fahrzeug auf die Bahn
   und folgt.
5. **Am Flugzeug:** Die Löschfahrzeuge stellen sich vor und hinter das Hauptfahrwerk (nie seitlich in Achsrichtung), der
   Einsatzleitwagen schräg vor die Nase. Nach einer kurzen Sichtkontrolle meldet die Feuerwehr das Ergebnis, vorerst über
   ATC-Vorlagen auf Englisch.
6. **Weiter:** Ist alles in Ordnung, rollt das Flugzeug zum Stand, ein Löschfahrzeug fährt hinterher (bei Follow-me dahinter).
   Sonst bleibt es auf der Bahn, und die Bahn bleibt gesperrt.
7. **Einrücken:** zurück zur Wache, außerhalb des Sichtbereichs entfernen.

Bei einem Notfall am Boden (Brand am Gate, heiße Bremsen am Stand) fallen die Schritte 3 und 4 weg.

### Fahrzeuge je Stufe

Die Kategorie kommt aus einer Tabelle ICAO-Typ → Feuerwehr-Kategorie. Ohne Eintrag dient die Wirbelschleppen-Klasse als
Ersatz: Light bis Kategorie 5, Medium Kategorie 7, Heavy Kategorie 9, Super Kategorie 10.

| Kategorie | Local Standby | Full Emergency |
| --- | --- | --- |
| bis 5 (GA, kleine Turboprops) | 1 FLF | 1 FLF, ELW, RTW |
| 6–7 (A320, B737, CRJ) | 2 FLF, ELW | 2 FLF, ELW, HLF, Rettungstreppe, RTW |
| 8–10 (A321XLR, B757, Großraum) | 3 FLF, ELW | 3 FLF, ELW, HLF, 2 Rettungstreppen, RTW |

FLF im Panther- und Z8-Stil zufällig gemischt. Heiße Bremsen und Startabbruch: 1 FLF, beim Startabbruch dazu der ELW.

## Wache und Wege

- **Wache:** Die MSFS-Daten kennen keine Feuerwachen, und eine Liste für jeden Flughafen ist nicht zu pflegen. Deshalb
  spawnen die Fahrzeuge standardmäßig auf einer **freien Parkposition** und fahren von dort los:
  - bevorzugt eine Fahrzeug-Parkposition (Typ 13 `VEHICLE`), sonst Vorfeld- oder Fracht-Positionen, Gates mit Fluggastbrücke
    zuletzt;
  - möglichst nah an der Bahnmitte, damit jedes Bahnende schnell erreichbar ist (echte Wachen liegen aus demselben Grund
    oft mittig);
  - frei heißt: kein eigenes KI-Flugzeug und kein Sim-Verkehr darauf; während des Einsatzes vergibt der eigene Verkehr die
    Position nicht;
  - außer Sicht des eigenen Flugzeugs, sonst die nächstbeste. Man sieht die Fahrzeuge erst auf den Rollwegen.
  - Vorteil: Parkpositionen sind schon Knoten im Rollweg-Netz, die Route zur Bahn gibt es damit ohne Fahrzeugwege.
- **Später:** echte Wachen aus OpenStreetMap (`amenity=fire_station`, beim ersten Besuch abfragen und lokal speichern,
  Hinweis „© OpenStreetMap-Mitwirkende“) und eigene Einträge für einzelne Plätze. Beides nur, wenn die freie Parkposition
  sichtbar stört.
- **Wege:** Für Wachen abseits des Rollweg-Netzes kämen die Fahrzeugwege (Typ 6 `VEHICLE`) und Straßen (Typ 7 `ROAD`) aus
  den MSFS-Daten dazu, die das Rollweg-Netz bisher nicht liest.
- **Bewegen:** wie beim Follow-me (`AICreateSimulatedObject`, Kinematik wie beim Rollen). Im Einsatz deutlich schneller als
  Flugzeuge auf dem Rollweg, die Werte legen wir beim Test fest.

## Verkehr und ATC während des Einsatzes

- Ab dem Alarm hält Duopilo die Landebahn für den eigenen Anflug frei: eigene KI-Landungen ins Holding oder Durchstarten,
  Abflüge warten am Haltepunkt.
- Nach der eigenen Landung bleibt die Bahn gesperrt, bis die Feuerwehr sie räumt. Dafür gibt es schon die Bahnbelegung und
  das Durchstarten im Verkehrsmodul.
- Rollen die Löschfahrzeuge über eine andere Bahn, gilt sie für die Dauer als belegt.

## Weitere Einsätze

| Einsatz | Nutzen | Bewertung |
| --- | --- | --- |
| Heiße Bremsen nach der Landung | häufig, gut erkennbar (`BRAKES HOT`) | **ja**, gleich nach Mayday/Pan Pan |
| Startabbruch bei hoher Geschwindigkeit | echtes Verfahren, gut erkennbar | **ja** |
| Brand am Boden (Triebwerk, APU) | selten, aber eindeutig | **ja** |
| Pilot fordert an | Übergewichtslandung, Vogelschlag, Reifenschaden | **ja**, über ATC-Phrasen |
| Betanken mit Passagieren an Bord | in echt meist nur Meldung, keine Wache | **Option**, Standard aus: 1 FLF steht auf dem Vorfeld in Sichtweite, solange getankt wird |
| Beladen ohne Betanken | kein Feuerwehrthema | nein |
| Treibstoffaustritt | im Sim nicht erkennbar | nein |
| Notfälle der eigenen KI | belebt den Flughafen: ab und zu ein KI-Flugzeug mit Pan Pan, die Feuerwehr rückt aus, wenn man gerade rollt | später, selten |
| Wasserfontäne (Water Salute) | Erstflug, letzter Flug, auf Wunsch im Dashboard; zwei FLF bilden einen Bogen über dem Rollweg | später, braucht einen Wasser-Effekt im Modell |
| Kontrollfahrten, Übungen | Fahrzeuge unterwegs ohne Notfall | später, niedrig |

Passagiere an Bord erkennt Duopilo nicht. Die Betankungswache hinge deshalb nur am Betanken am Boden mit stehenden
Triebwerken.

## Reihenfolge (Vorschlag)

1. Follow-me fertig: Fahrzeuge spawnen, bewegen, Licht schalten.
2. Spawn auf freier Parkposition und Mayday/Pan Pan bis zur Bereitstellung an der Bahn, Test in EDDL.
3. Hinterherfahren, Umstellen, Rückmeldung, Begleitung zum Stand.
4. Bahnsperre für den eigenen Verkehr.
5. Heiße Bremsen, Startabbruch, Brand am Boden, Anforderung durch den Piloten.
6. Optional: Betankungswache, KI-Notfälle, Wasserfontäne.

## Offen

- Wachen aus OpenStreetMap: Wie gut sind Flughafenwachen dort erfasst? Erst prüfen, wenn die freie Parkposition stört.
- Funk zwischen Feuerwehr und Pilot: eigene Frequenz und Stimme oder vorerst über den Tower?
- Betankungswache: überhaupt, und wenn ja, Standard an oder aus?
- Rettungstreppe an die Tür und Evakuierung: erst nach dem Rest.
- Woher Bremstemperatur und Brandwarnung bei Flugzeugen ohne ECAM-Leser kommen.

## Quellen

- [Airport crash tender (Wikipedia)](https://en.wikipedia.org/wiki/Airport_crash_tender): Eintreffzeit 2 und 3 Minuten,
  Kategorien von A320 und B777
- [ARFF categories, ICAO Annex 14 (Aviation Souk)](https://aviationsouk.com/knowledge/arff-categories-icao-annex-14-airport-fire-cover/):
  Kategorien-Tabelle und Fahrzeugzahl
- [Airservices Australia: What happens when I declare an emergency](https://www.airservicesaustralia.com/wp-content/uploads/Safety-Bulletin_What-happens-when-I-declare-an-emergency.pdf):
  Pan Pan → Local Standby, Mayday → Full Emergency
- [Jersey Airport: Emergency Orders](https://cdn.ports.je/web/Emergency%20Orders%20Issue%203.pdf): Alarmstufen und
  Bereitstellungsräume
- [CAA Nepal: SOPs for RFF Service 2019](https://dspace.caanepal.gov.np/bitstream/123456789/426/1/SOPs%20for%20RFF%20service%2c%202019.pdf):
  Folgen nach dem Aufsetzen, Kontakt zum Piloten, Evakuierung
- [Hot brakes and wheel fires (Code 7700)](https://code7700.com/hot_brakes_wheel_fires.htm): Annäherung an Räder,
  Schmelzsicherungen
- [ZFBO 2024 § 35 (Österreich)](https://www.jusline.at/gesetz/zfbo_2024/paragraf/35.pdf): Betanken mit Passagieren an Bord
- [ADR Rom: Aircraft Refuelling Procedure](https://www.adr.it/documents/d/aeroporti-di-roma-en/e15-mov18-aircraft-refuelling-procedure-rev-3_courtesy-english-translation):
  Meldung an die Feuerwehr, Schwelle für Treibstoffaustritt
- [Flughafen Hannover: Flughafenbenutzungsordnung](https://www.hannover-airport.de/fileadmin/downloads/B2B_und_Gesch%C3%A4ftskunden/Entgelte_und_AGB/AGB_und_Entgelttabellen/Flughafenbenutzungsordnung_Dez2024.pdf):
  Kraftstoffaustritt
- [SimConnect_AddToFacilityDefinition (MSFS 2024 SDK)](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Facilities/SimConnect_AddToFacilityDefinition.htm):
  Arten von Wegen und Parkpositionen
