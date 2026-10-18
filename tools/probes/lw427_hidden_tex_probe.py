#!/usr/bin/env python
"""Read only. Find an icon's texture records whether live or hidden, and say which backs its parts.

A hide points a texture record's name at string+1 and zeroes its hash, so the path based record
finders stop seeing it. This probe looks for pointers to the path string AND to string+1 for one
icon on both surfaces, prints each record's hash and size, and lists the parts entries beside
them, so a second tier change in one session can be judged: was a fresh record created after the
first hide, or does the hidden record still back the reloaded parts entry?

USAGE (game running)
    python tools/probes/lw427_hidden_tex_probe.py 33
"""
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lw381_icon_memscan import find_pid, k32, read  # noqa: E402
from lw381_parts_cache_probe import MEM_PRIVATE, q, sweep_many  # noqa: E402
from lw381_tex_cache_rename import records_for  # noqa: E402

TEX_PATHS = {
    "card": "ui/ffto/icon/equip_item/texture/ei_{n:03d}_uitx.tex",
    "small": "ui/ffto/icon/equip_item_s/texture/ei_s_{n:03d}_uitx.tex",
}


def main():
    item_id = int(sys.argv[1])
    pid = find_pid()
    h = k32.OpenProcess(0x0410, False, pid)
    if not h:
        raise SystemExit("could not open the game")
    for surface, fmt in TEX_PATHS.items():
        path = fmt.format(n=item_id).encode()
        strings = sweep_many(h, [path], MEM_PRIVATE)
        addrs = sorted(strings.get(path, []))
        print(f"{surface}: {len(addrs)} copy(ies) of the path string")
        for s in addrs:
            for kind, target in (("live", s), ("hidden", s + 1)):
                needle = struct.pack("<Q", target)
                holders = sweep_many(h, [needle], MEM_PRIVATE).get(needle, [])
                for ha in sorted(holders):
                    if ha & 7:
                        continue
                    head = read(h, ha, 0x40) or b""
                    if len(head) < 0x38:
                        continue
                    size = struct.unpack_from("<Q", head, 0x28)[0]
                    hsh = struct.unpack_from("<I", head, 0x30)[0]
                    print(f"  {kind:6} record 0x{ha:X} size 0x{size:X} hash 0x{hsh:08X} name->0x{target:X}")
    print("live records as the renamer sees them:")
    for r in records_for(h, item_id):
        print("  ", r)


if __name__ == "__main__":
    main()
