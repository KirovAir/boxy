#!/usr/bin/env python3
"""Post-process a vtracer SVG for flat brand art:
  - snap every fill to the nearest canonical brand hex (fixes vtracer's sampled approximations)
  - drop any path whose nearest color is the magenta chroma-key (the background)
Usage: snap_svg.py in.svg out.svg
"""
import re, sys

# Canonical brand palette. MAGENTA is the chroma key -> becomes transparent (path removed).
PALETTE = {
    "#FFFFFF": (255, 255, 255),
    "#FFA519": (255, 165, 25),
    "#313A4A": (49, 58, 74),
    "#FFDEAD": (255, 222, 173),
    "#DE7B19": (222, 123, 25),
}
MAGENTA = (255, 0, 255)

def dist2(a, b):
    return (a[0]-b[0])**2 + (a[1]-b[1])**2 + (a[2]-b[2])**2

def is_magenta_family(rgb):
    # Chroma key = magenta at ANY brightness: green far below red and blue,
    # red and blue both meaningful (e.g. #FF00FF and vtracer's dimmed #400040).
    r, g, b = rgb
    return r > 55 and b > 55 and g < 0.45 * r and g < 0.45 * b

def nearest(rgb):
    if is_magenta_family(rgb):
        return None  # background key -> remove
    best_hex, best_d = None, 1e18
    for hx, prgb in PALETTE.items():
        d = dist2(rgb, prgb)
        if d < best_d:
            best_d, best_hex = d, hx
    dmag = dist2(rgb, MAGENTA)
    if dmag < best_d:
        return None  # background key -> remove
    return best_hex

def hex2rgb(h):
    h = h.lstrip("#")
    if len(h) == 3:
        h = "".join(c*2 for c in h)
    return (int(h[0:2],16), int(h[2:4],16), int(h[4:6],16))

svg = open(sys.argv[1]).read()

# Split into path elements, decide keep/recolor per path.
path_re = re.compile(r'<path\b[^>]*?/>', re.DOTALL)
fill_re = re.compile(r'fill="(#[0-9a-fA-F]{3,6})"')

removed = 0
recolored = 0
def process(m):
    global removed, recolored
    el = m.group(0)
    fm = fill_re.search(el)
    if not fm:
        return el
    snap = nearest(hex2rgb(fm.group(1)))
    if snap is None:
        removed += 1
        return ""  # drop background path entirely
    if snap.upper() != fm.group(1).upper():
        recolored += 1
        el = fill_re.sub(f'fill="{snap}"', el, count=1)
    return el

out = path_re.sub(process, svg)
# collapse blank lines left by removed paths
out = re.sub(r'\n\s*\n', '\n', out)
open(sys.argv[2], "w").write(out)
print(f"snap_svg: removed {removed} bg path(s), recolored {recolored} fill(s) -> {sys.argv[2]}")
