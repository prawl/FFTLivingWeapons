"""The brushed slate-steel plate both Nexus images are built on.

Shared so the page banner (tools/make_banner.py) and the gallery main image
(tools/make_hero.py) sit on the SAME surface: two copies would drift and the page would stop
looking like one set. Specified by the owner's household, in their words: less charcoal grey,
more slate blue-ish grey, more metallic, then slightly darker.

Three ingredients, which together are what actually reads as metal rather than as noise:
  1. a vertical tone ramp, as if the plate is lit from above;
  2. anisotropic brush grain, high frequency ACROSS the brush direction and smooth ALONG it,
     so the scratches run as long lines rather than as even speckle;
  3. a raking specular band sweeping diagonally, the highlight a light leaves on metal.

Kept blue-grey and darkest along the bottom edge, because on the Nexus page banner that is
where Nexus draws its own white mod title.
"""
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

STEEL_TOP = (48, 61, 78)      # lit top edge of the plate
STEEL_BOT = (15, 21, 29)      # shadowed lower edge
STEEL_SPEC = (33, 42, 54)     # colour of the raking highlight
STEEL_EDGE = (8, 11, 14)      # vignette falloff


def steel_plate(w, h, seed=23, top=STEEL_TOP, bot=STEEL_BOT, spec_col=STEEL_SPEC,
                edge=STEEL_EDGE):
    """An RGBA brushed-steel plate of the given size. Seeded, so renders are reproducible."""
    rng = np.random.default_rng(seed)
    y = np.linspace(0.0, 1.0, h)[:, None]
    x = np.linspace(0.0, 1.0, w)[None, :]

    ramp = y ** 1.25
    base = (np.array(top, dtype=np.float32)[None, None, :] * (1 - ramp)[:, :, None]
            + np.array(bot, dtype=np.float32)[None, None, :] * ramp[:, :, None])
    base = np.repeat(base, w, axis=1) if base.shape[1] == 1 else base

    coarse = rng.normal(0.0, 1.0, size=(h, max(8, w // 55))).astype(np.float32)
    grain = np.array(
        Image.fromarray(coarse, mode="F").resize((w, h), Image.BILINEAR), dtype=np.float32)
    grain += rng.normal(0.0, 0.35, size=(h, w)).astype(np.float32)
    # soften ACROSS the brush only (3-tap vertical), so the scratches stay long and directional
    grain = (grain + np.roll(grain, 1, axis=0) + np.roll(grain, -1, axis=0)) / 3.0
    base += (grain * 7.0)[:, :, None]

    t = (x * 0.82 + (1.0 - y) * 0.18)
    spec = np.exp(-((t - 0.40) ** 2) / (2 * 0.22 ** 2)) * 0.60
    spec += np.exp(-((t - 0.88) ** 2) / (2 * 0.09 ** 2)) * 0.28
    base += spec[:, :, None] * np.array(spec_col, dtype=np.float32)[None, None, :]

    im = Image.fromarray(np.clip(base, 0, 255).astype(np.uint8), mode="RGB")

    vig = Image.new("L", (w, h), 0)
    ImageDraw.Draw(vig).ellipse([-w * 0.18, -h * 0.50, w * 1.18, h * 1.34], fill=255)
    vig = vig.filter(ImageFilter.GaussianBlur(160))
    return Image.composite(im, Image.new("RGB", (w, h), edge), vig).convert("RGBA")
