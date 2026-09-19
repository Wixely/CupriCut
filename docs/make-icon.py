"""The CupriCut mark: a frame, cut, the halves parted along the cut.

    python docs/make-icon.py        (needs Pillow; nothing else in this repo does)

NOT part of the build. Everything it writes is committed. It is here so the next change to the
icon is an edit to four numbers rather than a redraw, and so the vector master and the raster
sizes cannot drift apart - one set of constants produces both.

Judged at 16px throughout, because that is where icons die. Two earlier attempts failed there:
sprocket holes bitten out of the edge read as damage rather than as film, and a version whose
halves slid PARALLEL to the cut read as a chipped tile rather than a cut one. The perpendicular
offset is the whole mark.

Writes:
    docs/icon.svg          the master, and what the README uses
    docs/icon.png          512, for anywhere that wants a raster
    docs/icon-128.png      a smaller one
    docs/icon-sizes.png    a contact sheet, on both grounds, for judging it
    cupricut.ico           the application icon and /favicon.ico, 16 to 256
"""
import math
import os
from PIL import Image, ImageDraw

# ---- the mark, in a 256 unit square ---------------------------------------------------------
BOX = 256
PAD = 30          # inset from the edge
RADIUS = 48       # corner - a squircle, not a rounded rectangle
ANGLE = 30        # the cut, degrees above horizontal
PART = 10         # how far each half moves PERPENDICULAR to the cut

# Copper, lit from the top left. Three stops because two looked like plastic.
STOPS = [(0.0, '#F7B562'), (0.5, '#D9642A'), (1.0, '#9E3A12')]

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(REPO, 'docs')


def rgb(h):
    return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))


def offsets():
    """Perpendicular to the cut. Parallel would slide the halves PAST each other, which reads as
    a chipped tile rather than a cut one - the first attempt did exactly that."""
    return (PART * math.sin(math.radians(ANGLE)),
            PART * math.cos(math.radians(ANGLE)))


# ---- raster ----------------------------------------------------------------------------------
def ramp(size):
    g = Image.new('RGB', (size, size))
    px = g.load()
    a, b, c = (rgb(s[1]) for s in STOPS)
    for y in range(size):
        for x in range(size):
            t = (x * 0.35 + y * 0.65) / (size - 1)
            u, lo, hi = (t / 0.5, a, b) if t < 0.5 else ((t - 0.5) / 0.5, b, c)
            px[x, y] = tuple(int(lo[i] + (hi[i] - lo[i]) * u) for i in range(3))
    return g


def render(size=2048):
    """The mark at `size`, on transparency. Drawn large and downsampled, so the cut stays clean."""
    k = size / BOX

    def blank():
        return Image.new('L', (size, size), 0)

    body = blank()
    ImageDraw.Draw(body).rounded_rectangle(
        [PAD * k, PAD * k, (BOX - PAD) * k, (BOX - PAD) * k], radius=RADIUS * k, fill=255)

    # Everything above the cut line through the centre.
    up = blank()
    t = math.tan(math.radians(ANGLE))
    c, far = size / 2, size * 3
    ImageDraw.Draw(up).polygon(
        [(-far, c + (c + far) * t), (far, c - (far - c) * t), (far, -far), (-far, -far)], fill=255)

    dx, dy = (v * k for v in offsets())
    fill = ramp(size)
    out = Image.new('RGBA', (size, size), (0, 0, 0, 0))

    # The gradient is painted BEFORE the halves move, so the copper runs continuously across the
    # cut - which is what says one frame was cut rather than two tiles placed near each other.
    for mask, sign in ((up, -1), (Image.eval(up, lambda v: 255 - v), +1)):
        piece = Image.new('RGBA', (size, size), (0, 0, 0, 0))
        piece.paste(fill, (0, 0), Image.composite(body, blank(), mask))

        moved = Image.new('RGBA', (size, size), (0, 0, 0, 0))
        moved.paste(piece, (int(round(sign * dx)), int(round(sign * dy))))
        out.alpha_composite(moved)

    return out


# ---- vector -----------------------------------------------------------------------------------
def svg():
    """The same geometry, from the same constants. Both rects are identical and are translated
    AFTER the gradient is mapped to them, which is how the raster does it too."""
    dx, dy = offsets()
    t = math.tan(math.radians(ANGLE))
    c, far = BOX / 2, BOX * 3

    edge = f"{-far},{c + (c + far) * t:.3f} {far},{c - (far - c) * t:.3f}"
    stops = "\n      ".join(f'<stop offset="{o}" stop-color="{h}"/>' for o, h in STOPS)

    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {BOX} {BOX}" width="{BOX}" height="{BOX}"
     role="img" aria-label="CupriCut">
  <title>CupriCut</title>
  <defs>
    <linearGradient id="copper" x1="0" y1="0" x2="0.7" y2="1.3">
      {stops}
    </linearGradient>
    <clipPath id="above"><polygon points="{edge} {far},{-far} {-far},{-far}"/></clipPath>
    <clipPath id="below"><polygon points="{edge} {far},{far} {-far},{far}"/></clipPath>
  </defs>

  <!-- One frame, cut once, the halves parted perpendicular to the cut. -->
  <g clip-path="url(#above)">
    <rect x="{PAD}" y="{PAD}" width="{BOX - 2 * PAD}" height="{BOX - 2 * PAD}" rx="{RADIUS}"
          fill="url(#copper)" transform="translate({-dx:.3f} {-dy:.3f})"/>
  </g>
  <g clip-path="url(#below)">
    <rect x="{PAD}" y="{PAD}" width="{BOX - 2 * PAD}" height="{BOX - 2 * PAD}" rx="{RADIUS}"
          fill="url(#copper)" transform="translate({dx:.3f} {dy:.3f})"/>
  </g>
</svg>
'''


def contact_sheet(master):
    """The sizes that matter, on a dark ground and a light one. Nothing is finished until this
    looks right at 16."""
    cell, show = 150, (128, 64, 48, 32, 24, 16)
    art = master.resize((256, 256), Image.LANCZOS)

    sheet = Image.new('RGB', (40 + sum(show) + 26 * len(show), cell * 2), (10, 13, 19))
    d = ImageDraw.Draw(sheet)

    for band, bg in enumerate([(14, 18, 26), (243, 245, 250)]):
        y = band * cell
        d.rectangle([0, y, sheet.width, y + cell], fill=bg)
        x = 20
        for sz in show:
            r = art.resize((sz, sz), Image.LANCZOS)
            sheet.paste(r, (x, y + (cell - sz) // 2), r)
            x += sz + 26

    return sheet


if __name__ == '__main__':
    master = render()

    with open(os.path.join(DOCS, 'icon.svg'), 'w', encoding='utf-8') as f:
        f.write(svg())

    master.resize((512, 512), Image.LANCZOS).save(os.path.join(DOCS, 'icon.png'))
    master.resize((128, 128), Image.LANCZOS).save(os.path.join(DOCS, 'icon-128.png'))
    contact_sheet(master).save(os.path.join(DOCS, 'icon-sizes.png'))

    # Every size the Windows shell asks for, each downsampled from the MASTER rather than from the
    # next size up - which is where small icons usually turn to mush.
    master.resize((256, 256), Image.LANCZOS).save(
        os.path.join(REPO, 'cupricut.ico'), format='ICO',
        sizes=[(s, s) for s in (256, 128, 64, 48, 32, 24, 16)])

    print('wrote docs/icon.svg, docs/icon.png, docs/icon-128.png, docs/icon-sizes.png, cupricut.ico')
