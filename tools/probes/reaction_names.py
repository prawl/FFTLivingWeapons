import sqlite3, os
os.chdir(r"C:\Users\ptyRa\Dev\FFTLivingWeapons")
con = sqlite3.connect("working/nxd_ability/ability.sqlite")
for k in range(166, 236):
    r = con.execute('select Key, Name from "Ability-en" where Key=?', (k,)).fetchone()
    if r and r[1] and r[1] != "None":
        print(r[0], r[1])
