"""The lane glow palette: one deep/bright color pair per growth lane (LW-319 C rung).

Owner delegation 2026-08-25 late ("paint the weapons to their respective colors...
make the glow POP", pink over green blessed): every living weapon's icon rim wears its
LANE's hue instead of the LW-295 identity color, at pop strength. The C transform was
ruled on the knife sitting gallery: full-chroma inner ring held DEEP (the equip list
ground is white, so a rim pops by being darker and fully saturated, not lighter) and
a full-chroma BRIGHT outer band whose shipped alpha does the falloff.

Base hue sources, per lane:
  measured  = tools/probes/lw319_text_rgb_map.json (the 2026-08-25 card sitting)
  speed     = EMERALD, the green already living in the baked icon art (owner call
              2026-08-26 00:00, Warbrand card screenshot: "go back to green but look
              at the examples currently baked into the game, those look good"; a
              pink round was cut the same minute as too close to PA's red). Hue 140
              matches the Warbrand identity rim measured at (82,242,147) h144; the
              sage slot-40 TEXT green the owner vetoed as a rim stays vetoed.
  pa        = prominent red, slot 90's family (owner sweep read "I like this over
              the one we're using"). Rendered-RGB measure-later caveat.
Owner tour rulings 2026-08-26 (three at once, second sitting): katanas were too
close to MA blue, so they take the TEAL family WP had (glow h174, text slot 93)
and the old steel sat-cap dies with the steel; WP finds a new color, MAGENTA
h310 (the one open band, 50 degrees off PA red; text slot 82 pink, the sweep's
one new readable family); knight-sword orange and gold were 13 degrees apart,
now spread to h24 red-orange and h52 yellow-gold.
"""
import colorsys
import json
import pathlib

_MEASURED_PATH = (pathlib.Path(__file__).resolve().parents[1]
                  / "probes" / "lw319_text_rgb_map.json")


def _hsv(rgb):
    return colorsys.rgb_to_hsv(*(c / 255 for c in rgb))


def _rgb(h, s, v):
    return tuple(int(round(c * 255)) for c in colorsys.hsv_to_rgb(h, s, v))


def pop_deep(rgb, sat=1.0):
    """C rung inner band: the hue at full (or capped) chroma, value held deep."""
    h, s, v = _hsv(rgb)
    return _rgb(h, sat, min(max(v * 1.05, 0.60), 0.80))


def pop_bright(rgb, sat=1.0):
    """C rung outer band: the same hue at chroma cap and full value."""
    h, s, v = _hsv(rgb)
    return _rgb(h, sat, 1.0)


def _measured():
    m = json.loads(_MEASURED_PATH.read_text(encoding="utf8"))
    return {k: tuple(v["rgb"]) for k, v in m.items() if isinstance(v, dict)}


# Lane -> (base_rgb, sat_cap). Bases either measured (see module docstring) or the
# owner-read stand-ins awaiting a measurement pass.
def _bases():
    m = _measured()
    return {
        "speed":        (_rgb(140 / 360, 0.66, 0.95), 1.0),   # emerald (Warbrand rim family)
        "pa":           (_rgb(0 / 360, 0.85, 0.85), 1.0),     # prominent red stand-in (slot 90)
        "ma":           (m["MA"], 1.0),
        "hp":           (_rgb(24 / 360, 0.75, 1.00), 1.0),    # red-orange; spread from gold
        "wp":           (_rgb(310 / 360, 0.80, 0.95), 1.0),   # magenta (owner: WP finds a new color)
        "wp+faith":     (_rgb(52 / 360, 0.75, 0.95), 1.0),    # yellow-gold; spread from orange
        "pa+ma":        (m["PA+MA"], 1.0),
        "pa+ma+brave":  (_rgb(174 / 360, 0.80, 0.95), 1.0),   # teal, repurposed from WP (owner)
    }


def lane_glow():
    """{lane: {"deep": (r,g,b), "bright": (r,g,b)}} for every growth lane."""
    return {lane: {"deep": pop_deep(base, sat), "bright": pop_bright(base, sat)}
            for lane, (base, sat) in _bases().items()}
