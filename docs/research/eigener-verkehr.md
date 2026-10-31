# Recherche: Eigener KI-Verkehr statt Sim-Verkehr

Stand: 31.10.2026. Anlass: Mit stummem Sim-ATC (siehe [Sim-ATC](sim-atc.md)) fliegt der Live-Traffic des Sims weiter nach
seinen eigenen Daten. Kommt er dem eigenen Anflug zu nahe, kann Duopilo-ATC ihn weder staffeln noch wegschicken.

Fragen:

- Kann Duopilo störenden Sim-Verkehr löschen oder ausweichen lassen?
- Kann Duopilo eigenen Verkehr erzeugen, und woher kämen Flugpläne dafür?
- Wie machen es FSLTL, SayIntentions und BeyondATC?

## Ergebnis

- **Sim-Verkehr löschen geht nicht.** SimConnect entfernt nur Objekte, die der eigene Client erzeugt hat.
- **Eigene Flugzeuge erzeugen geht.** Sie gehören dann Duopilo: löschen, lenken, und das ATC kennt jedes einzelne.
- **Live-Positionen (ADS-B) nachzubilden löst das Anflugproblem nicht.** Echter Verkehr ist schon eng gestaffelt und folgt
  einem anderen ATC. SayIntentions verwirft ADS-B genau aus diesen Gründen.
- **Vorschlag:** Verkehr nach Fahrplan. Die Route rechnet Duopilo zur Laufzeit aus den Navdaten des Sims, den Anflug staffelt
  Duopilo-ATC.
- **Erster Schritt:** eine kurze Testlinie, um zu prüfen, ob ein NonATC-Flugzeug zuverlässig Wegpunkten folgt, landet und rollt.

## Sim-Verkehr: lesen ja, löschen nein

- `SimConnect_AIRemoveObject` entfernt laut SDK nur KI-Objekte, die der Client selbst erzeugt hat, nicht die anderer Clients
  oder des Sims.
- Beim Live-Traffic bricht der Aufruf mit Ausnahme 25 (`SIMCONNECT_EXCEPTION_ILLEGAL_OPERATION`) ab (DevSupport-Meldung).
- **AIFlow und AIGround** (kostenlos, von SayIntentions gepflegt, MSFS 2020 und 2024) beeinflussen den Sim-Verkehr trotzdem
  über SimConnect: Abstand zum eigenen Flugzeug und zwischen anfliegender KI, erst über die Geschwindigkeit, dann mit Holdings.
  AIGround kann festgefahrene KI per eigenem Squawk-Code „zappen“. Wie das intern geht, ist nicht dokumentiert. Über
  `AIRemoveObject` kann es nicht laufen.
- Für den Anflug ist AIFlow die Lösung ohne eigenen Code. Sie lässt den Live-Traffic aber unter Sim-Kontrolle.

## Eigene Flugzeuge erzeugen

In MSFS 2024 gibt es jede Funktion als `_EX1` mit zusätzlichem Livery-Parameter. Welche Modelle und Liveries installiert sind,
liefert `SimConnect_EnumerateSimObjectsAndLiveries`.

| Funktion | Was sie tut | Für Duopilo |
|---|---|---|
| `AICreateNonATCAircraft_EX1` | am Boden oder in der Luft an beliebiger Position; gesteuert über Wegpunkte | **der Weg**: Duopilo lenkt selbst |
| `AICreateEnrouteATCAircraft_EX1` | fliegt eine .pln-Datei mit der Sim-KI ab, auch ab einer Stelle mitten im Plan | fliegt unter Sim-ATC, nicht lenkbar |
| `AICreateParkedATCAircraft_EX1` | steht am Gate, `AISetAircraftFlightPlan` schickt es los; ohne freien Parkplatz kein Flugzeug | fliegt unter Sim-ATC |
| `AICreateSimulatedObject_EX1` | statische Objekte, Fahrzeuge | z. B. geparkte Flugzeuge |

Bekanntes Problem in MSFS 2024: Mit `AICreateEnrouteATCAircraft` erzeugte Flugzeuge schalten im Anflug oft nicht auf
`STATE_LANDING`, machen einen Tiefflug über den Platz oder fliegen tief weiter. In MSFS 2020 trat das nicht auf. Eine
Antwort von Asobo steht im Thread nicht.

### Steuerung über Wegpunkte (NonATC)

- Die Wegpunktliste (`SIMCONNECT_DATA_WAYPOINT`: Lat, Lon, Alt in ft, Flags, Speed in kt, Schub in %) geht per
  `SetDataOnSimObject` an das Objekt, ohne Längenbegrenzung.
- Pitch, Bank und Heading steuert der KI-Pilot selbst, der Client kann sie nicht setzen. Bei hoher Geschwindigkeit dreht die
  KI früher vor dem Wegpunkt ein.
- Flags: `SPEED_REQUESTED`, `THROTTLE_REQUESTED`, `COMPUTE_VERTICAL_SPEED`, `ALTITUDE_IS_AGL`, `ON_GROUND` (Pflicht für
  Rollwege), `REVERSE` (nur erster Wegpunkt), `WRAP_TO_FIRST` (nur letzter Wegpunkt).

### Erfahrungen anderer (mrlm-net/simconnect, Issue #289)

Ein Versuch mit dem FSLTL-A320 in LKPR, Bahn 24, MSFS 2024:

- **Landung über Wegpunkte klappt nur mit ausgefahrenem Fahrwerk ab dem Spawn.** Ohne schwebt das Flugzeug in 12 ft bei
  132 kt über die Bahn hinaus; der Autor vermutet einen bekannten KI-Bug. Mit Fahrwerk unten: Aufsetzen 645 m hinter der
  Schwelle auf der Mittellinie bei 129 kt, dann Abbremsen auf 37 kt in etwa 800 m und Rollen weiter unter Wegpunktkontrolle.
- **Geschwindigkeit:** In der Luft ignorierte die KI den Speed-Wunsch (164 kt statt 130–140 kt). Ungetestet: `THROTTLE_REQUESTED`
  oder früher verlangsamen.
- **Am Boden:** Die KI blieb 46 m vor dem letzten Rollwegpunkt stehen, daher einen Wegpunkt hinter die Haltelinie setzen.
- **Sim-ATC-Flugzeug übernehmen** (`AIReleaseControl`): Beim Ausrollen holte sich der Sim das Flugzeug nach etwa 8 s zurück.
  Erst nach Verlassen der Bahn übernehmen.
- **Rückfallebene:** Flugzeug einfrieren und die Position jeden Frame per `SetDataOnSimObject` setzen, so wie es vPilot und
  swift laut Autor machen.
- Einschränkung: ein Flughafen, ein Modell, eine Sim-Version.

## Datenquellen für echten Verkehr

ADS-B überträgt Callsign, Position, Höhe, Geschwindigkeit, Kurs und Squawk, aber weder Abflug noch Ziel noch Route.

| Quelle | Was | Bedingungen |
|---|---|---|
| adsb.lol | Live-Positionen, Verlaufsdaten | kostenlos, Daten unter ODbL; künftig API-Schlüssel über eigenes Einspeisen; Produktivnutzung vorher melden |
| OpenSky | Live-Positionen | nur nicht-kommerziell; 400 Credits/Tag anonym, 4.000 mit Konto |
| Flightradar24 | Live-Positionen | kostenloser Zugang (von FSLTL genutzt) seit 30.04.2026 abgeschaltet; jetzt über Navigraph Unlimited |
| adsbdb.com | Abflug und Ziel zum Callsign | nur plausibel; bei Linienflügen meist richtig, bei Charter und GA oft nicht |
| FlightAware AeroAPI | u. a. Flugplandaten (Nordamerika, Europa, Australien, Neuseeland) | Personal-Stufe ohne Mindestgebühr, 5 $ Freiguthaben/Monat, nur privat oder akademisch |
| Eurocontrol NM B2B | eingereichte Flugpläne Europa | nur für Flugsicherungen, Airlines, Flughäfen, Flugplanungsdienste; nicht erreichbar |
| FAA SWIM | Flugpläne und Änderungen | FAA-Konto und Nutzungsvereinbarung; nur US-Luftraum |

Auch der echte Flugplan endet praktisch vor der Landung: Bahn, Approach und Vektoren vergibt der Lotse, bei uns also
Duopilo-ATC.

## Wie andere es machen

- **FSLTL:** holte alle 20 Sekunden Live-Daten von Flightradar24. Seit Version 1.9.0 nur noch Fahrplanverkehr, Live-Daten
  wieder mit Navigraph Unlimited.
- **SayIntentions (AI Living World):** Verkehr nach Saisonflugplänen mit echten Gates und Routen (SIDs, STARs, Airways). Nicht
  jeder echte Flug wird eingespeist, damit Platz für den Piloten bleibt. Jedes Flugzeug ist eine eigene KI, die das eigene ATC
  einzeln lenkt. Staffelung, Routen und Stimmen rechnet die Cloud. Empfohlene Modelle: FSLTL-Basispaket, MSFS-2024-Standard
  geht auch. Sim-Verkehr muss komplett aus.
- **SayIntentions zu ADS-B** (FAQ): (1) für die meisten Weltregionen keine Daten, (2) echter Verkehr ist schon eng gestaffelt,
  (3) das eigene ATC kann keinen Verkehr staffeln, den ein anderes ATC lenkt, (4) Wetteränderungen und (5) Änderungen der
  Sim-Zeit sollen den Verkehr verschieben.
- **BeyondATC:** fester historischer FR24-Datensatz mit über einer Million Flügen.
- **AIG:** kostenlos, Fahrplanverkehr.

## Vorschlag für Duopilo

**Fahrplan statt Route.** Gespeichert werden nur Callsign, Airline, Typ, Abflug, Ziel, Uhrzeit und Wochentage. Weltweit sind
das grob 100.000 Linienflüge am Tag, als Wochenfahrplan einige MB.

**Route zur Laufzeit**, nur für Flüge in der Nähe des Piloten:

- SID, STAR, Approach, Rollwege und Parkpositionen liefert die Facilities-API von SimConnect (Duopilo nutzt sie schon für
  Flughafen-Frequenzen, `SimConnectClient.Facilities.cs`).
- Im Reiseflug reicht eine plausible Strecke.
- Bahn und Reihenfolge im Anflug vergibt Duopilo-ATC, für den Piloten und den Verkehr nach denselben Regeln.

**Nur der Ausschnitt um den Piloten.** Der Verkehr läuft auf zwei Ebenen:

- **Rechnerisch:** Der Fahrplan läuft als reine Daten für einen größeren Umkreis, etwa alles, was in der nächsten Stunde am
  eigenen Abflug- oder Zielflughafen startet oder landet. Das kostet fast nichts. So kann Duopilo-ATC den Anflug staffeln,
  bevor der Pilot dort ist, und auf der Frequenz sprechen auch Flugzeuge, die er noch nicht sieht.
- **Im Sim:** Ein Objekt entsteht erst, wenn ein Flugzeug in einen Radius kommt (etwa TCAS-Reichweite), am Rand und an seiner
  errechneten Position. Verlässt es den Radius, wird es entfernt. Geparkte Flugzeuge entstehen erst bei Annäherung an den
  Flughafen. Außerhalb des Bereichs um den Spieler lehnt der Sim neue KI-Objekte ohnehin ab.

Vorrang haben, was der Pilot sieht und hört (Entscheidung 31.10.2026):

1. Verkehr um das eigene Flugzeug, im Reiseflug wie im Anflug.
2. Flüge, die kurz vor und nach dem Piloten landen oder starten.
3. Abflüge, Landungen und Rollverkehr am Start- und Zielflughafen.
4. Geparkte Flugzeuge an den Gates.

Ziel ist ein Flughafen, der lebt, statt ein paar abgestellter Flugzeuge: Beim Rollen zur Bahn startet gerade jemand, beim
Abrollen zum Gate landet eine Maschine oder eine andere hebt ab. Das soll natürlich vorkommen können, nicht bei jedem Flug
gleich ablaufen.

Ein Flug nach New York existiert in Frankfurt also höchstens als Zeile im Fahrplan, nie als Flugzeug.

**Woher der Fahrplan kommt:**

1. Aus den adsb.lol-Verlaufsdaten abgeleitet: wer wann von wo nach wo, als Wochenmuster pro Saison, z. B. per GitHub Action.
   ODbL verlangt Namensnennung, und der abgeleitete Fahrplan steht dann ebenfalls unter ODbL.
2. Von der Community gepflegt, wie die Flugzeugprofile, zuerst für einzelne Regionen.
3. Generiert nach Flughafengröße und ansässigen Airlines, als Rückfall für Regionen ohne Daten.

**Modelle:** FSLTL-Basispaket empfehlen, MSFS-2024-Modelle als Rückfall. Typ und Airline werden auf Titel und Livery abgebildet.

**Folgen:**

- Sim-Verkehr muss aus. Das widerspricht der Empfehlung in [Sim-ATC](sim-atc.md) („Verkehr an lassen“), die dann angepasst
  wird. `get_traffic` und `TRAFFIC_PROXIMITY` sehen den eigenen Verkehr weiter, weil `RequestDataOnSimObjectType` alle
  Flugzeuge liefert.
- **Fahrwerk:** Braucht die Landung ein früh ausgefahrenes Fahrwerk (siehe Issue #289), fährt Duopilo es früher aus als
  realistisch, notfalls schon beim Spawn außer Sicht. Aus dem Cockpit fällt das kaum auf. Die Doku erklärt, dass es technisch
  nicht anders geht (Entscheidung 31.10.2026).
- Offen: Lizenz der Fahrplandaten, Leistung bei vielen Flugzeugen, Verhalten der KI in der Luft (Speed).

## Erster Schritt: Testlinie

Ziel ist nur die Mechanik: Folgt ein NonATC-Flugzeug zuverlässig Wegpunkten, landet es und rollt es? Die Strecke ist bewusst
kurz und nicht ganz realistisch.

**Testwerkzeug:** `traffic_test_start`, `traffic_test_status`, `traffic_test_stop` und `traffic_test_models` (Stufe A seit
31.10.2026, Beschreibung im README unter „Traffic test“). Standardmäßig aus (`Duopilo:TrafficTest:Enabled`), weil es Objekte
im Sim erzeugt. Erzeugte Objekte entfernt es beim Stoppen, nach `MaxMinutes` und beim Beenden mit `AIRemoveObject`.

**Ablauf:**

0. **Modelle:** `EnumerateSimObjectsAndLiveries` listet die installierten Modelle. Getestet wird mit dem FSLTL-A320 und
   einem MSFS-2024-Standardmodell.
1. **Stufe A – Endanflug:**
   - **A1** (wie Issue #289): Spawn 5 NM vor einer Bahn am Flughafen des Piloten (z. B. EDDL 23L), Fahrwerk unten,
     Wegpunkte auf dem 3°-Gleitpfad, Aufsetzpunkt rund 300 m hinter der Schwelle, Ausroll-Wegpunkte mit `ON_GROUND` und
     fallender Geschwindigkeit bis zum Halt auf der Bahn, ein Wegpunkt 60 m hinter dem Haltepunkt. Abrollen über Rollwege
     kommt erst mit Stufe C.
   - **A2 – Fahrwerk erst im Flug:** Spawn 15 NM vor der Bahn mit Fahrwerk oben, Fahrwerk bei 10 NM ausfahren (`GEAR_DOWN`
     an das KI-Objekt), Rest wie A1. Klärt, ob Ankünfte einen normalen Flug hinter sich haben können.
2. **Stufe B – Platzrunde:** Spawn auf der Bahn, Start über `ON_GROUND`-Wegpunkte, Steigflug auf 1.500 ft AGL, Gegenanflug,
   Queranflug, Landung wie in Stufe A. Dabei beobachten: Fährt die KI das Fahrwerk nach dem Start selbst ein, und klappt das
   Ausfahren im Gegenanflug?
3. **Stufe C – Testlinie DUO01 EDDL–EDDK:** Pushback (`REVERSE` am ersten Wegpunkt), Rollen über die Rollwege aus der
   Facilities-API, Start, direkt nach EDDK in 5.000 ft, Anflug, Landung, Rollen zur Parkposition, dann `AIRemoveObject`.

**Messung:**

- Log mit 1 Hz unter `%LOCALAPPDATA%\Duopilo\traffic-tests\`: Position, Höhe, IAS und GS in kt, Vertikalgeschwindigkeit,
  am Boden ja/nein, KI-Zustand.
- Auswertung je Stufe: Aufsetzpunkt und -geschwindigkeit, Abweichung von der Mittellinie, Speed in der Luft gegenüber dem
  Wunsch, Halt vor dem letzten Wegpunkt.
- Sichtprüfung durch den Piloten im Sim, Ergebnisse als Testlog.

**Entscheidung danach:**

- Klappt Stufe C: Fahrplan-Verkehr mit Wegpunkten weiterverfolgen.
- Klappt nur die Luft, nicht Landung oder Rollen: die Rückfallebene testen (einfrieren und Position jeden Frame setzen).
- Klappt beides nicht: Live-Traffic lassen und AIFlow empfehlen.

## Quellen

- [MSFS 2024 SDK – SimConnect_AIRemoveObject](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/ai-object/simconnect_airemoveobject/index.md)
- [MSFS DevSupport – Remove default Live/Online AI via SimConnect](https://devsupport.flightsimulator.com/t/remove-default-live-online-ai-via-simconnect/3619)
- [MSFS 2024 SDK – SimConnect_AICreateNonATCAircraft_EX1](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/ai-object/simconnect_aicreatenonatcaircraft_ex1/)
- [MSFS 2024 SDK – SimConnect_AICreateEnrouteATCAircraft_EX1](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/ai-object/simconnect_aicreateenrouteatcaircraft_ex1/)
- [MSFS 2024 SDK – SimConnect_AICreateParkedATCAircraft_EX1](https://docs.flightsimulator.com/msfs2024/flighting/programming-apis/simconnect/api-reference/ai-object/simconnect_aicreateparkedatcaircraft_ex1/)
- [MSFS 2024 SDK – SIMCONNECT_DATA_WAYPOINT](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/structures-and-enumerations/simconnect_data_waypoint/index.md)
- [MSFS 2024 SDK – SIMCONNECT_WAYPOINT_FLAGS](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Structures_And_Enumerations/SIMCONNECT_WAYPOINT_FLAGS.htm)
- [MSFS SDK – SIMCONNECT_FACILITY_DATA_TYPE](https://docs.flightsimulator.com/html/Programming_Tools/SimConnect/API_Reference/Structures_And_Enumerations/SIMCONNECT_FACILITY_DATA_TYPE.htm)
- [MSFS DevSupport – SimConnect Injections in FS2024 (_EX1, Liveries)](https://devsupport.flightsimulator.com/t/simconnect-injections-in-fs2024/11769)
- [MSFS DevSupport – AI injected aircraft do not switch to STATE_LANDING on approach](https://devsupport.flightsimulator.com/t/ai-injected-aircraft-do-not-switch-to-state-landing-on-approach/12124)
- [mrlm-net/simconnect – Issue #289: how AI aircraft land](https://github.com/mrlm-net/simconnect/issues/289)
- [AIFlow and AIGround (flightsim.to)](https://de.flightsim.to/file/86782/aiflow-and-aiground)
- [AIFlow (flightsim.to)](https://flightsim.to/addon/42731/aiflow)
- [AIGround (flightsim.to)](https://flightsim.to/file/42732/aiground)
- [SayIntentions – Traffic Injection FAQ](https://sayintentionsai.freshdesk.com/support/solutions/articles/154000221890-traffic-injection-and-sequencing-faq)
- [SayIntentions – AI Living World](https://sayintentions.ai/ai-living-world)
- [msfsaddons – FSLTL lost its live traffic](https://msfsaddons.com/?p=45410)
- [msfsaddons – Traffic-Optionen 2026](https://msfsaddons.com/?p=52592)
- [msfsaddons – Navigraph und Flightradar24](https://msfsaddons.com/2026/05/26/navigraph-and-flightradar24-team-up-to-bring-live-traffic-to-msfs-starting-with-fsltl/)
- [apollomaniacs – FSLTL-Injector](https://apollomaniacs.com/flight_msfs_traffic_en.htm)
- [adsb.lol API (Lizenz und Bedingungen)](https://api.adsb.lol/api/openapi.json)
- [FlyItalyADSB – ADS-B API comparison](https://flyitalyadsb.com/en/dati/confronto/)
- [wiert.me – ADS-B: Abflug und Ziel über adsbdb](https://wiert.me/2025/10/28/some-ads-b-api-link-notes-hoping-to-be-able-to-get-from-to-airport-data-from-it/)
- [FlightAware – AeroAPI Pricing](https://flightaware.com/api)
- [FlightAware – Data Feeds](https://zh-tw.flightaware.com/commercial/data)
- [EUROCONTROL – NM B2B Web Services](https://eurocontrol.int/service/network-manager-business-business-b2b-web-services)
- [FAA – SWIM Overview (ICAO 2026)](https://www.icao.int/sites/default/files/EURNAT/MeetingDocs/Air%20Navigation%20workshops/SWIM%20Seminar%202026/2.1-FAA-SWIM-Overview_B.Abraham.pdf)
- [FAA – SFDPS](https://www.faa.gov/air_traffic/technology/swim/sfdps)
