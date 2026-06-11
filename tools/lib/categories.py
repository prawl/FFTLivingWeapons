"""Weapon category sets. Two scopes, derived so they cannot drift:

WEAPON_CATS        the 18 wieldable weapon categories. The Living Weapon system (growth,
                   kill scaffolding, meta.json) and the description bake key off this set.
WEAPON_TABLE_CATS  generate.py's ItemWeaponData emit scope: the 18 above plus Throwing and
                   Bomb -- the ninja's Throw-command projectiles (damage = Speed x WP).
                   They get weapon TABLE rows but no living-weapon growth, so they exist
                   only in the table-emit superset.
"""
WEAPON_CATS = frozenset({
    "Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff",
    "Flail", "Gun", "Crossbow", "Bow", "Instrument", "Book", "Polearm", "Pole", "Bag", "Cloth"})

WEAPON_TABLE_CATS = WEAPON_CATS | {"Throwing", "Bomb"}
