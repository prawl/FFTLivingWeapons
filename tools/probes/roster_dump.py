import ctypes, ctypes.wintypes as w, struct, sys

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

def find_pid(name):
    arr = (w.DWORD * 4096)(); needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not h: continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return h
        k32.CloseHandle(h)
    return None

h = find_pid("fft_enhanced.exe")
if not h:
    print("game not running"); sys.exit(1)

def rpm(addr, n):
    buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw

BASE, STRIDE, SLOTS = 0x1411A18D0, 0x258, 20
RRHAND, ROFF, RLVL, RBR, RFA, RNAME = 0x14, 0x18, 0x1D, 0x1E, 0x1F, 0x230
print(f"{'slot':>4} {'nameId':>6} {'lvl':>3} {'br':>3} {'fa':>3} {'mainHand':>8} {'offHand':>7}")
for s in range(SLOTS):
    b = BASE + s * STRIDE
    d = rpm(b, 0x238)
    if d is None: print(f"{s:>4} <unreadable>"); continue
    lvl, br, fa = d[RLVL], d[RBR], d[RFA]
    if lvl == 0: continue   # empty slot
    rr = struct.unpack_from('<H', d, RRHAND)[0]
    ro = struct.unpack_from('<H', d, ROFF)[0]
    nm = struct.unpack_from('<H', d, RNAME)[0]
    print(f"{s:>4} {nm:>6} {lvl:>3} {br:>3} {fa:>3} {rr:>8} {ro:>7}")
