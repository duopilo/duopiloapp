#!/usr/bin/env python3
"""
Erzeugt src/Duopilo/data/ourairports/airports.csv aus OurAirports (airports.csv) – die Einordnung aller Flughäfen für
Duopilos eigenen Verkehr: Art des Platzes, Linienverkehr ja/nein und ob es ein Militärflugplatz ist.

Spalten der erzeugten Datei: ident, kind, scheduled, military
- ident: ICAO-Code, sonst GPS-Code, sonst die OurAirports-Kennung (MSFS nutzt dieselben Kennungen); ein Platz kann unter
  mehreren Kennungen stehen
- kind: L (large_airport), M (medium_airport), S (small_airport), H (heliport), W (seaplane_base), B (balloonport),
  C (closed)
- scheduled: 1 = Linienverkehr (scheduled_service)
- military: 1 = Militärflugplatz nach dem Namen ("Air Base", "AFB", "RAF", "Army Airfield", "Naval Air Station", ...);
  Plätze mit Linienverkehr gelten nie als militärisch (Frankfurt hieß einmal auch "Rhein-Main Air Base")

Aufruf (Python 3.9+, keine weiteren Pakete):

    git clone --depth 1 https://github.com/davidmegginson/ourairports-data
    python tools/import_airports.py --ourairports ourairports-data

Lizenz der Daten: OurAirports, gemeinfrei (Public Domain) – die erzeugte Datei ebenso.
"""

from __future__ import annotations

import argparse
import csv
import os
import re
import subprocess
import sys

KINDS = {
    "large_airport": "L",
    "medium_airport": "M",
    "small_airport": "S",
    "heliport": "H",
    "seaplane_base": "W",
    "balloonport": "B",
    "closed": "C",
}

MILITARY = re.compile(
    r"\b(air ?base|air force base|afb|air force|army air ?field|army airfield|army air base|army heliport|naval air station|"
    r"naval air ?base|naval airbase|naval air facility|nas|marine corps air station|mcas|raf|air station|military|militar|"
    r"fliegerhorst|heeresflugplatz|marinefliegerhorst|base a[ée]rienne|base a[ée]rea|ba \d+|aeronautica militare|"
    r"air national guard|ang base|joint base|jb)\b",
    re.IGNORECASE,
)

IDENT = re.compile(r"^[A-Z0-9]{3,4}$")


def revision(repo: str) -> str:
    try:
        return subprocess.run(["git", "-C", repo, "log", "-1", "--format=%h (%cs)"], capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return "unbekannt"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    parser.add_argument("--ourairports", required=True, help="Ordner mit airports.csv (Klon von ourairports-data)")
    parser.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "src", "Duopilo", "data", "ourairports", "airports.csv"))
    args = parser.parse_args()

    source = os.path.join(args.ourairports, "airports.csv")
    rows: dict[str, tuple[str, str, str]] = {}
    with open(source, encoding="utf-8", newline="") as f:
        for r in csv.DictReader(f):
            kind = KINDS.get(r["type"])
            if kind is None:
                continue
            scheduled = "1" if r["scheduled_service"] == "yes" else "0"
            military = "1" if scheduled == "0" and MILITARY.search(r["name"]) else "0"
            for ident in (r["icao_code"], r["gps_code"], r["ident"]):
                ident = (ident or "").strip().upper()
                if not IDENT.match(ident):
                    continue
                old = rows.get(ident)
                # Kennung doppelt: der größere bzw. offene Platz gewinnt
                if old is None or "LMSHWBC".index(kind) < "LMSHWBC".index(old[0]):
                    rows[ident] = (kind, scheduled, military)

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# OurAirports {revision(args.ourairports)}, gemeinfrei – erzeugt mit tools/import_airports.py\n")
        f.write("ident,kind,scheduled,military\n")
        for ident in sorted(rows):
            kind, scheduled, military = rows[ident]
            f.write(f"{ident},{kind},{scheduled},{military}\n")

    military = sum(1 for v in rows.values() if v[2] == "1")
    print(f"{len(rows)} Kennungen, davon {military} militärisch -> {os.path.abspath(args.out)}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
