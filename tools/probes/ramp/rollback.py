"""Roll the 16 shields back to the pre-flashy-glow deploy (the owner-passed standard
glow). Run with the game closed: python rollback.py"""
import hashlib, os, shutil, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
ROLLBACK = os.path.join(HERE, "rollback_current")
INSTALL_ICON = (r"c:\program files (x86)\steam\steamapps\common"
                r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods"
                r"\prawl.fft.livingweapons\FFTIVC\data\enhanced\ui\ffto\icon")

out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq fft_enhanced.exe"],
                     capture_output=True, text=True).stdout
assert "fft_enhanced.exe" not in out.lower(), "GAME IS RUNNING - close it first"

n = 0
for fn in sorted(os.listdir(ROLLBACK)):
    sub = "equip_item_s" if fn.startswith("ei_s_") else "equip_item"
    src = os.path.join(ROLLBACK, fn)
    dst = os.path.join(INSTALL_ICON, sub, "texture", fn)
    shutil.copy(src, dst)
    same = (hashlib.md5(open(src, "rb").read()).hexdigest()
            == hashlib.md5(open(dst, "rb").read()).hexdigest())
    assert same, f"verify failed: {fn}"
    n += 1
print(f"rolled back {n} shield textures (restart the game to see it)")
