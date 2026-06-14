using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>Per-weapon facts the growth + display need, baked from data/items.json
/// at build time into meta.json (id -> WeaponMeta). Keeps the runtime free of any
/// dependency on the dev repo.</summary>
public sealed class WeaponMeta
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("wp")] public int Wp { get; set; }
    [JsonProperty("cat")] public string Cat { get; set; } = "";
    [JsonProperty("formula")] public int Formula { get; set; }
    // The weapon's flavor line -- the stable lead of its description. The in-card Kills
    // counter is anchored to this (the nearest flavor before a "Kills " is that weapon's).
    [JsonProperty("flavor")] public string Flavor { get; set; } = "";
    // The iconic passive this weapon grants its wielder at a kill-tier (null for most
    // weapons -- they are pure stat growth). Only support passives are wired; see Signatures.
    [JsonProperty("signature")] public WeaponSignature? Signature { get; set; }
}

/// <summary>A weapon's curated tier-grant: at kill-tier &gt;= AtTier the wielder gains the
/// support passive AbilityId. Additive-only (Slot == "support") -- never reaction/movement,
/// which would hijack the player's single slot. Baked from data/items.json into meta.json.</summary>
public sealed class WeaponSignature
{
    [JsonProperty("abilityId")] public int AbilityId { get; set; }
    [JsonProperty("slot")] public string Slot { get; set; } = "";
    [JsonProperty("atTier")] public int AtTier { get; set; }
    // Short ability name painted onto the card at AtTier (e.g. "Concentration"); "" = no card display.
    [JsonProperty("displayLabel")] public string DisplayLabel { get; set; } = "";
    // Conditional HP-gate: grant only applies while current HP is below this % of max (0 = always-on,
    // e.g. Mortal Coil's Attack Boost while HP < 50%). Arm-and-stays once tripped (never strips a support).
    [JsonProperty("hpBelow")] public int HpBelow { get; set; }
    // TIMED stat grant (no support id): a flat StatBonus to Stat for the wielder's first ForTurns turns,
    // then reverted -- e.g. (former) Galewind Speed +3 for 3 turns. ForTurns == 0 means not a timed grant.
    [JsonProperty("stat")] public string Stat { get; set; } = "";
    [JsonProperty("statBonus")] public int StatBonus { get; set; }
    [JsonProperty("forTurns")] public int ForTurns { get; set; }
    // CHARM-LOCK aura: while a unit wields this at AtTier, any Charm the party lands is held unbreakable
    // for this many of the target's turns, then force-cleared (Galewind). 0 = not a charm-lock weapon.
    [JsonProperty("charmLockTurns")] public int CharmLockTurns { get; set; }
    // DOOM-HASTEN aura (Eclipsebolt "Eagle Eye"): while a unit wields this at AtTier, any Doom on an enemy
    // has its countdown forced down to this value (proven: write band +0x59). 0 = not a doom-hasten weapon.
    [JsonProperty("doomCountdownTo")] public int DoomCountdownTo { get; set; }
    // RICOCHET aura (Stormarc "Arc Lightning"): while a unit wields this at AtTier, each damage event the
    // wielder deals to an enemy bounces chip damage (RicochetPct % of original, floor 1) to the nearest
    // OTHER enemy within RicochetRadius Manhattan tiles. Chip never kills (HP floor 1). 0 = not a ricochet weapon.
    [JsonProperty("ricochetRadius")] public int RicochetRadius { get; set; }
    [JsonProperty("ricochetPct")] public int RicochetPct { get; set; }
    // MAIM (Huntress "Maim"): struck enemies lose their reaction abilities for this many of their turns,
    // then the saved bits restore. 0 = not a maim weapon.
    [JsonProperty("crippleTurns")] public int CrippleTurns { get; set; }
    // BARRAGE (Yoichi "Barrage"): while a unit wields this at AtTier, the wielder gains Barrage (ability 358)
    // injected into their current job's JobCommand record. 0 = not a barrage weapon.
    [JsonProperty("grantCommandAbilityId")] public int GrantCommandAbilityId { get; set; }
    // LIFE SAP (Umbral Rod "Life Sap"): a kill credited to this weapon at AtTier heals the wielder by
    // Tuning.LifeSapPct of max HP (clamped; never revives). false = not a life-sap weapon.
    [JsonProperty("lifeSapOnKill")] public bool LifeSapOnKill { get; set; }
    // WYRMBLOOD (Dragon Rod "Wyrmblood"): at each wielder turn edge, the wielder + allies within this
    // many Manhattan tiles regen their OWN maxHP/Tuning.WyrmbloodDiv (emulated; the Regen status bit is
    // unmapped and never touched). 0 = not a regen-splash weapon.
    [JsonProperty("regenSplashRadius")] public int RegenSplashRadius { get; set; }
    // SPIRITUAL FONT (Wellspring "Spiritual Font"): at AtTier, a completed wielder turn whose grid
    // position changed since their previous turn restores Tuning.FontHpPct of max HP and FontMpPct of
    // max MP -- written by the RUNTIME, not via engine movement passives (the engine honors only one).
    [JsonProperty("fontOnMove")] public bool FontOnMove { get; set; }
    // RAPTURE (Rod of Faith "Rapture"): below Tuning.RaptureHpPct of max HP, Master Teleportation
    // (Tuning.RaptureMoveId) replaces the wielder's movement for Tuning.RaptureTurns turns, then the
    // saved movement restores. false = not a rapture weapon.
    [JsonProperty("raptureMove")] public bool RaptureMove { get; set; }
    // FEIGN DEATH (Wrathblade "Feign Death"): at AtTier the wielder is held with Reraise (band
    // +0x47/0x20) -- re-applied through the death that clears it -- so a lethal hit plays out as a
    // real death (an AI-ignored corpse) that the engine auto-revives, animated, at ~10% HP. ONCE
    // per battle (the bit is dropped the instant the revive is seen). false = not a feign-death weapon.
    [JsonProperty("feignDeath")] public bool FeignDeath { get; set; }
    // AFTERIMAGE (Swiftedge "Afterimage"): at AtTier the wielder's Speed ramps by
    // Tuning.AfterimageSpeedPerTurn for each completed turn (capped at Tuning.AfterimageSpeedCap
    // turns' worth) and resets to 0 when the wielder takes damage. Swiftedge's damage is Speed x WP
    // (formula 99), so the ramp accelerates its damage. false = not an afterimage weapon.
    [JsonProperty("afterimage")] public bool Afterimage { get; set; }
}

internal static class MetaLoader
{
    /// <summary>Load meta.json from the mod directory. Missing/parse failure ->
    /// empty map (growth falls back to PA, display shows nothing) rather than a crash.</summary>
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
