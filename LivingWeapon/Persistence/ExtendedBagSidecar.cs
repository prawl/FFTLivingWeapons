using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// Remembers the bag counts of the extended item ids (261+) in extended_inventory.json.
/// The save only serialises 261 count bytes (docs/LIVE_LEDGER.md [capbreak-save-roundtrip-1-5-2]).
/// Lives in SaveLocation.SaveDir beside kills.json.
///
/// Schema 2 (2026-08-27) keys counts per save, using the key from the save header at
/// serialise time (SaveEdgeTracker.KeyFromHeader). Schema 1's single global file failed the owner's
/// test 2 (2026-08-27 18:53), where a load's own clear was recorded as a sale. A schema 1 file
/// migrates into <see cref="TakeLegacy"/>, handed to the first load with an unknown key.
///
/// Capped at <see cref="MaxSaves"/> keys, evicting the least recently used. The old cap of 64
/// evicted oldest-written, and an evening of battle autosaves pushed out the owner's real save
/// (pt2775; 2026-08-30). A load that finds its key moves it to the back.
///
/// Two threads. Engine's tick records saves and the game's thread reads from the load
/// detour. <c>_gate</c> guards the maps, <c>_ioGate</c> serialises the write and never nests I/O.
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
            // Handing out the stored map is safe because RecordSave replaces entries, never mutates.
            if (_saves.TryGetValue(key, out var c))
            {
                // Move the used key to the back (memory only, game thread; RecordSave persists it).
                _order.Remove(key);
                _order.Add(key);
                counts = c;
                return true;
            }
        }
        counts = new Dictionary<int, int>();
        return false;
    }

    /// <summary>The schema-1 counts, handed out once to the first unknown-key load; later unknown
    /// keys get the data seed.</summary>
    public Dictionary<int, int>? TakeLegacy()
    {
        lock (_gate)
        {
            var l = _legacy;
            _legacy = null;
            return l;
        }
    }

    /// <summary>Records the counts the game just serialised under <paramref name="key"/> and
    /// persists atomically. A failed write logs and leaves the previous file intact.</summary>
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

    /// <summary>Persists after the legacy counts were taken, so they are not handed out again
    /// next launch.</summary>
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
