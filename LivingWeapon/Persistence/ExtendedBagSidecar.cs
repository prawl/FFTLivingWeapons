using System;
using System.Collections.Generic;
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
/// Bounded: <see cref="MaxSaves"/> keys are kept and the least recently USED one goes first
/// (LW-351 fix round 7, 2026-08-30: the old cap of 64 with oldest-written eviction let one
/// evening of battle autosaves, two to four keys per fight, push the owner's real save out, so
/// his pt2775 load came back "never seen" and its counts were replaced by the seed). A load that
/// finds its key moves that key to the back of the line; entries are a few bytes each, so a
/// thousand of them is still a small file. Load never throws; Update is fail-soft.
///
/// TWO THREADS since LW-351: Engine's tick records a save here, and the game's OWN thread reads
/// here from inside the load detour (the replay has to beat the game's menu-template rebuild).
/// The maps are plain Dictionary/List and SidecarJson writes through a fixed .tmp path, so both
/// are locked: <c>_gate</c> guards the in-memory state and <c>_ioGate</c> serialises the disk
/// write, and _gate is never held across the I/O so a load edge cannot wait on a disk write.
/// </summary>
internal sealed partial class ExtendedBagSidecar
{
    internal const int SchemaVersion = 2;
    internal const int MaxSaves = 1024;
    public const string FileName = "extended_inventory.json";

    private readonly object _gate = new();     // the in-memory maps below
    private readonly object _ioGate = new();   // one writer at a time on the file itself
    private readonly string _path;
    private readonly int _maxSaves;
    private readonly Dictionary<string, Dictionary<int, int>> _saves;
    private readonly List<string> _order;   // least recently used first
    private Dictionary<int, int>? _legacy;

    public string LoadedFrom { get; }
    public int SaveCount { get { lock (_gate) return _saves.Count; } }

    private ExtendedBagSidecar(string path, Dictionary<string, Dictionary<int, int>> saves, List<string> order,
        Dictionary<int, int>? legacy, string loadedFrom, int maxSaves = MaxSaves)
    {
        _path = path;
        _maxSaves = Math.Max(1, maxSaves);
        _saves = saves;
        _order = order;
        _legacy = legacy;
        LoadedFrom = loadedFrom;
    }

    /// <summary>The counts recorded for <paramref name="key"/>, if that save was ever serialised
    /// while this mod ran.</summary>
    public bool TryGetSave(string key, out IReadOnlyDictionary<int, int> counts)
    {
        lock (_gate)
        {
            // Safe to hand the stored map out by reference: RecordSave replaces an entry with a
            // fresh dictionary, it never mutates one that was already handed out.
            if (_saves.TryGetValue(key, out var c))
            {
                // A used key goes to the back of the line, so a save the player keeps loading is
                // never the one the cap throws away. In memory only: the next RecordSave persists
                // the order, and a read on the game's own thread must not touch the disk.
                _order.Remove(key);
                _order.Add(key);
                counts = c;
                return true;
            }
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
            while (_order.Count > _maxSaves)
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
}
