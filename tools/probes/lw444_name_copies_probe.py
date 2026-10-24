"""LW-444 Phase 0: which copy of a weapon's name does an item list read?

The card painter gives every weapon name a two character tier tag ("  ", "+ ", "+2", "+3",
Tuning.Suffix) by rewriting the room baked after the name in heap copies. Once the card counters are
all found the whole heap sweep stops and only the description pool copies get painted. If a list
row draws its name from some other copy, that copy keeps whatever tag it was born with, which is the
LW-444 symptom. This probe lists every copy and lets the owner flip one copy at a time to see which
one the list shows.

  python tools/probes/lw444_name_copies_probe.py scan "<weapon name>"
      READ-ONLY. Every copy of the name in ASCII and UTF-16LE across committed readable memory,
      with the two chars after it, whether "Kills: " (same encoding) sits within 512 bytes after
      it (a description pool copy the painter owns), the region type, and a tally by tag.

  python tools/probes/lw444_name_copies_probe.py id "<weapon name>"
      Prints the weapon's id from LivingWeapon/meta.json (lw381_tally_poke.py takes the id).

  python tools/probes/lw444_name_copies_probe.py poke "<weapon name>" <hexaddr> <enc> "<2 chars>"
      WRITES two chars. Re-reads the name at hexaddr in that encoding (1 = ASCII, 2 = UTF-16LE),
      refuses unless the name is there and the current tag is one of the four valid tags, writes the
      new tag, reads it back, and prints the restore command. One copy per run.

Reading a scan: run it with the weapon pick list open on a row whose tag reads wrong. A copy whose
tag is stale while the pool copies read right is the candidate. Poke it to the right tag, back out of
the list and reopen it; if the row now reads right, that copy is what the list draws.
"""
import json
import sys
from collections import Counter
from pathlib import Path

from item_text_census import (find_pid, rpm, wpm, regions, region_info, chunks, enc_bytes,
                              gloss, substr_hits)

VALID_TAGS = ("  ", "+ ", "+2", "+3")
POOL_WINDOW = 512
READ_ACCESS = 0x0410      # PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
WRITE_ACCESS = 0x0438     # adds PROCESS_VM_OPERATION | PROCESS_VM_WRITE


def tag_text(raw, enc):
    if raw is None or len(raw) < 2 * enc:
        return None
    if enc == 1:
        return raw[:2].decode("latin-1")
    return raw[:4].decode("utf-16-le", "replace")


def open_game(access):
    pid, h = find_pid(access)
    if not h:
        print("fft_enhanced.exe is not running (or could not be opened).")
        sys.exit(2)
    return h


def cmd_scan(name):
    h = open_game(READ_ACCESS)
    region_list = list(regions(h))
    hits = []
    for enc in (1, 2):
        needle = enc_bytes(name, enc)
        seen = set()
        for rbase, rsize in region_list:
            for cbase, buf in chunks(h, rbase, rsize):
                for addr in substr_hits(buf, cbase, needle):
                    if addr not in seen:
                        seen.add(addr)
                        hits.append((addr, enc))
    hits.sort()
    tally = Counter()
    print(f"{len(hits)} copies of {name!r}")
    print("address        enc  tag    pool  region                   before")
    for addr, enc in hits:
        nlen = len(enc_bytes(name, enc))
        tag = tag_text(rpm(h, addr + nlen, 2 * enc), enc)
        after = rpm(h, addr + nlen, POOL_WINDOW) or b""
        pool = enc_bytes("Kills: ", enc) in after
        before = rpm(h, addr - 8, 8)
        info = region_info(h, addr)
        region = f"{info[2]} {info[3]}" if info else "unmapped"
        shown = repr(tag) if tag is not None else "?"
        tally[(enc, tag, pool)] += 1
        print(f"{addr:012X}   {enc}   {shown:6} {'yes' if pool else 'no':5} {region:24} "
              f"|{gloss(before) if before else '?'}|")
    print("\ntally (enc, tag, pool): count")
    for key, n in sorted(tally.items(), key=lambda kv: str(kv[0])):
        print(f"  {key}: {n}")
    tags_ok = sum(n for (e, t, p), n in tally.items() if t in VALID_TAGS)
    print(f"\n{tags_ok} of {len(hits)} copies carry a valid tag room; only those are list or card candidates.")


def cmd_poke(name, hexaddr, enc, new_tag):
    if enc not in (1, 2):
        print("enc must be 1 (ASCII) or 2 (UTF-16LE).")
        return 2
    if new_tag not in VALID_TAGS:
        print(f"new tag must be one of {VALID_TAGS}.")
        return 2
    addr = int(hexaddr, 16)
    h = open_game(WRITE_ACCESS)
    needle = enc_bytes(name, enc)
    if rpm(h, addr, len(needle)) != needle:
        print("refused: the name is not at that address any more (heap moved or wrong encoding).")
        return 1
    slot = addr + len(needle)
    old = tag_text(rpm(h, slot, 2 * enc), enc)
    if old not in VALID_TAGS:
        print(f"refused: the current tag {old!r} is not one of the valid tags, so this is not a tag room.")
        return 1
    if not wpm(h, slot, enc_bytes(new_tag, enc)):
        print("write failed (page not writable?). Nothing changed.")
        return 1
    back = tag_text(rpm(h, slot, 2 * enc), enc)
    print(f"{addr:012X} enc{enc}: tag {old!r} -> {back!r}")
    print(f'restore: python tools\\probes\\lw444_name_copies_probe.py poke "{name}" {addr:012X} {enc} "{old}"')
    return 0 if back == new_tag else 1


def cmd_id(name):
    meta = json.loads((Path(__file__).resolve().parents[2] / "LivingWeapon" / "meta.json").read_text(encoding="utf-8"))
    ids = [k for k, v in meta.items() if (v.get("name") or "").lower() == name.lower()]
    print(f"{name}: id {ids[0]}" if ids else f"{name!r} is not a tracked weapon name in meta.json.")
    return 0 if ids else 1


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "id":
        return cmd_id(sys.argv[2])
    if len(sys.argv) == 3 and sys.argv[1] == "scan":
        cmd_scan(sys.argv[2])
        return 0
    if len(sys.argv) == 6 and sys.argv[1] == "poke":
        return cmd_poke(sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5])
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
