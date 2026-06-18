import sqlite3, json, os

os.chdir(r"C:\Users\ptyRa\Dev\FFTLivingWeapons")

con = sqlite3.connect("working/nxd_ability/ability.sqlite")
print("== vanilla Ability-en around 358 ==")
for key in range(355, 362):
    row = con.execute('select Key, Name, Description from "Ability-en" where Key=?', (key,)).fetchone()
    if row:
        print(" ", row[0], "|", row[1], "|", (row[2] or "")[:110])
con.close()

mod_nxd = r"C:\Users\ptyRa\Downloads\Barrage for Machinist-51-0-1-1764831553 (1)\FFTIVC\data\enhanced\nxd\ability.en.nxd"
print("\nmod nxd size:", os.path.getsize(mod_nxd))
data = open(mod_nxd, "rb").read()
print("contains 'Barrage':", b"Barrage" in data)
i = data.find(b"Barrage")
while i != -1:
    print("  @", i, "->", data[max(0,i-20):i+120])
    i = data.find(b"Barrage", i + 1)

print("\n== Cleaver (id 20) knockback proc ==")
d = json.load(open("data/items.json"))
items = d["items"] if isinstance(d, dict) and "items" in d else d
for it in items:
    if it.get("id") == 20:
        print(json.dumps(it.get("proposed"), indent=1))

print("\n== what ability is the knockback proc? ==")
con = sqlite3.connect("working/nxd_ability/ability.sqlite")
for key in (148, 149, 150, 88, 89, 90, 91):
    row = con.execute('select Key, Name from "Ability-en" where Key=?', (key,)).fetchone()
    if row:
        print(" ", row[0], "|", row[1])
con.close()
