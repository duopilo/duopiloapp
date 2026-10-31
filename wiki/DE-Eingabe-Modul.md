[English](Input-Module) | **Deutsch**

# Eingabe-Modul

Duopilo kann Knöpfe drücken und Schalter stellen – als Pilot Monitoring, **auf Ansage des Piloten**, mit Readback und Bestätigung. Aktionen gibt es bisher für den **FlyByWire A32NX** und den **Headwind A330-900neo**; die meisten sind im Sim noch nicht geprüft.

## Standardmäßig gesperrt

Freigeben im Dashboard: in der Ansagen-Leiste zweimal auf **Eingabe gesperrt** klicken („Wirklich freigeben?“). Die Leiste zeigt auch die offene Aktion mit Countdown (auch eine, die über Claude Desktop vorbereitet wurde), dort kannst du sie abbrechen, und sie listet jede Eingabe mit ✓/✗.

Alternativ `Duopilo:Input:Enabled` in `appsettings.Local.json` – der Schalter im Dashboard hat Vorrang und gilt für alle Duopilo-Instanzen.

## Zwei Schritte

1. Du sagst „Set speed two five zero“. Claude ruft `prepare_action` auf; Duopilo prüft Wert und Sperren und gibt den Readback zurück: „Speed 250 selected“. **Noch ändert sich nichts.**
2. Claude liest ihn vor. Erst nach deinem **„confirm“** ruft Claude `confirm_action` auf. Duopilo prüft die Sperren noch einmal gegen den aktuellen Zustand, führt aus und liest danach im Flugzeug nach, ob der Wert angekommen ist.

„Negative“ oder ein korrigierter Wert verwirft die Aktion. Eine vorbereitete Aktion verfällt nach 60 s; es gibt immer nur eine.

Mit der [Sprachschleife](DE-Sprachschleife#ausführen--callout-standard) werden sicher erkannte Befehle direkt ausgeführt und danach angesagt („Speed two five zero set“); den Weg über den Readback gibt es, wenn die Erkennung unsicher ist oder ein Warnhinweis greift.

## Was geht (A32NX)

| Bereich | Aktionen |
|---|---|
| FCU | Speed (selected/managed), Heading (selected/managed), Altitude, Pull/Push, Increment 100/1000, V/S, LOC, APPR, EXPED, AP 1/2, A/THR |
| EFIS | QNH in hPa, STD/QNH, LS, FD, ND-Modus und -Reichweite (Kapitän) |
| Konfiguration | Fahrwerk, Klappenhebel, Speedbrake, Ground Spoiler armen |
| Bremsen | Autobrake OFF/LO/MED/MAX |
| Licht | Beacon, Wing, Runway Turn-off, Landing (on/off/retract), Nose (T.O/Taxi/off), Strobe (on/auto/off) |
| Schilder | Seat Belts, No Smoking/No PED |
| Anti-Ice | Triebwerke (beide oder einzeln), Flügel, Probe/Window Heat |
| Air/APU/Elec | Pack 1/2, APU Master, APU Start, APU Bleed, External Power (am Boden mit GPU) |
| Transponder | Squawk, Modus, ALT RPTG, TCAS-Modus und Traffic |
| Funk | COM 1 Standby, Swap |

NAV & LOGO fehlt beim A32NX (nur als `B:`-Input-Event erreichbar). Details je Flugzeug: [docs/aircraft.de.md](https://github.com/duopilo/duopiloapp/blob/main/docs/aircraft.de.md).

## Was nie geht

Triebwerks-Master, Zündung/Start, Schub, Feuer-Handles und -Agents, Sprit (Pumpen, Crossfeed), Hydraulik (Pumpen, PTU, RAT), IDG-Trennung, MCDU, Pause/Slew/Sim-Steuerung – **auch nicht mit Bestätigung**. Was das Flugzeug beschädigen kann oder irreversibel ist, machen die Piloten selbst.

Das ist im Code festgelegt; auch ein bearbeitetes Profil kommt nicht daran vorbei:

- Nur Events aus einer festen Liste (FCU, Autobrake, Fahrwerk, Klappen, Spoiler, Licht, Schilder, Transponder, COM).
- Geschrieben werden nur `L:`-Variablen, nie Standard-SimVars – und auch dort greift die Sperrliste.
- Fahrwerk und Klappen nur über ihre Events und nur zusammen mit der passenden Sperre.
- Bereiche und IDs wie `Engine`, `Fuel`, `Fire`, `Hydraulic`, `eng.*`, `fuel.*`, `*.idg1` werden beim Laden entfernt.
- Sicherheitsrelevante Schalter gibt es nur unter ihrer Standard-ID (`gear.down`, `air.pack1` …), damit die Sperren immer greifen.
- Es gibt kein Tool, das beliebige Events oder Variablen schreibt.

## Sperren

Abgelehnt mit Grund:

- Fahrwerk einfahren nur in der Luft
- Fahrwerk ausfahren nur bis VLO (A320/A330: 250 kt) und unter `maxGearAltitudeFt` (25.000 ft)
- Klappen nur bis zur VFE der neuen Stellung
- keine Speedbrake in den gesperrten Klappenstellungen (`speedbrakeInhibitFlaps`, A320/A330: FULL)
- Batterien aus und beide Packs aus nur am Boden (ein Pack aus im Flug geht)
- APU Master aus nicht bei APU Bleed an
- ADIRS aus nur am Boden

Dazu kommen Bedingungen je Aktion, z. B. V/S und Heading nicht im TRK/FPA-Modus, QNH nur im QNH-Modus, APU Start nur mit APU Master an.

## Rückfrage mit Warnhinweis

Ein zweites „confirm“ nach dem Readback brauchst du bei:

- Transponder STBY, ALT RPTG aus, TCAS STBY im Flug
- Squawk 7500 / 7600 / 7700
- ADIRS aus am Boden
- AP 1/2 und A/THR aus im Flug
- FCU-Höhe unter der MSA oder außerhalb der ATC-Freigabe

Das erste `confirm_action` liefert `status: "awaiting_caution_confirmation"` mit einem Warnhinweis („Caution, transponder standby in flight. Confirm transponder standby?“). Erst das zweite „confirm“ mit demselben Token führt aus. Steht der Schalter schon so, entfällt die Rückfrage.

**Fehlende Grenzwerte** im Profil (VLO/VLE, VFE, `maxGearAltitudeFt`, `speedbrakeInhibitFlaps`, `msaSource`) brauchen ebenfalls die Rückfrage – im Dashboard steht dann „Grenzwert fehlt“, in `duopilo_status` `profileWarnings`.

## Wie Duopilo das Ergebnis prüft

- Werte werden absolut gesetzt. Steht der Wert schon, wird nichts geschrieben: `alreadySet`.
- Der neue Wert muss stehen bleiben (`holdMs`, Standard 1 s). Setzt das Flugzeug ihn zurück (z. B. APU START ohne Strom), gilt er als nicht übernommen.
- `verification` in der Antwort:

| Wert | Bedeutung |
|---|---|
| `confirmed` | ein echter Flugzeugzustand wurde gelesen (FCU-Anzeige, Klappenhebel, Licht, Schütz …) |
| `unverified` | nur die Schalterstellung war lesbar; die Wirkung ist nicht nachgewiesen |
| `failed` | das Flugzeug hat den Wert nicht übernommen |

- **Fahrwerk und Klappen:** Nach `gear.down` wartet Duopilo, bis alle Beine verriegelt sind – „Gear down, three green“ –, sonst kommt nach 20 s „Gear disagree“. Genauso „Flaps two“, sobald Klappen und Vorflügel stillstehen, sonst „Flaps disagree“.
- Mit einem alten Token sagt dir `confirm_action`, was aus der Aktion geworden ist (ersetzt, abgelaufen, schon verwendet, andere Instanz).

## Geprüfte Aktionen merken

Aktionen mit `"verified": true` im Profil wurden im Sim getestet. Weitere merkt sich Duopilo in `%LOCALAPPDATA%\Duopilo\verified-actions.json`:

- automatisch, wenn eine Ausführung `confirmed` war;
- per `mark_action_verified`, wenn du im Cockpit gesehen hast, dass eine `unverified`-Aktion funktioniert hat („Seatbelts on“, „APU avail“, „check“).

Ändert sich die Definition einer Aktion, gilt ihre gespeicherte Prüfung nicht mehr. `list_actions` zeigt je Aktion `verified`: `profile`, `sim`, `pilot` oder leer.

## Aktionen definieren

Aktionen stehen im Flugzeugprofil unter `actions` (Events, Variablen, Readback, Prüfung, `requires`). Siehe [Flugzeugprofile](DE-Flugzeugprofile).
