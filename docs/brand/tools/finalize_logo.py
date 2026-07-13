#!/usr/bin/env python3
"""Finalize a boxy wordmark:
  - punch the 'b' bowl counter as a REAL transparent hole (even-odd subpath in the navy 'b')
  - ensure a viewBox
  - emit a color version and an on-dark version (navy letters -> white)
Usage: finalize_logo.py snapped.svg out_color.svg out_ondark.svg
"""
import re, sys

NAVY = "#313A4A"
src, out_color, out_dark = sys.argv[1], sys.argv[2], sys.argv[3]
svg = open(src).read()

def ensure_viewbox(s):
    m = re.search(r'<svg\b[^>]*>', s); tag = m.group(0)
    if 'viewBox' in tag: return s
    w = re.search(r'width="(\d+(?:\.\d+)?)"', tag); h = re.search(r'height="(\d+(?:\.\d+)?)"', tag)
    if w and h:
        return s.replace(tag, tag[:-1] + f' viewBox="0 0 {w.group(1)} {h.group(1)}">', 1)
    return s
svg = ensure_viewbox(svg)

def translate_of(el):
    tm = re.search(r'translate\(([-\d.]+)[ ,]+([-\d.]+)\)', el)
    return (float(tm.group(1)), float(tm.group(2))) if tm else (0.0, 0.0)

def coords(el):
    tx, ty = translate_of(el)
    d = re.search(r'\bd="([^"]+)"', el).group(1)
    n = [float(x) for x in re.findall(r'-?\d+\.?\d*', d)]
    xs = [v + tx for v in n[0::2]]; ys = [v + ty for v in n[1::2]]
    return xs, ys

def offset_d(d, dx, dy):
    out = []; last = 0; idx = 0
    for m in re.finditer(r'-?\d+\.?\d*', d):
        out.append(d[last:m.start()])
        v = float(m.group()) + (dx if idx % 2 == 0 else dy)
        out.append(f"{v:.2f}")
        last = m.end(); idx += 1
    out.append(d[last:])
    return "".join(out)

paths = list(re.finditer(r'<path\b[^>]*?/>', svg, re.DOTALL))

# 1) counter = leftmost white path
whites = []
for m in paths:
    el = m.group(0)
    if re.search(r'fill="#(?:fff|ffffff|FFF|FFFFFF)"', el):
        tx, _ = translate_of(el)
        whites.append((tx, el))
whites.sort(key=lambda t: t[0])
counter_el = whites[0][1] if whites else None

if counter_el:
    cxs, cys = coords(counter_el)
    ccx, ccy = (min(cxs)+max(cxs))/2, (min(cys)+max(cys))/2
    ctx, cty = translate_of(counter_el)
    counter_d = re.search(r'\bd="([^"]+)"', counter_el).group(1)
    # 2) navy path containing the counter center = the 'b'
    b_el = None
    for m in paths:
        el = m.group(0)
        if re.search(rf'fill="{NAVY}"', el, re.I):
            xs, ys = coords(el)
            if min(xs) < ccx < max(xs) and min(ys) < ccy < max(ys):
                b_el = el; break
    if b_el:
        ntx, nty = translate_of(b_el)
        # counter into b-local space, append as subpath, even-odd => hole
        counter_local = offset_d(counter_d, ctx - ntx, cty - nty)
        b_d = re.search(r'\bd="([^"]+)"', b_el).group(1)
        new_b = b_el
        new_b = new_b.replace(f'd="{b_d}"', f'd="{b_d} {counter_local}"', 1)
        if 'fill-rule' not in new_b:
            new_b = new_b.replace('<path', '<path fill-rule="evenodd"', 1)
        svg = svg.replace(b_el, new_b, 1)
        svg = svg.replace(counter_el, "", 1)
        print(f"punched b-counter hole (center {ccx:.0f},{ccy:.0f})")
    else:
        print("WARN: no navy 'b' path found containing counter; leaving as-is")

svg = re.sub(r'\n\s*\n', '\n', svg)
open(out_color, "w").write(svg)
open(out_dark, "w").write(re.sub(re.escape(NAVY), '#FFFFFF', svg, flags=re.IGNORECASE))
print(f"wrote {out_color} and {out_dark}")
