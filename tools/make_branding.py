#!/usr/bin/env python3
"""
Erzeugt Logo, App-Icon, Tray-Icons und Webseiten-Icons von Duopilo aus einer einzigen Geometrie (branding/README.md).

Schreibt nach branding/ sowie src/Duopilo/Assets/duopilo.ico und src/Duopilo/Assets/tray-*.ico. Der Schriftzug
„duopilo“ wird aus B612 Bold (src/Duopilo/Dashboard/fonts) in Pfade umgewandelt, die SVGs brauchen also keine Schrift.

Aufruf (aus dem Repo-Ordner; braucht rsvg-convert, Pillow und fontTools):

    pip install pillow fonttools
    python tools/make_branding.py
"""
import io
import os
import struct
import subprocess
import sys

from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.ttLib import TTFont
from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "src", "Duopilo", "Dashboard", "fonts", "B612-Bold.woff")
OUT = sys.argv[2] if len(sys.argv) > 2 else REPO

# Farben (aus dem Dashboard)
CYAN = "#31c6ee"
CYAN_DARK = "#1e7b94"  # Cyan × 0,62 für hellen Grund
WHITE = "#e9eef1"
INK = "#161c22"
TILE = "#161c22"
TRAY_TILE = "#2a3540"
STATUS = {"waiting": "#93a1ad", "ok": "#3be06a", "caution": "#ffa620", "warning": "#ff3b30"}

# Geometrie im 100er-Raster. Links: Pilot Flying (gefüllt), rechts: Pilot Monitoring (Kontur).
LEFT = "M46 29 L46 70 L15 70 Q10.5 70 11.1 65.5 L15.4 38.6 Q16.1 34.6 20.1 33.9 Z"
RIGHT_OUTLINE = "M56.5 32 L56.5 67.5 L84.5 67.5 Q86.6 67.5 86.4 65.2 L82.2 39 Q81.7 36.5 79.3 36.2 Z"  # Strich 5
RIGHT_OUTLINE_SMALL = "M58 33.6 L58 66 L83.2 66 Q85 66 84.8 64 L80.7 40.5 Q80.2 38 77.8 37.6 Z"  # Strich 8, ≤ 32 px
RIGHT_FILLED = "M54 29 L54 70 L85 70 Q89.5 70 88.9 65.5 L84.6 38.6 Q83.9 34.6 79.9 33.9 Z"  # Tray-Status


def mark(c1, c2, small=False):
    d, w = (RIGHT_OUTLINE_SMALL, 8) if small else (RIGHT_OUTLINE, 5)
    return (f'<path d="{LEFT}" fill="{c1}"/>'
            f'<path d="{d}" fill="none" stroke="{c2}" stroke-width="{w}" stroke-linejoin="round"/>')


def svg(view_box, body, label=None):
    a11y = f' role="img" aria-label="{label}"' if label else ' aria-hidden="true"'
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{view_box}"{a11y}>{body}</svg>\n'


def tile(body_fn, small, fill=TILE, full_bleed=False):
    # Kleine Größen: Marke größer, damit sie bei 16 px noch Pixel bekommt.
    s, t = (1.0, 0) if small else (0.86, 7)
    rx = 0 if full_bleed else 22
    return f'<rect width="100" height="100" rx="{rx}" fill="{fill}"/><g transform="translate({t} {t}) scale({s})">{body_fn(small)}</g>'


def app_icon(small, full_bleed=False):
    return svg("0 0 100 100", tile(lambda sm: mark(CYAN, WHITE, sm), small, full_bleed=full_bleed), "Duopilo")


def tray_icon(status, small):
    def body(sm):
        return f'<path d="{LEFT}" fill="{CYAN}"/><path d="{RIGHT_FILLED}" fill="{STATUS[status]}"/>'
    return svg("0 0 100 100", tile(body, small, fill=TRAY_TILE))


# --- Schriftzug „duopilo“ als Pfade (B612 Bold, mit Kerning aus der kern-Tabelle) ---
font = TTFont(FONT)
upm = font["head"].unitsPerEm
cmap = font.getBestCmap()
gs = font.getGlyphSet()
hmtx = font["hmtx"]
kern = {}
if "kern" in font:
    for table in font["kern"].kernTables:
        kern.update(getattr(table, "kernTable", {}) or {})


def wordmark(text, size, tracking=0.01):
    k = size / upm
    x = 0.0
    paths, xmin, ymin, xmax, ymax = [], None, None, None, None
    names = [cmap[ord(ch)] for ch in text]
    for i, name in enumerate(names):
        pen = SVGPathPen(gs)
        gs[name].draw(pen)
        bp = BoundsPen(gs)
        gs[name].draw(bp)
        if bp.bounds:
            b = bp.bounds
            gx0, gx1 = x + b[0] * k, x + b[2] * k
            gy0, gy1 = -b[3] * k, -b[1] * k
            xmin = gx0 if xmin is None else min(xmin, gx0)
            xmax = gx1 if xmax is None else max(xmax, gx1)
            ymin = gy0 if ymin is None else min(ymin, gy0)
            ymax = gy1 if ymax is None else max(ymax, gy1)
        paths.append(f'<path transform="translate({x:.2f} 0) scale({k:.5f} {-k:.5f})" d="{pen.getCommands()}"/>')
        x += hmtx[name][0] * k + tracking * size
        if i + 1 < len(names):
            x += kern.get((name, names[i + 1]), 0) * k
    return "".join(paths), (xmin, ymin, xmax, ymax)


def lockup(c1, c2, text_color):
    size = 64
    words, (wx0, wy0, wx1, wy1) = wordmark("duopilo", size)
    s = 0.96
    mark_x0, mark_x1 = 10.6 * s, 89.4 * s
    mark_top, mark_bottom = 29 * s, 70 * s
    baseline = -wy0  # oberste Kante des Schriftzugs = 0
    mx = -mark_x0
    my = baseline - mark_bottom  # Unterkante der Marke auf der Grundlinie
    gap = 20
    tx = (mark_x1 - mark_x0) + gap - wx0
    width = tx + wx1
    height = baseline + wy1
    body = (f'<g transform="translate({mx:.2f} {my:.2f}) scale({s})">{mark(c1, c2)}</g>'
            f'<g transform="translate({tx:.2f} {baseline:.2f})" fill="{text_color}">{words}</g>')
    return svg(f"0 0 {width:.1f} {height:.1f}", body, "Duopilo")


def write(rel, text):
    path = os.path.join(OUT, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return path


def render(svg_text, size):
    png = subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size)],
                         input=svg_text.encode(), capture_output=True, check=True).stdout
    return png


def write_png(rel, svg_text, size):
    path = os.path.join(OUT, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(render(svg_text, size))


def write_ico(rel, svg_for_size, sizes):
    """ICO mit PNG-Einträgen (wie die bisherigen Icons), jede Größe einzeln gerendert."""
    pngs = []
    for s in sizes:
        data = render(svg_for_size(s), s)
        Image.open(io.BytesIO(data)).verify()
        pngs.append(data)
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, pngs):
        d = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", d, d, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    path = os.path.join(OUT, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(header + entries + b"".join(pngs))


MARK_BOX = "10 28 80 43"

# Vektor-Dateien
write("branding/duopilo-mark.svg", svg(MARK_BOX, mark(CYAN, WHITE), "Duopilo"))
write("branding/duopilo-mark-light.svg", svg(MARK_BOX, mark(CYAN_DARK, INK), "Duopilo"))
write("branding/duopilo-logo.svg", lockup(CYAN, WHITE, WHITE))
write("branding/duopilo-logo-light.svg", lockup(CYAN_DARK, INK, INK))
write("branding/duopilo-icon.svg", app_icon(False))
write("branding/duopilo-icon-small.svg", app_icon(True))
for st in STATUS:
    write(f"branding/tray/tray-{st}.svg", tray_icon(st, True))

# Webseite
write("branding/web/favicon.svg", app_icon(True))
write_png("branding/web/apple-touch-icon.png", app_icon(False, full_bleed=True), 180)
write_png("branding/web/icon-192.png", app_icon(False), 192)
write_png("branding/web/icon-512.png", app_icon(False), 512)
write_ico("branding/web/favicon.ico", lambda s: app_icon(s <= 32), [16, 32, 48])

# Programm
write_ico("src/Duopilo/Assets/duopilo.ico", lambda s: app_icon(s <= 32), [16, 20, 24, 32, 40, 48, 64, 128, 256])
for st in STATUS:
    write_ico(f"src/Duopilo/Assets/tray-{st}.ico", lambda s, st=st: tray_icon(st, s <= 32), [16, 20, 24, 32, 40, 48])

print("ok")
