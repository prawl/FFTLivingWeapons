"""PROBE: the ramp recolour engine prototype that replaced the rejected icon rework.

Built and iterated live with the owner on 2026-08-16 (twelve review rounds, three player
critiques folded in). Renders the 16 shields under four treatments as 1x strips and
computes the mechanical gates (invented-edge count, outline-ring delta, sat ratio).
This is the reference implementation for the LW-247 engine port into recolor_icons.py;
the per-item decisions (twin groups, POP set, Swiftguard indigo, tint overrides) live
in its tables. Outputs land in %TEMP%/ramp_prototype_work, never in the repo.

Rules the engine enforces, each bought by a specific defect:
  value is NEVER touched (invented brightness edges impossible by construction);
  hue rotates by ONE per-material delta, low-chroma pixels snap to the family;
  donor ramps come from the vanilla cache and are re-centred with clamped hue travel;
  shared-drawing twin families take a consensus frame map (frame = identity, face = silver);
  saturated outline ink is drained to neutral dark; shadows obey sat <= 0.30 + 0.55*value;
  outline ring, speculars, and non-identity materials stay vanilla.
"""
import colorsys, math, os, re, shutil, subprocess, sys
from PIL import Image

REPO = r"C:\Users\ptyRa\Dev\FFTLivingWeapons"
CACHE = os.path.join(os.environ["TEMP"], "vanilla_cache")
SCRATCH = os.path.join(os.environ["TEMP"], "ramp_prototype_work")
os.makedirs(SCRATCH, exist_ok=True)
FF16 = r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe"
SHIELDS = list(range(128, 144))
HALO_HI, HALO_LO = 224, 48
NEUTRAL_SAT = 0.18          # below this a pixel reads as unpainted metal/neutral
CHROMATIC_MIN_FRAC = 0.08   # less chromatic than this -> Mode B (donor ramp)

# --- identity tints, parsed from the repo source (read-only) -------------------------
src = open(os.path.join(REPO, "tools", "recolor_icons.py"), encoding="utf-8").read()
TINTS = {}
for m in re.finditer(r"^\s*(\d+):\s*\(([\d.]+),\s*([\d.]+),\s*([\d.]+)\)", src, re.M):
    i = int(m.group(1))
    if i in SHIELDS:
        TINTS[i] = (float(m.group(2)), float(m.group(3)), float(m.group(4)))
assert len(TINTS) == 16, f"parsed {len(TINTS)} shield tints, want 16"
# owner call 2026-08-16: Swiftguard drops the teal for the one hue family no shield
# uses -- bright orchid (between Nightward's dark violet and Galewall's hot pink)
TINTS[131] = (0.68, 0.60, 1.00)  # ROYAL INDIGO, INVERTED: identity on the FACE,
                                 # frame stays silver -- structurally unique in the
                                 # kite family (picker candidate B, working choice)
TWIN_VSCALE = {}
TWIN_INVERT = set()              # inversion OFF for smalls: the kite frame covers too
                                 # much of the 48px drawing, so face-identity reads
                                 # splotchy there; the CARD keeps its indigo face via
                                 # the generic engine (cards are not twin-routed)

NAMES = {128: "Tideward", 129: "Galewall", 130: "Stormwall", 131: "Swiftguard",
         132: "Wardstone", 133: "Sanctguard", 134: "Rimeward", 135: "Emberward",
         136: "Spellbane", 137: "Trailblazer", 138: "Vanguard", 139: "Nightward",
         140: "Genji Shield", 141: "Kaiser Shield", 142: "Venetian Shield", 143: "Aegis Prime"}

def circ_mean(hues, weights):
    x = sum(w * math.cos(2 * math.pi * h) for h, w in zip(hues, weights))
    y = sum(w * math.sin(2 * math.pi * h) for h, w in zip(hues, weights))
    return (math.atan2(y, x) / (2 * math.pi)) % 1.0

def hdist(a, b):
    d = abs(a - b) % 1.0
    return min(d, 1.0 - d)

def load_vanilla(icon_id, surface):
    name = f"ei_{icon_id}_uitx.dds" if surface == "card" else f"ei_s_{icon_id}_uitx.dds"
    return Image.open(os.path.join(CACHE, name)).convert("RGBA")

def load_shipped(icon_id, surface):
    sub = "equip_item" if surface == "card" else "equip_item_s"
    stem = f"ei_{icon_id}_uitx" if surface == "card" else f"ei_s_{icon_id}_uitx"
    tex = os.path.join(REPO, "mod", "FFTIVC", "data", "enhanced", "ui", "ffto",
                       "icon", sub, "texture", f"{stem}.tex")
    work = os.path.join(SCRATCH, f"_ship_{stem}.tex")
    dds = os.path.join(SCRATCH, f"_ship_{stem}.dds")
    if not os.path.exists(dds):
        shutil.copy(tex, work)
        subprocess.run([FF16, "tex-conv", "-i", work], capture_output=True)
    return Image.open(dds).convert("RGBA")

# --- per-sprite analysis --------------------------------------------------------------
def analyze(im):
    """Classify pixels; return dict with per-pixel HSV, masks, cluster stats."""
    w, h = im.size
    px = im.load()
    hsv = {}
    solid, ring, spec = set(), set(), set()
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a < HALO_HI:
                continue
            hh, ss, vv = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            hsv[(x, y)] = (hh, ss, vv)
            solid.add((x, y))
    for (x, y) in solid:
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if not (0 <= nx < w and 0 <= ny < h) or px[nx, ny][3] < HALO_HI:
                ring.add((x, y)); break
    for p, (hh, ss, vv) in hsv.items():
        if ss < 0.15 and vv > 0.85:
            spec.add(p)
    chrom = [p for p in solid if hsv[p][1] >= NEUTRAL_SAT and p not in ring]
    return {"hsv": hsv, "solid": solid, "ring": ring, "spec": spec, "chrom": chrom, "size": (w, h)}

def hue_clusters(points, hsv, k_max=4):
    """Circular k-means on hue, sat-weighted; k picked by hue spread. Returns
    list of (member_set, mean_hue) sorted by size desc."""
    if not points:
        return []
    hues = [hsv[p][0] for p in points]
    sats = [hsv[p][1] for p in points]
    mean = circ_mean(hues, sats)
    spread = sum(s * hdist(hh, mean) for hh, s in zip(hues, sats)) / max(1e-9, sum(sats))
    k = 1 if spread < 0.03 else (2 if spread < 0.07 else (3 if spread < 0.12 else 4))
    k = min(k, k_max)
    # init: k evenly spaced quantiles of hue distance from mean, deterministic
    ordered = sorted(points, key=lambda p: hsv[p][0])
    centers = [hsv[ordered[int((i + 0.5) * len(ordered) / k)]][0] for i in range(k)]
    members = None
    for _ in range(12):
        members = [[] for _ in range(k)]
        for p in points:
            j = min(range(k), key=lambda c: hdist(hsv[p][0], centers[c]))
            members[j].append(p)
        new = [circ_mean([hsv[p][0] for p in ms], [hsv[p][1] for p in ms]) if ms else centers[j]
               for j, ms in enumerate(members)]
        if all(hdist(a, b) < 1e-4 for a, b in zip(new, centers)):
            centers = new; break
        centers = new
    out = [(set(ms), c) for ms, c in zip(members, centers) if ms]
    # merge clusters closer than a material distinction: two hues 0.06 apart are one
    # painted material with internal ramp travel, never two materials (Aegis's face
    # split 0.113/0.079 and rendered as interleaved blue/gold mottle before this)
    merged = []
    for ms, c in out:
        for i, (m2, c2) in enumerate(merged):
            if hdist(c, c2) < 0.06:
                u = m2 | ms
                merged[i] = (u, circ_mean([hsv[p][0] for p in u], [hsv[p][1] for p in u]))
                break
        else:
            merged.append((ms, c))
    merged.sort(key=lambda t: -len(t[0]))
    return merged

# --- donor ramps (Mode B) -------------------------------------------------------------
_donor_cache = {}
def donor_ramp(target_hue, surface):
    """5-step (h,s,v) ramp from the vanilla sprite whose dominant chromatic material
    sits closest to target_hue. Scans the whole cache once per surface."""
    key = surface
    if key not in _donor_cache:
        cands = []
        pat = re.compile(r"^ei_s_(\d+)_uitx\.dds$" if surface == "small" else r"^ei_(\d+)_uitx\.dds$")
        for fn in os.listdir(CACHE):
            m = pat.match(fn)
            if not m:
                continue
            im = Image.open(os.path.join(CACHE, fn)).convert("RGBA")
            a = analyze(im)
            if len(a["chrom"]) < 40:
                continue
            cl = hue_clusters(a["chrom"], a["hsv"], k_max=2)
            if not cl:
                continue
            mem, ch = cl[0]
            sats = [a["hsv"][p][1] for p in mem]
            if sum(sats) / len(sats) < 0.30:
                continue
            cands.append((ch, mem, a["hsv"]))
        _donor_cache[key] = cands
    cands = _donor_cache[key]
    best = min(cands, key=lambda t: hdist(t[0], target_hue))
    mem, hsv = best[1], best[2]
    pts = sorted(mem, key=lambda p: hsv[p][2])
    steps = []
    n = len(pts)
    for i in range(5):
        seg = pts[int(i * n / 5):int((i + 1) * n / 5)]
        hs = circ_mean([hsv[p][0] for p in seg], [max(0.05, hsv[p][1]) for p in seg])
        ss = sum(hsv[p][1] for p in seg) / len(seg)
        vs = sum(hsv[p][2] for p in seg) / len(seg)
        steps.append((hs, ss, vs))
    # hue discipline: keep each step's OFFSET from the donor's own mean (that offset is
    # the artist's shadow->light hue travel), but re-centre the ramp on the target and
    # clamp the travel, so a hue-noisy donor cannot paint a rainbow.
    centre = circ_mean([h for h, _, _ in steps], [max(0.05, s) for _, s, _ in steps])
    disciplined = []
    for hs, ss, vs in steps:
        off = ((hs - centre + 0.5) % 1.0) - 0.5
        off = max(-0.06, min(0.06, off))
        disciplined.append(((target_hue + off) % 1.0, ss, vs))
    return disciplined

# --- the three treatments -------------------------------------------------------------
def feather_weights(target, size, protected):
    """1.0 inside target, 0 elsewhere/protected; 3x3 box-blurred once."""
    w, h = size
    raw = {p: 1.0 for p in target if p not in protected}
    out = {}
    for y in range(h):
        for x in range(w):
            if (x, y) in protected:
                continue  # blur must never bleed paint onto ring/spec pixels
            acc = cnt = 0
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    acc += raw.get((x + dx, y + dy), 0.0); cnt += 1
            if acc:
                out[(x, y)] = acc / cnt
    return out

def dilate(points, solid, r=2):
    """Grow a pixel set by r (chebyshev), clipped to solid pixels. A material is a
    REGION: its desaturated shadow pixels belong to it, not to the steel body."""
    out = set()
    for (x, y) in points:
        for dy in range(-r, r + 1):
            for dx in range(-r, r + 1):
                q = (x + dx, y + dy)
                if q in solid:
                    out.add(q)
    return out

def paint(po, hsv, p, nh, ns, wt, vs=1.0):
    """Blend toward (nh, ns, source-v) in HSV space so a feather seam can never
    dim value the way an RGB lerp between distant hues does. A WIDE hue move does
    not walk the wheel (gold->silver-blue passes through green): it snaps to the
    target hue and fades saturation across the seam instead."""
    hh, ss, vv = hsv[p]
    dh = ((nh - hh + 0.5) % 1.0) - 0.5
    if abs(dh) > 0.15:
        bh, bs = nh, ss + (ns - ss) * wt
    else:
        bh, bs = (hh + dh * wt) % 1.0, ss + (ns - ss) * wt
    bv = vv + (min(1.0, vv * vs) - vv) * wt
    r, g, b = (int(round(c * 255)) for c in colorsys.hsv_to_rgb(bh, min(1.0, bs), bv))
    a0 = po[p][3]
    po[p] = (r, g, b, a0)

# small surface: two families of palette-variants sharing ONE drawing each
TWIN_GROUPS = [
    {129, 131, 133, 135, 139, 143},   # the kite shield (Nightward's family; 131
                                      # joined 2026-08-16, jaccard 0.989 vs 129)
    {130, 134, 136, 138, 140, 142},  # the round shield
]
_twin_ref = {}

def twin_reference(gi):
    """The variant with the cleanest neutral face (Nightward's family benchmark)
    donates the material MAP: which pixels are frame, which are face, and the
    silver ramp the face should wear. One drawing, one map, five items."""
    if gi in _twin_ref:
        return _twin_ref[gi]
    best, chroms = None, []
    for i in sorted(TWIN_GROUPS[gi]):
        im = load_vanilla(i, "small")
        a = analyze(im)
        inner = {p for p in a["solid"] if p not in a["ring"]}
        neutral = [p for p in inner if a["hsv"][p][1] < NEUTRAL_SAT]
        frac = len(neutral) / max(1, len(inner))
        if best is None or frac > best[0]:
            best = (frac, i, a, inner)
        chroms.append(set(a["chrom"]))
    _, ref_id, a, inner = best
    # frame seed: the two variants with the FEWEST chromatic pixels are the ones whose
    # face is neutral, so where they agree is frame and only frame. If they cannot
    # agree (a variant paints even its frame silver), fall back to the reference alone.
    chroms.sort(key=len)
    inter = chroms[0] & chroms[1]
    seed = (inter if len(inter) >= 0.5 * len(chroms[0]) else set(a["chrom"])) & inner
    frame = dilate(seed, a["solid"], r=1) & inner
    face = inner - frame
    pts = sorted(face, key=lambda p: a["hsv"][p][2])
    steps = []
    n = len(pts)
    for i in range(5):
        seg = pts[int(i * n / 5):int((i + 1) * n / 5)]
        hs = circ_mean([a["hsv"][p][0] for p in seg], [max(0.05, a["hsv"][p][1]) for p in seg])
        ss = sum(a["hsv"][p][1] for p in seg) / len(seg)
        steps.append((hs, ss, 0.0))
    _twin_ref[gi] = (frame, face, steps, ref_id, seed)
    return _twin_ref[gi]

def twin_prototype(im, tint, gi, punch=False, item_id=None):
    """Nightward-style split imposed on every variant of the shared drawing:
    frame carries the identity, face wears the reference's silver."""
    t_hue, t_sat, _ = tint
    if punch:
        t_sat = max(t_sat, 0.32)  # a tint that hides at 48px is not an identity
    frame, face, ramp, _, seed = twin_reference(gi)
    inverted = item_id in TWIN_INVERT
    if inverted:
        frame, face, seed = face, frame, set(face)
    a = analyze(im)
    hsv, solid = a["hsv"], a["solid"]
    # the ring is only sacred where it is actually outline INK (dark or neutral);
    # a bright coloured ring pixel is material shading running into the edge and
    # takes its region's treatment (Emberward's red face reaches its outline)
    ink = {p for p in a["ring"] if hsv[p][2] < 0.45 or hsv[p][1] < 0.20}
    released = a["ring"] - ink
    near_frame = dilate(seed, solid, r=1)
    frame = frame | {p for p in released if p in near_frame}
    face = face | {p for p in released if p not in near_frame}
    protected = ink | a["spec"]
    out = im.copy(); po = out.load()
    import bisect
    # per-variant map correction: a frame-region pixel whose hue sits far from the
    # frame's own family is face paint caught in the borrowed map's 1px collar
    # (Emberward's red face under Nightward's frame) -- route it to the face pass
    fr0 = [p for p in frame if p in hsv and p not in protected]
    core = [p for p in seed if p in hsv and hsv[p][1] >= 0.10]  # true frame lines only
    cmean0 = circ_mean([hsv[p][0] for p in core], [hsv[p][1] for p in core]) if core else t_hue
    outliers = set() if inverted else {p for p in fr0 if p not in seed and hsv[p][1] >= 0.10
                and abs(((hsv[p][0] - cmean0 + 0.5) % 1.0) - 0.5) > 0.07}
    frame = frame - outliers
    face = face | outliers
    # face -> reference silver ramp (value structure stays the item's own)
    body = [p for p in face if p in hsv and p not in protected]
    vs = sorted(hsv[p][2] for p in body)
    wts = feather_weights(set(body), a["size"], protected | frame)
    for p, wt in wts.items():
        if p not in hsv or wt <= 0:
            continue
        pos = min(0.9999, bisect.bisect_left(vs, hsv[p][2]) / max(1, len(vs) - 1)) * 4
        i0, frac = int(pos), pos - int(pos)
        h0, s0, _ = ramp[i0]; h1, s1, _ = ramp[min(4, i0 + 1)]
        dh = ((h1 - h0 + 0.5) % 1.0) - 0.5
        paint(po, hsv, p, (h0 + dh * frac) % 1.0, s0 + (s1 - s0) * frac, wt)
    # frame -> identity rotation of the item's own pixels
    fr_protect = (protected - a["spec"]) if inverted else protected
    fr = [p for p in frame if p in hsv and p not in fr_protect]
    chrom_fr = [p for p in fr if hsv[p][1] >= 0.10]
    cmean = circ_mean([hsv[p][0] for p in chrom_fr], [hsv[p][1] for p in chrom_fr]) if chrom_fr else t_hue
    sats = [hsv[p][1] for p in chrom_fr] or [t_sat]
    hi = 3.0 if punch else 1.3  # a silver-variant frame may need to BECOME crimson
    lo = 0.7 if t_sat >= 0.25 else 0.15  # an achromatic identity must be ALLOWED to drain colour
    g = max(lo, min(hi, t_sat / max(1e-9, sum(sats) / len(sats))))
    wts = feather_weights(set(fr), a["size"], fr_protect | face)
    for p, wt in wts.items():
        if p not in hsv or wt <= 0:
            continue
        hh, ss, vv = hsv[p]
        trust = min(1.0, ss / 0.35)
        off = ((hh - cmean + 0.5) % 1.0) - 0.5
        off = max(-0.08, min(0.08, off * trust))
        damp = 0.55 + 0.45 * vv   # the artist mutes his shadows; so do we
        ns = ss * g
        if punch or inverted:
            ns = max(ns, t_sat * 0.85)  # a silver surface has no sat to multiply
        ns = min(ns * damp, 0.30 + 0.55 * vv)  # vanilla law: dark pixels stay muted
        if p in a["spec"]:
            ns = min(ns, 0.22)  # speculars join the family, but only as a pale tint
        paint(po, hsv, p, (t_hue + off) % 1.0, ns, wt, vs=TWIN_VSCALE.get(item_id, 1.0))
    # ink stays dark but goes NEUTRAL dark (Nightward's outline is desaturated; a
    # variant whose outline ink is red-brown reads as bleed against the silver face)
    for p in protected:
        if p in hsv and p not in a["spec"] and hsv[p][1] > 0.25:
            hh, ss, vv = hsv[p]
            paint(po, hsv, p, hh, 0.15, 1.0)
    return out

PUNCH = {130, 134, 136, 138, 140, 142}  # tints chosen near-vanilla for the OLD loud
                                         # engine; under a faithful engine they need help

def prototype(im, tint, surface, item_id=None):
    """v2 placement rule: the BODY carries the identity (the game's own grammar is a
    coloured field wearing gold furniture). Neutral steel bodies get a donor ramp in
    the identity family; chromatic bodies get rotated. Smaller chromatic accents
    (trim, gems, emblems) stay vanilla, which is what keeps the layers separated."""
    if surface == "small":
        for gi, grp in enumerate(TWIN_GROUPS):
            if item_id in grp:
                return twin_prototype(im, tint, gi, punch=item_id in PUNCH, item_id=item_id)
    t_hue, t_sat, t_vm = tint
    punch = item_id in PUNCH
    vscale = max(0.8, min(1.25, t_vm)) if punch else 1.0
    if punch:
        t_sat = max(t_sat, 0.32)  # a tint that hides at 48px is not an identity
    a = analyze(im)
    hsv, solid, ring, spec = a["hsv"], a["solid"], a["ring"], a["spec"]
    out = im.copy(); po = out.load()
    protected = ring | spec
    inner = [p for p in solid if p not in ring]
    neutral = [p for p in inner if hsv[p][1] < NEUTRAL_SAT]
    neutral_frac = len(neutral) / max(1, len(inner))
    if neutral_frac > 0.5:
        # Mode B: steel body -> donor ramp in the identity family; accents untouched.
        # The accent (ALL chromatic pixels here) is protected as a dilated REGION so
        # its desaturated shadow pixels are not mistaken for body steel.
        protected = protected | dilate(set(a["chrom"]), solid, r=2)
        ramp = donor_ramp(t_hue, surface)
        r_sat = sum(s for _, s, _ in ramp) / len(ramp)
        lo, hi = (0.8, 1.8) if punch else (0.3, 1.5)
        sat_g = max(lo, min(hi, t_sat / max(1e-9, r_sat)))
        body = [p for p in neutral if p not in protected]
        vs = sorted(hsv[p][2] for p in body)
        import bisect
        def q(v):
            return bisect.bisect_left(vs, v) / max(1, len(vs) - 1)
        wts = feather_weights(set(body), a["size"], protected)
        for p, wt in wts.items():
            if p not in hsv or wt <= 0:
                continue
            pos = min(0.9999, q(hsv[p][2])) * 4
            i0, frac = int(pos), pos - int(pos)
            h0, s0, _ = ramp[i0]; h1, s1, _ = ramp[min(4, i0 + 1)]
            dh = ((h1 - h0 + 0.5) % 1.0) - 0.5
            paint(po, hsv, p, (h0 + dh * frac) % 1.0, (s0 + (s1 - s0) * frac) * sat_g, wt, vs=vscale)
    else:
        # Mode A: chromatic body -> rotate the dominant material; other materials vanilla,
        # protected as dilated regions so the feather cannot cross the material seam.
        clusters = hue_clusters(a["chrom"], hsv)
        near = [cl for cl in clusters if hdist(cl[1], t_hue) < 0.10]
        chosen = near[0] if near else clusters[0]  # "copper cross on BLUE field": if a
        mem, cmean = chosen                        # material already speaks the identity
        others = set().union(*(ms for ms, c in clusters if (ms, c) != chosen)) or set()
        if others:
            protected = protected | (dilate(others, solid, r=2) - mem)
        delta = (t_hue - cmean) % 1.0
        sats = [hsv[p][1] for p in mem]
        lo, hi = (0.9, 1.8) if punch else (0.7, 1.3)
        g = max(lo, min(hi, t_sat / max(1e-9, sum(sats) / len(sats))))
        # membership widened to the cluster's REGION: mid-sat pixels inside it rotate
        # with the family instead of speckling half-painted
        region = dilate(mem, solid, r=1)
        mem2 = mem | {p for p in region if p not in protected and hsv[p][1] >= 0.08}
        wts = feather_weights(mem2, a["size"], protected)
        for p, wt in wts.items():
            if p not in hsv or wt <= 0:
                continue
            hh, ss, vv = hsv[p]
            # a weak-chroma pixel's hue is noise: snap it to the family hue; a committed
            # pixel keeps its personal offset (the artist's hue travel along the ramp)
            trust = min(1.0, ss / 0.35)
            off = ((hh - cmean + 0.5) % 1.0) - 0.5
            off = max(-0.08, min(0.08, off * trust))  # family stays a family
            damp = (0.7 + 0.3 * vv) if punch else (0.55 + 0.45 * vv)  # artist mutes shadows
            ns = min(ss * g * damp, 0.30 + 0.55 * vv)  # vanilla law: dark pixels stay muted
            paint(po, hsv, p, (t_hue + off) % 1.0, ns, wt, vs=vscale)
    return out

def glow(im, tint):
    """Identity rim as an ADDED layer outside the body. The body contour uses a looser
    alpha threshold plus a majority smooth, so the rim follows the SHAPE the eye sees,
    not the alpha cliff (which dives inward where the artist fades an edge into the
    painted drop-shadow)."""
    t_hue, t_sat, _ = tint
    w, h = im.size
    out = im.copy(); po = out.load(); px = im.load()
    rim_rgb = tuple(int(round(c * 255)) for c in colorsys.hsv_to_rgb(t_hue, min(1.0, t_sat * 1.1), 0.95))
    body = {(x, y) for y in range(h) for x in range(w) if px[x, y][3] >= 160}
    # majority smooth: a non-body pixel with 5+ body neighbours joins; a body pixel
    # with fewer than 3 body neighbours drops -- removes single-pixel contour wiggles
    smoothed = set()
    for y in range(h):
        for x in range(w):
            n = sum((x + dx, y + dy) in body for dy in (-1, 0, 1) for dx in (-1, 0, 1)
                    if (dx, dy) != (0, 0))
            if ((x, y) in body and n >= 3) or ((x, y) not in body and n >= 5):
                smoothed.add((x, y))
    for y in range(h):
        for x in range(w):
            if (x, y) in smoothed:
                continue
            d = min((max(abs(dx), abs(dy)) for dx in (-2, -1, 0, 1, 2) for dy in (-2, -1, 0, 1, 2)
                     if (x + dx, y + dy) in smoothed), default=99)
            if d == 1:
                po[x, y] = rim_rgb + (170,)
            elif d == 2:
                po[x, y] = rim_rgb + (80,)
    return out

# --- metrics --------------------------------------------------------------------------
def metrics(van, out):
    av, ao = analyze(van), analyze(out)
    hv, ho = av["hsv"], ao["hsv"]
    edges = 0
    for (x, y) in av["solid"]:
        for dx, dy in ((1, 0), (0, 1)):
            q_ = (x + dx, y + dy)
            if q_ in av["solid"] and (x, y) in ho and q_ in ho:
                dvv = abs(hv[(x, y)][2] - hv[q_][2])
                dvo = abs(ho[(x, y)][2] - ho[q_][2])
                if dvo > 0.25 and dvv < 0.08:
                    edges += 1
    ring_d = [abs(hv[p][1] - ho[p][1]) for p in av["ring"] if p in ho]
    sat_v = [hv[p][1] for p in av["solid"]]
    sat_o = [ho[p][1] for p in ao["solid"]]
    return {"edges": edges,
            "ring_dsat": sum(ring_d) / max(1, len(ring_d)),
            "sat_ratio": (sum(sat_o) / max(1, len(sat_o))) / max(1e-9, sum(sat_v) / max(1, len(sat_v)))}

# --- strip render ---------------------------------------------------------------------
# POP experiment (owner request 2026-08-16): loud comparison pass on these four.
# REVERT by setting POP = set().
POP = {134, 136, 138}  # 131 removed: its identity is changing, pop would distort the read

def pop_filter(im):
    """Post-pass: crank chroma on the rendered sprite. Ink and speculars stay quiet."""
    out = im.copy(); po = out.load()
    w, h = out.size
    for y in range(h):
        for x in range(w):
            r, g, b, al = po[x, y]
            if al < HALO_HI:
                continue
            hh, ss, vv = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if ss < 0.12 or vv < 0.30:   # speculars and ink keep their manners
                continue
            ns = min(1.0, ss * 1.6)
            nv = min(1.0, max(0.0, 0.5 + (vv - 0.5) * 1.08))
            nr, ng, nb = (int(round(c * 255)) for c in colorsys.hsv_to_rgb(hh, ns, nv))
            po[x, y] = (nr, ng, nb, al)
    return out

def build(surface):
    vans, ships, protos, glows, mets = [], [], [], [], []
    for i in SHIELDS:
        van = load_vanilla(i, surface)
        ship = load_shipped(i, surface)
        pro = prototype(van, TINTS[i], surface, item_id=i)
        if i in POP:
            pro = pop_filter(pro)
        gl = glow(pro, TINTS[i])  # the chosen treatment: recoloured body + identity rim
        m = metrics(van, pro)
        vans.append(van); ships.append(ship); protos.append(pro); glows.append(gl); mets.append(m)
        print(f"  {i} {NAMES[i]:<16} edges={m['edges']:<4} ringDsat={m['ring_dsat']:.3f} "
              f"satRatio={m['sat_ratio']:.2f}")
    w, h = vans[0].size
    pad, label_h = 2, 14
    W = 16 * (w + pad) + pad
    H = 4 * (h + pad) + pad + label_h
    sheet = Image.new("RGBA", (W, H), (34, 34, 40, 255))
    for r, row in enumerate([vans, ships, protos, glows]):
        for c, im in enumerate(row):
            sheet.paste(im, (pad + c * (w + pad), label_h + pad + r * (h + pad)), im)
    out = os.path.join(SCRATCH, f"proto_strip_{surface}.png")
    sheet.save(out)
    print(f"  -> {out}")
    return mets

if __name__ == "__main__":
    import json
    all_mets = {}
    for surf in ("small", "card"):
        print(f"[{surf}] rows: vanilla / shipped / prototype / prototype+glow")
        all_mets[surf] = build(surf)
    with open(os.path.join(SCRATCH, "proto_metrics.json"), "w") as f:
        json.dump({"ids": SHIELDS, "names": [NAMES[i] for i in SHIELDS], "metrics": all_mets}, f, indent=1)
