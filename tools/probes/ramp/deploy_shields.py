"""Bake the current chosen treatment (ramp body + dark-outlined identity glow) for
SHIELDS and HELMS and deploy into the LIVE Reloaded install for the in-game look.

Mirrors build()'s selection exactly: KEEP_SHIPPED items keep the shipped body and only
gain the glow; POP items get the pop filter; everything else runs the ramp engine.
Backs up any install texture it has not backed up before (install_backup/), so the
whole deploy stays reversible. Does NOT touch the repo mod tree (LW-247 owns that)."""
import os, shutil, subprocess
import ramp_prototype as rp

INSTALL = r"c:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods\prawl.fft.livingweapons"
SCRATCH = os.path.dirname(os.path.abspath(__file__))
BACKUP = os.path.join(SCRATCH, "install_backup")
WORK = os.path.join(SCRATCH, "bake_work")
os.makedirs(BACKUP, exist_ok=True)
os.makedirs(WORK, exist_ok=True)

def tex_dir(surface):
    sub = "equip_item" if surface == "card" else "equip_item_s"
    return os.path.join(INSTALL, "FFTIVC", "data", "enhanced", "ui", "ffto", "icon", sub, "texture")

deployed = 0
for surface in ("small", "card"):
    dst_dir = tex_dir(surface)
    for i in rp.SHIELDS + rp.HELMS:
        stem = f"ei_{i:03d}_uitx" if surface == "card" else f"ei_s_{i:03d}_uitx"
        dst = os.path.join(dst_dir, f"{stem}.tex")
        bak = os.path.join(BACKUP, f"{stem}.tex")
        if os.path.exists(dst) and not os.path.exists(bak):
            shutil.copy(dst, bak)
        if i in rp.KEEP_SHIPPED:
            base = rp.load_shipped(i, surface)
        else:
            van = rp.load_vanilla(i, surface)
            base = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
            if i in rp.POP:
                base = rp.pop_filter(base)
        final = rp.glow(base, rp.TINTS[i])
        png = os.path.join(WORK, f"{stem}.png")
        final.save(png)
        subprocess.run([rp.FF16, "img-conv", "-i", png, "--no-chunk-compression"],
                       capture_output=True)
        tex = os.path.join(WORK, f"{stem}.tex")
        assert os.path.exists(tex), f"img-conv produced no tex for {stem}"
        shutil.copy(tex, dst)
        deployed += 1
print(f"deployed {deployed} textures into {INSTALL}")
