"""Live spike: make Warbrand (recat flail->sword, item id 67) SWING sword art.

!!! CORRECTED 2026-07-01 -- READ FIRST. The render-only premise below is WRONG.
The render global 0x1407B0764 drives DAMAGE, not just art: publishing a different
id through pub2 (this probe's swaptest, and the since-removed WeaponArtHook that
shipped the same idea) makes Warbrand read the SWAPPED weapon's WP, so its damage
collapses. "CWeapon+0x20 is untouched, so damage/stats/name stay Warbrand's" and
"the damage number should stay the same" (both below) are FALSE -- the swaptest
corrupts damage exactly like the hook did. There is NO render-only lever: art is
welded to the weapon id, the same id that drives damage. Correct fix = relocate
the item onto a native sword id. See the weapon-blade-art-walled memory +
docs/LIVE_LEDGER.md. The disasm capture below is accurate; only its render-only
CONCLUSION is retracted.

WHY A CODE PATCH AND NOT A PLAIN MEMORY WRITE
---------------------------------------------
The battlefield weapon-model id is the render global 0x1407B0764. But publisher
"pub2" (0x14F5891FC) rewrites that global from CWeapon+0x20 (the equipped id) on
EVERY swing, and the weapon mesh reads it the SAME frame -- so a plain write to
the global loses the race (RenderModelHold, an 8ms hold, produced a clean fist for
id261, not even a flicker). Writing the SOURCE (CWeapon+0x20) works but that field
also drives damage, so it neuters the weapon. The only render-only lever is to
change pub2's copy step itself. Captured disasm (2026-06-27, image base
0x140000000, no ASLR):

  0x14F5891FC   movzx eax, word [rbx+0x20]   ; 0F B7 43 20  -- pub2 loads the equipped id
  0x14F589200   mov   [0x1407B0764], ax      ; ...publishes it to the render global
  0x14F589212   cmp   ax, bp                  ; clamp checks (skipped when id < cap 261)
  0x14F589217   cmp   r14w, ax / ja ...       ; id 67 < 261 -> global keeps 67 -> FLAIL art

THE EXPERIMENT (this probe): overwrite the 4-byte load at 0x14F5891FC with
`mov ax, <sword_id>` (66 B8 lo hi -- SAME length, fully reversible). pub2 then
publishes <sword_id> instead of the real id, so the swinging unit renders that
sword's art -- proven at the source, before the same-frame read. CWeapon+0x20 is
untouched, so damage/stats/name stay Warbrand's.

This is UNCONDITIONAL (every rendered weapon swings <sword_id> art during the hold)
-- that is fine for a concept test / picking the sword. The shipped fix wraps the
same idea in a 5-byte redirect that only remaps id 67 and leaves other weapons alone.

Register/flag safety of the swap: ax is reused at 0x14F589212, but `mov ax,imm`
just replaces the loaded id (which we WANT); the upper eax is dead (reloaded at
0x14F58921D), and no other register is touched. Reversible on Ctrl-C, on a keypress,
and on the hold timeout (try/finally ALWAYS restores). A raw write to a code page
AVs and crashes the game; this uses the debugger-style cross-process guarded write
(VirtualProtectEx -> WriteProcessMemory -> restore), so no AV can occur.

USAGE (game running; equip Warbrand id 67 on a deployed unit, be in a battle):
  python tools\\probes\\warbrand_art_probe.py disasm         # confirm pub2 bytes match the capture
  python tools\\probes\\warbrand_art_probe.py read           # render global + Ramza CWeapon
  python tools\\probes\\warbrand_art_probe.py swaptest 35    # publish id 35 (Excalibur) art for ~30s
  python tools\\probes\\warbrand_art_probe.py swaptest 37 45 # id 37 art, hold 45s
  python tools\\probes\\warbrand_art_probe.py restore        # force-restore original bytes (safety net)

Sweep a few sword ids to pick the best-looking one for Warbrand, e.g. a plain
broadsword vs a KnightSword (37 = Chaos Blade art). Watch: art should change AND
the Warbrand damage number should stay the same.
Env: FFT_PID overrides the auto-picked pid (largest-working-set FFT_enhanced.exe).
"""
import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

try:
    import msvcrt  # Windows-only early-restore keypress; absence just disables it
except ImportError:
    msvcrt = None

# --- addresses (live-verified 2026-06-27; VA == live addr, no ASLR) ---
PUB2_LOAD     = 0x14F5891FC          # movzx eax, word [rbx+0x20]  (the id load we swap)
ORIG_BYTES    = bytes((0x0F, 0xB7, 0x43, 0x20))  # 0F B7 43 20
RENDER_GLOBAL = 0x1407B0764          # u16 battlefield weapon-model id pub2 publishes
CWEAPON       = 0x141855D00          # u16 Ramza's equipped main-hand id (live)

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
PROCESS_VM_OPERATION = 0x0008
PROCESS_VM_READ      = 0x0010
PROCESS_VM_WRITE     = 0x0020
PROCESS_QUERY_INFORMATION = 0x0400
PAGE_EXECUTE_READWRITE = 0x40

k32.OpenProcess.restype = wintypes.HANDLE
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.CloseHandle.argtypes = [wintypes.HANDLE]
k32.ReadProcessMemory.argtypes = [wintypes.HANDLE, wintypes.LPCVOID, wintypes.LPVOID,
                                  ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.WriteProcessMemory.argtypes = [wintypes.HANDLE, wintypes.LPVOID, wintypes.LPCVOID,
                                   ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.VirtualProtectEx.argtypes = [wintypes.HANDLE, wintypes.LPVOID, ctypes.c_size_t,
                                 wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]


def find_pid():
    if os.environ.get("FFT_PID"):
        return int(os.environ["FFT_PID"])
    out = subprocess.check_output(
        ["tasklist", "/fi", "imagename eq FFT_enhanced.exe", "/fo", "csv", "/nh"],
        text=True, errors="ignore")
    best_pid, best_mem = None, -1
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 5 and parts[0].lower().startswith("fft_enhanced"):
            pid = int(parts[1])
            mem = int(parts[4].replace(",", "").replace("K", "").strip() or 0)
            if mem > best_mem:
                best_pid, best_mem = pid, mem
    if best_pid is None:
        raise SystemExit("FFT_enhanced.exe not running")
    return best_pid


def open_proc(pid):
    access = (PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE
              | PROCESS_QUERY_INFORMATION)
    h = k32.OpenProcess(access, False, pid)
    if not h:
        raise SystemExit(f"OpenProcess({pid}) failed err={ctypes.get_last_error()} (run elevated?)")
    return h


def rpm(h, va, n):
    buf = (ctypes.c_ubyte * n)()
    got = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(va), buf, n, ctypes.byref(got)):
        raise SystemExit(f"ReadProcessMemory @0x{va:X} failed err={ctypes.get_last_error()}")
    return bytes(buf[:got.value])


def rd_u16(h, va):
    return int.from_bytes(rpm(h, va, 2), "little")


def wpm_guarded(h, va, data):
    """Debugger-style guarded write: flip page RWX, write, restore protection, flush icache."""
    n = len(data)
    old = wintypes.DWORD(0)
    if not k32.VirtualProtectEx(h, ctypes.c_void_p(va), n, PAGE_EXECUTE_READWRITE, ctypes.byref(old)):
        raise SystemExit(f"VirtualProtectEx @0x{va:X} failed err={ctypes.get_last_error()}")
    try:
        buf = (ctypes.c_ubyte * n)(*data)
        wrote = ctypes.c_size_t(0)
        if not k32.WriteProcessMemory(h, ctypes.c_void_p(va), buf, n, ctypes.byref(wrote)):
            raise SystemExit(f"WriteProcessMemory @0x{va:X} failed err={ctypes.get_last_error()}")
    finally:
        tmp = wintypes.DWORD(0)
        k32.VirtualProtectEx(h, ctypes.c_void_p(va), n, old.value, ctypes.byref(tmp))
    k32.FlushInstructionCache(h, ctypes.c_void_p(va), n)


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


def do_disasm(h):
    code = rpm(h, PUB2_LOAD, 40)
    print(f"pub2 @0x{PUB2_LOAD:X}  first bytes = {hexs(code[:8])}")
    match = code[:4] == ORIG_BYTES
    print(f"  load instr [{hexs(code[:4])}] {'== expected 0F B7 43 20 (OK)' if match else '!= expected -- BUILD MAY HAVE MOVED; do NOT swaptest'}")
    try:
        from capstone import Cs, CS_ARCH_X86, CS_MODE_64
        md = Cs(CS_ARCH_X86, CS_MODE_64)
        for insn in md.disasm(code, PUB2_LOAD):
            print(f"    {insn.address:#014x}  {insn.bytes.hex():<20} {insn.mnemonic} {insn.op_str}")
    except ImportError:
        print("  (capstone not installed -- byte check above is authoritative)")
    return match


def do_read(h):
    g = rd_u16(h, RENDER_GLOBAL)
    w = rd_u16(h, CWEAPON)
    print(f"render global @0x{RENDER_GLOBAL:X} = {g}   (last-published battlefield weapon-model id)")
    print(f"Ramza CWeapon @0x{CWEAPON:X} = {w}   (equipped main-hand id; equip Warbrand -> expect 67)")


def do_swaptest(h, sword_id, hold_s):
    if not (0 <= sword_id <= 0xFFFF):
        raise SystemExit("sword_id must be 0..65535")
    before = rpm(h, PUB2_LOAD, 4)
    print(f"pid load  0x{PUB2_LOAD:X} = {hexs(before)}")
    if before != ORIG_BYTES:
        raise SystemExit(f"ABORT: expected {hexs(ORIG_BYTES)} (movzx eax,[rbx+0x20]); found {hexs(before)}.\n"
                         f"       Either already patched, or the build moved -- run `disasm` and re-check.")
    patched = bytes((0x66, 0xB8, sword_id & 0xFF, (sword_id >> 8) & 0xFF))  # mov ax, imm16
    try:
        wpm_guarded(h, PUB2_LOAD, patched)
        after = rpm(h, PUB2_LOAD, 4)
        ok = after == patched
        print(f"        now = {hexs(after)}  ({'PATCHED: mov ax, ' + str(sword_id) if ok else 'MISMATCH!'})")
        if not ok:
            return
        print(f"\n>>> pub2 now publishes id {sword_id} for EVERY rendered weapon. Go swing Warbrand.")
        print(f">>> Watch: swing art should be id {sword_id}'s; Warbrand DAMAGE should be unchanged.")
        print(f">>> Auto-restores in {hold_s}s. Press any key in this window to restore now.\n")
        end = time.time() + hold_s
        while time.time() < end:
            rem = int(end - time.time()) + 1
            print(f"    holding... {rem:3d}s left  (render global now = {rd_u16(h, RENDER_GLOBAL)})   ", end="\r")
            if msvcrt and msvcrt.kbhit():
                msvcrt.getch()
                break
            time.sleep(1)
    finally:
        wpm_guarded(h, PUB2_LOAD, ORIG_BYTES)
        restored = rpm(h, PUB2_LOAD, 4)
        print(f"\nrestored 0x{PUB2_LOAD:X} = {hexs(restored)}  "
              f"({'OK' if restored == ORIG_BYTES else 'RESTORE FAILED -- run `restore`!'})")


def do_restore(h):
    before = rpm(h, PUB2_LOAD, 4)
    print(f"before 0x{PUB2_LOAD:X} = {hexs(before)}")
    if before == ORIG_BYTES:
        print("  already original; nothing to do.")
        return
    wpm_guarded(h, PUB2_LOAD, ORIG_BYTES)
    after = rpm(h, PUB2_LOAD, 4)
    print(f"after  0x{PUB2_LOAD:X} = {hexs(after)}  ({'OK' if after == ORIG_BYTES else 'STILL WRONG!'})")


def main():
    a = sys.argv[1:]
    if not a or a[0] in ("-h", "--help"):
        print(__doc__)
        return
    pid = find_pid()
    h = open_proc(pid)
    try:
        cmd = a[0]
        if cmd == "disasm":
            do_disasm(h)
        elif cmd == "read":
            do_read(h)
        elif cmd == "swaptest":
            if len(a) < 2:
                raise SystemExit("usage: swaptest <sword_id> [hold_seconds=30]")
            sword_id = int(a[1], 0)
            hold_s = int(a[2]) if len(a) > 2 else 30
            do_swaptest(h, sword_id, hold_s)
        elif cmd == "restore":
            do_restore(h)
        else:
            print("unknown subcommand; see --help")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
