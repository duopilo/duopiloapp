# Feuerwehr-Fahrzeuge: Licht, Sound, Räder, Lenkung und Rettungstreppe schalten

Stand: 03.11.2026. Status: Blaulicht und Fahrlicht im Sim getestet. Rück- und Bremslicht, Warnlichtfarben, Sound, Liveries, Räder,
Lenkung und Treppe sind exportiert, in den Dateien geprüft und als Paket installiert (03.11.2026), im Sim aber noch nicht getestet.
Der Einbau in Duopilo ist teilweise offen.

## Paket

Die Fahrzeuge liegen im MSFS-2024-Paket `duopilo-fire` (Community-Ordner). Gebaut werden sie mit Blender-Skripten im lokalen Ordner `Duopilo-Modelle`, der nicht im Repo liegt. Es gibt keine Herstellerlogos und keine Herstellernamen.

Bauen (im Ordner `Duopilo-Modelle`): `_alles_exportieren.bat` (alle Fahrzeuge und Follow-me, Blender 4.5), dann MSFS beenden und
`_pakete_bauen.bat` (beide Pakete; installiert nur bei Exitcode 0, steht in `build.log`). Die Store-Version des Paket-Tools endet bisher
mit Exitcode −2015035325 (0x87E50043), nachdem sie das Paket vollständig geschrieben hat. Dann die Ausgabe prüfen (Dateien, `layout.json`)
und mit `_pakete_installieren.bat` installieren.

Jedes Fahrzeug (`SimObjects/GroundVehicles`, `category=GroundVehicle`) hat drei Varianten, die `_Licht`-Variante dazu zwei weitere Lackfarben (siehe [Liveries](#liveries)):

| Fahrzeug | ruhig | Blaulicht blinkt (Animation) | Licht schaltbar |
| --- | --- | --- | --- |
| HLF 20 | `Duopilo_HLF` | `Duopilo_HLF_Einsatz` | `Duopilo_HLF_Licht` |
| Rettungswagen | `Duopilo_RTW` | `Duopilo_RTW_Einsatz` | `Duopilo_RTW_Licht` |
| Einsatzleitwagen | `Duopilo_ELW` | `Duopilo_ELW_Einsatz` | `Duopilo_ELW_Licht` |
| Rettungstreppe | `Duopilo_RTF` | `Duopilo_RTF_Einsatz` | `Duopilo_RTF_Licht` |
| FLF 8x8, runde Kabine | `Duopilo_FLF_Rund` | `Duopilo_FLF_Rund_Einsatz` | `Duopilo_FLF_Rund_Licht` |
| FLF 8x8, eckige Kabine | `Duopilo_FLF_Eckig` | `Duopilo_FLF_Eckig_Einsatz` | `Duopilo_FLF_Eckig_Licht` |

Die FLF hießen bis zum 03.11.2026 `Duopilo_FLF_Panther*` und `Duopilo_FLF_Z8*` (Produktnamen). Mit dem neuen Paket gibt es die alten
Titel nicht mehr; Duopilo spawnt die neuen und fällt auf die alten zurück, solange noch das alte Paket installiert ist.

Die `_Licht`-Variante hat echte Lichter, die wie bei den Standard-Bodenfahrzeugen auch auf den Boden scheinen. Das Warnlicht blitzt als Doppelblitz, links und rechts abwechselnd. Die Scheinwerfer werfen einen Kegel nach vorn, die Rückleuchten einen kleinen roten Schein nach hinten. Ohne Schalter ist alles aus und still.

## Schalter je Fahrzeug

| Schalter | Variable | Werte | Ersatz |
| --- | --- | --- | --- |
| Blaulicht | `L:1:DUOPILO_BLAULICHT` | 0/1 | `A:LIGHT BEACON` |
| Fahrlicht (Scheinwerfer) | `L:1:DUOPILO_FAHRLICHT` | 0/1 | `A:LIGHT LANDING` |
| Räder drehen | `L:1:DUOPILO_WEG` | Strecke in m seit dem Spawn, jeden Frame | – |
| Lenkung | `L:1:DUOPILO_KURVE` | Krümmung in 1/m, + = rechts, 0 = gerade, jeden Frame | – |
| Rücklicht | `L:1:DUOPILO_FAHRLICHT` | geht mit dem Fahrlicht an | `A:LIGHT LANDING` |
| Bremslicht | `L:1:DUOPILO_BREMSE` | 0/1: bremst gerade oder hat eben gehalten; auch ohne Fahrlicht | – |
| Motorgeräusch | `L:1:DUOPILO_TEMPO` | Tempo in m/s (0 bis 25), jeden Frame; bei 0 Leerlauf | – |
| Martinshorn | `L:1:DUOPILO_SIRENE` | 0/1: nur auf der Anfahrt in Fahrt (`Duopilo:Fire:Siren`) | – |
| Art des Horns | `L:1:DUOPILO_HORN` | 0 Folgetonhorn, 1 Wail, 2 Yelp, 3 Hi-Lo; je Region ([Regionen](feuerwehr-regionen.md)), bisher immer 0 | – |
| Warnlichtfarbe | `L:1:DUOPILO_LICHTFARBE` | 0 blau, 1 rot/weiß im Wechsel, 2 rot; gilt bei eingeschaltetem Blaulicht; je Region, bisher immer 0 | – |
| Rettungstreppe (nur RTF) | `L:1:DUOPILO_TREPPE` | 0 bis 100 (siehe unten) | – |

- Blaulicht, Fahrlicht und Bremslicht sind unabhängig voneinander. Das Rücklicht hängt am Fahrlicht.
- `L:1:` ist eine Z-Variable und gilt in MSFS 2024 nur für das eine SimObject. Die globalen `L:DUOPILO_*` werden nicht mehr ausgewertet. Mit ihnen gingen im Test alle Fahrzeuge gleichzeitig an.
- Im Modell schalten die Variablen über `<Behaviors>`/`<Visibility>` Knoten sichtbar (siehe [Licht-Knoten](#licht-knoten)).

### Licht-Knoten

Je Knoten gibt es drei Stufen (`_LOD0` bis `_LOD2`), jede mit eigenem `<Visibility>`-Eintrag. Ein Straßenlicht hat eine feste Farbe,
deshalb hat jede Warnlichtfarbe eigene Knoten mit eigenen Lichtern.

| Knoten | sichtbar bei | Inhalt |
| --- | --- | --- |
| `Blaulicht_LODn` | Blaulicht an und `LICHTFARBE` < 0,5 | blaue Blitzer, links und rechts im Wechsel |
| `Rotweisslicht_LODn` | Blaulicht an und `LICHTFARBE` 0,5 bis 1,5 | links rot, rechts weiß, im Wechsel |
| `Rotlicht_LODn` | Blaulicht an und `LICHTFARBE` > 1,5 | rote Blitzer |
| `Fahrlicht_LODn` | Fahrlicht | Scheinwerfer, Kegel nach vorn |
| `Ruecklicht_LODn` | Fahrlicht | 2 rote Lichter (60 cd, nur nachts) und leuchtende Glaskopien `Ruecklicht_Glas_LODn` |
| `Bremslicht_LODn` | `BREMSE` | 2 rote Lichter (400 cd, am Tag 250 cd) und hellere Glaskopien `Bremslicht_Glas_LODn` |
| `<Farbe>_Treppe_Buehne_LODn` (nur RTF) | wie die Farbe | Warnlichter auf der Bühne, fahren mit |

- In der `_Licht`-Variante sind die Warnlichtgläser neutral grau, weil die Farbe je Region wechselt; es leuchten die Lichter.
- Die Rückleuchtengläser des Modells leuchten nicht selbst. Es leuchten die Glaskopien, die mit dem Rück- oder Bremslicht sichtbar werden.
- Vergleiche im Code stehen in der Modell-XML als `&lt;` (ein rohes `<` macht die XML ungültig, auch für den Exporter).

| Fahrzeug | Warnlicht-Gruppen | Lichter je Stufe | Rückleuchten (Lage im Modell) |
| --- | --- | --- | --- |
| HLF | 10 | 66 | links/rechts ±1,12 m, 0,65 m hoch |
| RTW | 6 | 42 | ±1,00 m, 1,00 m hoch |
| ELW | 6 | 42 | ±0,70 m, 0,97 m hoch |
| RTF | 8, davon 4 auf der Bühne | 54 | ±1,11 m, 0,78 m hoch |
| FLF Rund | 8 | 54 | ±1,22 m, 1,61 m hoch |
| FLF Eckig | 6 | 42 | ±1,27 m, 1,56 m hoch |

### Sound

Wave-Data-Sounds (`WwiseData="False"`, ohne Wwise), nur in der `_Licht`-Variante und ihren Liveries. Wail, Yelp, Hi-Lo und die
Motoren sind synthetisch (CC0), das Folgetonhorn ist aus MuseScore gerendert (Soundfont MS Basic, MIT). Quellen und Lizenztext stehen in
`QUELLEN.txt`, das in jedem Sound-Ordner mit im Paket liegt. Alle Dateien: 16 Bit PCM, 44,1 kHz mono, nahtlose Schleifen.

| Slot | Datei | spielt | Reichweite (`FullScaleDistance`) |
| --- | --- | --- | --- |
| `custom_sound_01` | `horn_folgeton.wav` | `SIRENE` ≥ 0,5 und `HORN` = 0 | 90 m |
| `custom_sound_02` | `horn_wail.wav` | `SIRENE` ≥ 0,5 und `HORN` = 1 | 90 m |
| `custom_sound_03` | `horn_yelp.wav` | `SIRENE` ≥ 0,5 und `HORN` = 2 | 90 m |
| `custom_sound_04` | `horn_hilo.wav` | `SIRENE` ≥ 0,5 und `HORN` = 3 | 90 m |
| `custom_sound_05` | `motor_lkw.wav` (FLF, HLF, RTF) oder `motor_transporter.wav` (ELW, RTW, Follow-me) | immer; Tonhöhe 1,0 bis 1,6 und Lautstärke 60 bis 100 % über `TEMPO` 0 bis 25 m/s | 18 m (Follow-me 15 m) |

- `sim.cfg` der `_Licht`-Titel: `sound="licht"` und `soundai="licht"`. Beide Ordner (`sound.licht/sound.xml`, `soundai.licht/soundai.xml`)
  haben denselben Inhalt, weil noch offen ist, welchen MSFS bei per SimConnect gespawnten Bodenfahrzeugen nimmt. Nach dem Test den
  anderen entfernen.
- In der XML stehen die Variablen ohne `L:1:` als `LocalVarEX1="DUOPILO_SIRENE"`, die Wahl des Horns über `<Requires LocalVarEX1="DUOPILO_HORN">`
  mit `<Range>`, Tonhöhe und Lautstärke über `<PitchCurve LocalVarEX1="DUOPILO_TEMPO">` und `<VolumeCurve …>`.
- **Im Sim (03.11.2026):** Wave-Data-Sounds spielen bei per SimConnect gespawnten Bodenfahrzeugen (im Forum: mit 1.0.72 kaputt, mit
  1.0.77 behoben). Jedes Fahrzeug spielt seinen eigenen Motor, obwohl alle denselben Slot `custom_sound_05` nutzen.
- **Offen, im Sim prüfen:** Greift `LocalVarEX1` bei Z-Variablen (`L:1:`), also Horn an/aus und Wahl des Horns? Laut SDK nehmen `PitchCurve`/`VolumeCurve` nur `SimVar`; ob `LocalVarEX1` dort geht,
  ist ungeprüft. Wenn nicht: Duopilo müsste eine SimVar am Objekt setzen, die der Sound lesen darf.
- Die SDK-Seite nennt die `WwiseData="False"`-Attribute „für das Portieren alter Inhalte“. Für KI-Objekte sind nur die Slots
  `custom_sound_01` bis `_12` freigegeben. Dasselbe `WwiseEvent` nicht zweimal verwenden (bekannter Fehler).

### Liveries

Gleiche Modelle, andere Lackfarben, ausgewählt über den Titel. Duopilo hängt den Zusatz an den `_Licht`-Titel; ohne Zusatz kommt Rot.

| Livery | Titel | Region | Farben |
| --- | --- | --- | --- |
| Rot (Default) | `Duopilo_<Fahrzeug>_Licht` | Deutschland/Österreich, Default | rot, Streifen leuchtgelb |
| Gelbgruen | `Duopilo_<Fahrzeug>_Licht_Gelbgruen` | USA/Kanada (ARFF) | gelbgrün, Streifen rot, Konturmarkierung weiß |
| Gelb | `Duopilo_<Fahrzeug>_Licht_Gelb` | UK/Irland/Australien | gelb, Streifen und Flächen weiß, Konturmarkierung rot |

- Umsetzung: `livery.cfg` und `szLivery` (`AICreateSimulatedObject_EX1`) gelten laut SDK nur für modulare SimObjects. Die Modelle haben
  keine Texturen, nur Materialfarben. Jede Livery ist deshalb eine Kopie von `model.licht` (`model.licht_gelbgruen`, `model.licht_gelb`)
  mit geänderten Grundfarben der Materialien `Lack_Leuchtrot`, `Lack_Rot`, `Folie_Leuchtgelb` und `Kontur_Gelb`, als eigener
  `[fltsim.3]`/`[fltsim.4]`-Eintrag. Lichter, Animationen, Behaviors und Sound sind gleich.
- Keine Wappen, Logos oder echten Wachennamen. Die Schrift („AIRPORT FIRE DEPARTMENT“, „RTW 1“ …) ist Folie und wechselt die Farbe mit:
  auf Gelb ist sie weiß und schlechter lesbar.

### Räder und Lenkung

Stand: 02.11.2026. Alle sechs `_Licht`-Varianten sind exportiert und die Werte im glTF geprüft, im Sim aber noch nicht getestet. In den anderen Varianten stehen die Räder gerade und drehen nicht.

| Fahrzeug | Raddurchmesser | Radumfang | Radstand für die Lenkung | gelenkte Achsen | Achsen (z vor dem Nullpunkt) |
| --- | --- | --- | --- | --- | --- |
| HLF | 1,08 m | 3,3929 m | 4,50 m | A1 | +2,20 / −2,30 |
| RTW | 0,71 m | 2,2305 m | 4,325 m | A1 | +2,52 / −1,805 |
| ELW | 0,71 m | 2,2305 m | 4,49 m | A1 | +2,45 / −2,04 |
| RTF | 1,04 m | 3,2673 m | 4,50 m | A1 | +2,30 / −2,20 |
| FLF Rund (8x8) | 1,413 m | 4,439 m | 6,15 m (A1 bis Mitte A3/A4) | A1 (35°), A2 (Faktor 0,724, also 25,3°) | +3,40 / +1,70 / −1,90 / −3,60 |
| FLF Eckig (8x8) | 1,413 m | 4,439 m | 6,15 m (A1 bis Mitte A3/A4) | A1 (35°), A2 (Faktor 0,724, also 25,3°) | +3,40 / +1,70 / −1,90 / −3,60 |

- **Animation `Raeder`** (`Type="Sim"`, Länge 100, kein Lag): Sie dreht die Knoten `Rad_A<n><L|R>_LODn` (alle Räder) um die Radachse und rollt vorwärts. Bild 100 entspricht 360°. Code: `(L:1:DUOPILO_WEG, number) <Radumfang> / d flr - 100 *`
- **Animation `Lenkung`** (`Type="Sim"`, Länge 100, Lag 80): Sie dreht die Knoten `Lenk_A<n><L|R>_LODn` um die Hochachse; 0 ist voll links, 50 gerade, 100 voll rechts, Vollausschlag ±35°. Die Lenk-Knoten sind Eltern der gelenkten Räder, mit dem Drehpunkt in der Radmitte; Lenken und Drehen überlagern sich. Code: `(L:1:DUOPILO_KURVE, number) <Radstand> * atg rddg -35 max 35 min 35 / 50 * 50 +`
- **Follow-me:** `Duopilo_FollowMe_Licht` kommt aus einem eigenen Skript und Exporter (`FollowMe/export_msfs.py`) mit eigenem Paket
  (`duopilo-followme`). Seit dem 03.11.2026 hat es dieselben Rück- und Bremslichter (`Ruecklicht_LODn` mit dem Fahrlicht, das dort auch
  mit `A:LIGHT BEACON` angeht; `Bremslicht_LODn` mit `BREMSE`) und den Motor (`motor_transporter.wav`, `custom_sound_05`, kein Horn).
  Raddrehen, Lenkung, Warnlichtfarbe und Liveries fehlen dort.

## Einbau in Duopilo (offen)

Gebraucht für Follow-me und die Flughafenfeuerwehr (siehe [Roadmap](../../wiki/Roadmap.md)):

1. Die `_Licht`-Variante spawnen und die ObjectID aus `SIMCONNECT_RECV_ASSIGNED_OBJECT_ID` merken.
2. Je Variable eine eigene DataDefinition anlegen: `AddToDataDefinition(def, "L:1:DUOPILO_BLAULICHT", "Number", SIMCONNECT_DATATYPE_FLOAT64)`.
3. Mit `SetDataOnSimObject(def, objectId, …)` 0 oder 1 setzen.
4. Während das Objekt lädt, wird der Wert ignoriert. Nach einem Respawn oder Reload steht er wieder auf 0. Deshalb kurz nach dem Spawn setzen und bei jeder Zustandsänderung erneut senden.
5. Falls `L:1:` per SimConnect nicht greift: die A-Variablen `LIGHT BEACON` und `LIGHT LANDING` (Bool) auf dieselbe ObjectID setzen. Das Modell wertet beide aus.
6. Räder und Lenkung (Branch `feature/feuerwehr`): `L:1:DUOPILO_WEG` und `L:1:DUOPILO_KURVE` setzt Duopilo im Frame-Takt mit der Lage
   (je eine DataDefinition, nur wenn sich der Wert ändert). Die Krümmung kommt aus Kursänderung je Meter, über 0,4 s geglättet.

## Rettungstreppe anlegen (RTF, offen)

Stand: 02.11.2026. Das Modell ist fertig und exportiert, im Sim aber noch nicht getestet. Die Logik in Duopilo fehlt noch.

Beim RTF hebt sich die Bühne über dem Fahrerhaus, und an der rechten Seite fährt eine Brücke aus. Die Treppe ist zweiteilig und teleskopisch: Sie schwenkt mit, und ihr Fuß bleibt hinten am Fahrzeug.

| | Wert |
| --- | --- |
| Variable je Fahrzeug | `L:1:DUOPILO_TREPPE`, 0 bis 100 (`number`), nur `Duopilo_RTF_Licht` |
| 0 | eingefahren, Fahrstellung |
| 0 bis 10 | Brücke fährt rechts 0,62 m aus |
| 10 bis 100 | Bühne hebt sich gleichmäßig um bis zu 2,50 m |
| Brückenoberkante | 3,13 m (bei 10) bis 5,63 m (bei 100) über Boden |
| Wert für eine Schwellenhöhe h | `p = 10 + 90 * (h - 3.13) / 2.50`, gültig für h von 3,13 bis 5,63 m |
| Tempo | Lag 5, also etwa 20 s von 0 bis 100 |

- **Andocken seitlich rechts:** Das Fahrzeug steht parallel zum Rumpf, mit der rechten Seite zur Tür. Die Türmitte liegt auf Höhe der Brückenmitte, 1,80 m vor dem Modell-Nullpunkt (Fahrzeugmitte am Boden). Die ausgefahrene Brücke reicht bis 1,81 m rechts der Fahrzeugmitte.
- **Im Modell:** Animation `Treppe` (Länge 100, `Type="Sim"`) in `<Behaviors>`. Bewegt werden die Knoten `Treppe_Buehne`, `Treppe_Bruecke`, `Treppe_Oben` und `Treppe_Unten`. Die Blaulichter auf der Bühne fahren mit, eigener Schalter-Knoten `Blaulicht_Treppe_Buehne`.
- **Prüfen:** Ob MSFS 0 bis 100 auf die ganze Bewegung legt. Exportiert sind 100 Bilder bei 24 fps, also 4,17 s.

Ablauf in Duopilo (offen):

1. Das RTF fährt mit `p = 0` zur Tür und hält parallel zum Rumpf, mit etwa 0,3 m zwischen der ausgefahrenen Brückenkante und dem Rumpf. Für die genaue Lage muss Duopilo das Fahrzeug selbst stellen, die Wegpunkt-Steuerung ist zu ungenau.
2. `p` aus der Schwellenhöhe des Flugzeugtyps setzen und warten, bis die Bühne oben ist.
3. Zum Abfahren `p = 0` setzen, etwa 20 s warten, erst dann losfahren.

Die Schwellenhöhe der Türen gehört ins Flugzeugprofil. Grobe Richtwerte, noch ungeprüft: A320 etwa 3,4 m, A330/B767 etwa 4,7 m, B777/B787/A350 etwa 5,3 bis 5,5 m. Niedriger als 3,13 m geht nicht, die B737 mit etwa 2,7 m ist also zu niedrig.

## Test

- Zwei `Duopilo_HLF_Licht` spawnen und nur bei einem das Blaulicht setzen. Nur dieses eine darf blitzen.
- Fahrlicht allein einschalten: Das Blaulicht bleibt aus.
- Noch ungetestet: ob SimConnect `L:1:` auf KI-Objekte schreiben kann. Laut SDK ja, im Forum gab es dazu offene Fragen.
- Räder: `L:1:DUOPILO_WEG` auf 0, dann auf einen Radumfang, dann auf 1,5 Radumfänge setzen. Bei 0 und bei einem Umfang steht das Rad gleich, bei 1,5 Umfängen ist es um 180° gedreht. Beim Hochzählen muss es vorwärts rollen.
- Lenkung: `L:1:DUOPILO_KURVE` auf 0,04 setzen, die Vorderräder lenken nach rechts. Bei −0,04 lenken sie nach links, bei 0 stehen sie gerade.
- Zwei Fahrzeuge desselben Typs: Nur das, dessen Werte gesetzt sind, darf sich bewegen.
- RTF: `L:1:DUOPILO_TREPPE` auf 10, 55 und 100 setzen. Die Brücke muss rechts herausfahren, die Bühne auf halbe bzw. volle Höhe gehen, und die Treppe muss geschlossen bleiben.
- Rück- und Bremslicht: `FAHRLICHT` 1 → Rücklicht an; `BREMSE` 1 → deutlich heller, auch bei `FAHRLICHT` 0; beides 0 → aus.
- Warnlichtfarbe: `BLAULICHT` 1 mit `LICHTFARBE` 0, 1, 2 → blau, rot/weiß, rot, auf dem Fahrzeug und auf dem Boden.
- Horn: `SIRENE` 1 mit `HORN` 0 bis 3 → die vier Töne, leiser mit Abstand; `SIRENE` 0 → still.
- Motor: `TEMPO` 0 → 10 → 25 → der Motor geht hoch und wird lauter.
- Liveries: `_Licht`, `_Licht_Gelbgruen` und `_Licht_Gelb` spawnen und ansehen.
- Prüfskripte im Ordner `Duopilo-Modelle\MSFS`: `check_raeder.py` (Räder, Lenkung, Treppe) und `check_release.py` (XML lesbar,
  Schalt-Knoten, Lichter je Knoten, Glaskopien, Lackfarben der Liveries, Sound-Ordner und Titel).

## Später

- Die Varianten ohne `_Licht` (ruhig/`_Einsatz`) entfernen, sobald `_Licht` überall läuft.

## Quellen

- [SimConnect_AddToDataDefinition (MSFS 2024 SDK)](https://docs.flightsimulator.com/msfs2024/flighting/programming-apis/simconnect/api-reference/events-and-data/simconnect_addtodatadefinition/): Abschnitt zu Z- und `L:1:`-Variablen
- [How to access scoped LVars (L:1:) with SimConnect](https://devsupport.flightsimulator.com/t/how-to-access-scoped-lvars-l-with-simconnect/12540)
- [Wave Data Sound Properties (MSFS 2024 SDK)](https://docs.flightsimulator.com/msfs2024/flighting/content-configuration/sounds/wave-data-sound-properties/index.md): `WwiseData="False"`, Slots für KI-Objekte, `PitchCurve`/`VolumeCurve`
- [sound.xml Properties (MSFS 2024 SDK)](https://docs.flightsimulator.com/msfs2024/flighting/content-configuration/sounds/sound-xml-properties/index.md): Ordner `sound`/`soundai`, `LocalVarEX1`, `<Requires>`, `<Range>`
- [sim.cfg (MSFS 2024 SDK)](https://docs.flightsimulator.com/msfs2024/retail/content-configuration/cfg-files/sim.cfg/index.md): `sound=` und `soundai=` je `[fltsim.N]`
- [livery.cfg (MSFS 2024 SDK)](https://docs.flightsimulator.com/msfs2024/flighting/content-configuration/cfg-files/livery.cfg/index.md): Liveries für modulare SimObjects
- [MSFS 2020 SimObjects wav sounds not working](https://devsupport.flightsimulator.com/t/msfs-2020-simobjects-wav-sounds-not-working/10429): WAV-Sounds bei per SimConnect gespawnten Bodenfahrzeugen, behoben in 1.0.77
- [Multiple SimVar sounds with the same event name …](https://devsupport.flightsimulator.com/t/multiple-simvar-sounds-with-the-same-event-name-use-the-first-definitions-nodename-as-the-position/14554)
