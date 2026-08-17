"""Shield flashy glow via BODY RECONSTRUCTION (v2 -- the fresh-render proof caught six
cards whose deployed bodies predate later engine drift, so bodies are not re-rendered).

The glow layer only ever writes OUTSIDE the smoothed body silhouette, and every body
pass preserves vanilla's alpha channel, so:
  reconstructed body = deployed image with all pixels outside smoothed(vanilla) restored
  to vanilla. Proof per icon: default glow over the reconstruction must equal the
  deployed image bit-for-bit. Only a 32/32 green board deploys the flashy round.
"""
import hashlib, os, shutil, subprocess, sys
from PIL import Image

BANK = r"C:\Users\ptyRa\Downloads\fft_ref\session_bank_2026-08-16"
DURABLE = r"C:\Users\ptyRa\Downloads\fft_ref\shield_flashy_2026-08-16"
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ramp_v2 as rp

INSTALL_ICON = (r"c:\program files (x86)\steam\steamapps\common"
                r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods"
                r"\prawl.fft.livingweapons\FFTIVC\data\enhanced\ui\ffto\icon")
ROLLBACK = os.path.join(DURABLE, "rollback_current")   # taken by the v1 run, verified below
FLASHY = os.path.join(DURABLE, "flashy_bake")
os.makedirs(FLASHY, exist_ok=True)
FLASH = dict(inner_a=235, outer_a=135, third_a=60, min_de=40.0)


def md5(p):
    return hashlib.md5(open(p, "rb").read()).hexdigest()


def tex_dir(surface):
    return os.path.join(INSTALL_ICON,
                        "equip_item" if surface == "card" else "equip_item_s", "texture")


def stem_for(i, surface):
    return f"ei_{i:03d}_uitx" if surface == "card" else f"ei_s_{i:03d}_uitx"


def smoothed_mask(im):
    """Same contour rule as glow(): alpha >= 160 body, majority-smoothed."""
    w, h = im.size
    px = im.load()
    body = {(x, y) for y in range(h) for x in range(w) if px[x, y][3] >= 160}
    out = set()
    for y in range(h):
        for x in range(w):
            n = sum((x + dx, y + dy) in body for dy in (-1, 0, 1) for dx in (-1, 0, 1)
                    if (dx, dy) != (0, 0))
            if ((x, y) in body and n >= 3) or ((x, y) not in body and n >= 5):
                out.add((x, y))
    return out


out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq fft_enhanced.exe"],
                     capture_output=True, text=True).stdout
assert "fft_enhanced.exe" not in out.lower(), "GAME IS RUNNING - refuse to deploy"

# rollback copies must exist and still match the install (taken before any deploy)
for i in rp.SHIELDS:
    for surface in ("small", "card"):
        stem = stem_for(i, surface)
        bak = os.path.join(ROLLBACK, f"{stem}.tex")
        assert os.path.exists(bak), f"rollback copy missing: {stem}"
        assert md5(bak) == md5(os.path.join(tex_dir(surface), f"{stem}.tex")), \
            f"install changed since rollback copy: {stem}"
print("rollback copies verified against the live install (32/32)", flush=True)

# --- reconstruct + prove --------------------------------------------------------------
recon = {}
fails = []
for i in rp.SHIELDS:
    for surface in ("small", "card"):
        stem = stem_for(i, surface)
        van = rp.load_vanilla(i, surface)
        deployed = Image.open(os.path.join(BANK, "bake_work", f"{stem}.png")).convert("RGBA")
        # bake_work png content == install tex content, hash-proven in the v1 run
        mask = smoothed_mask(van)
        body = deployed.copy()
        bp, vp = body.load(), van.load()
        w, h = van.size
        for y in range(h):
            for x in range(w):
                if (x, y) not in mask:
                    bp[x, y] = vp[x, y]
        gl = rp.glow(body, rp.TINTS[i])
        if list(gl.convert("RGBA").getdata()) != list(deployed.getdata()):
            # rim colour may predate a tint change: extract the deployed rim directly
            # (every alpha-170 pixel the old glow wrote carries the pure rim colour)
            dp = deployed.load()
            rims = {dp[x, y][:3] for y in range(h) for x in range(w)
                    if dp[x, y][3] == 170 and (x, y) not in mask}
            if len(rims) == 1:
                gl2 = rp.glow(body, rp.TINTS[i], rim_rgb=rims.pop())
                if list(gl2.convert("RGBA").getdata()) == list(deployed.getdata()):
                    recon[(i, surface)] = (body, "extracted-rim")
                    continue
            fails.append(stem)
            continue
        recon[(i, surface)] = (body, "tint-rim")
if fails:
    print("RECONSTRUCTION PROOF FAILED - NOTHING DEPLOYED:")
    for f in fails:
        print("  ", f)
    sys.exit(1)
n_ext = sum(1 for _, kind in recon.values() if kind == "extracted-rim")
print(f"reconstruction proof: 32/32 exact ({n_ext} via extracted rim colour)", flush=True)

# --- flashy glow, bake, deploy --------------------------------------------------------
for i in rp.SHIELDS:
    rim_sat = max(rp.TINTS[i][1], 0.80)
    for surface in ("small", "card"):
        stem = stem_for(i, surface)
        body, _ = recon[(i, surface)]
        gl = rp.glow(body, rp.TINTS[i], rim_sat=rim_sat, **FLASH)
        png = os.path.join(FLASHY, f"{stem}.png")
        gl.save(png)
        r = subprocess.run([rp.FF16, "img-conv", "-i", png, "--no-chunk-compression"],
                           capture_output=True, text=True)
        tex = os.path.join(FLASHY, f"{stem}.tex")
        assert os.path.exists(tex), f"img-conv produced no tex for {stem}: {r.stdout} {r.stderr}"
        dst = os.path.join(tex_dir(surface), f"{stem}.tex")
        shutil.copy(tex, dst)
        assert md5(tex) == md5(dst), f"deploy verify failed for {stem}"
    print(f"{i} deployed", flush=True)
print("SHIELD FLASHY ROUND DEPLOYED: 32 textures hash-verified; rollback in", ROLLBACK)
