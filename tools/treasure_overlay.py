"""Treasure Master overlay -- draws a marker ON each treasure tile by projecting the tile's world
position through the game's live camera VIEW matrix. External, transparent, click-through window
(markers only, no panels): zero game-process modification, no render hook, no Denuvo risk.

Geometry: the camera view matrix sits at a module-static address (found via a rotation differential;
orthonormal 3x3 = FFT's 2:1 iso). We read it every frame, project world(tile) -> view, then a simple
orthographic view->screen (SCALE, CX, CY). The screen part is CALIBRATED live -- use the keys below
until the diamonds land on the tiles; the params print to this console so we can lock them in.

  python tools\\treasure_overlay.py        # Sledge Weald's 4 treasures (Id 74), prototype

KEYS (focus the overlay window):  arrows = move all markers (CX/CY) | +/- = SCALE | [ ] = tile size
  ; ' = height scale | y = flip screenY sign | x = flip world axis | r = re-read tiles | Esc = quit
Run the game in BORDERLESS/WINDOWED (an overlay can't draw over exclusive-fullscreen D3D12).
"""
import ctypes
import ctypes.wintypes as w
import struct
import tkinter as tk

VIEW_MATRIX_ADDR = 0x1407D61EC          # module-static; game updates the values live
CURSOR_X, CURSOR_Y = 0x140C64A54, 0x140C6496C   # live grid cursor (u8) -- the calibration reference
# Sledge Weald (table Id 74): (x, y, item). Heights default 0 for first calibration.
TILES = [(0, 1, "Bow Gun"), (1, 9, "Escutcheon"), (5, 11, "rare144"), (6, 6, "TRAP")]

# --- calibration params (tune live with the keys; printed to console) ---
P = {"SCALE": 40.0, "CX": 960.0, "CY": 540.0, "TS": 1.0, "HS": 1.0, "YSIGN": -1.0, "XSIGN": 1.0}

PROCESS_VM_READ, PROCESS_QUERY_INFORMATION = 0x0010, 0x0400
k32, psapi, user32 = ctypes.windll.kernel32, ctypes.windll.psapi, ctypes.windll.user32


def find_handle(name="fft_enhanced.exe"):
    arr, needed = (w.DWORD * 4096)(), w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return h
        k32.CloseHandle(h)
    return None


HANDLE = find_handle()


def read_matrix():
    if not HANDLE:
        return None
    buf, got = ctypes.create_string_buffer(64), ctypes.c_size_t()
    if not k32.ReadProcessMemory(HANDLE, ctypes.c_void_p(VIEW_MATRIX_ADDR), buf, 64, ctypes.byref(got)) or got.value != 64:
        return None
    m = struct.unpack("<16f", buf.raw)
    if any(v != v or abs(v) > 1e9 for v in m):
        return None
    return m


def read_u8(addr):
    buf, got = ctypes.create_string_buffer(1), ctypes.c_size_t()
    if k32.ReadProcessMemory(HANDLE, ctypes.c_void_p(addr), buf, 1, ctypes.byref(got)) and got.value == 1:
        return buf.raw[0]
    return None


def read_cursor():
    if not HANDLE:
        return None
    cx, cy = read_u8(CURSOR_X), read_u8(CURSOR_Y)
    return (cx, cy) if cx is not None else None


def _view(m, wx, wy, wz):
    return (wx * m[0] + wy * m[4] + wz * m[8] + m[12],
            wx * m[1] + wy * m[5] + wz * m[9] + m[13])


def project(m, gx, gy, h):
    # tile -> view, RELATIVE to the map-center tile so the big world translation cancels and
    # CX/CY/SCALE work in sane screen-pixel ranges. world = (x*TS, height*HS, z*TS).
    vx, vy = _view(m, P["XSIGN"] * gx * P["TS"], h * P["HS"], gy * P["TS"])
    rx, ry = _view(m, P["XSIGN"] * 7.5 * P["TS"], 0.0, 6.0 * P["TS"])
    sx = P["CX"] + P["SCALE"] * (vx - rx)
    sy = P["CY"] + P["YSIGN"] * P["SCALE"] * (vy - ry)
    return sx, sy


root = tk.Tk()
root.overrideredirect(True)
root.attributes("-topmost", True)
W, H = root.winfo_screenwidth(), root.winfo_screenheight()
root.geometry(f"{W}x{H}+0+0")
TRANSP = "#010101"
root.configure(bg=TRANSP)
root.attributes("-transparentcolor", TRANSP)
canvas = tk.Canvas(root, bg=TRANSP, highlightthickness=0)
canvas.pack(fill="both", expand=True)
P["CX"], P["CY"] = W / 2, H / 2


def show_params():
    print("  ".join(f"{k}={P[k]:.1f}" for k in ("SCALE", "CX", "CY", "TS", "HS", "YSIGN", "XSIGN")))


# Global F-key tuning (FFT ignores F-keys; works while the GAME is focused). VK: F1=0x70..F12=0x7B.
# held = continuous nudge each frame; toggles edge-detected.
CONT = [(0x70, "SCALE", -2), (0x71, "SCALE", 2), (0x72, "CX", -12), (0x73, "CX", 12),
        (0x74, "CY", -12), (0x75, "CY", 12), (0x76, "TS", -0.25), (0x77, "TS", 0.25),
        (0x7A, "HS", -0.25), (0x7B, "HS", 0.25)]
TOGGLE = {0x78: "YSIGN", 0x79: "XSIGN"}   # F9, F10
_prev = {}


def poll_keys():
    changed = False
    for vk, key, d in CONT:
        if user32.GetAsyncKeyState(vk) & 0x8000:
            P[key] += d
            changed = True
    for vk, key in TOGGLE.items():
        down = bool(user32.GetAsyncKeyState(vk) & 0x8000)
        if down and not _prev.get(vk):
            P[key] = -P[key]
            changed = True
        _prev[vk] = down
    if user32.GetAsyncKeyState(0x1B) & 0x8000:   # Esc
        root.destroy()
    if changed:
        show_params()


def redraw():
    poll_keys()
    canvas.delete("all")
    # fixed sanity dot at the projection center -- if THIS is invisible, it's a window/fullscreen
    # issue, not the projection. If it shows but the diamonds don't, it's the math.
    canvas.create_oval(P["CX"] - 7, P["CY"] - 7, P["CX"] + 7, P["CY"] + 7, outline="#ff00ff", width=3)
    m = read_matrix()
    if m:
        for gx, gy, label in TILES:
            sx, sy = project(m, gx, gy, 0.0)
            col = "#ff3030" if label == "TRAP" else "#30ff60"
            r = 16
            canvas.create_polygon(sx, sy - r, sx + r, sy, sx, sy + r, sx - r, sy,
                                  outline=col, width=3, fill="")
            canvas.create_oval(sx - 2, sy - 2, sx + 2, sy + 2, fill=col, outline=col)
            canvas.create_text(sx + r + 4, sy, text=f"({gx},{gy}) {label}", fill=col,
                               anchor="w", font=("Consolas", 10, "bold"))
        # live cursor crosshair -- THE calibration anchor: move the cursor, tune F-keys until this
        # white cross rides on the real game cursor; then every diamond is right too.
        cur = read_cursor()
        if cur and cur[0] is not None:
            sx, sy = project(m, cur[0], cur[1], 0.0)
            canvas.create_line(sx - 15, sy, sx + 15, sy, fill="#ffffff", width=2)
            canvas.create_line(sx, sy - 15, sx, sy + 15, fill="#ffffff", width=2)
            canvas.create_text(sx + 17, sy - 11, text=f"CUR({cur[0]},{cur[1]})", fill="#ffffff",
                               anchor="w", font=("Consolas", 10, "bold"))
    root.after(50, redraw)


print(f"game handle: {'ok' if HANDLE else 'NOT FOUND'} | screen {W}x{H} | matrix at {VIEW_MATRIX_ADDR:#x}")
print("Keep the GAME focused. Tune with F-keys:")
print("  F1/F2 SCALE  |  F3/F4 left-right  |  F5/F6 up-down  |  F7/F8 tile-size  |  F9 flipY  F10 flipX  |  F11/F12 height  |  Esc quit")
show_params()
redraw()
root.mainloop()
