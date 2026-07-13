#!/bin/bash
#
# Regenerate every raster the app ships from the brand masters.
#   - Wordmark comes from the SVG in docs/brand/svg (its "b" needs a punched hole).
#   - Mascots and the cat-head icon come from the faithful raster cutouts in
#     docs/brand/raster (the original art, kept exact, no vector redraw).
#
# Run this whenever a master changes; commit the resulting PNGs.
#
# Requires: an SVG->PNG renderer + ImageMagick 7 + pngquant.
#   Renderer: cairosvg (`pip install cairosvg`) or librsvg (RSVG=rsvg-convert).
#   Override: CAIROSVG=/path/to/cairosvg docs/brand/tools/export.sh
#
set -euo pipefail
cd "$(dirname "$0")/../../.."           # repo root
SVG=docs/brand/svg
RAS=docs/brand/raster
OUT=Boxy.Web/wwwroot/img
CREAM='#F3EAD8'
# Head crop box within the sitting cutout (source coords, 1254x813): x,y,w,h + square pad
HEAD_CROP="556x442+350+153"; HEAD_SQUARE="624x624"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
CAIROSVG="${CAIROSVG:-cairosvg}"; RSVG="${RSVG:-}"

render() { # svg out width
  if [ -n "$RSVG" ]; then "$RSVG" -w "$3" "$1" -o "$2"; else "$CAIROSVG" "$1" -o "$2" --output-width "$3"; fi
}
svg_h() { render "$1" "$TMP/r.png" 2600; magick "$TMP/r.png" -trim +repage -resize "x${3}" "$2"; }   # SVG, fit height
ras_w() { magick "$1" -trim +repage -resize "${3}x" "$2"; }                                          # raster, fit width
head_icon() { magick "$RAS/boxy-mascot-sitting.png" -crop "$HEAD_CROP" +repage \
                -background none -gravity center -extent "$HEAD_SQUARE" -resize "${2}x${2}" "$1"; }

echo "wordmark (cube SVG, punched b-hole)"
svg_h "$SVG/boxy-logo.svg"         "$OUT/boxy-logo.png"       300   # navy, on light
svg_h "$SVG/boxy-logo-on-dark.svg" "$OUT/boxy-logo-light.png" 300   # white, on dark

echo "mascots (faithful raster)"
ras_w "$RAS/boxy-mascot-sitting.png" "$OUT/boxy-cat.png"      492
ras_w "$RAS/boxy-mascot-sitting.png" "$OUT/boxy-cat-sm.png"   262
ras_w "$RAS/boxy-mascot-playing.png" "$OUT/boxy-chase.png"    560
ras_w "$RAS/boxy-mascot-playing.png" "$OUT/boxy-chase-sm.png" 192

echo "icons (cat head, raster)"
head_icon "$OUT/apple-touch-icon.png" 180
head_icon "$OUT/favicon.png"          96

echo "README logo (sitting cat, raster)"
ras_w "$RAS/boxy-mascot-sitting.png" docs/logo.png 492

echo "OG card (raster cat + cube wordmark on cream)"
magick "$RAS/boxy-mascot-sitting.png" -trim +repage -resize x330 "$TMP/og-cat.png"
svg_h "$SVG/boxy-logo.svg" "$TMP/og-wm.png" 92
magick -size 1200x630 "xc:$CREAM" \
  "$TMP/og-cat.png" -gravity North -geometry +0+70  -composite \
  "$TMP/og-wm.png"  -gravity North -geometry +0+452 -composite \
  "$OUT/og-default.png"

echo "optimize"
for f in "$OUT"/boxy-logo.png "$OUT"/boxy-logo-light.png "$OUT"/boxy-cat.png "$OUT"/boxy-cat-sm.png \
         "$OUT"/boxy-chase.png "$OUT"/boxy-chase-sm.png "$OUT"/apple-touch-icon.png "$OUT"/favicon.png \
         "$OUT"/og-default.png docs/logo.png; do
  pngquant --quality=90-100 --skip-if-larger --force --ext .png "$f" 2>/dev/null || true
done
echo "done."
