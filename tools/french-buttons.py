# Remakes in French the template buttons whose word is part of the picture ("Play", "Scenes", "Set Up", "Main Menu"): the English
# word is wiped, the French one written in its place with the colours of the original lettering. The English pictures are not touched:
# the French ones go beside them, in LANG_FRA\<role>\0.normal.png and 0.sub.png (the layout the vendor uses for its own languages).
#
#   python tools\french-buttons.py [templates folder] [--only text] [--sheet out.jpg] [--verbose]
#
# Then `python tools\vectorize-buttons.py` traces them to SVG like the others. Needs Pillow, numpy and scipy; the fonts are Windows' own.
#
# How: the buttons of one template share their plate, so what differs between "Play" and "Scenes" is the lettering. Those pixels are
# filled again from the plate around them, and the French word is painted with the colours the old letters had at the same height
# (body from top to bottom, outline around it or shadow thrown to one side). A pictogram left of the word, ornaments beside it stay.
import copy, os, sys, shutil, hashlib
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage

WORDS = {"play": "Lecture", "scenes": "Chapitres", "setup": "Réglages", "home": "Accueil"}
FONTS = r"C:\Windows\Fonts"
SS = 4  # the word is drawn four times larger, then reduced: smooth edges

# font: a Windows font of the same kind as the lettering; upper: capitals; icon: a pictogram stands left of the word and stays;
# track: letter spacing, in thousandths of the size; var: named instance of a variable font
STYLES = {
    "Birthday1": dict(font="Rubik-Bold.ttf"),
    "Birthday2": dict(font="Rubik-Bold.ttf"),
    "Christmas1": dict(font="impact.ttf"),
    "Christmas2": dict(font="Rubik-Bold.ttf"),
    "Cloudy Ocean Night": dict(font="ariblk.ttf", upper=True, icon=True),
    "Holiday": dict(font="seguibli.ttf", upper=True, icon=True),
    "Infinite Universe": dict(font="segoeuib.ttf", upper=True, icon=True, track=40),
    "NewYear1": dict(font="seguisb.ttf"),
    "NewYear2": dict(font="impact.ttf"),
    "Streaming V2": dict(font="verdanab.ttf", upper=True, track=40),
    "The Fleeting Meteor": dict(font="ariblk.ttf", upper=True, icon=True),
    "The Sea of Mountains": dict(font="segoeuib.ttf", upper=True, icon=True, track=30),
    "UHD Birthday1": dict(font="Rubik-Bold.ttf"),
    "UHD Birthday2": dict(font="Rubik-Bold.ttf"),
    "UHD Christmas1": dict(font="impact.ttf"),
    "UHD Christmas2": dict(font="Rubik-Bold.ttf"),
    "UHD Holiday": dict(font="seguibli.ttf", upper=True),
    "UHD NewYear1": dict(font="seguisb.ttf"),
    "UHD NewYear2": dict(font="impact.ttf"),
    "UHD Streaming V2": dict(font="verdanab.ttf", upper=True, track=40),
    "UHD Wedding1": dict(font="segoescb.ttf"),
    "UHD Wedding2": dict(font="comicbd.ttf"),
    "Wedding1": dict(font="pala.ttf"),
    "Wedding2": dict(font="bahnschrift.ttf", var="SemiLight", track=140),
}


def load(path):
    return np.asarray(Image.open(path).convert("RGBA"), dtype=np.float32)


def pre(a):
    """Colours weighted by their opacity: what a transparent pixel hides does not count as a difference."""
    out = a.copy()
    out[..., :3] *= a[..., 3:4] / 255
    return out


def differ(a, b, limit):
    return np.abs(pre(a) - pre(b)).max(axis=2) > limit


def runs(flags, gap):
    """The stretches of true values, those closer than `gap` joined."""
    out = []
    for i in np.flatnonzero(flags):
        if out and i - out[-1][1] <= gap: out[-1][1] = i
        else: out.append([i, i])
    return out


def longest(counts, gap):
    """The stretch that holds the most: the rows of the letters, not those of a crown or a line that moves with them."""
    return max(runs(counts >= 2, gap), key=lambda r: counts[r[0]:r[1] + 1].sum(), default=None)


def gap_of(a, b):
    """How much each pixel differs from one picture to the other."""
    return np.abs(pre(a) - pre(b)).max(axis=2)


def hole_of(gap):
    """What to wipe, from how much each pixel changes with the word: the letters down to the faint end of their glow. A crown or a line that moves with them, apart, stays."""
    strong = ndimage.binary_opening(gap > 14, np.ones((2, 2), bool))
    rows = longest(strong.sum(axis=1), 3)
    if rows is None: return None
    letters = np.zeros_like(strong)
    letters[rows[0]:rows[1] + 1] = strong[rows[0]:rows[1] + 1]
    plain = ndimage.binary_dilation(ndimage.binary_fill_holes(letters), iterations=4)
    apart = ndimage.binary_dilation(strong & ~letters, iterations=3)  # the crown, the line: not to be reached through their glow
    lab, _ = ndimage.label(ndimage.binary_dilation(gap > 4, iterations=2) & ~apart)
    wide = ndimage.binary_fill_holes(np.isin(lab, np.unique(lab[letters]))) | plain
    return wide if wide.sum() <= 4 * plain.sum() else plain  # plates drawn anew for each button differ everywhere a little


def wipe(img, hole):
    """The picture without what the hole covers: each row runs from the plate left of a gap to the plate right of it, each column from above to below when the plate is striped upright."""
    p = pre(img)
    ys, xs = np.nonzero(hole)
    if len(ys) == 0: return img.copy()
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, max(0, xs.min() - 30), xs.max() + 31  # the plate beside the word, on its rows
    near, plate = p[y0:y1, x0:x1], ~hole[y0:y1, x0:x1]
    step = lambda axis, both: (np.abs(np.diff(near, axis=axis)).max(axis=2) * both).sum() / max(1, both.sum())
    upright = step(1, plate[:, 1:] & plate[:, :-1]) > 1.5 * step(0, plate[1:] & plate[:-1])
    if upright: p, hole = p.transpose(1, 0, 2), hole.T
    out = p.copy()
    for y in np.flatnonzero(hole.any(axis=1)):
        gap, known = np.flatnonzero(hole[y]), np.flatnonzero(~hole[y])
        if len(known):
            for c in range(4): out[y, gap, c] = np.interp(gap, known, p[y, known, c])
    out[hole] = ndimage.gaussian_filter1d(out, 1.5, axis=0)[hole]  # no streak from one line to the next
    if upright: out = out.transpose(1, 0, 2)
    alpha = np.maximum(out[..., 3:4], 1e-3)
    out[..., :3] = np.clip(out[..., :3] * 255 / alpha, 0, 255)
    return out


def ink_of(img, hole, limit=26):
    """The pixels this button's own letters (and pictogram) add to the plate."""
    return ndimage.binary_opening(differ(img, wipe(img, hole), limit) & hole, np.ones((2, 2), bool))


def word_start(ink):
    """(first column of ink, first column of the word): they differ when a pictogram comes first, a blob about as wide as it is high and set apart."""
    rows, blobs = np.flatnonzero(ink.any(axis=1)), runs(ink.any(axis=0), 3)
    if not blobs: return 0, 0
    high, (a, b) = rows[-1] - rows[0] + 1, blobs[0]
    return (a, blobs[1][0]) if len(blobs) > 1 and 0.55 * high <= b - a + 1 <= 1.6 * high and blobs[1][0] - b > 6 else (a, a)


def two_kinds(colours):
    """The pixels of a lettering split in two by colour (k-means, k = 2): its body and its outline or shadow."""
    centres = np.stack([colours[colours.sum(axis=1).argmin()], colours[colours.sum(axis=1).argmax()]]).astype(np.float64)
    for _ in range(12):
        kind = np.linalg.norm(colours[:, None, :] - centres[None], axis=2).argmin(axis=1)
        for k in (0, 1):
            if (kind == k).any(): centres[k] = colours[kind == k].mean(axis=0)
    return kind, centres


class Look:
    """What the old letters looked like: the body's colour from top to bottom, and around it, from the inside out, outlines and shadows thrown to one side."""

    def __init__(self, img, ink, bare):
        self.around = []  # outermost first: ("ring", width, tint) or ("slide", (dx, dy), tint)
        core, colour, plate = ink, pre(img).astype(np.float64), pre(bare).astype(np.float64)
        for _ in range(4):
            ys, xs = np.nonzero(core)
            if len(ys) <= 50 or len(self.around) == 2: break
            kind, centres = two_kinds(colour[ys, xs])
            if np.linalg.norm(centres[0] - centres[1]) <= 55: break
            share = (kind == 0).mean()
            if min(share, 1 - share) <= 0.08:  # a few stray pixels: set aside, and look again
                core = core.copy()
                core[ys[kind == (share > 0.5)], xs[kind == (share > 0.5)]] = False
                continue
            depth = ndimage.distance_transform_edt(core)[ys, xs]
            deep = depth[kind == 0].mean(), depth[kind == 1].mean()
            if abs(deep[0] - deep[1]) > 0.15 * max(deep): inner = 0 if deep[0] > deep[1] else 1
            else:  # thin letters are no deeper than their glow: the body is what stands out from the plate
                away = np.abs(colour[ys, xs] - plate[ys, xs]).max(axis=1)
                inner = 0 if away[kind == 0].mean() > away[kind == 1].mean() else 1
            body = np.zeros_like(core); body[ys[kind == inner], xs[kind == inner]] = True
            rest = core & ~body
            rows = np.flatnonzero(body.any(axis=1))
            if abs((rows[0] - ys.min()) - (ys.max() - rows[-1])) > 0.3 * (np.ptp(ys) + 1): break  # one end of the body only: its own colours from top to bottom, not a layer around it
            if rest.sum() <= 0.12 * max(1, body.sum()):  # too little to be a layer of the lettering: set aside as well
                core = body
                continue
            tint = np.median(colour[rest], axis=0)
            # a shadow lies to one side of the body, an outline all around it: which of the two rebuilds the old ink best
            slid, at = None, (0, 0)
            for dy in range(-3, 9):
                for dx in range(-8, 9):
                    miss = np.count_nonzero((body | np.roll(body, (dy, dx), (0, 1))) ^ core)
                    if slid is None or miss < slid: slid, at = miss, (dx, dy)
            near = ndimage.distance_transform_edt(~body)
            ring, wide = min((np.count_nonzero((near <= w) ^ core), w) for w in (1, 1.5, 2, 2.5, 3, 4, 5, 6))
            if max(abs(at[0]), abs(at[1])) >= 2 and slid < 0.85 * ring: self.around.append(("slide", at, tint))
            else:
                if wide <= 1:  # the soft edge of the letters, half body half plate, is no outline
                    if self.around: break
                    b, q = np.median(colour[body], axis=0), np.median(plate[rest], axis=0)
                    t = np.dot(tint - b, q - b) / max(1.0, np.dot(q - b, q - b))
                    if 0.1 < t < 0.9 and np.linalg.norm(tint - (b + t * (q - b))) < 30:
                        core = body
                        break
                self.around.append(("ring", wide, tint))
            core = body
        rows = longest(core.sum(axis=1), 1)  # the capitals; a line under the word is another stretch
        ys, xs = np.nonzero(core)
        self.top, self.bottom = (int(rows[0]), int(rows[1])) if rows else (int(ys.min()), int(ys.max()))
        inside = ndimage.distance_transform_edt(core)[ys, xs] >= 1.2
        v = np.clip((ys - self.top) * 24 // max(1, self.bottom - self.top + 1), 0, 23)
        self.fill, seen = np.zeros((24, 4)), np.zeros(24, bool)
        for k in range(24):
            here = (v == k) & inside if ((v == k) & inside).any() else (v == k)
            if here.any(): self.fill[k], seen[k] = np.median(colour[ys[here], xs[here]], axis=0), True
        rows = np.flatnonzero(seen)
        for k in range(24):
            if len(rows) and not seen[k]: self.fill[k] = self.fill[rows[np.abs(rows - k).argmin()]]

    def __str__(self):
        return f"capitals {self.top}-{self.bottom}, around [" + ", ".join(f"{k} {p}" for k, p, _ in self.around) + "]"

    def paint(self, glyph):
        """The word, from its shape drawn SS times larger: RGBA weighted by opacity, at the button's scale."""
        shape = glyph > 127
        big = np.zeros(shape.shape + (4,))
        def over(mask, tint):
            nonlocal big
            a = mask[..., None] * (np.asarray(tint)[..., 3:4] / 255)
            big = np.asarray(tint) * mask[..., None] + big * (1 - a)
        layers, outer = [], shape
        for kind, p, tint in reversed(self.around):  # each one is built on what it surrounds
            outer = ndimage.distance_transform_edt(~outer) <= p * SS if kind == "ring" else outer | np.roll(outer, (p[1] * SS, p[0] * SS), (0, 1))
            layers.append((outer, tint))
        for mask, tint in reversed(layers): over(mask, tint)
        rows = np.arange(shape.shape[0])[:, None] / SS
        v = np.clip(((rows - self.top) * 24 / max(1, self.bottom - self.top + 1)).astype(int), 0, 23)
        over(shape, self.fill[np.broadcast_to(v, shape.shape)])
        h, w = shape.shape[0] // SS, shape.shape[1] // SS
        return big[:h * SS, :w * SS].reshape(h, SS, w, SS, 4).mean(axis=(1, 3)).astype(np.float32)


def font_of(style, size):
    font = ImageFont.truetype(os.path.join(FONTS, style["font"]), max(4, int(round(size))))
    if style.get("var"): font.set_variation_by_name(style["var"])
    return font


def shape_of(word, style, cap, room):
    """The word as a mask SS times larger, `cap` pixels high for a capital, squeezed (to 72 % at most) then reduced to fit `room`."""
    box = font_of(style, 400).getbbox("S", anchor="ls")
    size = 400 * cap * SS / max(1, box[3] - box[1])
    for _ in range(6):
        font = font_of(style, size)
        track = style.get("track", 0) * size / 1000
        long = sum(font.getlength(c) for c in word) + track * (len(word) - 1)
        if long * 0.72 <= room * SS: break
        size *= room * SS / (long * 0.72)
    pad = int(size)
    mask = Image.new("L", (int(long) + 2 * pad, 3 * pad), 0)
    draw, x = ImageDraw.Draw(mask), pad
    for c in word:
        draw.text((x, 2 * pad), c, 255, font, anchor="ls")
        x += font.getlength(c) + track
    cols = np.flatnonzero(np.asarray(mask).any(axis=0))
    mask = mask.crop((int(cols[0]), 0, int(cols[-1]) + 1, 3 * pad))
    if mask.width > room * SS: mask = mask.resize((int(room * SS), mask.height), Image.LANCZOS)
    return np.asarray(mask), 2 * pad  # and the row of its baseline


def sides(inks, faints):
    """Ornaments on either side of the word (chevrons, a sparkle and its tail), which move with it: for each button the (left, right, word) column spans.
    They are the outer blobs as wide on the left as on the right, and as wide from one button to the other: letters change with the word."""
    blobs = {r: runs(i.any(axis=0), 5) for r, i in inks.items()}
    n = 0
    while all(len(b) > 2 * n + 2 for b in blobs.values()):
        left, right = [b[n][1] - b[n][0] for b in blobs.values()], [b[-1 - n][1] - b[-1 - n][0] for b in blobs.values()]
        if max(left) - min(left) > 2 or max(abs(l - r) for l, r in zip(left, right)) > 3: break
        n += 1
    out = {}
    for r, b in blobs.items() if n else ():
        cols = np.flatnonzero(faints[r].any(axis=0))  # the faint end of a tail goes with it
        far = max(b[n - 1][1] - b[0][0], min(b[n - 1][1] - int(cols[0]), int(cols[-1]) - b[-n][0]))
        out[r] = (b[n - 1][1] - far, b[n - 1][1]), (b[-n][0], b[-n][0] + far), (b[n][0], b[-n - 1][1])
    return out


def remake(img, bare, look, word, style, start, middle, room, orn, band):
    """The French button: the bare plate, the word on the old baseline (from where the old word started, or centred where the old ones were), its ornaments beside it."""
    h, w = img.shape[:2]
    glyph, base = shape_of(word.upper() if style.get("upper") else word, style, look.bottom - look.top + 1, room)
    gw = glyph.shape[1] / SS
    gx = start if start is not None else middle - gw / 2
    canvas = np.zeros((h * SS, w * SS), np.uint8)
    ty, tx = int(round((look.bottom + 1) * SS - base)), int(round(gx * SS))
    sy0, sx0 = max(0, -ty), max(0, -tx)
    part = glyph[sy0:sy0 + canvas.shape[0] - max(0, ty), sx0:sx0 + canvas.shape[1] - max(0, tx)]
    canvas[max(0, ty):max(0, ty) + part.shape[0], max(0, tx):max(0, tx) + part.shape[1]] = part
    ink = look.paint(canvas)
    out = ink + pre(bare) * (1 - ink[..., 3:4] / 255)
    alpha = np.maximum(out[..., 3:4], 1e-3)
    out[..., :3] = np.clip(out[..., :3] * 255 / alpha, 0, 255)
    if orn is not None:  # each ornament keeps its distance from the word; the strip it sits on goes with it (the plate only changes from top to bottom)
        (a0, b0), (a1, b1), (wa, wb) = orn
        to_left, to_right = int(round(gx)) - (wa - b0) - (b0 - a0), int(round(gx + gw)) + (a1 - wb) - 1
        if to_left - 1 >= 0 and to_right + (b1 - a1) + 2 <= w:
            out[band[0]:band[1] + 1, to_left - 1:to_left + b0 - a0 + 2] = img[band[0]:band[1] + 1, a0 - 1:b0 + 2]
            out[band[0]:band[1] + 1, to_right - 1:to_right + b1 - a1 + 2] = img[band[0]:band[1] + 1, a1 - 1:b1 + 2]
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8), "RGBA")


def right_of(hole, x):
    out = hole.copy()
    out[:, :max(0, x)] = False
    return out


def main():
    args = [a for i, a in enumerate(sys.argv[1:], 1) if not a.startswith("--") and sys.argv[i - 1] not in ("--only", "--sheet")]
    opt = lambda name: sys.argv[sys.argv.index(name) + 1] if name in sys.argv else None
    root = args[0] if args else os.path.join(os.path.expanduser("~"), "Documents", "BurnSmith", "Templates", "sources")
    only, made, pairs = opt("--only") or "", {}, []

    def write(tdir, role, state, picture):
        source, target = os.path.join(tdir, role, f"0.{state}.png"), os.path.join(tdir, "LANG_FRA", role, f"0.{state}.png")
        os.makedirs(os.path.dirname(target), exist_ok=True)
        picture.save(target)
        pairs.append((source, target))
        made[hashlib.sha1(open(source, "rb").read()).hexdigest()] = target

    for folder in sorted(os.listdir(os.path.join(root, "Blu-ray"))):
        style = STYLES.get(folder)
        if style is None or only not in folder: continue
        tdir = os.path.join(root, "Blu-ray", folder)
        wordy = lambda im: im.shape[1] >= 200 and im.shape[1] >= 2 * im.shape[0]  # a round pictogram has no word in it
        every = {s: {r: load(f) for r in WORDS if os.path.exists(f := os.path.join(tdir, r, f"0.{s}.png"))} for s in ("normal", "sub")}
        capitals, begins = {}, {}
        for state, images in every.items():
            images = {r: im for r, im in images.items() if wordy(im)}
            # the pictures of the commonest size are read together: what changes from one to the other is the word
            sizes = {}
            for r, im in images.items(): sizes.setdefault(im.shape[:2], []).append(r)
            size = max(sizes, key=lambda k: len(sizes[k]), default=None)
            group = {r: images[r] for r in sizes[size]} if size and len(sizes[size]) > 1 else {}
            look = None
            if group:
                names, gap = list(group), np.zeros(size, np.float32)
                for i, a in enumerate(names):
                    for b in names[i + 1:]: gap = np.maximum(gap, gap_of(group[a], group[b]))
                hole = hole_of(gap)
            if group and hole is not None:
                h, w = size
                ink = {r: ink_of(im, hole) for r, im in group.items()}
                ink = {r: i for r, i in ink.items() if i.any()}
                starts = {r: word_start(ink_of(group[r], hole, 70)) for r in ink} if style.get("icon") else {}  # a soft shadow must not join the pictogram to the word
                if state == "sub" and size in begins: starts = begins[size]  # selected, a glow joins the pictogram to the word
                begins.setdefault(size, starts)
                if starts:  # the pictogram stays: only what follows it is wiped and read as letters
                    ink = {r: right_of(i, starts[r][1] - 3) for r, i in ink.items()}
                orn = {} if starts else sides(ink, {r: ink_of(group[r], hole, 8) for r in ink})
                ys, xs = np.nonzero(hole), np.flatnonzero(np.any([i.any(axis=0) for i in ink.values()], axis=0))
                band, x0, x1 = (int(ys[0].min()), int(ys[0].max())), int(xs[0]), int(xs[-1])
                model = "scenes" if "scenes" in ink else next(iter(ink))  # no letter of "Scenes" goes under the line
                letters = ink[model] if model not in orn else right_of(ink[model], orn[model][2][0] - 2) & ~right_of(ink[model], orn[model][2][1] + 3)
                look = Look(group[model], letters, wipe(group[model], hole))
                if state == "sub" and size in capitals: look.top, look.bottom = capitals[size]  # selected, the same word at the same place: the glow added around it is no measure
                capitals.setdefault(size, (look.top, look.bottom))
                margin = min((s[0] for s in starts.values()), default=0)
                if "--verbose" in sys.argv:
                    print(f"{folder} {state}: words {x0}-{x1}, rows {band}, {look}, ornaments {bool(orn)}, starts { {r: (int(a), int(b)) for r, (a, b) in starts.items()} }")
                for r in ink:
                    o = orn.get(r)
                    if starts:
                        at = int(starts[r][1])
                        picture = remake(group[r], wipe(group[r], right_of(hole, at - 3)), look, WORDS[r], style, at, None, max(w - margin, x1 + 6) - at, None, band)
                    else:
                        room = (x1 - x0 + 1) - (0 if o is None else 2 * (o[2][0] - o[0][0] + 2))
                        picture = remake(group[r], wipe(group[r], hole), look, WORDS[r], style, None, (x0 + x1 + 1) / 2, room, o, band)
                    write(tdir, r, state, picture)
            # a picture of another size (a wider "Main Menu" on its own plate): its word is what changes when the button is selected
            for r, im in images.items():
                if r in group: continue
                other = every["sub" if state == "normal" else "normal"].get(r)
                hole = hole_of(gap_of(im, other)) if other is not None and other.shape == im.shape else None
                if hole is not None and hole.sum() > 0.4 * hole.size: hole = None
                dy = None
                if hole is None and look is not None:
                    # the whole plate changes when selected: the word is where the others have theirs, counted from the middle
                    dy, dx = (im.shape[0] - size[0]) // 2, (im.shape[1] - size[1]) // 2
                    hole = np.zeros(im.shape[:2], bool)
                    hole[max(0, band[0] + dy):band[1] + dy + 1, max(0, x0 - 4 + min(dx, 0)):x1 + 5 + dx + max(dx, 0)] = True
                if hole is None:
                    print(f"  not remade: {folder} {r} {state} (its word cannot be told from its plate)")
                    continue
                letters = ink_of(im, hole)
                if not letters.any(): continue
                ys, xs = np.nonzero(letters)
                if dy is None:
                    own = Look(im, letters, wipe(im, hole))
                    if look is not None and not own.around: own.around = look.around
                else:  # the others' lettering, at the same place from the middle
                    own = copy.copy(look)
                    own.top, own.bottom = look.top + dy, look.bottom + dy
                if style.get("icon"):  # the pictogram does not change with the state: the word starts at the first changed column
                    picture = remake(im, wipe(im, hole), own, WORDS[r], style, int(xs.min()), None, int(xs.max()) + 7 - int(xs.min()), None, None)
                else:
                    picture = remake(im, wipe(im, hole), own, WORDS[r], style, None, (int(xs.min()) + int(xs.max()) + 1) / 2, im.shape[1] * 0.7, None, None)
                write(tdir, r, state, picture)
    # a picture another template shares byte for byte (Streaming's "Play" is Infinite Universe's) takes the same French one
    for d, _, names in os.walk(os.path.join(root, "Blu-ray")):
        if "LANG_" in d or os.path.basename(d) not in WORDS: continue
        for n in names:
            if n not in ("0.normal.png", "0.sub.png"): continue
            source = os.path.join(d, n)
            target = os.path.join(os.path.dirname(d), "LANG_FRA", os.path.basename(d), n)
            twin = made.get(hashlib.sha1(open(source, "rb").read()).hexdigest())
            if twin and os.path.normcase(twin) != os.path.normcase(target) and only in d and source not in [p[0] for p in pairs]:
                os.makedirs(os.path.dirname(target), exist_ok=True)
                shutil.copyfile(twin, target)
                pairs.append((source, target))
    print(f"{len(pairs)} French buttons")
    if opt("--sheet"):
        cell_w, cell_h, cols = 470, 150, 4
        sheet = Image.new("RGB", (cell_w * cols, cell_h * ((len(pairs) * 2 + cols - 1) // cols)), (90, 90, 100))
        for i, pair in enumerate(pairs):
            for j, f in enumerate(pair):
                im = Image.open(f).convert("RGBA"); im.thumbnail((cell_w - 10, cell_h - 10))
                n = i * 2 + j
                sheet.paste(im, ((n % cols) * cell_w + 5, (n // cols) * cell_h + 5), im)
        sheet.save(opt("--sheet"), quality=90)


if __name__ == "__main__":
    main()
