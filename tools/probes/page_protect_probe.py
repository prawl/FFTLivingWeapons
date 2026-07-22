"""Page-protection probe: can the in-process DLL write the ability tables, or only Python?

WHY THIS EXISTS
---------------
Provoke currently works only while two Python probes hold it up.  Making it real means the C#
runtime performs the same two writes: the authored inflict-status row, and ability 189's
InflictStatus byte in the LIVE action table.

That is not a given.  The external probe (ability_grant_probe.py, class Mem.write) writes, and if
the write does not read back it falls back to VirtualProtectEx -> PAGE_EXECUTE_READWRITE and puts
the old protection back.  The in-process runtime has NO such fallback: LivingWeapon/Mem.cs is
WriteProcessMemory only, and every shipped writer pre-filters with Mem.Writable, which is a
VirtualQuery that accepts ONLY PAGE_READWRITE, PAGE_WRITECOPY, PAGE_EXECUTE_READWRITE and
PAGE_EXECUTE_WRITECOPY (Mem.cs:124).  A PAGE_READONLY page therefore fails the pre-filter and the
shipped code path returns without writing, silently.

So there are two separate questions and this probe answers both, without a design meeting:

  1. What protection do these pages actually carry?  If they read PAGE_READWRITE, the runtime can
     use the ordinary guarded write path and this whole worry evaporates.
  2. If they are read-only: does a bare WriteProcessMemory succeed anyway?  It often does on image
     pages, because the section was mapped WRITECOPY and the kernel can fault in a private copy --
     the same reason a debugger can plant an int3 in .text without calling VirtualProtect.  If bare
     WPM works, the runtime needs a documented Writable-pre-filter exception, not a new
     VirtualProtect capability.

The known-good control is the JobCommand table at 0x14067E213.  Shipped Barrage writes there every
tick THROUGH the Mem.Writable pre-filter and is live-proven twice, so whatever that page reports is
by definition "a protection the shipped runtime can write".  Any target page reporting the same
value needs no new machinery at all.

SAFETY
------
Read-only by default.  `writetest` is opt-in and writes each byte's OWN CURRENT VALUE back over
itself: a successful write changes nothing and a refused write changes nothing, so the game state
is identical either way.  It still reads back afterwards to prove the call actually landed rather
than merely returning success.  No snapshot is needed because no value is ever altered.

Verbs
-----
  python tools\\probes\\page_protect_probe.py
      Report protection for every address this arc cares about, plus the control.  Read-only.

  python tools\\probes\\page_protect_probe.py writetest
      The above, then rewrite each byte with its own current value and report whether the bare
      WriteProcessMemory call landed.  Changes nothing.

  python tools\\probes\\page_protect_probe.py <addr> [<addr> ...]
      Report protection for arbitrary addresses (0x... accepted).

  python tools\\probes\\page_protect_probe.py --selftest
      Offline checks (no game needed): the protection decoder and the Mem.Writable replica.
"""
import ctypes
import ctypes.wintypes as w
import sys

PROCESS_VM_READ = 0x0010
PROCESS_VM_WRITE = 0x0020
PROCESS_VM_OPERATION = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400
PROC_NAME = "fft_enhanced.exe"

MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01
# Verbatim from LivingWeapon/Mem.cs:124 -- RW | WriteCopy | ExecRW | ExecWriteCopy. The point of
# this probe is to compare a live page against THIS set, so it must not drift from the C#.
MEM_CS_WRITABLE = 0x04 | 0x08 | 0x40 | 0x80

PROTECT_NAMES = {
    0x01: "PAGE_NOACCESS",
    0x02: "PAGE_READONLY",
    0x04: "PAGE_READWRITE",
    0x08: "PAGE_WRITECOPY",
    0x10: "PAGE_EXECUTE",
    0x20: "PAGE_EXECUTE_READ",
    0x40: "PAGE_EXECUTE_READWRITE",
    0x80: "PAGE_EXECUTE_WRITECOPY",
}

# The addresses this arc turns on. Provenance is the 2026-07-22 live session (handoff + the
# ability_table_probe.py verify pass); the control is Barrage.cs:49.
TARGETS = [
    (0x14067E213, "CONTROL: JobCommand table (shipped Barrage writes here every tick)"),
    (0x14078B2DC, "LIVE ability action table, row 0"),
    (0x14078C1AF, "ability 189 InflictStatus byte (live base + 189*20 + 15)"),
    (0x14078961C, "DECOY action table (engine ignores writes here) -- expect same page class"),
    (0x14080FBA0, "inflict-status table, row 0"),
    (0x14080FC4E, "authored inflict row 29 (base + 29*6)"),
]

k32 = ctypes.windll.kernel32


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("th32ProcessID", w.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", w.DWORD),
                ("cntThreads", w.DWORD), ("th32ParentProcessID", w.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", w.DWORD),
                ("szExeFile", ctypes.c_char * 260)]


class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", w.DWORD), ("__align", w.DWORD),
                ("RegionSize", ctypes.c_size_t), ("State", w.DWORD), ("Protect", w.DWORD),
                ("Type", w.DWORD), ("__align2", w.DWORD)]


def find_pid(name=PROC_NAME):
    snap = k32.CreateToolhelp32Snapshot(0x00000002, 0)
    if snap == -1:
        return None
    entry = PROCESSENTRY32()
    entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
    hit = None
    ok = k32.Process32First(snap, ctypes.byref(entry))
    while ok:
        if entry.szExeFile.decode(errors="ignore").lower() == name.lower():
            hit = entry.th32ProcessID
            break
        ok = k32.Process32Next(snap, ctypes.byref(entry))
    k32.CloseHandle(snap)
    return hit


# --------------------------------------------------------------------------- pure decoding
def protect_name(protect):
    """Decode a VirtualQuery Protect value, keeping the modifier bits visible."""
    base = protect & 0xFF
    name = PROTECT_NAMES.get(base, f"0x{base:02X}?")
    if protect & PAGE_GUARD:
        name += "|GUARD"
    if protect & 0x200:  # PAGE_NOCACHE
        name += "|NOCACHE"
    return name


def mem_cs_writable(state, protect):
    """Replica of LivingWeapon/Mem.cs Probe(needWrite: true), minus the range check. Returns True
    when the SHIPPED runtime's pre-filter would allow a write to this page."""
    if state != MEM_COMMIT:
        return False
    if protect & (PAGE_GUARD | PAGE_NOACCESS):
        return False
    return (protect & MEM_CS_WRITABLE) != 0


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION
                                 | PROCESS_QUERY_INFORMATION, False, pid)
        if not self.h:
            raise SystemExit(f"could not open pid {pid} (error {k32.GetLastError()})")

    def query(self, addr):
        mbi = MEMORY_BASIC_INFORMATION()
        got = k32.VirtualQueryEx(self.h, ctypes.c_void_p(addr), ctypes.byref(mbi),
                                 ctypes.sizeof(mbi))
        return mbi if got else None

    def read(self, addr, size):
        buf = (ctypes.c_ubyte * size)()
        got = ctypes.c_size_t(0)
        ok = k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf),
                                   ctypes.c_size_t(size), ctypes.byref(got))
        return bytes(buf) if ok and got.value == size else None

    def raw_write(self, addr, data):
        """Bare WriteProcessMemory, NO VirtualProtect fallback -- deliberately the same shape as
        LivingWeapon/Mem.cs, because the whole question is what the DLL can do unaided.
        Returns (call_succeeded, last_error)."""
        buf = (ctypes.c_ubyte * len(data))(*data)
        put = ctypes.c_size_t(0)
        ok = bool(k32.WriteProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf),
                                         ctypes.c_size_t(len(data)), ctypes.byref(put)))
        return (ok and put.value == len(data)), k32.GetLastError()

    def close(self):
        if self.h:
            k32.CloseHandle(self.h)
            self.h = None


def report(mem, addr, label, do_writetest):
    mbi = mem.query(addr)
    if mbi is None or mbi.State != MEM_COMMIT:
        print(f"0x{addr:X}  UNMAPPED / not committed   {label}")
        return
    base = mbi.BaseAddress or 0
    print(f"0x{addr:X}  {protect_name(mbi.Protect):<26} "
          f"region 0x{base:X}+0x{mbi.RegionSize:X}  alloc {protect_name(mbi.AllocationProtect)}")
    print(f"             {label}")
    allowed = mem_cs_writable(mbi.State, mbi.Protect)
    print(f"             Mem.Writable (shipped C# pre-filter) would {'ALLOW' if allowed else 'REFUSE'} a write here")

    if not do_writetest:
        return
    before = mem.read(addr, 1)
    if before is None:
        print("             writetest: byte unreadable, skipped")
        return
    # Write the byte's OWN value back: lands or is refused, either way nothing changes.
    ok, err = mem.raw_write(addr, before)
    after = mem.read(addr, 1)
    verdict = "LANDED" if ok else f"REFUSED (GetLastError {err})"
    same = "unchanged" if after == before else f"CHANGED {before.hex()} -> {after.hex()} (!!)"
    print(f"             writetest: bare WriteProcessMemory {verdict}; byte {same}")


def verdict(mem):
    """The one line the caller actually wants."""
    ctrl = mem.query(TARGETS[0][0])
    tgt = [mem.query(a) for a, _ in TARGETS[1:]]
    if ctrl is None or any(t is None for t in tgt):
        return "INCONCLUSIVE: something did not map. Is a save loaded?"
    ctrl_ok = mem_cs_writable(ctrl.State, ctrl.Protect)
    tgt_ok = all(mem_cs_writable(t.State, t.Protect) for t in tgt)
    if tgt_ok:
        return ("VERDICT: every table page passes the shipped Mem.Writable pre-filter. The runtime "
                "can make these writes through the ordinary guarded path, no new capability.")
    if ctrl_ok:
        return ("VERDICT: the control page is writable but a table page is NOT. The shipped guarded "
                "write path would silently refuse. Read the writetest lines: if bare WPM LANDED, the "
                "fix is a documented Mem.Writable exception; if it was REFUSED, the runtime needs a "
                "VirtualProtect capability it has never had, and that is a design decision.")
    return ("VERDICT: even the CONTROL page reads unwritable, which contradicts shipped Barrage. "
            "Distrust this run before distrusting the game: wrong build, no save loaded, or the "
            "addresses have moved (see docs/PATCH_REANCHOR.md).")


# --------------------------------------------------------------------------- selftest
def selftest():
    fails = []

    def check(label, got, want):
        if got != want:
            fails.append(f"{label}: got {got!r}, want {want!r}")

    check("readonly name", protect_name(0x02), "PAGE_READONLY")
    check("execread name", protect_name(0x20), "PAGE_EXECUTE_READ")
    check("guard modifier kept", protect_name(0x04 | PAGE_GUARD), "PAGE_READWRITE|GUARD")

    # The pre-filter replica must agree with Mem.cs:123-125 case for case.
    check("RW allowed", mem_cs_writable(MEM_COMMIT, 0x04), True)
    check("WriteCopy allowed", mem_cs_writable(MEM_COMMIT, 0x08), True)
    check("ExecRW allowed", mem_cs_writable(MEM_COMMIT, 0x40), True)
    check("ExecWriteCopy allowed", mem_cs_writable(MEM_COMMIT, 0x80), True)
    check("ReadOnly refused", mem_cs_writable(MEM_COMMIT, 0x02), False)
    check("ExecRead refused", mem_cs_writable(MEM_COMMIT, 0x20), False)
    check("guard refused even when RW", mem_cs_writable(MEM_COMMIT, 0x04 | PAGE_GUARD), False)
    check("uncommitted refused", mem_cs_writable(0x2000, 0x04), False)

    # Target addressing, so a typo cannot masquerade as a live finding.
    check("live inflict byte of 189", 0x14078B2DC + 189 * 20 + 15, 0x14078C1AF)
    check("authored inflict row 29", 0x14080FBA0 + 29 * 6, 0x14080FC4E)
    check("decoy is one table length earlier", 0x14078B2DC - 368 * 20, 0x14078961C)

    if fails:
        print("SELFTEST FAILED")
        for f in fails:
            print("  " + f)
        return 1
    print("selftest OK")
    return 0


def main():
    args = sys.argv[1:]
    if args and args[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if args and args[0] == "--selftest":
        return selftest()

    do_writetest = bool(args) and args[0] == "writetest"
    custom = [int(a, 0) for a in args if a.lower().startswith("0x")]

    pid = find_pid()
    if pid is None:
        print(f"{PROC_NAME} is not running")
        return 2
    mem = Mem(pid)
    try:
        targets = [(a, "(caller-supplied)") for a in custom] or TARGETS
        for addr, label in targets:
            report(mem, addr, label, do_writetest)
            print()
        if not custom:
            print(verdict(mem))
            if not do_writetest:
                print("Run `page_protect_probe.py writetest` to find out whether a bare "
                      "WriteProcessMemory lands. It rewrites each byte with its own value, so it "
                      "cannot change game state.")
    finally:
        mem.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
