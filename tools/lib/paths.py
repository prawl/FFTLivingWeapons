"""Machine + repo paths shared by the tools layer (formerly 12 verbatim copies).

ROOT is the repo checkout (lib sits one level below tools/, hence parents[2]). The Steam and
FF16Tools paths are this box's installs; CI (Linux) never touches them, it only runs
generate/analyze/gen_living_weapon_meta, which stay inside ROOT.
"""
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ITEMS = ROOT / "data" / "items.json"   # the only hand-edited item source

# Deployable mod tree: the sparse modloader tables + the full-table nxd overrides.
MOD_TABLES = ROOT / "mod" / "FFTIVC" / "tables" / "enhanced"
MOD_NXD_DIR = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "nxd"
MOD_ITEM_NXD = MOD_NXD_DIR / "item.en.nxd"
MOD_ABILITY_NXD = MOD_NXD_DIR / "ability.en.nxd"

# FF16Tools CLI (sqlite-to-nxd / nxd-to-sqlite / tex-conv / img-conv; base game pacs are
# encrypted, every call needs -g fft). FF16TOOLS_CLI overrides it so a version bump is one
# env edit instead of a hunt through scripts.
FF16 = Path(os.environ.get(
    "FF16TOOLS_CLI",
    r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe"))

# Steam install: the live Reloaded mods folder + the modloader's vanilla table templates
# (the authoritative baseline decode_tables.py and the JobData builders read from).
STEAM_FFT = Path(r"C:\Program Files (x86)\Steam\steamapps\common"
                 r"\FINAL FANTASY TACTICS - The Ivalice Chronicles")
RELOADED_MODS = STEAM_FFT / "Reloaded" / "Mods"
TABLE_DATA = RELOADED_MODS / "FFTIVC_Mod_Loader" / "TableData"
