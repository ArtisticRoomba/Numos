#!/usr/bin/env python3
"""Builds the Cyrillic TTF that the viewer merges into ProggyClean.

The source is the public-domain X.Org misc-fixed 6x13 bitmap font:
https://gitlab.freedesktop.org/xorg/font/misc-misc/-/raw/master/6x13.bdf
Each glyph gets a 7 px advance with the extra column on the left, so the ink sits where ProggyClean puts it.

ImGui rasterizes TTF outlines and ignores embedded bitmaps. Each set pixel therefore becomes a square outline on a
100-unit grid, so the glyphs land on whole pixels at 13 px like ProggyClean.

Usage: build_fixed_cyrillic.py <6x13.bdf> <out.ttf>
"""

import sys

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

UNITS_PER_PIXEL = 100
FIRST_CODEPOINT = 0x0400
LAST_CODEPOINT = 0x04FF
ADVANCE_PIXELS = 7
NOTDEF_ADVANCE_PIXELS = ADVANCE_PIXELS
FAMILY = "Numos Fixed Cyrillic"
STYLE = "Regular"
DESCRIPTION = "Derived from the public-domain X.Org misc-fixed 6x13 font, rebuilt as pixel-square outlines."


def parse_bdf(path):
    """Returns (ascent, descent, glyphs). Each glyph is (advance, w, h, xoff, yoff, rows) keyed by codepoint."""
    ascent = descent = None
    glyphs = {}
    current = None
    in_bitmap = False
    with open(path, "r", encoding="ascii") as handle:
        for raw in handle:
            line = raw.strip()
            if in_bitmap:
                if line == "ENDCHAR":
                    in_bitmap = False
                    if current is not None and current["encoding"] >= 0:
                        glyphs[current["encoding"]] = current
                    current = None
                else:
                    current["rows"].append(int(line, 16) if line else 0)
                    current["row_hex_len"] = len(line)
                continue
            parts = line.split()
            if not parts:
                continue
            key = parts[0]
            if key == "FONT_ASCENT":
                ascent = int(parts[1])
            elif key == "FONT_DESCENT":
                descent = int(parts[1])
            elif key == "STARTCHAR":
                current = {"encoding": -1, "advance": 0, "bbx": (0, 0, 0, 0), "rows": []}
            elif current is not None and key == "ENCODING":
                current["encoding"] = int(parts[1])
            elif current is not None and key == "DWIDTH":
                current["advance"] = int(parts[1])
            elif current is not None and key == "BBX":
                current["bbx"] = tuple(int(p) for p in parts[1:5])
            elif current is not None and key == "BITMAP":
                in_bitmap = True
    if ascent is None or descent is None:
        raise ValueError("BDF has no FONT_ASCENT or FONT_DESCENT")
    return ascent, descent, glyphs


def bitmap_pixels(glyph):
    """Returns the set of (x, y) pixel cells, with x, y in pixels relative to the origin and y up."""
    w, h, xoff, yoff = glyph["bbx"]
    row_bytes = (w + 7) // 8
    pixels = set()
    for r, value in enumerate(glyph["rows"]):
        for c in range(w):
            if (value >> (row_bytes * 8 - 1 - c)) & 1:
                pixels.add((xoff + c, yoff + h - r - 1))
    return pixels


def row_runs(pixels):
    """Merges horizontal runs of pixels into (x0, x1, y) pixel rectangles, one pixel tall."""
    rects = []
    for y in sorted({p[1] for p in pixels}, reverse=True):
        xs = sorted(x for x, py in pixels if py == y)
        start = prev = xs[0]
        for x in xs[1:]:
            if x != prev + 1:
                rects.append((start, prev + 1, y))
                start = x
            prev = x
        rects.append((start, prev + 1, y))
    return rects


def draw_glyph(rects):
    pen = TTGlyphPen(None)
    u = UNITS_PER_PIXEL
    for x0, x1, y in rects:
        # Clockwise is the outer-contour direction in TrueType.
        pen.moveTo((x0 * u, y * u))
        pen.lineTo((x0 * u, (y + 1) * u))
        pen.lineTo((x1 * u, (y + 1) * u))
        pen.lineTo((x1 * u, y * u))
        pen.closePath()
    return pen.glyph()


def glyph_pixels(glyph):
    """Rebuilds the pixel set from a built glyph's contours, asserting every coordinate is on the grid."""
    pixels = set()
    if glyph.numberOfContours <= 0:
        return pixels
    coords, end_pts, _ = glyph.getCoordinates(None)
    start = 0
    for end in end_pts:
        contour = coords[start:end + 1]
        start = end + 1
        assert len(contour) == 4, "expected a rectangle"
        for x, y in contour:
            assert x % UNITS_PER_PIXEL == 0 and y % UNITS_PER_PIXEL == 0, "off-grid coordinate"
        xs = [p[0] for p in contour]
        ys = [p[1] for p in contour]
        x0, x1 = min(xs) // UNITS_PER_PIXEL, max(xs) // UNITS_PER_PIXEL
        y0, y1 = min(ys) // UNITS_PER_PIXEL, max(ys) // UNITS_PER_PIXEL
        assert y1 - y0 == 1, "rectangle is not one pixel tall"
        for x in range(x0, x1):
            assert (x, y0) not in pixels, "overlapping rectangles"
            pixels.add((x, y0))
    return pixels


def build(bdf_path, out_path):
    ascent, descent, bdf_glyphs = parse_bdf(bdf_path)
    u = UNITS_PER_PIXEL
    codepoints = sorted(cp for cp in bdf_glyphs if FIRST_CODEPOINT <= cp <= LAST_CODEPOINT)

    glyph_order = [".notdef"]
    cmap = {}
    glyf = {".notdef": TTGlyphPen(None).glyph()}
    metrics = {".notdef": (NOTDEF_ADVANCE_PIXELS * u, 0)}
    expected = {}

    for cp in codepoints:
        name = "uni%04X" % cp
        source = bdf_glyphs[cp]
        shift = ADVANCE_PIXELS - source["advance"]
        if shift < 0:
            raise ValueError("glyph U+%04X advance %d exceeds %d px" % (cp, source["advance"], ADVANCE_PIXELS))
        pixels = {(x + shift, y) for x, y in bitmap_pixels(source)}
        glyph = draw_glyph(row_runs(pixels)) if pixels else TTGlyphPen(None).glyph()
        glyph_order.append(name)
        cmap[cp] = name
        glyf[name] = glyph
        expected[name] = pixels
        glyph.recalcBounds(None)
        lsb = glyph.xMin if pixels else 0
        metrics[name] = (ADVANCE_PIXELS * u, lsb)

    builder = FontBuilder((ascent + descent) * u, isTTF=True)
    builder.setupGlyphOrder(glyph_order)
    builder.setupCharacterMap(cmap)
    builder.setupGlyf(glyf)
    builder.setupHorizontalMetrics(metrics)
    builder.setupHorizontalHeader(ascent=ascent * u, descent=-descent * u, lineGap=0)
    builder.setupNameTable({
        "familyName": FAMILY,
        "styleName": STYLE,
        "uniqueFontIdentifier": "%s %s" % (FAMILY, STYLE),
        "fullName": "%s %s" % (FAMILY, STYLE),
        "psName": "NumosFixedCyrillic-Regular",
        "copyright": DESCRIPTION,
        "description": DESCRIPTION,
    })
    builder.setupOS2(
        sTypoAscender=ascent * u,
        sTypoDescender=-descent * u,
        sTypoLineGap=0,
        usWinAscent=ascent * u,
        usWinDescent=descent * u,
        fsType=0,
    )
    builder.setupPost()
    builder.setupHead(unitsPerEm=(ascent + descent) * u, created=0, modified=0)
    font = builder.font
    font["head"].created = 0
    font["head"].modified = 0

    built = font["glyf"]
    for name, pixels in expected.items():
        assert glyph_pixels(built[name]) == pixels, "pixel mismatch in " + name

    builder.save(out_path)
    print("glyphs: %d (plus .notdef)" % len(codepoints))


def main(argv):
    if len(argv) != 3:
        print(__doc__)
        return 2
    build(argv[1], argv[2])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
