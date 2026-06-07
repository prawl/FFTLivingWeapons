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
