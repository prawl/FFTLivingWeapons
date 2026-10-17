using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>Per-weapon facts for growth and display, baked from data/items.json into
/// meta.json (id -> WeaponMeta) so the runtime does not depend on the dev repo.</summary>
public sealed class WeaponMeta
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("wp")] public int Wp { get; set; }
    [JsonProperty("cat")] public string Cat { get; set; } = "";
    [JsonProperty("formula")] public int Formula { get; set; }
    // Growth lane ("pa", "pa+ma+brave", "wp+faith", ...) baked from docs/living_weapon_grid.csv
    // by tools/gen_living_weapon_meta.py (lane_of). "" = no growth.
    [JsonProperty("lane")] public string Lane { get; set; } = "";
    // The stable lead line of the description; the in-card Kills counter anchors to it.
    [JsonProperty("flavor")] public string Flavor { get; set; } = "";
    // The tagged "Grows: ..." card line (lib.flavor.grows_line, LW-329 color tag) as baked;
    // CardScanner's second anchor beside Flavor (the Grows line sits closer to the Kills scaffold).
    // Matched as baked. "" for non-living ids.
    [JsonProperty("growsLine")] public string GrowsLine { get; set; } = "";
    // Passive granted at a kill tier; null for pure stat-growth weapons.
    [JsonProperty("signature")] public WeaponSignature? Signature { get; set; }
    // Palette bank index and 15 BGR555 codes for entries 1..15 (entry 0 is transparency; see
    // Offsets.WeaponPaletteBankA/B), from data/weapon_colors.json + data/weapon_palette_overrides.json.
    // Sentinel defaults because 0 is a used palette index (Materia Blade's).
    [JsonProperty("palette")] public int Palette { get; set; } = -1;
    [JsonProperty("colors")] public int[]? Colors { get; set; }
    // The id this design lived at before it moved (items.json `migratedFrom`).
    // TallyMigration carries kills and deeds across on it, so the pairing lives in data and no C# file
    // names a weapon. 0 = never moved.
    [JsonProperty("migratedFrom")] public int MigratedFrom { get; set; }
}

/// <summary>A weapon's tier grant: at kill tier &gt;= AtTier the wielder gains the support
/// passive AbilityId. Support slot only, so it never takes the player's reaction or movement slot.
/// Baked from data/items.json into meta.json.</summary>
public sealed class WeaponSignature
{
    [JsonProperty("abilityId")] public int AbilityId { get; set; }
    [JsonProperty("slot")] public string Slot { get; set; } = "";
    [JsonProperty("atTier")] public int AtTier { get; set; }
    // Ability name painted on the card at AtTier; "" = none.
    [JsonProperty("displayLabel")] public string DisplayLabel { get; set; } = "";
    // Applies only while HP is below this % of max (0 = always); stays armed once tripped.
    [JsonProperty("hpBelow")] public int HpBelow { get; set; }
    // Flat StatBonus while mounted (combat +0x1B4 bit 0x80, found 2026-06-26); Speed only.
    [JsonProperty("stat")] public string Stat { get; set; } = "";
    [JsonProperty("statBonus")] public int StatBonus { get; set; }
    [JsonProperty("mounted")] public bool Mounted { get; set; }
    // Puppeteer (Galewind): the struck enemy is player-controlled for this many of its turns
    // (agency bit, combat +0x05 / 0x08), with a Tuning.PuppeteerCooldownTurns cooldown.
    [JsonProperty("puppeteerTurns")] public int PuppeteerTurns { get; set; }
    // Doom-hasten (Eclipsebolt "Eagle Eye"): enemy Doom countdown forced down to this (band +0x59).
    [JsonProperty("doomCountdownTo")] public int DoomCountdownTo { get; set; }
    // Ricochet (Stormarc "Arc Lightning"): RicochetPct % chip (min 1, never kills) to the nearest
    // other enemy within RicochetRadius Manhattan tiles.
    [JsonProperty("ricochetRadius")] public int RicochetRadius { get; set; }
    [JsonProperty("ricochetPct")] public int RicochetPct { get; set; }
    // Maim (Huntress): struck enemies lose reaction abilities for this many of their turns.
    [JsonProperty("crippleTurns")] public int CrippleTurns { get; set; }
    // Barrage (Yoichi): injects ability 358 into the wielder's JobCommand record.
    [JsonProperty("grantCommandAbilityId")] public int GrantCommandAbilityId { get; set; }
    // Renewal (Mending Staff): each turn edge heals allies within this many Chebyshev tiles for
    // round(maxHP * Tuning.RenewalPct).
    [JsonProperty("regenAuraRadius")] public int RegenAuraRadius { get; set; }
    // Choir (Warlock's Staff): every deployed +3 bearer holds the Non-charge support bit while one
    // is alive with the staff. No ally aura; >0 is just the enable flag.
    [JsonProperty("instantCastRadius")] public int InstantCastRadius { get; set; }
    // Spiritual Font (Wellspring): a turn ending on a new tile restores Tuning.FontHpPct HP and
    // Tuning.FontMpPct MP. Runtime-written because the engine honors one movement passive.
    [JsonProperty("fontOnMove")] public bool FontOnMove { get; set; }
    // Rapture (Rod of Faith): below Tuning.RaptureHpPct HP, Master Teleportation (Tuning.RaptureMoveId)
    // replaces movement for Tuning.RaptureTurns turns.
    [JsonProperty("raptureMove")] public bool RaptureMove { get; set; }
    // Feign Death (Wrathblade): holds Reraise (band +0x47/0x20) through the death that clears it,
    // so the engine auto-revives the wielder at about 10% HP. Once per battle.
    [JsonProperty("feignDeath")] public bool FeignDeath { get; set; }
    // Afterimage (Swiftedge): Speed ramps by Tuning.AfterimageSpeedPerTurn per turn (cap
    // Tuning.AfterimageSpeedCap), resets on damage. Damage is Speed x WP (formula 99).
    [JsonProperty("afterimage")] public bool Afterimage { get; set; }
    // Larceny (Arcanum): a hit steals the target's top holdable buff for Tuning.LarcenyHoldTurns
    // global turns (wielder turns can be gamed by parking). Only Reraise/Invisible are wired
    // (Larceny.Policy.cs).
    [JsonProperty("larcenyTurns")] public int LarcenyTurns { get; set; }
    // Ultima (Materia Blade): PA = round(naturalPA * UltimaMul[tier][hpBand]); owns the PA lane.
    [JsonProperty("ultima")] public bool Ultima { get; set; }
    // Benediction (Sanctus Staff): ally HP restored during the wielder's action window is boosted
    // by this percent. Uses observed HP, so overheal earns nothing.
    [JsonProperty("healBoostPct")] public int HealBoostPct { get; set; }
    // Sanctuary (Staff of the Magi): fallen allies' crystal counter (combat +0x07 / band -0x15) held at SanctuaryHearts.
    [JsonProperty("antiCrystallize")] public bool AntiCrystallize { get; set; }
    // Kobu (Kiyomori): a melee hit raises the wielder's brave to the target's (band +0x0F), once,
    // capped at Tuning.KobuBraveCap. Katana formula 1: PA x Brave/100 x WP.
    [JsonProperty("braveOneUp")] public bool BraveOneUp { get; set; }
    // Iai (Ame-no-Murakumo): wielder CT (band +0x25) held at 100 until their first turn ends.
    [JsonProperty("iai")] public bool Iai { get; set; }
    // Mushin (Kiku-ichimonji): a full round of stillness arms one boosted attack (GrowthEngine.Mushin.cs).
    [JsonProperty("mushin")] public bool Mushin { get; set; }
    // Bulwark (Sunderer, docs/BULWARK_AC.md): a full Wait bars the vacant tile behind the wielder
    // (terrain-grid byte +6 bit 0x02) until their next turn, death, or loss of location.
    [JsonProperty("bulwark")] public bool Bulwark { get; set; }
    // Gun Slinger (Blaster): between battles, a twin Blaster goes in the roster off-hand
    // (ROffHand +0x18) and Dual Wield (Key 477) in the support slot (RSupport +0x0A); restored after.
    [JsonProperty("gunSlinger")] public bool GunSlinger { get; set; }
}

internal static class MetaLoader
{
    /// <summary>Load meta.json from the mod directory. A missing or unparseable file gives an
    /// empty map (growth falls back to PA, display shows nothing) instead of a crash.</summary>
    public static Dictionary<int, WeaponMeta> Load(string modDir)
    {
        try
        {
            var path = Path.Combine(modDir, "meta.json");
            if (!File.Exists(path)) return new Dictionary<int, WeaponMeta>();
            var raw = JsonConvert.DeserializeObject<Dictionary<string, WeaponMeta>>(File.ReadAllText(path))
                      ?? new Dictionary<string, WeaponMeta>();
            var map = new Dictionary<int, WeaponMeta>(raw.Count);
            foreach (var kv in raw)
                if (int.TryParse(kv.Key, out int id)) map[id] = kv.Value;
            return map;
        }
        catch
        {
            return new Dictionary<int, WeaponMeta>();
        }
    }
}
