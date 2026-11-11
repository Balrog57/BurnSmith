# Traces the button pictures of the vendor templates into SVG with VectorCraft's Image Trace, one name.svg beside each name.png.
# BurnSmith draws the .svg in place of the picture it stands beside (InstalledTemplate.Asset), and a converted pack carries both.
#
#   python tools\vectorize-buttons.py [templates folder] [--force] [--only text]
#
# The folder is the one template-folders.txt names (default: Documents\BurnSmith\Templates\sources). --force traces again what
# already has its .svg, --only keeps the pictures whose path contains the text. Needs Pillow and VectorCraft
# (VECTORCRAFT_CLI, else its usual folder). `BurnSmith.Cli check-vectors` then measures every trace against its picture.
#
# Image Trace knows colours, not transparency. So a picture is cut into the few levels of transparency it really has (a glass
# plate, a glow, the opaque lettering): each level is traced on its own in one VectorCraft document, as a group whose opacity
# gives that level once the groups are stacked. Each group holds everything at least as opaque as its level, so no seam shows.
import json, os, subprocess, sys, tempfile
from PIL import Image, ImageChops, ImageFilter

CLI = os.environ.get("VECTORCRAFT_CLI", r"C:\Program Files\ArtCraft\VectorCraft\vectorcraft-cli.exe")
# noise 1: a rim or a stripe one pixel wide is kept (the photo preset drops areas under 4 px); gradients: an evenly shaded area is one path, not bands
TRACE = {"preset": "High Fidelity Photo", "params": {"gradients": True, "noise": 1, "paths": 95, "colors": 96}}
BIN, SHARE, FAINT, EVEN = 24, 0.005, 8, 16  # levels of transparency: bins of 24 holding 0.5 % of the even areas; under 8/255 nothing shows
SMALL = 64  # a picture whose short side is under 64 px is enlarged (up to four times) before it is traced

def is_button(path):
    folder, name = os.path.basename(os.path.dirname(path)), os.path.basename(path)
    if not name.lower().endswith(".png") or ".mask." in name or any(p.startswith("LANG_") for p in path.split(os.sep)):
        return False
    return folder in ("play", "scenes", "setup", "home", "left", "right") or (
        folder in ("Scene", "1920x1080") and os.path.splitext(name)[0] in ("back", "next", "previous"))


def levels(alpha):
    """The opacities (1..255) this picture really uses, lowest first, the fullest bin when none stands out."""
    hist = alpha.histogram()
    seen = sum(hist[FAINT:])
    if seen == 0:
        return []
    # an even area is where the opacity hardly changes from a pixel to its neighbours: a plate, a rim, a glow. The one-pixel ramp of a
    # smoothed edge is not one, and gets no level of its own (it would be a ring of paths per step for nothing the eye sees)
    even = alpha.histogram(mask=ImageChops.subtract(alpha.filter(ImageFilter.MaxFilter(3)), alpha.filter(ImageFilter.MinFilter(3))).point(lambda d: 255 if d <= EVEN else 0))
    bins = [(sum(hist[a] for a in r), sum(a * hist[a] for a in r), sum(even[a] for a in r), r[-1] == 255) for r in (range(s, min(s + BIN, 256)) for s in range(FAINT, 256, BIN))]
    # a level: enough even area, or enough pixels whatever their neighbours (thin lettering), or anything opaque (a ramp never is: fine chevrons)
    kept = [b for b in bins if b[2] >= SHARE * seen or b[0] >= 4 * SHARE * seen or b[3] and b[0] >= SHARE / 4 * seen] or [max(bins)]
    return [min(255, round(total / count)) for count, total, _, _ in kept]


def trace(png, svg, work):
    im = Image.open(png).convert("RGBA")
    alpha = im.getchannel("A")
    found = levels(alpha)
    if not found:
        return "vide"
    steps = [{"command": "file.new", "params": {"width": im.width, "height": im.height, "units": "Pixels", "backgroundContents": "transparent"}}]
    # a small picture (a 30-pixel arrow) is traced from an enlarged copy, placed back at its size: its outlines then follow the smoothed
    # edges between the pixels instead of their staircase. On a large button the same costs two to ten times the file for a gain nobody sees
    zoom = int(os.environ.get("VECTORIZE_ZOOM", 0)) or min(4, -(-SMALL // min(im.size)))
    big = im.resize((im.width * zoom, im.height * zoom), Image.LANCZOS) if zoom > 1 else im
    alpha = big.getchannel("A")
    below = 0
    for k, level in enumerate(found):
        cut = (below + level) / 2  # a pixel goes to the nearest level: an anti-aliased edge is cut at half its level, not traced as ten rings
        layer = big.copy()
        layer.putalpha(alpha.point(lambda a: 255 if a >= cut else 0))
        file = os.path.join(work, f"level{k}.png")
        layer.save(file)
        # stacked groups: what shows through the ones below is 1 - product(1 - opacity), so each adds only what is missing
        opacity = 100 if level >= 250 else 100 * (1 - (1 - level / 255) / (1 - below / 255))
        # BurnSmith's SVG reader gives a group's opacity to each of its paths: in a see-through group they must not lie over one another
        how = dict(TRACE, params=dict(TRACE["params"], method="overlapping" if opacity == 100 else "abutting", noise=int(os.environ.get("VECTORIZE_NOISE", 0)) or zoom * zoom))
        steps += [{"command": "file.place", "params": {"path": file, "link": False, "rect": [0, 0, im.width, im.height]}},
                  {"command": "imageTrace.makeAndExpand", "params": how},
                  {"command": "object.group", "params": {}},
                  {"command": "transparency.set", "params": {"opacity": round(opacity, 2)}}]
        below = level
    commands = os.path.join(work, "steps.json")
    with open(commands, "w", encoding="utf-8") as f:
        json.dump(steps, f)
    if os.path.exists(svg):
        os.remove(svg)
    traced = os.path.join(work, "traced.svg")
    # written again with one decimal: a quarter lighter, and a tenth of a pixel is finer than anything drawn
    for step in ([CLI, "run", "--commands", commands, "--export", traced], [CLI, "convert", traced, svg, "--decimals", "1"]):
        run = subprocess.run(step, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if run.returncode != 0 or '"format":"svg"' not in run.stdout.replace(" ", ""):
            if os.path.exists(svg):
                os.remove(svg)
            return "échec : " + (run.stderr or run.stdout).strip()[-300:]
    return None


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    only = sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else ""
    if only in args:
        args.remove(only)
    root = args[0] if args else os.path.join(os.path.expanduser("~"), "Documents", "BurnSmith", "Templates", "sources")
    if not os.path.exists(CLI):
        sys.exit(f"VectorCraft introuvable : {CLI}")
    buttons = sorted(os.path.join(d, f) for d, _, files in os.walk(root) for f in files if is_button(os.path.join(d, f)) and only in os.path.join(d, f))
    done = failed = 0
    with tempfile.TemporaryDirectory() as work:
        for png in buttons:
            svg = os.path.splitext(png)[0] + ".svg"
            if "--force" not in sys.argv and os.path.exists(svg) and os.path.getmtime(svg) >= os.path.getmtime(png):
                continue
            problem = trace(png, svg, work)
            if problem:
                failed += 1
                print(f"non tracé : {png} ({problem})", file=sys.stderr)
            else:
                done += 1
    print(f"{len(buttons)} boutons, {done} tracés, {failed} échecs")
    sys.exit(1 if failed else 0)


main()
