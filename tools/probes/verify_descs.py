"""Print baked descriptions for spot-check: a signature knife, the Zwill, a non-signature weapon."""
import sqlite3

con = sqlite3.connect(r"C:\Users\ptyRa\Dev\FFTLivingWeapons\working\pilot_item.sqlite")
for key in [3, 10, 30]:
    name, desc = con.execute('SELECT Name, Description FROM "Item-en" WHERE Key=?', (key,)).fetchone()
    print(f"=== id {key}: {name!r} ===")
    print(desc)
    print("---")
