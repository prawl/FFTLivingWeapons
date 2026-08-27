using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// LW-348: extended_inventory.json, the bag counts of the extended-inventory ids (261+), kept by
/// the mod because the save file cannot hold them: the save serialises exactly 261 count bytes
/// packed against the next array (docs/LIVE_LEDGER.md [capbreak-save-roundtrip-1-5-2], owner
/// cold-boot round trip 2026-08-27). Lives in SaveLocation.SaveDir beside kills.json (survives a
/// mod-folder-replace update). The boot arm replays it into the bag array before the game runs
/// (a load leaves ids 261+ alone, so the replayed values survive every load that session); the
/// tick loop saves it whenever a count changes (buy, sell, pick up, break).
///
/// Ruling still owed by the owner (docs/TODO.md LW-348): an id with NO entry here gets its
/// data-declared SeedCount (the Moonblade: 1) so a first-time install sees the item at all; an
/// explicit 0 stays 0. Load never throws: missing, corrupt or unrecognised means "no entries".
/// </summary>
internal sealed class ExtendedBagSidecar
{
    internal const int SchemaVersion = 1;
    public const string FileName = "extended_inventory.json";

    private readonly string _path;
    private readonly Dictionary<int, int> _counts;

    public IReadOnlyDictionary<int, int> Counts => _counts;
    public string LoadedFrom { get; }

    private ExtendedBagSidecar(string path, Dictionary<int, int> counts, string loadedFrom)
    {
        _path = path;
        _counts = counts;
        LoadedFrom = loadedFrom;
    }

    public static ExtendedBagSidecar Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ExtendedBagSidecar(path, new Dictionary<int, int>(), "none (first run)");
            var dto = JsonConvert.DeserializeObject<Dto>(File.ReadAllText(path));
            if (dto == null || dto.Version != SchemaVersion)
            {
                ModLogger.Warn(LogVerb.Save, "The extended-inventory sidecar has an unrecognised schema; starting with no saved counts.");
                return new ExtendedBagSidecar(path, new Dictionary<int, int>(), "unrecognised schema");
            }
            var counts = new Dictionary<int, int>();
            foreach (var kv in dto.Counts)
                if (int.TryParse(kv.Key, out int id) && kv.Value >= 0 && kv.Value <= 255) counts[id] = kv.Value;
            return new ExtendedBagSidecar(path, counts, path);
        }
        catch (Exception ex)
        {
            ModLogger.Warn(LogVerb.Save, "The extended-inventory sidecar could not be read; starting with no saved counts: " + ex.Message);
            return new ExtendedBagSidecar(path, new Dictionary<int, int>(), "unreadable");
        }
    }

    /// <summary>The count to put in the bag at boot for <paramref name="id"/>: the saved one when
    /// there is an entry, else the data's first-copy seed.</summary>
    public int ResolveBootCount(int id, int seedCount) => _counts.TryGetValue(id, out int c) ? c : seedCount;

    /// <summary>Record <paramref name="counts"/> (the live bag bytes) and persist atomically.
    /// Fail-soft: a failed save logs and leaves the previous file intact (this runs on Engine's
    /// tick thread, which must never raise).</summary>
    public void Update(IReadOnlyDictionary<int, int> counts)
    {
        foreach (var kv in counts) _counts[kv.Key] = kv.Value;
        try
        {
            var dto = new Dto { Version = SchemaVersion };
            foreach (var kv in _counts) dto.Counts[kv.Key.ToString()] = kv.Value;
            SidecarJson.SaveAtomic(_path, JsonConvert.SerializeObject(dto));
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Save, "Failed to save the extended-inventory bag counts to disk: " + ex.Message);
        }
    }

    private sealed class Dto
    {
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("counts")] public Dictionary<string, int> Counts { get; set; } = new();
    }
}
