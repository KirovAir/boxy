# Boxy brand assets

Source of truth for the Boxy logo, wordmark, mascots and icons.

- The **wordmark** ships from the vector masters in [`svg/`](svg) (its "b" needs a real
  punched hole, which only vector gives cleanly).
- The **mascots and the cat-head icon** ship from the faithful raster cutouts in
  [`raster/`](raster). Vector-tracing the detailed mascots introduced tiny edge artifacts
  (ear tips especially), so we keep the original art exactly and just remove the background
  and resize. The vector mascot traces are still in `svg/` for reference, but are not shipped.

Everything the app ships (`Boxy.Web/wwwroot/img/*.png` and `docs/logo.png`) is rendered by
[`tools/export.sh`](tools/export.sh). Never hand-edit a PNG: change the master, re-run the
export, commit the PNGs.

## Palette

| Role | Hex |
|------|-----|
| Orange (primary) | `#FFA519` |
| Navy / ink | `#313A4A` |
| Cream (folder tab) | `#FFDEAD` |
| Dark orange (shadows) | `#DE7B19` |
| OG card background | `#F3EAD8` |

Every master uses exactly these values (tracing samples approximate colours, so fills
are snapped back to the canonical hex).

## Masters (`svg/`)

| File | What it is |
|------|-----------|
| `boxy-logo.svg` / `boxy-logo-on-dark.svg` | Wordmark, cube style. Navy for light backgrounds, white for dark. The "b" bowl is a real transparent hole. |
| `boxy-logo-play.svg` / `boxy-logo-play-on-dark.svg` | Wordmark, play-button style. Kept as an alternative. |
| `boxy-mascot-sitting.svg` | Box-cat sitting. |
| `boxy-mascot-playing.svg` | Box-cat chasing a ball of wool. |
| `boxy-mascot-head.svg` | Just the cat face in the box. |
| `boxy-mascot-head-square.svg` | Head centred in a square, for icons. |
| `boxy-icon-play.svg` | The play-in-box mark (alternative app icon). |

`sources/` holds the original AI-generated PNGs the masters were traced from.

## Where each raster is used

`tools/export.sh` writes these; the mapping is the contract:

| App file | Rendered from | Size | Used in |
|----------|---------------|------|---------|
| `img/boxy-logo.png` | `svg/boxy-logo.svg` | height 300, navy | Login, public upload header |
| `img/boxy-logo-light.png` | `svg/boxy-logo-on-dark.svg` | height 300, white | app top bar, share page |
| `img/boxy-cat.png` / `-sm.png` | `raster/boxy-mascot-sitting.png` | w 492 / 262 | login, empty states |
| `img/boxy-chase.png` / `-sm.png` | `raster/boxy-mascot-playing.png` | w 560 / 192 | status/404 page, footer |
| `img/favicon.png` | `raster/boxy-mascot-sitting.png` (head crop) | 96x96 | `<link rel="icon">` |
| `img/apple-touch-icon.png` | `raster/boxy-mascot-sitting.png` (head crop) | 180x180 | `<link rel="apple-touch-icon">` |
| `img/og-default.png` | raster cat + cube wordmark on cream | 1200x630 | default social card |
| `docs/logo.png` | `raster/boxy-mascot-sitting.png` | w 492 | repo README |

The wordmark is sized by CSS height (`.brand-logo`), the mascots by width, so exact
pixel dimensions are not load-bearing; aspect ratio is. The cat-head icon is cropped from
the sitting cutout (`HEAD_CROP` in `export.sh`) and padded to a square.

## Regenerating the rasters

```bash
# deps: an SVG renderer + ImageMagick 7 + pngquant
pip install cairosvg          # or: brew install librsvg  (then RSVG=rsvg-convert)
brew install imagemagick pngquant

docs/brand/tools/export.sh    # rewrites every app raster from svg/
```

Override the renderer if `cairosvg` is not on PATH:
`CAIROSVG=/path/to/cairosvg docs/brand/tools/export.sh`.

## The tracing pipeline (new illustration -> clean SVG master)

How the masters were built from the flat AI PNGs in `sources/`. Tools live in
[`tools/`](tools); the vector tracer is [VTracer](https://github.com/visioncortex/vtracer)
0.6.4 (download the native binary, do not use the Python wheel, see gotchas).

1. **Cut out the background by connectivity, not by colour.** A global "make white
   transparent" punches holes in the white *inside* the art (cat paws, cloud badge,
   folder tab). Flood-fill from the border instead so only the connected background goes:
   `magick in.png -alpha set -bordercolor white -border 1 -fuzz 8% -fill none -draw "alpha 0,0 floodfill" -shave 1x1 ...`

2. **Supersample and erode (mascots).** Tracing at source resolution leaves a wavy wobble
   on diagonal edges (ears) and a lumpy yarn ball. Upscale 4x first so edges are sub-pixel
   accurate. That introduces a second problem: the navy-against-white edge ramp quantises to
   cream, giving a pale halo on the fur. Fix it by eroding the alpha silhouette a few px
   before compositing (`-channel A -morphology Erode Octagon:3 +channel`); this trims the
   contaminated ring without touching any internal detail. See `tools/refine.sh`.

3. **Quantise to the exact palette, then trace.** Snap to the brand swatch with dithering
   off (`+dither -remap tools/palette_key.png`) so anti-aliasing does not spawn hundreds of
   sliver paths, then trace:
   `vtracer -i quant.png -o out.svg --colormode color --mode spline --hierarchical stacked --filter_speckle 16 --color_precision 8 --gradient_step 0 --corner_threshold 60 --segment_length 4 --path_precision 2`
   Use `--hierarchical cutout` instead of `stacked` for tight crops where the largest region
   touches the frame (the head icon), otherwise the background layer can swallow it.

4. **Snap, punch, optimise.** `tools/snap_svg.py` drops the magenta chroma-key background and
   resets every fill to the canonical brand hex. `tools/finalize_logo.py` turns the "b" bowl
   into a real transparent hole (appends the counter as an even-odd subpath in the navy "b",
   not just deleting a white fill on top) and emits the on-dark variant. Finish with
   `svgo --multipass -p 2`.

### Gotchas / lessons learned

- **VTracer segfaults as a Python wheel on Python 3.14.** Use the standalone native binary.
- **Chroma-key with magenta, and match the whole family.** VTracer can emit the background as
  dimmed magenta (`#400040`), not pure `#FF00FF`; `snap_svg.py` treats any "green far below
  red and blue" as the key so it is removed reliably.
- **Flatten alpha first.** If a source PNG already has an alpha channel, flatten it over white
  (`-background white -flatten -alpha off`) before the flood-fill or the key leaks.
- **AI matting is the wrong tool here.** rembg with `isnet-anime` erased the flat art; `birefnet`
  and `u2net` work but are softer than the classical cut. Vector is the right call for flat art.
- **Deleting the white counter is not a hole.** It just exposes the navy underneath; you have to
  subtract it (even-odd), which is what step 4 does.

## Tools

`tools/` contains `snap_svg.py`, `finalize_logo.py`, `refine.sh`, `add_vb.py`,
`palette_key.png` and `export.sh`. External: VTracer 0.6.4 (native), ImageMagick 7, SVGO,
pngquant, and an SVG renderer (cairosvg or librsvg).
