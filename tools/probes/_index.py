"""Generate tools/probes/README.md, the probe-pile index (LW backlog: findability).

The README is a GENERATED artifact: edit the GROUPS map here and re-run
`python tools/probes/_index.py`; never hand-edit README.md (repo rule for generated
files). Descriptions are pulled live from each probe's own module docstring first line,
so a probe whose header changes re-indexes itself on the next run. Probes missing from
GROUPS land in the Ungrouped section rather than vanishing (no silent caps), and names
in GROUPS with no file on disk are reported as stale.

Not a probe itself: no game access, no memory reads; pure file generation.
"""
import ast
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent

GROUPS = {
    "Shared harness and RE utilities (import these, do not reinvent)": [
        "battle_cheats.py", "battle_toolbag.py", "status_map.py", "code_patch.py",
        "disasm.py", "code_dump.py", "hexdump.py", "struct_watch.py", "ptr_scan.py",
        "vtable_scan.py", "caller_scan.py", "retaddr_check.py", "thread_sample.py",
        "react_hunt.py", "page_protect_probe.py", "rarebits.py", "realunits.py",
    ],
    "Unit identity, kill credit, and attribution": [
        "nameid_unique_probe.py", "unitid_probe.py", "actor_attrib_probe.py",
        "condensed_fp_probe.py", "cursor_resolve_probe.py", "collapse_stats.py",
        "oracle_probe.py", "killcredit_probe.py", "turnteam_probe.py", "team_probe.py",
        "stamp_swap.py", "frame_diff.py", "combat_scan.py", "sentinel_probe.py",
        "arec_watch.py",
    ],
    "Turns, CT, and the scheduler": [
        "ct_probe.py", "ct_watch.py", "ct_offset_probe.py", "turn_queue_probe.py",
        "provoke_lookahead_probe.py", "acted_probe.py", "acted_watch.py",
        "acted_moved_watch.py", "wait_state_probe.py", "mushin_wait_probe.py",
        "charging_probe.py",
    ],
    "Equipment, roster, and inventory (the LW-193 gear-loss family lives here)": [
        "offhand_shield_probe.py", "twinless_probe.py", "menu_signal_probe.py",
        "dualgun_probe.py", "roster_dump.py", "roster_head.py", "roster_read.py",
        "roster_ring_probe.py", "roster_span_probe.py", "roster_sprite.py",
        "roster_loss_trace.py", "roster_reaction_shove.py", "scholar_ring_inv_probe.py",
        "item_type_probe.py", "purchase_diff.py", "refund_jp.py", "learned_probe.py",
        "set_learned.py", "set_learned2.py", "clear_stray.py", "restore_counter.py",
        "hold_secondary.py", "support_poke.py", "mp_probe.py", "hp_poke.py",
        "staff_ma_probe.py", "ramza_check.py",
    ],
    "Statuses, stats, and reactions": [
        "status_probe.py", "poison_probe.py", "charm_probe.py", "brave_probe.py",
        "brave_oneshot_probe.py", "berserk_probe.py", "evasive_stance_probe.py",
        "cripple_probe.py", "combat_reaction.py", "reaction_force_probe.py",
        "reaction_grant_probe.py", "reaction_id_sites.py", "reaction_names.py",
        "noncharge_probe.py", "intercept_probe.py", "haste_ct_probe.py",
        "give_enemy_buffs.py", "zodiac_probe.py", "mount_probe.py", "apply_engine_find.py",
        "provoke_mark_probe.py",
    ],
    "Ability grants and the JobCommand table": [
        "barrage_probe.py", "barrage_check.py", "ability_grant_probe.py",
        "ability_table_probe.py", "jobcommand_find_probe.py", "job_kit_dump.py",
        "jobpage_scan.py", "jobpage_refine.py", "cmdlist_scan.py", "cmdlist_scan2.py",
        "cmdlist_after.py", "cmdarray_scan.py", "kit_stamp_probe.py",
        "aim_all_barrage.py", "steal_only_barrage.py", "fix_ext.py", "tier_scan.py",
        "decode_abilities.py",
    ],
    "Position, render nodes, spawn and despawn": [
        "swap_units.py", "node_dump_diff.py", "node_move_watch.py", "node_world_fit.py",
        "move_render_probe.py", "spawn_probe.py", "clone_probe.py", "despawn_probe.py",
        "formation_diff.py", "ai_target_scan.py", "deployed_signal_probe.py",
        "puppet_probe.py", "knockback_probe.py", "anim_poke_probe.py",
    ],
    "Tiles, terrain, treasure, and battlefield state": [
        "terrain_probe.py", "tile_cal.py", "tilecast_probe.py", "mark_probe.py",
        "treasure_addr.py", "treasure_reanchor_probe.py", "highlight_probe.py",
        "victory_probe.py", "weather_probe.py", "facing_probe.py", "taunt_probe.py",
    ],
    "UI, text, and card display": [
        "display_probe.py", "item_text_census.py", "attack_row_redirect.py",
        "attack_table_scan.py", "plant_alloc_probe.py", "menu_cursor.py", "menu_diff.py",
        "menu_region.py", "menu_strings.py", "ui_menu_probe.py", "ui_contention_probe.py",
        "callout_show_probe.py", "callout_life_probe.py", "callout_timer_probe.py",
        "numeral_probe.py", "heal_number_probe.py", "feign_probe.py",
    ],
    "Icons, art, and sprites": [
        "ramp_engine_prototype.py", "palette_experiment.py", "vanilla_palette_sample.py",
        "live_icon_patch_probe.py", "warbrand_art_probe.py", "warbrand_damage_probe.py",
        "weapon_sprite_probe.py", "weapon_sprite_writetest.py", "combat_sprite.py",
        "combat_struct_diff.py",
    ],
    "Game-patch re-anchoring": [
        "exe_reanchor_scan.py", "rip_xref_reanchor.py", "anchorscan_feasibility_probe.py",
        "display_probe.py",
    ],
    "Data and doc one-shots (no game process needed)": [
        "add_dmg_col.py", "merge_grid.py", "merge_rods.py", "verify_descs.py",
        "lw320_obtain_power.py",
        "verify_grant_slot.py", "fix_mojibake.py", "poach_diff.py", "read_state.py",
        "larceny_locate_probe.py", "find_counter.py", "rawrec.py", "roster_read.py",
        "crystal_counter_probe.py",
    ],
}

HEADER = """\
# tools/probes: the probe pile index

GENERATED by `python tools/probes/_index.py` from each probe's own docstring; edit the
GROUPS map in `_index.py` and re-run, never this file. A probe appears once per group it
belongs to (a few genuinely serve two).

Ground rules before running anything here:
1. READ THE HEADER FIRST. Some probes WRITE game memory (each says so in its first
   lines); some are one-shots wired to a specific long-finished arc.
2. Addresses come from `tools/lib/offsets.py` parsing Offsets.cs in the newer probes;
   OLDER probes may carry stale pre-1.5 constants (the ct_probe family famously does).
   docs/LIVE_LEDGER.md is the tie-breaker on any claim a header makes.
3. Evidence tapes live in `tapes/` and are cited by ledger rows; never rename or move
   them.
4. New probes: guarded RPM/WPM only, verify-before-write, restore stated in the header,
   and land here (tracked), never in %TEMP%.
"""


def first_line(path):
    try:
        doc = ast.get_docstring(ast.parse(path.read_text(encoding="utf-8", errors="replace")))
        return next((l.strip() for l in (doc or "").splitlines() if l.strip()), "(no docstring)")
    except Exception:
        return "(unparseable)"


def main():
    files = {p.name: p for p in ROOT.glob("*.py") if p.name != "_index.py"}
    grouped = set()
    out = [HEADER]
    stale = []
    for title, names in GROUPS.items():
        out.append(f"\n## {title}\n")
        for n in sorted(set(names)):
            if n not in files:
                stale.append(n)
                continue
            grouped.add(n)
            out.append(f"- `{n}` {first_line(files[n])}")
    leftovers = sorted(set(files) - grouped)
    if leftovers:
        out.append("\n## Ungrouped (add these to _index.py's GROUPS)\n")
        for n in leftovers:
            out.append(f"- `{n}` {first_line(files[n])}")
    out.append("\n## tapes/\n")
    out.append("- Evidence tapes cited by docs/LIVE_LEDGER.md rows; append-only, never rename.")
    (ROOT / "README.md").write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")
    print(f"README.md written: {len(grouped)} grouped, {len(leftovers)} ungrouped"
          + (f", STALE entries (no such file): {stale}" if stale else ""))


if __name__ == "__main__":
    main()
