"""Repair UTF-8-read-as-cp1252 mojibake in the docs: for each line, if the reverse
transform (encode cp1252 -> decode utf-8) succeeds and changes the text, apply it."""
import io

PATHS = [
    r"C:\Users\ptyRa\Dev\FFTItemOverhaul\docs\HANDOFF.md",
    r"C:\Users\ptyRa\Dev\FFTItemOverhaul\docs\DEV_TEST_RECIPES.md",
]

for p in PATHS:
    s = io.open(p, encoding="utf-8", newline="").read()
    out_lines = []
    fixed = 0
    for line in s.split("\n"):
        try:
            rev = line.encode("cp1252").decode("utf-8")
            if rev != line:
                line = rev
                fixed += 1
        except (UnicodeEncodeError, UnicodeDecodeError):
            pass  # line is already clean (or not reversible) -- leave it
        out_lines.append(line)
    io.open(p, "w", encoding="utf-8", newline="").write("\n".join(out_lines))
    print(p.rsplit("\\", 1)[-1], "lines repaired:", fixed)
