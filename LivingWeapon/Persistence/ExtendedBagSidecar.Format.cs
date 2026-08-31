using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// LW-351 stage 3 V8-4: the FILE-FORMAT half of <see cref="ExtendedBagSidecar"/>, split out of the
/// base file under the 200-line house guideline -- schema parsing, the schema-1-to-2 migration,
/// and the on-disk <see cref="Dto"/> shape it deserialises into. The base file keeps the fields,
/// construction, and the read/write API the rest of the mod calls.
/// </summary>
internal sealed partial class ExtendedBagSidecar
{
    public static ExtendedBagSidecar Load(string path, int maxSaves = MaxSaves)
    {
        var empty = new Dictionary<string, Dictionary<int, int>>();
        try
        {
            if (!File.Exists(path)) return new ExtendedBagSidecar(path, empty, new List<string>(), null, "none (first run)", maxSaves);
            var dto = JsonConvert.DeserializeObject<Dto>(File.ReadAllText(path));
            if (dto == null) return Unrecognised(path, empty, maxSaves);
            if (dto.Version == 1)
            {
                var legacy = ParseCounts(dto.Counts);
                return new ExtendedBagSidecar(path, empty, new List<string>(), legacy.Count > 0 ? legacy : null, path + " (schema 1, migrated)", maxSaves);
            }
            if (dto.Version != SchemaVersion) return Unrecognised(path, empty, maxSaves);
            var saves = new Dictionary<string, Dictionary<int, int>>();
            var order = new List<string>();
            foreach (var key in dto.Order ?? new List<string>())
                if (dto.Saves != null && dto.Saves.TryGetValue(key, out var raw) && !saves.ContainsKey(key))
                {
                    saves[key] = ParseCounts(raw);
                    order.Add(key);
                }
            return new ExtendedBagSidecar(path, saves, order, dto.Legacy != null ? ParseCounts(dto.Legacy) : null, path, maxSaves);
        }
        catch (Exception ex)
        {
            ModLogger.Warn(LogVerb.Save, "The extended-inventory sidecar could not be read; starting with no saved counts: " + ex.Message);
            return new ExtendedBagSidecar(path, empty, new List<string>(), null, "unreadable", maxSaves);
        }
    }

    private static ExtendedBagSidecar Unrecognised(string path, Dictionary<string, Dictionary<int, int>> empty, int maxSaves)
    {
        ModLogger.Warn(LogVerb.Save, "The extended-inventory sidecar has an unrecognised schema; starting with no saved counts.");
        return new ExtendedBagSidecar(path, empty, new List<string>(), null, "unrecognised schema", maxSaves);
    }

    private static Dictionary<int, int> ParseCounts(Dictionary<string, int>? raw)
    {
        var counts = new Dictionary<int, int>();
        if (raw == null) return counts;
        foreach (var kv in raw)
            if (int.TryParse(kv.Key, out int id) && kv.Value >= 0 && kv.Value <= 255) counts[id] = kv.Value;
        return counts;
    }

    private sealed class Dto
    {
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("counts", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, int>? Counts { get; set; }   // schema 1
        [JsonProperty("order", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Order { get; set; }
        [JsonProperty("saves", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, Dictionary<string, int>> Saves { get; set; } = new();
        [JsonProperty("legacy", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, int>? Legacy { get; set; }
    }
}
