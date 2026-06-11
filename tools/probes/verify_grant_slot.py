"""Assert every signature weapon's baked Grant line carries its 16-space slot."""
import sqlite3

con = sqlite3.connect(r"C:\Users\ptyRa\Dev\FFTItemOverhaul\working\pilot_item.sqlite")
ok = True
for key in [3, 4, 6, 8, 9, 10]:
    (desc,) = con.execute('SELECT Description FROM "Item-en" WHERE Key=?', (key,)).fetchone()
    lines = desc.split("\n")
    grant = [l for l in lines if l.startswith("Grant")]
    kills_last = lines[-1].startswith("Kills: ")
    slot_ok = bool(grant) and grant[0] == "Grant " + " " * 16
    print(f"id {key}: grant_slot={'OK' if slot_ok else 'BROKEN ' + repr(grant)} kills_last={kills_last}")
    ok &= slot_ok and kills_last
print("ALL OK" if ok else "FAIL")
