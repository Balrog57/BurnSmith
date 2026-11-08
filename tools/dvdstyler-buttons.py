"""Turns DVDStyler's button files (buttons/*.xml: an SVG plus parameters) into plain SVG files for BurnSmith's library.

    python tools/dvdstyler-buttons.py "C:/Program Files/DVDStyler/buttons" assets/library/open/buttons

name.svg     = the button as DVDStyler draws it by default (its label emptied: BurnSmith writes the label over the picture)
name.sub.svg = the same with the colours DVDStyler gives the "highlighted" state (the button under the remote's cursor)
The picture slot of frame buttons (an <image> the video thumbnail goes in) is removed; the frame stays.
Width/height = DVDStyler's default size; BurnSmith redraws the SVG at the size the button is given.
A button with fixed proportions is flattened by VectorCraft (vectorcraft-cli convert), when it is installed.
"""
import copy, os, re, subprocess, sys
import xml.etree.ElementTree as ET

VECTORCRAFT = os.environ.get("VECTORCRAFT_CLI", r"C:\Program Files\ArtCraft\VectorCraft\vectorcraft-cli.exe")
if not os.path.exists(VECTORCRAFT): VECTORCRAFT = None

SVG, XLINK = "http://www.w3.org/2000/svg", "http://www.w3.org/1999/xlink"
ET.register_namespace("", SVG)
ET.register_namespace("xlink", XLINK)
SKIP = {"text-v3.xml",     # only a label: BurnSmith's own "Texte seul" look already does that
        "settings.xml"}    # its path is broken upstream (a "C" with five numbers): "gear" is the same idea and draws


def local(tag): return tag.rsplit("}", 1)[-1]


def set_value(el, attr, value):
    """Sets a presentation value where it is in force: in the style when the style names it, else as an attribute."""
    style = el.get("style", "")
    if re.search(rf"(^|;)\s*{re.escape(attr)}\s*:", style):
        el.set("style", re.sub(rf"((^|;)\s*{re.escape(attr)}\s*:)[^;]*", rf"\g<1>{value}", style))
    else:
        el.set(attr, value)


def inline_symbols(svg):
    """<use> of a <symbol> becomes the nested <svg> it stands for (same box, viewBox and stretch), which the WPF reader draws:
    DVDStyler's frames stretch a symbol to 100% of the button that way."""
    href = f"{{{XLINK}}}href"
    symbols = {e.get("id"): e for e in svg.iter() if local(e.tag) == "symbol"}
    for parent in list(svg.iter()):
        for i, use in enumerate(list(parent)):
            if local(use.tag) != "use" or (sym := symbols.get((use.get(href) or use.get("href") or "").lstrip("#"))) is None: continue
            nested = ET.Element(f"{{{SVG}}}svg", {k: v for k, v in use.attrib.items() if k not in (href, "href")})
            for k in ("viewBox", "preserveAspectRatio"):
                if sym.get(k): nested.set(k, sym.get(k))
            nested.extend(copy.deepcopy(list(sym)))
            parent[i] = nested


def size(expr, fallback, text_size):
    """DVDStyler's default size; one that follows the label ("8|text+8") gets a box a label fits in."""
    if "text" in (expr or ""): return text_size
    m = re.match(r"\s*(\d+)", expr or "")
    return m.group(1) if m else fallback


def convert(path, out):
    root = ET.parse(path).getroot()
    svg = next(e for e in root if local(e.tag) == "svg")
    if not svg.tag.startswith("{"):  # a few files leave out the namespace: give it to every element
        for e in svg.iter():
            if not e.tag.startswith("{"): e.tag = f"{{{SVG}}}{e.tag}"
    inline_symbols(svg)
    by_id = {e.get("id"): e for e in svg.iter() if e.get("id")}
    parent = {kid: e for e in svg.iter() for kid in e}

    states = []
    for p in root.iter("parameter"):
        kind, el = p.findtext("type", ""), by_id.get(p.findtext("element", ""))
        if el is None: continue
        if kind == "text":
            for t in el.iter(): t.text = ""
        elif kind == "image":
            parent[el].remove(el)
        elif (hl := p.findtext("default-value/highlighted")) and p.findtext("attribute"):
            # a state that only recolours the label changes nothing here: BurnSmith draws the label itself
            if not any(local(t.tag) == "text" for t in el.iter()):
                states.append((p.findtext("element"), p.findtext("attribute"), hl.strip()))

    vb = (svg.get("viewBox") or "").replace(",", " ").split()
    w = size(root.findtext("default-size/width"), svg.get("width") or (vb[2] if len(vb) == 4 else "100"), "220")
    h = size(root.findtext("default-size/height"), svg.get("height") or (vb[3] if len(vb) == 4 else "100"), "60")
    svg.set("width", w.rstrip("%")), svg.set("height", h.rstrip("%"))

    stem = os.path.join(out, os.path.splitext(os.path.basename(path))[0])
    write(svg, stem + ".svg", len(vb) == 4)
    if states:
        sub = copy.deepcopy(svg)
        ids = {e.get("id"): e for e in sub.iter() if e.get("id")}
        for element, attr, value in states: set_value(ids[element], attr, value)
        write(sub, stem + ".sub.svg", len(vb) == 4)
    return bool(states)


def write(svg, file, fixed_shape):
    """A button with a viewBox keeps its proportions: VectorCraft rewrites it as flat, plain SVG (Inkscape's nested transforms and
    gradient chains are more than the WPF reader draws). A button laid out in percentages is kept as it is, so it still stretches."""
    ET.ElementTree(svg).write(file, encoding="utf-8", xml_declaration=True)
    if fixed_shape and VECTORCRAFT:
        flat = file + ".tmp.svg"
        subprocess.run([VECTORCRAFT, "convert", file, flat], check=True, capture_output=True)
        os.replace(flat, file)


if __name__ == "__main__":
    src, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    for name in sorted(os.listdir(src)):
        if name.endswith(".xml") and name not in SKIP:
            print(name, "+ état choisi" if convert(os.path.join(src, name), out) else "")
