"""The CupriCut mark: a frame, cut, the halves parted along the cut.

    python docs/make-icon.py        (needs Pillow; nothing else in this repo does)

NOT part of the build. Everything it writes is committed. It is here so the next change to the
icon is an edit to four numbers rather than a redraw, and so the vector master and the raster
sizes cannot drift apart - one set of constants produces both.

Judged at 16px throughout, because that is where icons die. Two earlier attempts failed there:
sprocket holes bitten out of the edge read as damage rather than as film, and a version whose
halves slid PARALLEL to the cut read as a chipped tile rather than a cut one. The perpendicular
offset is the whole mark.

The SVG then made that same parallel-slide mistake a second time, for a different reason, and it
was only found by RENDERING IT - a browser screenshot beside the PNG, which is the only way to
check a vector file this repository has. Two things were wrong and only one of them was visible:

  - clip-path applies in the user space of the element referencing it, so a transform on the
    inner rect slides the rect underneath a stationary clip. The cut edge never moves and no gap
    opens. The translate has to be OUTSIDE the clip. This was the one that looked broken.
  - the gradient used objectBoundingBox units, mapping the whole ramp across the rect, while the
    raster painted it across the CANVAS and the inset shape only ever sampled t = 0.118 to 0.886.
    Same stops, same direction, visibly harsher. Both now project onto GRAD_FROM -> GRAD_TO.

Writes:
    docs/icon.svg          the master, and what the README uses
    docs/icon.png          512, for anywhere that wants a raster
    docs/icon-128.png      a smaller one
    docs/icon-sizes.png    a contact sheet, on both grounds, for judging it
    docs/wordmark.png      the CupriCut lettering, in the same copper
    cupricut.ico           the application icon and /favicon.ico, 16 to 256
"""
import math
import os
from PIL import Image, ImageDraw, ImageFont

# ---- the mark, in a 256 unit square ---------------------------------------------------------
BOX = 256
PAD = 30          # inset from the edge
RADIUS = 48       # corner - a squircle, not a rounded rectangle
ANGLE = 30        # the cut, degrees above horizontal
PART = 10         # how far each half moves PERPENDICULAR to the cut

# Copper, lit from the top left. Three stops because two looked like plastic.
STOPS = [(0.0, '#F7B562'), (0.5, '#D9642A'), (1.0, '#9E3A12')]

# The gradient axis, in the SAME 256 coordinates as everything else, running corner to corner
# across the whole canvas rather than across the shape.
#
# That distinction is the entire reason the first SVG looked wrong while the PNGs looked right.
# The raster painted this ramp across the canvas and the shape - inset by PAD - only ever sampled
# t = 0.118 to 0.886, the middle of it. The SVG used objectBoundingBox units, which map t = 0 to 1
# across the RECT, so it showed the pale top and the near-black bottom that the raster never
# reaches. Same three stops, same direction, and visibly harsher.
#
# Both now project onto this one axis, so there is nothing left to disagree about.
GRAD_FROM = (0.0, 0.0)
GRAD_TO = (163.76, 304.13)

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
    """The copper, projected onto GRAD_FROM -> GRAD_TO exactly as a linear gradient does it."""
    g = Image.new('RGB', (size, size))
    px = g.load()
    a, b, c = (rgb(st[1]) for st in STOPS)

    k = size / BOX
    ax, ay = GRAD_FROM[0] * k, GRAD_FROM[1] * k
    dx, dy = GRAD_TO[0] * k - ax, GRAD_TO[1] * k - ay
    span = dx * dx + dy * dy

    for y in range(size):
        for x in range(size):
            t = min(1.0, max(0.0, ((x - ax) * dx + (y - ay) * dy) / span))
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
    """The same geometry and the same gradient axis, from the same constants.

    userSpaceOnUse, not the default objectBoundingBox: the gradient belongs to the CANVAS, not to
    each rect. Mapping it per-rect is what made the first version of this file look harsher than
    the PNGs - see the note by GRAD_TO."""
    dx, dy = offsets()
    t = math.tan(math.radians(ANGLE))
    c, far = BOX / 2, BOX * 3

    edge = f"{-far},{c + (c + far) * t:.3f} {far},{c - (far - c) * t:.3f}"
    stops = "\n      ".join(f'<stop offset="{o}" stop-color="{h}"/>' for o, h in STOPS)

    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {BOX} {BOX}" width="{BOX}" height="{BOX}"
     role="img" aria-label="CupriCut">
  <title>CupriCut</title>
  <defs>
    <linearGradient id="copper" gradientUnits="userSpaceOnUse"
                    x1="{GRAD_FROM[0]}" y1="{GRAD_FROM[1]}" x2="{GRAD_TO[0]}" y2="{GRAD_TO[1]}">
      {stops}
    </linearGradient>
    <clipPath id="above"><polygon points="{edge} {far},{-far} {-far},{-far}"/></clipPath>
    <clipPath id="below"><polygon points="{edge} {far},{far} {-far},{far}"/></clipPath>
  </defs>

  <!-- One frame, cut once, the halves parted perpendicular to the cut.

       The translate is OUTSIDE the clip on purpose. A clip-path applies in the user space of the
       element that references it, so with the transform on the inner rect the clip stays put and
       the rect slides underneath it - the cut edge never moves, no gap opens, and the result looks
       like a chipped tile. Clipping first and moving the whole piece is what the raster does. -->
  <g transform="translate({-dx:.3f} {-dy:.3f})">
    <g clip-path="url(#above)">
      <rect x="{PAD}" y="{PAD}" width="{BOX - 2 * PAD}" height="{BOX - 2 * PAD}" rx="{RADIUS}"
            fill="url(#copper)"/>
    </g>
  </g>
  <g transform="translate({dx:.3f} {dy:.3f})">
    <g clip-path="url(#below)">
      <rect x="{PAD}" y="{PAD}" width="{BOX - 2 * PAD}" height="{BOX - 2 * PAD}" rx="{RADIUS}"
            fill="url(#copper)"/>
    </g>
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


# ---- wordmark --------------------------------------------------------------------------------
WORD = 'CupriCut'
FACE = os.path.join(REPO, 'fonts', 'NotoSans-Bold.ttf')


def sampled_range():
    """The slice of the ramp the icon's shape actually lands on.

    The mark is inset by PAD, so it never reaches either end of the gradient - it runs from about
    t = 0.118 to t = 0.886, and the pale top and near-black bottom are only ever theoretical. The
    axis points into the +x +y quadrant, so the two inset corners are the two extremes."""
    dx, dy = GRAD_TO[0] - GRAD_FROM[0], GRAD_TO[1] - GRAD_FROM[1]
    span = dx * dx + dy * dy

    def at(x, y):
        return ((x - GRAD_FROM[0]) * dx + (y - GRAD_FROM[1]) * dy) / span

    return at(PAD, PAD), at(BOX - PAD, BOX - PAD)


def word_ramp(w, h):
    """The same copper, across a wide box instead of a square one.

    Two things have to agree with the icon or the pair reads as two brands sat together. The
    DIRECTION, which is the unit vector of GRAD_FROM -> GRAD_TO, and the RANGE, which is
    `sampled_range` above. Both are derived rather than typed, so a change to PAD or to the axis
    carries across on its own.

    The range is not a nicety. The first wordmark ran t from 0 to 1 and its leading C came out in
    a pale #F7B562 the icon never shows - faint enough to lose against GitHub's white, which is
    the default theme. Exactly the mistake the SVG made, in the other direction."""
    g = Image.new('RGB', (w, h))
    px = g.load()
    a, b, c = (rgb(st[1]) for st in STOPS)
    lo_t, hi_t = sampled_range()

    n = math.hypot(GRAD_TO[0] - GRAD_FROM[0], GRAD_TO[1] - GRAD_FROM[1])
    ux, uy = (GRAD_TO[0] - GRAD_FROM[0]) / n, (GRAD_TO[1] - GRAD_FROM[1]) / n
    span = w * ux + h * uy          # the box's own extent along that axis

    for y in range(h):
        for x in range(w):
            t = lo_t + (hi_t - lo_t) * min(1.0, max(0.0, (x * ux + y * uy) / span))
            u, lo, hi = (t / 0.5, a, b) if t < 0.5 else ((t - 0.5) / 0.5, b, c)
            px[x, y] = tuple(int(lo[i] + (hi[i] - lo[i]) * u) for i in range(3))
    return g


def wordmark(size=96, scale=4, pad=18):
    """'CupriCut' in the icon's copper, on transparency.

    Plain, after trying two alternatives side by side on both grounds. Running the icon's cut
    through the letters mangled the C and the u and read as a rendering fault rather than a
    motif - the gap that works at 256px square is noise across a word. Setting 'Cupri' in the
    pale ink and picking out 'Cut' in copper looked best of the three on dark and then very
    nearly vanished on white. This one is the only one that survives both."""
    f = ImageFont.truetype(FACE, size * scale)
    p = pad * scale

    box = ImageDraw.Draw(Image.new('L', (1, 1))).textbbox((0, 0), WORD, font=f)
    w, h = box[2] - box[0] + p * 2, box[3] - box[1] + p * 2

    mask = Image.new('L', (w, h), 0)
    ImageDraw.Draw(mask).text((p - box[0], p - box[1]), WORD, font=f, fill=255)

    out = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    out.paste(word_ramp(w, h), (0, 0), mask)
    return out


if __name__ == '__main__':
    master = render()

    with open(os.path.join(DOCS, 'icon.svg'), 'w', encoding='utf-8') as f:
        f.write(svg())

    master.resize((512, 512), Image.LANCZOS).save(os.path.join(DOCS, 'icon.png'))
    master.resize((128, 128), Image.LANCZOS).save(os.path.join(DOCS, 'icon-128.png'))
    contact_sheet(master).save(os.path.join(DOCS, 'icon-sizes.png'))

    # Twice the width the README shows it at, so it stays sharp on a high-density screen.
    mark = wordmark()
    mark.resize((640, max(1, mark.height * 640 // mark.width)), Image.LANCZOS).save(
        os.path.join(DOCS, 'wordmark.png'))

    # Every size the Windows shell asks for, each downsampled from the MASTER rather than from the
    # next size up - which is where small icons usually turn to mush.
    master.resize((256, 256), Image.LANCZOS).save(
        os.path.join(REPO, 'cupricut.ico'), format='ICO',
        sizes=[(s, s) for s in (256, 128, 64, 48, 32, 24, 16)])

    print('wrote docs/icon.svg, docs/icon.png, docs/icon-128.png, docs/icon-sizes.png, '
          'docs/wordmark.png, cupricut.ico')
