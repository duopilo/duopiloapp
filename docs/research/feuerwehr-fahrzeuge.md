# Feuerwehr-Fahrzeuge: Licht und Blaulicht schalten

Stand: 02.11.2026. Status: Modelle fertig und im Sim getestet. Der Einbau in Duopilo ist noch offen.

## Paket

Die Fahrzeuge liegen im MSFS-2024-Paket `duopilo-fire` (Community-Ordner). Gebaut werden sie mit Blender-Skripten im lokalen Ordner `Duopilo-Modelle`, der nicht im Repo liegt. Es gibt keine Herstellerlogos.

Jedes Fahrzeug (`SimObjects/GroundVehicles`, `category=GroundVehicle`) hat drei Varianten:

| Fahrzeug | ruhig | Blaulicht blinkt (Animation) | Licht schaltbar |
| --- | --- | --- | --- |
| HLF 20 | `Duopilo_HLF` | `Duopilo_HLF_Einsatz` | `Duopilo_HLF_Licht` |
| Rettungswagen | `Duopilo_RTW` | `Duopilo_RTW_Einsatz` | `Duopilo_RTW_Licht` |
| Einsatzleitwagen | `Duopilo_ELW` | `Duopilo_ELW_Einsatz` | `Duopilo_ELW_Licht` |
| Rettungstreppe | `Duopilo_RTF` | `Duopilo_RTF_Einsatz` | `Duopilo_RTF_Licht` |
| FLF (Panther-Stil) | `Duopilo_FLF_Panther` | `Duopilo_FLF_Panther_Einsatz` | `Duopilo_FLF_Panther_Licht` |
| FLF (Z8-Stil) | `Duopilo_FLF_Z8` | `Duopilo_FLF_Z8_Einsatz` | `Duopilo_FLF_Z8_Licht` |

Die `_Licht`-Variante hat echte Lichter, die wie bei den Standard-Bodenfahrzeugen auch auf den Boden scheinen. Das Blaulicht blitzt als Doppelblitz, links und rechts abwechselnd. Die Scheinwerfer werfen einen Kegel nach vorn. Ohne Schalter ist alles aus.

## Schalter je Fahrzeug

| Licht | Variable (0/1) | Ersatz |
| --- | --- | --- |
| Blaulicht | `L:1:DUOPILO_BLAULICHT` | `A:LIGHT BEACON` |
| Fahrlicht (Scheinwerfer) | `L:1:DUOPILO_FAHRLICHT` | `A:LIGHT LANDING` |

- Blaulicht und Fahrlicht sind unabhängig voneinander.
- `L:1:` ist eine Z-Variable und gilt in MSFS 2024 nur für das eine SimObject. Die globalen `L:DUOPILO_*` werden nicht mehr ausgewertet. Mit ihnen gingen im Test alle Fahrzeuge gleichzeitig an.
- Im Modell schalten die Variablen über `<Behaviors>`/`<Visibility>` die Knoten `Blaulicht_LODn` und `Fahrlicht_LODn` sichtbar.

## Einbau in Duopilo (offen)

Gebraucht für Follow-me und die Flughafenfeuerwehr (siehe [Roadmap](../../wiki/Roadmap.md)):

1. Die `_Licht`-Variante spawnen und die ObjectID aus `SIMCONNECT_RECV_ASSIGNED_OBJECT_ID` merken.
2. Je Variable eine eigene DataDefinition anlegen: `AddToDataDefinition(def, "L:1:DUOPILO_BLAULICHT", "Number", SIMCONNECT_DATATYPE_FLOAT64)`.
3. Mit `SetDataOnSimObject(def, objectId, …)` 0 oder 1 setzen.
4. Während das Objekt lädt, wird der Wert ignoriert. Nach einem Respawn oder Reload steht er wieder auf 0. Deshalb kurz nach dem Spawn setzen und bei jeder Zustandsänderung erneut senden.
5. Falls `L:1:` per SimConnect nicht greift: die A-Variablen `LIGHT BEACON` und `LIGHT LANDING` (Bool) auf dieselbe ObjectID setzen. Das Modell wertet beide aus.

## Test

- Zwei `Duopilo_HLF_Licht` spawnen und nur bei einem das Blaulicht setzen. Nur dieses eine darf blitzen.
- Fahrlicht allein einschalten: Das Blaulicht bleibt aus.
- Noch ungetestet: ob SimConnect `L:1:` auf KI-Objekte schreiben kann. Laut SDK ja, im Forum gab es dazu offene Fragen.

## Später

- Die Varianten ohne `_Licht` (ruhig/`_Einsatz`) entfernen, sobald `_Licht` überall läuft.

## Quellen

- [SimConnect_AddToDataDefinition (MSFS 2024 SDK)](https://docs.flightsimulator.com/msfs2024/flighting/programming-apis/simconnect/api-reference/events-and-data/simconnect_addtodatadefinition/): Abschnitt zu Z- und `L:1:`-Variablen
- [How to access scoped LVars (L:1:) with SimConnect](https://devsupport.flightsimulator.com/t/how-to-access-scoped-lvars-l-with-simconnect/12540)
