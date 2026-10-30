#!/usr/bin/env python3
"""
Erzeugt src/Duopilo/data/sectors/atc-sectors.json aus dem VATGlasses-Datenprojekt (VATSIM-Sektordaten).

Duopilo braucht davon nur die Streckensektoren (Positionen vom Typ CTR/FSS mit VHF-Frequenz): Polygon, Höhenband,
Frequenz, Rufname der Station und die FIR, zu der der Sektor gehört. Die FIR-Grenzen selbst kommen aus VAT-Spy
(src/Duopilo/data/vatspy), die Zuordnung Sektor → FIR läuft über die Gruppe des Sektors, das Rufzeichen-Präfix der
Position (VATSpy.dat) oder – als letzte Möglichkeit – über die Lage des Sektors.

Aufruf (Python 3.9+, keine weiteren Pakete):

    git clone --depth 1 https://github.com/lennycolton/vatglasses-data
    git clone --depth 1 https://github.com/vatsimnetwork/vatspy-data-project
    python tools/import_sectors.py --vatglasses vatglasses-data --vatspy vatspy-data-project

Lizenz der Daten: VATGlasses Data Project, CC BY-NC-SA 4.0 – die erzeugte Datei steht unter derselben Lizenz.
"""

from __future__ import annotations

import argparse
import datetime
import json
import math
import os
import re
import subprocess
import sys

ENROUTE_TYPES = {"CTR", "FSS"}
COORD = re.compile(r"^(-?)(\d+)$")
ICAO_FIR = re.compile(r"^[A-Z]{4}$")


def dms(token: str, degree_digits: int) -> float:
    """VATGlasses-Koordinate: Breite DDMMSS, Länge DDDMMSS, Süd/West mit Minus."""
    m = COORD.match(str(token).strip())
    if not m or len(m.group(2)) != degree_digits + 4:
        raise ValueError(f"Koordinate nicht lesbar: {token!r}")
    digits = m.group(2)
    value = int(digits[:degree_digits]) + int(digits[degree_digits:degree_digits + 2]) / 60 + int(digits[degree_digits + 2:]) / 3600
    return -value if m.group(1) else value


def frequency(text) -> float | None:
    try:
        mhz = float(str(text).replace(",", "."))
    except (TypeError, ValueError):
        return None
    return mhz if 118.0 <= mhz < 137.0 else None


def simplify(points: list[tuple[float, float]], tolerance: float) -> list[tuple[float, float]]:
    """Douglas-Peucker auf einem offenen Linienzug (Grad, für kleine Toleranzen ausreichend)."""
    if len(points) < 3:
        return points
    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    stack = [(0, len(points) - 1)]
    while stack:
        first, last = stack.pop()
        (x1, y1), (x2, y2) = points[first], points[last]
        dx, dy = x2 - x1, y2 - y1
        length = math.hypot(dx, dy)
        best, index = -1.0, -1
        for i in range(first + 1, last):
            px, py = points[i]
            d = math.hypot(px - x1, py - y1) if length == 0 else abs(dy * px - dx * py + x2 * y1 - y2 * x1) / length
            if d > best:
                best, index = d, i
        if best > tolerance and index > 0:
            keep[index] = True
            stack.append((first, index))
            stack.append((index, last))
    return [p for p, k in zip(points, keep) if k]


def ring(raw_points, tolerance: float) -> list[tuple[float, float]]:
    points: list[tuple[float, float]] = []
    for p in raw_points:
        lat, lon = dms(p[0], 2), dms(p[1], 3)
        if not points or (abs(points[-1][0] - lon) > 1e-9 or abs(points[-1][1] - lat) > 1e-9):
            points.append((lon, lat))
    if len(points) > 1 and points[0] == points[-1]:
        points.pop()
    if len(points) < 3:
        return []
    # Geschlossen vereinfachen: am weitesten entfernten Punkt teilen, damit der Ring nicht zusammenfällt
    far = max(range(len(points)), key=lambda i: (points[i][0] - points[0][0]) ** 2 + (points[i][1] - points[0][1]) ** 2)
    a = simplify(points[: far + 1], tolerance)
    b = simplify(points[far:] + [points[0]], tolerance)
    result = a[:-1] + b[:-1]
    return result if len(result) >= 3 else points


def inside(points, lon, lat) -> bool:
    c = False
    j = len(points) - 1
    for i in range(len(points)):
        xi, yi = points[i]
        xj, yj = points[j]
        if (yi > lat) != (yj > lat) and lon < (xj - xi) * (lat - yi) / (yj - yi) + xi:
            c = not c
        j = i
    return c


def area(points) -> float:
    return abs(sum(points[i - 1][0] * points[i][1] - points[i][0] * points[i - 1][1] for i in range(len(points)))) / 2


def load_vatspy(path: str):
    """FIR-Polygone (oberste Ebene) und Rufzeichen-Präfix → FIR aus VATSpy.dat/Boundaries.geojson."""
    prefixes: dict[str, str] = {}
    firs: set[str] = set()
    section = None
    with open(os.path.join(path, "VATSpy.dat"), encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith(";"):
                continue
            if line.startswith("["):
                section = line
                continue
            if section == "[FIRs]":
                parts = line.split("|")
                if len(parts) >= 4 and ICAO_FIR.match(parts[0]):
                    firs.add(parts[0])
                    if parts[2]:
                        prefixes.setdefault(parts[2].upper(), parts[0])
                    prefixes.setdefault(parts[0], parts[0])

    with open(os.path.join(path, "Boundaries.geojson"), encoding="utf-8") as f:
        geo = json.load(f)
    polygons = []
    for feature in geo["features"]:
        fid = feature["properties"]["id"]
        if fid not in firs:
            continue
        geometry = feature["geometry"]
        parts = geometry["coordinates"] if geometry["type"] == "MultiPolygon" else [geometry["coordinates"]]
        for part in parts:
            outer = [(c[0], c[1]) for c in part[0]]
            xs = [p[0] for p in outer]
            ys = [p[1] for p in outer]
            polygons.append((fid, outer, (min(xs), max(xs), min(ys), max(ys)), area(outer)))
    return firs, prefixes, polygons


def load_dataset(path: str):
    """Ein Datensatz: einzelne JSON-Datei oder Ordner mit airspace.json, positions.json und ownership/default.json."""
    if os.path.isdir(path):
        with open(os.path.join(path, "airspace.json"), encoding="utf-8") as f:
            airspace_doc = json.load(f)
        with open(os.path.join(path, "positions.json"), encoding="utf-8") as f:
            positions = json.load(f)["positions"]
        ownership = {}
        default = os.path.join(path, "ownership", "default.json")
        if os.path.exists(default):
            with open(default, encoding="utf-8") as f:
                ownership = json.load(f).get("airspace", {})
        raw = airspace_doc["airspace"]
        items = []
        for uid, a in (raw.items() if isinstance(raw, dict) else ((x.get("uid") or x.get("id"), x) for x in raw)):
            a = dict(a)
            a.setdefault("uid", uid)
            a["owner"] = a.get("owner") or ownership.get(uid) or []
            items.append(a)
        return items, positions
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    return doc.get("airspace", []), doc.get("positions", {})


def git_head(path: str) -> str:
    try:
        return subprocess.check_output(["git", "-C", path, "rev-parse", "HEAD"], text=True).strip()
    except (OSError, subprocess.CalledProcessError):
        return ""


def main() -> int:
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--vatglasses", required=True, help="Checkout von github.com/lennycolton/vatglasses-data")
    parser.add_argument("--vatspy", default=os.path.join(repo, "src", "Duopilo", "data", "vatspy"),
                        help="Ordner mit VATSpy.dat und Boundaries.geojson (Standard: die mitgelieferten)")
    parser.add_argument("--out", default=os.path.join(repo, "src", "Duopilo", "data", "sectors", "atc-sectors.json"))
    parser.add_argument("--tolerance", type=float, default=0.002, help="Vereinfachung der Polygone in Grad (Standard 0.002 ≈ 0,12 NM)")
    args = parser.parse_args()

    firs, prefixes, fir_polygons = load_vatspy(args.vatspy)
    data_dir = os.path.join(args.vatglasses, "data")
    sectors = []
    unassigned = 0
    skipped = 0
    for name in sorted(os.listdir(data_dir)):
        path = os.path.join(data_dir, name)
        if name == "nodata.json" or not (os.path.isdir(path) or name.endswith(".json")):
            continue
        try:
            airspace, positions = load_dataset(path)
        except (OSError, KeyError, ValueError) as e:
            print(f"übersprungen: {name}: {e}", file=sys.stderr)
            continue

        for a in airspace:
            owner = a.get("owner") or []
            position = positions.get(owner[0]) if owner else None
            if not position or position.get("type") not in ENROUTE_TYPES:
                continue
            mhz = frequency(position.get("frequency"))
            if mhz is None:
                skipped += 1
                continue

            group = str(a.get("group") or "").upper()
            pre = position.get("pre") or []
            if isinstance(pre, str):
                pre = [pre]
            fir = group if ICAO_FIR.match(group) else next((prefixes[p.upper()] for p in pre if p.upper() in prefixes), "")

            for volume in a.get("sectors", []):
                try:
                    points = ring(volume.get("points", []), args.tolerance)
                except ValueError as e:
                    print(f"{name}/{a.get('id')}: {e}", file=sys.stderr)
                    continue
                if not points:
                    continue
                volume_fir = fir
                if not volume_fir:
                    # Lage: kleinste FIR, die den Schwerpunkt enthält
                    cx = sum(p[0] for p in points) / len(points)
                    cy = sum(p[1] for p in points) / len(points)
                    hits = [(ar, fid) for fid, outer, (x0, x1, y0, y1), ar in fir_polygons
                            if x0 <= cx <= x1 and y0 <= cy <= y1 and inside(outer, cx, cy)]
                    volume_fir = min(hits)[1] if hits else ""
                    if not volume_fir:
                        unassigned += 1
                sectors.append({
                    "fir": volume_fir,
                    "id": str(a.get("uid") or a.get("id") or owner[0]),
                    "position": str(owner[0]),
                    "name": str(position.get("callsign") or "").strip(),
                    "frequency": f"{mhz:.3f}",
                    "minFl": int(volume.get("min", 0) or 0),
                    "maxFl": int(volume.get("max", 999) or 999),
                    "points": [round(v, 4) for p in points for v in p],
                })

    sectors.sort(key=lambda s: (s["fir"], s["id"], s["minFl"]))
    doc = {
        "_hinweis": "Erzeugt mit tools/import_sectors.py – nicht von Hand bearbeiten. Eigene Korrekturen gehören in atc-templates.json.",
        "source": "VATGlasses Data Project, https://github.com/lennycolton/vatglasses-data",
        "sourceCommit": git_head(args.vatglasses),
        "license": "CC BY-NC-SA 4.0 (https://creativecommons.org/licenses/by-nc-sa/4.0/)",
        "generated": datetime.date.today().isoformat(),
        "format": "points: Länge, Breite abwechselnd (Grad); minFl/maxFl in Flight Levels",
        "sectors": sectors,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("{\n")
        keys = [k for k in doc if k != "sectors"]
        for k in keys:
            f.write(f"  {json.dumps(k)}: {json.dumps(doc[k], ensure_ascii=False)},\n")
        f.write('  "sectors": [\n')
        f.write(",\n".join("    " + json.dumps(s, ensure_ascii=False, separators=(",", ":")) for s in sectors))
        f.write("\n  ]\n}\n")

    firs_with_sectors = len({s["fir"] for s in sectors if s["fir"]})
    print(f"{len(sectors)} Sektoren in {firs_with_sectors} FIRs → {args.out} "
          f"({os.path.getsize(args.out) / 1e6:.1f} MB; ohne FIR: {unassigned}, ohne VHF-Frequenz: {skipped})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
