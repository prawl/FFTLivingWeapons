"""Deliberate off-derivation content the item.en.nxd bake carries (LW-78).

Shared by tools/audit_nxd_bakes.py (which passes these cells as ALLOWED) and
tools/rebase_pilot_item.py (which re-applies them onto a fresh vanilla decode), so the
audit and the rebase can never disagree about what is intentional.

ALLOWED_ITEM_CELLS: (Key, column) -> (value, reason). Hand edits patch_names.py does not
derive from items.json; anything else in the bake that differs from current vanilla and
from the items.json derivation is a defect the audit flags.
"""

ALLOWED_ITEM_CELLS = {
    # Sanguine Gauche drains HP on every hit (items.json id 6, formula 48 Night Sword);
    # 1001 is the game's own "Absorbs HP" Special Effect badge.
    (6, "UiStatusEffectId"): (1001, "Sanguine Gauche lifeleech badge"),
    # Warbrand (id 67) is our deterministic Sword rework on the Iron Flail slot; vanilla
    # 1.5.1 flags row 67 as a random-damage flail. Our card must not claim random damage.
    (67, "IsRandomDamage"): (0, "Warbrand is a deterministic sword by design"),
    # Unknown display flags 1.5.x set on rows whose vanilla identity (Nagnarok, Oak Staff,
    # Iron Flail) this mod replaces outright; the bake's 0s are the release-long known-good
    # card rendering for OUR items, so we keep them rather than adopt flags about items
    # that no longer exist here. Revisit if a card ever renders wrong.
    (31, "Unknown1B"): (0, "Lightbringer replaces Nagnarok; keep known-good card"),
    (59, "Unknown18"): (0, "Birchwood Staff replaces Oak Staff; keep known-good card"),
    (67, "Unknown18"): (0, "Warbrand replaces Iron Flail; keep known-good card"),
}

# Key 261 is the 261-item cap-break research row (docs/research/ITEM_CAP_261_BREAK_JOURNEY.md):
# display text for the id-261 item the sibling FFTHandsFree experiment can equip. Inert for
# normal players (the base game never shows a 262nd item); kept so the experiment keeps text.
ALLOWED_EXTRA_ROWS = {"Item-en": {261}}
