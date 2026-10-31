[English](Pilot-Monitoring) | **Deutsch**

# Pilot Monitoring

Alle 200 ms prüft Duopilo eine Reihe von Regeln und meldet Auffälligkeiten mit einem kurzen englischen Callout. Die Meldungen werden gesprochen ([Sprachausgabe](DE-Sprachausgabe)), im Dashboard angezeigt und an Claude geliefert (`activeAlerts`, `get_alerts`, `wait_for_alerts`).

Nach dem Laden eines Flugs, einem Flugzeugwechsel oder einem Air-Start schweigt das Monitoring 10 s, nach einer Pause 3 s – dann springen die Werte. Solange der Sim pausiert ist, ruhen alle Regeln (`SIM_PAUSED`).

## Sicherheits- und Abweichungsregeln

| Regel | Callout | Bedingung |
|---|---|---|
| `GEAR_SPEED` | Gear speed | Fahrwerk nicht eingefahren und IAS über VLE |
| `FLAP_SPEED` | Flap speed | IAS über VFE der Klappenstellung |
| `OVERSPEED` | Overspeed | IAS über VMO oder Overspeed-Warnung des Sims |
| `STALL` | Stall | Stall-Warnung des Sims in der Luft |
| `SINK_RATE` | Sink rate | unter 2500 ft RA mehr als 1500–3000 ft/min Sinken |
| `BANK_ANGLE` | Bank angle | mehr als 35° (unter 150 ft RA enger, über 2500 ft RA 45°) |
| `MASTER_WARNING` / `MASTER_CAUTION` | Master caution, ELEC GEN 1 OFF | Master-Licht an, mit der zuletzt aufgetauchten ECAM-Meldung |
| `ECAM_MESSAGE` | HYD G RSVR LO LVL | weitere ECAM-Meldung, während Master Warning/Caution schon leuchtet |
| `GEAR_DOWN_HIGH` | Gear still down | Fahrwerk draußen im Steigflug über 2000 ft AGL oder über FL150 |
| `SPEED_LIMIT_FL100` | Speed limit | unter 10.000 ft schneller als 250 kt |
| `SPEED_DEVIATION` | Speed | IAS weicht vom Ziel ab (+15/−10 kt, im Anflug +10/−5 kt) und läuft nicht darauf zu |
| `AP_DISCONNECT` | Autopilot off | Autopilot in der Luft getrennt |
| `ALT_1000_TO_GO` | One thousand to go | 1000 ft vor der FCU-Höhe |
| `TOP_OF_DESCENT` | Top of descent | A32NX: FMS-Meldung T/D REACHED; sonst TOD aus SimBrief |
| `LANDING_CHECK` | Landing checklist complete / Spoilers not armed | bei 2000 ft RA im Sinkflug: Fahrwerk, Landeklappen, Ground Spoiler; bei 1000 ft RA erneut, falls noch etwas fehlt |
| `RUNWAY_ASSIGNED` | Runway two three left assigned | das Ingame-ATC von MSFS hat eine Bahn zugewiesen |
| `TRAFFIC_PROXIMITY` | Traffic, 2 o'clock, 2 miles, 500 feet above, climbing | Ziel in der Luft näher als 3 NM und innerhalb ±1000 ft, eigenes Flugzeug über 500 ft AGL; danach 60 s Ruhe – siehe [Verkehr](DE-Verkehr) |

## Autoflug-Regeln

| Regel | Callout | Bedingung |
|---|---|---|
| `TCAS_RA` | TCAS RA / Clear of conflict | vertikale FMA zeigt 0,3 s lang TCAS; „Clear of conflict“ 1,5 s nach dem Ende |
| `ATHR_OFF` | Autothrust off | A/THR geht in der Luft von armed/active auf aus (nicht beim Trennen unter 100 ft RA im Aufsetzen) |
| `FMA_CHANGE` | Loc star / Glideslope star / Alt blue | neuer lateraler/vertikaler oder neu gearmter Modus in der Luft, nach 1 s |
| `LOC_DEVIATION` | Loc | LOC oder LAND aktiv, 50–2000 ft RA, 1 s lang mehr als ¼ Dot |
| `GS_DEVIATION` | Glideslope | G/S oder LAND aktiv, 100–2000 ft RA, 1 s lang mehr als 1 Dot |
| `APPR_NOT_ACTIVE` | Approach phase not activated | ab 15 NM vor dem Ziel mit Managed Speed, FMGC nicht in der Approach-Phase – einmal je Anflug |

Diese Regeln hängen an Profil-Bindings (FMA, A/THR, FMGC-Phase, LOC/GS-Abweichung). **Duopilo rät nicht:** Ohne Binding bleibt der Callout für dieses Flugzeug aus. `get_aircraft_profile` → `callouts` zeigt je Regel `verified`, `unverified` oder `missing`; die Übersicht je Flugzeug steht in [docs/aircraft.de.md](https://github.com/duopilo/duopiloapp/blob/main/docs/aircraft.de.md#autoflug-callouts-v092).

## Callouts des Pilot Monitoring

| Regel | Callout | Wann |
|---|---|---|
| `PM_100_KNOTS`, `PM_V1`, `PM_ROTATE` | One hundred knots / V1 / Rotate | Startlauf (V1/VR beim A32NX aus der MCDU) |
| `PM_POSITIVE_CLIMB` | Positive climb | nach dem Abheben, sobald es wirklich steigt |
| `PM_SPOILERS` | Spoilers / No spoilers | nach dem Aufsetzen („No spoilers“ nach 3 s ohne Ground Spoiler) |
| `PM_REVERSE` | Reverse green | Schubumkehr ausgefahren |
| `PM_DECEL` | Decel | Autobrake DECEL (A32NX), sonst spürbare Verzögerung |
| `PM_70_KNOTS` | Seventy knots | Landerollen unter 70 kt |
| `PM_TEN_THOUSAND` | Ten thousand | durch 10.000 ft, steigend und sinkend |
| `PM_TRANSITION` | Transition altitude / Transition level | TA im Steigflug, TL im Sinkflug (aus dem FMS, sonst SimBrief) |
| `PM_FLAPS` | Speed checked, flaps two / Flaps zero | Klappenhebel steht 1,5 s in neuer Stellung; beim Ausfahren mit Speed-Check gegen VFE |
| `PM_GEAR` | Gear down / Gear up | Fahrwerkshebel in der Luft |
| `TAXI_SPEED` | Taxi speed | am Boden länger als 2 s über 25 kt GS, höchstens alle 30 s (einstellbar in `voice-settings.json`) |
| `FUEL_CHECK` | HENDY, fuel 34.1, 2.2 below plan | Reiseflug, siehe [Fuel Check](#fuel-check) |
| `FUEL_WARNING` | HENDY, fuel 31.2, 2.8 below plan, deviation increased by 0.6 | Abweichung seit dem letzten Check um 300 kg schlechter, oder Landemenge unter Minimum + Puffer |

Radiohöhen (2500, 1000, 500 …), „Hundred above“, „Minimum“ und „Retard“ sagt der A32NX selbst an. Für Flugzeuge ohne eigene Ansagen kann Duopilo sie ergänzen: **Ansagen → Stimme** oder `Duopilo:Monitoring:AircraftAutoCallouts`.

## Fuel Check

Mit SimBrief-Plan vergleicht Duopilo im Reiseflug den Sprit an Bord mit dem Plan – wie die Crew beim Fuel Check:

- **An jedem OFP-Wegpunkt** im Reiseflug (ohne TOC/TOD): „Hendy, fuel 34.1, 2.2 below plan“ (Tonnen, eine Nachkommastelle; unter 50 kg Abweichung „on plan“).
- **Intervall:** 30 min (Sim-Zeit) kein Wegpunkt → Check ohne Ort.
- **Warnung `FUEL_WARNING`** (laufend geprüft):
  - die Abweichung ist seit dem letzten Check um mehr als `DeviationIncreaseKg` gewachsen (Leckverdacht);
  - die prognostizierte Landemenge (geplant + heutige Abweichung) fällt unter Minimum + Puffer oder unter das Minimum. Der Puffer ist höchstens halb so groß wie die geplante Reserve, damit knapp geplante Flüge nicht schon beim ersten Check warnen.
- **Nachtanken** erkennt Duopilo und meldet es stumm („Nachtanken erkannt: 3.530 → 11.090 kg“); der Vergleich beginnt neu.

Im Dashboard steht unter der Route der Trend der Abweichung; `get_progress` liefert ihn als `fuelCheck`.

```jsonc
"Duopilo": { "Monitoring": { "FuelCheck": {
  "IntervalMinutes": 30,       // 0 = kein Intervall-Check
  "DeviationIncreaseKg": 300,  // 0 = keine Warnung bei wachsender Abweichung
  "LandingBufferKg": 500,
  "MinSpacingMinutes": 0       // z. B. 10 = über Europa nicht an jedem Wegpunkt ansagen
} } }
```

Nur die Ansagen abschalten, Warnungen behalten: `FUEL_CHECK` in `DisabledRules` eintragen.

## Regeln abschalten

Ganz abschalten (keine Meldung, keine Ansage) in `appsettings.Local.json`:

```jsonc
"Duopilo": { "Monitoring": { "DisabledRules": [ "SPEED_DEVIATION" ] } }
```

Sichtbar lassen, aber nicht sprechen: `"mutedRules": [ "PM_GEAR" ]` in `%LOCALAPPDATA%\Duopilo\speech.json`.

Mit eingeschalteter [Sprachschleife](DE-Sprachschleife) erwarten wichtige Meldungen außerdem eine Quittung („Check“ / „Copy“).
