# Feuerwehr: Regionen und Veröffentlichung

Stand: 03.11.2026. Status: Planung, nichts davon gebaut. Gehört zu [Feuerwehr: Stand und Test](feuerwehr-test.md) und
[Feuerwehr-Fahrzeuge](feuerwehr-fahrzeuge.md).

Duopilo wird veröffentlicht. Die Feuerwehr muss deshalb an jedem Flughafen der Welt stimmig wirken und nicht nur in Deutschland.
Leitlinie: Es gibt einen guten weltweiten Default, und die Regionen sind Daten, kein Code.

## Was sich je Region unterscheidet

| | Deutschland/Österreich | USA/Kanada | UK/Irland/Australien | Default (Rest der Welt) |
| --- | --- | --- | --- | --- |
| Fahrzeuge | FLF, ELW, HLF, RTW, Rettungstreppe | ARFF-Crash-Tender, Command, Rescue | Crash Tender, Command | FLF, ELW, RTW |
| Farbe | rot | gelbgrün oder rot | gelb/weiß oder rot | rot |
| Warnlicht | blau | rot/weiß | blau | blau |
| Horn | Folgetonhorn (Quarte, a'/d'') | Wail, Yelp, Presslufthorn | Two-Tone, Wail | Wail |
| Anzahl Fahrzeuge | ICAO-Kategorie | FAA-Index A–E | ICAO-Kategorie | ICAO-Kategorie |
| Rufzeichen im Funk | „Florian …“ | „Crash 1“, „ARFF 1“ | „Fire 1“ | „Fire 1“ |

Die Angaben zu Farben und Rufzeichen sind typisch, nicht überall gleich. Sie werden beim Bauen der Profile je Land geprüft.

## Regionsprofile (Duopilo)

- **Zuordnung über den ICAO-Präfix**, längster Treffer gewinnt: `ED`/`ET`/`LO` → Deutschland/Österreich, `K`/`C` → USA/Kanada,
  `EG`/`EI`/`Y` → UK/Irland/Australien, sonst Default.
- **Inhalt je Profil:** Modelle je Fahrzeugart, Livery, Horn (`L:1:DUOPILO_HORN`), Warnlichtfarbe (`L:1:DUOPILO_LICHTFARBE`),
  Rufzeichen, Regel für die Anzahl (ICAO oder FAA-Index) und ob das Horn auf der Anfahrt läuft.
- **Als JSON** im Datenordner mitgeliefert. Eine eigene Datei des Nutzers ergänzt oder ersetzt Profile, ohne dass wir neu bauen
  müssen. So kann die Community Länder nachtragen.
- **FAA-Index (USA):** nach der Länge des größten Flugzeugs mit mindestens fünf täglichen Abflügen. Index A ein Fahrzeug, B ein bis
  zwei, C zwei bis drei, D und E drei. Duopilo nimmt dafür die Länge des eigenen Flugzeugs, wie jetzt bei der ICAO-Kategorie.

## Modelle (Paket duopilo-fire)

- **Farbe über Liveries:** gleiche Form, andere Texturen. Duopilo wählt die Livery beim Spawnen (AICreateSimulatedObject_EX1).
- **Horn und Warnlicht über Variablen:** ein Paket mit allen Tönen und Lichtfarben, Duopilo setzt je Region
  `L:1:DUOPILO_HORN` (0 Folgetonhorn, 1 Wail, 2 Yelp, 3 Hi-Lo) und `L:1:DUOPILO_LICHTFARBE` (0 blau, 1 rot/weiß, 2 rot).
- **Eigene Modelle** nur, wo sich die Form deutlich unterscheidet, zuerst ein US-Crash-Tender.

## Vor der Veröffentlichung

- **Keine Herstellernamen:** „Panther“ (Rosenbauer) und „Z8“ (Ziegler) sind Produktnamen. Die SimObjects bekommen neutrale Namen,
  etwa `Duopilo_FLF_Rund_Licht` und `Duopilo_FLF_Eckig_Licht`; Duopilo stellt die Voreinstellung `Fire:FlfModels` gleichzeitig
  um. Logos haben die Modelle schon keine.
- **Sounds mit klarer Lizenz**, mit Quellenliste im Paket (`Duopilo-Modelle\Sounds\QUELLEN.txt`). Wail, Yelp, Hi-Lo und zwei
  Dieselmotoren sind synthetisch (`sounds_erzeugen.py`, CC0). Das synthetische Folgetonhorn klang zu künstlich; das Horn kommt aus
  MuseScore (`folgetonhorn.mid`, Horn und Trompete übereinander, Soundfont MS Basic) und wird mit `folgeton_aus_aufnahme.py` zur
  rauen Schleife. MS Basic steht laut Lizenzdatei im MuseScore-Repo unter MIT: Den Lizenztext vor der Veröffentlichung wörtlich in
  `QUELLEN.txt` übernehmen.
- **Paket mit ausliefern:** `duopilo-fire` wird bisher lokal aus Blender gebaut. Für ein Release braucht es eine Version im
  Manifest, einen Download oder Installer-Schritt und eine Prüfung in Duopilo, ob die passende Version im Community-Ordner liegt
  (sonst Hinweis statt stummer Fehlversuche beim Spawnen).
- **OpenStreetMap:** Die Wachen kommen zur Laufzeit von der Overpass API, der ODbL-Hinweis steht in den THIRD-PARTY-NOTICES.

## Reihenfolge

1. Deutschland komplett (Rücklicht, Bremslicht, Sound, neutrale Namen).
2. Regionsprofile in Duopilo mit Default und Deutschland, Horn- und Lichtfarben-Variable.
3. USA/Kanada: Livery, Wail/Yelp, rot/weiß, FAA-Index, später eigenes Crash-Tender-Modell.
4. UK/Irland/Australien und weitere über Livery und Horn.
5. Paket versioniert ausliefern und in Duopilo prüfen.
