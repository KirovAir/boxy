#!/bin/bash
# refine <outname> <input.png> [supersample%] [erode]  -> fix/<outname>.svg
set -e
cd "$(dirname "$0")"
PY=/opt/homebrew/bin/python3.12
out="$1"; inpng="$2"; SS="${3:-400}"; ERO="${4:-3}"
magick "$inpng" -filter Lanczos -resize ${SS}% \
   -alpha set -bordercolor white -border 1 -fuzz 8% -fill none -draw "alpha 0,0 floodfill" -shave 1x1 \
   -channel A -morphology Erode Octagon:${ERO} +channel \
   -background magenta -flatten fix/${out}_qin.png
magick fix/${out}_qin.png +dither -remap palette_key.png fix/${out}_q.png
./vtracer -i fix/${out}_q.png -o fix/${out}_raw.svg \
   --colormode color --mode spline --hierarchical stacked \
   --filter_speckle 16 --color_precision 8 --gradient_step 0 \
   --corner_threshold 60 --segment_length 4 --path_precision 2 >/dev/null
$PY snap_svg.py fix/${out}_raw.svg fix/${out}_snap.svg >/dev/null
$PY add_vb.py fix/${out}_snap.svg >/dev/null
npx --yes svgo@latest --multipass -p 2 -i fix/${out}_snap.svg -o fix/${out}.svg >/dev/null 2>&1
echo "  refined $out -> $(wc -c < fix/${out}.svg) bytes"
