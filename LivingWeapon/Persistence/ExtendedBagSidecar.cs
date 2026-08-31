using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// LW-348 / LW-353: extended_inventory.json, the bag counts of the extended-inventory ids
/// (261+), kept by the mod because the save file cannot hold them (the save serialises exactly
/// 261 count bytes; docs/LIVE_LEDGER.md [capbreak-save-roundtrip-1-5-2]). Lives in
/// SaveLocation.SaveDir beside kills.json.
///
/// SCHEMA 2 (LW-353, 2026-08-27): counts are keyed PER SAVE. The key is derived from the save
/// struct's own header at the moment the game serialises it (SaveEdgeTracker.KeyFromHeader),
/// the counts are those of that instant, and the same key is read back when the game applies
/// that save, so loading slot B never sees slot A's items and loading an older save of the same
/// slot gets that save's own counts. The owner's test 2 (2026-08-27 18:53) is what schema 1's
/// one global file, recorded continuously, failed: a load's own clear was written to disk as a
/// sale. Schema 1 files migrate their single count map into <see cref="TakeLegacy"/>, a one-shot
/// fallback for the first load whose key is unknown (the saves that predate the keys).
///
/// Bounded: the newest <see cref="MaxSaves"/> keys are kept (an autosave lands every battle and
/// shop visit; the file must not grow forever). Load never throws; Update is fail-soft.
///
/// TWO THREADS since LW-351: Engine's tick records a save here, and the game's OWN thread reads
/// here from inside the load detour (the replay has to beat the game's menu-template rebuild).
/// The maps are plain Dictionary/List and SidecarJson writes through a fixed .tmp path, so both
/// are locked: <c>_gate</c> guards the in-memory state and <c>_ioGate</c> serialises the disk
/// write, and _gate is never held across the I/O so a load edge cannot wait on a disk write.
/// </summary>
internal sealed class ExtendedBagSidecar
{
    internal const int SchemaVersion = 2;
    internal const int MaxSaves = 64;
    public const string FileName = "extended_inventory.json";

    private readonly object _gate = new();     // the in-memory maps below
    private readonly object _ioGate = new();   // one writer at a time on the file itself
    private readonly string _path;
    private readonly Dictionary<string, Dictionary<int, int>> _saves;
    private readonly List<string> _order;   // oldest first
    private Dictionary<int, int>? _legacy;

    public string LoadedFrom { get; }
    public int SaveCount { get { lock (_gate) return _saves.Count; } }

    private ExtendedBagSidecar(string path, Dictionary<string, Dictionary<int, int>> saves, List<string> order,
        Dictionary<int, int>? legacy, string loadedFrom)
    {
        _path = path;
        _saves = saves;
        _order = order;
        _legacy = legacy;
        LoadedFrom = loadedFrom;
    }

    public static ExtendedBagSidecar Load(string path)
    {
        var empty = new Dictionary<string, Dictionary<int, int>>();
        try
        {
            if (!File.Exists(path)) return new ExtendedBagSidecar(path, empty, new List<string>(), null, "none (first run)");
            var dto = JsonConvert.DeserializeObject<Dto>(File.ReadAllText(path));
            if (dto == null) return Unrecognised(path, empty);
            if (dto.Version == 1)
            {
                var legacy = ParseCounts(dto.Counts);
                return new ExtendedBagSidecar(path, empty, new List<string>(), legacy.Count > 0 ? legacy : null, path + " (schema 1, migrated)");
            }
            if (dto.Version != SchemaVersion) return Unrecognised(path, empty);
            var saves = new Dictionary<string, Dictionary<int, int>>();
            var order = new List<string>();
            foreach (var key in dto.Order ?? new List<string>())
                if (dto.Saves != null && dto.Saves.TryGetValue(key, out var raw) && !saves.ContainsKey(key))
                {
                    saves[key] = ParseCounts(raw);
                    order.Add(key);
                }
            return new ExtendedBagSidecar(path, saves, order, dto.Legacy != null ? ParseCounts(dto.Legacy) : null, path);
        }
        catch (Exception ex)
        {
            ModLogger.Warn(LogVerb.Save, "The extended-inventory sidecar could not be read; starting with no saved counts: " + ex.Message);
            return new ExtendedBagSidecar(path, empty, new List<string>(), null, "unreadable");
        }
    }

    private static ExtendedBagSidecar Unrecognised(string path, Dictionary<string, Dictionary<int, int>> empty)
    {
        ModLogger.Warn(LogVerb.Save, "The extended-inventory sidecar has an unrecognised schema; starting with no saved counts.");
        return new ExtendedBagSidecar(path, empty, new List<string>(), null, "unrecognised schema");
    }

    private static Dictionary<int, int> ParseCounts(Dictionary<string, int>? raw)
    {
        var counts = new Dictionary<int, int>();
        if (raw == null) return counts;
        foreach (var kv in raw)
            if (int.TryParse(kv.Key, out int id) && kv.Value >= 0 && kv.Value <= 255) counts[id] = kv.Value;
        return counts;
    }

    /// <summary>The counts recorded for <paramref name="key"/>, if that save was ever serialised
    /// while this mod ran.</summary>
    public bool TryGetSave(string key, out IReadOnlyDictionary<int, int> counts)
    {
        lock (_gate)
        {
            // Safe to hand the stored map out by reference: RecordSave replaces an entry with a
            // fresh dictionary, it never mutates one that was already handed out.
            if (_saves.TryGetValue(key, out var c)) { counts = c; return true; }
        }
        counts = new Dictionary<int, int>();
        return false;
    }

    /// <summary>The schema-1 counts, handed out ONCE (the first unknown-key load after the
    /// migration inherits them; every later unknown key gets the data seed).</summary>
    public Dictionary<int, int>? TakeLegacy()
    {
        lock (_gate)
        {
            var l = _legacy;
            _legacy = null;
            return l;
        }
    }

    /// <summary>Record the counts the game just serialised under <paramref name="key"/> and
    /// persist atomically (SidecarJson chain). Fail-soft: a failed write logs and leaves the
    /// previous file intact.</summary>
    public void RecordSave(string key, IReadOnlyDictionary<int, int> counts)
    {
        lock (_gate)
        {
            _saves[key] = new Dictionary<int, int>(counts);
            _order.Remove(key);
            _order.Add(key);
            while (_order.Count > MaxSaves)
            {
                _saves.Remove(_order[0]);
                _order.RemoveAt(0);
            }
        }
        Persist();
    }

    /// <summary>Persist after the legacy counts were consumed, so they are not handed out twice
    /// across launches.</summary>
    public void PersistAfterLegacyTaken() => Persist();

    private void Persist()
    {
        try
        {
            string json;
            lock (_gate)
            {
                var dto = new Dto { Version = SchemaVersion, Order = new List<string>(_order) };
                foreach (var key in _order)
                {
                    var m = new Dictionary<string, int>();
                    foreach (var kv in _saves[key]) m[kv.Key.ToString()] = kv.Value;
                    dto.Saves[key] = m;
                }
                if (_legacy != null)
                {
                    dto.Legacy = new Dictionary<string, int>();
                    foreach (var kv in _legacy) dto.Legacy[kv.Key.ToString()] = kv.Value;
                }
                json = JsonConvert.SerializeObject(dto);
            }
            lock (_ioGate) SidecarJson.SaveAtomic(_path, json);
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Save, "Failed to save the extended-inventory bag counts to disk: " + ex.Message);
        }
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
