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
