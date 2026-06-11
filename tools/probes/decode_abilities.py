import sqlite3, glob, os

os.chdir(r"C:\Users\ptyRa\Dev\FFTItemOverhaul")
for db in glob.glob("working/**/*.sqlite", recursive=True):
    con = sqlite3.connect(db)
    try:
        tables = [r[0] for r in con.execute("select name from sqlite_master where type='table'")]
    except Exception as e:
        print(db, "ERR", e)
        continue
    for t in tables:
        if "bility" not in t.lower():
            continue
        cols = [c[1] for c in con.execute(f'PRAGMA table_info("{t}")')]
        print(db, t, "cols:", cols[:8])
        keycol = "Key" if "Key" in cols else cols[0]
        for key in (10, 13, 20, 24, 127, 131, 201, 213, 214, 246):
            try:
                row = con.execute(f'select * from "{t}" where {keycol}=?', (key,)).fetchone()
                if row:
                    print(" ", key, "->", str(row)[:140])
            except Exception as e:
                print("  query err", e)
                break
    con.close()
