#!/usr/bin/env python3
"""
Baut die Webseite duopilo.app aus site/ nach _site/ (oder --out).

- kopiert site/, die Webseiten-Icons aus branding/web/, Logo und Bildmarke aus branding/ und die B612-Schriften
  aus src/Duopilo/Dashboard/fonts – nichts davon liegt doppelt im Repo;
- trägt das neueste veröffentlichte Release ein (Version, Links zu Setup und ZIP). Bevorzugt wird die neueste
  stabile Version, sonst die neueste Vorabversion (Beta). Ohne veröffentlichtes Release zeigt die Seite
  „kommt bald“. Entwürfe (Drafts) zählen nie;
- bricht ab, solange in den Seiten noch „TODO:“ steht (Impressum), außer mit --allow-todo.

Aufruf (Python 3.9+, keine weiteren Pakete):

    python tools/build_site.py                        # Release über die GitHub-API holen
    python tools/build_site.py --no-release --allow-todo
    python -m http.server -d _site 8000               # Vorschau: http://localhost:8000

Umgebungsvariablen: GITHUB_REPOSITORY (Standard duopilo/duopiloapp), GITHUB_TOKEN (optional, gegen das Rate-Limit).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = os.environ.get("GITHUB_REPOSITORY", "duopilo/duopiloapp")
RELEASES_URL = f"https://github.com/{REPO}/releases"

FONTS = ["B612-Regular.woff", "B612-Bold.woff", "B612Mono-Regular.woff", "OFL.txt"]
BRAND = ["duopilo-logo.svg", "duopilo-logo-light.svg", "duopilo-mark.svg"]


def fetch_release(token: str | None) -> dict | None:
    req = urllib.request.Request(
        f"https://api.github.com/repos/{REPO}/releases?per_page=30",
        headers={"Accept": "application/vnd.github+json", "User-Agent": "duopilo-site-build"},
    )
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=30) as resp:
        releases = json.load(resp)
    # Nur Releases mit Installer: Ein gerade veröffentlichtes Release bekommt Setup und ZIP erst vom Release-Workflow
    # (ca. 15 min später) – bis dahin zeigt der Download-Button auf die vorige Version statt ins Leere.
    published = [r for r in releases if not r.get("draft") and asset_url(r, "-Setup.exe")]
    stable = [r for r in published if not r.get("prerelease")]
    return (stable or published or [None])[0]


def asset_url(release: dict, suffix: str) -> str | None:
    for asset in release.get("assets", []):
        if asset["name"].endswith(suffix):
            return asset["browser_download_url"]
    return None


def release_values(release: dict | None) -> dict[str, str]:
    if release is None:
        return {
            "RELEASE_STATE": "no-release",
            "VERSION": "",
            "SETUP_URL": RELEASES_URL,
            "ZIP_URL": RELEASES_URL,
            "RELEASE_URL": RELEASES_URL,
        }
    setup = asset_url(release, "-Setup.exe")
    if setup is None:
        sys.exit(f"::error title=Webseite::Release {release['tag_name']} hat kein *-Setup.exe.")
    state = "has-release is-prerelease" if release.get("prerelease") else "has-release"
    return {
        "RELEASE_STATE": state,
        "VERSION": release["tag_name"].lstrip("v"),
        "SETUP_URL": setup,
        "ZIP_URL": asset_url(release, "-win-x64.zip") or release["html_url"],
        "RELEASE_URL": release["html_url"],
    }


def copy(src: str, dst: str) -> None:
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--out", default=os.path.join(ROOT, "_site"))
    ap.add_argument("--no-release", action="store_true", help="ohne Release bauen („kommt bald“)")
    ap.add_argument("--release-json", help="Release-Daten aus einer Datei statt von der API (zum Testen)")
    ap.add_argument("--allow-todo", action="store_true", help="offene TODO: (Impressum) nur warnen")
    args = ap.parse_args()

    out = os.path.abspath(args.out)
    if os.path.exists(out):
        shutil.rmtree(out)
    shutil.copytree(os.path.join(ROOT, "site"), out, ignore=shutil.ignore_patterns("README.md"))

    web = os.path.join(ROOT, "branding", "web")
    for name in os.listdir(web):
        copy(os.path.join(web, name), os.path.join(out, name))
    for name in BRAND:
        copy(os.path.join(ROOT, "branding", name), os.path.join(out, "assets", "brand", name))
    for name in FONTS:
        copy(os.path.join(ROOT, "src", "Duopilo", "Dashboard", "fonts", name), os.path.join(out, "assets", "fonts", name))

    if args.no_release:
        release = None
    elif args.release_json:
        with open(args.release_json, encoding="utf-8") as f:
            release = json.load(f)
    else:
        try:
            release = fetch_release(os.environ.get("GITHUB_TOKEN"))
        except (urllib.error.URLError, TimeoutError) as e:
            sys.exit(f"::error title=Webseite::Releases nicht abrufbar: {e}")
    values = release_values(release)

    todos = []
    for folder, _, files in os.walk(out):
        for name in files:
            if not name.endswith(".html"):
                continue
            path = os.path.join(folder, name)
            with open(path, encoding="utf-8") as f:
                text = f.read()
            for key, value in values.items():
                text = text.replace(f"%%{key}%%", value)
            left = sorted(set(re.findall(r"%%[A-Z_]+%%", text)))
            if left:
                sys.exit(f"::error title=Webseite::{os.path.relpath(path, out)}: unbekannte Platzhalter {', '.join(left)}")
            if "TODO:" in text:
                todos.append(os.path.relpath(path, out))
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)

    if todos:
        kind = "warning" if args.allow_todo else "error"
        print(f"::{kind} title=Webseite::Noch offene TODO: in {', '.join(todos)} (Impressum ausfüllen).")
        if not args.allow_todo:
            sys.exit(1)

    shown = f"{values['VERSION']} ({values['RELEASE_STATE']})" if values["VERSION"] else "kein Release (kommt bald)"
    print(f"Webseite gebaut: {out} – Release: {shown}")


if __name__ == "__main__":
    main()
