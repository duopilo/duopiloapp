# Duopilo branding

The mark shows two cockpit windscreen panes side by side: the **left pane is filled** (pilot flying), the **right pane is an outline** (pilot monitoring). In the tray icon the right pane carries the status.

All files are generated from one geometry by [`tools/make_branding.py`](../tools/make_branding.py) – change the script, not the files.

## Files

| File | Use |
|---|---|
| `duopilo-logo.svg` | Logo (mark + wordmark) on dark backgrounds |
| `duopilo-logo-light.svg` | Logo on light backgrounds |
| `duopilo-mark.svg` / `duopilo-mark-light.svg` | Mark only, dark / light backgrounds |
| `duopilo-icon.svg` | App icon (rounded tile), 48 px and up |
| `duopilo-icon-small.svg` | App icon for 32 px and below (larger mark, thicker outline) |
| `tray/tray-*.svg` | Tray status icons: `waiting`, `ok`, `caution`, `warning` |
| `web/` | Website: `favicon.svg`, `favicon.ico`, `apple-touch-icon.png` (180), `icon-192.png`, `icon-512.png` |

The program icons `src/Duopilo/Assets/duopilo.ico` and `tray-*.ico` come from the same script; each size is rendered separately.

## Colors

Taken from the dashboard.

| Role | Hex |
|---|---|
| Left pane (dark background) | `#31c6ee` |
| Left pane (light background) | `#1e7b94` |
| Right pane, wordmark (dark background) | `#e9eef1` |
| Right pane, wordmark (light background), icon tile | `#161c22` |
| Tray tile | `#2a3540` |
| Tray status: waiting / ok / caution / warning | `#93a1ad` / `#3be06a` / `#ffa620` / `#ff3b30` |

## Wordmark

Lowercase **duopilo** in [B612](https://github.com/polarsys/b612) Bold – the typeface designed for Airbus cockpit displays and already used by the dashboard (SIL Open Font License 1.1). In the SVGs the letters are paths, so no font is needed.

## Usage

- Keep the colors of the two panes; only the tray icon colors the right pane.
- Below 32 px use the small icon variant.
- Keep clear space around the logo of at least the width of one pane.

## Regenerating

```bash
pip install pillow fonttools   # plus rsvg-convert (librsvg)
python tools/make_branding.py
```
