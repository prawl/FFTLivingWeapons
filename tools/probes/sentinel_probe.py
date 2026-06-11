import ctypes, ctypes.wintypes as w, struct, sys

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

def find_pid(name):
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not h: continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return arr[i], h
        k32.CloseHandle(h)
    return None, None

pid, h = find_pid("fft_enhanced.exe")
if not h:
    print("game not running"); sys.exit(1)

def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw

def u32(a):
    b = rpm(a, 4); return struct.unpack('<I', b)[0] if b else None
def u16(a):
    b = rpm(a, 2); return struct.unpack('<H', b)[0] if b else None
def u8(a):
    b = rpm(a, 1); return b[0] if b else None

print(f"pid={pid}")
print(f"slot0      = {u32(0x14077CA30):#x}")
print(f"slot9      = {u32(0x14077CA54):#x}")
print(f"battleMode = {u8(0x140900650)}")
print(f"eventId    = {u16(0x14077CA94)}")
print(f"pauseFlag  = {u8(0x140C64A5C)}")
print(f"submenu    = {u8(0x140D3A10C)}")
