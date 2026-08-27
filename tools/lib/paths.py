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
# LW-346: the extended-inventory tables (ids 261+). Same row schema as the modloader's XML, but
# read by LivingWeapon.dll at boot, NOT by the modloader, so they live OUTSIDE the FFTIVC tree
# the loader scans (a 261-row XML table is silently dropped there, journal 2026-08-26).
MOD_EXTENDED = ROOT / "mod" / "extended_inventory"
MOD_NXD_DIR = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "nxd"
MOD_ITEM_NXD = MOD_NXD_DIR / "item.en.nxd"
MOD_ABILITY_NXD = MOD_NXD_DIR / "ability.en.nxd"
# LW-123: the status name/description table. Needed because Provoke marks its target with a status
# that ships with a BLANK name and description (UIStatusEffect Key 1), so the mark has no way to
# announce itself until we write those two cells.
MOD_STATUS_NXD = MOD_NXD_DIR / "uistatuseffect.en.nxd"

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
# RELOADEDIIMODS overrides the mods folder the same way BuildLinked.ps1 and scan_logs.py's
# resolve_mod_dir already honor -- this used to be a third hardcoded copy of the Steam default
# that ignored the env var entirely (LW-148). One rule, read here, imported everywhere else.
_env_mods = os.environ.get("RELOADEDIIMODS")
RELOADED_MODS = Path(_env_mods) if _env_mods else STEAM_FFT / "Reloaded" / "Mods"
TABLE_DATA = RELOADED_MODS / "FFTIVC_Mod_Loader" / "TableData"
