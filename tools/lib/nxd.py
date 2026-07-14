"""FF16Tools nxd encode + deploy helpers (formerly 5 copied subprocess bodies).

NXD overrides are full-table replace and the base game pacs are encrypted, so every
FF16Tools call carries -g fft. A failed encode leaves no output file (or a partial set),
which the modloader would treat as "no override" -- so encode_sqlite_to_nxd refuses to
return without the expected file.
"""
import shutil
import subprocess

from .paths import FF16


def encode_sqlite_to_nxd(sqlite, out_dir, nxd_name):
    """Encode a working sqlite to nxd via FF16Tools; return the built nxd Path.

    SystemExit (with the encoder's output) if the expected file does not appear."""
    out_dir.mkdir(parents=True, exist_ok=True)
    r = subprocess.run([str(FF16), "sqlite-to-nxd", "-i", str(sqlite), "-o", str(out_dir), "-g", "fft"],
                       capture_output=True, text=True)
    out = out_dir / nxd_name
    if r.returncode != 0 or not out.exists():
        produced = [f.name for f in out_dir.glob("*.nxd")]
        raise SystemExit(f"ENCODE FAILED (expected {nxd_name}, encoder produced {produced}):\n"
                         + r.stdout + r.stderr)
    return out


def decode_nxd_to_sqlite(nxd_paths, work_dir, out_name):
    """Decode one or more nxd files to a single sqlite via FF16Tools; return the sqlite Path.

    The files are copied into a fresh input dir first because the decoder takes a directory
    and derives each table name from the file name (item.en.nxd -> Item-en).
    SystemExit (with the decoder's output) if the expected sqlite does not appear."""
    in_dir = work_dir / (out_name + ".in")
    in_dir.mkdir(parents=True, exist_ok=True)
    for p in nxd_paths:
        shutil.copy(p, in_dir / p.name)
    out = work_dir / out_name
    r = subprocess.run([str(FF16), "nxd-to-sqlite", "-i", str(in_dir), "-o", str(out), "-g", "fft"],
                       capture_output=True, text=True)
    if r.returncode != 0 or not out.exists():
        raise SystemExit(f"DECODE FAILED ({out_name}):\n" + r.stdout + r.stderr)
    return out


def deploy_nxd(built, dest):
    """Copy a built nxd into place (mod tree or the live Reloaded folder), creating dirs."""
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy(built, dest)
    return dest
