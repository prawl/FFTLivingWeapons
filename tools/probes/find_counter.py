import sqlite3, os
os.chdir(r"C:\Users\ptyRa\Dev\FFTLivingWeapons")
con = sqlite3.connect("working/nxd_ability/ability.sqlite")
tables = [r[0] for r in con.execute("select name from sqlite_master where type='table'")]
print("tables:", tables)
for t in tables:
    if "-en" not in t:
        continue
    try:
        rows = con.execute(
            f'select Key, Name from "{t}" where Name in '
            "('Counter','Auto-Potion','Concentration','Attack Boost','Defense Boost',"
            "'Magick Boost','Counter Tackle','First Strike','Teleport','Move +1','Safeguard')"
        ).fetchall()
    except Exception as e:
        print(t, "err", e)
        continue
    for r in rows:
        print(t, r[0], r[1])
