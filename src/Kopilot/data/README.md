# Luftraum- und Airline-Daten

Daten für das Kopilot-ATC. Kopilot liest sie beim Start der ATC-Sitzung. Ein eigener, neuerer Stand in
`%LOCALAPPDATA%\Kopilot\data\…` (gleiche Ordnerstruktur) hat Vorrang vor den mitgelieferten Dateien und übersteht
jedes Update. Danach `atc_control reload`.

| Ordner | Inhalt | Quelle | Lizenz |
|---|---|---|---|
| `vatspy/` | FIR-Grenzen (`Boundaries.geojson`) und FIR-Namen (`VATSpy.dat`), unverändertes Originalformat | [VAT-Spy Data Project](https://github.com/vatsimnetwork/vatspy-data-project), Stand `10fbcea` (27.10.2026) | CC BY-SA 4.0 (`vatspy/LICENSE`) |
| `sectors/` | Streckensektoren mit Höhenband, Frequenz und Rufname (`atc-sectors.json`), erzeugt mit `tools/import_sectors.py` | [VATGlasses Data Project](https://github.com/lennycolton/vatglasses-data) (VATSIM-Sektordaten), Stand `5946a1c` (29.10.2026) | CC BY-NC-SA 4.0 (`sectors/LICENSE`) – nur nicht-kommerziell |
| `openflights/` | Airlines mit ICAO-Kennung und Telephony-Rufzeichen (`airlines.dat`, Originalformat) | [OpenFlights](https://openflights.org/data.php), Stand `e539814` (23.10.2026) | Open Database License (ODbL) 1.0, Inhalte unter der Database Contents License |

## Aktualisieren

VAT-Spy und OpenFlights: die Dateien aus dem jeweiligen Repository unverändert hierher (oder nach
`%LOCALAPPDATA%\Kopilot\data\vatspy` bzw. `…\openflights`) kopieren.

Sektoren:

```
git clone --depth 1 https://github.com/lennycolton/vatglasses-data
python tools/import_sectors.py --vatglasses vatglasses-data
```

Das Skript übernimmt nur Streckenpositionen (CTR/FSS) mit VHF-Frequenz, ordnet jeden Sektor einer FIR zu (Gruppe,
Rufzeichen-Präfix aus `VATSpy.dat` oder Lage) und vereinfacht die Polygone auf etwa 0,1 nm.

## Hinweise

- Die Sektoren sind die von VATSIM. Grenzen und Frequenzen können vom MSFS-ATC abweichen (z. B. Montreal: VATSIM
  132.625/133.025, MSFS 132.25/119.4). Für die USA fehlen viele ARTCCs (u. a. ZMP, ZDV, ZLA) – dort gilt eine Frequenz je
  FIR aus `atc-templates.json`.
- VAT-Spy-Grenzen überlappen sich teilweise (VATSIM-Delegationen, z. B. reicht CZQX bis hinter Wabush). Welche FIRs die
  Route durchfliegt, gibt deshalb das OFP vor; VAT-Spy bestimmt nur, wo die Grenze liegt.
- Korrekturen gehören in `atc-templates.json` (Namen, Frequenzen, Rufzeichen) – die Daten hier bleiben unverändert.
