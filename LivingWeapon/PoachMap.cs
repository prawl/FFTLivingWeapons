using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>One species' Poach carcass drop pair: the common and rare inventory item keys +
/// display names poach.json ships for that species (victim job byte - 95).</summary>
internal readonly record struct PoachCarcass(int CommonKey, string CommonName, int RareKey, string RareName);

/// <summary>
/// Loads LivingWeapon/poach.json (LW-167; tools/extract_poach_map.py's committed output --
/// species = victim job byte - 95 -&gt; {common, rare} carcass {key, name}) from the mod
/// directory, mirroring MetaLoader/GunSlingerStore's modDir-rooted load pattern.
///
/// DISARM ON FAILURE, NOT CRASH-OR-LIMP: a missing or corrupt file leaves <see cref="IsLoaded"/>
/// false and every <see cref="TryGetSpecies"/> lookup false -- LivingPoach checks this before ever
/// consulting the map, so a broken deploy quietly refuses to poach against garbage data rather
/// than crash or fabricate a drop. Exactly ONE Warning is logged (the map loads once at
/// construction and never reloads mid-session, so there is no risk of this spamming the tick loop).
/// </summary>
internal sealed class PoachMap
{
    private readonly Dictionary<int, PoachCarcass> _species = new();

    /// <summary>True when poach.json parsed cleanly and carried at least one usable species entry.</summary>
    public bool IsLoaded { get; }

    public PoachMap(string modDir)
    {
        try
        {
            string path = Path.Combine(modDir, "poach.json");
            var doc = JsonConvert.DeserializeObject<PoachDoc>(File.ReadAllText(path));
            if (doc?.Species == null) throw new InvalidDataException("poach.json has no \"species\" table");

            foreach (var kv in doc.Species)
            {
                if (!int.TryParse(kv.Key, out int idx)) continue;
                var e = kv.Value;
                if (e?.Common == null || e.Rare == null) continue;
                _species[idx] = new PoachCarcass(e.Common.Key, e.Common.Name ?? "", e.Rare.Key, e.Rare.Name ?? "");
            }
            if (_species.Count == 0) throw new InvalidDataException("poach.json parsed but every species entry was malformed");

            IsLoaded = true;
        }
        catch (Exception)
        {
            IsLoaded = false;
            _species.Clear();
            ModLogger.Warn(LogVerb.Signature, "Living Poach is disarmed: the poach map file is missing or unreadable.");
        }
    }

    /// <summary>Look up one species' carcass pair by index (victim job byte - 95). False (and a
    /// default record) when the map never loaded, or this species has no entry.</summary>
    public bool TryGetSpecies(int speciesIndex, out PoachCarcass carcass) => _species.TryGetValue(speciesIndex, out carcass);

    private sealed class PoachDoc
    {
        [JsonProperty("species")] public Dictionary<string, PoachSpeciesDto>? Species { get; set; }
    }

    private sealed class PoachSpeciesDto
    {
        [JsonProperty("common")] public PoachEntryDto? Common { get; set; }
        [JsonProperty("rare")] public PoachEntryDto? Rare { get; set; }
    }

    private sealed class PoachEntryDto
    {
        [JsonProperty("key")] public int Key { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
    }
}
