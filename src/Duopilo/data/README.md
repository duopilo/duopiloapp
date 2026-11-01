# Airspace and airline data

Data for the Duopilo ATC. Duopilo reads it when the ATC session starts. Your own, newer copy in
`%LOCALAPPDATA%\Duopilo\data\…` (same folder structure) takes precedence over the bundled files and survives
every update. Then run `atc_control reload`.

| Folder | Contents | Source | License |
|---|---|---|---|
| `vatspy/` | FIR boundaries (`Boundaries.geojson`) and FIR names (`VATSpy.dat`), unmodified original format | [VAT-Spy Data Project](https://github.com/vatsimnetwork/vatspy-data-project), revision `10fbcea` (2026-10-27) | CC BY-SA 4.0 (`vatspy/LICENSE`) |
| `sectors/` | En-route sectors with altitude band, frequency and callsign (`atc-sectors.json`), generated with `tools/import_sectors.py` | [VATGlasses Data Project](https://github.com/lennycolton/vatglasses-data) (VATSIM sector data), revision `5946a1c` (2026-10-29) | CC BY-NC-SA 4.0 (`sectors/LICENSE`) – non-commercial only |
| `openflights/` | Airlines with ICAO code and telephony callsign (`airlines.dat`, original format) | [OpenFlights](https://openflights.org/data.php), revision `e539814` (2026-10-23) | Open Database License (ODbL) 1.0, contents under the Database Contents License |
| `traffic/` | Runway rules for Duopilo's own traffic (`runway-rules.json`): which runway an aircraft may use for landing or takeoff, by operation, category, ICAO type and wake category. Read at every traffic test start; your own copy replaces the rules of an airport | Duopilo (EDDF: Landebahn Nordwest landings only without B747, A380, MD-11, An-124, L-1011; Startbahn West takeoffs only) | MIT (Duopilo) |

## Updating

VAT-Spy and OpenFlights: copy the files from the respective repository unchanged to here (or to
`%LOCALAPPDATA%\Duopilo\data\vatspy` or `…\openflights`).

Sectors:

```
git clone --depth 1 https://github.com/lennycolton/vatglasses-data
python tools/import_sectors.py --vatglasses vatglasses-data
```

The script only takes en-route positions (CTR/FSS) with a VHF frequency, assigns each sector to a FIR (group,
callsign prefix from `VATSpy.dat`, or location) and simplifies the polygons to about 0.1 nm.

## Notes

- The sectors are VATSIM's. Boundaries and frequencies can differ from the MSFS ATC (e.g. Montreal: VATSIM
  132.625/133.025, MSFS 132.25/119.4). Many US ARTCCs are missing (including ZMP, ZDV, ZLA) – there, one frequency per
  FIR from `atc-templates.json` applies.
- VAT-Spy boundaries partly overlap (VATSIM delegations, e.g. CZQX extends beyond Wabush). So the OFP determines which
  FIRs the route passes through; VAT-Spy only determines where the boundary lies.
- Corrections belong in `atc-templates.json` (names, frequencies, callsigns) – the data here stays unmodified.
