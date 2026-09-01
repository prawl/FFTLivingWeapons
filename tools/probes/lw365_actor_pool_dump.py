"""LW-365 READ-ONLY dump/watch of the animation actor pool (the render nodes).

Plain language: every unit on the field owns a fixed animation record; the swing art comes
from two words inside it. This lists them per pool slot, or watches them change during a swing.
Pool: base 0x140D2D5F8, stride 0x548 (2026-08-31 read); +0x148 unit pointer (BattleUnitsBase
0x141853CE0 + slot*0x200), +0x1BC ability id, +0x1C8 swing weapon/effect id, +0x150 mirror,
+0x1FC name id, +0x10 the animation page request register (see anim_poke_probe.py).

    python tools/probes/lw365_actor_pool_dump.py          # one-shot table
    python tools/probes/lw365_actor_pool_dump.py watch    # 600 s change log
"""
import os
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lw346_live_disasm import find_pid, k32, rd as _rd  # noqa: E402

_H = k32.OpenProcess(0x0410, False, find_pid("fft_enhanced.exe"))


def rd(a, n):
    return _rd(_H, a, n)


STRIDE = 0x548
# 0x140D30038, 0x140D30AC8, 0x140D31010 are pool slots; walk down to find the base (unit ptr sanity)
base = 0x140D30038 - 8*STRIDE
def fields(a):
    b = rd(a, 0x220)
    if b is None: return None
    return dict(idx=b[8], unit=struct.unpack_from("<Q", b, 0x148)[0], w150=struct.unpack_from("<H", b, 0x150)[0],
                w1bc=struct.unpack_from("<H", b, 0x1bc)[0], w1c2=struct.unpack_from("<H", b, 0x1c2)[0],
                w1c8=struct.unpack_from("<H", b, 0x1c8)[0], w1ca=struct.unpack_from("<H", b, 0x1ca)[0],
                w1fc=struct.unpack_from("<H", b, 0x1fc)[0], b8f=b[0x8f])
if len(sys.argv) > 1 and sys.argv[1] == "watch":
    last = {}; t0 = time.time()
    while time.time() - t0 < 600:
        for s in range(24):
            a = base + s*STRIDE; f = fields(a)
            if f is None: continue
            key = (f["unit"], f["w1bc"], f["w1c2"], f["w1c8"], f["w1ca"], f["w150"])
            if last.get(s) != key:
                print(f"{time.time()-t0:8.3f}s actor{s:2} @{a:#x} unit={f['unit']:#x} 1bc={f['w1bc']:#x} 1c2={f['w1c2']:#x} 1c8={f['w1c8']} 1ca={f['w1ca']:#x} 150={f['w150']}", flush=True)
                last[s] = key
        time.sleep(0.0005)
else:
    for s in range(24):
        a = base + s*STRIDE; f = fields(a)
        if f and (f["unit"] or f["w1c8"] or f["w1bc"]):
            print(f"actor{s:2} @{a:#x}: idx={f['idx']} unit={f['unit']:#x} 150={f['w150']} 1bc={f['w1bc']:#x} 1c2={f['w1c2']:#x} 1c8={f['w1c8']} 1ca={f['w1ca']:#x} 1fc={f['w1fc']:#x} 8f={f['b8f']:#x}")
