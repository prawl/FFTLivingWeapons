"""Read only, offline. Which of the five static tables the mod writes are file backed in the exe
image, what their vanilla bytes are, and which code sites address them.

Why. LW-399 puts a vanilla byte check in front of every writer that targets a fixed game table
(Bulwark's terrain grid, the weapon palette banks, the poach den store, Provoke's action and
inflict tables, the WP table hold). A check needs expected bytes with provenance. This probe maps
each pinned VA through the PE section table the way sentinel_file_image_probe.py does: a file
backed span is printed as the game maps it; an address past a section's raw data reads zero
until the game writes it, so it can never be a vanilla byte signature. For such tables the probe
also hunts the exe's own code for the instructions that address them (an image relative disp32
for the terrain grid, RIP relative disp32 for the rest), since those instruction bytes are the
honest file backed landmark that says "the game still keeps this table here".

The PINS are hand copied from Offsets.cs, Provoke.Policy.cs and Barrage.cs and must be re-copied
after any re-anchor. Row 189 of the action table and row 29 of the inflict table are printed on
their own because Provoke writes exactly those bytes.

Usage. python -I tools/probes/static_table_file_image_probe.py [path\to\fft_enhanced.exe]
"""
import json
import struct
import sys
from array import array
from pathlib import Path

HERE = Path(__file__).resolve()
sys.path.insert(0, str(HERE.parents[1]))
from lib.paths import STEAM_FFT  # noqa: E402

IMAGE_BASE = 0x140000000

# name, preferred base VA, bytes to print, source line
PINS = [
    ("PathTerrainGrid (first 8 tiles)", 0x140D8DCB0, 64, "Offsets.cs PathTerrainGrid"),
    ("MapDimsWH", 0x140C6AD6A, 2, "Offsets.cs MapDimsWH"),
    ("WeaponPaletteBankA (palette 0)", 0x140D35750, 32, "Offsets.cs WeaponPaletteBankA"),
    ("WeaponPaletteBankB (palette 0)", 0x140D35950, 32, "Offsets.cs WeaponPaletteBankB"),
    ("PoachStoreBase (96 keys)", 0x1411A7A1B, 96, "Offsets.cs PoachStoreBase"),
    ("LiveActionTable row 0", 0x14078B2DC, 20, "Offsets.cs LiveActionTable"),
    ("DecoyActionTable row 0", 0x14078961C, 20, "Provoke.Policy.cs DecoyActionTable"),
    ("InflictTable rows 0..3", 0x14080FBA0, 24, "Offsets.cs InflictTable"),
    ("ItemStatsBase rows 0..3", 0x14080F690, 32, "Offsets.cs ItemStatsBase"),
    ("JobCommand rec8 (control, Rec8Sig)", 0x14067E213 + 8 * 25, 8, "Barrage.AbilityBase + 8*RecSize"),
    ("RosterBase (control, save state)", 0x1411A7D10, 16, "Offsets.cs RosterBase"),
]

ACTION_ROWS, ACTION_STRIDE, ACTION_INFLICT_OFF = 368, 20, 15
INFLICT_ROWS, INFLICT_STRIDE = 128, 6
ITEM_ROWS, ITEM_STRIDE, ITEM_WP_OFF = 128, 8, 4
PROVOKE_ABILITY, PROVOKE_INFLICT_ROW = 189, 29

# Data addresses whose referencing instructions are worth listing (RIP relative disp32 scan).
RIP_TARGETS = [
    ("WeaponPaletteBankA", 0x140D35750),
    ("WeaponPaletteBankB", 0x140D35950),
    ("PathTerrainGrid", 0x140D8DCB0),
    ("PathTerrainGrid+2", 0x140D8DCB2),
    ("PoachStoreBase", 0x1411A7A1B),
    ("LiveActionTable", 0x14078B2DC),
    ("InflictTable", 0x14080FBA0),
    ("ItemStatsBase", 0x14080F690),
    ("RosterBase (control, expect many)", 0x1411A7D10),
]
# Image relative disp32 needles (the terrain reader indexes [base + idx*8 + rva]).
IMM_TARGETS = [("PathTerrainGrid rva+0", 0xD8DCB0), ("PathTerrainGrid rva+2", 0xD8DCB2),
               ("PathTerrainGrid rva+3", 0xD8DCB3), ("PathTerrainGrid rva+6", 0xD8DCB6),
               ("ItemStatsBase rva", 0x80F690), ("ItemStatsBase rva+4", 0x80F694),
               ("LiveActionTable rva", 0x78B2DC), ("LiveActionTable rva+15", 0x78B2EB),
               ("DecoyActionTable rva", 0x78961C), ("InflictTable rva", 0x80FBA0)]

# Instruction spans proposed as door landmarks. Start VA, length, what the bytes decode to.
# Each start is the disp32 hit minus its opcode and ModRM/SIB bytes, decoded by hand below.
SITES = [
    ("TerrainVetoTestSite", 0x1402711E8, 8, "test byte ptr [rdi+rcx*8+0xD8DCB6], 3 (record +6, bits 0x03)"),
    ("TerrainInspectReadSite", 0x14027B264, 14, "mov cl,[rdx+rcx*8+0xD8DCB3]; mov dl,[rdx+rax*8+0xD8DCB2]"),
    ("PaletteClearSite", 0x1401D462D, 13, "lea rcx,[rip+disp] -> 0x140D35750; mov r8d, 0x400"),
    ("PaletteBankBSite", 0x14026EADF, 7, "lea rax,[rip+disp] -> 0x140D35950"),
    ("PoachStoreSiteA", 0x1401AFFEA, 7, "lea rsi,[rip+disp] -> 0x1411A7A1B"),
    ("PoachStoreSiteB", 0x1401AAD73, 7, "lea r15,[rip+disp] -> 0x1411A7A1B"),
    ("InflictTableSite", 0x1400FA8ED, 7, "lea rdx,[rip+disp] -> 0x14080FBA0"),
    ("ItemStatsSite", 0x1402B8CBC, 7, "lea rcx,[r8+0x80F690] (image relative disp32)"),
]


def sections(data):
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    n_sec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    opt = e_lfanew + 24
    tds = struct.unpack_from("<I", data, e_lfanew + 8)[0]
    soi = struct.unpack_from("<I", data, opt + 56)[0]
    tbl = opt + opt_size
    out = []
    for i in range(n_sec):
        off = tbl + i * 40
        name = data[off:off + 8].rstrip(b"\x00").decode("ascii", "replace")
        vsize, va, raw_size, raw_ptr, _, _, _, _, flags = struct.unpack_from("<IIIIIIHHI", data, off + 8)
        out.append((name, va, vsize, raw_ptr, raw_size, flags))
    return out, tds, soi


def va_to_file(secs, va):
    rva = va - IMAGE_BASE
    for name, sva, vsize, raw_ptr, raw_size, _ in secs:
        if sva <= rva < sva + max(vsize, raw_size):
            delta = rva - sva
            if delta >= raw_size:
                return None, name
            return raw_ptr + delta, name
    return None, None


def read_span(data, secs, va, n):
    """Bytes as the game maps them at module map time: file bytes, or zeros past raw data."""
    off, sec = va_to_file(secs, va)
    if sec is None:
        return None, None
    if off is None:
        return bytes(n), sec
    return data[off:off + n], sec


def hexs(b):
    return b.hex(" ") if b else "-"


def print_pins(data, secs):
    print("\n== pinned spans (file backed means the exe holds these bytes; zero means bss until the game writes)")
    for name, va, n, src in PINS:
        span, sec = read_span(data, secs, va, n)
        if sec is None:
            print(f"{name:40s} 0x{va:X}  NOT IN ANY SECTION  [{src}]")
            continue
        off, _ = va_to_file(secs, va)
        backing = f"file backed at 0x{off:X}" if off is not None else "past raw data, ZERO until init"
        allzero = all(b == 0 for b in span)
        print(f"{name:40s} 0x{va:X}  {sec:8s} {backing}{'  (all zero)' if allzero else ''}  [{src}]")
        print(f"{'':40s} {hexs(span)}")


def print_provoke(data, secs):
    print("\n== Provoke's exact write targets")
    row = 0x14078B2DC + PROVOKE_ABILITY * ACTION_STRIDE
    span, _ = read_span(data, secs, row, ACTION_STRIDE)
    print(f"action row {PROVOKE_ABILITY} @0x{row:X}: {hexs(span)}")
    if span:
        print(f"  byte +{ACTION_INFLICT_OFF} (InflictStatus index) = 0x{span[ACTION_INFLICT_OFF]:02X} ({span[ACTION_INFLICT_OFF]})")
    decoy = 0x14078961C + PROVOKE_ABILITY * ACTION_STRIDE
    dspan, _ = read_span(data, secs, decoy, ACTION_STRIDE)
    print(f"decoy row {PROVOKE_ABILITY} @0x{decoy:X}: {hexs(dspan)}  identical to live: {dspan == span}")
    inf = 0x14080FBA0 + PROVOKE_INFLICT_ROW * INFLICT_STRIDE
    ispan, _ = read_span(data, secs, inf, INFLICT_STRIDE)
    print(f"inflict row {PROVOKE_INFLICT_ROW} @0x{inf:X}: {hexs(ispan)}")
    # Neighbouring rows Provoke never writes, candidates for the door's signature span.
    for r in (PROVOKE_INFLICT_ROW - 1, PROVOKE_INFLICT_ROW + 1):
        s, _ = read_span(data, secs, 0x14080FBA0 + r * INFLICT_STRIDE, INFLICT_STRIDE)
        print(f"inflict row {r}: {hexs(s)}")
    # How many of the 128 inflict rows are non zero (a zero row cannot anchor a signature).
    whole, _ = read_span(data, secs, 0x14080FBA0, INFLICT_ROWS * INFLICT_STRIDE)
    nz = sum(1 for r in range(INFLICT_ROWS) if any(whole[r * 6:(r + 1) * 6]))
    print(f"inflict table: {nz} of {INFLICT_ROWS} rows non zero; rows 0..7: {hexs(whole[:48])}")
    live, _ = read_span(data, secs, 0x14078B2DC, ACTION_ROWS * ACTION_STRIDE)
    dec, _ = read_span(data, secs, 0x14078961C, ACTION_ROWS * ACTION_STRIDE)
    print(f"action table: live and decoy byte identical over all {ACTION_ROWS} rows: {live == dec}")
    print(f"  live rows 188..190:  {hexs(live[188 * 20:191 * 20])}")
    print(f"  decoy rows 188..190: {hexs(dec[188 * 20:191 * 20])}")
    distinct_live = {live[r * 20:(r + 1) * 20] for r in range(ACTION_ROWS)}
    print(f"  live file image holds {len(distinct_live)} distinct row pattern(s): "
          + "; ".join(hexs(x) for x in list(distinct_live)[:3]))


def print_itemstats(data, secs):
    print("\n== ItemStats rows against data/items.json (is the file image vanilla, modded, or neither)")
    items_path = HERE.parents[2] / "data" / "items.json"
    try:
        doc = json.loads(items_path.read_text(encoding="utf-8"))
    except OSError:
        print(f"items.json not found at {items_path}; skipping")
        return
    items = doc["items"] if isinstance(doc, dict) else doc
    table, _ = read_span(data, secs, 0x14080F690, ITEM_ROWS * ITEM_STRIDE)
    base_hits = prop_hits = both = neither = compared = 0
    examples = []
    for it in items:
        iid = it.get("id")
        if not isinstance(iid, int) or iid >= ITEM_ROWS:
            continue
        b = it.get("baseline", {}).get("wp")
        p = it.get("proposed", {}).get("wp")
        if b is None or p is None:
            continue
        compared += 1
        wp = table[iid * ITEM_STRIDE + ITEM_WP_OFF]
        if wp == b and wp == p:
            both += 1
        elif wp == b:
            base_hits += 1
        elif wp == p:
            prop_hits += 1
        else:
            neither += 1
        if len(examples) < 6:
            examples.append((iid, it.get("vanillaName"), b, p, wp, hexs(table[iid * 8:(iid + 1) * 8])))
    print(f"compared {compared} weapon ids < {ITEM_ROWS}: wp byte == baseline only {base_hits}, == proposed only {prop_hits}, "
          f"== both {both}, == neither {neither}")
    for e in examples:
        print(f"  id {e[0]:3d} {str(e[1]):22s} baseline {e[2]:2d} proposed {e[3]:2d} file {e[4]:3d}  row {e[5]}")
    print("  rows where the file matches neither baseline nor proposed (items.json baseline suspects):")
    for it in items:
        iid = it.get("id")
        if not isinstance(iid, int) or iid >= ITEM_ROWS:
            continue
        b = it.get("baseline", {}).get("wp")
        p = it.get("proposed", {}).get("wp")
        if b is None or p is None:
            continue
        wp = table[iid * ITEM_STRIDE + ITEM_WP_OFF]
        if wp != b and wp != p:
            print(f"    id {iid:3d} {str(it.get('vanillaName')):22s} baseline {b:2d} proposed {p:2d} file {wp:3d}  row {hexs(table[iid * 8:(iid + 1) * 8])}")
    # Candidate pins for the gun growth door: vanilla and proposed differ, so the pre init table is
    # told apart from the loaded one. Largest gaps first, eight of them.
    cands = []
    for it in items:
        iid = it.get("id")
        if not isinstance(iid, int) or iid >= ITEM_ROWS:
            continue
        b = it.get("baseline", {}).get("wp")
        p = it.get("proposed", {}).get("wp")
        if b is None or p is None:
            continue
        wp = table[iid * ITEM_STRIDE + ITEM_WP_OFF]
        if wp == b and p != b:
            cands.append((abs(p - b), iid, it.get("vanillaName"), b, p, table[iid * 8:(iid + 1) * 8]))
    cands.sort(reverse=True)
    print("  candidate pinned ids (file == baseline, proposed differs), top 8 by gap:")
    for gap, iid, nm, b, p, row in cands[:8]:
        print(f"    id {iid:3d} {str(nm):22s} vanilla wp {b:2d} proposed {p:2d}  vanilla row {hexs(row)}")


def print_sites(data, secs):
    print()
    print("== proposed code site landmarks (C# ready; every byte below is read from the exe file)")
    for name, va, n, decode in SITES:
        span, sec = read_span(data, secs, va, n)
        off, _ = va_to_file(secs, va)
        csharp = ", ".join(f"0x{b:02X}" for b in span)
        print(f"{name:24s} VA 0x{va:X} file 0x{off:X} {sec} {n} bytes  // {decode}")
        print(f"    {{ {csharp} }}")
    live, _ = read_span(data, secs, 0x14078B2DC + 188 * 20, 60)
    dec, _ = read_span(data, secs, 0x14078961C + 188 * 20, 60)
    print(f"ActionRows188to190PreInit (live file image, 60 bytes): {{ {', '.join(f'0x{b:02X}' for b in live)} }}")
    print(f"ActionRows188to190Vanilla (decoy file image, 60 bytes): {{ {', '.join(f'0x{b:02X}' for b in dec)} }}")
    inf, _ = read_span(data, secs, 0x14080FBA0, 48)
    print(f"InflictRows0to7Vanilla (48 bytes): {{ {', '.join(f'0x{b:02X}' for b in inf)} }}")


def reloc_check(data, secs):
    """The base relocation table must hold no entry inside .text or within 8 bytes of a SITES span,
    otherwise the file bytes of a span could differ from the live bytes under ASLR."""
    print("\n== base relocations against the code site spans")
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    opt = e_lfanew + 24
    reloc_rva, reloc_size = struct.unpack_from("<II", data, opt + 112 + 5 * 8)
    off, _ = va_to_file(secs, IMAGE_BASE + reloc_rva)
    text = next(s for s in secs if s[0] == ".text")
    t_lo, t_hi = text[1], text[1] + max(text[2], text[4])
    spans = [(va - IMAGE_BASE - 8, va - IMAGE_BASE + n + 8) for _, va, n, _ in SITES]
    total = in_text = near = 0
    pos = 0
    while pos < reloc_size:
        page, block = struct.unpack_from("<II", data, off + pos)
        if block < 8:
            break
        for i in range((block - 8) // 2):
            entry = struct.unpack_from("<H", data, off + pos + 8 + 2 * i)[0]
            if entry >> 12 == 0:
                continue
            rva = page + (entry & 0xFFF)
            total += 1
            in_text += t_lo <= rva < t_hi
            near += any(lo <= rva < hi for lo, hi in spans)
        pos += block
    print(f".reloc: {total} entries, {in_text} inside .text, {near} within 8 bytes of a SITES span")


def rip_scan(data, secs, targets):
    """Every exec section offset o where int32 at o == target - (va(o) + 4), i.e. a RIP relative
    operand whose displacement lands on the target. Four alignments through array('i') keep it fast."""
    print("\n== RIP relative references in executable sections (instruction bytes around each hit)")
    exec_secs = [s for s in secs if s[5] & 0x20000000]
    print("exec sections: " + ", ".join(f"{s[0]} va 0x{s[1]:X} raw 0x{s[4]:X}" for s in exec_secs))
    for label, target in targets:
        hits = []
        for name, sva, vsize, raw_ptr, raw_size, _ in exec_secs:
            sec = data[raw_ptr:raw_ptr + raw_size]
            sec_va = IMAGE_BASE + sva
            for a in range(4):
                n = (len(sec) - a) // 4
                arr = array("i")
                arr.frombytes(sec[a:a + 4 * n])
                base = target - sec_va - a - 4
                hits.extend((sec_va + a + 4 * j, raw_ptr + a + 4 * j, name)
                            for j, v in enumerate(arr) if v == base - 4 * j)
                if len(hits) > 40:
                    break
        print(f"{label:36s} 0x{target:X}: {len(hits)} hit(s)")
        for va, off, name in hits[:8]:
            ctx = data[off - 8:off + 8]
            print(f"    disp at 0x{va:X} ({name})  bytes [-8..+8): {hexs(ctx)}")
        if len(hits) > 8:
            print(f"    ... {len(hits) - 8} more")


def imm_scan(data, secs, needles):
    print("\n== image relative disp32 needles (the terrain reader's [base + idx*8 + rva] shape)")
    exec_secs = [s for s in secs if s[5] & 0x20000000]
    for label, rva in needles:
        needle = struct.pack("<I", rva)
        found = []
        for name, sva, vsize, raw_ptr, raw_size, _ in exec_secs:
            sec = data[raw_ptr:raw_ptr + raw_size]
            i = sec.find(needle)
            while i >= 0 and len(found) < 12:
                found.append((IMAGE_BASE + sva + i, raw_ptr + i, name))
                i = sec.find(needle, i + 1)
        print(f"{label:24s} 0x{rva:X}: {len(found)} hit(s)")
        for va, off, name in found:
            print(f"    disp at 0x{va:X} ({name})  bytes [-8..+8): {hexs(data[off - 8:off + 8])}")


def main():
    exe = Path(sys.argv[1]) if len(sys.argv) > 1 else STEAM_FFT / "fft_enhanced.exe"
    data = exe.read_bytes()
    secs, tds, soi = sections(data)
    print(f"exe {exe}  size {len(data)}")
    print(f"TimeDateStamp 0x{tds:08X} SizeOfImage 0x{soi:08X}")
    print("sections: " + ", ".join(f"{s[0]}(va 0x{s[1]:X} vsize 0x{s[2]:X} raw 0x{s[4]:X}{' X' if s[5] & 0x20000000 else ''})" for s in secs))
    print_pins(data, secs)
    print_provoke(data, secs)
    print_itemstats(data, secs)
    imm_scan(data, secs, IMM_TARGETS)
    rip_scan(data, secs, RIP_TARGETS)
    print_sites(data, secs)
    reloc_check(data, secs)


if __name__ == "__main__":
    main()
